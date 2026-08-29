using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace HidProbe;

internal static class Program
{
    private const string DefaultHardwareId = "VID_1915&PID_1025";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                return AudioPacketProbe.RunInteractive();
            }

            if (IsHelp(args[0]))
            {
                PrintHelp();
                return 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "list" => ListDevices(GetOption(args, "--hardware-id") ?? DefaultHardwareId),
                "capture" => Capture(args),
                "hid-list" => ListHidInterfaces(GetOption(args, "--hardware-id") ?? DefaultHardwareId),
                "hid-capture" => CaptureHidInterfaces(args),
                "feature-diagnostic" => RunFeatureDiagnostic(),
                "packet-diagnostic" => AudioPacketProbe.RunInteractive(),
                "audio" => MeasureAudio(args),
                "voice-diagnostic" => RunInteractiveVoiceDiagnostic(),
                _ => Fail($"未知命令：{args[0]}")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"HidProbe 失败：{exception.Message}");
            return 1;
        }
    }

    private static bool IsHelp(string value) => value is "-h" or "--help" or "help";

    private static int ListDevices(string hardwareId)
    {
        IReadOnlyList<RawDeviceDescriptor> devices = RawInputNative.EnumerateDevices(hardwareId);
        Console.WriteLine(JsonSerializer.Serialize(devices, JsonOptions.Indented));
        return devices.Count == 0 ? 2 : 0;
    }

    private static int Capture(string[] args)
    {
        string hardwareId = GetOption(args, "--hardware-id") ?? DefaultHardwareId;
        string label = GetOption(args, "--label") ?? "unlabeled";
        TimeSpan duration = ParseDuration(args, defaultSeconds: 10);
        string output = GetOutputPath(args, $"hid-{SanitizeFileName(label)}-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");

        IReadOnlyList<RawDeviceDescriptor> devices = RawInputNative.EnumerateDevices(hardwareId);
        if (devices.Count == 0)
        {
            return Fail($"没有找到包含 {hardwareId} 的 Raw Input 设备。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        Console.WriteLine($"开始采集 {duration.TotalSeconds:0.#} 秒，仅记录 {hardwareId}；标签：{label}");
        Console.WriteLine($"输出：{output}");
        using RawCaptureSession session = new(devices, hardwareId, label, output);
        RawCaptureSummary summary = session.Run(duration);
        Console.WriteLine(JsonSerializer.Serialize(summary, JsonOptions.Indented));
        return summary.EventCount == 0 ? 3 : 0;
    }

    private static int ListHidInterfaces(string hardwareId)
    {
        IReadOnlyList<HidInterfaceDescriptor> devices = HidInterfaceNative.Enumerate(hardwareId);
        Console.WriteLine(JsonSerializer.Serialize(devices, JsonOptions.Indented));
        return devices.Count == 0 ? 2 : 0;
    }

    private static int CaptureHidInterfaces(string[] args)
    {
        string hardwareId = GetOption(args, "--hardware-id") ?? DefaultHardwareId;
        string label = GetOption(args, "--label") ?? "unlabeled";
        TimeSpan duration = ParseDuration(args, defaultSeconds: 10);
        string output = GetOutputPath(args, $"hid-interface-{SanitizeFileName(label)}-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        HidInterfaceCaptureSummary summary = HidInterfaceCapture
            .RunAsync(hardwareId, label, duration, output)
            .GetAwaiter()
            .GetResult();
        Console.WriteLine(JsonSerializer.Serialize(summary, JsonOptions.Indented));
        return summary.ReportCount == 0 ? 3 : 0;
    }

    private static int MeasureAudio(string[] args)
    {
        string endpointName = GetOption(args, "--name") ?? "SG Control Mic";
        string label = GetOption(args, "--label") ?? "unlabeled";
        TimeSpan duration = ParseDuration(args, defaultSeconds: 5);
        string output = GetOutputPath(args, $"audio-level-{SanitizeFileName(label)}-{DateTime.Now:yyyyMMdd-HHmmss}.json");

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        AudioLevelSummary summary = AudioMeterProbe.Measure(endpointName, label, duration, TimeSpan.FromMilliseconds(50));
        File.WriteAllText(output, JsonSerializer.Serialize(summary, JsonOptions.Indented), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Console.WriteLine(JsonSerializer.Serialize(summary, JsonOptions.Indented));
        Console.WriteLine($"输出：{output}");
        return 0;
    }

    private static int RunFeatureDiagnostic()
    {
        if (Process.GetProcessesByName("VoiceRemoteBridge.App").Length > 0)
        {
            return Fail("请先在 Voice Remote Bridge 中点击“停止桥接”和“退出”，再运行本诊断工具。");
        }

        HidInterfaceDescriptor[] candidates = HidInterfaceNative
            .Enumerate(DefaultHardwareId)
            .Where(item =>
                item.CanOpenForRead &&
                item.FeatureReportByteLength > 0 &&
                item.FeatureReportIds.Count > 0)
            .ToArray();
        if (candidates.Length == 0)
        {
            return Fail("接收器没有可只读访问且声明了 Report ID 的 Feature Report 接口；未执行任何设备读取。");
        }

        string directory = Path.GetFullPath(Path.Combine(
            "artifacts",
            $"feature-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}"));
        Directory.CreateDirectory(directory);
        string samplesOutput = Path.Combine(directory, "feature-reports.jsonl");
        string summaryOutput = Path.Combine(directory, "summary.json");

        Console.WriteLine("Voice Remote Bridge - 厂商 Feature Report 只读诊断");
        Console.WriteLine("本工具只发送标准 GET_REPORT 读取请求，读取设备自己声明的报告编号。");
        Console.WriteLine("不会调用 SetFeature/SetOutputReport，不会使用设备写接口或改变设备状态；不读取普通键盘，也不录音。");
        Console.WriteLine();
        foreach (HidInterfaceDescriptor candidate in candidates)
        {
            Console.WriteLine($"接口：UsagePage=0x{candidate.UsagePage:X4}, Usage=0x{candidate.Usage:X4}, " +
                $"FeatureLength={candidate.FeatureReportByteLength}, " +
                $"ReportID=[{string.Join(", ", candidate.FeatureReportIds.Select(id => $"0x{id:X2}"))}]");
        }

        Console.WriteLine();
        Console.WriteLine("测试会连续读取状态；每次按 Enter 只用于标记你完成了遥控器动作。");
        Console.Write("先确保遥控器语音键处于松开状态，然后按 Enter 开始：");
        Console.ReadLine();

        DateTimeOffset startedAt = DateTimeOffset.Now;
        Stopwatch stopwatch = Stopwatch.StartNew();
        string phase = "baseline-released";
        List<string> errors = [];
        Dictionary<string, HashSet<string>> distinctReportsByPhase = new(StringComparer.Ordinal);
        List<(HidInterfaceDescriptor Descriptor, SafeFileHandle Handle)> openInterfaces = [];
        foreach (HidInterfaceDescriptor candidate in candidates)
        {
            SafeFileHandle handle = HidInterfaceNative.OpenForFeatureRead(candidate.DevicePath);
            if (handle.IsInvalid)
            {
                errors.Add($"{candidate.DevicePath}: CreateFile Win32={Marshal.GetLastWin32Error()}");
                handle.Dispose();
                continue;
            }

            openInterfaces.Add((candidate, handle));
        }

        if (openInterfaces.Count == 0)
        {
            return Fail("Feature Report 接口无法打开；未读取任何设备状态。");
        }

        int totalSamples = 0;
        int successfulSamples = 0;
        using CancellationTokenSource cancellation = new();
        using StreamWriter writer = new(samplesOutput, append: false, new UTF8Encoding(false))
        {
            AutoFlush = true
        };
        Task pollTask = Task.Run(async () =>
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    string phaseSnapshot = Volatile.Read(ref phase);
                    foreach ((HidInterfaceDescriptor descriptor, SafeFileHandle handle) in openInterfaces)
                    {
                        foreach (byte reportId in descriptor.FeatureReportIds)
                        {
                            byte[] buffer = new byte[descriptor.FeatureReportByteLength];
                            buffer[0] = reportId;
                            bool succeeded = NativeMethods.HidDGetFeature(handle, buffer, buffer.Length);
                            int error = succeeded ? 0 : Marshal.GetLastWin32Error();
                            string? reportHex = succeeded ? Convert.ToHexString(buffer) : null;
                            totalSamples++;
                            if (succeeded)
                            {
                                successfulSamples++;
                                if (!distinctReportsByPhase.TryGetValue(phaseSnapshot, out HashSet<string>? reports))
                                {
                                    reports = new HashSet<string>(StringComparer.Ordinal);
                                    distinctReportsByPhase.Add(phaseSnapshot, reports);
                                }

                                reports.Add($"{reportId:X2}:{reportHex}");
                            }

                            object record = new
                            {
                                eventType = "hidFeatureReport",
                                timestamp = DateTimeOffset.Now,
                                elapsedMilliseconds = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
                                phase = phaseSnapshot,
                                descriptor.DevicePath,
                                descriptor.UsagePage,
                                descriptor.Usage,
                                reportId,
                                reportLength = buffer.Length,
                                succeeded,
                                win32Error = error,
                                reportHex
                            };
                            writer.WriteLine(JsonSerializer.Serialize(record, JsonOptions.Compact));
                        }
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(50), cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                errors.Add($"{exception.GetType().Name}: {exception.Message}");
            }
        });

        void SetPhase(string value) => Volatile.Write(ref phase, value);

        Thread.Sleep(TimeSpan.FromSeconds(2));
        SetPhase("press-transition-1");
        Console.Write(">>> 现在按住遥控器语音键不要松开；按稳后用另一只手按 Enter：");
        Console.ReadLine();
        SetPhase("held-1");
        Thread.Sleep(TimeSpan.FromSeconds(3));
        SetPhase("release-transition-1");
        Console.Write(">>> 现在松开遥控器语音键；松开后用另一只手按 Enter：");
        Console.ReadLine();
        SetPhase("released-1");
        Thread.Sleep(TimeSpan.FromSeconds(3));

        SetPhase("press-transition-2");
        Console.Write(">>> 重复一次：按住语音键不要松开；按稳后按 Enter：");
        Console.ReadLine();
        SetPhase("held-2");
        Thread.Sleep(TimeSpan.FromSeconds(3));
        SetPhase("release-transition-2");
        Console.Write(">>> 松开语音键；松开后按 Enter：");
        Console.ReadLine();
        SetPhase("released-2");
        Thread.Sleep(TimeSpan.FromSeconds(3));

        cancellation.Cancel();
        pollTask.GetAwaiter().GetResult();
        stopwatch.Stop();
        foreach ((_, SafeFileHandle handle) in openInterfaces)
        {
            handle.Dispose();
        }

        var phaseSummary = distinctReportsByPhase
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                item => item.Key,
                item => item.Value.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        object summary = new
        {
            hardwareId = DefaultHardwareId,
            startedAt,
            durationSeconds = stopwatch.Elapsed.TotalSeconds,
            interfaceCount = openInterfaces.Count,
            reportIds = candidates
                .SelectMany(item => item.FeatureReportIds)
                .Distinct()
                .Order()
                .Select(id => (int)id)
                .ToArray(),
            totalSamples,
            successfulSamples,
            failedSamples = totalSamples - successfulSamples,
            distinctReportsByPhase = phaseSummary,
            errors
        };
        File.WriteAllText(summaryOutput, JsonSerializer.Serialize(summary, JsonOptions.Indented), new UTF8Encoding(false));

        Console.WriteLine();
        Console.WriteLine("Feature Report 只读诊断已完成。");
        Console.WriteLine($"读取成功：{successfulSamples}/{totalSamples}；阶段数：{phaseSummary.Count}；错误数：{errors.Count}。");
        Console.WriteLine($"结果目录：{directory}");
        Console.WriteLine("请保持这些文件不变，然后告诉 Codex“Feature 诊断已完成”。");
        Console.WriteLine("按 Enter 关闭窗口。");
        Console.ReadLine();
        return successfulSamples > 0 ? 0 : 3;
    }

    private static int RunInteractiveVoiceDiagnostic()
    {
        const string hardwareId = DefaultHardwareId;
        const string label = "voice-hold-release";
        TimeSpan duration = TimeSpan.FromSeconds(15);
        if (Process.GetProcessesByName("VoiceRemoteBridge.App").Length > 0)
        {
            return Fail("请先在 Voice Remote Bridge 中点击“停止桥接”和“退出”，再运行本诊断工具。");
        }

        string directory = Path.GetFullPath(Path.Combine(
            "artifacts",
            $"voice-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}"));
        Directory.CreateDirectory(directory);
        string rawOutput = Path.Combine(directory, "raw-input.jsonl");
        string hidOutput = Path.Combine(directory, "hid-interfaces.jsonl");
        string audioOutput = Path.Combine(directory, "audio-levels.jsonl");
        string markerOutput = Path.Combine(directory, "phase-markers.jsonl");

        IReadOnlyList<RawDeviceDescriptor> devices = RawInputNative.EnumerateDevices(hardwareId);
        Console.WriteLine("Voice Remote Bridge - 语音键全通道只读诊断");
        Console.WriteLine("本工具只记录指定接收端的 HID 报告、时间和麦克风音量峰值，不读取普通键盘，不录音。");
        Console.WriteLine("操作过程：倒计时后左手持续按住实体键盘 Ctrl+Win；右手按提示操作遥控器；最后再松开 Ctrl+Win。");
        Console.WriteLine();
        Console.Write("准备好后按 Enter 开始：");
        Console.ReadLine();

        DateTimeOffset startedAt = DateTimeOffset.Now;
        using StreamWriter markerWriter = new(markerOutput, append: false, new UTF8Encoding(false))
        {
            AutoFlush = true
        };
        void Mark(string phase)
        {
            var marker = new
            {
                eventType = "phaseMarker",
                timestamp = DateTimeOffset.Now,
                elapsedMilliseconds = Math.Round((DateTimeOffset.Now - startedAt).TotalMilliseconds, 3),
                phase
            };
            markerWriter.WriteLine(JsonSerializer.Serialize(marker, JsonOptions.Compact));
        }

        Task<HidInterfaceCaptureSummary> hidTask = HidInterfaceCapture.RunAsync(
            hardwareId,
            label,
            duration,
            hidOutput);
        Task<RawCaptureSummary> rawTask = Task.Run(() =>
        {
            using RawCaptureSession session = new(devices, hardwareId, label, rawOutput);
            return session.Run(duration);
        });
        Task<AudioLevelSummary> audioTask = Task.Run(() =>
        {
            using StreamWriter audioWriter = new(audioOutput, append: false, new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            return AudioMeterProbe.Measure(
                "SG Control Mic",
                label,
                duration,
                TimeSpan.FromMilliseconds(50),
                sample => audioWriter.WriteLine(JsonSerializer.Serialize(sample, JsonOptions.Compact)));
        });

        Mark("preparation-start");
        Console.WriteLine("请把左手放到实体键盘 Ctrl+Win，右手拿好遥控器。3 秒后开始……");
        Thread.Sleep(TimeSpan.FromSeconds(3));
        Mark("ctrl-win-hold-prompt");
        Console.WriteLine(">>> 现在用左手持续按住 Ctrl+Win，整个测试期间都不要松开！");
        Thread.Sleep(TimeSpan.FromSeconds(1));
        Mark("active-voice-session-baseline-start");
        Console.WriteLine("[1/4] 保持 Ctrl+Win，遥控器语音键保持松开 2 秒……");
        Thread.Sleep(TimeSpan.FromSeconds(2));
        Mark("physical-hold-silent-start");
        Console.WriteLine(">>> 保持 Ctrl+Win；现在按住遥控器语音键并保持安静，不要说话，也不要松开！");
        Thread.Sleep(TimeSpan.FromSeconds(2));
        Mark("physical-hold-speech-start");
        Console.WriteLine(">>> 两边都继续按住，靠近遥控器持续说“啊——”，不要松开！");
        Thread.Sleep(TimeSpan.FromSeconds(3));
        Mark("physical-release-start");
        Console.WriteLine(">>> 只松开遥控器语音键；Ctrl+Win 继续按住！");
        Thread.Sleep(TimeSpan.FromSeconds(3));
        Mark("observation-complete");
        Console.WriteLine(">>> 现在可以松开 Ctrl+Win。采集即将结束……");

        Task.WaitAll(hidTask, rawTask, audioTask);
        HidInterfaceCaptureSummary hid = hidTask.GetAwaiter().GetResult();
        RawCaptureSummary raw = rawTask.GetAwaiter().GetResult();
        AudioLevelSummary audio = audioTask.GetAwaiter().GetResult();
        Console.WriteLine();
        Console.WriteLine("诊断采集完成。");
        Console.WriteLine($"直接 HID 报告：{hid.ReportCount} 条；Raw Input 事件：{raw.EventCount} 条。");
        Console.WriteLine($"音量峰值样本：{audio.SampleCount} 个；有效样本：{audio.ActiveSampleCount} 个；最大峰值：{audio.MaximumPeak:F4}。");
        Console.WriteLine($"结果目录：{directory}");
        Console.WriteLine("请保持这些文件不变，然后告诉 Codex“诊断已完成”。");
        Console.WriteLine("按 Enter 关闭窗口。");
        Console.ReadLine();
        return 0;
    }

    private static TimeSpan ParseDuration(string[] args, int defaultSeconds)
    {
        string? raw = GetOption(args, "--duration");
        if (raw is null)
        {
            return TimeSpan.FromSeconds(defaultSeconds);
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds is <= 0 or > 300)
        {
            throw new ArgumentException("--duration 必须是 0 到 300 之间的秒数。");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static string GetOutputPath(string[] args, string defaultName)
    {
        string raw = GetOption(args, "--output") ?? Path.Combine("artifacts", defaultName);
        return Path.GetFullPath(raw);
    }

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

    private static string SanitizeFileName(string value)
    {
        HashSet<char> invalid = [.. Path.GetInvalidFileNameChars()];
        StringBuilder builder = new(value.Length);
        foreach (char character in value)
        {
            builder.Append(invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character);
        }

        return builder.ToString();
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            HidProbe - Voice Remote Bridge 阶段 0A 只读诊断工具

            用法：
              HidProbe list [--hardware-id VID_1915&PID_1025]
              HidProbe capture [--hardware-id ...] [--label voice] [--duration 10] [--output artifacts/file.jsonl]
              HidProbe hid-list [--hardware-id VID_1915&PID_1025]
              HidProbe hid-capture [--hardware-id ...] [--label voice] [--duration 10] [--output artifacts/file.jsonl]
              HidProbe feature-diagnostic
              HidProbe packet-diagnostic
              HidProbe audio [--name "SG Control Mic"] [--label baseline] [--duration 5] [--output artifacts/file.json]
              HidProbe voice-diagnostic

            capture 只保存指定接收端的 Raw Input 事件，不采集普通电脑键盘。
            hid-capture 只读打开可共享的目标 HID 接口，不发送 Output/Feature Report。
            audio 只读取端点峰值，不录制或保存音频。
            voice-diagnostic 交互式采集一次“松开 → 按住 4 秒 → 松开”的全通道时序。
            """);
    }
}

internal static class JsonOptions
{
    internal static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

internal sealed record RawDeviceDescriptor(
    string DevicePath,
    string DeviceType,
    uint VendorId,
    uint ProductId,
    ushort UsagePage,
    ushort Usage);

internal sealed record RawCaptureSummary(
    string HardwareId,
    string Label,
    string OutputPath,
    DateTimeOffset StartedAt,
    double DurationSeconds,
    int DeviceCount,
    int EventCount,
    IReadOnlyDictionary<string, int> EventsByDevice,
    IReadOnlyDictionary<string, int> EventsByType);

internal sealed class RawCaptureSession : IDisposable
{
    private readonly IReadOnlyList<RawDeviceDescriptor> devices;
    private readonly string hardwareId;
    private readonly string label;
    private readonly string outputPath;
    private readonly StreamWriter writer;
    private readonly Stopwatch stopwatch = new();
    private readonly Dictionary<string, int> eventsByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> eventsByType = new(StringComparer.OrdinalIgnoreCase);
    private readonly object writerLock = new();
    private DateTimeOffset startedAt;
    private int eventCount;
    private bool disposed;

    internal RawCaptureSession(
        IReadOnlyList<RawDeviceDescriptor> devices,
        string hardwareId,
        string label,
        string outputPath)
    {
        this.devices = devices;
        this.hardwareId = hardwareId;
        this.label = label;
        this.outputPath = outputPath;
        writer = new StreamWriter(outputPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    internal RawCaptureSummary Run(TimeSpan duration)
    {
        startedAt = DateTimeOffset.Now;
        stopwatch.Start();
        RawCaptureWindow.Run(devices, duration, HandleRawInput, HandleDeviceChange);
        stopwatch.Stop();

        return new RawCaptureSummary(
            hardwareId,
            label,
            outputPath,
            startedAt,
            stopwatch.Elapsed.TotalSeconds,
            devices.Count,
            eventCount,
            new Dictionary<string, int>(eventsByDevice),
            new Dictionary<string, int>(eventsByType));
    }

    private void HandleRawInput(nint rawInputHandle)
    {
        RawInputPacket? packet = RawInputNative.ReadRawInput(rawInputHandle);
        if (packet is null || !packet.DevicePath.Contains(hardwareId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        object payload = packet.Type switch
        {
            "keyboard" => ParseKeyboard(packet.Payload),
            "hid" => ParseHid(packet.Payload),
            _ => new { rawDataHex = Convert.ToHexString(packet.Payload) }
        };

        WriteEvent(new
        {
            eventType = "rawInput",
            timestamp = DateTimeOffset.Now,
            elapsedMilliseconds = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
            label,
            packet.Type,
            packet.DevicePath,
            payload
        }, packet.DevicePath, packet.Type);
    }

    private void HandleDeviceChange(nint deviceHandle, nuint change)
    {
        string devicePath = RawInputNative.GetDevicePath(deviceHandle) ?? $"handle:0x{deviceHandle:X}";
        if (!devicePath.Contains(hardwareId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string changeName = change == RawInputNative.GidcArrival ? "arrival" : "removal";
        WriteEvent(new
        {
            eventType = "deviceChange",
            timestamp = DateTimeOffset.Now,
            elapsedMilliseconds = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
            label,
            change = changeName,
            devicePath
        }, devicePath, $"device-{changeName}");
    }

    private void WriteEvent(object value, string devicePath, string type)
    {
        lock (writerLock)
        {
            writer.WriteLine(JsonSerializer.Serialize(value, JsonOptions.Compact));
            eventCount++;
            eventsByDevice[devicePath] = eventsByDevice.GetValueOrDefault(devicePath) + 1;
            eventsByType[type] = eventsByType.GetValueOrDefault(type) + 1;
        }
    }

    private static object ParseKeyboard(byte[] bytes)
    {
        if (bytes.Length < 16)
        {
            return new { rawDataHex = Convert.ToHexString(bytes), parseError = "RAWKEYBOARD payload too short" };
        }

        return new
        {
            makeCode = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2)),
            flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2)),
            reserved = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)),
            virtualKey = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2)),
            message = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)),
            extraInformation = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4)),
            rawDataHex = Convert.ToHexString(bytes)
        };
    }

    private static object ParseHid(byte[] bytes)
    {
        if (bytes.Length < 8)
        {
            return new { rawDataHex = Convert.ToHexString(bytes), parseError = "RAWHID payload too short" };
        }

        uint reportSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        uint reportCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        return new
        {
            reportSize,
            reportCount,
            reportsHex = Convert.ToHexString(bytes.AsSpan(8)),
            rawDataHex = Convert.ToHexString(bytes)
        };
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        writer.Dispose();
        disposed = true;
    }
}

internal sealed record RawInputPacket(string Type, string DevicePath, byte[] Payload);

internal static class RawCaptureWindow
{
    private const uint WmInput = 0x00FF;
    private const uint WmInputDeviceChange = 0x00FE;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const int ErrorClassAlreadyExists = 1410;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevDevNotify = 0x00002000;
    private static readonly nint HwndMessage = new(-3);
    private static readonly WindowProcedure WindowProcedureDelegate = WindowProcedure;
    private static RawCaptureWindowState? state;

    internal static void Run(
        IReadOnlyList<RawDeviceDescriptor> devices,
        TimeSpan duration,
        Action<nint> rawInputHandler,
        Action<nint, nuint> deviceChangeHandler)
    {
        string className = $"VoiceRemoteBridge.HidProbe.{Environment.ProcessId}";
        WndClass windowClass = new()
        {
            ClassName = className,
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(WindowProcedureDelegate)
        };

        ushort classAtom = NativeMethods.RegisterClass(ref windowClass);
        int registerError = Marshal.GetLastWin32Error();
        if (classAtom == 0 && registerError != ErrorClassAlreadyExists)
        {
            throw new InvalidOperationException($"RegisterClass 失败，Win32={registerError}。");
        }

        nint window = NativeMethods.CreateWindowEx(
            0,
            className,
            "Voice Remote Bridge HID Probe",
            0,
            0,
            0,
            0,
            0,
            HwndMessage,
            nint.Zero,
            nint.Zero,
            nint.Zero);
        if (window == nint.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx 失败，Win32={Marshal.GetLastWin32Error()}。");
        }

        state = new RawCaptureWindowState(rawInputHandler, deviceChangeHandler);
        RegisterDevices(devices, window);

        using Timer timer = new(
            _ => NativeMethods.PostMessage(window, WmClose, nuint.Zero, nint.Zero),
            null,
            duration,
            Timeout.InfiniteTimeSpan);

        while (true)
        {
            int result = NativeMethods.GetMessage(out Message message, nint.Zero, 0, 0);
            if (result == -1)
            {
                throw new InvalidOperationException($"GetMessage 失败，Win32={Marshal.GetLastWin32Error()}。");
            }

            if (result == 0)
            {
                break;
            }

            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessage(ref message);
        }

        state = null;
    }

    private static void RegisterDevices(IReadOnlyList<RawDeviceDescriptor> devices, nint window)
    {
        RawInputDevice[] registrations = devices
            .Where(device => device.UsagePage != 0 && device.Usage != 0)
            .Select(device => new RawInputDevice
            {
                UsagePage = device.UsagePage,
                Usage = device.Usage,
                Flags = RidevInputSink | RidevDevNotify,
                TargetWindow = window
            })
            .DistinctBy(device => (device.UsagePage, device.Usage))
            .ToArray();

        if (registrations.Length == 0)
        {
            throw new InvalidOperationException("目标设备没有可注册的 Raw Input Usage。");
        }

        if (!NativeMethods.RegisterRawInputDevices(
            registrations,
            (uint)registrations.Length,
            (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            throw new InvalidOperationException($"RegisterRawInputDevices 失败，Win32={Marshal.GetLastWin32Error()}。");
        }
    }

    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == WmInput)
        {
            state?.RawInputHandler(lParam);
        }
        else if (message == WmInputDeviceChange)
        {
            state?.DeviceChangeHandler(lParam, wParam);
        }
        else if (message == WmClose)
        {
            NativeMethods.DestroyWindow(window);
            return nint.Zero;
        }
        else if (message == WmDestroy)
        {
            NativeMethods.PostQuitMessage(0);
            return nint.Zero;
        }

        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }

    private sealed record RawCaptureWindowState(
        Action<nint> RawInputHandler,
        Action<nint, nuint> DeviceChangeHandler);
}

internal static class RawInputNative
{
    internal const nuint GidcArrival = 1;
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RidiDeviceInfo = 0x2000000B;
    private const uint RimTypeMouse = 0;
    private const uint RimTypeKeyboard = 1;
    private const uint RimTypeHid = 2;
    private const uint InvalidUInt = uint.MaxValue;

    internal static IReadOnlyList<RawDeviceDescriptor> EnumerateDevices(string hardwareId)
    {
        uint count = 0;
        uint listItemSize = (uint)Marshal.SizeOf<RawInputDeviceList>();
        uint firstResult = NativeMethods.GetRawInputDeviceList(nint.Zero, ref count, listItemSize);
        if (firstResult == InvalidUInt)
        {
            throw new InvalidOperationException($"GetRawInputDeviceList(count) 失败，Win32={Marshal.GetLastWin32Error()}。");
        }

        if (count == 0)
        {
            return [];
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)(count * listItemSize)));
        try
        {
            uint actualCount = count;
            uint result = NativeMethods.GetRawInputDeviceList(buffer, ref actualCount, listItemSize);
            if (result == InvalidUInt)
            {
                throw new InvalidOperationException($"GetRawInputDeviceList(data) 失败，Win32={Marshal.GetLastWin32Error()}。");
            }

            List<RawDeviceDescriptor> devices = [];
            for (uint index = 0; index < actualCount; index++)
            {
                nint itemPointer = buffer + checked((int)(index * listItemSize));
                RawInputDeviceList item = Marshal.PtrToStructure<RawInputDeviceList>(itemPointer);
                string? path = GetDevicePath(item.DeviceHandle);
                if (path is null || !path.Contains(hardwareId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                RawDeviceInfo info = GetDeviceInfo(item.DeviceHandle);
                (uint vendorId, uint productId) = ParseVendorProduct(path, info);
                (ushort usagePage, ushort usage) = GetUsage(item.Type, info);
                devices.Add(new RawDeviceDescriptor(
                    path,
                    GetTypeName(item.Type),
                    vendorId,
                    productId,
                    usagePage,
                    usage));
            }

            return devices;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static string? GetDevicePath(nint deviceHandle)
    {
        uint characterCount = 0;
        uint firstResult = NativeMethods.GetRawInputDeviceInfo(deviceHandle, RidiDeviceName, nint.Zero, ref characterCount);
        if (firstResult == InvalidUInt || characterCount == 0)
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)((characterCount + 1) * sizeof(char))));
        try
        {
            uint capacity = characterCount;
            uint result = NativeMethods.GetRawInputDeviceInfo(deviceHandle, RidiDeviceName, buffer, ref capacity);
            return result == InvalidUInt
                ? null
                : Marshal.PtrToStringUni(buffer, checked((int)capacity))?.TrimEnd('\0');
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static RawInputPacket? ReadRawInput(nint rawInputHandle)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        uint firstResult = NativeMethods.GetRawInputData(rawInputHandle, RidInput, nint.Zero, ref size, headerSize);
        if (firstResult == InvalidUInt || size < headerSize)
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            uint capacity = size;
            uint result = NativeMethods.GetRawInputData(rawInputHandle, RidInput, buffer, ref capacity, headerSize);
            if (result == InvalidUInt || result < headerSize)
            {
                return null;
            }

            RawInputHeader header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            string? path = GetDevicePath(header.DeviceHandle);
            if (path is null)
            {
                return null;
            }

            int payloadSize = checked((int)result - (int)headerSize);
            byte[] payload = new byte[payloadSize];
            Marshal.Copy(buffer + (int)headerSize, payload, 0, payloadSize);
            return new RawInputPacket(GetTypeName(header.Type), path, payload);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static RawDeviceInfo GetDeviceInfo(nint handle)
    {
        uint size = (uint)Marshal.SizeOf<RawDeviceInfo>();
        RawDeviceInfo value = new() { Size = size };
        nint buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            Marshal.StructureToPtr(value, buffer, fDeleteOld: false);
            uint capacity = size;
            uint result = NativeMethods.GetRawInputDeviceInfo(handle, RidiDeviceInfo, buffer, ref capacity);
            if (result == InvalidUInt)
            {
                throw new InvalidOperationException($"GetRawInputDeviceInfo 失败，Win32={Marshal.GetLastWin32Error()}。");
            }

            return Marshal.PtrToStructure<RawDeviceInfo>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (uint VendorId, uint ProductId) ParseVendorProduct(string path, RawDeviceInfo info)
    {
        if (info.Type == RimTypeHid)
        {
            return (info.Union.Hid.VendorId, info.Union.Hid.ProductId);
        }

        uint vendorId = ParseHexComponent(path, "VID_");
        uint productId = ParseHexComponent(path, "PID_");
        return (vendorId, productId);
    }

    private static uint ParseHexComponent(string path, string marker)
    {
        int start = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || start + marker.Length + 4 > path.Length)
        {
            return 0;
        }

        ReadOnlySpan<char> value = path.AsSpan(start + marker.Length, 4);
        return uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed) ? parsed : 0;
    }

    private static (ushort UsagePage, ushort Usage) GetUsage(uint type, RawDeviceInfo info) => type switch
    {
        RimTypeMouse => (0x01, 0x02),
        RimTypeKeyboard => (0x01, 0x06),
        RimTypeHid => (info.Union.Hid.UsagePage, info.Union.Hid.Usage),
        _ => (0, 0)
    };

    private static string GetTypeName(uint type) => type switch
    {
        RimTypeMouse => "mouse",
        RimTypeKeyboard => "keyboard",
        RimTypeHid => "hid",
        _ => $"unknown-{type}"
    };
}

internal sealed record HidInterfaceDescriptor(
    string DevicePath,
    uint VendorId,
    uint ProductId,
    ushort VersionNumber,
    ushort UsagePage,
    ushort Usage,
    ushort InputReportByteLength,
    ushort OutputReportByteLength,
    ushort FeatureReportByteLength,
    ushort FeatureButtonCapCount,
    ushort FeatureValueCapCount,
    IReadOnlyList<byte> FeatureReportIds,
    bool CanOpenForRead,
    int ReadOpenError);

internal sealed record HidInterfaceCaptureSummary(
    string HardwareId,
    string Label,
    string OutputPath,
    DateTimeOffset StartedAt,
    double DurationSeconds,
    int InterfaceCount,
    int ReadableInterfaceCount,
    int ReportCount,
    IReadOnlyDictionary<string, int> ReportsByInterface,
    IReadOnlyList<string> Errors);

internal static class HidInterfaceCapture
{
    internal static async Task<HidInterfaceCaptureSummary> RunAsync(
        string hardwareId,
        string label,
        TimeSpan duration,
        string outputPath)
    {
        IReadOnlyList<HidInterfaceDescriptor> interfaces = HidInterfaceNative.Enumerate(hardwareId);
        HidInterfaceDescriptor[] readable = interfaces
            .Where(item => item.CanOpenForRead && item.InputReportByteLength > 0)
            .ToArray();

        DateTimeOffset startedAt = DateTimeOffset.Now;
        Stopwatch stopwatch = Stopwatch.StartNew();
        Dictionary<string, int> reportsByInterface = new(StringComparer.OrdinalIgnoreCase);
        List<string> errors = [];
        int reportCount = 0;
        object outputLock = new();
        using StreamWriter writer = new(outputPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        using CancellationTokenSource cancellation = new(duration);

        Task[] tasks = readable
            .Select(item => ReadLoopAsync(item, label, stopwatch, writer, outputLock, reportsByInterface, errors, cancellation.Token, () => Interlocked.Increment(ref reportCount)))
            .ToArray();

        if (tasks.Length > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        else
        {
            await Task.Delay(duration).ConfigureAwait(false);
        }

        stopwatch.Stop();
        return new HidInterfaceCaptureSummary(
            hardwareId,
            label,
            outputPath,
            startedAt,
            stopwatch.Elapsed.TotalSeconds,
            interfaces.Count,
            readable.Length,
            reportCount,
            reportsByInterface,
            errors);
    }

    private static async Task ReadLoopAsync(
        HidInterfaceDescriptor descriptor,
        string label,
        Stopwatch stopwatch,
        StreamWriter writer,
        object outputLock,
        Dictionary<string, int> reportsByInterface,
        List<string> errors,
        CancellationToken cancellationToken,
        Action incrementReportCount)
    {
        using SafeFileHandle handle = HidInterfaceNative.OpenForRead(descriptor.DevicePath);
        if (handle.IsInvalid)
        {
            lock (outputLock)
            {
                errors.Add($"{descriptor.DevicePath}: CreateFile Win32={Marshal.GetLastWin32Error()}");
            }

            return;
        }

        try
        {
            using FileStream stream = new(handle, FileAccess.Read, descriptor.InputReportByteLength, isAsync: true);
            byte[] buffer = new byte[descriptor.InputReportByteLength];
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                string hex = Convert.ToHexString(buffer.AsSpan(0, bytesRead));
                object record = new
                {
                    eventType = "hidInputReport",
                    timestamp = DateTimeOffset.Now,
                    elapsedMilliseconds = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
                    label,
                    descriptor.DevicePath,
                    descriptor.UsagePage,
                    descriptor.Usage,
                    reportLength = bytesRead,
                    reportHex = hex
                };

                lock (outputLock)
                {
                    writer.WriteLine(JsonSerializer.Serialize(record, JsonOptions.Compact));
                    reportsByInterface[descriptor.DevicePath] = reportsByInterface.GetValueOrDefault(descriptor.DevicePath) + 1;
                    incrementReportCount();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lock (outputLock)
            {
                errors.Add($"{descriptor.DevicePath}: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }
}

internal static class HidInterfaceNative
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const int ErrorNoMoreItems = 259;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int HidpStatusSuccess = 0x00110000;

    internal static IReadOnlyList<HidInterfaceDescriptor> Enumerate(string hardwareId)
    {
        NativeMethods.HidDGetHidGuid(out Guid hidGuid);
        nint deviceInfoSet = NativeMethods.SetupDiGetClassDevs(
            ref hidGuid,
            nint.Zero,
            nint.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (deviceInfoSet == new nint(-1))
        {
            throw new InvalidOperationException($"SetupDiGetClassDevs 失败，Win32={Marshal.GetLastWin32Error()}。");
        }

        try
        {
            List<HidInterfaceDescriptor> result = [];
            for (uint index = 0; ; index++)
            {
                DeviceInterfaceData interfaceData = new()
                {
                    Size = (uint)Marshal.SizeOf<DeviceInterfaceData>()
                };
                if (!NativeMethods.SetupDiEnumDeviceInterfaces(
                    deviceInfoSet,
                    nint.Zero,
                    ref hidGuid,
                    index,
                    ref interfaceData))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                    {
                        break;
                    }

                    throw new InvalidOperationException($"SetupDiEnumDeviceInterfaces 失败，Win32={error}。");
                }

                string path = GetInterfacePath(deviceInfoSet, ref interfaceData);
                if (!path.Contains(hardwareId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(Inspect(path));
            }

            return result;
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    internal static SafeFileHandle OpenForRead(string path) => NativeMethods.CreateFile(
        path,
        GenericRead,
        FileShareRead | FileShareWrite,
        nint.Zero,
        OpenExisting,
        FileFlagOverlapped,
        nint.Zero);

    internal static SafeFileHandle OpenForFeatureRead(string path) => NativeMethods.CreateFile(
        path,
        GenericRead,
        FileShareRead | FileShareWrite,
        nint.Zero,
        OpenExisting,
        0,
        nint.Zero);

    private static string GetInterfacePath(nint deviceInfoSet, ref DeviceInterfaceData interfaceData)
    {
        NativeMethods.SetupDiGetDeviceInterfaceDetail(
            deviceInfoSet,
            ref interfaceData,
            nint.Zero,
            0,
            out uint requiredSize,
            nint.Zero);
        if (requiredSize == 0)
        {
            throw new InvalidOperationException($"SetupDiGetDeviceInterfaceDetail(size) 失败，Win32={Marshal.GetLastWin32Error()}。");
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)requiredSize));
        try
        {
            Marshal.WriteInt32(buffer, nint.Size == 8 ? 8 : 6);
            if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(
                deviceInfoSet,
                ref interfaceData,
                buffer,
                requiredSize,
                out _,
                nint.Zero))
            {
                throw new InvalidOperationException($"SetupDiGetDeviceInterfaceDetail(data) 失败，Win32={Marshal.GetLastWin32Error()}。");
            }

            return Marshal.PtrToStringUni(buffer + sizeof(uint)) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static HidInterfaceDescriptor Inspect(string path)
    {
        using SafeFileHandle metadataHandle = NativeMethods.CreateFile(
            path,
            0,
            FileShareRead | FileShareWrite,
            nint.Zero,
            OpenExisting,
            0,
            nint.Zero);
        if (metadataHandle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            return new HidInterfaceDescriptor(path, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], false, error);
        }

        HidAttributes attributes = new() { Size = Marshal.SizeOf<HidAttributes>() };
        if (!NativeMethods.HidDGetAttributes(metadataHandle, ref attributes))
        {
            throw new InvalidOperationException($"HidD_GetAttributes 失败：{path}，Win32={Marshal.GetLastWin32Error()}。");
        }

        if (!NativeMethods.HidDGetPreparsedData(metadataHandle, out nint preparsedData))
        {
            throw new InvalidOperationException($"HidD_GetPreparsedData 失败：{path}，Win32={Marshal.GetLastWin32Error()}。");
        }

        HidCaps caps;
        IReadOnlyList<byte> featureReportIds;
        try
        {
            int status = NativeMethods.HidPGetCaps(preparsedData, out caps);
            if (status != HidpStatusSuccess)
            {
                throw new InvalidOperationException($"HidP_GetCaps 失败：{path}，NTSTATUS=0x{status:X8}。");
            }

            featureReportIds = GetFeatureReportIds(preparsedData, caps, path);
        }
        finally
        {
            NativeMethods.HidDFreePreparsedData(preparsedData);
        }

        using SafeFileHandle readHandle = OpenForRead(path);
        int readError = readHandle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        return new HidInterfaceDescriptor(
            path,
            attributes.VendorId,
            attributes.ProductId,
            attributes.VersionNumber,
            caps.UsagePage,
            caps.Usage,
            caps.InputReportByteLength,
            caps.OutputReportByteLength,
            caps.FeatureReportByteLength,
            caps.NumberFeatureButtonCaps,
            caps.NumberFeatureValueCaps,
            featureReportIds,
            !readHandle.IsInvalid,
            readError);
    }

    private static IReadOnlyList<byte> GetFeatureReportIds(nint preparsedData, HidCaps caps, string path)
    {
        HashSet<byte> reportIds = [];
        if (caps.NumberFeatureValueCaps > 0)
        {
            HidValueCaps[] valueCaps = new HidValueCaps[caps.NumberFeatureValueCaps];
            ushort valueCapsLength = caps.NumberFeatureValueCaps;
            int status = NativeMethods.HidPGetValueCaps(
                HidReportType.Feature,
                valueCaps,
                ref valueCapsLength,
                preparsedData);
            if (status != HidpStatusSuccess)
            {
                throw new InvalidOperationException($"HidP_GetValueCaps 失败：{path}，NTSTATUS=0x{status:X8}。");
            }

            foreach (HidValueCaps capability in valueCaps.AsSpan(0, valueCapsLength))
            {
                reportIds.Add(capability.ReportId);
            }
        }

        if (caps.NumberFeatureButtonCaps > 0)
        {
            HidButtonCaps[] buttonCaps = new HidButtonCaps[caps.NumberFeatureButtonCaps];
            ushort buttonCapsLength = caps.NumberFeatureButtonCaps;
            int status = NativeMethods.HidPGetButtonCaps(
                HidReportType.Feature,
                buttonCaps,
                ref buttonCapsLength,
                preparsedData);
            if (status != HidpStatusSuccess)
            {
                throw new InvalidOperationException($"HidP_GetButtonCaps 失败：{path}，NTSTATUS=0x{status:X8}。");
            }

            foreach (HidButtonCaps capability in buttonCaps.AsSpan(0, buttonCapsLength))
            {
                reportIds.Add(capability.ReportId);
            }
        }

        return reportIds.Order().ToArray();
    }
}

internal sealed record AudioLevelSummary(
    string EndpointName,
    string EndpointId,
    string Label,
    DateTimeOffset StartedAt,
    double DurationSeconds,
    int SampleCount,
    int ActiveSampleCount,
    float MaximumPeak,
    double AveragePeak);

internal sealed record AudioLevelSample(
    string EventType,
    DateTimeOffset Timestamp,
    double ElapsedMilliseconds,
    float Peak);

internal static class AudioMeterProbe
{
    private const uint DeviceStateActive = 0x00000001;
    private const uint StgmRead = 0;
    private const uint ClsctxAll = 23;
    private const ushort VtLpwstr = 31;
    private static readonly Guid AudioMeterInterfaceId = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    private static readonly PropertyKey DeviceFriendlyName = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        14);

    internal static AudioLevelSummary Measure(
        string requestedName,
        string label,
        TimeSpan duration,
        TimeSpan interval,
        Action<AudioLevelSample>? sampleObserver = null)
    {
        int initializeResult = NativeMethods.CoInitializeEx(nint.Zero, 0x2);
        bool uninitialize = initializeResult >= 0;
        if (initializeResult < 0 && initializeResult != unchecked((int)0x80010106))
        {
            Marshal.ThrowExceptionForHR(initializeResult);
        }

        object? meterObject = null;
        IMMDevice? selectedDevice = null;
        IMMDeviceCollection? collection = null;
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(DataFlow.Capture, DeviceStateActive, out collection));
            Marshal.ThrowExceptionForHR(collection.GetCount(out uint count));

            string? selectedName = null;
            for (uint index = 0; index < count; index++)
            {
                Marshal.ThrowExceptionForHR(collection.Item(index, out IMMDevice candidate));
                string friendlyName = GetFriendlyName(candidate);
                if (friendlyName.Contains(requestedName, StringComparison.OrdinalIgnoreCase))
                {
                    selectedDevice = candidate;
                    selectedName = friendlyName;
                    break;
                }

                Marshal.FinalReleaseComObject(candidate);
            }

            if (selectedDevice is null || selectedName is null)
            {
                throw new InvalidOperationException($"未找到活动录音端点：{requestedName}。");
            }

            Marshal.ThrowExceptionForHR(selectedDevice.GetId(out string endpointId));
            Guid interfaceId = AudioMeterInterfaceId;
            Marshal.ThrowExceptionForHR(selectedDevice.Activate(ref interfaceId, ClsctxAll, nint.Zero, out meterObject));
            IAudioMeterInformation meter = (IAudioMeterInformation)meterObject;

            DateTimeOffset startedAt = DateTimeOffset.Now;
            Stopwatch stopwatch = Stopwatch.StartNew();
            int sampleCount = 0;
            int activeSamples = 0;
            float maximum = 0;
            double total = 0;
            while (stopwatch.Elapsed < duration)
            {
                Marshal.ThrowExceptionForHR(meter.GetPeakValue(out float peak));
                sampleObserver?.Invoke(new AudioLevelSample(
                    "audioPeak",
                    DateTimeOffset.Now,
                    Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
                    peak));
                maximum = Math.Max(maximum, peak);
                total += peak;
                sampleCount++;
                if (peak > 0.001f)
                {
                    activeSamples++;
                }

                Thread.Sleep(interval);
            }

            stopwatch.Stop();
            return new AudioLevelSummary(
                selectedName,
                endpointId,
                label,
                startedAt,
                stopwatch.Elapsed.TotalSeconds,
                sampleCount,
                activeSamples,
                maximum,
                sampleCount == 0 ? 0 : total / sampleCount);
        }
        finally
        {
            if (meterObject is not null && Marshal.IsComObject(meterObject))
            {
                Marshal.FinalReleaseComObject(meterObject);
            }

            if (selectedDevice is not null)
            {
                Marshal.FinalReleaseComObject(selectedDevice);
            }

            if (collection is not null)
            {
                Marshal.FinalReleaseComObject(collection);
            }

            if (enumerator is not null)
            {
                Marshal.FinalReleaseComObject(enumerator);
            }

            if (uninitialize)
            {
                NativeMethods.CoUninitialize();
            }
        }
    }

    internal static string GetFriendlyName(IMMDevice device)
    {
        Marshal.ThrowExceptionForHR(device.OpenPropertyStore(StgmRead, out IPropertyStore store));
        try
        {
            PropertyKey key = DeviceFriendlyName;
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out PropVariant value));
            try
            {
                return value.VarType == VtLpwstr && value.PointerValue != nint.Zero
                    ? Marshal.PtrToStringUni(value.PointerValue) ?? string.Empty
                    : string.Empty;
            }
            finally
            {
                NativeMethods.PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(store);
        }
    }
}

internal static class AudioPacketProbe
{
    internal static int RunInteractive()
    {
        if (Process.GetProcessesByName("VoiceRemoteBridge.App").Length > 0)
        {
            Console.Error.WriteLine("请先在 Voice Remote Bridge 中点击“停止桥接”和“退出”，再运行本诊断工具。");
            return 2;
        }

        string directory = Path.GetFullPath(Path.Combine(
            "artifacts",
            $"audio-packet-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}"));
        Directory.CreateDirectory(directory);
        string packetsOutput = Path.Combine(directory, "packet-metrics.jsonl");
        string summaryOutput = Path.Combine(directory, "summary.json");

        Console.WriteLine("Voice Remote Bridge - WASAPI 原始包状态诊断");
        Console.WriteLine("本工具打开 SG Control Mic 的共享模式捕获流，只记录每个包的状态和数值统计。");
        Console.WriteLine("音频字节只在内存中计算后立即丢弃；不保存音频、不保存语音内容、不做转写、不上传数据。");
        Console.WriteLine("测试不会启动微信语音输入，也不会修改系统默认麦克风。");
        Console.WriteLine();
        Console.Write("确保遥控器语音键处于松开状态，然后按 Enter 开始：");
        Console.ReadLine();

        using AudioPacketCaptureSession session = new("SG Control Mic", packetsOutput);
        session.SetPhase("baseline-released");
        session.Start();
        try
        {
            Thread.Sleep(TimeSpan.FromSeconds(3));

            session.SetPhase("press-transition-1");
            Console.Write(">>> 按住遥控器语音键并保持安静；按稳后用另一只手按 Enter：");
            Console.ReadLine();
            session.SetPhase("held-silent-1");
            Thread.Sleep(TimeSpan.FromSeconds(4));

            session.SetPhase("held-speech-1");
            Console.Write(">>> 继续按住遥控器，对着它持续说“啊——”约 2 秒；说完按 Enter，但先别松遥控器：");
            Console.ReadLine();
            session.SetPhase("held-silent-after-speech-1");
            Thread.Sleep(TimeSpan.FromSeconds(2));

            session.SetPhase("release-transition-1");
            Console.Write(">>> 现在松开遥控器语音键；松开后按 Enter：");
            Console.ReadLine();
            session.SetPhase("released-1");
            Thread.Sleep(TimeSpan.FromSeconds(5));

            session.SetPhase("press-transition-2");
            Console.Write(">>> 第二轮只测静默：再次按住语音键，不要说话；按稳后按 Enter：");
            Console.ReadLine();
            session.SetPhase("held-silent-2");
            Thread.Sleep(TimeSpan.FromSeconds(4));

            session.SetPhase("release-transition-2");
            Console.Write(">>> 松开语音键；松开后按 Enter：");
            Console.ReadLine();
            session.SetPhase("released-2");
            Thread.Sleep(TimeSpan.FromSeconds(5));
        }
        finally
        {
            session.Stop();
        }

        if (session.Failure is not null)
        {
            throw new InvalidOperationException("WASAPI 捕获线程失败。", session.Failure);
        }

        object summary = session.CreateSummary();
        File.WriteAllText(summaryOutput, JsonSerializer.Serialize(summary, JsonOptions.Indented), new UTF8Encoding(false));

        Console.WriteLine();
        Console.WriteLine("WASAPI 原始包状态诊断已完成。");
        Console.WriteLine($"端点：{session.EndpointName}");
        Console.WriteLine($"格式：{session.FormatDescription}");
        Console.WriteLine($"包数：{session.PacketCount}；结果目录：{directory}");
        Console.WriteLine("请保持这些文件不变，然后告诉 Codex“原始包诊断已完成”。");
        Console.WriteLine("按 Enter 关闭窗口。");
        Console.ReadLine();
        return session.PacketCount > 0 ? 0 : 3;
    }
}

internal sealed class AudioPacketCaptureSession : IDisposable
{
    private const uint DeviceStateActive = 0x00000001;
    private const uint ClsctxAll = 23;
    private const long RequestedBufferDuration = 10_000_000;
    private static readonly Guid AudioClientInterfaceId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid AudioCaptureClientInterfaceId = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    private readonly string _requestedEndpointName;
    private readonly string _outputPath;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Dictionary<string, AudioPacketPhaseAccumulator> _phaseAccumulators = new(StringComparer.Ordinal);
    private string _phase = "initializing";
    private Thread? _thread;
    private DateTimeOffset _startedAt;
    private Stopwatch? _stopwatch;
    private int _disposed;

    internal AudioPacketCaptureSession(string requestedEndpointName, string outputPath)
    {
        _requestedEndpointName = requestedEndpointName;
        _outputPath = outputPath;
    }

    internal string EndpointName { get; private set; } = string.Empty;

    internal string EndpointId { get; private set; } = string.Empty;

    internal string FormatDescription { get; private set; } = string.Empty;

    internal int PacketCount { get; private set; }

    internal Exception? Failure { get; private set; }

    internal void SetPhase(string phase) => Volatile.Write(ref _phase, phase);

    internal void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("音频包诊断已经启动。");
        }

        _thread = new Thread(CaptureThread)
        {
            IsBackground = true,
            Name = "VoiceRemoteBridge.AudioPacketProbe"
        };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("等待 WASAPI 捕获流启动超时。");
        }

        if (Failure is not null)
        {
            throw new InvalidOperationException("WASAPI 捕获流启动失败。", Failure);
        }
    }

    internal void Stop()
    {
        _cancellation.Cancel();
        if (_thread is not null && !_thread.Join(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("等待 WASAPI 捕获线程退出超时。");
        }
    }

    internal object CreateSummary()
    {
        Dictionary<string, object> phases = _phaseAccumulators
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                item => item.Key,
                item => (object)item.Value.ToSummary(),
                StringComparer.Ordinal);
        return new
        {
            endpointName = EndpointName,
            endpointId = EndpointId,
            format = FormatDescription,
            startedAt = _startedAt,
            durationSeconds = _stopwatch?.Elapsed.TotalSeconds ?? 0,
            packetCount = PacketCount,
            phases,
            failure = Failure?.ToString()
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
        _cancellation.Dispose();
    }

    private void CaptureThread()
    {
        int initializeResult = NativeMethods.CoInitializeEx(nint.Zero, 0);
        bool uninitialize = initializeResult >= 0;
        object? audioClientObject = null;
        object? captureClientObject = null;
        IMMDevice? selectedDevice = null;
        IMMDeviceCollection? collection = null;
        IMMDeviceEnumerator? enumerator = null;
        nint formatPointer = nint.Zero;
        bool audioStarted = false;
        try
        {
            if (initializeResult < 0 && initializeResult != unchecked((int)0x80010106))
            {
                Marshal.ThrowExceptionForHR(initializeResult);
            }

            enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(DataFlow.Capture, DeviceStateActive, out collection));
            Marshal.ThrowExceptionForHR(collection.GetCount(out uint count));
            for (uint index = 0; index < count; index++)
            {
                Marshal.ThrowExceptionForHR(collection.Item(index, out IMMDevice candidate));
                string friendlyName = AudioMeterProbe.GetFriendlyName(candidate);
                if (friendlyName.Contains(_requestedEndpointName, StringComparison.OrdinalIgnoreCase))
                {
                    selectedDevice = candidate;
                    EndpointName = friendlyName;
                    break;
                }

                Marshal.FinalReleaseComObject(candidate);
            }

            if (selectedDevice is null)
            {
                throw new InvalidOperationException($"未找到活动录音端点：{_requestedEndpointName}。");
            }

            Marshal.ThrowExceptionForHR(selectedDevice.GetId(out string endpointId));
            EndpointId = endpointId;
            Guid audioClientId = AudioClientInterfaceId;
            Marshal.ThrowExceptionForHR(selectedDevice.Activate(ref audioClientId, ClsctxAll, nint.Zero, out audioClientObject));
            IAudioClient audioClient = (IAudioClient)audioClientObject;
            Marshal.ThrowExceptionForHR(audioClient.GetMixFormat(out formatPointer));
            AudioWaveFormat format = AudioWaveFormat.Parse(formatPointer);
            FormatDescription = format.ToString();
            Marshal.ThrowExceptionForHR(audioClient.Initialize(
                AudioClientShareMode.Shared,
                0,
                RequestedBufferDuration,
                0,
                formatPointer,
                nint.Zero));
            Marshal.ThrowExceptionForHR(audioClient.GetBufferSize(out uint bufferFrameCount));
            Guid captureClientId = AudioCaptureClientInterfaceId;
            Marshal.ThrowExceptionForHR(audioClient.GetService(ref captureClientId, out captureClientObject));
            IAudioCaptureClient captureClient = (IAudioCaptureClient)captureClientObject;

            _startedAt = DateTimeOffset.Now;
            _stopwatch = Stopwatch.StartNew();
            using StreamWriter writer = new(_outputPath, append: false, new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            Marshal.ThrowExceptionForHR(audioClient.Start());
            audioStarted = true;
            _ready.Set();

            while (!_cancellation.IsCancellationRequested)
            {
                Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out uint nextPacketFrames));
                while (nextPacketFrames > 0)
                {
                    int getBufferResult = captureClient.GetBuffer(
                        out nint data,
                        out uint frames,
                        out AudioClientBufferFlags flags,
                        out ulong devicePosition,
                        out ulong qpcPosition);
                    Marshal.ThrowExceptionForHR(getBufferResult);
                    AudioPacketStatistics statistics;
                    try
                    {
                        statistics = AudioPacketStatistics.Calculate(data, frames, flags, format);
                    }
                    finally
                    {
                        Marshal.ThrowExceptionForHR(captureClient.ReleaseBuffer(frames));
                    }

                    string phase = Volatile.Read(ref _phase);
                    if (!_phaseAccumulators.TryGetValue(phase, out AudioPacketPhaseAccumulator? accumulator))
                    {
                        accumulator = new AudioPacketPhaseAccumulator();
                        _phaseAccumulators.Add(phase, accumulator);
                    }

                    accumulator.Add(frames, flags, statistics);
                    PacketCount++;
                    object record = new
                    {
                        eventType = "audioCapturePacketMetrics",
                        timestamp = DateTimeOffset.Now,
                        elapsedMilliseconds = Math.Round(_stopwatch.Elapsed.TotalMilliseconds, 3),
                        phase,
                        frames,
                        flags = (uint)flags,
                        silent = flags.HasFlag(AudioClientBufferFlags.Silent),
                        dataDiscontinuity = flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity),
                        timestampError = flags.HasFlag(AudioClientBufferFlags.TimestampError),
                        devicePosition,
                        qpcPosition,
                        bufferFrameCount,
                        statistics.ByteCount,
                        statistics.NonZeroByteCount,
                        nonZeroByteRatio = Math.Round(statistics.NonZeroByteRatio, 9),
                        meanAbsolute = Math.Round(statistics.MeanAbsolute, 9),
                        rms = Math.Round(statistics.Rms, 9),
                        peakAbsolute = Math.Round(statistics.PeakAbsolute, 9)
                    };
                    writer.WriteLine(JsonSerializer.Serialize(record, JsonOptions.Compact));
                    Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out nextPacketFrames));
                }

                Thread.Sleep(TimeSpan.FromMilliseconds(5));
            }

            _stopwatch.Stop();
        }
        catch (Exception exception)
        {
            Failure = exception;
        }
        finally
        {
            _ready.Set();
            if (audioStarted && audioClientObject is IAudioClient audioClient)
            {
                audioClient.Stop();
            }

            if (formatPointer != nint.Zero)
            {
                Marshal.FreeCoTaskMem(formatPointer);
            }

            if (captureClientObject is not null && Marshal.IsComObject(captureClientObject))
            {
                Marshal.FinalReleaseComObject(captureClientObject);
            }

            if (audioClientObject is not null && Marshal.IsComObject(audioClientObject))
            {
                Marshal.FinalReleaseComObject(audioClientObject);
            }

            if (selectedDevice is not null)
            {
                Marshal.FinalReleaseComObject(selectedDevice);
            }

            if (collection is not null)
            {
                Marshal.FinalReleaseComObject(collection);
            }

            if (enumerator is not null)
            {
                Marshal.FinalReleaseComObject(enumerator);
            }

            if (uninitialize)
            {
                NativeMethods.CoUninitialize();
            }
        }
    }
}

internal sealed class AudioPacketPhaseAccumulator
{
    private long _totalBytes;
    private long _totalNonZeroBytes;
    private double _rmsTotal;
    private double _meanAbsoluteTotal;

    internal int PacketCount { get; private set; }

    internal long FrameCount { get; private set; }

    internal int SilentPacketCount { get; private set; }

    internal int DataDiscontinuityCount { get; private set; }

    internal double MaximumPeakAbsolute { get; private set; }

    internal void Add(uint frames, AudioClientBufferFlags flags, AudioPacketStatistics statistics)
    {
        PacketCount++;
        FrameCount += frames;
        _totalBytes += statistics.ByteCount;
        _totalNonZeroBytes += statistics.NonZeroByteCount;
        _rmsTotal += statistics.Rms;
        _meanAbsoluteTotal += statistics.MeanAbsolute;
        MaximumPeakAbsolute = Math.Max(MaximumPeakAbsolute, statistics.PeakAbsolute);
        if (flags.HasFlag(AudioClientBufferFlags.Silent))
        {
            SilentPacketCount++;
        }

        if (flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity))
        {
            DataDiscontinuityCount++;
        }
    }

    internal object ToSummary() => new
    {
        packetCount = PacketCount,
        frameCount = FrameCount,
        silentPacketCount = SilentPacketCount,
        silentPacketRatio = PacketCount == 0 ? 0 : (double)SilentPacketCount / PacketCount,
        dataDiscontinuityCount = DataDiscontinuityCount,
        nonZeroByteRatio = _totalBytes == 0 ? 0 : (double)_totalNonZeroBytes / _totalBytes,
        averageMeanAbsolute = PacketCount == 0 ? 0 : _meanAbsoluteTotal / PacketCount,
        averageRms = PacketCount == 0 ? 0 : _rmsTotal / PacketCount,
        maximumPeakAbsolute = MaximumPeakAbsolute
    };
}

internal readonly record struct AudioPacketStatistics(
    int ByteCount,
    int NonZeroByteCount,
    double NonZeroByteRatio,
    double MeanAbsolute,
    double Rms,
    double PeakAbsolute)
{
    internal static unsafe AudioPacketStatistics Calculate(
        nint data,
        uint frames,
        AudioClientBufferFlags flags,
        AudioWaveFormat format)
    {
        int byteCount = checked((int)(frames * format.BlockAlign));
        if (byteCount == 0 || data == nint.Zero || flags.HasFlag(AudioClientBufferFlags.Silent))
        {
            return new AudioPacketStatistics(byteCount, 0, 0, 0, 0, 0);
        }

        ReadOnlySpan<byte> bytes = new((void*)data, byteCount);
        int nonZeroBytes = 0;
        foreach (byte value in bytes)
        {
            if (value != 0)
            {
                nonZeroBytes++;
            }
        }

        double meanAbsolute = 0;
        double rms = 0;
        double peak = 0;
        if (format.Encoding == AudioSampleEncoding.Float32)
        {
            ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(bytes);
            CalculateNormalizedStatistics(samples, out meanAbsolute, out rms, out peak);
        }
        else if (format.Encoding == AudioSampleEncoding.Pcm16)
        {
            ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(bytes);
            CalculateNormalizedStatistics(samples, out meanAbsolute, out rms, out peak);
        }
        else if (format.Encoding == AudioSampleEncoding.Pcm32)
        {
            ReadOnlySpan<int> samples = MemoryMarshal.Cast<byte, int>(bytes);
            CalculateNormalizedStatistics(samples, out meanAbsolute, out rms, out peak);
        }

        return new AudioPacketStatistics(
            byteCount,
            nonZeroBytes,
            (double)nonZeroBytes / byteCount,
            meanAbsolute,
            rms,
            peak);
    }

    private static void CalculateNormalizedStatistics(
        ReadOnlySpan<float> samples,
        out double meanAbsolute,
        out double rms,
        out double peak)
    {
        double absoluteTotal = 0;
        double squareTotal = 0;
        peak = 0;
        int validCount = 0;
        foreach (float sample in samples)
        {
            if (!float.IsFinite(sample))
            {
                continue;
            }

            double absolute = Math.Abs(sample);
            absoluteTotal += absolute;
            squareTotal += sample * sample;
            peak = Math.Max(peak, absolute);
            validCount++;
        }

        meanAbsolute = validCount == 0 ? 0 : absoluteTotal / validCount;
        rms = validCount == 0 ? 0 : Math.Sqrt(squareTotal / validCount);
    }

    private static void CalculateNormalizedStatistics(
        ReadOnlySpan<short> samples,
        out double meanAbsolute,
        out double rms,
        out double peak)
    {
        double absoluteTotal = 0;
        double squareTotal = 0;
        peak = 0;
        foreach (short sample in samples)
        {
            double normalized = sample / 32768.0;
            double absolute = Math.Abs(normalized);
            absoluteTotal += absolute;
            squareTotal += normalized * normalized;
            peak = Math.Max(peak, absolute);
        }

        meanAbsolute = samples.Length == 0 ? 0 : absoluteTotal / samples.Length;
        rms = samples.Length == 0 ? 0 : Math.Sqrt(squareTotal / samples.Length);
    }

    private static void CalculateNormalizedStatistics(
        ReadOnlySpan<int> samples,
        out double meanAbsolute,
        out double rms,
        out double peak)
    {
        double absoluteTotal = 0;
        double squareTotal = 0;
        peak = 0;
        foreach (int sample in samples)
        {
            double normalized = sample / 2147483648.0;
            double absolute = Math.Abs(normalized);
            absoluteTotal += absolute;
            squareTotal += normalized * normalized;
            peak = Math.Max(peak, absolute);
        }

        meanAbsolute = samples.Length == 0 ? 0 : absoluteTotal / samples.Length;
        rms = samples.Length == 0 ? 0 : Math.Sqrt(squareTotal / samples.Length);
    }
}

internal readonly record struct AudioWaveFormat(
    ushort FormatTag,
    ushort Channels,
    uint SamplesPerSecond,
    ushort BlockAlign,
    ushort BitsPerSample,
    AudioSampleEncoding Encoding)
{
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");

    internal static AudioWaveFormat Parse(nint pointer)
    {
        ushort formatTag = unchecked((ushort)Marshal.ReadInt16(pointer, 0));
        ushort channels = unchecked((ushort)Marshal.ReadInt16(pointer, 2));
        uint samplesPerSecond = unchecked((uint)Marshal.ReadInt32(pointer, 4));
        ushort blockAlign = unchecked((ushort)Marshal.ReadInt16(pointer, 12));
        ushort bitsPerSample = unchecked((ushort)Marshal.ReadInt16(pointer, 14));
        ushort extraSize = unchecked((ushort)Marshal.ReadInt16(pointer, 16));
        Guid subFormat = Guid.Empty;
        if (formatTag == 0xFFFE && extraSize >= 22)
        {
            subFormat = Marshal.PtrToStructure<Guid>(pointer + 24);
        }

        AudioSampleEncoding encoding = formatTag switch
        {
            1 when bitsPerSample == 16 => AudioSampleEncoding.Pcm16,
            1 when bitsPerSample == 32 => AudioSampleEncoding.Pcm32,
            3 when bitsPerSample == 32 => AudioSampleEncoding.Float32,
            0xFFFE when subFormat == PcmSubFormat && bitsPerSample == 16 => AudioSampleEncoding.Pcm16,
            0xFFFE when subFormat == PcmSubFormat && bitsPerSample == 32 => AudioSampleEncoding.Pcm32,
            0xFFFE when subFormat == FloatSubFormat && bitsPerSample == 32 => AudioSampleEncoding.Float32,
            _ => AudioSampleEncoding.Unknown
        };
        return new AudioWaveFormat(formatTag, channels, samplesPerSecond, blockAlign, bitsPerSample, encoding);
    }

    public override string ToString() =>
        $"{SamplesPerSecond} Hz, {Channels} ch, {BitsPerSample} bit, block={BlockAlign}, {Encoding}, tag=0x{FormatTag:X4}";
}

internal enum AudioSampleEncoding
{
    Unknown,
    Pcm16,
    Pcm32,
    Float32
}

[Flags]
internal enum AudioClientBufferFlags : uint
{
    None = 0,
    DataDiscontinuity = 0x1,
    Silent = 0x2,
    TimestampError = 0x4
}

internal enum AudioClientShareMode
{
    Shared = 0,
    Exclusive = 1
}

internal static partial class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode)]
    internal static extern ushort RegisterClass(ref WndClass windowClass);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    internal static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetMessageW")]
    internal static extern int GetMessage(out Message message, nint window, uint minimumMessage, uint maximumMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static extern nint DispatchMessage(ref Message message);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices,
        uint deviceCount,
        uint deviceSize);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputDeviceList(nint deviceList, ref uint deviceCount, uint deviceSize);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetRawInputDeviceInfoW")]
    internal static extern uint GetRawInputDeviceInfo(nint device, uint command, nint data, ref uint dataSize);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputData(nint rawInput, uint command, nint data, ref uint size, uint headerSize);

    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(nint reserved, uint coInit);

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(ref PropVariant variant);

    [DllImport("hid.dll", EntryPoint = "HidD_GetHidGuid")]
    internal static extern void HidDGetHidGuid(out Guid hidGuid);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode)]
    internal static extern nint SetupDiGetClassDevs(
        ref Guid classGuid,
        nint enumerator,
        nint parentWindow,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInterfaces(
        nint deviceInfoSet,
        nint deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref DeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInterfaceDetail(
        nint deviceInfoSet,
        ref DeviceInterfaceData deviceInterfaceData,
        nint deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        nint deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CreateFileW", CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("hid.dll", SetLastError = true, EntryPoint = "HidD_GetAttributes")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool HidDGetAttributes(SafeFileHandle device, ref HidAttributes attributes);

    [DllImport("hid.dll", SetLastError = true, EntryPoint = "HidD_GetPreparsedData")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool HidDGetPreparsedData(SafeFileHandle device, out nint preparsedData);

    [DllImport("hid.dll", EntryPoint = "HidD_FreePreparsedData")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool HidDFreePreparsedData(nint preparsedData);

    [DllImport("hid.dll", EntryPoint = "HidP_GetCaps")]
    internal static extern int HidPGetCaps(nint preparsedData, out HidCaps capabilities);

    [DllImport("hid.dll", EntryPoint = "HidP_GetValueCaps")]
    internal static extern int HidPGetValueCaps(
        HidReportType reportType,
        [Out] HidValueCaps[] valueCaps,
        ref ushort valueCapsLength,
        nint preparsedData);

    [DllImport("hid.dll", EntryPoint = "HidP_GetButtonCaps")]
    internal static extern int HidPGetButtonCaps(
        HidReportType reportType,
        [Out] HidButtonCaps[] buttonCaps,
        ref ushort buttonCapsLength,
        nint preparsedData);

    [DllImport("hid.dll", SetLastError = true, EntryPoint = "HidD_GetFeature")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidDGetFeature(
        SafeFileHandle device,
        [In, Out] byte[] reportBuffer,
        int reportBufferLength);
}

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WndClass
{
    internal uint Style;
    internal nint WindowProcedure;
    internal int ClassExtraBytes;
    internal int WindowExtraBytes;
    internal nint Instance;
    internal nint Icon;
    internal nint Cursor;
    internal nint BackgroundBrush;
    internal string? MenuName;
    internal string ClassName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Message
{
    internal nint Window;
    internal uint Value;
    internal nuint WParam;
    internal nint LParam;
    internal uint Time;
    internal Point Point;
    internal uint Private;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Point
{
    internal int X;
    internal int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputDevice
{
    internal ushort UsagePage;
    internal ushort Usage;
    internal uint Flags;
    internal nint TargetWindow;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputDeviceList
{
    internal nint DeviceHandle;
    internal uint Type;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputHeader
{
    internal uint Type;
    internal uint Size;
    internal nint DeviceHandle;
    internal nint WParam;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawDeviceInfo
{
    internal uint Size;
    internal uint Type;
    internal RawDeviceInfoUnion Union;
}

[StructLayout(LayoutKind.Explicit)]
internal struct RawDeviceInfoUnion
{
    [FieldOffset(0)]
    internal RawDeviceInfoMouse Mouse;

    [FieldOffset(0)]
    internal RawDeviceInfoKeyboard Keyboard;

    [FieldOffset(0)]
    internal RawDeviceInfoHid Hid;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawDeviceInfoMouse
{
    internal uint Id;
    internal uint ButtonCount;
    internal uint SampleRate;
    [MarshalAs(UnmanagedType.Bool)]
    internal bool HasHorizontalWheel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawDeviceInfoKeyboard
{
    internal uint Type;
    internal uint SubType;
    internal uint KeyboardMode;
    internal uint FunctionKeyCount;
    internal uint IndicatorCount;
    internal uint TotalKeyCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawDeviceInfoHid
{
    internal uint VendorId;
    internal uint ProductId;
    internal uint VersionNumber;
    internal ushort UsagePage;
    internal ushort Usage;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DeviceInterfaceData
{
    internal uint Size;
    internal Guid InterfaceClassGuid;
    internal uint Flags;
    internal nuint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HidAttributes
{
    internal int Size;
    internal ushort VendorId;
    internal ushort ProductId;
    internal ushort VersionNumber;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HidCaps
{
    internal ushort Usage;
    internal ushort UsagePage;
    internal ushort InputReportByteLength;
    internal ushort OutputReportByteLength;
    internal ushort FeatureReportByteLength;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
    internal ushort[] Reserved;

    internal ushort NumberLinkCollectionNodes;
    internal ushort NumberInputButtonCaps;
    internal ushort NumberInputValueCaps;
    internal ushort NumberInputDataIndices;
    internal ushort NumberOutputButtonCaps;
    internal ushort NumberOutputValueCaps;
    internal ushort NumberOutputDataIndices;
    internal ushort NumberFeatureButtonCaps;
    internal ushort NumberFeatureValueCaps;
    internal ushort NumberFeatureDataIndices;
}

internal enum HidReportType
{
    Input = 0,
    Output = 1,
    Feature = 2
}

[StructLayout(LayoutKind.Explicit, Size = 72)]
internal struct HidValueCaps
{
    [FieldOffset(0)]
    internal ushort UsagePage;

    [FieldOffset(2)]
    internal byte ReportId;
}

[StructLayout(LayoutKind.Explicit, Size = 72)]
internal struct HidButtonCaps
{
    [FieldOffset(0)]
    internal ushort UsagePage;

    [FieldOffset(2)]
    internal byte ReportId;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct PropertyKey
{
    internal PropertyKey(Guid formatId, uint propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }

    internal readonly Guid FormatId;
    internal readonly uint PropertyId;
}

[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    [FieldOffset(0)]
    internal ushort VarType;

    [FieldOffset(8)]
    internal nint PointerValue;
}

internal enum DataFlow
{
    Render,
    Capture,
    All
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal sealed class MMDeviceEnumeratorComObject
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(DataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(DataFlow dataFlow, int role, out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(nint client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(nint client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int Item(uint index, out IMMDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid interfaceId, uint classContext, nint activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    [PreserveSig]
    int OpenPropertyStore(uint accessMode, out IPropertyStore properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out uint state);
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint propertyCount);

    [PreserveSig]
    int GetAt(uint propertyIndex, out PropertyKey key);

    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropVariant value);

    [PreserveSig]
    int SetValue(ref PropertyKey key, ref PropVariant value);

    [PreserveSig]
    int Commit();
}

[ComImport]
[Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformation
{
    [PreserveSig]
    int GetPeakValue(out float peak);

    [PreserveSig]
    int GetMeteringChannelCount(out int channelCount);

    [PreserveSig]
    int GetChannelsPeakValues(int channelCount, [Out] float[] peakValues);

    [PreserveSig]
    int QueryHardwareSupport(out int hardwareSupportMask);
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(
        AudioClientShareMode shareMode,
        uint streamFlags,
        long bufferDuration,
        long periodicity,
        nint format,
        nint audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint bufferFrameCount);

    [PreserveSig]
    int GetStreamLatency(out long latency);

    [PreserveSig]
    int GetCurrentPadding(out uint paddingFrameCount);

    [PreserveSig]
    int IsFormatSupported(AudioClientShareMode shareMode, nint format, out nint closestMatch);

    [PreserveSig]
    int GetMixFormat(out nint deviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(nint eventHandle);

    [PreserveSig]
    int GetService(
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(
        out nint data,
        out uint framesToRead,
        out AudioClientBufferFlags flags,
        out ulong devicePosition,
        out ulong qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(uint framesRead);

    [PreserveSig]
    int GetNextPacketSize(out uint nextPacketFrames);
}
