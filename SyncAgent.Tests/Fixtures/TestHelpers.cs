using Microsoft.Extensions.Logging.Abstractions;
using SyncAgent.Config;
using SyncAgent.Data;
using SyncAgent.Data.Models;

namespace SyncAgent.Tests.Fixtures;

public static class TestHelpers
{
    // ── Config builders ───────────────────────────────────────────────────────

    public static SyncConfig MakeConfig(
        string dbPath,
        string? pgConnStr  = null,
        int     maxRetries = 3,
        int     batchSize  = 100,
        List<TableMap>? tables = null) => new()
    {
        SQLitePath            = dbPath,
        PostgresConnStr       = pgConnStr ?? "",
        StationId             = "TEST-01",
        SiteName              = "Test Site",
        BatchSize             = batchSize,
        MaxRetries            = maxRetries,
        CommandTimeoutSeconds = 10,
        PruneAfterDays        = 0,
        Tables                = tables ?? []
    };

    // ── TableMap builders ──────────────────────────────────────────────────────

    public static TableMap SimpleMap(
        string source          = "orders",
        string target          = "public.orders",
        string pk              = "order_id",
        bool   injectStationId = false,
        string conflictStrategy = "nothing") => new()
    {
        SourceTable      = source,
        TargetTable      = target,
        PrimaryKey       = pk,
        InjectStationId  = injectStationId,
        ConflictStrategy = conflictStrategy
    };

    // ── GenericRecord builder ─────────────────────────────────────────────────

    public static GenericRecord MakeRecord(
        string recordId,
        string sourceTable = "orders",
        string targetTable = "public.orders",
        string pk          = "order_id",
        Dictionary<string, object?>? columns = null) => new()
    {
        RecordId    = recordId,
        SourceTable = sourceTable,
        TargetTable = targetTable,
        PrimaryKey  = pk,
        PrimaryKeys = [pk],
        Columns     = columns ?? new Dictionary<string, object?>
        {
            [pk]       = recordId,
            ["amount"] = 100.0
        }
    };

    // ── Service factories ─────────────────────────────────────────────────────

    public static SQLiteReader MakeReader(SyncConfig config) =>
        new(config, NullLogger<SQLiteReader>.Instance);

    public static SQLiteReader MakeReader(string dbPath) =>
        MakeReader(MakeConfig(dbPath));

    public static PostgresWriter MakeWriter(SyncConfig config) =>
        new(config, NullLogger<PostgresWriter>.Instance);
}
