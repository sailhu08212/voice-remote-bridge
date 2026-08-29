using System.Globalization;

namespace VoiceRemoteBridge.Core;

public sealed record KeyChordParseResult(
    bool Succeeded,
    IReadOnlyList<ushort> Keys,
    string Error);

public static class KeyChordCodec
{
    private static readonly IReadOnlyDictionary<string, ushort> NamedKeys =
        new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["SHIFT"] = 0x10,
            ["CTRL"] = 0x11,
            ["CONTROL"] = 0x11,
            ["ALT"] = 0x12,
            ["WIN"] = 0x5B,
            ["LWIN"] = 0x5B,
            ["RWIN"] = 0x5C,
            ["SPACE"] = 0x20,
            ["ENTER"] = 0x0D,
            ["TAB"] = 0x09,
            ["ESC"] = 0x1B,
            ["ESCAPE"] = 0x1B,
            ["BACKSPACE"] = 0x08,
            ["DELETE"] = 0x2E,
            ["INSERT"] = 0x2D,
            ["HOME"] = 0x24,
            ["END"] = 0x23,
            ["PAGEUP"] = 0x21,
            ["PAGEDOWN"] = 0x22,
            ["UP"] = 0x26,
            ["DOWN"] = 0x28,
            ["LEFT"] = 0x25,
            ["RIGHT"] = 0x27
        };

    public static KeyChordParseResult Parse(string? text, bool allowEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return allowEmpty
                ? new KeyChordParseResult(true, [], string.Empty)
                : new KeyChordParseResult(false, [], "快捷键不能为空。");
        }

        string[] tokens = text.Split(
            ['+', ',', ';', '，', '；'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        List<ushort> keys = [];
        foreach (string token in tokens)
        {
            if (!TryParseToken(token, out ushort key))
            {
                return new KeyChordParseResult(false, [], $"无法识别按键“{token}”。");
            }

            if (keys.Contains(key))
            {
                return new KeyChordParseResult(false, [], $"按键“{token}”重复出现。");
            }

            keys.Add(key);
        }

        return keys.Count == 0 && !allowEmpty
            ? new KeyChordParseResult(false, [], "快捷键不能为空。")
            : new KeyChordParseResult(true, keys, string.Empty);
    }

    public static string Format(IEnumerable<ushort> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return string.Join('+', keys.Select(FormatKey));
    }

    private static bool TryParseToken(string token, out ushort key)
    {
        if (NamedKeys.TryGetValue(token, out key))
        {
            return true;
        }

        if (token.Length == 1)
        {
            char value = char.ToUpperInvariant(token[0]);
            if (value is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                key = value;
                return true;
            }
        }

        if (token.Length is >= 2 and <= 3 &&
            token[0] is 'F' or 'f' &&
            int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int functionNumber) &&
            functionNumber is >= 1 and <= 24)
        {
            key = checked((ushort)(0x70 + functionNumber - 1));
            return true;
        }

        string hex = token.StartsWith("VK_", StringComparison.OrdinalIgnoreCase)
            ? token[3..]
            : token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : string.Empty;
        return hex.Length is > 0 and <= 4 &&
               ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out key) &&
               key != 0;
    }

    private static string FormatKey(ushort key)
    {
        KeyValuePair<string, ushort> named = NamedKeys
            .Where(item => item.Key is not ("CONTROL" or "LWIN" or "ESCAPE"))
            .FirstOrDefault(item => item.Value == key);
        if (!string.IsNullOrEmpty(named.Key))
        {
            return named.Key switch
            {
                "SHIFT" => "Shift",
                "CTRL" => "Ctrl",
                "ALT" => "Alt",
                "WIN" => "Win",
                "RWIN" => "RWin",
                _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(named.Key.ToLowerInvariant())
            };
        }

        if (key is >= 0x70 and <= 0x87)
        {
            return $"F{key - 0x70 + 1}";
        }

        if (key is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return ((char)key).ToString();
        }

        return $"VK_{key:X2}";
    }
}
