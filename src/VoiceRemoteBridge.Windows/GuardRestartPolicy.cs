namespace VoiceRemoteBridge.Windows;

public sealed record GuardRestartSnapshot(
    int ConsecutiveFailures,
    DateTimeOffset NextAttemptAt,
    bool LockedOut);

public sealed class GuardRestartPolicy
{
    public const int MaximumConsecutiveFailures = 5;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16)
    ];
    private static readonly TimeSpan StableDuration = TimeSpan.FromSeconds(60);
    private readonly object sync = new();
    private int consecutiveFailures;
    private DateTimeOffset nextAttemptAt = DateTimeOffset.MinValue;

    public GuardRestartSnapshot Snapshot
    {
        get
        {
            lock (sync)
            {
                return new GuardRestartSnapshot(
                    consecutiveFailures,
                    nextAttemptAt,
                    consecutiveFailures >= MaximumConsecutiveFailures);
            }
        }
    }

    public bool CanAttempt(DateTimeOffset now, out TimeSpan wait)
    {
        lock (sync)
        {
            if (consecutiveFailures >= MaximumConsecutiveFailures)
            {
                wait = Timeout.InfiniteTimeSpan;
                return false;
            }

            wait = nextAttemptAt > now ? nextAttemptAt - now : TimeSpan.Zero;
            return wait == TimeSpan.Zero;
        }
    }

    public GuardRestartSnapshot RecordFailure(DateTimeOffset now)
    {
        lock (sync)
        {
            consecutiveFailures = Math.Min(consecutiveFailures + 1, MaximumConsecutiveFailures);
            nextAttemptAt = now + RetryDelays[consecutiveFailures - 1];
            return new GuardRestartSnapshot(
                consecutiveFailures,
                nextAttemptAt,
                consecutiveFailures >= MaximumConsecutiveFailures);
        }
    }

    public void ObserveHealthy(DateTimeOffset now, DateTimeOffset startedAt)
    {
        if (startedAt == DateTimeOffset.MinValue || now - startedAt < StableDuration)
        {
            return;
        }

        Reset();
    }

    public void Reset()
    {
        lock (sync)
        {
            consecutiveFailures = 0;
            nextAttemptAt = DateTimeOffset.MinValue;
        }
    }
}
