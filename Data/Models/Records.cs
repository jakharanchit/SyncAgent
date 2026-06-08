namespace SyncAgent.Data.Models;

// Lightweight row from sync_status — what GetPendingAsync returns
public sealed record PendingRecord(string RecordId, string TableName, int RetryCount);

// One record from any SQLite table, hydrated as a column-value dictionary
public sealed class GenericRecord
{
    public string                    RecordId    { get; init; } = "";
    public string                    SourceTable { get; init; } = "";
    public string                    TargetTable { get; init; } = "";
    public string                    PrimaryKey  { get; init; } = "";
    public Dictionary<string, object?> Columns   { get; init; } = new();
}
