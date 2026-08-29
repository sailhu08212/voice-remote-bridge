using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VoiceRemoteBridge.Core;

namespace VoiceRemoteBridge.Windows;

public sealed record VoiceUiWindowMatch(
    bool Found,
    nint WindowHandle,
    int ProcessId,
    string WindowClass)
{
    public static VoiceUiWindowMatch NotFound { get; } = new(false, nint.Zero, 0, string.Empty);
}

public static class VoiceUiWindowProbe
{
    public static VoiceUiWindowMatch FindVisible(VoiceUiConfirmationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<string> errors = options.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(options));
        }

        string processName = Path.GetFileNameWithoutExtension(options.ProcessName.Trim());
        HashSet<int> processIds = [];
        Process[] processes = Process.GetProcessesByName(processName);
        try
        {
            foreach (Process process in processes)
            {
                processIds.Add(process.Id);
            }
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }

        if (processIds.Count == 0)
        {
            return VoiceUiWindowMatch.NotFound;
        }

        VoiceUiWindowMatch match = VoiceUiWindowMatch.NotFound;
        EnumWindows(
            (windowHandle, _) =>
            {
                if (!IsWindowVisible(windowHandle))
                {
                    return true;
                }

                GetWindowThreadProcessId(windowHandle, out uint processId);
                if (!processIds.Contains(unchecked((int)processId)))
                {
                    return true;
                }

                StringBuilder className = new(512);
                if (GetClassName(windowHandle, className, className.Capacity) <= 0 ||
                    !string.Equals(className.ToString(), options.WindowClass.Trim(), StringComparison.Ordinal))
                {
                    return true;
                }

                match = new VoiceUiWindowMatch(
                    true,
                    windowHandle,
                    unchecked((int)processId),
                    className.ToString());
                return false;
            },
            nint.Zero);
        return match;
    }

    private delegate bool EnumWindowsCallback(nint windowHandle, nint state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint windowHandle, StringBuilder className, int maximumCount);
}
