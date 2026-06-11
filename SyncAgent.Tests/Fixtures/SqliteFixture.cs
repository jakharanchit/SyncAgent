using Microsoft.Data.Sqlite;

namespace SyncAgent.Tests.Fixtures;

/// <summary>
/// Creates a fresh temp SQLite database per test. Disposed and deleted after each test.
/// xUnit instantiates the test class once per test method, so this gives each test its own DB.
/// </summary>
public sealed class SqliteFixture : IDisposable
{
    public string DbPath { get; } =
        Path.Combine(Path.GetTempPath(), $"syncagent_test_{Guid.NewGuid():N}.db");

    public SqliteFixture()
    {
        using var conn = Open(write: true);
        conn.Open();
        Exec(conn, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS sync_status (
                record_id       TEXT    NOT NULL,
                table_name      TEXT    NOT NULL,
                synced          INTEGER NOT NULL DEFAULT 0,
                retry_count     INTEGER NOT NULL DEFAULT 0,
                last_attempt    TEXT,
                next_attempt    TEXT,
                failure_reason  TEXT,
                created_at      TEXT    NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (record_id, table_name)
            );
            """);
    }

    // ── Data helpers ──────────────────────────────────────────────────────────

    public void InsertPending(
        string recordId, string tableName,
        int synced = 0, int retryCount = 0,
        string? nextAttempt = null, string? lastAttempt = null)
    {
        using var conn = Open(write: true);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sync_status (record_id, table_name, synced, retry_count, next_attempt, last_attempt)
            VALUES (@id, @table, @synced, @retry, @next, @last)
            """;
        cmd.Parameters.AddWithValue("@id",     recordId);
        cmd.Parameters.AddWithValue("@table",  tableName);
        cmd.Parameters.AddWithValue("@synced", synced);
        cmd.Parameters.AddWithValue("@retry",  retryCount);
        cmd.Parameters.AddWithValue("@next",   (object?)nextAttempt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@last",   (object?)lastAttempt ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void ExecSql(string sql, params (string Name, object? Value)[] parameters)
    {
        using var conn = Open(write: true);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ── Status readers ────────────────────────────────────────────────────────

    public (int Synced, int RetryCount, string? NextAttempt, string? FailureReason)
        ReadStatus(string recordId, string tableName)
    {
        using var conn = Open(write: false);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT synced, retry_count, next_attempt, failure_reason
            FROM   sync_status
            WHERE  record_id  = @id
              AND  table_name = @table
            """;
        cmd.Parameters.AddWithValue("@id",    recordId);
        cmd.Parameters.AddWithValue("@table", tableName);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
            return (
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3));

        throw new InvalidOperationException($"Row not found: {recordId}/{tableName}");
    }

    public int CountWhere(string whereClause)
    {
        using var conn = Open(write: false);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM sync_status WHERE {whereClause}";
        return (int)(long)(cmd.ExecuteScalar() ?? 0L);
    }

    // ── Schema builder variant (no sync_status) ───────────────────────────────

    /// <summary>Creates a DB path WITHOUT applying the sync_status schema — used to test missing schema detection.</summary>
    public static string CreateEmptyDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"syncagent_empty_{Guid.NewGuid():N}.db");
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate");
        conn.Open();
        return path;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public SqliteConnection Open(bool write) =>
        new($"Data Source={DbPath};Mode={(write ? "ReadWriteCreate" : "ReadOnly")}");

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TryDelete(DbPath);
        TryDelete(DbPath + "-wal");
        TryDelete(DbPath + "-shm");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
