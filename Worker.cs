using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncAgent.Config;
using SyncAgent.Health;
using SyncAgent.Sync;

namespace SyncAgent;

public sealed class Worker : BackgroundService
{
    private readonly SyncOrchestrator  _orchestrator;
    private readonly HealthReporter    _health;
    private readonly SyncConfig        _config;
    private readonly ILogger<Worker>   _logger;

    public Worker(
        SyncOrchestrator orchestrator,
        HealthReporter health,
        SyncConfig config,
        ILogger<Worker> logger)
    {
        _orchestrator = orchestrator;
        _health       = health;
        _config       = config;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "SyncAgent starting. StationId={StationId} SiteName={SiteName} Interval={Interval}s",
            _config.StationId, _config.SiteName, _config.IntervalSeconds);

        await _orchestrator.VerifyStartupAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _orchestrator.RunCycleAsync(ct);

                if (result.Synced > 0 || result.Failed > 0)
                    _logger.LogInformation(
                        "Cycle complete. Synced={Synced} Pending={Pending} Failed={Failed} DeadLetter={Dead}",
                        result.Synced, result.StillPending, result.Failed, result.DeadLetterCount);

                await _health.WriteAsync(result, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Sync cycle error");
                await _health.WriteErrorAsync(ex, ct);
            }

            await Task.Delay(TimeSpan.FromSeconds(_config.IntervalSeconds), ct);
        }

        _logger.LogInformation("SyncAgent stopped cleanly.");
    }
}
