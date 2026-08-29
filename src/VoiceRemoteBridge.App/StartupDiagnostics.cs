using System.IO;

namespace VoiceRemoteBridge.App;

internal static class StartupDiagnostics
{
    private const string TraceEnvironmentVariable = "VRB_STARTUP_TRACE";

    internal static void Record(string stage)
    {
        string? path = Environment.GetEnvironmentVariable(TraceEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.AppendAllText(
                Path.GetFullPath(path),
                $"{DateTimeOffset.Now:O}\t{Environment.ProcessId}\t{stage}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }
}
