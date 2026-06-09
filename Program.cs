using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SyncAgent;
using SyncAgent.Config;
using SyncAgent.Data;
using SyncAgent.Health;
using SyncAgent.Sync;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var host = Host.CreateDefaultBuilder(args)
        // ── Single config file ───────────────────────────────────────────────
        // All configuration lives in syncagent.json.
        // syncagent.local.json (gitignored) overrides for local development.
        // SYNCAGENT_ env vars override everything (useful for Docker / CI).
        .ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.Sources.Clear();
            cfg.AddJsonFile("syncagent.json",       optional: false, reloadOnChange: false);
            cfg.AddJsonFile("syncagent.local.json",  optional: true,  reloadOnChange: false);
            cfg.AddEnvironmentVariables(prefix: "SYNCAGENT_");
        })
        // ── Logging ──────────────────────────────────────────────────────────
        .UseSerilog((ctx, _, logConfig) =>
        {
            var logPath  = ctx.Configuration["Logging:LogPath"]  ?? "./logs";
            var minLevel = Enum.TryParse<LogEventLevel>(
                ctx.Configuration["Logging:MinLevel"], ignoreCase: true, out var lvl)
                ? lvl : LogEventLevel.Information;

            logConfig
                .MinimumLevel.Is(minLevel)
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System",    LogEventLevel.Warning)
                .WriteTo.Console(outputTemplate:
                    "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(
                    Path.Combine(logPath, "syncagent-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        })
        // ── Services ─────────────────────────────────────────────────────────
        .ConfigureServices((ctx, services) =>
        {
            var cfg = ctx.Configuration;

            var config = new SyncConfig
            {
                SQLitePath      = cfg["Sync:SQLitePath"]       ?? "./station.db",
                IntervalSeconds = int.TryParse(cfg["Sync:IntervalSeconds"], out var iv) ? iv : 30,
                BatchSize       = int.TryParse(cfg["Sync:BatchSize"],       out var bs) ? bs : 100,
                MaxRetries      = int.TryParse(cfg["Sync:MaxRetries"],      out var mr) ? mr : 10,
                HealthFilePath  = cfg["Sync:HealthFilePath"]   ?? "./sync-health.json",
                PostgresConnStr = cfg["Postgres:ConnectionString"] ?? "",
                StationId       = cfg["Station:StationId"]     ?? "",
                SiteName        = cfg["Station:SiteName"]      ?? "",
                LogPath         = cfg["Logging:LogPath"]        ?? "./logs",
                LogMinLevel     = cfg["Logging:MinLevel"]       ?? "Information",
                Tables          = cfg.GetSection("Tables").Get<List<TableMap>>() ?? []
            };

            services.AddSingleton(config);
            services.AddSingleton<SQLiteReader>();
            services.AddSingleton<PostgresWriter>();
            services.AddSingleton<RetryPolicy>();
            services.AddSingleton<SyncOrchestrator>();
            services.AddSingleton<HealthReporter>();
            services.AddHostedService<Worker>();
        })
        // ── Host lifetime ─────────────────────────────────────────────────────
        // Same binary: Windows Service on Windows, systemd on Linux.
        .UseWindowsService(o => o.ServiceName = "SyncAgent")
        .UseSystemd()
        .Build();

    Log.Information("SyncAgent host built. Starting...");
    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "SyncAgent terminated unexpectedly.");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
