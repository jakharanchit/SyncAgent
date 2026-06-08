using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SyncAgent.Config;
using SyncAgent.Data.Models;

namespace SyncAgent.Health;

public sealed class HealthReporter
{
    private readonly string _healthFilePath;
    private readonly string _stationId;
    private readonly ILogger<HealthReporter> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string AgentVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

    public HealthReporter(SyncConfig config, ILogger<HealthReporter> logger)
    {
        _healthFilePath = config.HealthFilePath;
        _stationId      = config.StationId;
        _logger         = logger;
    }

    public async Task WriteAsync(CycleResult result, CancellationToken ct)
    {
        var report = new
        {
            stationId          = _stationId,
            lastCycleAt        = DateTime.UtcNow.ToString("O"),
            lastSyncedAt       = result.LastSyncedAt?.ToString("O"),
            pendingCount       = result.StillPending,
            deadLetterCount    = result.DeadLetterCount,
            postgresReachable  = result.PostgresReachable,
            agentVersion       = AgentVersion
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

    // Write to .tmp then rename — LabVIEW never reads a partial file
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
