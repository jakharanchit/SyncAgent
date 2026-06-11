using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SyncAgent.Config;
using SyncAgent.Data.Models;

namespace SyncAgent.Data;

public sealed class SQLiteReader
{
    private readonly string               _dbPath;
    private readonly ILogger<SQLiteReader> _logger;

    public SQLiteReader(SyncConfig config, ILogger<SQLiteReader> logger)
    {
        _dbPath = config.SQLitePath;
        _logger = logger;
    }

    // ── Pending query ─────────────────────────────────────────────────────────

    public async Task<List<PendingRecord>> GetPendingAsync(int batchSize, CancellationToken ct)
    {
        const string sql = """
            SELECT record_id, table_name, retry_count
            FROM   sync_status
            WHERE  synced = 0
              AND  (next_attempt IS NULL OR next_attempt <= datetime('now'))
            ORDER  BY record_id ASC
            LIMIT  @batchSize
            """;

        await using var conn = OpenReadWrite();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@batchSize", batchSize);

        var results = new List<PendingRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new PendingRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2)));

        return results;
    }

    // ── Generic hydration ─────────────────────────────────────────────────────

    public async Task<List<GenericRecord>> HydrateRecordsAsync(
        List<PendingRecord> pending, List<TableMap> tableMaps, CancellationToken ct)
    {
        var results  = new List<GenericRecord>();
        var mapIndex = tableMaps.ToDictionary(
            m => m.SourceTable,
            StringComparer.OrdinalIgnoreCase);

        await using var conn = OpenReadOnly();
        await conn.OpenAsync(ct);

        foreach (var group in pending.GroupBy(p => p.TableName))
        {
            if (!mapIndex.TryGetValue(group.Key, out var map))
            {
                _logger.LogWarning(
                    "No table mapping configured for '{Table}' — rows skipped. " +
                    "Add an entry to the Tables array in syncagent.json.",
                    group.Key);
                continue;
            }

            var ids = group.Select(p => p.RecordId).ToList();
            results.AddRange(await HydrateTableAsync(conn, map, ids, ct));
        }

        return results;
    }

    private static async Task<List<GenericRecord>> HydrateTableAsync(
        SqliteConnection conn, TableMap map, List<string> ids, CancellationToken ct)
    {
        var effectivePks = map.GetEffectivePrimaryKeys();
        bool isComposite = effectivePks.Length > 1;

        string whereClause;
        if (isComposite)
        {
            // WHERE (pk1, pk2) IN ((@p0k0, @p0k1), (@p1k0, @p1k1), ...)
            var tuples = Enumerable.Range(0, ids.Count)
                .Select(i => $"({string.Join(", ", Enumerable.Range(0, effectivePks.Length).Select(k => $"@p{i}k{k}"))})");
            var pkList = string.Join(", ", effectivePks.Select(pk => $"\"{pk}\""));
            whereClause = $"({pkList}) IN ({string.Join(", ", tuples)})";
        }
        else
        {
            var placeholders = string.Join(", ", Enumerable.Range(0, ids.Count).Select(i => $"@p{i}"));
            whereClause = $"\"{effectivePks[0]}\" IN ({placeholders})";
        }

        var sql = $"""
            SELECT * FROM "{map.SourceTable}"
            WHERE {whereClause}
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        if (isComposite)
        {
            for (int i = 0; i < ids.Count; i++)
            {
                var parts = ids[i].Split(map.PrimaryKeySeparator);
                for (int k = 0; k < effectivePks.Length; k++)
                    cmd.Parameters.AddWithValue($"@p{i}k{k}", k < parts.Length ? (object)parts[k] : DBNull.Value);
            }
        }
        else
        {
            for (int i = 0; i < ids.Count; i++)
                cmd.Parameters.AddWithValue($"@p{i}", ids[i]);
        }

        var records = new List<GenericRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var columns = new Dictionary<string, object?>(reader.FieldCount,
                StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < reader.FieldCount; i++)
                columns[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);

            // Coerce SQLite INTEGER (0/1) → bool for columns declared as BooleanColumns.
            foreach (var col in map.BooleanColumns)
                if (columns.TryGetValue(col, out var v) && v is long l)
                    columns[col] = l != 0;

            // For composite PKs, record_id is the concatenated key values
            var recordId = isComposite
                ? string.Join(map.PrimaryKeySeparator,
                    effectivePks.Select(pk => columns.GetValueOrDefault(pk)?.ToString() ?? ""))
                : columns.GetValueOrDefault(effectivePks[0])?.ToString() ?? "";

            records.Add(new GenericRecord
            {
                RecordId    = recordId,
                SourceTable = map.SourceTable,
                TargetTable = map.TargetTable,
                PrimaryKey  = map.PrimaryKey,
                PrimaryKeys = effectivePks,
                Columns     = columns
            });
        }

        return records;
    }

    // ── Delete log ────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads pending delete entries from the source table's delete-log table.
    /// The delete-log table must have columns: record_id TEXT, synced INTEGER DEFAULT 0.
    /// If the table doesn't exist, logs a warning and returns empty.
    /// </summary>
    public async Task<List<PendingDelete>> GetPendingDeletesAsync(
        TableMap map, int batchSize, CancellationToken ct)
    {
        var logTable = map.GetEffectiveDeleteLogTable();
        var sql = $"""
            SELECT record_id FROM "{logTable}"
            WHERE  synced = 0
            ORDER  BY rowid ASC
            LIMIT  @batchSize
            """;

        await using var conn = OpenReadOnly();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@batchSize", batchSize);

        var results = new List<PendingDelete>();
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                results.Add(new PendingDelete(
                    reader.GetString(0),
                    map.SourceTable,
                    map.TargetTable,
                    map.PrimaryKey,
                    logTable));
        }
        catch (SqliteException ex) when (ex.Message.Contains("no such table"))
        {
            // Logged once — caller handles via warning
            throw new InvalidOperationException(
                $"Delete log table '{logTable}' not found. Create it and a DELETE trigger on " +
                $"'{map.SourceTable}' before enabling SyncDeletes.", ex);
        }

        return results;
    }

    public async Task MarkDeletesSyncedAsync(string logTable, List<string> recordIds, CancellationToken ct)
    {
        if (recordIds.Count == 0) return;

        var placeholders = string.Join(", ",
            Enumerable.Range(0, recordIds.Count).Select(i => $"@p{i}"));

        var sql = $"""
            UPDATE "{logTable}"
            SET    synced = 1
            WHERE  record_id IN ({placeholders})
            """;

        await using var conn = OpenReadWrite();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < recordIds.Count; i++)
            cmd.Parameters.AddWithValue($"@p{i}", recordIds[i]);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Status updates ────────────────────────────────────────────────────────

    /// <summary>
    /// Marks successfully synced records using the composite key (record_id, table_name).
    /// </summary>
    public async Task MarkSyncedAsync(List<(string RecordId, string TableName)> records, CancellationToken ct)
    {
        if (records.Count == 0) return;

        await using var conn = OpenReadWrite();
        await conn.OpenAsync(ct);

        foreach (var (recordId, tableName) in records)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE sync_status
                SET    synced = 1, last_attempt = datetime('now')
                WHERE  record_id  = @recordId
                  AND  table_name = @tableName
                """;
            cmd.Parameters.AddWithValue("@recordId",  recordId);
            cmd.Parameters.AddWithValue("@tableName", tableName);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task UpdateRetryAsync(
        string recordId, string tableName, int retryCount,
        string? nextAttempt, string? failureReason,
        bool deadLetter, CancellationToken ct)
    {
        const string sql = """
            UPDATE sync_status
            SET    retry_count    = @retryCount,
                   last_attempt   = datetime('now'),
                   next_attempt   = @nextAttempt,
                   failure_reason = @failureReason,
                   synced         = @synced
            WHERE  record_id  = @recordId
              AND  table_name = @tableName
            """;

        await using var conn = OpenReadWrite();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@retryCount",    retryCount);
        cmd.Parameters.AddWithValue("@nextAttempt",   (object?)nextAttempt   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@failureReason", (object?)failureReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@synced",        deadLetter ? 2 : 0);
        cmd.Parameters.AddWithValue("@recordId",      recordId);
        cmd.Parameters.AddWithValue("@tableName",     tableName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Updates only failure_reason and last_attempt — does NOT touch retry_count or synced.
    /// Used for infrastructure failures where the record is healthy but the transport is broken.
    /// </summary>
    public async Task UpdateFailureReasonAsync(
        string recordId, string tableName, string? reason, CancellationToken ct)
    {
        const string sql = """
            UPDATE sync_status
            SET    last_attempt   = datetime('now'),
                   failure_reason = @failureReason
            WHERE  record_id  = @recordId
              AND  table_name = @tableName
            """;

        await using var conn = OpenReadWrite();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@failureReason", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@recordId",      recordId);
        cmd.Parameters.AddWithValue("@tableName",     tableName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Pruning ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes successfully synced rows older than <paramref name="days"/> days.
    /// Prevents unbounded growth of sync_status on high-throughput stations.
    /// </summary>
    public async Task PruneOldSyncedAsync(int days, CancellationToken ct)
    {
        var sql = $"""
            DELETE FROM sync_status
            WHERE  synced = 1
              AND  last_attempt < datetime('now', '-{days} days')
            """;

        await using var conn = OpenReadWrite();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var deleted = await cmd.ExecuteNonQueryAsync(ct);
        if (deleted > 0)
            _logger.LogInformation(
                "Pruned {Count} old synced records (older than {Days} days)", deleted, days);
    }

    // ── Stats / observability ─────────────────────────────────────────────────

    /// <summary>
    /// Returns pending and dead-letter counts per table for health reporting.
    /// </summary>
    public async Task<List<TableSyncStats>> GetPendingStatsByTableAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT table_name,
                   SUM(CASE WHEN synced = 0 THEN 1 ELSE 0 END) AS pending,
                   SUM(CASE WHEN synced = 2 THEN 1 ELSE 0 END) AS dead_letter
            FROM   sync_status
            WHERE  synced IN (0, 2)
            GROUP  BY table_name
            ORDER  BY table_name
            """;

        await using var conn = OpenReadOnly();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var results = new List<TableSyncStats>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new TableSyncStats
            {
                TableName  = reader.GetString(0),
                Pending    = reader.IsDBNull(1) ? 0 : (int)reader.GetInt64(1),
                DeadLetter = reader.IsDBNull(2) ? 0 : (int)reader.GetInt64(2)
            });

        return results;
    }

    // ── CLI helpers ───────────────────────────────────────────────────────────

    /// <summary>Returns a summary row for each table: pending, dead-letter, last sync time.</summary>
    public async Task<List<(string Table, int Pending, int DeadLetter, string? LastAttempt)>>
        GetStatusSummaryAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT   table_name,
                     SUM(CASE WHEN synced = 0 THEN 1 ELSE 0 END)   AS pending,
                     SUM(CASE WHEN synced = 2 THEN 1 ELSE 0 END)   AS dead_letter,
                     MAX(CASE WHEN synced = 1 THEN last_attempt END) AS last_synced
            FROM     sync_status
            GROUP BY table_name
            ORDER BY table_name
            """;

        await using var conn = OpenReadOnly();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var results = new List<(string, int, int, string?)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? 0 : (int)reader.GetInt64(1),
                reader.IsDBNull(2) ? 0 : (int)reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));

        return results;
    }

    /// <summary>
    /// Resets dead-letter records (synced=2) back to pending (synced=0, retry_count=0).
    /// Optionally filters to a single table. Returns the count of rows reset.
    /// </summary>
    public async Task<int> ResetDeadLettersAsync(string? tableName, CancellationToken ct)
    {
        var sql = tableName is not null
            ? """
              UPDATE sync_status
              SET    synced      = 0,
                     retry_count = 0,
                     next_attempt = NULL,
                     failure_reason = NULL
              WHERE  synced = 2
                AND  table_name = @tableName
              """
            : """
              UPDATE sync_status
              SET    synced      = 0,
                     retry_count = 0,
                     next_attempt = NULL,
                     failure_reason = NULL
              WHERE  synced = 2
              """;

        await using var conn = OpenReadWrite();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (tableName is not null)
            cmd.Parameters.AddWithValue("@tableName", tableName);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Startup checks ────────────────────────────────────────────────────────

    private static readonly string[] RequiredColumns =
        ["record_id", "table_name", "synced", "retry_count",
         "next_attempt", "failure_reason", "last_attempt"];

    public async Task<string[]> VerifySchemaAsync(CancellationToken ct)
    {
        await using var conn = OpenReadOnly();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(sync_status)";

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            found.Add(reader.GetString(1));

        return RequiredColumns.Where(c => !found.Contains(c)).ToArray();
    }

    public async Task EnsureWalModeAsync(CancellationToken ct)
    {
        await using var conn = OpenReadWrite();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL";
        var mode = await cmd.ExecuteScalarAsync(ct) as string ?? "";

        if (!mode.Equals("wal", StringComparison.OrdinalIgnoreCase))
            _logger.LogWarning(
                "Could not set WAL journal mode (current: {Mode}). " +
                "Concurrent writes may cause SQLITE_BUSY errors.", mode);
        else
            _logger.LogDebug("SQLite journal mode: WAL");
    }

    public async Task<int> GetDeadLetterCountAsync(CancellationToken ct)
    {
        const string sql = "SELECT COUNT(*) FROM sync_status WHERE synced = 2";

        await using var conn = OpenReadWrite();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long count ? (int)count : 0;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private SqliteConnection OpenReadOnly()  =>
        new($"Data Source={_dbPath};Mode=ReadOnly");

    private SqliteConnection OpenReadWrite() =>
        new($"Data Source={_dbPath};Mode=ReadWriteCreate");
}
