using SyncAgent.Data.Models;

namespace SyncAgent.Sync;

public interface ISyncOrchestrator
{
    Task VerifyStartupAsync(CancellationToken ct);
    Task<CycleResult> RunCycleAsync(CancellationToken ct);
}
