using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SyncAgent.Config;
using SyncAgent.Data.Models;
using SyncAgent.Health;
using SyncAgent.Sync;
using Xunit;

namespace SyncAgent.Tests.Unit;

public sealed class WorkerTests
{
    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeOrchestrator : ISyncOrchestrator
    {
        private readonly Queue<CycleResult> _results;
        private readonly Action<int>?       _afterEachCall;

        public int        CallCount { get; private set; }
        public Exception? Throw     { get; init; }

        public FakeOrchestrator(IEnumerable<CycleResult>? results = null, Action<int>? afterEachCall = null)
        {
            _results       = new Queue<CycleResult>(results ?? [CycleResult.Empty()]);
            _afterEachCall = afterEachCall;
        }

        public Task VerifyStartupAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<CycleResult> RunCycleAsync(CancellationToken ct)
        {
            CallCount++;
            if (Throw is not null) throw Throw;
            var result = _results.Count > 0 ? _results.Dequeue() : CycleResult.Empty();
            _afterEachCall?.Invoke(CallCount);
            return Task.FromResult(result);
        }
    }

    private sealed class FakeHealthReporter : IHealthReporter
    {
        public record WriteCall(CycleResult Result, long SyncedTotal, long DurationMs);

        public List<WriteCall>  WriteAsyncCalls      { get; } = [];
        public List<Exception>  WriteErrorAsyncCalls { get; } = [];

        public Task WriteAsync(CycleResult result, long syncedTotal, long cycleDurationMs, CancellationToken ct)
        {
            WriteAsyncCalls.Add(new WriteCall(result, syncedTotal, cycleDurationMs));
            return Task.CompletedTask;
        }

        public Task WriteErrorAsync(Exception ex, CancellationToken ct)
        {
            WriteErrorAsyncCalls.Add(ex);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly TaskCompletionSource _tcs = new();
        public bool StopCalled { get; private set; }
        public Task StopSignal => _tcs.Task;

        public void StopApplication() { StopCalled = true; _tcs.TrySetResult(); }

        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SyncAgent.Worker MakeWorker(
        ISyncOrchestrator orchestrator,
        IHealthReporter   health,
        SyncConfig        config,
        FakeLifetime      lifetime) =>
        new(orchestrator, health, config, lifetime, NullLogger<SyncAgent.Worker>.Instance);

    // Runs until Worker calls StopApplication() (dry-run path).
    private static async Task RunUntilStopped(SyncAgent.Worker worker, FakeLifetime lifetime)
    {
        await worker.StartAsync(CancellationToken.None);
        await lifetime.StopSignal.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SuccessfulCycle_CallsWriteAsync()
    {
        var result       = new CycleResult { Synced = 3, PostgresReachable = true, TableStats = [] };
        var orchestrator = new FakeOrchestrator(results: [result]);
        var health       = new FakeHealthReporter();
        var lifetime     = new FakeLifetime();
        var worker       = MakeWorker(orchestrator, health, new SyncConfig { IsDryRun = true }, lifetime);

        await RunUntilStopped(worker, lifetime);

        health.WriteAsyncCalls.Should().HaveCount(1);
        health.WriteAsyncCalls[0].Result.Synced.Should().Be(3);
        health.WriteErrorAsyncCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_CycleThrows_CallsWriteErrorAsync()
    {
        var ex           = new InvalidOperationException("db broken");
        var orchestrator = new FakeOrchestrator() { Throw = ex };
        var health       = new FakeHealthReporter();
        var lifetime     = new FakeLifetime();
        var worker       = MakeWorker(orchestrator, health, new SyncConfig { IsDryRun = true }, lifetime);

        await RunUntilStopped(worker, lifetime);

        health.WriteErrorAsyncCalls.Should().HaveCount(1);
        health.WriteErrorAsyncCalls[0].Message.Should().Be("db broken");
        health.WriteAsyncCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_IsDryRun_StopsAfterOneCycle()
    {
        var orchestrator = new FakeOrchestrator();
        var health       = new FakeHealthReporter();
        var lifetime     = new FakeLifetime();
        var worker       = MakeWorker(orchestrator, health, new SyncConfig { IsDryRun = true }, lifetime);

        await RunUntilStopped(worker, lifetime);

        lifetime.StopCalled.Should().BeTrue();
        orchestrator.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleCycles_SyncedTotalAccumulates()
    {
        var cts = new CancellationTokenSource();
        var results = new[]
        {
            new CycleResult { Synced = 5, TableStats = [] },
            new CycleResult { Synced = 7, TableStats = [] }
        };
        var orchestrator = new FakeOrchestrator(
            results:       results,
            afterEachCall: count => { if (count >= 2) cts.Cancel(); });
        var health   = new FakeHealthReporter();
        var lifetime = new FakeLifetime();
        var config   = new SyncConfig { IsDryRun = false, IntervalSeconds = 0 };
        var worker   = MakeWorker(orchestrator, health, config, lifetime);

        try
    {
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
    }
    catch (OperationCanceledException) { /* expected when token cancelled mid-delay */ }

        health.WriteAsyncCalls.Should().HaveCount(2);
        health.WriteAsyncCalls[0].SyncedTotal.Should().Be(5);
        health.WriteAsyncCalls[1].SyncedTotal.Should().Be(12);
    }
}
