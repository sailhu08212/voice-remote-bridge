using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using VoiceRemoteBridge.Core;

namespace VoiceRemoteBridge.Windows;

public sealed record RawInputDeviceDescriptor(
    string DevicePath,
    string Type,
    uint VendorId,
    uint ProductId,
    ushort UsagePage,
    ushort Usage);

public sealed class RawInputReportSource : IHidReportSource
{
    private const uint WmInput = 0x00FF;
    private const uint WmInputDeviceChange = 0x00FE;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevDevNotify = 0x00002000;
    private const uint RidevRemove = 0x00000001;
    private const int ErrorClassAlreadyExists = 1410;
    private static readonly nint HwndMessage = new(-3);
    private static readonly RawInputWindowProcedure WindowProcedureDelegate = WindowProcedure;
    private static readonly object ActiveSync = new();
    private static RawInputReportSource? activeSource;
    private readonly HidButtonBinding binding;
    private readonly Stopwatch stopwatch = new();
    private TaskCompletionSource<bool> windowReady = CreateWindowReadySource();
    private Task? worker;
    private nint window;
    private bool connected;
    private bool disposed;

    public RawInputReportSource(HidButtonBinding binding)
    {
        this.binding = binding ?? throw new ArgumentNullException(nameof(binding));
        if (binding.Transport != HidTransport.RawInput)
        {
            throw new ArgumentException("Binding transport must be RawInput.", nameof(binding));
        }

        IReadOnlyList<string> errors = binding.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(' ', errors), nameof(binding));
        }
    }

    public event EventHandler<HidReportEventArgs>? ReportReceived;

    public event EventHandler<bool>? ConnectionChanged;

    public event EventHandler<string>? Diagnostic;

    public bool IsConnected => connected;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (worker is not null)
        {
            return;
        }

        lock (ActiveSync)
        {
            if (activeSource is not null)
            {
                throw new InvalidOperationException("Only one Raw Input source can run in this process.");
            }

            activeSource = this;
        }

        stopwatch.Restart();
        windowReady = CreateWindowReadySource();
        worker = Task.Run(RunMessageLoop);
    }

    public async Task StopAsync()
    {
        Task? currentWorker = worker;
        if (currentWorker is null)
        {
            return;
        }

        bool created = await windowReady.Task.ConfigureAwait(false);
        nint currentWindow = window;
        if (created && currentWindow != nint.Zero)
        {
            RawInputNativeMethods.PostMessage(currentWindow, WmClose, nuint.Zero, nint.Zero);
        }

        await currentWorker.ConfigureAwait(false);
        worker = null;
        stopwatch.Stop();
        SetConnected(false);
        lock (ActiveSync)
        {
            if (ReferenceEquals(activeSource, this))
            {
                activeSource = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        disposed = true;
    }

    public static IReadOnlyList<RawInputDeviceDescriptor> Enumerate(string hardwareId) =>
        RawInputDeviceDiscovery.Enumerate(hardwareId);

    private void RunMessageLoop()
    {
        try
        {
            string className = $"VoiceRemoteBridge.RawInput.{Environment.ProcessId}";
            RawInputWindowClass windowClass = new()
            {
                ClassName = className,
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(WindowProcedureDelegate)
            };
            ushort atom = RawInputNativeMethods.RegisterClass(ref windowClass);
            int registerError = Marshal.GetLastWin32Error();
            if (atom == 0 && registerError != ErrorClassAlreadyExists)
            {
                throw new Win32Exception(registerError, "RegisterClass failed for the Raw Input window.");
            }

            window = RawInputNativeMethods.CreateWindowEx(
                0,
                className,
                "Voice Remote Bridge Raw Input",
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
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed for Raw Input.");
            }

            windowReady.TrySetResult(true);

            Register(window, remove: false);
            RefreshConnectionState();
            while (true)
            {
                int result = RawInputNativeMethods.GetMessage(out RawInputMessage message, nint.Zero, 0, 0);
                if (result == -1)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMessage failed for Raw Input.");
                }

                if (result == 0)
                {
                    break;
                }

                RawInputNativeMethods.TranslateMessage(ref message);
                RawInputNativeMethods.DispatchMessage(ref message);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            Diagnostic?.Invoke(this, $"Raw Input stopped: {exception.Message}");
        }
        finally
        {
            windowReady.TrySetResult(false);
            window = nint.Zero;
            SetConnected(false);
            lock (ActiveSync)
            {
                if (ReferenceEquals(activeSource, this))
                {
                    activeSource = null;
                }
            }
        }
    }

    private void HandleRawInput(nint rawInputHandle)
    {
        RawInputPacket? packet = RawInputDeviceDiscovery.Read(rawInputHandle);
        if (packet is null ||
            !packet.DevicePath.Contains(binding.HardwareId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SetConnected(true);
        if (packet.Type == RawInputDeviceType.Hid)
        {
            if (packet.Payload.Length < 8)
            {
                return;
            }

            uint reportSize = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.AsSpan(0, 4));
            uint reportCount = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.AsSpan(4, 4));
            if (reportSize == 0 || reportCount == 0 ||
                (ulong)reportSize * reportCount > (ulong)(packet.Payload.Length - 8))
            {
                return;
            }

            for (uint index = 0; index < reportCount; index++)
            {
                int offset = checked(8 + (int)(index * reportSize));
                byte[] report = packet.Payload.AsSpan(offset, checked((int)reportSize)).ToArray();
                RaiseReport(report);
            }
        }
        else
        {
            RaiseReport(packet.Payload);
        }
    }

    private void RaiseReport(byte[] report) => ReportReceived?.Invoke(
        this,
        new HidReportEventArgs(
            binding.UsagePage,
            binding.Usage,
            report,
            stopwatch.Elapsed));

    private void RefreshConnectionState()
    {
        try
        {
            bool present = RawInputDeviceDiscovery.Enumerate(binding.HardwareId)
                .Any(item => item.UsagePage == binding.UsagePage && item.Usage == binding.Usage);
            SetConnected(present);
        }
        catch (InvalidOperationException exception)
        {
            Diagnostic?.Invoke(this, $"Raw Input enumeration failed: {exception.Message}");
            SetConnected(false);
        }
    }

    private void SetConnected(bool value)
    {
        if (connected == value)
        {
            return;
        }

        connected = value;
        ConnectionChanged?.Invoke(this, value);
    }

    private void Register(nint target, bool remove)
    {
        RawInputRegistration[] registrations =
        [
            new RawInputRegistration
            {
                UsagePage = binding.UsagePage,
                Usage = binding.Usage,
                Flags = remove ? RidevRemove : RidevInputSink | RidevDevNotify,
                TargetWindow = remove ? nint.Zero : target
            }
        ];
        if (!RawInputNativeMethods.RegisterRawInputDevices(
            registrations,
            1,
            (uint)Marshal.SizeOf<RawInputRegistration>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                remove ? "Raw Input unregister failed." : "Raw Input registration failed.");
        }
    }

    private static nint WindowProcedure(nint target, uint message, nuint wParam, nint lParam)
    {
        RawInputReportSource? source;
        lock (ActiveSync)
        {
            source = activeSource;
        }

        if (message == WmInput)
        {
            source?.HandleRawInput(lParam);
        }
        else if (message == WmInputDeviceChange)
        {
            source?.RefreshConnectionState();
        }
        else if (message == WmClose)
        {
            if (source is not null)
            {
                try
                {
                    source.Register(target, remove: true);
                }
                catch (Win32Exception exception)
                {
                    source.Diagnostic?.Invoke(source, exception.Message);
                }
            }

            RawInputNativeMethods.DestroyWindow(target);
            return nint.Zero;
        }
        else if (message == WmDestroy)
        {
            RawInputNativeMethods.PostQuitMessage(0);
            return nint.Zero;
        }

        return RawInputNativeMethods.DefWindowProc(target, message, wParam, lParam);
    }

    private static TaskCompletionSource<bool> CreateWindowReadySource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal enum RawInputDeviceType : uint
{
    Mouse = 0,
    Keyboard = 1,
    Hid = 2
}

internal sealed record RawInputPacket(
    RawInputDeviceType Type,
    string DevicePath,
    byte[] Payload);

internal static class RawInputDeviceDiscovery
{
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RidiDeviceInfo = 0x2000000B;
    private const uint InvalidUInt = uint.MaxValue;

    internal static IReadOnlyList<RawInputDeviceDescriptor> Enumerate(string hardwareId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareId);
        uint count = 0;
        uint itemSize = (uint)Marshal.SizeOf<RawInputDeviceListItem>();
        if (RawInputNativeMethods.GetRawInputDeviceList(nint.Zero, ref count, itemSize) == InvalidUInt)
        {
            throw new InvalidOperationException($"GetRawInputDeviceList(count) failed, Win32={Marshal.GetLastWin32Error()}.");
        }

        if (count == 0)
        {
            return [];
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)(count * itemSize)));
        try
        {
            uint actual = count;
            if (RawInputNativeMethods.GetRawInputDeviceList(buffer, ref actual, itemSize) == InvalidUInt)
            {
                throw new InvalidOperationException($"GetRawInputDeviceList(data) failed, Win32={Marshal.GetLastWin32Error()}.");
            }

            List<RawInputDeviceDescriptor> result = [];
            for (uint index = 0; index < actual; index++)
            {
                RawInputDeviceListItem item = Marshal.PtrToStructure<RawInputDeviceListItem>(
                    buffer + checked((int)(index * itemSize)));
                string? path = GetPath(item.DeviceHandle);
                if (path is null || !path.Contains(hardwareId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                RawInputDeviceInfo info = GetInfo(item.DeviceHandle);
                (ushort page, ushort usage) = item.Type switch
                {
                    RawInputDeviceType.Mouse => ((ushort)0x01, (ushort)0x02),
                    RawInputDeviceType.Keyboard => ((ushort)0x01, (ushort)0x06),
                    RawInputDeviceType.Hid => (info.Union.Hid.UsagePage, info.Union.Hid.Usage),
                    _ => ((ushort)0, (ushort)0)
                };
                uint vendor = item.Type == RawInputDeviceType.Hid
                    ? info.Union.Hid.VendorId
                    : ParseHex(path, "VID_");
                uint product = item.Type == RawInputDeviceType.Hid
                    ? info.Union.Hid.ProductId
                    : ParseHex(path, "PID_");
                result.Add(new RawInputDeviceDescriptor(
                    path,
                    item.Type.ToString(),
                    vendor,
                    product,
                    page,
                    usage));
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static RawInputPacket? Read(nint rawInputHandle)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        uint initial = RawInputNativeMethods.GetRawInputData(
            rawInputHandle,
            RidInput,
            nint.Zero,
            ref size,
            headerSize);
        if (initial == InvalidUInt || size < headerSize)
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            uint capacity = size;
            uint result = RawInputNativeMethods.GetRawInputData(
                rawInputHandle,
                RidInput,
                buffer,
                ref capacity,
                headerSize);
            if (result == InvalidUInt || result < headerSize)
            {
                return null;
            }

            RawInputHeader header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            string? path = GetPath(header.DeviceHandle);
            if (path is null)
            {
                return null;
            }

            int payloadLength = checked((int)result - (int)headerSize);
            byte[] payload = new byte[payloadLength];
            Marshal.Copy(buffer + (int)headerSize, payload, 0, payloadLength);
            return new RawInputPacket(header.Type, path, payload);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? GetPath(nint device)
    {
        uint count = 0;
        uint first = RawInputNativeMethods.GetRawInputDeviceInfo(
            device,
            RidiDeviceName,
            nint.Zero,
            ref count);
        if (first == InvalidUInt || count == 0)
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)((count + 1) * sizeof(char))));
        try
        {
            uint capacity = count;
            uint result = RawInputNativeMethods.GetRawInputDeviceInfo(
                device,
                RidiDeviceName,
                buffer,
                ref capacity);
            return result == InvalidUInt
                ? null
                : Marshal.PtrToStringUni(buffer, checked((int)capacity))?.TrimEnd('\0');
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static RawInputDeviceInfo GetInfo(nint device)
    {
        uint size = (uint)Marshal.SizeOf<RawInputDeviceInfo>();
        RawInputDeviceInfo value = new() { Size = size };
        nint buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            Marshal.StructureToPtr(value, buffer, fDeleteOld: false);
            uint capacity = size;
            if (RawInputNativeMethods.GetRawInputDeviceInfo(
                device,
                RidiDeviceInfo,
                buffer,
                ref capacity) == InvalidUInt)
            {
                throw new InvalidOperationException($"GetRawInputDeviceInfo failed, Win32={Marshal.GetLastWin32Error()}.");
            }

            return Marshal.PtrToStructure<RawInputDeviceInfo>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static uint ParseHex(string path, string marker)
    {
        int start = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || start + marker.Length + 4 > path.Length)
        {
            return 0;
        }

        return uint.TryParse(
            path.AsSpan(start + marker.Length, 4),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out uint value)
            ? value
            : 0;
    }
}

internal static class RawInputNativeMethods
{
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode)]
    internal static extern ushort RegisterClass(ref RawInputWindowClass windowClass);

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
    internal static extern int GetMessage(out RawInputMessage message, nint window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref RawInputMessage message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static extern nint DispatchMessage(ref RawInputMessage message);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterRawInputDevices(
        [In] RawInputRegistration[] devices,
        uint count,
        uint size);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputDeviceList(nint list, ref uint count, uint size);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetRawInputDeviceInfoW")]
    internal static extern uint GetRawInputDeviceInfo(nint device, uint command, nint data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
}

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate nint RawInputWindowProcedure(nint window, uint message, nuint wParam, nint lParam);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct RawInputWindowClass
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
internal struct RawInputMessage
{
    internal nint Window;
    internal uint Value;
    internal nuint WParam;
    internal nint LParam;
    internal uint Time;
    internal RawInputPoint Point;
    internal uint Private;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputPoint
{
    internal int X;
    internal int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputRegistration
{
    internal ushort UsagePage;
    internal ushort Usage;
    internal uint Flags;
    internal nint TargetWindow;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputDeviceListItem
{
    internal nint DeviceHandle;
    internal RawInputDeviceType Type;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputHeader
{
    internal RawInputDeviceType Type;
    internal uint Size;
    internal nint DeviceHandle;
    internal nint WParam;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputDeviceInfo
{
    internal uint Size;
    internal RawInputDeviceType Type;
    internal RawInputDeviceInfoUnion Union;
}

[StructLayout(LayoutKind.Explicit)]
internal struct RawInputDeviceInfoUnion
{
    [FieldOffset(0)]
    internal RawInputMouseInfo Mouse;

    [FieldOffset(0)]
    internal RawInputKeyboardInfo Keyboard;

    [FieldOffset(0)]
    internal RawInputHidInfo Hid;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputMouseInfo
{
    internal uint Id;
    internal uint ButtonCount;
    internal uint SampleRate;
    [MarshalAs(UnmanagedType.Bool)]
    internal bool HasHorizontalWheel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputKeyboardInfo
{
    internal uint Type;
    internal uint SubType;
    internal uint KeyboardMode;
    internal uint FunctionKeyCount;
    internal uint IndicatorCount;
    internal uint TotalKeyCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawInputHidInfo
{
    internal uint VendorId;
    internal uint ProductId;
    internal uint VersionNumber;
    internal ushort UsagePage;
    internal ushort Usage;
}
