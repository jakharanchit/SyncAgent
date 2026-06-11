using SyncAgent.Data.Models;

namespace SyncAgent.Health;

public interface IHealthReporter
{
    Task WriteAsync(CycleResult result, long syncedTotal, long cycleDurationMs, CancellationToken ct);
    Task WriteErrorAsync(Exception ex, CancellationToken ct);
}
