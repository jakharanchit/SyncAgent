namespace SyncAgent.Config;

public sealed class TableMap
{
    // ── Core mapping ──────────────────────────────────────────────────────────
    public string   SourceTable      { get; set; } = "";
    public string   TargetTable      { get; set; } = "";
    public bool     InjectStationId  { get; set; } = true;

    // ── Primary key ───────────────────────────────────────────────────────────
    /// <summary>Single-column primary key. Use PrimaryKeys for composite PKs.</summary>
    public string   PrimaryKey       { get; set; } = "";

    /// <summary>
    /// Composite primary key — two or more columns (e.g. ["device_id", "sequence_no"]).
    /// When set, overrides PrimaryKey. record_id in sync_status stores the concatenated
    /// key values joined by PrimaryKeySeparator (default "|").
    /// </summary>
    public string[] PrimaryKeys      { get; set; } = [];

    /// <summary>Separator used to join composite PK values in sync_status.record_id. Default "|".</summary>
    public string   PrimaryKeySeparator { get; set; } = "|";

    /// <summary>Returns the effective PK column(s): PrimaryKeys if set, else [ PrimaryKey ].</summary>
    public string[] GetEffectivePrimaryKeys() =>
        PrimaryKeys.Length > 0 ? PrimaryKeys : [PrimaryKey];

    // ── Column handling ───────────────────────────────────────────────────────
    public string[] TimestampColumns { get; set; } = [];
    public string[] BooleanColumns   { get; set; } = [];

    /// <summary>
    /// SQLite columns to exclude from the PostgreSQL INSERT (e.g. local-only UI state blobs).
    /// Specified by their SQLite column name.
    /// </summary>
    public string[] ExcludeColumns   { get; set; } = [];

    /// <summary>
    /// Rename columns from SQLite name → PostgreSQL name.
    /// Example: { "ts": "created_at" } writes the SQLite "ts" column as "created_at" in Postgres.
    /// </summary>
    public Dictionary<string, string> ColumnMap { get; set; } = new();

    // ── Conflict / upsert behaviour ───────────────────────────────────────────
    /// <summary>
    /// How to handle duplicate primary keys in PostgreSQL.
    /// "nothing" (default) — ON CONFLICT DO NOTHING (silently ignore duplicates).
    /// "update"            — ON CONFLICT DO UPDATE SET col=EXCLUDED.col, ... (upsert).
    /// </summary>
    public string   ConflictStrategy { get; set; } = "nothing";

    // ── Delete propagation ────────────────────────────────────────────────────
    /// <summary>
    /// When true, SyncAgent reads from a delete-log table and issues DELETE statements
    /// against the PostgreSQL target. Requires a SQLite trigger on the source table.
    /// See syncagent.example.json for the expected delete-log table schema and trigger.
    /// </summary>
    public bool     SyncDeletes      { get; set; } = false;

    /// <summary>
    /// Name of the SQLite delete-log table. Defaults to "{SourceTable}_deletes".
    /// </summary>
    public string   DeleteLogTable   { get; set; } = "";

    /// <summary>Returns the effective delete-log table name.</summary>
    public string GetEffectiveDeleteLogTable() =>
        string.IsNullOrWhiteSpace(DeleteLogTable) ? $"{SourceTable}_deletes" : DeleteLogTable;
}

public sealed class SyncConfig
{
    // ── Core paths ────────────────────────────────────────────────────────────
    public string         SQLitePath      { get; set; } = "./station.db";
    public string         PostgresConnStr { get; set; } = "";

    // ── Station identity ──────────────────────────────────────────────────────
    public string         StationId       { get; set; } = "";
    public string         SiteName        { get; set; } = "";

    // ── Sync behaviour ────────────────────────────────────────────────────────
    public int            IntervalSeconds       { get; set; } = 30;
    public int            BatchSize             { get; set; } = 100;
    public int            MaxRetries            { get; set; } = 10;
    public string         HealthFilePath        { get; set; } = "./sync-health.json";

    /// <summary>
    /// Timeout in seconds for individual PostgreSQL commands (INSERT/DELETE statements).
    /// Default 30 s. Set to 0 for no timeout (not recommended on production stations).
    /// </summary>
    public int            CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Delete synced rows older than this many days at the start of each cycle.
    /// Set to 0 (default) to disable pruning entirely.
    /// </summary>
    public int            PruneAfterDays        { get; set; } = 0;

    // ── Logging ───────────────────────────────────────────────────────────────
    public string         LogPath         { get; set; } = "./logs";
    public string         LogMinLevel     { get; set; } = "Information";

    /// <summary>
    /// Number of daily log files to retain. Default 30. Increase for compliance deployments.
    /// </summary>
    public int            RetentionDays   { get; set; } = 30;

    // ── Alerting ──────────────────────────────────────────────────────────────
    /// <summary>
    /// Optional webhook URL. When a record reaches dead-letter state, SyncAgent POSTs
    /// a JSON payload to this URL. Leave empty to disable.
    /// </summary>
    public string?        AlertWebhookUrl { get; set; }

    // ── Health endpoint ───────────────────────────────────────────────────────
    /// <summary>
    /// Port for the HTTP health endpoint (GET /health returns the health JSON).
    /// Default 0 = disabled. When enabled, listens on http://localhost:{port}/health/.
    /// On Windows, no elevation is needed for localhost bindings.
    /// </summary>
    public int            HealthEndpointPort { get; set; } = 0;

    // ── Runtime flags (not persisted to JSON) ─────────────────────────────────
    /// <summary>When true, SyncAgent reads and logs pending records but writes nothing.</summary>
    public bool           IsDryRun        { get; set; } = false;

    // ── Table mappings ────────────────────────────────────────────────────────
    public List<TableMap> Tables          { get; set; } = [];
}
