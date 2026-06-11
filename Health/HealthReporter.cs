using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SyncAgent.Config;
using SyncAgent.Data.Models;

namespace SyncAgent.Health;

public sealed class HealthReporter : IHealthReporter
{
    private readonly string _healthFilePath;
    private readonly string _stationId;
    private readonly ILogger<HealthReporter> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly string AgentVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

    public HealthReporter(SyncConfig config, ILogger<HealthReporter> logger)
    {
        _healthFilePath = config.HealthFilePath;
        _stationId      = config.StationId;
        _logger         = logger;
    }

    /// <param name="result">Cycle outcome.</param>
    /// <param name="syncedTotal">Cumulative records synced since service start.</param>
    /// <param name="cycleDurationMs">Wall-clock duration of the last cycle in milliseconds.</param>
    public async Task WriteAsync(
        CycleResult result, long syncedTotal, long cycleDurationMs, CancellationToken ct)
    {
        var report = new
        {
            stationId           = _stationId,
            lastCycleAt         = DateTime.UtcNow.ToString("O"),
            lastSyncedAt        = result.LastSyncedAt?.ToString("O"),
            pendingCount        = result.StillPending,
            deadLetterCount     = result.DeadLetterCount,
            postgresReachable   = result.PostgresReachable,
            infraDeferredCount  = result.Deferred,
            lastInfraErrorAt    = result.LastInfraErrorAt?.ToString("O"),
            syncedTotal         = syncedTotal,
            lastCycleDurationMs = cycleDurationMs,
            agentVersion        = AgentVersion,
            tables              = result.TableStats.Count > 0
                ? result.TableStats.Select(t => new
                    {
                        name       = t.TableName,
                        pending    = t.Pending,
                        deadLetter = t.DeadLetter
                    }).ToArray()
                : null
        };

        await WriteAtomicAsync(JsonSerializer.Serialize(report, JsonOptions), ct);
    }

    public async Task WriteErrorAsync(Exception ex, CancellationToken ct)
    {
        var report = new
        {
            stationId         = _stationId,
            lastCycleAt       = DateTime.UtcNow.ToString("O"),
            lastSyncedAt      = (string?)null,
            pendingCount      = -1,
            deadLetterCount   = -1,
            postgresReachable = false,
            agentVersion      = AgentVersion,
            error             = ex.Message
        };

        await WriteAtomicAsync(JsonSerializer.Serialize(report, JsonOptions), ct);
    }

    // Write to .tmp then rename — readers never see a partial file
    private async Task WriteAtomicAsync(string json, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(_healthFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tmpPath = _healthFilePath + ".tmp";
            await File.WriteAllTextAsync(tmpPath, json, ct);
            File.Move(tmpPath, _healthFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write health file: {Path}", _healthFilePath);
        }
    }
}
