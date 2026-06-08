namespace SyncAgent.Config;

public sealed class TableMap
{
    public string   SourceTable      { get; set; } = "";
    public string   TargetTable      { get; set; } = "";
    public string   PrimaryKey       { get; set; } = "";
    public bool     InjectStationId  { get; set; } = true;
    public string[] TimestampColumns { get; set; } = [];
    public string[] BooleanColumns   { get; set; } = [];
}

public sealed class SyncConfig
{
    public string         SQLitePath      { get; set; } = "./station.db";
    public string         PostgresConnStr { get; set; } = "";
    public string         StationId       { get; set; } = "";
    public string         SiteName        { get; set; } = "";
    public int            IntervalSeconds { get; set; } = 30;
    public int            BatchSize       { get; set; } = 100;
    public int            MaxRetries      { get; set; } = 10;
    public int[]          BackoffSeconds  { get; set; } =
        [30, 60, 120, 300, 900, 3600, 3600, 3600, 3600, 3600];
    public string         HealthFilePath  { get; set; } = "./sync-health.json";
    public string         LogPath         { get; set; } = "./logs";
    public string         LogMinLevel     { get; set; } = "Information";
    public List<TableMap> Tables          { get; set; } = [];
}
