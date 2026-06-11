using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace SyncAgent.Tests.Fixtures;

/// <summary>
/// Spins up a real PostgreSQL container via Testcontainers.
/// Shared across all tests in a [Collection("Postgres")] collection.
/// Requires Docker to be running.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    // ── DDL helpers ───────────────────────────────────────────────────────────

    public async Task ExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> CountAsync(string tableName)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {tableName}", conn);
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    /// <summary>Generates a unique table name to isolate tests. Prefix keeps it recognisable.</summary>
    public static string UniqueName(string prefix = "tbl") =>
        $"{prefix}_{Guid.NewGuid():N}"[..30]; // keep under 64 chars for Postgres identifier limit
}

/// <summary>Collection definition so both PostgresWriterTests and SyncOrchestratorTests share one container.</summary>
[CollectionDefinition("Postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture> { }
