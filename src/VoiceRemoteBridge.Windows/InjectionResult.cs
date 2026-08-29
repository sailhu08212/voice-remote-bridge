namespace VoiceRemoteBridge.Windows;

public sealed record InjectionResult(
    bool Succeeded,
    int Win32Error,
    string Message)
{
    public static InjectionResult Success { get; } = new(true, 0, string.Empty);

    public static InjectionResult Failure(int error, string message) => new(false, error, message);
}

public enum KeyInjectionMode
{
    VirtualKey,
    ScanCode
}

