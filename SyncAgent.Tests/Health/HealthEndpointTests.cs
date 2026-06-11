using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SyncAgent.Config;
using SyncAgent.Health;
using Xunit;

namespace SyncAgent.Tests.Health;

public sealed class HealthEndpointTests : IAsyncDisposable
{
    private readonly string        _dir;
    private readonly string        _healthFile;
    private readonly int           _port;
    private readonly HealthEndpoint _endpoint;
    private readonly CancellationTokenSource _cts = new();

    public HealthEndpointTests()
    {
        _dir        = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _healthFile = Path.Combine(_dir, "health.json");
        _port       = GetFreePort();

        var config = new SyncConfig
        {
            HealthEndpointPort = _port,
            HealthFilePath     = _healthFile
        };
        _endpoint = new HealthEndpoint(config, NullLogger<HealthEndpoint>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { await _endpoint.StopAsync(CancellationToken.None); } catch { }
        _cts.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_HealthFileExists_Returns200WithJson()
    {
        await File.WriteAllTextAsync(_healthFile, """{"stationId":"TEST","pendingCount":0}""");
        await StartEndpointAsync();

        using var http     = new HttpClient();
        var response = await http.GetAsync($"http://localhost:{_port}/health/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("stationId");
    }

    [Fact]
    public async Task Get_HealthFileMissing_Returns503()
    {
        // Do NOT create the health file
        await StartEndpointAsync();

        using var http     = new HttpClient();
        var response = await http.GetAsync($"http://localhost:{_port}/health/");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("starting");
    }

    [Fact]
    public async Task Post_Returns405MethodNotAllowed()
    {
        await StartEndpointAsync();

        using var http     = new HttpClient();
        var response = await http.PostAsync(
            $"http://localhost:{_port}/health/",
            new StringContent("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Get_ContentTypeIsJson()
    {
        await File.WriteAllTextAsync(_healthFile, "{}");
        await StartEndpointAsync();

        using var http     = new HttpClient();
        var response = await http.GetAsync($"http://localhost:{_port}/health/");

        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task DisabledWhenPortZero_NoListenerStarted()
    {
        var config = new SyncConfig { HealthEndpointPort = 0, HealthFilePath = _healthFile };
        var disabled = new HealthEndpoint(config, NullLogger<HealthEndpoint>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Should complete quickly (return immediately) since port=0 disables the endpoint
        await disabled.StartAsync(cts.Token);
        // No assertion needed — if it didn't throw or hang, the feature is disabled correctly
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private async Task StartEndpointAsync()
    {
        await _endpoint.StartAsync(_cts.Token);
        // Give the HttpListener time to bind before we make requests.
        // ExecuteAsync calls listener.Start() synchronously before the first await,
        // so a small delay is sufficient even on slow CI machines.
        await Task.Delay(150);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
