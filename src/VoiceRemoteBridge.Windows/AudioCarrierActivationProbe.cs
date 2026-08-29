using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoiceRemoteBridge.Windows;

public sealed record AudioCarrierActivationOptions(
    TimeSpan WarmupDuration,
    TimeSpan ActivationTimeout,
    double RmsThreshold,
    int RequiredConsecutivePackets)
{
    public IReadOnlyList<string> Validate()
    {
        List<string> errors = [];
        if (WarmupDuration < TimeSpan.Zero || WarmupDuration >= ActivationTimeout)
        {
            errors.Add("Audio carrier warmup must be non-negative and shorter than the activation timeout.");
        }

        if (ActivationTimeout <= TimeSpan.Zero)
        {
            errors.Add("Audio carrier activation timeout must be positive.");
        }

        if (!double.IsFinite(RmsThreshold) || RmsThreshold <= 0 || RmsThreshold > 1)
        {
            errors.Add("Audio carrier RMS threshold must be greater than zero and at most one.");
        }

        if (RequiredConsecutivePackets is < 1 or > 100)
        {
            errors.Add("Audio carrier consecutive packet count must be between 1 and 100.");
        }

        return errors;
    }
}

public sealed record AudioCarrierActivationResult(
    bool Activated,
    string EndpointName,
    string FormatDescription,
    int ObservedPackets,
    int ConsecutiveActivePackets,
    double MaximumRms,
    string Message);

public readonly record struct AudioCarrierPacketMetrics(
    int ByteCount,
    int NonZeroByteCount,
    double NonZeroByteRatio,
    double Rms,
    double PeakAbsolute);

public static class AudioCarrierMetricsCalculator
{
    public static AudioCarrierPacketMetrics CalculateFloat32(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return new AudioCarrierPacketMetrics(0, 0, 0, 0, 0);
        }

        int nonZeroBytes = 0;
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(samples);
        foreach (byte value in bytes)
        {
            if (value != 0)
            {
                nonZeroBytes++;
            }
        }

        double squareTotal = 0;
        double peak = 0;
        int validSamples = 0;
        foreach (float sample in samples)
        {
            if (!float.IsFinite(sample))
            {
                continue;
            }

            double absolute = Math.Abs(sample);
            squareTotal += sample * sample;
            peak = Math.Max(peak, absolute);
            validSamples++;
        }

        return new AudioCarrierPacketMetrics(
            bytes.Length,
            nonZeroBytes,
            (double)nonZeroBytes / bytes.Length,
            validSamples == 0 ? 0 : Math.Sqrt(squareTotal / validSamples),
            peak);
    }
}

public sealed class AudioCarrierActivationSession : IAsyncDisposable
{
    private const uint DeviceStateActive = 0x00000001;
    private const uint ClsctxAll = 23;
    private const long RequestedBufferDuration = 10_000_000;
    private const double MinimumNonZeroByteRatio = 0.10;
    private static readonly Guid AudioClientInterfaceId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid AudioCaptureClientInterfaceId = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    private readonly string requestedEndpointName;
    private readonly AudioCarrierActivationOptions options;
    private readonly CancellationTokenSource cancellation = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<AudioCarrierActivationResult> activation = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? thread;
    private int disposed;

    public AudioCarrierActivationSession(
        string requestedEndpointName,
        AudioCarrierActivationOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedEndpointName);
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<string> errors = options.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(' ', errors), nameof(options));
        }

        this.requestedEndpointName = requestedEndpointName;
        this.options = options;
    }

    public async Task<AudioCarrierActivationResult> WaitForActivationAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        Start();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        try
        {
            return await activation.Task
                .WaitAsync(options.ActivationTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new AudioCarrierActivationResult(
                false,
                EndpointName,
                FormatDescription,
                ObservedPackets,
                ConsecutiveActivePackets,
                MaximumRms,
                $"Microphone carrier was not confirmed within {options.ActivationTimeout.TotalMilliseconds:F0} ms.");
        }
    }

    public string EndpointName { get; private set; } = string.Empty;

    public string FormatDescription { get; private set; } = string.Empty;

    public int ObservedPackets { get; private set; }

    public int ConsecutiveActivePackets { get; private set; }

    public double MaximumRms { get; private set; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        cancellation.Cancel();
        if (thread is not null && !thread.Join(TimeSpan.FromSeconds(5)))
        {
            cancellation.Dispose();
            throw new TimeoutException("Timed out while stopping the audio carrier activation probe.");
        }

        cancellation.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Start()
    {
        if (thread is not null)
        {
            return;
        }

        thread = new Thread(CaptureThread)
        {
            IsBackground = true,
            Name = "VoiceRemoteBridge.AudioCarrierActivation"
        };
        thread.Start();
    }

    private void CaptureThread()
    {
        int initializeResult = AudioEndpointNativeMethods.CoInitializeEx(nint.Zero, 0);
        bool uninitialize = initializeResult >= 0;
        IAudioDeviceEnumerator? enumerator = null;
        IAudioDeviceCollection? collection = null;
        IAudioDevice? selected = null;
        object? audioClientObject = null;
        object? captureClientObject = null;
        nint formatPointer = nint.Zero;
        bool audioStarted = false;
        try
        {
            if (initializeResult < 0 && initializeResult != unchecked((int)0x80010106))
            {
                Marshal.ThrowExceptionForHR(initializeResult);
            }

            enumerator = (IAudioDeviceEnumerator)(object)new AudioDeviceEnumeratorComObject();
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(
                AudioDataFlow.Capture,
                DeviceStateActive,
                out collection));
            Marshal.ThrowExceptionForHR(collection.GetCount(out uint count));
            for (uint index = 0; index < count; index++)
            {
                Marshal.ThrowExceptionForHR(collection.Item(index, out IAudioDevice candidate));
                string friendlyName = AudioEndpointStatusProbe.GetFriendlyName(candidate);
                if (friendlyName.Contains(requestedEndpointName, StringComparison.OrdinalIgnoreCase))
                {
                    selected = candidate;
                    EndpointName = friendlyName;
                    break;
                }

                AudioEndpointStatusProbe.ReleaseCom(candidate);
            }

            if (selected is null)
            {
                throw new InvalidOperationException($"未找到活动录音端点：{requestedEndpointName}。");
            }

            Guid audioClientId = AudioClientInterfaceId;
            Marshal.ThrowExceptionForHR(selected.Activate(
                ref audioClientId,
                ClsctxAll,
                nint.Zero,
                out audioClientObject));
            ICarrierAudioClient audioClient = (ICarrierAudioClient)audioClientObject;
            Marshal.ThrowExceptionForHR(audioClient.GetMixFormat(out formatPointer));
            CarrierWaveFormat format = CarrierWaveFormat.Parse(formatPointer);
            if (format.Encoding == CarrierSampleEncoding.Unknown)
            {
                throw new NotSupportedException($"不支持的录音格式：{format}。");
            }

            FormatDescription = format.ToString();
            Marshal.ThrowExceptionForHR(audioClient.Initialize(
                CarrierAudioClientShareMode.Shared,
                0,
                RequestedBufferDuration,
                0,
                formatPointer,
                nint.Zero));
            Guid captureClientId = AudioCaptureClientInterfaceId;
            Marshal.ThrowExceptionForHR(audioClient.GetService(ref captureClientId, out captureClientObject));
            ICarrierAudioCaptureClient captureClient = (ICarrierAudioCaptureClient)captureClientObject;
            Marshal.ThrowExceptionForHR(audioClient.Start());
            audioStarted = true;
            Stopwatch stopwatch = Stopwatch.StartNew();
            ready.TrySetResult();

            while (!cancellation.IsCancellationRequested)
            {
                Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out uint nextPacketFrames));
                while (nextPacketFrames > 0)
                {
                    Marshal.ThrowExceptionForHR(captureClient.GetBuffer(
                        out nint data,
                        out uint frames,
                        out CarrierAudioClientBufferFlags flags,
                        out _,
                        out _));
                    AudioCarrierPacketMetrics metrics;
                    try
                    {
                        metrics = CalculateMetrics(data, frames, flags, format);
                    }
                    finally
                    {
                        Marshal.ThrowExceptionForHR(captureClient.ReleaseBuffer(frames));
                    }

                    ObservedPackets++;
                    MaximumRms = Math.Max(MaximumRms, metrics.Rms);
                    bool activePacket = stopwatch.Elapsed >= options.WarmupDuration &&
                        metrics.Rms >= options.RmsThreshold &&
                        metrics.NonZeroByteRatio >= MinimumNonZeroByteRatio;
                    ConsecutiveActivePackets = activePacket ? ConsecutiveActivePackets + 1 : 0;
                    if (ConsecutiveActivePackets >= options.RequiredConsecutivePackets)
                    {
                        activation.TrySetResult(new AudioCarrierActivationResult(
                            true,
                            EndpointName,
                            FormatDescription,
                            ObservedPackets,
                            ConsecutiveActivePackets,
                            MaximumRms,
                            "持续麦克风载波已确认。"));
                    }

                    Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out nextPacketFrames));
                }

                Thread.Sleep(5);
            }
        }
        catch (Exception exception)
        {
            ready.TrySetException(exception);
            activation.TrySetException(exception);
        }
        finally
        {
            ready.TrySetResult();
            if (audioStarted && audioClientObject is ICarrierAudioClient audioClient)
            {
                audioClient.Stop();
            }

            if (formatPointer != nint.Zero)
            {
                Marshal.FreeCoTaskMem(formatPointer);
            }

            AudioEndpointStatusProbe.ReleaseCom(captureClientObject);
            AudioEndpointStatusProbe.ReleaseCom(audioClientObject);
            AudioEndpointStatusProbe.ReleaseCom(selected);
            AudioEndpointStatusProbe.ReleaseCom(collection);
            AudioEndpointStatusProbe.ReleaseCom(enumerator);
            if (uninitialize)
            {
                AudioEndpointNativeMethods.CoUninitialize();
            }
        }
    }

    private static AudioCarrierPacketMetrics CalculateMetrics(
        nint data,
        uint frames,
        CarrierAudioClientBufferFlags flags,
        CarrierWaveFormat format)
    {
        int byteCount = checked((int)(frames * format.BlockAlign));
        if (byteCount == 0 || data == nint.Zero || flags.HasFlag(CarrierAudioClientBufferFlags.Silent))
        {
            return new AudioCarrierPacketMetrics(byteCount, 0, 0, 0, 0);
        }

        byte[] bytes = new byte[byteCount];
        Marshal.Copy(data, bytes, 0, byteCount);
        return format.Encoding switch
        {
            CarrierSampleEncoding.Float32 => AudioCarrierMetricsCalculator.CalculateFloat32(
                MemoryMarshal.Cast<byte, float>(bytes)),
            CarrierSampleEncoding.Pcm16 => CalculateIntegerMetrics(
                MemoryMarshal.Cast<byte, short>(bytes),
                short.MaxValue + 1.0),
            CarrierSampleEncoding.Pcm32 => CalculateIntegerMetrics(
                MemoryMarshal.Cast<byte, int>(bytes),
                int.MaxValue + 1.0),
            _ => new AudioCarrierPacketMetrics(byteCount, 0, 0, 0, 0)
        };
    }

    private static AudioCarrierPacketMetrics CalculateIntegerMetrics<T>(
        ReadOnlySpan<T> samples,
        double divisor)
        where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(samples);
        int nonZeroBytes = 0;
        foreach (byte value in bytes)
        {
            if (value != 0)
            {
                nonZeroBytes++;
            }
        }

        double squareTotal = 0;
        double peak = 0;
        foreach (T sample in samples)
        {
            double normalized = Convert.ToDouble(sample, System.Globalization.CultureInfo.InvariantCulture) / divisor;
            squareTotal += normalized * normalized;
            peak = Math.Max(peak, Math.Abs(normalized));
        }

        return new AudioCarrierPacketMetrics(
            bytes.Length,
            nonZeroBytes,
            bytes.IsEmpty ? 0 : (double)nonZeroBytes / bytes.Length,
            samples.IsEmpty ? 0 : Math.Sqrt(squareTotal / samples.Length),
            peak);
    }
}

internal readonly record struct CarrierWaveFormat(
    ushort FormatTag,
    ushort Channels,
    uint SamplesPerSecond,
    ushort BlockAlign,
    ushort BitsPerSample,
    CarrierSampleEncoding Encoding)
{
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");

    internal static CarrierWaveFormat Parse(nint pointer)
    {
        ushort formatTag = unchecked((ushort)Marshal.ReadInt16(pointer, 0));
        ushort channels = unchecked((ushort)Marshal.ReadInt16(pointer, 2));
        uint samplesPerSecond = unchecked((uint)Marshal.ReadInt32(pointer, 4));
        ushort blockAlign = unchecked((ushort)Marshal.ReadInt16(pointer, 12));
        ushort bitsPerSample = unchecked((ushort)Marshal.ReadInt16(pointer, 14));
        ushort extraSize = unchecked((ushort)Marshal.ReadInt16(pointer, 16));
        Guid subFormat = formatTag == 0xFFFE && extraSize >= 22
            ? Marshal.PtrToStructure<Guid>(pointer + 24)
            : Guid.Empty;
        CarrierSampleEncoding encoding = formatTag switch
        {
            1 when bitsPerSample == 16 => CarrierSampleEncoding.Pcm16,
            1 when bitsPerSample == 32 => CarrierSampleEncoding.Pcm32,
            3 when bitsPerSample == 32 => CarrierSampleEncoding.Float32,
            0xFFFE when subFormat == PcmSubFormat && bitsPerSample == 16 => CarrierSampleEncoding.Pcm16,
            0xFFFE when subFormat == PcmSubFormat && bitsPerSample == 32 => CarrierSampleEncoding.Pcm32,
            0xFFFE when subFormat == FloatSubFormat && bitsPerSample == 32 => CarrierSampleEncoding.Float32,
            _ => CarrierSampleEncoding.Unknown
        };
        return new CarrierWaveFormat(formatTag, channels, samplesPerSecond, blockAlign, bitsPerSample, encoding);
    }

    public override string ToString() =>
        $"{SamplesPerSecond} Hz, {Channels} ch, {BitsPerSample} bit, block={BlockAlign}, {Encoding}, tag=0x{FormatTag:X4}";
}

internal enum CarrierSampleEncoding
{
    Unknown,
    Pcm16,
    Pcm32,
    Float32
}

[Flags]
internal enum CarrierAudioClientBufferFlags : uint
{
    None = 0,
    DataDiscontinuity = 0x1,
    Silent = 0x2,
    TimestampError = 0x4
}

internal enum CarrierAudioClientShareMode
{
    Shared = 0,
    Exclusive = 1
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICarrierAudioClient
{
    [PreserveSig]
    int Initialize(
        CarrierAudioClientShareMode shareMode,
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
    int IsFormatSupported(CarrierAudioClientShareMode shareMode, nint format, out nint closestMatch);

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
    int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICarrierAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(
        out nint data,
        out uint framesToRead,
        out CarrierAudioClientBufferFlags flags,
        out ulong devicePosition,
        out ulong qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(uint framesRead);

    [PreserveSig]
    int GetNextPacketSize(out uint nextPacketFrames);
}
