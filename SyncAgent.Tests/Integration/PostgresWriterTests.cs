using FluentAssertions;
using SyncAgent.Config;
using SyncAgent.Data.Models;
using SyncAgent.Tests.Fixtures;
using Xunit;

namespace SyncAgent.Tests.Integration;

/// <summary>
/// Real PostgreSQL via Testcontainers. Each test creates its own uniquely-named table to
/// avoid cross-test interference. Requires Docker to be running.
/// </summary>
[Collection("Postgres")]
public sealed class PostgresWriterTests
{
    private readonly PostgresFixture _pg;

    public PostgresWriterTests(PostgresFixture pg) => _pg = pg;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates a table and returns its name, plus a configured writer.</summary>
    private async Task<(string TableName, SyncAgent.Data.PostgresWriter Writer)> SetupAsync(
        string ddl,
        TableMap? mapOverride = null)
    {
        var name = PostgresFixture.UniqueName("orders");
        var finalDdl = ddl.Replace("__TABLE__", name);
        await _pg.ExecAsync(finalDdl);

        var map = mapOverride ?? TestHelpers.SimpleMap("orders", name, "order_id");
        var config = TestHelpers.MakeConfig("unused.db", _pg.ConnectionString, tables: [map]);
        var writer = TestHelpers.MakeWriter(config);
        return (name, writer);
    }

    private static GenericRecord Rec(string id, string table, Dictionary<string, object?> cols) => new()
    {
        RecordId    = id,
        SourceTable = "orders",
        TargetTable = table,
        PrimaryKey  = "order_id",
        PrimaryKeys = ["order_id"],
        Columns     = cols
    };

    // ── WriteBatchAsync — happy paths ──────────────────────────────────────────

    [Fact]
    public async Task WriteBatch_SingleRecord_InsertsRow()
    {
        var (tbl, writer) = await SetupAsync(
            "CREATE TABLE __TABLE__ (order_id TEXT PRIMARY KEY, amount DOUBLE PRECISION)");

        var result = await writer.WriteBatchAsync(
            [Rec("ord-1", tbl, new() { ["order_id"] = "ord-1", ["amount"] = 99.5 })],
            CancellationToken.None);

        result.Succeeded.Should().ContainSingle(r => r.RecordId == "ord-1");
        result.Failures.Should().BeEmpty();
        (await _pg.CountAsync(tbl)).Should().Be(1);
    }

    [Fact]
    public async Task WriteBatch_MultipleRecords_AllInserted()
    {
        var (tbl, writer) = await SetupAsync(
            "CREATE TABLE __TABLE__ (order_id TEXT PRIMARY KEY, amount DOUBLE PRECISION)");

        var records = Enumerable.Range(1, 3).Select(i =>
            Rec($"ord-{i}", tbl, new() { ["order_id"] = $"ord-{i}", ["amount"] = (double)i * 10 })).ToList();

        var result = await writer.WriteBatchAsync(records, CancellationToken.None);

        result.Succeeded.Should().HaveCount(3);
        (await _pg.CountAsync(tbl)).Should().Be(3);
    }

    [Fact]
    public async Task WriteBatch_ConflictStrategyNothing_DuplicateIgnored_OriginalPreserved()
    {
        var (tbl, writer) = await SetupAsync(
            "CREATE TABLE __TABLE__ (order_id TEXT PRIMARY KEY, amount DOUBLE PRECISION)");

        // Insert original
        await _pg.ExecAsync($"INSERT INTO {tbl} VALUES ('dup', 1.0)");

        // Try inserting same PK with different amount
        var result = await writer.WriteBatchAsync(
            [Rec("dup", tbl, new() { ["order_id"] = "dup", ["amount"] = 999.0 })],
            CancellationToken.None);

        result.Succeeded.Should().ContainSingle(r => r.RecordId == "dup");
        // Original amount unchanged (conflict DO NOTHING)
        var amount = await _pg.ScalarAsync($"SELECT amount FROM {tbl} WHERE order_id = 'dup'");
        Convert.ToDouble(amount).Should().Be(1.0);
    }

    [Fact]
    public async Task WriteBatch_ConflictStrategyUpdate_DuplicateIsUpdated()
    {
        var name = PostgresFixture.UniqueName("orders");
        await _pg.ExecAsync($"CREATE TABLE {name} (order_id TEXT PRIMARY KEY, amount DOUBLE PRECISION)");
        await _pg.ExecAsync($"INSERT INTO {name} VALUES ('dup', 1.0)");

        var map    = TestHelpers.SimpleMap("orders", name, "order_id", conflictStrategy: "update");
        var config = TestHelpers.MakeConfig("unused.db", _pg.ConnectionString, tables: [map]);
        var writer = TestHelpers.MakeWriter(config);

        await writer.WriteBatchAsync(
            [Rec("dup", name, new() { ["order_id"] = "dup", ["amount"] = 999.0 })],
            CancellationToken.None);

        var amount = await _pg.ScalarAsync($"SELECT amount FROM {name} WHERE order_id = 'dup'");
        Convert.ToDouble(amount).Should().Be(999.0);
    }

    // ── Column transformations ─────────────────────────────────────────────────

    [Fact]
    public async Task WriteBatch_InjectStationId_PopulatesColumn()
    {
        var name = PostgresFixture.UniqueName("orders");
        await _pg.ExecAsync(
            $"CREATE TABLE {name} (order_id TEXT PRIMARY KEY, station_id TEXT)");

        var map = new TableMap
        {
            SourceTable     = "orders",
            TargetTable     = name,
            PrimaryKey      = "order_id",
            InjectStationId = true,
            ConflictStrategy = "nothing"
        };
        var config = TestHelpers.MakeConfig("unused.db", _pg.ConnectionString, tables: [map]);
        config.StationId = "STA-TEST";
        var writer = new SyncAgent.Data.PostgresWriter(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncAgent.Data.PostgresWriter>.Instance);

        await writer.WriteBatchAsync(
            [Rec("ord-1", name, new() { ["order_id"] = "ord-1" })],
            CancellationToken.None);

        var stationId = await _pg.ScalarAsync($"SELECT station_id FROM {name} WHERE order_id = 'ord-1'");
        stationId.Should().Be("STA-TEST");
    }

    [Fact]
    public async Task WriteBatch_TimestampColumns_CastToTimestamptz()
    {
        var name = PostgresFixture.UniqueName("orders");
        await _pg.ExecAsync(
            $"CREATE TABLE {name} (order_id TEXT PRIMARY KEY, created_at TIMESTAMPTZ)");

        var map = new TableMap
        {
            SourceTable      = "orders",
            TargetTable      = name,
            PrimaryKey       = "order_id",
            TimestampColumns = ["created_at"],
            InjectStationId  = false,
            ConflictStrategy = "nothing"
        };
        var config = TestHelpers.MakeConfig("unused.db", _pg.ConnectionString, tables: [map]);
        var writer = TestHelpers.MakeWriter(config);

        var result = await writer.WriteBatchAsync(
            [Rec("ord-1", name, new()
            {
                ["order_id"]   = "ord-1",
                ["created_at"] = "2026-01-15 10:30:00"
            })],
            CancellationToken.None);

        result.Succeeded.Should().ContainSingle();
        // If the cast didn't work, the INSERT would throw a type error
        (await _pg.CountAsync(name)).Should().Be(1);
    }

    [Fact]
    public async Task WriteBatch_ExcludeColumns_ExcludedColumnNotInserted()
    {
        var name = PostgresFixture.UniqueName("orders");
        // Postgres table does NOT have the excluded column — so if ExcludeColumns
        // didn't work, the INSERT would fail with "column does not exist"
        await _pg.ExecAsync(
            $"CREATE TABLE {name} (order_id TEXT PRIMARY KEY, amount DOUBLE PRECISION)");

        var map = new TableMap
        {
            SourceTable      = "orders",
            TargetTable      = name,
            PrimaryKey       = "order_id",
            ExcludeColumns   = ["internal_note"],
            InjectStationId  = false,
            ConflictStrategy = "nothing"
        };
        var config = TestHelpers.MakeConfig("unused.db", _pg.ConnectionString, tables: [map]);
        var writer = TestHelpers.MakeWriter(config);

        var result = await writer.WriteBatchAsync(
            [Rec("ord-1", name, new()
            {
                ["order_id"]     = "ord-1",
                ["amount"]       = 10.0,
                ["internal_note"] = "secret"  // should be excluded
            })],
            CancellationToken.None);

        result.Succeeded.Should().ContainSingle();
    }

    [Fact]
    public async Task WriteBatch_ColumnMap_RenamesColumnInInsert()
    {
        var name = PostgresFixture.UniqueName("orders");
        // Postgres has 'created_at'; SQLite source has 'ts'
        await _pg.ExecAsync(
            $"CREATE TABLE {name} (order_id TEXT PRIMARY KEY, created_at TEXT)");

        var map = new TableMap
        {
            SourceTable      = "orders",
            TargetTable      = name,
            PrimaryKey       = "order_id",
            ColumnMap        = new() { ["ts"] = "created_at" },
            InjectStationId  = false,
            ConflictStrategy = "nothing"
        };
        var config = TestHelpers.MakeConfig("unused.db", _pg.ConnectionString, tables: [map]);
        var writer = TestHelpers.MakeWriter(config);

        var result = await writer.WriteBatchAsync(
            [Rec("ord-1", name, new()
            {
                ["order_id"] = "ord-1",
                ["ts"]       = "2026-01-15"   // SQLite name
            })],
            CancellationToken.None);

        result.Succeeded.Should().ContainSingle();
        var val = await _pg.ScalarAsync($"SELECT created_at FROM {name} WHERE order_id = 'ord-1'");
        val.Should().Be("2026-01-15");
    }

    [Fact]
    public async Task WriteBatch_CompositePk_InsertsAndConflictsCorrectly()
    {
        var name = PostgresFixture.UniqueName("meas");
        await _pg.ExecAsync($"""
            CREATE TABLE {name} (
                device_id TEXT    NOT NULL,
                seq_no    INTEGER NOT NULL,
                value     DOUBLE PRECISION,
                PRIMARY KEY (device_id, seq_no)
            )
            """);

        var map = new TableMap
        {
            SourceTable      = "measurements",
            TargetTable      = name,
            PrimaryKeys      = ["device_id", "seq_no"],
            InjectStationId  = false,
            ConflictStrategy = "nothing"
        };
        var config = TestHelpers.MakeConfig("unused.db", _pg.ConnectionString, tables: [map]);
        var writer = TestHelpers.MakeWriter(config);

        var record = new GenericRecord
        {
            RecordId    = "dev-1|1",
            SourceTable = "measurements",
            TargetTable = name,
            PrimaryKey  = "",
            PrimaryKeys = ["device_id", "seq_no"],
            Columns     = new() { ["device_id"] = "dev-1", ["seq_no"] = 1L, ["value"] = 42.0 }
        };

        var result = await writer.WriteBatchAsync([record], CancellationToken.None);

        result.Succeeded.Should().ContainSingle();
        (await _pg.CountAsync(name)).Should().Be(1);

        // Insert duplicate → conflict DO NOTHING, no error
        var result2 = await writer.WriteBatchAsync([record], CancellationToken.None);
        result2.Succeeded.Should().ContainSingle();
        (await _pg.CountAsync(name)).Should().Be(1); // still 1 row
    }

    // ── Failure handling ───────────────────────────────────────────────────────

    [Fact]
    public async Task WriteBatch_BadConnectionString_AllRecordsAreInfraFailures()
    {
        var map    = TestHelpers.SimpleMap("orders", "public.orders", "order_id");
        var config = TestHelpers.MakeConfig("unused.db",
            "Host=nonexistent.invalid.test;Database=x;Username=u;Password=p;Connect Timeout=2",
            tables: [map]);
        var writer = TestHelpers.MakeWriter(config);

        var records = new List<GenericRecord>
        {
            Rec("r1", "public.orders", new() { ["order_id"] = "r1", ["amount"] = 1.0 }),
            Rec("r2", "public.orders", new() { ["order_id"] = "r2", ["amount"] = 2.0 })
        };

        var result = await writer.WriteBatchAsync(records, CancellationToken.None);

        result.Succeeded.Should().BeEmpty();
        result.Failures.Should().HaveCount(2);
        result.Failures.Should().AllSatisfy(f => f.Kind.Should().Be(FailureKind.Infrastructure));
    }

    [Fact]
    public async Task WriteBatch_DataError_FallsBackToPerRecord_BadRecordIsDataFailure()
    {
        var name = PostgresFixture.UniqueName("orders");
        // amount NOT NULL — inserting NULL will cause a 23502 (not_null_violation) data error
        await _pg.ExecAsync(
            $"CREATE TABLE {name} (order_id TEXT PRIMARY KEY, amount DOUBLE PRECISION NOT NULL)");

        var map    = TestHelpers.SimpleMap("orders", name, "order_id");
        var config = TestHelpers.MakeConfig("unused.db", _pg.ConnectionString, tables: [map]);
        var writer = TestHelpers.MakeWriter(config);

        var records = new List<GenericRecord>
        {
            Rec("good", name, new() { ["order_id"] = "good", ["amount"] = 50.0 }),
            Rec("bad",  name, new() { ["order_id"] = "bad",  ["amount"] = null })   // will fail
        };

        var result = await writer.WriteBatchAsync(records, CancellationToken.None);

        result.Succeeded.Should().ContainSingle(r => r.RecordId == "good");
        result.Failures.Should().ContainSingle(f =>
            f.RecordId == "bad" && f.Kind == FailureKind.Data);
    }

    // ── DeleteBatchAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteBatch_ExistingRow_DeletedAndReturned()
    {
        var name = PostgresFixture.UniqueName("orders");
        await _pg.ExecAsync($"CREATE TABLE {name} (order_id TEXT PRIMARY KEY)");
        await _pg.ExecAsync($"INSERT INTO {name} VALUES ('del-1')");

        var map    = TestHelpers.SimpleMap("orders", name, "order_id");
        var config = TestHelpers.MakeConfig("unused.db", _pg.ConnectionString, tables: [map]);
        var writer = TestHelpers.MakeWriter(config);

        var deletes = new List<PendingDelete>
        {
            new("del-1", "orders", name, "order_id", "orders_deletes")
        };

        var succeeded = await writer.DeleteBatchAsync(deletes, CancellationToken.None);

        succeeded.Should().ContainSingle("del-1");
        (await _pg.CountAsync(name)).Should().Be(0);
    }

    [Fact]
    public async Task DeleteBatch_MissingRow_StillCountedAsSucceeded()
    {
        // DELETE on a non-existent row doesn't throw — it just affects 0 rows.
        // SyncAgent marks it succeeded to avoid infinite retry on un-deletable rows.
        var name = PostgresFixture.UniqueName("orders");
        await _pg.ExecAsync($"CREATE TABLE {name} (order_id TEXT PRIMARY KEY)");

        var map    = TestHelpers.SimpleMap("orders", name, "order_id");
        var config = TestHelpers.MakeConfig("unused.db", _pg.ConnectionString, tables: [map]);
        var writer = TestHelpers.MakeWriter(config);

        var deletes = new List<PendingDelete>
        {
            new("ghost", "orders", name, "order_id", "orders_deletes")
        };

        var succeeded = await writer.DeleteBatchAsync(deletes, CancellationToken.None);

        succeeded.Should().ContainSingle("ghost");
    }

    [Fact]
    public async Task DeleteBatch_BadConnection_ReturnsEmpty()
    {
        var map    = TestHelpers.SimpleMap("orders", "public.orders", "order_id");
        var config = TestHelpers.MakeConfig("unused.db",
            "Host=nonexistent.invalid.test;Database=x;Username=u;Password=p;Connect Timeout=2",
            tables: [map]);
        var writer = TestHelpers.MakeWriter(config);

        var deletes = new List<PendingDelete>
        {
            new("d1", "orders", "public.orders", "order_id", "orders_deletes")
        };

        var succeeded = await writer.DeleteBatchAsync(deletes, CancellationToken.None);

        succeeded.Should().BeEmpty();
    }

    // ── ValidateSchemaAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ValidateSchema_MissingTable_ReturnsWarning()
    {
        var map    = TestHelpers.SimpleMap("orders", "public.nonexistent_table_xyz", "order_id");
        var config = TestHelpers.MakeConfig("unused.db", _pg.ConnectionString, tables: [map]);
        var writer = TestHelpers.MakeWriter(config);

        var warnings = await writer.ValidateSchemaAsync([map], CancellationToken.None);

        warnings.Should().ContainSingle(w => w.Contains("nonexistent_table_xyz"));
    }

    [Fact]
    public async Task ValidateSchema_MissingPkColumn_ReturnsWarning()
    {
        var name = PostgresFixture.UniqueName("orders");
        await _pg.ExecAsync($"CREATE TABLE {name} (wrong_col TEXT)");

        var map    = TestHelpers.SimpleMap("orders", name, "order_id");  // PK = order_id, not in table
        var config = TestHelpers.MakeConfig("unused.db", _pg.ConnectionString, tables: [map]);
        var writer = TestHelpers.MakeWriter(config);

        var warnings = await writer.ValidateSchemaAsync([map], CancellationToken.None);

        warnings.Should().ContainSingle(w => w.Contains("order_id"));
    }

    [Fact]
    public async Task ValidateSchema_MissingStationIdWithInject_ReturnsWarning()
    {
        var name = PostgresFixture.UniqueName("orders");
        await _pg.ExecAsync($"CREATE TABLE {name} (order_id TEXT PRIMARY KEY)");
        // No station_id column

        var map = new TableMap
        {
            SourceTable     = "orders",
            TargetTable     = name,
            PrimaryKey      = "order_id",
            InjectStationId = true  // requires station_id in Postgres
        };
        var config = TestHelpers.MakeConfig("unused.db", _pg.ConnectionString, tables: [map]);
        var writer = TestHelpers.MakeWriter(config);

        var warnings = await writer.ValidateSchemaAsync([map], CancellationToken.None);

        warnings.Should().ContainSingle(w => w.Contains("station_id"));
    }

    [Fact]
    public async Task ValidateSchema_ValidTable_ReturnsNoWarnings()
    {
        var name = PostgresFixture.UniqueName("orders");
        await _pg.ExecAsync(
            $"CREATE TABLE {name} (order_id TEXT PRIMARY KEY, amount DOUBLE PRECISION)");

        var map    = TestHelpers.SimpleMap("orders", name, "order_id", injectStationId: false);
        var config = TestHelpers.MakeConfig("unused.db", _pg.ConnectionString, tables: [map]);
        var writer = TestHelpers.MakeWriter(config);

        var warnings = await writer.ValidateSchemaAsync([map], CancellationToken.None);

        warnings.Should().BeEmpty();
    }

    // ── TestConnectionAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task TestConnection_ValidConnectionString_ReturnsTrue()
    {
        var config = TestHelpers.MakeConfig("unused.db", _pg.ConnectionString);
        var writer = TestHelpers.MakeWriter(config);

        var ok = await writer.TestConnectionAsync(CancellationToken.None);

        ok.Should().BeTrue();
    }

    [Fact]
    public async Task TestConnection_BadConnectionString_ReturnsFalse()
    {
        var config = TestHelpers.MakeConfig("unused.db",
            "Host=nonexistent.invalid.test;Database=x;Username=u;Password=p;Connect Timeout=2");
        var writer = TestHelpers.MakeWriter(config);

        var ok = await writer.TestConnectionAsync(CancellationToken.None);

        ok.Should().BeFalse();
    }
}
