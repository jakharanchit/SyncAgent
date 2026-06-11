namespace SyncAgent.Data.Models;

/// <summary>Lightweight row from sync_status — what GetPendingAsync returns.</summary>
public sealed record PendingRecord(string RecordId, string TableName, int RetryCount);

/// <summary>
/// A pending row from a delete-log table — signals that a record was deleted in SQLite
/// and the corresponding PostgreSQL row should also be deleted.
/// </summary>
public sealed record PendingDelete(
    string RecordId,
    string SourceTable,
    string TargetTable,
    string PrimaryKey,
    string DeleteLogTable);

/// <summary>One record from any SQLite table, hydrated as a column-value dictionary.</summary>
public sealed class GenericRecord
{
    public string                    RecordId    { get; init; } = "";
    public string                    SourceTable { get; init; } = "";
    public string                    TargetTable { get; init; } = "";
    /// <summary>Single PK column name (legacy). For composite PKs see PrimaryKeys.</summary>
    public string                    PrimaryKey  { get; init; } = "";
    /// <summary>All effective PK column names (length ≥ 1).</summary>
    public string[]                  PrimaryKeys { get; init; } = [];
    public Dictionary<string, object?> Columns   { get; init; } = new();
}
