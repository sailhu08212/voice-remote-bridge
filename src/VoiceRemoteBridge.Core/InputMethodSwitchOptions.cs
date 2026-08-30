namespace VoiceRemoteBridge.Core;

public sealed record InputMethodSwitchOptions
{
    public required string TargetProfile { get; init; }

    public int ActivationTimeoutMilliseconds { get; init; } = 1_000;

    public int PostActivationDelayMilliseconds { get; init; } = 1_000;

    public int RestoreDelayMilliseconds { get; init; } = 500;

    public bool RestoreAfterStop { get; init; } = true;

    public bool AllowProfileEnablement { get; init; }

    public bool? RefreshWhenAlreadyActive { get; init; }

    public IReadOnlyList<string> Validate()
    {
        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(TargetProfile))
        {
            errors.Add("Input-method switch target profile is required.");
        }

        if (ActivationTimeoutMilliseconds is < 100 or > 5_000)
        {
            errors.Add("Input-method activation timeout must be between 100 and 5000 milliseconds.");
        }

        if (PostActivationDelayMilliseconds is < 0 or > 5_000)
        {
            errors.Add("Input-method post-activation delay must be between 0 and 5000 milliseconds.");
        }

        if (RestoreDelayMilliseconds is < 0 or > 5_000)
        {
            errors.Add("Input-method restore delay must be between 0 and 5000 milliseconds.");
        }

        return errors;
    }
}
