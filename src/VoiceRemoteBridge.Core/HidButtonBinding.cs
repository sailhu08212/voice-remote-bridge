using System.Globalization;

namespace VoiceRemoteBridge.Core;

public enum HidTransport
{
    RawInput,
    HidInterface
}

public sealed record HidReportPattern
{
    public required string ValueHex { get; init; }

    public required string MaskHex { get; init; }

    public IReadOnlyList<string> Validate(string name)
    {
        List<string> errors = [];
        if (!TryParseHex(ValueHex, out byte[] value))
        {
            errors.Add($"{name} value is not valid hexadecimal.");
        }

        if (!TryParseHex(MaskHex, out byte[] mask))
        {
            errors.Add($"{name} mask is not valid hexadecimal.");
        }

        if (errors.Count == 0 && value.Length != mask.Length)
        {
            errors.Add($"{name} value and mask lengths differ.");
        }

        if (errors.Count == 0 && mask.All(item => item == 0))
        {
            errors.Add($"{name} mask cannot be all zero.");
        }

        return errors;
    }

    public bool Matches(ReadOnlySpan<byte> report)
    {
        if (!TryParseHex(ValueHex, out byte[] value) ||
            !TryParseHex(MaskHex, out byte[] mask) ||
            report.Length != value.Length ||
            value.Length != mask.Length)
        {
            return false;
        }

        for (int index = 0; index < report.Length; index++)
        {
            if ((report[index] & mask[index]) != (value[index] & mask[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseHex(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value) || value.Length % 2 != 0)
        {
            return false;
        }

        byte[] parsed = new byte[value.Length / 2];
        for (int index = 0; index < parsed.Length; index++)
        {
            if (!byte.TryParse(
                value.AsSpan(index * 2, 2),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out parsed[index]))
            {
                return false;
            }
        }

        bytes = parsed;
        return true;
    }
}

public sealed record HidButtonBinding
{
    public int SchemaVersion { get; init; } = 1;

    public string HardwareId { get; init; } = "VID_1915&PID_1025";

    public HidTransport Transport { get; init; } = HidTransport.HidInterface;

    public required ushort UsagePage { get; init; }

    public required ushort Usage { get; init; }

    public required HidReportPattern Pressed { get; init; }

    public required HidReportPattern Released { get; init; }

    public IReadOnlyList<string> Validate()
    {
        List<string> errors = [];
        if (SchemaVersion != 1)
        {
            errors.Add($"Unsupported HID binding schema version: {SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(HardwareId))
        {
            errors.Add("Hardware id is required.");
        }

        if (UsagePage == 0)
        {
            errors.Add("Usage page is required.");
        }

        errors.AddRange(Pressed.Validate("Pressed report"));
        errors.AddRange(Released.Validate("Released report"));
        if (errors.Count == 0 &&
            string.Equals(Pressed.ValueHex, Released.ValueHex, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Pressed.MaskHex, Released.MaskHex, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Pressed and released report patterns are identical.");
        }

        return errors;
    }
}

public sealed class HidSignalDecoder
{
    private readonly HidButtonBinding binding;
    private bool pressed;

    public HidSignalDecoder(HidButtonBinding binding)
    {
        this.binding = binding ?? throw new ArgumentNullException(nameof(binding));
        IReadOnlyList<string> errors = binding.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(' ', errors), nameof(binding));
        }
    }

    public RemoteSignalKind? Decode(ushort usagePage, ushort usage, ReadOnlySpan<byte> report)
    {
        if (usagePage != binding.UsagePage || usage != binding.Usage)
        {
            return null;
        }

        if (binding.Released.Matches(report))
        {
            RemoteSignalKind signal = pressed ? RemoteSignalKind.Released : RemoteSignalKind.Neutral;
            pressed = false;
            return signal;
        }

        if (binding.Pressed.Matches(report))
        {
            RemoteSignalKind signal = pressed ? RemoteSignalKind.Repeated : RemoteSignalKind.Pressed;
            pressed = true;
            return signal;
        }

        return null;
    }

    public void Reset() => pressed = false;
}

