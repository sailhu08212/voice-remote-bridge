namespace VoiceRemoteBridge.Core;

public sealed record VoiceUiConfirmationOptions
{
    public required string ProcessName { get; init; }

    public required string WindowClass { get; init; }

    public int TimeoutMilliseconds { get; init; } = 2_500;

    public IReadOnlyList<string> Validate()
    {
        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(ProcessName))
        {
            errors.Add("Voice UI confirmation process name is required.");
        }

        if (string.IsNullOrWhiteSpace(WindowClass))
        {
            errors.Add("Voice UI confirmation window class is required.");
        }

        if (TimeoutMilliseconds is < 100 or > 10_000)
        {
            errors.Add("Voice UI confirmation timeout must be between 100 and 10000 milliseconds.");
        }

        return errors;
    }
}
