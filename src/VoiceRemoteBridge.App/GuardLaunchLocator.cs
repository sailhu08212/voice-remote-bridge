using System.IO;
using VoiceRemoteBridge.Core;
using VoiceRemoteBridge.Windows;

namespace VoiceRemoteBridge.App;

internal static class GuardLaunchLocator
{
    internal static InputGuardProcessManager CreateManager()
    {
        GuardLaunchInfo launchInfo = Resolve();
        return new InputGuardProcessManager(
            launchInfo,
            TimeSpan.FromMilliseconds(GuardProtocol.DefaultLeaseMilliseconds),
            TimeSpan.FromSeconds(3));
    }

    internal static GuardLaunchInfo Resolve()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string? currentProcess = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(currentProcess) &&
            string.Equals(
                Path.GetFileName(currentProcess),
                "VoiceRemoteBridge.App.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return new GuardLaunchInfo(currentProcess, ["--input-guard"]);
        }

        string[] executableCandidates =
        [
            Path.Combine(baseDirectory, "VoiceRemoteBridge.InputGuard.exe"),
            Path.Combine(baseDirectory, "InputGuard", "VoiceRemoteBridge.InputGuard.exe"),
            Path.GetFullPath(Path.Combine(
                baseDirectory,
                "..",
                "..",
                "..",
                "..",
                "VoiceRemoteBridge.InputGuard",
                "bin",
                "Debug",
                "net10.0-windows",
                "VoiceRemoteBridge.InputGuard.exe")),
            Path.GetFullPath(Path.Combine(
                baseDirectory,
                "..",
                "..",
                "..",
                "..",
                "VoiceRemoteBridge.InputGuard",
                "bin",
                "Release",
                "net10.0-windows",
                "VoiceRemoteBridge.InputGuard.exe"))
        ];

        string? executable = executableCandidates.FirstOrDefault(File.Exists);
        if (executable is null)
        {
            throw new FileNotFoundException(
                "VoiceRemoteBridge.InputGuard.exe was not found beside the app or in the development output.");
        }

        return new GuardLaunchInfo(executable, []);
    }
}
