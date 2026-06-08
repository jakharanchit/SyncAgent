using SyncAgent.Config;

namespace SyncAgent.Sync;

public sealed class RetryPolicy
{
    private readonly SyncConfig _config;

    public RetryPolicy(SyncConfig config) => _config = config;

    // Returns an ISO8601 UTC datetime string for the next attempt, with ±10% jitter.
    // Jitter prevents a thundering herd when all stations come back online simultaneously.
    public string ComputeNextAttempt(int retryCount)
    {
        var index   = Math.Min(retryCount, _config.BackoffSeconds.Length - 1);
        var seconds = _config.BackoffSeconds[index];
        var jitter  = seconds * 0.1 * (Random.Shared.NextDouble() * 2 - 1);
        var wait    = Math.Max(1, (int)(seconds + jitter));

        return DateTime.UtcNow.AddSeconds(wait)
            .ToString("yyyy-MM-dd HH:mm:ss");
    }

    public bool IsDeadLetter(int retryCount) => retryCount >= _config.MaxRetries;
}
