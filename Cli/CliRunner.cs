using Microsoft.Extensions.Configuration;
using SyncAgent.Config;
using SyncAgent.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace SyncAgent.Cli;

/// <summary>
/// Handles admin CLI commands that run once and exit without starting the background service.
/// Each method loads config from the standard config files and opens SQLite directly.
/// </summary>
public static class CliRunner
{
    // ── --status ──────────────────────────────────────────────────────────────

    public static async Task<int> StatusAsync(string[] args)
    {
        var (config, error) = LoadConfig();
        if (error is not null) { Console.Error.WriteLine(error); return 1; }

        var reader = new SQLiteReader(config!, NullLogger<SQLiteReader>.Instance);

        List<(string Table, int Pending, int DeadLetter, string? LastAttempt)> rows;
        try
        {
            rows = await reader.GetStatusSummaryAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error reading SQLite: {ex.Message}");
            return 1;
        }

        if (rows.Count == 0)
        {
            Console.WriteLine("sync_status is empty — no records have been registered yet.");
            return 0;
        }

        Console.WriteLine($"{"Table",-25} {"Pending",8} {"DeadLetter",12}  Last synced");
        Console.WriteLine(new string('-', 70));
        foreach (var (table, pending, dead, lastAttempt) in rows)
            Console.WriteLine(
                $"{table,-25} {pending,8} {dead,12}  {lastAttempt ?? "(never)"}");

        int totalPending = rows.Sum(r => r.Pending);
        int totalDead    = rows.Sum(r => r.DeadLetter);
        Console.WriteLine(new string('-', 70));
        Console.WriteLine($"{"TOTAL",-25} {totalPending,8} {totalDead,12}");

        return 0;
    }

    // ── --reset-dead-letters ──────────────────────────────────────────────────

    public static async Task<int> ResetDeadLettersAsync(string[] args)
    {
        var (config, error) = LoadConfig();
        if (error is not null) { Console.Error.WriteLine(error); return 1; }

        // Optional --table=<name> filter
        string? tableName = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--table=", StringComparison.OrdinalIgnoreCase))
            {
                tableName = arg["--table=".Length..];
                break;
            }
        }

        var reader = new SQLiteReader(config!, NullLogger<SQLiteReader>.Instance);

        int count;
        try
        {
            count = await reader.ResetDeadLettersAsync(tableName, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error resetting dead letters: {ex.Message}");
            return 1;
        }

        var scope = tableName is not null ? $" in table '{tableName}'" : "";
        Console.WriteLine($"Reset {count} dead-letter record(s){scope} to pending.");
        return 0;
    }

    // ── Config loader ─────────────────────────────────────────────────────────

    private static (SyncConfig? Config, string? Error) LoadConfig()
    {
        IConfiguration cfg;
        try
        {
            cfg = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("syncagent.json",      optional: false)
                .AddJsonFile("syncagent.local.json", optional: true)
                .AddEnvironmentVariables(prefix: "SYNCAGENT_")
                .Build();
        }
        catch (Exception ex)
        {
            return (null, $"Failed to load syncagent.json: {ex.Message}");
        }

        var config = new SyncConfig
        {
            SQLitePath = cfg["Sync:SQLitePath"] ?? "./station.db",
            Tables     = cfg.GetSection("Tables").Get<List<TableMap>>() ?? []
        };

        return (config, null);
    }
}
