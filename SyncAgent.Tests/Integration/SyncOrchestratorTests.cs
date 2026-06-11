using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SyncAgent.Config;
using SyncAgent.Data;
using SyncAgent.Data.Models;
using SyncAgent.Sync;
using SyncAgent.Tests.Fixtures;
using Xunit;

namespace SyncAgent.Tests.Integration;

/// <summary>
/// End-to-end tests that exercise the full sync cycle with real SQLite + real PostgreSQL.
/// Each test gets a fresh SQLite DB and creates a unique Postgres table.
/// </summary>
[Collection("Postgres")]
public sealed class SyncOrchestratorTests : IDisposable
{
    private readonly PostgresFixture _pg;
    private readonly SqliteFixture   _db = new();

    public SyncOrchestratorTests(PostgresFixture pg) => _pg = pg;

    public void Dispose() => _db.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private SyncOrchestrator MakeOrchestrator(SyncConfig config)
    {
        var reader = TestHelpers.MakeReader(config);
        var writer = TestHelpers.MakeWriter(config);
        var retry  = new RetryPolicy(config);
        return new SyncOrchestrator(
            reader, writer, retry, config,
            NullLogger<SyncOrchestrator>.Instance);
    }

    /// <summary>Creates a Postgres table and returns its name.</summary>
    private async Task<string> PgTableAsync(string ddl)
    {
        var name = PostgresFixture.UniqueName("sync");
        await _pg.ExecAsync(ddl.Replace("__TABLE__", name));
        return name;
    }

    // ── RunCycleAsync — empty / no-op ─────────────────────────────────────────

    [Fact]
    public async Task RunCycle_NoPending_ReturnsSyncedZero()
    {
        var config = TestHelpers.MakeConfig(_db.DbPath, _pg.ConnectionString, tables: [
            TestHelpers.SimpleMap()
        ]);

        var result = await MakeOrchestrator(config).RunCycleAsync(CancellationToken.None);

        result.Synced.Should().Be(0);
        result.PostgresReachable.Should().BeTrue();
    }

    // ── RunCycleAsync — golden path ───────────────────────────────────────────

    [Fact]
    public async Task RunCycle_AllRecordsSucceed_SyncedInSQLiteAndPostgres()
    {
        var pgTable = await PgTableAsync(
            "CREATE TABLE __TABLE__ (order_id TEXT PRIMARY KEY, amount DOUBLE PRECISION)");

        // SQLite application table + data
        _db.ExecSql("CREATE TABLE orders (order_id TEXT PRIMARY KEY, amount REAL)");
        _db.ExecSql("INSERT INTO orders VALUES ('ord-1', 42.0)");
        _db.InsertPending("ord-1", "orders");

        var config = TestHelpers.MakeConfig(_db.DbPath, _pg.ConnectionString, tables: [
            TestHelpers.SimpleMap("orders", pgTable, "order_id")
        ]);

        var result = await MakeOrchestrator(config).RunCycleAsync(CancellationToken.None);

        result.Synced.Should().Be(1);
        result.Failed.Should().Be(0);
        result.PostgresReachable.Should().BeTrue();

        _db.ReadStatus("ord-1", "orders").Synced.Should().Be(1);
        (await _pg.CountAsync(pgTable)).Should().Be(1);
    }

    [Fact]
    public async Task RunCycle_MultipleRecords_AllSyncedCorrectly()
    {
        var pgTable = await PgTableAsync(
            "CREATE TABLE __TABLE__ (order_id TEXT PRIMARY KEY, amount DOUBLE PRECISION)");

        _db.ExecSql("CREATE TABLE orders (order_id TEXT PRIMARY KEY, amount REAL)");
        foreach (var i in Enumerable.Range(1, 5))
        {
            _db.ExecSql($"INSERT INTO orders VALUES ('ord-{i}', {i * 10.0})");
            _db.InsertPending($"ord-{i}", "orders");
        }

        var config = TestHelpers.MakeConfig(_db.DbPath, _pg.ConnectionString, tables: [
            TestHelpers.SimpleMap("orders", pgTable, "order_id")
        ]);

        var result = await MakeOrchestrator(config).RunCycleAsync(CancellationToken.None);

        result.Synced.Should().Be(5);
        (await _pg.CountAsync(pgTable)).Should().Be(5);
    }

    // ── RunCycleAsync — infrastructure failure ────────────────────────────────

    [Fact]
    public async Task RunCycle_InfraFailure_RecordsDeferred_RetryCountUnchanged()
    {
        _db.ExecSql("CREATE TABLE orders (order_id TEXT PRIMARY KEY, amount REAL)");
        _db.ExecSql("INSERT INTO orders VALUES ('ord-1', 10.0)");
        _db.InsertPending("ord-1", "orders", retryCount: 2);

        var config = TestHelpers.MakeConfig(_db.DbPath,
            "Host=nonexistent.invalid.test;Database=x;Username=u;Password=p;Connect Timeout=2",
            tables: [TestHelpers.SimpleMap("orders", "public.orders", "order_id")]);

        var result = await MakeOrchestrator(config).RunCycleAsync(CancellationToken.None);

        result.Synced.Should().Be(0);
        result.PostgresReachable.Should().BeFalse();

        // retry_count must NOT be incremented for infra failures
        _db.ReadStatus("ord-1", "orders").RetryCount.Should().Be(2);
        _db.ReadStatus("ord-1", "orders").Synced.Should().Be(0);
    }

    [Fact]
    public async Task RunCycle_InfraFailure_FailureReasonIsSet()
    {
        _db.ExecSql("CREATE TABLE orders (order_id TEXT PRIMARY KEY)");
        _db.ExecSql("INSERT INTO orders VALUES ('ord-1')");
        _db.InsertPending("ord-1", "orders");

        var config = TestHelpers.MakeConfig(_db.DbPath,
            "Host=nonexistent.invalid.test;Database=x;Username=u;Password=p;Connect Timeout=2",
            tables: [TestHelpers.SimpleMap("orders", "public.orders", "order_id")]);

        await MakeOrchestrator(config).RunCycleAsync(CancellationToken.None);

        _db.ReadStatus("ord-1", "orders").FailureReason.Should().NotBeNullOrEmpty();
    }

    // ── RunCycleAsync — data failure ──────────────────────────────────────────

    [Fact]
    public async Task RunCycle_DataFailure_RetryCountIncremented()
    {
        var pgTable = await PgTableAsync(
            "CREATE TABLE __TABLE__ (order_id TEXT PRIMARY KEY, amount DOUBLE PRECISION NOT NULL)");

        // amount=NULL will cause a NOT NULL violation in Postgres → data failure
        _db.ExecSql("CREATE TABLE orders (order_id TEXT PRIMARY KEY, amount REAL)");
        _db.ExecSql("INSERT INTO orders (order_id) VALUES ('bad')");  // amount is NULL
        _db.InsertPending("bad", "orders", retryCount: 0);

        var config = TestHelpers.MakeConfig(_db.DbPath, _pg.ConnectionString, tables: [
            TestHelpers.SimpleMap("orders", pgTable, "order_id")
        ]);

        var result = await MakeOrchestrator(config).RunCycleAsync(CancellationToken.None);

        result.Synced.Should().Be(0);
        _db.ReadStatus("bad", "orders").RetryCount.Should().Be(1);
        _db.ReadStatus("bad", "orders").Synced.Should().Be(0);
    }

    [Fact]
    public async Task RunCycle_DataFailureReachesMaxRetries_RecordDeadLettered()
    {
        var pgTable = await PgTableAsync(
            "CREATE TABLE __TABLE__ (order_id TEXT PRIMARY KEY, amount DOUBLE PRECISION NOT NULL)");

        _db.ExecSql("CREATE TABLE orders (order_id TEXT PRIMARY KEY, amount REAL)");
        _db.ExecSql("INSERT INTO orders (order_id) VALUES ('bad')");

        // MaxRetries=1: first data failure dead-letters the record (newCount=1 >= maxRetries=1)
        _db.InsertPending("bad", "orders", retryCount: 0);

        var config = TestHelpers.MakeConfig(_db.DbPath, _pg.ConnectionString,
            maxRetries: 1, tables: [
                TestHelpers.SimpleMap("orders", pgTable, "order_id")
            ]);

        await MakeOrchestrator(config).RunCycleAsync(CancellationToken.None);

        _db.ReadStatus("bad", "orders").Synced.Should().Be(2);
    }

    // ── RunCycleAsync — mixed success and failure ──────────────────────────────

    [Fact]
    public async Task RunCycle_MixedRecords_GoodOnesSyncedBadOnesRetried()
    {
        var pgTable = await PgTableAsync(
            "CREATE TABLE __TABLE__ (order_id TEXT PRIMARY KEY, amount DOUBLE PRECISION NOT NULL)");

        _db.ExecSql("CREATE TABLE orders (order_id TEXT PRIMARY KEY, amount REAL)");
        _db.ExecSql("INSERT INTO orders VALUES ('good', 100.0)");
        _db.ExecSql("INSERT INTO orders (order_id) VALUES ('bad')");   // NULL amount
        _db.InsertPending("good", "orders");
        _db.InsertPending("bad", "orders");

        var config = TestHelpers.MakeConfig(_db.DbPath, _pg.ConnectionString, tables: [
            TestHelpers.SimpleMap("orders", pgTable, "order_id")
        ]);

        var result = await MakeOrchestrator(config).RunCycleAsync(CancellationToken.None);

        result.Synced.Should().Be(1);
        _db.ReadStatus("good", "orders").Synced.Should().Be(1);
        _db.ReadStatus("bad",  "orders").Synced.Should().Be(0);
        _db.ReadStatus("bad",  "orders").RetryCount.Should().Be(1);
        (await _pg.CountAsync(pgTable)).Should().Be(1);
    }

    // ── RunCycleAsync — idempotency ───────────────────────────────────────────

    [Fact]
    public async Task RunCycle_RunTwice_NoDuplicates_NoErrors()
    {
        var pgTable = await PgTableAsync(
            "CREATE TABLE __TABLE__ (order_id TEXT PRIMARY KEY, amount DOUBLE PRECISION)");

        _db.ExecSql("CREATE TABLE orders (order_id TEXT PRIMARY KEY, amount REAL)");
        _db.ExecSql("INSERT INTO orders VALUES ('ord-1', 10.0)");
        _db.InsertPending("ord-1", "orders");

        var config = TestHelpers.MakeConfig(_db.DbPath, _pg.ConnectionString, tables: [
            TestHelpers.SimpleMap("orders", pgTable, "order_id")
        ]);
        var orch = MakeOrchestrator(config);

        var r1 = await orch.RunCycleAsync(CancellationToken.None);
        var r2 = await orch.RunCycleAsync(CancellationToken.None); // nothing pending

        r1.Synced.Should().Be(1);
        r2.Synced.Should().Be(0); // already synced, nothing pending
        (await _pg.CountAsync(pgTable)).Should().Be(1); // still just one row
    }

    // ── RunCycleAsync — delete propagation ───────────────────────────────────

    [Fact]
    public async Task RunCycle_DeletePropagation_RowDeletedFromPostgres()
    {
        var pgTable = await PgTableAsync(
            "CREATE TABLE __TABLE__ (order_id TEXT PRIMARY KEY)");

        // Pre-insert a row in Postgres that we'll delete
        await _pg.ExecAsync($"INSERT INTO {pgTable} VALUES ('del-1')");

        // Delete log in SQLite
        _db.ExecSql("""
            CREATE TABLE orders_deletes (
                record_id TEXT NOT NULL,
                synced    INTEGER NOT NULL DEFAULT 0
            );
            INSERT INTO orders_deletes (record_id, synced) VALUES ('del-1', 0);
            """);

        var map = new TableMap
        {
            SourceTable     = "orders",
            TargetTable     = pgTable,
            PrimaryKey      = "order_id",
            SyncDeletes     = true,
            InjectStationId = false,
            ConflictStrategy = "nothing"
        };
        var config = TestHelpers.MakeConfig(_db.DbPath, _pg.ConnectionString, tables: [map]);

        var result = await MakeOrchestrator(config).RunCycleAsync(CancellationToken.None);

        result.Deleted.Should().Be(1);
        (await _pg.CountAsync(pgTable)).Should().Be(0);

        // Delete log entry marked as synced in SQLite
        using var conn = _db.Open(write: false);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT synced FROM orders_deletes WHERE record_id = 'del-1'";
        ((long)cmd.ExecuteScalar()!).Should().Be(1);
    }

    // ── RunCycleAsync — pruning ───────────────────────────────────────────────

    [Fact]
    public async Task RunCycle_PruneAfterDays_OldSyncedRowsRemoved()
    {
        // Pre-insert old synced rows
        _db.ExecSql("""
            INSERT INTO sync_status (record_id, table_name, synced, retry_count, last_attempt)
            VALUES ('old1', 'orders', 1, 0, '2000-01-01 00:00:00'),
                   ('old2', 'orders', 1, 0, '2000-01-01 00:00:00')
            """);

        var config = TestHelpers.MakeConfig(_db.DbPath, _pg.ConnectionString);
        config.PruneAfterDays = 1;
        config.Tables = [TestHelpers.SimpleMap()];

        await MakeOrchestrator(config).RunCycleAsync(CancellationToken.None);

        _db.CountWhere("record_id IN ('old1', 'old2')").Should().Be(0);
    }

    // ── VerifyStartupAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyStartup_ValidEverything_DoesNotThrow()
    {
        var pgTable = await PgTableAsync(
            "CREATE TABLE __TABLE__ (order_id TEXT PRIMARY KEY)");

        var config = TestHelpers.MakeConfig(_db.DbPath, _pg.ConnectionString, tables: [
            TestHelpers.SimpleMap("orders", pgTable, "order_id", injectStationId: false)
        ]);

        var orch = MakeOrchestrator(config);
        var act = () => orch.VerifyStartupAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task VerifyStartup_MissingSyncStatusTable_ThrowsInvalidOperation()
    {
        var emptyPath = SqliteFixture.CreateEmptyDb();
        try
        {
            var config = TestHelpers.MakeConfig(emptyPath, _pg.ConnectionString);
            var orch   = MakeOrchestrator(config);

            var act = () => orch.VerifyStartupAsync(CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*sync_status*");
        }
        finally
        {
            SqliteFixtureExtensions.TryDeleteStatic(emptyPath);
        }
    }

    [Fact]
    public async Task VerifyStartup_PostgresUnreachable_DoesNotThrow()
    {
        var config = TestHelpers.MakeConfig(_db.DbPath,
            "Host=nonexistent.invalid.test;Database=x;Username=u;Password=p;Connect Timeout=2");

        var orch = MakeOrchestrator(config);
        var act  = () => orch.VerifyStartupAsync(CancellationToken.None);

        // Should warn but not throw — offline-first design
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task VerifyStartup_DeadLetterRecordsPresent_DoesNotThrow()
    {
        _db.InsertPending("d1", "orders", synced: 2);

        var config = TestHelpers.MakeConfig(_db.DbPath, _pg.ConnectionString);
        var orch   = MakeOrchestrator(config);

        // Should log a warning but not throw
        await orch.VerifyStartupAsync(CancellationToken.None);
        _db.CountWhere("synced = 2").Should().Be(1); // record untouched
    }
}
