using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using SyncAgent.Cli;
using SyncAgent.Tests.Fixtures;
using Xunit;

namespace SyncAgent.Tests.Cli;

/// <summary>
/// Tests for CliRunner admin commands.
/// CliRunner reads syncagent.json from the current directory, so each test:
///   1. Creates a temp directory with a valid syncagent.json pointing to a fresh SQLite DB.
///   2. Changes the process CWD to that temp directory.
///   3. Runs the command and asserts the result.
///   4. Restores the original CWD in a finally block.
///
/// Tests in this collection run sequentially to avoid CWD races between test classes.
/// </summary>
[Collection("CLI")]
public sealed class CliRunnerTests : IDisposable
{
    private readonly string       _dir;
    private readonly SqliteFixture _db = new();
    private readonly string       _originalDir = Directory.GetCurrentDirectory();

    public CliRunnerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        // Always restore CWD — even if a test fails mid-way
        try { Directory.SetCurrentDirectory(_originalDir); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
        _db.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void WriteConfig()
    {
        var json = JsonSerializer.Serialize(new
        {
            Sync = new { SQLitePath = _db.DbPath },
            Tables = Array.Empty<object>()
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(_dir, "syncagent.json"), json);
    }

    private void EnterTempDir() => Directory.SetCurrentDirectory(_dir);

    // ── StatusAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_EmptyDatabase_ExitsZeroWithEmptyMessage()
    {
        WriteConfig();
        EnterTempDir();

        var exitCode = await CliRunner.StatusAsync([]);

        exitCode.Should().Be(0);
    }

    [Fact]
    public async Task Status_WithRecords_ExitsZeroAndPrintsTable()
    {
        _db.InsertPending("ord-1", "orders", synced: 0);
        _db.InsertPending("ord-2", "orders", synced: 2);

        WriteConfig();
        EnterTempDir();

        // Capture stdout
        var original = Console.Out;
        await using var writer = new System.IO.StringWriter();
        Console.SetOut(writer);
        try
        {
            var exitCode = await CliRunner.StatusAsync([]);
            exitCode.Should().Be(0);

            var output = writer.ToString();
            output.Should().Contain("orders");
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    // ── ResetDeadLettersAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task ResetDeadLetters_All_ResetsDeadLettersAndExitsZero()
    {
        _db.InsertPending("d1", "orders", synced: 2, retryCount: 5);
        _db.InsertPending("d2", "items",  synced: 2, retryCount: 3);
        _db.InsertPending("p1", "orders", synced: 0);

        WriteConfig();
        EnterTempDir();

        var exitCode = await CliRunner.ResetDeadLettersAsync([]);

        exitCode.Should().Be(0);
        _db.ReadStatus("d1", "orders").Synced.Should().Be(0);
        _db.ReadStatus("d1", "orders").RetryCount.Should().Be(0);
        _db.ReadStatus("d2", "items").Synced.Should().Be(0);
        _db.ReadStatus("p1", "orders").Synced.Should().Be(0); // untouched
    }

    [Fact]
    public async Task ResetDeadLetters_WithTableFlag_OnlyResetsSpecifiedTable()
    {
        _db.InsertPending("d1", "orders", synced: 2);
        _db.InsertPending("d2", "items",  synced: 2);

        WriteConfig();
        EnterTempDir();

        var exitCode = await CliRunner.ResetDeadLettersAsync(["--table=orders"]);

        exitCode.Should().Be(0);
        _db.ReadStatus("d1", "orders").Synced.Should().Be(0);
        _db.ReadStatus("d2", "items").Synced.Should().Be(2); // untouched
    }
}

/// <summary>Ensures CLI tests run sequentially to avoid CWD mutations interfering.</summary>
[CollectionDefinition("CLI", DisableParallelization = true)]
public class CliCollection { }
