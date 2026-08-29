using System.Runtime.InteropServices;

namespace VoiceRemoteBridge.Windows;

public sealed class Win32KeyInjector
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventScanCode = 0x0008;
    private const uint MapVkToVsc = 0;
    private const nuint InjectionMarker = 0x565242;
    private readonly object sync = new();
    private readonly List<ushort> heldKeys = [];

    public IReadOnlyList<ushort> HeldKeys
    {
        get
        {
            lock (sync)
            {
                return heldKeys.ToArray();
            }
        }
    }

    public InjectionResult Hold(IReadOnlyList<ushort> keys, KeyInjectionMode mode)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return InjectionResult.Failure(0, "Cannot hold an empty key chord.");
        }

        lock (sync)
        {
            if (heldKeys.Count > 0)
            {
                return InjectionResult.Failure(0, "Another key chord is already held by this injector.");
            }

            Input[] inputs = keys.Select(key => CreateInput(key, keyUp: false, mode)).ToArray();
            uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            if (sent != inputs.Length)
            {
                int error = Marshal.GetLastWin32Error();
                ReleaseKeysBestEffort(keys, mode);
                return InjectionResult.Failure(error, $"SendInput sent {sent}/{inputs.Length} key-down events.");
            }

            heldKeys.AddRange(keys);
            return InjectionResult.Success;
        }
    }

    public InjectionResult Tap(IReadOnlyList<ushort> keys, KeyInjectionMode mode)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return InjectionResult.Failure(0, "Cannot tap an empty key chord.");
        }

        lock (sync)
        {
            if (heldKeys.Count > 0)
            {
                return InjectionResult.Failure(0, "Cannot tap while another key chord is held.");
            }

            Input[] inputs =
            [
                .. keys.Select(key => CreateInput(key, keyUp: false, mode)),
                .. keys.Reverse().Select(key => CreateInput(key, keyUp: true, mode))
            ];
            uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            if (sent != inputs.Length)
            {
                int error = Marshal.GetLastWin32Error();
                ReleaseKeysBestEffort(keys, mode);
                return InjectionResult.Failure(error, $"SendInput sent {sent}/{inputs.Length} atomic tap events.");
            }

            return InjectionResult.Success;
        }
    }

    public InjectionResult ReleaseAll(KeyInjectionMode mode)
    {
        lock (sync)
        {
            if (heldKeys.Count == 0)
            {
                return InjectionResult.Success;
            }

            ushort[] keys = heldKeys.ToArray();
            Input[] inputs = keys.Reverse().Select(key => CreateInput(key, keyUp: true, mode)).ToArray();
            uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            int error = sent == inputs.Length ? 0 : Marshal.GetLastWin32Error();

            // Forget only the key-up events confirmed as inserted. Remaining keys stay registered so
            // a caller can retry EmergencyStop rather than silently losing the safety state.
            int confirmed = checked((int)Math.Min(sent, (uint)heldKeys.Count));
            for (int index = 0; index < confirmed; index++)
            {
                heldKeys.RemoveAt(heldKeys.Count - 1);
            }

            return sent == inputs.Length
                ? InjectionResult.Success
                : InjectionResult.Failure(error, $"SendInput sent {sent}/{inputs.Length} key-up events.");
        }
    }

    public InjectionResult ReleaseSpecific(IReadOnlyList<ushort> keys, KeyInjectionMode mode)
    {
        ArgumentNullException.ThrowIfNull(keys);
        lock (sync)
        {
            Input[] inputs = keys.Reverse().Select(key => CreateInput(key, keyUp: true, mode)).ToArray();
            uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            int error = sent == inputs.Length ? 0 : Marshal.GetLastWin32Error();
            foreach (ushort key in keys.Reverse().Take(checked((int)sent)))
            {
                heldKeys.Remove(key);
            }

            return sent == inputs.Length
                ? InjectionResult.Success
                : InjectionResult.Failure(error, $"SendInput sent {sent}/{inputs.Length} recovery key-up events.");
        }
    }

    private static void ReleaseKeysBestEffort(IReadOnlyList<ushort> keys, KeyInjectionMode mode)
    {
        Input[] releases = keys.Reverse().Select(key => CreateInput(key, keyUp: true, mode)).ToArray();
        NativeMethods.SendInput((uint)releases.Length, releases, Marshal.SizeOf<Input>());
    }

    private static Input CreateInput(ushort key, bool keyUp, KeyInjectionMode mode)
    {
        uint flags = keyUp ? KeyEventKeyUp : 0;
        ushort virtualKey = key;
        ushort scanCode = 0;
        if (mode == KeyInjectionMode.ScanCode)
        {
            flags |= KeyEventScanCode;
            scanCode = checked((ushort)NativeMethods.MapVirtualKey(key, MapVkToVsc));
            virtualKey = 0;
        }

        return new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    ScanCode = scanCode,
                    Flags = flags,
                    ExtraInformation = InjectionMarker
                }
            }
        };
    }
}

internal static partial class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint inputCount, [In] Input[] inputs, int inputSize);

    [DllImport("user32.dll")]
    internal static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);
}

[StructLayout(LayoutKind.Sequential)]
internal struct Input
{
    internal uint Type;
    internal InputUnion Union;
}

[StructLayout(LayoutKind.Explicit)]
internal struct InputUnion
{
    [FieldOffset(0)]
    internal MouseInput Mouse;

    [FieldOffset(0)]
    internal KeyboardInput Keyboard;

    [FieldOffset(0)]
    internal HardwareInput Hardware;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MouseInput
{
    internal int X;
    internal int Y;
    internal uint MouseData;
    internal uint Flags;
    internal uint Time;
    internal nuint ExtraInformation;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KeyboardInput
{
    internal ushort VirtualKey;
    internal ushort ScanCode;
    internal uint Flags;
    internal uint Time;
    internal nuint ExtraInformation;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HardwareInput
{
    internal uint Message;
    internal ushort ParameterLow;
    internal ushort ParameterHigh;
}
