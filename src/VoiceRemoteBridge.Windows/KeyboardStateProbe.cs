namespace VoiceRemoteBridge.Windows;

public static class KeyboardStateProbe
{
    public static bool IsDown(ushort virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
