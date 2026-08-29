namespace VoiceRemoteBridge.Core;

public sealed record BridgeTiming
{
    public BridgeTiming(
        TimeSpan holdThreshold,
        TimeSpan maximumSpeechDuration,
        TimeSpan retriggerCooldown,
        TimeSpan stopTimeout)
    {
        HoldThreshold = RequirePositive(holdThreshold, nameof(holdThreshold));
        MaximumSpeechDuration = RequirePositive(maximumSpeechDuration, nameof(maximumSpeechDuration));
        RetriggerCooldown = RequireNonNegative(retriggerCooldown, nameof(retriggerCooldown));
        StopTimeout = RequirePositive(stopTimeout, nameof(stopTimeout));

        if (MaximumSpeechDuration <= HoldThreshold)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSpeechDuration),
                "Maximum speech duration must exceed the hold threshold.");
        }
    }

    public TimeSpan HoldThreshold { get; }

    public TimeSpan MaximumSpeechDuration { get; }

    public TimeSpan RetriggerCooldown { get; }

    public TimeSpan StopTimeout { get; }

    public static BridgeTiming Default { get; } = new(
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromSeconds(120),
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromSeconds(1));

    private static TimeSpan RequirePositive(TimeSpan value, string parameterName) =>
        value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, "Value must be positive.");

    private static TimeSpan RequireNonNegative(TimeSpan value, string parameterName) =>
        value >= TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, "Value must not be negative.");
}

