using SyncAgent.Config;

namespace SyncAgent.Sync;

public sealed class RetryPolicy
{
    private readonly int _maxRetries;

    public RetryPolicy(SyncConfig config) => _maxRetries = config.MaxRetries;

    // Exponential backoff: 30 s → 60 s → 2 min → 4 min → … capped at 1 hour.
    // ±10% jitter prevents a thundering herd when multiple stations reconnect simultaneously.
    public string ComputeNextAttempt(int retryCount)
    {
        var seconds = (int)Math.Min(30 * Math.Pow(2, retryCount - 1), 3600);
        var jitter  = seconds * 0.1 * (Random.Shared.NextDouble() * 2 - 1);
        var wait    = Math.Max(1, (int)(seconds + jitter));

        return DateTime.UtcNow.AddSeconds(wait)
            .ToString("yyyy-MM-dd HH:mm:ss");
    }

    public bool IsDeadLetter(int retryCount) => retryCount >= _maxRetries;
}
