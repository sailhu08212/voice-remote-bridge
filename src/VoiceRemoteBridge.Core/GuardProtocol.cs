namespace VoiceRemoteBridge.Core;

public enum GuardOperation
{
    Ping,
    Hold,
    Renew,
    Release,
    Shutdown
}

public sealed record GuardRequest
{
    public int ProtocolVersion { get; init; } = GuardProtocol.CurrentVersion;

    public Guid RequestId { get; init; } = Guid.NewGuid();

    public required GuardOperation Operation { get; init; }

    public long Epoch { get; init; }

    public IReadOnlyList<ushort> Keys { get; init; } = [];

    public string InjectionMode { get; init; } = "VirtualKey";
}

public sealed record GuardResponse
{
    public int ProtocolVersion { get; init; } = GuardProtocol.CurrentVersion;

    public required Guid RequestId { get; init; }

    public required bool Succeeded { get; init; }

    public required long ActiveEpoch { get; init; }

    public required bool KeysHeld { get; init; }

    public required string Message { get; init; }

    public int Win32Error { get; init; }
}

public static class GuardProtocol
{
    public const int CurrentVersion = 1;

    public const int DefaultLeaseMilliseconds = 3_000;

    public static string BuildPipeName(string sessionToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);
        if (sessionToken.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("Session token contains unsupported characters.", nameof(sessionToken));
        }

        return $"VoiceRemoteBridge.InputGuard.{sessionToken}";
    }
}
