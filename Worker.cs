using System.Diagnostics;
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
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<Worker>   _logger;

    // Throughput counter — cumulative since service start
    private long _syncedTotal;

    public Worker(
        SyncOrchestrator orchestrator,
        HealthReporter health,
        SyncConfig config,
        IHostApplicationLifetime lifetime,
        ILogger<Worker> logger)
    {
        _orchestrator = orchestrator;
        _health       = health;
        _config       = config;
        _lifetime     = lifetime;
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
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await _orchestrator.RunCycleAsync(ct);
                sw.Stop();

                _syncedTotal += result.Synced;

                if (result.Synced > 0 || result.Deleted > 0 || result.Failed > 0 || result.Deferred > 0)
                    _logger.LogInformation(
                        "Cycle complete. Synced={Synced} Deleted={Deleted} Pending={Pending} " +
                        "Failed={Failed} Deferred={Deferred} DeadLetter={Dead} Duration={Ms}ms",
                        result.Synced, result.Deleted, result.StillPending,
                        result.Failed, result.Deferred, result.DeadLetterCount, sw.ElapsedMilliseconds);

                await _health.WriteAsync(result, _syncedTotal, sw.ElapsedMilliseconds, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sw.Stop();
                _logger.LogError(ex, "Sync cycle error");
                await _health.WriteErrorAsync(ex, ct);
            }

            // In dry-run mode, run exactly one cycle then exit cleanly
            if (_config.IsDryRun)
            {
                _logger.LogInformation("Dry run complete. Stopping.");
                _lifetime.StopApplication();
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(_config.IntervalSeconds), ct);
        }

        _logger.LogInformation("SyncAgent stopped cleanly.");
    }
}
