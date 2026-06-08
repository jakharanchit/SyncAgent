using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SyncAgent.Config;
using SyncAgent.Data.Models;

namespace SyncAgent.Data;

public sealed class SQLiteReader
{
    private readonly string               _dbPath;
    private readonly ILogger<SQLiteReader> _logger;

    private const int ExpectedSchemaVersion = 1;

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
    // Reads any table by name, returns all columns as a dictionary.
    // TableMap drives: which PK to filter on, which columns need type coercions.

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
        var placeholders = string.Join(", ",
            Enumerable.Range(0, ids.Count).Select(i => $"@p{i}"));

        // Quote identifiers so table/column names with underscores or mixed case are safe
        var sql = $"""
            SELECT * FROM "{map.SourceTable}"
            WHERE  "{map.PrimaryKey}" IN ({placeholders})
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"@p{i}", ids[i]);

        var records = new List<GenericRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var columns = new Dictionary<string, object?>(reader.FieldCount,
                StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < reader.FieldCount; i++)
                columns[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);

            // Coerce SQLite INTEGER (0/1) → bool for columns declared as BooleanColumns.
            // PostgreSQL BOOLEAN requires a real bool; Npgsql rejects a long for that type.
            foreach (var col in map.BooleanColumns)
                if (columns.TryGetValue(col, out var v) && v is long l)
                    columns[col] = l != 0;

            records.Add(new GenericRecord
            {
                RecordId    = columns[map.PrimaryKey]?.ToString() ?? "",
                SourceTable = map.SourceTable,
                TargetTable = map.TargetTable,
                PrimaryKey  = map.PrimaryKey,
                Columns     = columns
            });
        }

        return records;
    }

    // ── Status updates ────────────────────────────────────────────────────────

    public async Task MarkSyncedAsync(List<string> recordIds, CancellationToken ct)
    {
        if (recordIds.Count == 0) return;

        var placeholders = string.Join(", ",
            Enumerable.Range(0, recordIds.Count).Select(i => $"@p{i}"));
        var sql = $"""
            UPDATE sync_status
            SET    synced = 1, last_attempt = datetime('now')
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

    // ── Startup checks ────────────────────────────────────────────────────────

    public async Task<int> GetSchemaVersionAsync(CancellationToken ct)
    {
        const string sql = "SELECT version FROM schema_version ORDER BY rowid DESC LIMIT 1";

        await using var conn = OpenReadOnly();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long v ? (int)v : 0;
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
