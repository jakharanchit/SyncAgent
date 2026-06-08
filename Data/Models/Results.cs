namespace SyncAgent.Data.Models;

public sealed class WriteResult
{
    public List<string>       SucceededIds { get; init; } = [];
    public List<FailedRecord> Failures     { get; init; } = [];

    public static WriteResult Success(List<string> ids) =>
        new() { SucceededIds = ids };

    public static WriteResult AllFailed(List<GenericRecord> records, Exception ex) =>
        new()
        {
            Failures = records
                .Select(r => new FailedRecord(r.RecordId, r.SourceTable, 0, ex))
                .ToList()
        };
}

public sealed record FailedRecord(string RecordId, string TableName, int RetryCount, Exception Exception);

public sealed class CycleResult
{
    public int       Synced            { get; init; }
    public int       StillPending      { get; init; }
    public int       Failed            { get; init; }
    public int       DeadLetterCount   { get; init; }
    public bool      PostgresReachable { get; init; }
    public DateTime? LastSyncedAt      { get; init; }

    public static CycleResult Empty() => new() { PostgresReachable = true };
}
