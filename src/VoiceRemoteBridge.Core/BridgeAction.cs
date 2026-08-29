namespace VoiceRemoteBridge.Core;

public enum BridgeActionKind
{
    CaptureFocus,
    EvaluatePreflight,
    StartAdapter,
    AbandonAdapterStart,
    StopAdapter,
    EmergencyStopAdapter,
    Notify
}

public sealed record BridgeAction(
    BridgeActionKind Kind,
    long Epoch,
    string Reason);

public sealed record PreflightDecision(
    bool IsReady,
    bool FocusUnchanged,
    string FailureReason)
{
    public static PreflightDecision Ready { get; } = new(true, true, string.Empty);

    public static PreflightDecision NotReady(string reason) => new(false, true, reason);

    public static PreflightDecision FocusChanged(string reason = "Focus changed during hold threshold.") =>
        new(true, false, reason);
}

public sealed record BridgeSnapshot(
    BridgeState State,
    long Epoch,
    bool PhysicalNeutral,
    TimeSpan LastTimestamp,
    TimeSpan? CandidateSince,
    TimeSpan? StartingSince,
    TimeSpan? SpeakingSince,
    TimeSpan? StoppingSince,
    TimeSpan CooldownUntil,
    string LastReason);

