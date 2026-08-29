using VoiceRemoteBridge.Core;

namespace VoiceRemoteBridge.Windows;

public sealed record VoiceButtonLearningEndpoint(
    string Id,
    string DisplayName,
    string HardwareId,
    HidTransport Transport,
    ushort UsagePage,
    ushort Usage,
    ushort InputReportLength);

public sealed record VoiceButtonLearningResult(
    bool Succeeded,
    HidButtonBinding? Binding,
    string Message,
    int NeutralReportCount,
    int PressedReportCount,
    int ReleasedReportCount);

public enum VoiceButtonLearningPhase
{
    Neutral,
    Pressed,
    Released,
    Completed
}

public sealed class VoiceButtonLearningSession : IAsyncDisposable
{
    private const int MaximumReportsPerPhase = 4_096;
    private readonly VoiceButtonLearningEndpoint endpoint;
    private readonly IHidReportSource source;
    private readonly bool allowOneShotVoice;
    private readonly object reportsLock = new();
    private readonly List<byte[]> neutralReports = [];
    private readonly List<byte[]> pressedReports = [];
    private readonly List<byte[]> releasedReports = [];
    private VoiceButtonLearningPhase phase = VoiceButtonLearningPhase.Neutral;
    private bool started;
    private bool disposed;

    public VoiceButtonLearningSession(VoiceButtonLearningEndpoint endpoint, bool allowOneShotVoice = false)
    {
        this.endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        this.allowOneShotVoice = allowOneShotVoice;
        HidButtonBinding provisional = new()
        {
            HardwareId = endpoint.HardwareId,
            Transport = endpoint.Transport,
            UsagePage = endpoint.UsagePage,
            Usage = endpoint.Usage,
            Pressed = new HidReportPattern { ValueHex = "00", MaskHex = "FF" },
            Released = new HidReportPattern { ValueHex = "01", MaskHex = "FF" }
        };
        source = endpoint.Transport switch
        {
            HidTransport.HidInterface => new HidInterfaceReportSource(provisional),
            HidTransport.RawInput => new RawInputReportSource(provisional),
            _ => throw new ArgumentOutOfRangeException(nameof(endpoint.Transport))
        };
    }

    public event EventHandler<string>? Diagnostic;

    public VoiceButtonLearningPhase Phase => phase;

    public bool IsConnected => source.IsConnected;

    public static IReadOnlyList<VoiceButtonLearningEndpoint> Discover(string hardwareId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareId);
        List<VoiceButtonLearningEndpoint> endpoints = [];

        foreach (HidInterfaceDescriptor descriptor in HidDeviceDiscovery.Enumerate(hardwareId)
                     .Where(item => item.CanOpenForRead && item.InputReportByteLength > 0))
        {
            endpoints.Add(new VoiceButtonLearningEndpoint(
                $"hid:{descriptor.UsagePage:X4}:{descriptor.Usage:X4}:{descriptor.DevicePath}",
                $"直接 HID · UsagePage 0x{descriptor.UsagePage:X4} / Usage 0x{descriptor.Usage:X4} · {descriptor.InputReportByteLength} 字节",
                hardwareId,
                HidTransport.HidInterface,
                descriptor.UsagePage,
                descriptor.Usage,
                descriptor.InputReportByteLength));
        }

        foreach (RawInputDeviceDescriptor descriptor in RawInputReportSource.Enumerate(hardwareId))
        {
            endpoints.Add(new VoiceButtonLearningEndpoint(
                $"raw:{descriptor.UsagePage:X4}:{descriptor.Usage:X4}:{descriptor.DevicePath}",
                $"Raw Input · {descriptor.Type} · UsagePage 0x{descriptor.UsagePage:X4} / Usage 0x{descriptor.Usage:X4}",
                hardwareId,
                HidTransport.RawInput,
                descriptor.UsagePage,
                descriptor.Usage,
                0));
        }

        return endpoints
            .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
        {
            return;
        }

        source.ReportReceived += Source_ReportReceived;
        source.Diagnostic += Source_Diagnostic;
        source.Start();
        started = true;
    }

    public void BeginPressedPhase()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureStarted();
        phase = VoiceButtonLearningPhase.Pressed;
    }

    public void BeginReleasedPhase()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureStarted();
        phase = VoiceButtonLearningPhase.Released;
    }

    public async Task<VoiceButtonLearningResult> CompleteAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureStarted();
        phase = VoiceButtonLearningPhase.Completed;
        await source.StopAsync().ConfigureAwait(false);

        byte[][] neutral;
        byte[][] pressed;
        byte[][] released;
        lock (reportsLock)
        {
            neutral = neutralReports.Select(item => item.ToArray()).ToArray();
            pressed = pressedReports.Select(item => item.ToArray()).ToArray();
            released = releasedReports.Select(item => item.ToArray()).ToArray();
        }

        return Analyze(endpoint, neutral, pressed, released, allowOneShotVoice);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        source.ReportReceived -= Source_ReportReceived;
        source.Diagnostic -= Source_Diagnostic;
        await source.DisposeAsync().ConfigureAwait(false);
        disposed = true;
    }

    public static VoiceButtonLearningResult Analyze(
        VoiceButtonLearningEndpoint endpoint,
        IReadOnlyList<byte[]> neutralReports,
        IReadOnlyList<byte[]> pressedReports,
        IReadOnlyList<byte[]> releasedReports,
        bool allowOneShotVoice = false)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(neutralReports);
        ArgumentNullException.ThrowIfNull(pressedReports);
        ArgumentNullException.ThrowIfNull(releasedReports);

        byte[]? neutral = MostFrequent(neutralReports);
        if (neutral is null)
        {
            return Failure("没有采集到按键松开时的中性基线报告。", neutralReports, pressedReports, releasedReports);
        }

        byte[]? released = MostFrequent(releasedReports);
        if (released is null)
        {
            return Failure(
                "没有在实际松开阶段采集到报告，不能证明存在可靠的物理松开信号。",
                neutralReports,
                pressedReports,
                releasedReports);
        }

        if (!released.AsSpan().SequenceEqual(neutral))
        {
            return Failure(
                "松开阶段的报告没有回到学习前的中性基线。",
                neutralReports,
                pressedReports,
                releasedReports);
        }

        byte[]? pressed = MostFrequent(
            pressedReports.Where(item => !item.AsSpan().SequenceEqual(neutral)).ToArray());
        if (pressed is null)
        {
            return Failure("按下阶段没有采集到区别于松开状态的报告。", neutralReports, pressedReports, releasedReports);
        }

        int firstPressedIndex = IndexOfReport(pressedReports, pressed);
        if (firstPressedIndex >= 0 &&
            pressedReports.Skip(firstPressedIndex + 1).Any(item => item.AsSpan().SequenceEqual(neutral)))
        {
            if (!allowOneShotVoice)
            {
                return Failure(
                    "按键仍被物理按住时，HID 报告已经提前回到中性；该通道只是短脉冲，不能用于 Push-to-Talk。",
                    neutralReports,
                    pressedReports,
                    releasedReports);
            }
        }

        if (pressed.Length != released.Length)
        {
            return Failure("按下和松开报告长度不同，无法生成安全匹配规则。", neutralReports, pressedReports, releasedReports);
        }

        byte[] mask = new byte[pressed.Length];
        for (int index = 0; index < mask.Length; index++)
        {
            mask[index] = (byte)(pressed[index] ^ released[index]);
        }

        if (mask.All(value => value == 0))
        {
            return Failure("按下和松开报告没有稳定差异。", neutralReports, pressedReports, releasedReports);
        }

        HidButtonBinding binding = new()
        {
            HardwareId = endpoint.HardwareId,
            Transport = endpoint.Transport,
            UsagePage = endpoint.UsagePage,
            Usage = endpoint.Usage,
            Pressed = new HidReportPattern
            {
                ValueHex = Convert.ToHexString(pressed),
                MaskHex = Convert.ToHexString(mask)
            },
            Released = new HidReportPattern
            {
                ValueHex = Convert.ToHexString(released),
                MaskHex = Convert.ToHexString(mask)
            }
        };
        IReadOnlyList<string> errors = binding.Validate();
        return errors.Count == 0
            ? new VoiceButtonLearningResult(
                true,
                binding,
                allowOneShotVoice
                    ? "已生成一次脉冲匹配规则；它只表示按键命令，不代表物理松手，需按“双按提交”流程验收。"
                    : "已生成按位差异掩码；必须再通过一次真实长按/松开回放验收。",
                neutralReports.Count,
                pressedReports.Count,
                releasedReports.Count)
            : Failure(string.Join(" ", errors), neutralReports, pressedReports, releasedReports);
    }

    private static VoiceButtonLearningResult Failure(
        string message,
        IReadOnlyCollection<byte[]> neutral,
        IReadOnlyCollection<byte[]> pressed,
        IReadOnlyCollection<byte[]> released) =>
        new(false, null, message, neutral.Count, pressed.Count, released.Count);

    private static byte[]? MostFrequent(IReadOnlyList<byte[]> reports) => reports
        .GroupBy(Convert.ToHexString, StringComparer.Ordinal)
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Key, StringComparer.Ordinal)
        .Select(group => Convert.FromHexString(group.Key))
        .FirstOrDefault();

    private static int IndexOfReport(IReadOnlyList<byte[]> reports, ReadOnlySpan<byte> target)
    {
        for (int index = 0; index < reports.Count; index++)
        {
            if (reports[index].AsSpan().SequenceEqual(target))
            {
                return index;
            }
        }

        return -1;
    }

    private void Source_ReportReceived(object? sender, HidReportEventArgs eventArgs)
    {
        lock (reportsLock)
        {
            List<byte[]> target = phase switch
            {
                VoiceButtonLearningPhase.Neutral => neutralReports,
                VoiceButtonLearningPhase.Pressed => pressedReports,
                VoiceButtonLearningPhase.Released => releasedReports,
                _ => []
            };
            if (target.Count < MaximumReportsPerPhase)
            {
                target.Add(eventArgs.Report.ToArray());
            }
        }
    }

    private void Source_Diagnostic(object? sender, string message) => Diagnostic?.Invoke(this, message);

    private void EnsureStarted()
    {
        if (!started)
        {
            throw new InvalidOperationException("The learning session has not started.");
        }
    }
}
