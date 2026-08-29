using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace VoiceRemoteBridge.Windows;

public sealed record ActiveInputMethodSnapshot(
    nint LayoutHandle,
    string LayoutId,
    string Description)
{
    public bool Matches(string requiredValue)
    {
        if (string.IsNullOrWhiteSpace(requiredValue))
        {
            return true;
        }

        string value = requiredValue.Trim();
        return string.Equals(LayoutId, value, StringComparison.OrdinalIgnoreCase) ||
               string.Equals($"0x{LayoutId}", value, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(Description) &&
                Description.Contains(value, StringComparison.OrdinalIgnoreCase));
    }
}

public static class ActiveInputMethodProbe
{
    public static ActiveInputMethodSnapshot Capture(uint foregroundThreadId)
    {
        if (foregroundThreadId == 0)
        {
            return new ActiveInputMethodSnapshot(nint.Zero, string.Empty, string.Empty);
        }

        nint layout = InputMethodNativeMethods.GetKeyboardLayout(foregroundThreadId);
        uint layoutValue = unchecked((uint)layout.ToInt64());
        string layoutId = layoutValue.ToString("X8", CultureInfo.InvariantCulture);
        StringBuilder description = new(256);
        uint length = InputMethodNativeMethods.ImmGetDescription(layout, description, (uint)description.Capacity);
        return new ActiveInputMethodSnapshot(
            layout,
            layoutId,
            length > 0 ? description.ToString() : string.Empty);
    }
}

internal static class InputMethodNativeMethods
{
    [DllImport("user32.dll")]
    internal static extern nint GetKeyboardLayout(uint threadId);

    [DllImport("imm32.dll", EntryPoint = "ImmGetDescriptionW", CharSet = CharSet.Unicode)]
    internal static extern uint ImmGetDescription(nint keyboardLayout, StringBuilder description, uint length);
}
