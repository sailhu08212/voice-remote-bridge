using System.IO;
using VoiceRemoteBridge.InputGuard;

namespace VoiceRemoteBridge.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        StartupDiagnostics.Record("Program.Main entered");
        EnsureProcessWindowsDirectory();
        if (args.Contains("--input-guard", StringComparer.OrdinalIgnoreCase))
        {
            return InputGuardHost.RunAsync(args).GetAwaiter().GetResult();
        }

        App application = new();
        StartupDiagnostics.Record("App constructed");
        application.InitializeComponent();
        StartupDiagnostics.Record("App resources initialized");
        return application.Run();
    }

    private static void EnsureProcessWindowsDirectory()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
        {
            return;
        }

        string? systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (!string.IsNullOrWhiteSpace(systemRoot) && Path.IsPathFullyQualified(systemRoot))
        {
            Environment.SetEnvironmentVariable("windir", systemRoot, EnvironmentVariableTarget.Process);
        }
    }
}
