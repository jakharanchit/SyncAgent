using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SyncAgent.Config;
using SyncAgent.Data.Models;
using SyncAgent.Health;
using Xunit;

namespace SyncAgent.Tests.Health;

public sealed class HealthReporterTests : IDisposable
{
    private readonly string _dir  = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string _file;
    private readonly HealthReporter _reporter;

    public HealthReporterTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "sync-health.json");

        var config = new SyncConfig
        {
            StationId      = "TEST-01",
            HealthFilePath = _file
        };
        _reporter = new HealthReporter(config, NullLogger<HealthReporter>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── WriteAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_FileExists_WithExpectedFields()
    {
        var result = new CycleResult
        {
            Synced            = 5,
            StillPending      = 3,
            DeadLetterCount   = 1,
            PostgresReachable = true,
            Deferred          = 0,
            LastSyncedAt      = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc),
            TableStats        = [new TableSyncStats { TableName = "orders", Pending = 3, DeadLetter = 1 }]
        };

        await _reporter.WriteAsync(result, syncedTotal: 100, cycleDurationMs: 250, CancellationToken.None);

        File.Exists(_file).Should().BeTrue();

        using var doc  = JsonDocument.Parse(await File.ReadAllTextAsync(_file));
        var root = doc.RootElement;

        root.GetProperty("stationId").GetString().Should().Be("TEST-01");
        root.GetProperty("pendingCount").GetInt32().Should().Be(3);
        root.GetProperty("deadLetterCount").GetInt32().Should().Be(1);
        root.GetProperty("postgresReachable").GetBoolean().Should().BeTrue();
        root.GetProperty("syncedTotal").GetInt64().Should().Be(100);
        root.GetProperty("lastCycleDurationMs").GetInt64().Should().Be(250);
        root.GetProperty("agentVersion").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("lastCycleAt").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("lastSyncedAt").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("infraDeferredCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task WriteAsync_TablesArray_ContainsPerTableStats()
    {
        var result = new CycleResult
        {
            TableStats = [
                new TableSyncStats { TableName = "orders", Pending = 2, DeadLetter = 0 },
                new TableSyncStats { TableName = "items",  Pending = 1, DeadLetter = 1 }
            ]
        };

        await _reporter.WriteAsync(result, 0, 0, CancellationToken.None);

        using var doc  = JsonDocument.Parse(await File.ReadAllTextAsync(_file));
        var tables = doc.RootElement.GetProperty("tables");
        tables.GetArrayLength().Should().Be(2);

        var first = tables[0];
        first.GetProperty("name").GetString().Should().Be("orders");
        first.GetProperty("pending").GetInt32().Should().Be(2);
        first.GetProperty("deadLetter").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task WriteAsync_Atomic_NoTmpFileLeftBehind()
    {
        await _reporter.WriteAsync(CycleResult.Empty(), 0, 0, CancellationToken.None);

        File.Exists(_file + ".tmp").Should().BeFalse("tmp file must be renamed atomically");
        File.Exists(_file).Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_NoTableStats_TablesPropertyIsNull()
    {
        var result = new CycleResult { TableStats = [] };

        await _reporter.WriteAsync(result, 0, 0, CancellationToken.None);

        using var doc  = JsonDocument.Parse(await File.ReadAllTextAsync(_file));
        doc.RootElement.TryGetProperty("tables", out var tables).Should().BeFalse(
            "tables should be omitted (null ignored) when empty");
    }

    // ── WriteErrorAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task WriteErrorAsync_FileContainsPendingNegativeOne()
    {
        await _reporter.WriteErrorAsync(new Exception("something blew up"), CancellationToken.None);

        File.Exists(_file).Should().BeTrue();

        using var doc  = JsonDocument.Parse(await File.ReadAllTextAsync(_file));
        var root = doc.RootElement;

        root.GetProperty("pendingCount").GetInt32().Should().Be(-1);
        root.GetProperty("postgresReachable").GetBoolean().Should().BeFalse();
        root.GetProperty("error").GetString().Should().Be("something blew up");
    }

    [Fact]
    public async Task WriteErrorAsync_Atomic_NoTmpFile()
    {
        await _reporter.WriteErrorAsync(new Exception("boom"), CancellationToken.None);

        File.Exists(_file + ".tmp").Should().BeFalse();
    }

    // ── Directory creation ────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_DirectoryDoesNotExist_CreatesDirectory()
    {
        var subdir = Path.Combine(_dir, "new_subdir");
        var config = new SyncConfig
        {
            StationId      = "X",
            HealthFilePath = Path.Combine(subdir, "health.json")
        };
        var reporter = new HealthReporter(config, NullLogger<HealthReporter>.Instance);

        await reporter.WriteAsync(CycleResult.Empty(), 0, 0, CancellationToken.None);

        Directory.Exists(subdir).Should().BeTrue();
        File.Exists(Path.Combine(subdir, "health.json")).Should().BeTrue();
    }
}
