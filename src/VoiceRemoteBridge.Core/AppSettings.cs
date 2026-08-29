namespace VoiceRemoteBridge.Core;

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;

    public string HardwareId { get; init; } = "VID_1915&PID_1025";

    public string AudioEndpointName { get; init; } = "SG Control Mic";

    public HidButtonBinding? VoiceButton { get; init; }

    public AdapterProfile? SelectedAdapter { get; init; }

    public BridgeInteractionMode InteractionMode { get; init; } = BridgeInteractionMode.VoiceCommandPressAgain;

    public int HoldThresholdMilliseconds { get; init; } = 150;

    public int MaximumSpeechSeconds { get; init; } = 300;

    public int RetriggerCooldownMilliseconds { get; init; } = 300;

    public int StopTimeoutMilliseconds { get; init; } = 1_000;

    public int AudioActivationTimeoutMilliseconds { get; init; } = 1_200;

    public int AudioActivationWarmupMilliseconds { get; init; } = 250;

    public double AudioActivationRmsThreshold { get; init; } = 0.001;

    public int AudioActivationConsecutivePackets { get; init; } = 5;

    public int AudioHandoffMilliseconds { get; init; } = 500;

    public bool StartWithWindows { get; init; }

    public BridgeTiming ToTiming() => new(
        TimeSpan.FromMilliseconds(HoldThresholdMilliseconds),
        TimeSpan.FromSeconds(MaximumSpeechSeconds),
        TimeSpan.FromMilliseconds(RetriggerCooldownMilliseconds),
        TimeSpan.FromMilliseconds(StopTimeoutMilliseconds));

    public IReadOnlyList<string> Validate()
    {
        List<string> errors = [];
        if (SchemaVersion != 1)
        {
            errors.Add($"Unsupported settings schema version: {SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(HardwareId))
        {
            errors.Add("Hardware id is required.");
        }

        if (string.IsNullOrWhiteSpace(AudioEndpointName))
        {
            errors.Add("Audio endpoint name is required.");
        }

        try
        {
            _ = ToTiming();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            errors.Add(exception.Message);
        }

        if (!Enum.IsDefined(InteractionMode))
        {
            errors.Add($"Unsupported interaction mode: {InteractionMode}.");
        }

        if (InteractionMode == BridgeInteractionMode.VoiceCommandPressAgain &&
            SelectedAdapter?.InjectionLifetime == InjectionLifetime.HeldAcrossSpeech)
        {
            errors.Add(
                "VoiceCommandPressAgain cannot use HeldAcrossSpeech because a lost second pulse would leave modifier keys held. Use a Toggle/AtomicBatch or StartStopPair adapter.");
        }

        if (AudioActivationTimeoutMilliseconds <= 0)
        {
            errors.Add("Audio activation timeout must be positive.");
        }

        if (AudioActivationWarmupMilliseconds < 0 ||
            AudioActivationWarmupMilliseconds >= AudioActivationTimeoutMilliseconds)
        {
            errors.Add("Audio activation warmup must be non-negative and shorter than the activation timeout.");
        }

        if (!double.IsFinite(AudioActivationRmsThreshold) ||
            AudioActivationRmsThreshold <= 0 ||
            AudioActivationRmsThreshold > 1)
        {
            errors.Add("Audio activation RMS threshold must be greater than zero and at most one.");
        }

        if (AudioActivationConsecutivePackets is < 1 or > 100)
        {
            errors.Add("Audio activation consecutive packet count must be between 1 and 100.");
        }

        if (AudioHandoffMilliseconds is < 0 or > 5_000)
        {
            errors.Add("Audio handoff duration must be between 0 and 5000 milliseconds.");
        }

        if (VoiceButton is not null)
        {
            errors.AddRange(VoiceButton.Validate());
        }

        if (SelectedAdapter is not null)
        {
            errors.AddRange(SelectedAdapter.Validate());
        }

        return errors;
    }
}
