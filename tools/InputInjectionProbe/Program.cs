using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace InputInjectionProbe;

internal static class Program
{
    private static readonly IReadOnlyDictionary<string, ushort> KeyMap = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = 0x11,
        ["Control"] = 0x11,
        ["Shift"] = 0x10,
        ["Alt"] = 0x12,
        ["Win"] = 0x5B,
        ["LWin"] = 0x5B,
        ["H"] = 0x48,
        ["F13"] = 0x7C,
        ["F14"] = 0x7D,
        ["F15"] = 0x7E,
        ["F16"] = 0x7F,
        ["F17"] = 0x80,
        ["F18"] = 0x81,
        ["F19"] = 0x82,
        ["F20"] = 0x83,
        ["F21"] = 0x84,
        ["F22"] = 0x85,
        ["F23"] = 0x86,
        ["F24"] = 0x87
    };

    private static readonly HashSet<ushort> ModifierKeys = [0x10, 0x11, 0x12, 0x5B, 0x5C];

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintHelp();
                return 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "probe-hotkey" => ProbeHotKey(ParseChord(RequireOption(args, "--chord"))),
                "send" => Send(ParseChord(RequireOption(args, "--chord")), ParseHoldMilliseconds(args)),
                "list-processes" => ListProcesses(),
                "target" => RunTargetWindow(),
                _ => Fail($"未知命令：{args[0]}")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"InputInjectionProbe 失败：{exception.Message}");
            return 1;
        }
    }

    private static int ProbeHotKey(IReadOnlyList<ushort> chord)
    {
        (uint modifiers, ushort key) = ToRegisterHotKey(chord);
        int id = 0x6000 + Environment.ProcessId % 0x1000;
        bool success = NativeMethods.RegisterHotKey(nint.Zero, id, modifiers, key);
        int lastError = success ? 0 : Marshal.GetLastWin32Error();
        if (success)
        {
            NativeMethods.UnregisterHotKey(nint.Zero, id);
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            chord = FormatChord(chord),
            registerHotKeySucceeded = success,
            lastError,
            interpretation = success
                ? "未发现 RegisterHotKey API 层面的占用；仍不能排除低级钩子、Raw Input 或私有监听。"
                : "可能已被 RegisterHotKey 占用、属于系统保留组合，或其他调用条件不满足；不能据此确定占用进程。"
        }, JsonOptions));
        return success ? 0 : 2;
    }

    private static int Send(IReadOnlyList<ushort> chord, int holdMilliseconds)
    {
        ushort[] physicallyDown = chord.Where(IsPhysicallyDown).ToArray();
        if (physicallyDown.Length > 0)
        {
            return Fail($"拒绝注入：本次组合中的物理键已按下：{FormatChord(physicallyDown)}。");
        }

        if (holdMilliseconds == 0)
        {
            Input[] atomic =
            [
                .. chord.Select(key => CreateKeyboardInput(key, keyUp: false)),
                .. chord.Reverse().Select(key => CreateKeyboardInput(key, keyUp: true))
            ];
            SendInputChecked(atomic);
        }
        else
        {
            Input[] down = chord.Select(key => CreateKeyboardInput(key, keyUp: false)).ToArray();
            Input[] up = chord.Reverse().Select(key => CreateKeyboardInput(key, keyUp: true)).ToArray();
            bool downSent = false;
            try
            {
                SendInputChecked(down);
                downSent = true;
                Thread.Sleep(holdMilliseconds);
            }
            finally
            {
                if (downSent)
                {
                    SendInputChecked(up);
                }
            }
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            chord = FormatChord(chord),
            holdMilliseconds,
            completed = true,
            allKeysReleased = chord.All(key => !IsPhysicallyDown(key))
        }, JsonOptions));
        return 0;
    }

    private static int ListProcesses()
    {
        string[] patterns = ["WeType", "Weixin", "WeChat"];
        object[] processes = Process.GetProcesses()
            .Where(process => patterns.Any(pattern => process.ProcessName.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(process => process.Id)
            .Select(process => new
            {
                process.ProcessName,
                process.Id,
                MainWindowTitle = SafeGet(() => process.MainWindowTitle),
                Path = SafeGet(() => process.MainModule?.FileName)
            })
            .Cast<object>()
            .ToArray();
        Console.WriteLine(JsonSerializer.Serialize(processes, JsonOptions));
        return 0;
    }

    private static int RunTargetWindow()
    {
        ApplicationConfiguration.Initialize();
        using Form form = new()
        {
            Text = "Voice Remote Bridge - 0B Test Target",
            Width = 760,
            Height = 420,
            StartPosition = FormStartPosition.CenterScreen
        };
        TextBox textBox = new()
        {
            Name = "VoiceInputTarget",
            AccessibleName = "Voice Input Test Target",
            Multiline = true,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 16),
            Text = "此窗口仅用于 Voice Remote Bridge 阶段 0B。\r\n\r\n请把语音识别文字输入到这里。\r\n"
        };
        form.Controls.Add(textBox);
        form.Shown += (_, _) =>
        {
            textBox.SelectionStart = textBox.TextLength;
            textBox.Focus();
        };
        Application.Run(form);
        return 0;
    }

    private static string? SafeGet(Func<string?> accessor)
    {
        try
        {
            return accessor();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<ushort> ParseChord(string raw)
    {
        string[] parts = raw.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException("快捷键组合不能为空。");
        }

        List<ushort> keys = [];
        foreach (string part in parts)
        {
            if (!KeyMap.TryGetValue(part, out ushort key))
            {
                throw new ArgumentException($"不支持的按键：{part}。本探针仅支持 Ctrl/Shift/Alt/Win/H/F13-F24。");
            }

            if (!keys.Contains(key))
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    private static (uint Modifiers, ushort Key) ToRegisterHotKey(IReadOnlyList<ushort> chord)
    {
        ushort[] nonModifiers = chord.Where(key => !ModifierKeys.Contains(key)).ToArray();
        if (nonModifiers.Length > 1)
        {
            throw new ArgumentException("RegisterHotKey 探测要求组合中至多有一个非修饰键。");
        }

        ushort baseKey = nonModifiers.Length == 1
            ? nonModifiers[0]
            : chord.FirstOrDefault(key => key is 0x5B or 0x5C, chord[^1]);

        uint modifiers = 0;
        foreach (ushort key in chord)
        {
            if (key == baseKey)
            {
                continue;
            }

            modifiers |= key switch
            {
                0x10 => 0x0004u,
                0x11 => 0x0002u,
                0x12 => 0x0001u,
                0x5B or 0x5C => 0x0008u,
                _ => 0u
            };
        }

        return (modifiers, baseKey);
    }

    private static int ParseHoldMilliseconds(string[] args)
    {
        string? raw = GetOption(args, "--hold-ms");
        if (raw is null)
        {
            return 0;
        }

        if (!int.TryParse(raw, out int value) || value is < 0 or > 10_000)
        {
            throw new ArgumentException("--hold-ms 必须是 0 到 10000 之间的整数。");
        }

        return value;
    }

    private static string RequireOption(string[] args, string name) =>
        GetOption(args, name) ?? throw new ArgumentException($"缺少参数 {name}。");

    private static string? GetOption(string[] args, string name)
    {
        for (int index = 1; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static bool IsPhysicallyDown(ushort key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;

    private static Input CreateKeyboardInput(ushort key, bool keyUp) => new()
    {
        Type = 1,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = key,
                Flags = keyUp ? 0x0002u : 0u
            }
        }
    };

    private static void SendInputChecked(Input[] inputs)
    {
        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException($"SendInput 仅发送 {sent}/{inputs.Length} 个事件，Win32={Marshal.GetLastWin32Error()}。");
        }
    }

    private static string FormatChord(IEnumerable<ushort> chord) => string.Join(
        '+',
        chord.Select(key => KeyMap.FirstOrDefault(pair => pair.Value == key).Key ?? $"0x{key:X2}"));

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            InputInjectionProbe - Voice Remote Bridge 阶段 0B 探针

            用法：
              InputInjectionProbe list-processes
              InputInjectionProbe target
              InputInjectionProbe probe-hotkey --chord F13
              InputInjectionProbe probe-hotkey --chord Ctrl+Win+H
              InputInjectionProbe send --chord F13 [--hold-ms 1000]
              InputInjectionProbe send --chord Ctrl+Win [--hold-ms 1000]

            --hold-ms 0 会在一次 SendInput 调用中成对发送 Key Down/Key Up。
            非零值会保持按下，并通过 finally 保证反序释放。
            """);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint inputCount, [In] Input[] inputs, int inputSize);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint window, int id);
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
