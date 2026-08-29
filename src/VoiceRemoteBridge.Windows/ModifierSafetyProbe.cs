using VoiceRemoteBridge.Core;

namespace VoiceRemoteBridge.Windows;

public sealed record ModifierSafetyResult(
    bool IsSafe,
    IReadOnlyList<ushort> PhysicallyDownModifiers,
    string Message);

public static class ModifierSafetyProbe
{
    private static readonly ushort[] ModifierKeys = [0x10, 0x11, 0x12, 0x5B, 0x5C];

    public static ModifierSafetyResult Evaluate(AdapterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ushort[] down = ModifierKeys.Where(IsDown).ToArray();
        ushort[] requiredConflict = down.Intersect(profile.StartChord).ToArray();
        if (requiredConflict.Length > 0)
        {
            return new ModifierSafetyResult(
                false,
                down,
                "A modifier required by the adapter is already physically held.");
        }

        ushort[] forbiddenConflict = down.Intersect(profile.ForbiddenModifiers).ToArray();
        if (forbiddenConflict.Length > 0)
        {
            return new ModifierSafetyResult(
                false,
                down,
                "A physically held modifier would form a known forbidden chord.");
        }

        return new ModifierSafetyResult(true, down, string.Empty);
    }

    private static bool IsDown(ushort key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;
}

