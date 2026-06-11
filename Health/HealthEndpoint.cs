using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncAgent.Config;

namespace SyncAgent.Health;

/// <summary>
/// Optional HTTP health endpoint. When Sync.HealthEndpointPort is non-zero, serves the
/// health JSON on GET http://localhost:{port}/health/ so Prometheus, Datadog, and
/// load-balancer health checks can query SyncAgent directly without polling the file.
///
/// On Windows no elevation is needed for localhost bindings.
/// On Linux, ports below 1024 require elevated privileges — use a port ≥ 1024.
/// </summary>
public sealed class HealthEndpoint : BackgroundService
{
    private readonly SyncConfig              _config;
    private readonly ILogger<HealthEndpoint> _logger;

    public HealthEndpoint(SyncConfig config, ILogger<HealthEndpoint> logger)
    {
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_config.HealthEndpointPort <= 0)
            return;

        var prefix   = $"http://localhost:{_config.HealthEndpointPort}/health/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to start health endpoint on {Prefix}. " +
                "On Windows, 'localhost' bindings require no elevation. " +
                "On Linux, ports below 1024 require sudo.", prefix);
            return;
        }

        _logger.LogInformation("Health endpoint listening on {Prefix}", prefix);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // Fire-and-forget per request — don't await, so we can accept the next request immediately
                _ = HandleRequestAsync(ctx, ct);
            }
        }
        finally
        {
            listener.Stop();
            _logger.LogDebug("Health endpoint stopped.");
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            if (ctx.Request.HttpMethod != "GET")
            {
                ctx.Response.StatusCode = 405; // Method Not Allowed
                ctx.Response.Close();
                return;
            }

            string json;
            if (File.Exists(_config.HealthFilePath))
            {
                json = await File.ReadAllTextAsync(_config.HealthFilePath, ct);
                ctx.Response.StatusCode = 200;
            }
            else
            {
                json = """{"status":"starting","message":"Health file not yet written"}""";
                ctx.Response.StatusCode = 503; // Service Unavailable
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType     = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Health endpoint request error");
        }
        finally
        {
            ctx.Response.Close();
        }
    }
}
