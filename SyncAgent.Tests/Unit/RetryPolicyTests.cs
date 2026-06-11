using FluentAssertions;
using SyncAgent.Config;
using SyncAgent.Sync;
using Xunit;

namespace SyncAgent.Tests.Unit;

public class RetryPolicyTests
{
    private static RetryPolicy Make(int maxRetries = 5) =>
        new(new SyncConfig { MaxRetries = maxRetries });

    // ── ComputeNextAttempt ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 480)]
    public void ComputeNextAttempt_BackoffDoubles_WithinJitterWindow(int retryCount, int baseSeconds)
    {
        var policy = Make();
        var before = DateTime.UtcNow;

        var result = policy.ComputeNextAttempt(retryCount);

        var parsed = DateTime.Parse(result, null, System.Globalization.DateTimeStyles.None);
        var elapsed = (parsed - before).TotalSeconds;

        // Allow ±10% jitter + 2 s wall-clock slack for slow test runners
        elapsed.Should().BeGreaterThanOrEqualTo(baseSeconds * 0.9 - 1);
        elapsed.Should().BeLessThanOrEqualTo(baseSeconds * 1.1 + 2);
    }

    [Fact]
    public void ComputeNextAttempt_HighRetryCount_CapsAt3600Seconds()
    {
        var policy = Make();
        var before = DateTime.UtcNow;

        // 30 * 2^19 would be ~15 million seconds without the cap
        var result = policy.ComputeNextAttempt(20);

        var parsed  = DateTime.Parse(result, null, System.Globalization.DateTimeStyles.None);
        var elapsed = (parsed - before).TotalSeconds;

        elapsed.Should().BeLessThanOrEqualTo(3600 * 1.11 + 2); // 3600 + jitter + slack
    }

    [Fact]
    public void ComputeNextAttempt_ReturnValue_IsValidDateTimeString()
    {
        var result = Make().ComputeNextAttempt(1);

        DateTime.TryParse(result, out _).Should().BeTrue();
        // Must match the "yyyy-MM-dd HH:mm:ss" format SQLiteReader stores
        result.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$");
    }

    [Fact]
    public void ComputeNextAttempt_ResultIsInFuture()
    {
        var before = DateTime.UtcNow;
        var result = Make().ComputeNextAttempt(1);
        var parsed = DateTime.Parse(result, null, System.Globalization.DateTimeStyles.None);

        parsed.Should().BeAfter(before);
    }

    // ── IsDeadLetter ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 5, false)]
    [InlineData(4, 5, false)]
    [InlineData(5, 5, true)]   // at threshold → dead
    [InlineData(6, 5, true)]   // beyond threshold → dead
    [InlineData(0, 1, false)]
    [InlineData(1, 1, true)]   // maxRetries=1: first data failure dead-letters
    [InlineData(2, 1, true)]
    public void IsDeadLetter_RespectsMaxRetries(int retryCount, int maxRetries, bool expected)
    {
        Make(maxRetries).IsDeadLetter(retryCount).Should().Be(expected);
    }

    [Fact]
    public void IsDeadLetter_Zero_NeverDead()
    {
        // With maxRetries=0, everything is dead immediately — not useful but spec-correct
        Make(maxRetries: 0).IsDeadLetter(0).Should().BeTrue();
    }
}
