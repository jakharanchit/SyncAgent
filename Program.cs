using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SyncAgent;
using SyncAgent.Cli;
using SyncAgent.Config;
using SyncAgent.Data;
using SyncAgent.Health;
using SyncAgent.Sync;
using Serilog;
using Serilog.Events;

// ── Admin CLI flags — run once and exit, no host required ─────────────────────

if (args.Contains("--version"))
{
    var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
    Console.WriteLine($"SyncAgent {version}");
    return 0;
}

if (args.Contains("--status"))
    return await CliRunner.StatusAsync(args);

if (args.Contains("--reset-dead-letters"))
    return await CliRunner.ResetDeadLettersAsync(args);

// ── Bootstrap logger (before config is loaded) ────────────────────────────────

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

bool dryRun = args.Contains("--dry-run");
if (dryRun)
    Log.Information("--dry-run flag detected. SyncAgent will read and log pending records but write nothing.");

try
{
    var host = Host.CreateDefaultBuilder(args)
        // ── Config sources ────────────────────────────────────────────────────
        // All config lives in syncagent.json.
        // syncagent.local.json (gitignored) overrides for local development.
        // SYNCAGENT_ env vars override everything (Docker / CI / secrets).
        .ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.Sources.Clear();
            cfg.AddJsonFile("syncagent.json",       optional: false, reloadOnChange: false);
            cfg.AddJsonFile("syncagent.local.json",  optional: true,  reloadOnChange: false);
            cfg.AddEnvironmentVariables(prefix: "SYNCAGENT_");
        })
        // ── Logging ───────────────────────────────────────────────────────────
        .UseSerilog((ctx, _, logConfig) =>
        {
            var logPath   = ctx.Configuration["Logging:LogPath"]       ?? "./logs";
            var retention = ParseInt(ctx.Configuration["Logging:RetentionDays"], 30);
            var minLevel  = Enum.TryParse<LogEventLevel>(
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
                    rollingInterval:        RollingInterval.Day,
                    retainedFileCountLimit: retention,
                    outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        })
        // ── Services ──────────────────────────────────────────────────────────
        .ConfigureServices((ctx, services) =>
        {
            var cfg = ctx.Configuration;

            var config = new SyncConfig
            {
                SQLitePath            = cfg["Sync:SQLitePath"]              ?? "./station.db",
                IntervalSeconds       = ParseInt(cfg["Sync:IntervalSeconds"],  30),
                BatchSize             = ParseInt(cfg["Sync:BatchSize"],        100),
                MaxRetries            = ParseInt(cfg["Sync:MaxRetries"],       10),
                HealthFilePath        = cfg["Sync:HealthFilePath"]           ?? "./sync-health.json",
                CommandTimeoutSeconds = ParseInt(cfg["Sync:CommandTimeoutSeconds"], 30),
                PruneAfterDays        = ParseInt(cfg["Sync:PruneAfterDays"],   0),
                HealthEndpointPort    = ParseInt(cfg["Sync:HealthEndpointPort"], 0),
                PostgresConnStr       = cfg["Postgres:ConnectionString"]     ?? "",
                StationId             = cfg["Station:StationId"]             ?? "",
                SiteName              = cfg["Station:SiteName"]              ?? "",
                LogPath               = cfg["Logging:LogPath"]               ?? "./logs",
                LogMinLevel           = cfg["Logging:MinLevel"]              ?? "Information",
                RetentionDays         = ParseInt(cfg["Logging:RetentionDays"], 30),
                AlertWebhookUrl       = cfg["Alerts:WebhookUrl"],
                Tables                = cfg.GetSection("Tables").Get<List<TableMap>>() ?? [],
                IsDryRun              = dryRun
            };

            // Startup validation
            if (string.IsNullOrWhiteSpace(config.PostgresConnStr))
                throw new InvalidOperationException(
                    "Postgres:ConnectionString is required. Set it in syncagent.json or via " +
                    "the SYNCAGENT_Postgres__ConnectionString environment variable.");

            // ── #17: Secret warning ───────────────────────────────────────────
            // Warn if the connection string contains a plaintext password and the
            // env var override is not in use (suggesting the password is in the file).
            if (config.PostgresConnStr.Contains("Password=", StringComparison.OrdinalIgnoreCase) &&
                Environment.GetEnvironmentVariable("SYNCAGENT_Postgres__ConnectionString") is null)
            {
                Log.Warning(
                    "Connection string contains a plaintext password stored in syncagent.json. " +
                    "For production deployments, store the password in the " +
                    "SYNCAGENT_Postgres__ConnectionString environment variable instead, " +
                    "and remove Postgres:ConnectionString from syncagent.json.");
            }

            services.AddSingleton(config);
            services.AddSingleton<SQLiteReader>();
            services.AddSingleton<PostgresWriter>();
            services.AddSingleton<RetryPolicy>();
            services.AddSingleton<ISyncOrchestrator, SyncOrchestrator>();
            services.AddSingleton<IHealthReporter, HealthReporter>();
            services.AddHostedService<Worker>();

            // ── #14: HTTP health endpoint (enabled when port > 0) ─────────────
            if (config.HealthEndpointPort > 0)
                services.AddHostedService<HealthEndpoint>();
        })
        // ── Host lifetime ─────────────────────────────────────────────────────
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

// ── Helpers ───────────────────────────────────────────────────────────────────

static int ParseInt(string? value, int fallback) =>
    int.TryParse(value, out var parsed) ? parsed : fallback;
