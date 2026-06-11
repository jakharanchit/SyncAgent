using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SyncAgent.Config;
using SyncAgent.Data;
using SyncAgent.Data.Models;

namespace SyncAgent.Sync;

public sealed class SyncOrchestrator : ISyncOrchestrator
{
    private readonly SQLiteReader                _reader;
    private readonly PostgresWriter              _writer;
    private readonly RetryPolicy                 _retry;
    private readonly SyncConfig                  _config;
    private readonly ILogger<SyncOrchestrator>   _logger;

    // Shared HttpClient — thread-safe, intended to be reused.
    private static readonly HttpClient _http = new();

    // Track infra state across cycles for the health file
    private DateTime? _lastInfraErrorAt;

    // Only probe Postgres when last write attempt had failures (avoids extra round-trips on clean cycles)
    private bool _lastCycleHadFailures;

    public SyncOrchestrator(
        SQLiteReader reader,
        PostgresWriter writer,
        RetryPolicy retry,
        SyncConfig config,
        ILogger<SyncOrchestrator> logger)
    {
        _reader = reader;
        _writer = writer;
        _retry  = retry;
        _config = config;
        _logger = logger;
    }

    // ── Startup ───────────────────────────────────────────────────────────────

    public async Task VerifyStartupAsync(CancellationToken ct)
    {
        // SQLite schema check
        var missingColumns = await _reader.VerifySchemaAsync(ct);
        if (missingColumns.Length > 0)
        {
            var missing = string.Join(", ", missingColumns);
            var message = missingColumns.Length == 7
                ? "sync_status table not found. Run sql/sqlite-syncagent.sql to initialise the database."
                : $"sync_status table is missing required columns: {missing}. Re-run sql/sqlite-syncagent.sql.";
            _logger.LogError("{Message}", message);
            throw new InvalidOperationException(message);
        }

        _logger.LogInformation("SQLite schema OK.");
        await _reader.EnsureWalModeAsync(ct);

        if (_config.Tables.Count == 0)
            _logger.LogWarning(
                "No table mappings configured. Add entries to the Tables array in syncagent.json.");
        else
            _logger.LogInformation(
                "Table mappings loaded: {Tables}",
                string.Join(", ", _config.Tables.Select(t => $"{t.SourceTable}→{t.TargetTable}")));

        bool anyInjectStation = _config.Tables.Any(t => t.InjectStationId);
        if (anyInjectStation && string.IsNullOrWhiteSpace(_config.StationId))
            _logger.LogWarning(
                "Station.StationId is not set but InjectStationId=true on one or more table mappings.");

        // Postgres connectivity + schema validation (#11)
        var postgresOk = await _writer.TestConnectionAsync(ct);
        if (!postgresOk)
        {
            _logger.LogWarning(
                "PostgreSQL unreachable at startup. Sync will begin once connectivity is restored.");
        }
        else
        {
            _logger.LogInformation("PostgreSQL connection verified.");

            if (_config.Tables.Count > 0)
            {
                var schemaWarnings = await _writer.ValidateSchemaAsync(_config.Tables, ct);
                foreach (var w in schemaWarnings)
                    _logger.LogWarning("Schema validation: {Warning}", w);

                if (schemaWarnings.Count == 0)
                    _logger.LogInformation("PostgreSQL schema validation passed.");
            }
        }

        var deadLetterCount = await _reader.GetDeadLetterCountAsync(ct);
        if (deadLetterCount > 0)
            _logger.LogWarning("Dead letter records requiring manual review: {Count}", deadLetterCount);

        if (_config.IsDryRun)
            _logger.LogWarning("DRY RUN mode — no records will be written or updated.");

        _logger.LogInformation(
            "SyncAgent startup complete. StationId={StationId} SiteName={SiteName}",
            _config.StationId, _config.SiteName);
    }

    // ── Sync cycle ────────────────────────────────────────────────────────────

    public async Task<CycleResult> RunCycleAsync(CancellationToken ct)
    {
        // Optional: prune old synced rows
        if (_config.PruneAfterDays > 0)
            await _reader.PruneOldSyncedAsync(_config.PruneAfterDays, ct);

        // 1. Read pending records
        var pending    = await _reader.GetPendingAsync(_config.BatchSize, ct);
        var tableStats = await _reader.GetPendingStatsByTableAsync(ct);

        int totalSynced  = 0;
        int totalDeleted = 0;

        if (pending.Count == 0 && !_config.Tables.Any(t => t.SyncDeletes))
        {
            var dlCount = await _reader.GetDeadLetterCountAsync(ct);
            bool reachable = !_lastCycleHadFailures || await _writer.TestConnectionAsync(ct);
            _lastCycleHadFailures = false;

            return new CycleResult
            {
                PostgresReachable = reachable,
                DeadLetterCount   = dlCount,
                TableStats        = tableStats,
                LastInfraErrorAt  = _lastInfraErrorAt
            };
        }

        bool postgresReachable = true;

        // 2. Hydrate + write inserts/updates
        if (pending.Count > 0)
        {
            var records = await _reader.HydrateRecordsAsync(pending, _config.Tables, ct);
            if (records.Count == 0)
            {
                _logger.LogWarning(
                    "Got {Pending} pending rows but hydration returned 0 records. " +
                    "Check that all pending table_name values exist in the Tables config.",
                    pending.Count);
            }
            else if (_config.IsDryRun)
            {
                _logger.LogInformation(
                    "[DRY RUN] Would sync {Count} records: {Tables}",
                    records.Count,
                    string.Join(", ", records.GroupBy(r => r.SourceTable)
                        .Select(g => $"{g.Count()} × {g.Key}")));
            }
            else
            {
                var result = await _writer.WriteBatchAsync(records, ct);

                var infraFailures = result.Failures.Where(f => f.Kind == FailureKind.Infrastructure).ToList();
                var dataFailures  = result.Failures.Where(f => f.Kind == FailureKind.Data).ToList();

                postgresReachable = infraFailures.Count == 0;
                if (infraFailures.Count > 0)
                {
                    _lastInfraErrorAt    = DateTime.UtcNow;
                    _lastCycleHadFailures = true;
                }
                else
                {
                    _lastCycleHadFailures = dataFailures.Count > 0;
                }

                if (result.Succeeded.Count > 0)
                    await _reader.MarkSyncedAsync(result.Succeeded, ct);

                if (result.Failures.Count > 0)
                    await HandleFailuresAsync(result.Failures, pending, ct);

                totalSynced = result.Succeeded.Count;
            }
        }

        // 3. Delete propagation (#10)
        if (postgresReachable)
            totalDeleted = await RunDeletesAsync(ct);

        var deadLetterCount = await _reader.GetDeadLetterCountAsync(ct);
        tableStats = await _reader.GetPendingStatsByTableAsync(ct);

        return new CycleResult
        {
            Synced            = totalSynced,
            Deleted           = totalDeleted,
            StillPending      = pending.Count - totalSynced,
            Failed            = _lastCycleHadFailures ? pending.Count - totalSynced : 0,
            Deferred          = _lastInfraErrorAt.HasValue && !postgresReachable ? pending.Count - totalSynced : 0,
            DeadLetterCount   = deadLetterCount,
            PostgresReachable = postgresReachable,
            LastSyncedAt      = totalSynced > 0 ? DateTime.UtcNow : null,
            LastInfraErrorAt  = _lastInfraErrorAt,
            TableStats        = tableStats
        };
    }

    // ── Delete propagation ─────────────────────────────────────────────────────

    private async Task<int> RunDeletesAsync(CancellationToken ct)
    {
        int deleted = 0;

        foreach (var table in _config.Tables.Where(t => t.SyncDeletes))
        {
            List<PendingDelete> pendingDeletes;
            try
            {
                pendingDeletes = await _reader.GetPendingDeletesAsync(table, _config.BatchSize, ct);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning("{Message}", ex.Message);
                continue;
            }

            if (pendingDeletes.Count == 0) continue;

            if (_config.IsDryRun)
            {
                _logger.LogInformation(
                    "[DRY RUN] Would delete {Count} records from {Table}",
                    pendingDeletes.Count, table.TargetTable);
                continue;
            }

            var succeeded = await _writer.DeleteBatchAsync(pendingDeletes, ct);
            if (succeeded.Count > 0)
            {
                await _reader.MarkDeletesSyncedAsync(table.GetEffectiveDeleteLogTable(), succeeded, ct);
                deleted += succeeded.Count;
                _logger.LogDebug(
                    "Delete propagation: {Count} rows removed from {Table}",
                    succeeded.Count, table.TargetTable);
            }
        }

        return deleted;
    }

    // ── Failure handling ───────────────────────────────────────────────────────

    private async Task HandleFailuresAsync(
        List<FailedRecord> failures,
        List<PendingRecord> pending,
        CancellationToken ct)
    {
        var retryCounts = pending.ToDictionary(p => p.RecordId, p => p.RetryCount);

        foreach (var failure in failures)
        {
            if (failure.Kind == FailureKind.Infrastructure)
            {
                // Transport broken — do NOT touch retry_count or next_attempt.
                await _reader.UpdateFailureReasonAsync(
                    failure.RecordId, failure.TableName, failure.Exception.Message, ct);

                _logger.LogDebug(
                    "Infra failure deferred (retry_count unchanged): {RecordId} table={Table}",
                    failure.RecordId, failure.TableName);
                continue;
            }

            // Data failure — apply backoff and potentially dead-letter.
            var currentCount = retryCounts.GetValueOrDefault(failure.RecordId, failure.RetryCount);
            var newCount     = currentCount + 1;
            var isDead       = _retry.IsDeadLetter(newCount);
            var nextAttempt  = isDead ? null : _retry.ComputeNextAttempt(newCount);

            await _reader.UpdateRetryAsync(
                failure.RecordId, failure.TableName,
                newCount, nextAttempt, failure.Exception.Message, isDead, ct);

            if (isDead)
            {
                _logger.LogError(
                    "DeadLetter: {RecordId} table={Table} after {Count} data-level retries. Reason: {Reason}",
                    failure.RecordId, failure.TableName, newCount, failure.Exception.Message);

                await SendDeadLetterAlertAsync(failure, newCount, ct);
            }
            else
            {
                _logger.LogWarning(
                    "Data retry {Count}/{Max}: {RecordId} table={Table}. Next: {Next}. Reason: {Reason}",
                    newCount, _config.MaxRetries, failure.RecordId, failure.TableName,
                    nextAttempt, failure.Exception.Message);
            }
        }
    }

    // ── Dead-letter webhook alert ──────────────────────────────────────────────

    private async Task SendDeadLetterAlertAsync(FailedRecord failure, int retryCount, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.AlertWebhookUrl))
            return;

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                event_type  = "dead_letter",
                station_id  = _config.StationId,
                site_name   = _config.SiteName,
                record_id   = failure.RecordId,
                table_name  = failure.TableName,
                retry_count = retryCount,
                reason      = failure.Exception.Message,
                occurred_at = DateTime.UtcNow.ToString("O")
            });

            using var content  = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(_config.AlertWebhookUrl, content, ct);

            if (!response.IsSuccessStatusCode)
                _logger.LogWarning(
                    "Dead-letter alert webhook returned {Status}", (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to send dead-letter alert webhook");
        }
    }
}
