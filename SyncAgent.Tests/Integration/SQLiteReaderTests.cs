using FluentAssertions;
using SyncAgent.Config;
using SyncAgent.Tests.Fixtures;
using Xunit;

namespace SyncAgent.Tests.Integration;

/// <summary>
/// Each test gets its own SQLite database because xUnit instantiates the test class
/// once per test method. The IDisposable implementation deletes the temp file.
/// </summary>
public sealed class SQLiteReaderTests : IDisposable
{
    private readonly SqliteFixture _db  = new();
    private readonly SyncAgent.Data.SQLiteReader _reader;

    public SQLiteReaderTests()
    {
        _reader = TestHelpers.MakeReader(_db.DbPath);
    }

    public void Dispose() => _db.Dispose();

    // ── GetPendingAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetPendingAsync_EmptyTable_ReturnsEmptyList()
    {
        var result = await _reader.GetPendingAsync(100, CancellationToken.None);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPendingAsync_PendingRecord_IsReturned()
    {
        _db.InsertPending("ord-1", "orders");

        var result = await _reader.GetPendingAsync(100, CancellationToken.None);

        result.Should().ContainSingle(r => r.RecordId == "ord-1" && r.TableName == "orders");
    }

    [Fact]
    public async Task GetPendingAsync_NextAttemptInPast_IsReturned()
    {
        _db.InsertPending("ord-1", "orders", nextAttempt: "2000-01-01 00:00:00");

        var result = await _reader.GetPendingAsync(100, CancellationToken.None);

        result.Should().ContainSingle(r => r.RecordId == "ord-1");
    }

    [Fact]
    public async Task GetPendingAsync_NextAttemptInFuture_IsSkipped()
    {
        _db.InsertPending("ord-1", "orders", nextAttempt: "2099-01-01 00:00:00");

        var result = await _reader.GetPendingAsync(100, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPendingAsync_Synced1_IsSkipped()
    {
        _db.InsertPending("ord-1", "orders", synced: 1);

        var result = await _reader.GetPendingAsync(100, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPendingAsync_DeadLetter_IsSkipped()
    {
        _db.InsertPending("ord-1", "orders", synced: 2);

        var result = await _reader.GetPendingAsync(100, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPendingAsync_BatchSizeRespected()
    {
        for (int i = 0; i < 10; i++)
            _db.InsertPending($"ord-{i}", "orders");

        var result = await _reader.GetPendingAsync(5, CancellationToken.None);

        result.Should().HaveCount(5);
    }

    [Fact]
    public async Task GetPendingAsync_OrderedByRecordIdAsc()
    {
        // Insert in reverse order to verify sorting
        _db.InsertPending("c", "orders");
        _db.InsertPending("a", "orders");
        _db.InsertPending("b", "orders");

        var result = await _reader.GetPendingAsync(100, CancellationToken.None);

        result.Select(r => r.RecordId).Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task GetPendingAsync_RetryCountPreserved()
    {
        _db.InsertPending("ord-1", "orders", retryCount: 3);

        var result = await _reader.GetPendingAsync(100, CancellationToken.None);

        result[0].RetryCount.Should().Be(3);
    }

    // ── HydrateRecordsAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task HydrateRecordsAsync_SinglePk_ReturnsCorrectColumns()
    {
        _db.ExecSql("""
            CREATE TABLE orders (order_id TEXT PRIMARY KEY, amount REAL);
            INSERT INTO orders VALUES ('ord-1', 99.5);
            """);
        _db.InsertPending("ord-1", "orders");

        var pending = await _reader.GetPendingAsync(100, CancellationToken.None);
        var maps    = new List<TableMap> { TestHelpers.SimpleMap("orders", "public.orders", "order_id") };

        var records = await _reader.HydrateRecordsAsync(pending, maps, CancellationToken.None);

        records.Should().ContainSingle();
        records[0].RecordId.Should().Be("ord-1");
        records[0].Columns["order_id"].Should().Be("ord-1");
        records[0].Columns["amount"].Should().Be(99.5);
    }

    [Fact]
    public async Task HydrateRecordsAsync_CompositePk_SplitsRecordIdCorrectly()
    {
        _db.ExecSql("""
            CREATE TABLE measurements (device_id TEXT, seq_no INTEGER, value REAL,
                PRIMARY KEY (device_id, seq_no));
            INSERT INTO measurements VALUES ('dev-1', 1, 42.0);
            """);
        // Composite record_id uses separator "|"
        _db.InsertPending("dev-1|1", "measurements");

        var pending = await _reader.GetPendingAsync(100, CancellationToken.None);
        var maps    = new List<TableMap>
        {
            new()
            {
                SourceTable      = "measurements",
                TargetTable      = "public.measurements",
                PrimaryKeys      = ["device_id", "seq_no"],
                PrimaryKeySeparator = "|"
            }
        };

        var records = await _reader.HydrateRecordsAsync(pending, maps, CancellationToken.None);

        records.Should().ContainSingle();
        records[0].RecordId.Should().Be("dev-1|1");
        records[0].Columns["device_id"].Should().Be("dev-1");
    }

    [Fact]
    public async Task HydrateRecordsAsync_BooleanColumns_CoercesIntToBool()
    {
        _db.ExecSql("""
            CREATE TABLE flags (flag_id TEXT PRIMARY KEY, is_active INTEGER);
            INSERT INTO flags VALUES ('f1', 0);
            INSERT INTO flags VALUES ('f2', 1);
            """);
        _db.InsertPending("f1", "flags");
        _db.InsertPending("f2", "flags");

        var pending = await _reader.GetPendingAsync(100, CancellationToken.None);
        var maps = new List<TableMap>
        {
            new()
            {
                SourceTable    = "flags",
                TargetTable    = "public.flags",
                PrimaryKey     = "flag_id",
                BooleanColumns = ["is_active"]
            }
        };

        var records = await _reader.HydrateRecordsAsync(pending, maps, CancellationToken.None);

        var f1 = records.Single(r => r.RecordId == "f1");
        var f2 = records.Single(r => r.RecordId == "f2");
        f1.Columns["is_active"].Should().Be(false);
        f2.Columns["is_active"].Should().Be(true);
    }

    [Fact]
    public async Task HydrateRecordsAsync_RowDeletedBetweenPendingAndHydrate_GracefullyAbsent()
    {
        _db.ExecSql("""
            CREATE TABLE orders (order_id TEXT PRIMARY KEY, amount REAL);
            INSERT INTO orders VALUES ('ghost', 1.0);
            """);
        _db.InsertPending("ghost", "orders");

        // Delete the row from the source table — simulates a race
        _db.ExecSql("DELETE FROM orders WHERE order_id = 'ghost'");

        var pending = await _reader.GetPendingAsync(100, CancellationToken.None);
        var maps    = new List<TableMap> { TestHelpers.SimpleMap("orders", "public.orders", "order_id") };

        var records = await _reader.HydrateRecordsAsync(pending, maps, CancellationToken.None);

        // Should return 0 records, not throw
        records.Should().BeEmpty();
    }

    [Fact]
    public async Task HydrateRecordsAsync_UnknownTableName_RecordSkipped()
    {
        _db.InsertPending("r1", "nonexistent_table");

        var pending = await _reader.GetPendingAsync(100, CancellationToken.None);
        var maps    = new List<TableMap>(); // no mapping for "nonexistent_table"

        var records = await _reader.HydrateRecordsAsync(pending, maps, CancellationToken.None);

        records.Should().BeEmpty();
    }

    // ── MarkSyncedAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task MarkSyncedAsync_SetsSynced1AndTimestamp()
    {
        _db.InsertPending("ord-1", "orders");

        await _reader.MarkSyncedAsync([("ord-1", "orders")], CancellationToken.None);

        var (synced, retryCount, _, _) = _db.ReadStatus("ord-1", "orders");
        synced.Should().Be(1);
        retryCount.Should().Be(0); // untouched
    }

    [Fact]
    public async Task MarkSyncedAsync_EmptyList_NoOp()
    {
        // Should not throw
        await _reader.MarkSyncedAsync([], CancellationToken.None);
    }

    [Fact]
    public async Task MarkSyncedAsync_OnlyTargetedRecordUpdated()
    {
        _db.InsertPending("ord-1", "orders");
        _db.InsertPending("ord-2", "orders");

        await _reader.MarkSyncedAsync([("ord-1", "orders")], CancellationToken.None);

        _db.ReadStatus("ord-1", "orders").Synced.Should().Be(1);
        _db.ReadStatus("ord-2", "orders").Synced.Should().Be(0);
    }

    // ── UpdateRetryAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateRetryAsync_IncrementsRetryCount_KeepsSynced0()
    {
        _db.InsertPending("ord-1", "orders", retryCount: 0);

        await _reader.UpdateRetryAsync("ord-1", "orders", 1, "2099-01-01 00:00:00",
            "timeout", false, CancellationToken.None);

        var (synced, retryCount, nextAttempt, failureReason) = _db.ReadStatus("ord-1", "orders");
        synced.Should().Be(0);
        retryCount.Should().Be(1);
        nextAttempt.Should().Be("2099-01-01 00:00:00");
        failureReason.Should().Be("timeout");
    }

    [Fact]
    public async Task UpdateRetryAsync_DeadLetter_SetsSynced2()
    {
        _db.InsertPending("ord-1", "orders");

        await _reader.UpdateRetryAsync("ord-1", "orders", 10, null, "too many retries",
            deadLetter: true, CancellationToken.None);

        _db.ReadStatus("ord-1", "orders").Synced.Should().Be(2);
    }

    // ── UpdateFailureReasonAsync ──────────────────────────────────────────────

    [Fact]
    public async Task UpdateFailureReasonAsync_SetsReasonOnly_DoesNotTouchRetryCount()
    {
        _db.InsertPending("ord-1", "orders", retryCount: 2);

        await _reader.UpdateFailureReasonAsync("ord-1", "orders",
            "network error", CancellationToken.None);

        var (synced, retryCount, _, failureReason) = _db.ReadStatus("ord-1", "orders");
        retryCount.Should().Be(2);   // unchanged
        synced.Should().Be(0);       // unchanged
        failureReason.Should().Be("network error");
    }

    [Fact]
    public async Task UpdateFailureReasonAsync_NullReason_ClearsFailureReason()
    {
        _db.InsertPending("ord-1", "orders");
        await _reader.UpdateFailureReasonAsync("ord-1", "orders", "old error", CancellationToken.None);
        await _reader.UpdateFailureReasonAsync("ord-1", "orders", null, CancellationToken.None);

        _db.ReadStatus("ord-1", "orders").FailureReason.Should().BeNull();
    }

    // ── PruneOldSyncedAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task PruneOldSyncedAsync_OldSyncedRecords_AreDeleted()
    {
        // Insert a synced record with last_attempt set well in the past
        _db.ExecSql("""
            INSERT INTO sync_status (record_id, table_name, synced, retry_count, last_attempt)
            VALUES ('old', 'orders', 1, 0, '2000-01-01 00:00:00')
            """);

        await _reader.PruneOldSyncedAsync(1, CancellationToken.None);

        _db.CountWhere("record_id = 'old'").Should().Be(0);
    }

    [Fact]
    public async Task PruneOldSyncedAsync_RecentSyncedRecords_Kept()
    {
        // synced=1 but last_attempt is very recent
        _db.InsertPending("recent", "orders", synced: 1,
            lastAttempt: DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));

        await _reader.PruneOldSyncedAsync(30, CancellationToken.None);

        _db.CountWhere("record_id = 'recent'").Should().Be(1);
    }

    [Fact]
    public async Task PruneOldSyncedAsync_PendingRecords_NeverPruned()
    {
        _db.ExecSql("""
            INSERT INTO sync_status (record_id, table_name, synced, retry_count, last_attempt)
            VALUES ('pending', 'orders', 0, 0, '2000-01-01 00:00:00')
            """);

        await _reader.PruneOldSyncedAsync(1, CancellationToken.None);

        _db.CountWhere("record_id = 'pending'").Should().Be(1);
    }

    // ── ResetDeadLettersAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task ResetDeadLettersAsync_All_ResetsAllDeadLetters()
    {
        _db.InsertPending("d1", "orders", synced: 2, retryCount: 10);
        _db.InsertPending("d2", "orders", synced: 2, retryCount: 10);
        _db.InsertPending("p1", "orders", synced: 0);

        var count = await _reader.ResetDeadLettersAsync(null, CancellationToken.None);

        count.Should().Be(2);
        _db.ReadStatus("d1", "orders").Synced.Should().Be(0);
        _db.ReadStatus("d1", "orders").RetryCount.Should().Be(0);
        _db.ReadStatus("p1", "orders").Synced.Should().Be(0); // untouched
    }

    [Fact]
    public async Task ResetDeadLettersAsync_WithTableFilter_OnlyResetsSpecifiedTable()
    {
        _db.InsertPending("d1", "orders", synced: 2);
        _db.InsertPending("d2", "items",  synced: 2);

        var count = await _reader.ResetDeadLettersAsync("orders", CancellationToken.None);

        count.Should().Be(1);
        _db.ReadStatus("d1", "orders").Synced.Should().Be(0);
        _db.ReadStatus("d2", "items").Synced.Should().Be(2); // untouched
    }

    [Fact]
    public async Task ResetDeadLettersAsync_NoDeadLetters_ReturnsZero()
    {
        _db.InsertPending("p1", "orders");

        var count = await _reader.ResetDeadLettersAsync(null, CancellationToken.None);

        count.Should().Be(0);
    }

    // ── VerifySchemaAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task VerifySchemaAsync_FullSchema_ReturnsEmpty()
    {
        var missing = await _reader.VerifySchemaAsync(CancellationToken.None);
        missing.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifySchemaAsync_MissingTable_ReturnsAllRequiredColumns()
    {
        var emptyPath = SqliteFixture.CreateEmptyDb();
        try
        {
            var reader = TestHelpers.MakeReader(emptyPath);
            var missing = await reader.VerifySchemaAsync(CancellationToken.None);

            // All 7 required columns reported as missing
            missing.Should().HaveCount(7);
            missing.Should().Contain("record_id").And.Contain("synced").And.Contain("retry_count");
        }
        finally
        {
            SqliteFixtureExtensions.TryDeleteStatic(emptyPath);
        }
    }

    // ── EnsureWalModeAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureWalModeAsync_SetsJournalModeToWal()
    {
        await _reader.EnsureWalModeAsync(CancellationToken.None);

        // Verify via raw query
        using var conn = _db.Open(write: false);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        var mode = cmd.ExecuteScalar() as string;
        mode.Should().Be("wal");
    }

    // ── GetDeadLetterCountAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetDeadLetterCountAsync_ReturnsCorrectCount()
    {
        _db.InsertPending("d1", "orders", synced: 2);
        _db.InsertPending("d2", "orders", synced: 2);
        _db.InsertPending("p1", "orders", synced: 0);

        var count = await _reader.GetDeadLetterCountAsync(CancellationToken.None);

        count.Should().Be(2);
    }

    // ── GetPendingDeletesAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetPendingDeletesAsync_ReturnsUnsynced()
    {
        _db.ExecSql("""
            CREATE TABLE orders_deletes (record_id TEXT NOT NULL, synced INTEGER NOT NULL DEFAULT 0);
            INSERT INTO orders_deletes (record_id, synced) VALUES ('del-1', 0);
            INSERT INTO orders_deletes (record_id, synced) VALUES ('del-2', 1);
            """);

        var map = new TableMap
        {
            SourceTable = "orders",
            TargetTable = "public.orders",
            PrimaryKey  = "order_id"
        };

        var deletes = await _reader.GetPendingDeletesAsync(map, 100, CancellationToken.None);

        deletes.Should().ContainSingle(d => d.RecordId == "del-1");
        deletes.Should().NotContain(d => d.RecordId == "del-2");
    }

    [Fact]
    public async Task GetPendingDeletesAsync_MissingTable_ThrowsInvalidOperationException()
    {
        var map = new TableMap
        {
            SourceTable    = "orders",
            TargetTable    = "public.orders",
            PrimaryKey     = "order_id",
            DeleteLogTable = "nonexistent_deletes"
        };

        var act = () => _reader.GetPendingDeletesAsync(map, 100, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*nonexistent_deletes*");
    }

    // ── MarkDeletesSyncedAsync ────────────────────────────────────────────────

    [Fact]
    public async Task MarkDeletesSyncedAsync_SetsSynced1()
    {
        _db.ExecSql("""
            CREATE TABLE orders_deletes (record_id TEXT NOT NULL, synced INTEGER NOT NULL DEFAULT 0);
            INSERT INTO orders_deletes VALUES ('del-1', 0);
            INSERT INTO orders_deletes VALUES ('del-2', 0);
            """);

        await _reader.MarkDeletesSyncedAsync("orders_deletes", ["del-1"], CancellationToken.None);

        using var conn = _db.Open(write: false);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT synced FROM orders_deletes WHERE record_id = 'del-1'";
        ((long)cmd.ExecuteScalar()!).Should().Be(1);

        cmd.CommandText = "SELECT synced FROM orders_deletes WHERE record_id = 'del-2'";
        ((long)cmd.ExecuteScalar()!).Should().Be(0); // untouched
    }

    [Fact]
    public async Task MarkDeletesSyncedAsync_EmptyList_NoOp()
    {
        _db.ExecSql("CREATE TABLE orders_deletes (record_id TEXT, synced INTEGER DEFAULT 0)");
        await _reader.MarkDeletesSyncedAsync("orders_deletes", [], CancellationToken.None);
        // No exception
    }

    // ── GetPendingStatsByTableAsync ───────────────────────────────────────────

    [Fact]
    public async Task GetPendingStatsByTableAsync_ReturnsPerTableCounts()
    {
        _db.InsertPending("o1", "orders", synced: 0);
        _db.InsertPending("o2", "orders", synced: 0);
        _db.InsertPending("o3", "orders", synced: 2);
        _db.InsertPending("i1", "items",  synced: 0);

        var stats = await _reader.GetPendingStatsByTableAsync(CancellationToken.None);

        var orders = stats.Single(s => s.TableName == "orders");
        orders.Pending.Should().Be(2);
        orders.DeadLetter.Should().Be(1);

        var items = stats.Single(s => s.TableName == "items");
        items.Pending.Should().Be(1);
        items.DeadLetter.Should().Be(0);
    }
}

// Extend SqliteFixture with a static helper needed above
public static class SqliteFixtureExtensions
{
    public static void TryDeleteStatic(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        try { if (File.Exists(path + "-wal")) File.Delete(path + "-wal"); } catch { }
        try { if (File.Exists(path + "-shm")) File.Delete(path + "-shm"); } catch { }
    }
}
