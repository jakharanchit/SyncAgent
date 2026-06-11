using Microsoft.Extensions.Logging;
using Npgsql;
using SyncAgent.Config;
using SyncAgent.Data.Models;

namespace SyncAgent.Data;

public sealed class PostgresWriter
{
    private readonly string                          _connStr;
    private readonly string                          _stationId;
    private readonly Dictionary<string, TableMap>    _tableMapIndex;
    private readonly int                             _commandTimeoutSeconds;
    private readonly ILogger<PostgresWriter>         _logger;

    public PostgresWriter(SyncConfig config, ILogger<PostgresWriter> logger)
    {
        _connStr               = config.PostgresConnStr;
        _stationId             = config.StationId;
        _commandTimeoutSeconds = config.CommandTimeoutSeconds;
        _tableMapIndex         = config.Tables.ToDictionary(
            m => m.SourceTable,
            StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    // ── INSERT batch ──────────────────────────────────────────────────────────

    public async Task<WriteResult> WriteBatchAsync(List<GenericRecord> records, CancellationToken ct)
    {
        // ── Step 1: try to open the connection ────────────────────────────────
        NpgsqlConnection conn;
        try
        {
            conn = new NpgsqlConnection(_connStr);
            await conn.OpenAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "PostgreSQL unreachable — {Count} records deferred (retry_count unchanged). {Reason}",
                records.Count, ex.Message);
            return WriteResult.InfrastructureFailed(records, ex);
        }

        await using (conn)
        {
            // ── Step 2: attempt the whole batch in one transaction ─────────────
            try
            {
                return await TryBatchAsync(conn, records, ct);
            }
            catch (Exception batchEx) when (batchEx is not OperationCanceledException)
            {
                if (IsInfrastructureException(batchEx))
                {
                    _logger.LogWarning(
                        "Batch infrastructure failure — {Count} records deferred. {Reason}",
                        records.Count, batchEx.Message);
                    return WriteResult.InfrastructureFailed(records, batchEx);
                }

                // ── Step 3: data error → fall back to per-record to isolate bad row ──
                _logger.LogWarning(
                    "Batch failed with data error — falling back to per-record mode. {Reason}",
                    batchEx.Message);
                return await TryPerRecordAsync(conn, records, ct);
            }
        }
    }

    // ── Batch path (fast, atomic) ──────────────────────────────────────────────
    private async Task<WriteResult> TryBatchAsync(
        NpgsqlConnection conn, List<GenericRecord> records, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var record in records)
        {
            if (!_tableMapIndex.TryGetValue(record.SourceTable, out var map))
                throw new InvalidOperationException(
                    $"No table mapping for '{record.SourceTable}'. Add it to Tables in syncagent.json.");

            await InsertGenericAsync(conn, record, map, ct);
        }

        await tx.CommitAsync(ct);
        _logger.LogDebug("Batch committed: {Count} records", records.Count);
        return WriteResult.Success(records.Select(r => (r.RecordId, r.SourceTable)).ToList());
    }

    // ── Per-record fallback (isolates one bad row) ─────────────────────────────
    private async Task<WriteResult> TryPerRecordAsync(
        NpgsqlConnection conn, List<GenericRecord> records, CancellationToken ct)
    {
        var succeeded = new List<(string RecordId, string TableName)>();
        var failures  = new List<FailedRecord>();

        foreach (var record in records)
        {
            if (!_tableMapIndex.TryGetValue(record.SourceTable, out var map))
            {
                failures.Add(new FailedRecord(
                    record.RecordId, record.SourceTable, 0,
                    new InvalidOperationException($"No mapping for '{record.SourceTable}'"),
                    FailureKind.Data));
                continue;
            }

            try
            {
                await InsertGenericAsync(conn, record, map, ct);
                succeeded.Add((record.RecordId, record.SourceTable));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var kind = IsInfrastructureException(ex) ? FailureKind.Infrastructure : FailureKind.Data;
                failures.Add(new FailedRecord(record.RecordId, record.SourceTable, 0, ex, kind));

                if (kind == FailureKind.Infrastructure)
                {
                    // Lost the connection mid-batch — tag remaining records too
                    var processedIds = succeeded.Select(s => s.RecordId)
                        .Concat(failures.Select(f => f.RecordId))
                        .ToHashSet();
                    failures.AddRange(records
                        .Where(r => !processedIds.Contains(r.RecordId))
                        .Select(r => new FailedRecord(r.RecordId, r.SourceTable, 0, ex, FailureKind.Infrastructure)));
                    break;
                }
                // Data error → continue to next record
            }
        }

        _logger.LogDebug("Per-record fallback: {Succeeded} succeeded, {Failed} failed",
            succeeded.Count, failures.Count);
        return new WriteResult { Succeeded = succeeded, Failures = failures };
    }

    // ── DELETE batch (delete propagation) ─────────────────────────────────────

    /// <summary>
    /// Issues DELETE statements in PostgreSQL for records removed from SQLite.
    /// Returns record IDs that were successfully deleted (or confirmed absent).
    /// Infrastructure failures abort the batch; data errors are skipped.
    /// </summary>
    public async Task<List<string>> DeleteBatchAsync(
        List<PendingDelete> deletes, CancellationToken ct)
    {
        NpgsqlConnection conn;
        try
        {
            conn = new NpgsqlConnection(_connStr);
            await conn.OpenAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "PostgreSQL unreachable — {Count} deletes deferred. {Reason}",
                deletes.Count, ex.Message);
            return [];
        }

        await using (conn)
        {
            var succeeded = new List<string>();

            foreach (var delete in deletes)
            {
                var sql = $"""
                    DELETE FROM {delete.TargetTable}
                    WHERE {delete.PrimaryKey} = @recordId
                    """;

                try
                {
                    await using var cmd = new NpgsqlCommand(sql, conn)
                    {
                        CommandTimeout = _commandTimeoutSeconds
                    };
                    cmd.Parameters.AddWithValue("@recordId", delete.RecordId);
                    await cmd.ExecuteNonQueryAsync(ct);
                    succeeded.Add(delete.RecordId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (IsInfrastructureException(ex))
                    {
                        _logger.LogWarning(
                            "Delete infrastructure failure — {Count} remaining deletes deferred. {Reason}",
                            deletes.Count - succeeded.Count, ex.Message);
                        break;
                    }
                    _logger.LogWarning(
                        "Delete failed for {RecordId} in {Table}: {Reason}",
                        delete.RecordId, delete.TargetTable, ex.Message);
                    // Data error — mark as succeeded anyway to avoid infinite retry on un-deletable rows
                    succeeded.Add(delete.RecordId);
                }
            }

            return succeeded;
        }
    }

    // ── Schema validation ──────────────────────────────────────────────────────

    /// <summary>
    /// Checks each configured target table exists in PostgreSQL and has the expected columns.
    /// Returns a list of human-readable warning messages (empty = schema looks good).
    /// Queries information_schema — read-only, safe to run at startup.
    /// </summary>
    public async Task<List<string>> ValidateSchemaAsync(List<TableMap> tables, CancellationToken ct)
    {
        var warnings = new List<string>();

        NpgsqlConnection conn;
        try
        {
            conn = new NpgsqlConnection(_connStr);
            await conn.OpenAsync(ct);
        }
        catch
        {
            return warnings; // Already warned about unreachable Postgres elsewhere
        }

        await using (conn)
        {
            foreach (var table in tables)
            {
                // Parse schema.tablename — default schema is "public"
                var parts = table.TargetTable.Split('.', 2);
                var (schema, tableName) = parts.Length == 2
                    ? (parts[0], parts[1])
                    : ("public", parts[0]);

                const string sql = """
                    SELECT column_name
                    FROM   information_schema.columns
                    WHERE  table_schema = @schema
                      AND  table_name   = @table
                    """;

                await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = _commandTimeoutSeconds };
                cmd.Parameters.AddWithValue("@schema", schema);
                cmd.Parameters.AddWithValue("@table",  tableName);

                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    found.Add(reader.GetString(0));

                if (found.Count == 0)
                {
                    warnings.Add($"Target table '{table.TargetTable}' not found in PostgreSQL.");
                    continue;
                }

                // Expected Postgres column names (accounting for ColumnMap and station_id injection)
                var effectivePks = table.GetEffectivePrimaryKeys();
                var excludeSet   = new HashSet<string>(table.ExcludeColumns, StringComparer.OrdinalIgnoreCase);

                // We can't know source columns without hydrating a row, so we check PK and injected cols only
                foreach (var pk in effectivePks)
                {
                    var pgPk = table.ColumnMap.TryGetValue(pk, out var mapped) ? mapped : pk;
                    if (!found.Contains(pgPk))
                        warnings.Add($"Table '{table.TargetTable}': primary key column '{pgPk}' not found.");
                }

                if (table.InjectStationId && !found.Contains("station_id"))
                    warnings.Add($"Table '{table.TargetTable}': InjectStationId=true but 'station_id' column not found.");
            }
        }

        return warnings;
    }

    // ── Connection test ────────────────────────────────────────────────────────

    public async Task<bool> TestConnectionAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connStr);
            await conn.OpenAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Exception classifier ───────────────────────────────────────────────────
    // Npgsql sets IsTransient=true for connection-level problems.
    // PostgresException SqlState codes 08xxx (connection), 57xxx (operator shutdown),
    // 53xxx (insufficient resources), 40xxx (transaction rollback) are also infrastructure.
    private static bool IsInfrastructureException(Exception ex) =>
        ex is NpgsqlException { IsTransient: true } ||
        (ex is PostgresException pg && IsInfrastructureSqlState(pg.SqlState));

    private static bool IsInfrastructureSqlState(string? code) =>
        code is not null &&
        (code.StartsWith("08") ||   // Connection Exception
         code.StartsWith("57") ||   // Operator Intervention
         code.StartsWith("53") ||   // Insufficient Resources
         code.StartsWith("40"));    // Transaction Rollback

    // ── Generic INSERT builder ─────────────────────────────────────────────────
    // Supports:
    //   - ExcludeColumns: columns to skip (by SQLite name)
    //   - ColumnMap:      rename SQLite col → Postgres col
    //   - ConflictStrategy: "nothing" (DO NOTHING) | "update" (DO UPDATE SET ...)
    //   - Composite PK:   ON CONFLICT (pk1, pk2) ...
    //   - TimestampColumns: ::timestamptz cast
    //   - InjectStationId: appends station_id if not already in source row
    private async Task InsertGenericAsync(
        NpgsqlConnection conn, GenericRecord record, TableMap map, CancellationToken ct)
    {
        var excludeSet = new HashSet<string>(map.ExcludeColumns, StringComparer.OrdinalIgnoreCase);
        var sourceColumns = record.Columns.Keys
            .Where(c => !excludeSet.Contains(c))
            .ToList();

        bool injectStation = map.InjectStationId
            && !sourceColumns.Contains("station_id", StringComparer.OrdinalIgnoreCase);

        var allSourceCols = injectStation
            ? [.. sourceColumns, "station_id"]
            : sourceColumns;

        // SQLite col name → Postgres col name (via ColumnMap; identity if not mapped)
        string PgName(string col) =>
            map.ColumnMap.TryGetValue(col, out var pg) ? pg : col;

        var tsSet    = new HashSet<string>(map.TimestampColumns, StringComparer.OrdinalIgnoreCase);
        var pgNames  = allSourceCols.Select(PgName).ToList();
        var colList  = string.Join(", ", pgNames);
        var valList  = string.Join(", ", allSourceCols.Select(src =>
            tsSet.Contains(src) ? $"@{src}::timestamptz" : $"@{src}"));

        // ON CONFLICT clause
        var effectivePks = map.GetEffectivePrimaryKeys();
        var pkClause     = string.Join(", ", effectivePks);

        string conflictClause;
        if (map.ConflictStrategy.Equals("update", StringComparison.OrdinalIgnoreCase))
        {
            var pkPgSet  = new HashSet<string>(effectivePks.Select(PgName), StringComparer.OrdinalIgnoreCase);
            var setCols  = pgNames.Where(pg => !pkPgSet.Contains(pg) && pg != "station_id")
                                  .Select(pg => $"{pg} = EXCLUDED.{pg}")
                                  .ToList();
            conflictClause = setCols.Count > 0
                ? $"ON CONFLICT ({pkClause}) DO UPDATE SET {string.Join(", ", setCols)}"
                : $"ON CONFLICT ({pkClause}) DO NOTHING";  // nothing to update — fall back gracefully
        }
        else
        {
            conflictClause = $"ON CONFLICT ({pkClause}) DO NOTHING";
        }

        var sql = $"""
            INSERT INTO {map.TargetTable} ({colList})
            VALUES ({valList})
            {conflictClause}
            """;

        await using var cmd = new NpgsqlCommand(sql, conn)
        {
            CommandTimeout = _commandTimeoutSeconds
        };

        foreach (var src in sourceColumns)
            cmd.Parameters.AddWithValue($"@{src}", record.Columns.GetValueOrDefault(src) ?? DBNull.Value);

        if (injectStation)
            cmd.Parameters.AddWithValue("@station_id", _stationId);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
