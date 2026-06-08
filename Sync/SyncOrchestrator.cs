using Microsoft.Extensions.Logging;
using SyncAgent.Config;
using SyncAgent.Data;
using SyncAgent.Data.Models;

namespace SyncAgent.Sync;

public sealed class SyncOrchestrator
{
    private const int ExpectedSchemaVersion = 1;

    private readonly SQLiteReader                _reader;
    private readonly PostgresWriter              _writer;
    private readonly RetryPolicy                 _retry;
    private readonly SyncConfig                  _config;
    private readonly ILogger<SyncOrchestrator>   _logger;

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

    // Called once on startup before the loop begins.
    // Schema mismatch → throws (stops the service).
    // Postgres unreachable → warns only (station may start offline).
    public async Task VerifyStartupAsync(CancellationToken ct)
    {
        var schemaVersion = await _reader.GetSchemaVersionAsync(ct);
        if (schemaVersion != ExpectedSchemaVersion)
        {
            _logger.LogError(
                "Schema version mismatch. SQLite={Local} Expected={Expected}. Sync suspended.",
                schemaVersion, ExpectedSchemaVersion);
            throw new InvalidOperationException(
                $"Schema version mismatch: got {schemaVersion}, expected {ExpectedSchemaVersion}");
        }

        _logger.LogInformation("SQLite schema version OK: {Version}", schemaVersion);

        await _reader.EnsureWalModeAsync(ct);

        if (_config.Tables.Count == 0)
            _logger.LogWarning(
                "No table mappings configured. Add entries to the Tables array in syncagent.json. " +
                "SyncAgent will run but will not sync any records.");
        else
            _logger.LogInformation(
                "Table mappings loaded: {Tables}",
                string.Join(", ", _config.Tables.Select(t => $"{t.SourceTable}→{t.TargetTable}")));

        bool anyInjectStation = _config.Tables.Any(t => t.InjectStationId);
        if (anyInjectStation && string.IsNullOrWhiteSpace(_config.StationId))
            _logger.LogWarning(
                "Station.StationId is not set but InjectStationId=true on one or more table mappings. " +
                "An empty string will be written as station_id. Set Station.StationId in syncagent.json " +
                "or set InjectStationId=false on all tables if a station identifier is not needed.");

        var postgresOk = await _writer.TestConnectionAsync(ct);
        if (!postgresOk)
            _logger.LogWarning(
                "PostgreSQL unreachable at startup. Sync will begin once connectivity is restored.");
        else
            _logger.LogInformation("PostgreSQL connection verified.");

        var deadLetterCount = await _reader.GetDeadLetterCountAsync(ct);
        if (deadLetterCount > 0)
            _logger.LogWarning("Dead letter records requiring manual review: {Count}", deadLetterCount);

        _logger.LogInformation(
            "SyncAgent startup complete. StationId={StationId} SiteName={SiteName}",
            _config.StationId, _config.SiteName);
    }

    public async Task<CycleResult> RunCycleAsync(CancellationToken ct)
    {
        // 1. Read pending records due for sync
        var pending = await _reader.GetPendingAsync(_config.BatchSize, ct);
        if (pending.Count == 0)
        {
            var dlCount = await _reader.GetDeadLetterCountAsync(ct);
            return new CycleResult { PostgresReachable = true, DeadLetterCount = dlCount };
        }

        // 2. Hydrate — fetch full record data from SQLite using configured table maps
        var records = await _reader.HydrateRecordsAsync(pending, _config.Tables, ct);
        if (records.Count == 0)
        {
            _logger.LogWarning(
                "Got {Pending} pending rows but hydration returned 0 records. " +
                "Check that all pending table_name values exist in the Tables config.",
                pending.Count);
            return CycleResult.Empty();
        }

        // 3. Attempt batch write to PostgreSQL
        var result            = await _writer.WriteBatchAsync(records, ct);
        var postgresReachable = result.SucceededIds.Count > 0 || result.Failures.Count == 0;

        // 4. Mark synced records
        if (result.SucceededIds.Count > 0)
            await _reader.MarkSyncedAsync(result.SucceededIds, ct);

        // 5. Handle failures — apply backoff or dead-letter
        if (result.Failures.Count > 0)
            await HandleFailuresAsync(result.Failures, pending, ct);

        var deadLetterCount = await _reader.GetDeadLetterCountAsync(ct);

        return new CycleResult
        {
            Synced            = result.SucceededIds.Count,
            StillPending      = pending.Count - result.SucceededIds.Count,
            Failed            = result.Failures.Count,
            DeadLetterCount   = deadLetterCount,
            PostgresReachable = postgresReachable,
            LastSyncedAt      = result.SucceededIds.Count > 0 ? DateTime.UtcNow : null
        };
    }

    private async Task HandleFailuresAsync(
        List<FailedRecord> failures,
        List<PendingRecord> pending,
        CancellationToken ct)
    {
        var retryCounts = pending.ToDictionary(p => p.RecordId, p => p.RetryCount);

        foreach (var failure in failures)
        {
            var currentCount = retryCounts.GetValueOrDefault(failure.RecordId, failure.RetryCount);
            var newCount     = currentCount + 1;
            var isDead       = _retry.IsDeadLetter(newCount);
            var nextAttempt  = isDead ? null : _retry.ComputeNextAttempt(newCount);

            await _reader.UpdateRetryAsync(
                failure.RecordId,
                failure.TableName,
                newCount,
                nextAttempt,
                failure.Exception.Message,
                isDead,
                ct);

            if (isDead)
                _logger.LogError(
                    "DeadLetter: {RecordId} table={Table} after {Count} retries. Reason: {Reason}",
                    failure.RecordId, failure.TableName, newCount, failure.Exception.Message);
            else
                _logger.LogWarning(
                    "Retry {Count}/{Max}: {RecordId} table={Table}. Next attempt: {Next}. Reason: {Reason}",
                    newCount, _config.MaxRetries,
                    failure.RecordId, failure.TableName,
                    nextAttempt, failure.Exception.Message);
        }
    }
}
