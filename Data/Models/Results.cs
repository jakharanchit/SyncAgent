namespace SyncAgent.Data.Models;

/// <summary>
/// Classifies WHY a record failed — determines whether retry_count is incremented.
/// </summary>
public enum FailureKind
{
    /// <summary>
    /// PostgreSQL was unreachable or the connection dropped mid-batch.
    /// retry_count must NOT be incremented — the record is fine; the transport is broken.
    /// </summary>
    Infrastructure,

    /// <summary>
    /// PostgreSQL received the record but rejected it (type error, constraint violation, etc.).
    /// retry_count IS incremented; the record itself needs investigation.
    /// </summary>
    Data
}

public sealed class WriteResult
{
    /// <summary>Tuples of (RecordId, TableName) for successfully written records.</summary>
    public List<(string RecordId, string TableName)> Succeeded { get; init; } = [];
    public List<FailedRecord>                        Failures  { get; init; } = [];

    public static WriteResult Success(List<(string RecordId, string TableName)> records) =>
        new() { Succeeded = records };

    /// <summary>
    /// Connection could not be opened — all records deferred, retry_count untouched.
    /// </summary>
    public static WriteResult InfrastructureFailed(List<GenericRecord> records, Exception ex) =>
        new()
        {
            Failures = records
                .Select(r => new FailedRecord(r.RecordId, r.SourceTable, 0, ex, FailureKind.Infrastructure))
                .ToList()
        };

    /// <summary>
    /// A record-level error — callers should increment retry_count on affected records.
    /// </summary>
    public static WriteResult DataFailed(List<GenericRecord> records, Exception ex) =>
        new()
        {
            Failures = records
                .Select(r => new FailedRecord(r.RecordId, r.SourceTable, 0, ex, FailureKind.Data))
                .ToList()
        };
}

public sealed record FailedRecord(
    string      RecordId,
    string      TableName,
    int         RetryCount,
    Exception   Exception,
    FailureKind Kind = FailureKind.Infrastructure);

public sealed class TableSyncStats
{
    public string TableName   { get; init; } = "";
    public int    Pending     { get; init; }
    public int    DeadLetter  { get; init; }
}

public sealed class CycleResult
{
    public int                    Synced              { get; init; }
    public int                    Deleted             { get; init; }
    public int                    StillPending        { get; init; }
    public int                    Failed              { get; init; }
    /// <summary>Records deferred this cycle due to infrastructure failures (retry_count NOT touched).</summary>
    public int                    Deferred            { get; init; }
    public int                    DeadLetterCount     { get; init; }
    public bool                   PostgresReachable   { get; init; }
    public DateTime?              LastSyncedAt        { get; init; }
    public DateTime?              LastInfraErrorAt    { get; init; }
    public long                   CycleDurationMs     { get; init; }
    public List<TableSyncStats>   TableStats          { get; init; } = [];

    public static CycleResult Empty() => new() { PostgresReachable = true };
}
