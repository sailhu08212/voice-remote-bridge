using System.Runtime.InteropServices;

namespace VoiceRemoteBridge.Windows;

public sealed record FocusSnapshot(
    nint ForegroundWindow,
    nint FocusedWindow,
    uint ForegroundThreadId,
    bool IsValid);

public static class ForegroundFocusProbe
{
    public static FocusSnapshot Capture()
    {
        nint foreground = FocusNativeMethods.GetForegroundWindow();
        if (foreground == nint.Zero)
        {
            return new FocusSnapshot(nint.Zero, nint.Zero, 0, false);
        }

        uint threadId = FocusNativeMethods.GetWindowThreadProcessId(foreground, out _);
        if (threadId == 0)
        {
            return new FocusSnapshot(foreground, nint.Zero, 0, false);
        }

        GuiThreadInfo info = new() { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        bool success = FocusNativeMethods.GetGuiThreadInfo(threadId, ref info);
        return new FocusSnapshot(foreground, success ? info.FocusWindow : nint.Zero, threadId, success);
    }

    public static bool IsUnchanged(FocusSnapshot original, FocusSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(current);
        if (!original.IsValid || !current.IsValid)
        {
            return false;
        }

        return original.ForegroundWindow == current.ForegroundWindow &&
               original.FocusedWindow == current.FocusedWindow;
    }
}

internal static class FocusNativeMethods
{
    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetGUIThreadInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetGuiThreadInfo(uint threadId, ref GuiThreadInfo info);
}

[StructLayout(LayoutKind.Sequential)]
internal struct GuiThreadInfo
{
    internal uint Size;
    internal uint Flags;
    internal nint ActiveWindow;
    internal nint FocusWindow;
    internal nint CaptureWindow;
    internal nint MenuOwnerWindow;
    internal nint MoveSizeWindow;
    internal nint CaretWindow;
    internal NativeRectangle CaretRectangle;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRectangle
{
    internal int Left;
    internal int Top;
    internal int Right;
    internal int Bottom;
}
