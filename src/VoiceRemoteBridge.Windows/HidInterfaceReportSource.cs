using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using VoiceRemoteBridge.Core;

namespace VoiceRemoteBridge.Windows;

public sealed record HidInterfaceDescriptor(
    string DevicePath,
    uint VendorId,
    uint ProductId,
    ushort UsagePage,
    ushort Usage,
    ushort InputReportByteLength,
    bool CanOpenForRead,
    int ReadOpenError);

public sealed class HidReportEventArgs : EventArgs
{
    public HidReportEventArgs(ushort usagePage, ushort usage, byte[] report, TimeSpan timestamp)
    {
        UsagePage = usagePage;
        Usage = usage;
        Report = report;
        Timestamp = timestamp;
    }

    public ushort UsagePage { get; }

    public ushort Usage { get; }

    public byte[] Report { get; }

    public TimeSpan Timestamp { get; }
}

public interface IHidReportSource : IAsyncDisposable
{
    event EventHandler<HidReportEventArgs>? ReportReceived;

    event EventHandler<bool>? ConnectionChanged;

    event EventHandler<string>? Diagnostic;

    bool IsConnected { get; }

    void Start();

    Task StopAsync();
}

public sealed class HidInterfaceReportSource : IHidReportSource
{
    private readonly HidButtonBinding binding;
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource lifetime = new();
    private Task? worker;
    private long startTimestamp;
    private bool connected;
    private bool disposed;

    public HidInterfaceReportSource(HidButtonBinding binding, TimeProvider? timeProvider = null)
    {
        this.binding = binding ?? throw new ArgumentNullException(nameof(binding));
        if (binding.Transport != HidTransport.HidInterface)
        {
            throw new ArgumentException("Binding transport must be HidInterface.", nameof(binding));
        }

        IReadOnlyList<string> errors = binding.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(' ', errors), nameof(binding));
        }

        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<HidReportEventArgs>? ReportReceived;

    public event EventHandler<bool>? ConnectionChanged;

    public event EventHandler<string>? Diagnostic;

    public bool IsConnected => connected;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (worker is null)
        {
            startTimestamp = timeProvider.GetTimestamp();
            worker = Task.Run(() => RunAsync(lifetime.Token));
        }
    }

    public async Task StopAsync()
    {
        if (worker is null)
        {
            return;
        }

        lifetime.Cancel();
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        worker = null;
        SetConnected(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await StopAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HidInterfaceDescriptor? descriptor;
            try
            {
                descriptor = HidDeviceDiscovery.Enumerate(binding.HardwareId)
                    .FirstOrDefault(item =>
                        item.UsagePage == binding.UsagePage &&
                        item.Usage == binding.Usage &&
                        item.CanOpenForRead &&
                        item.InputReportByteLength > 0);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Diagnostic?.Invoke(this, $"HID enumeration failed: {exception.Message}");
                descriptor = null;
            }

            if (descriptor is null)
            {
                SetConnected(false);
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken).ConfigureAwait(false);
                continue;
            }

            using SafeFileHandle handle = HidDeviceDiscovery.OpenForRead(descriptor.DevicePath);
            if (handle.IsInvalid)
            {
                SetConnected(false);
                Diagnostic?.Invoke(this, $"HID open failed: Win32={Marshal.GetLastWin32Error()}.");
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                using FileStream stream = new(
                    handle,
                    FileAccess.Read,
                    descriptor.InputReportByteLength,
                    isAsync: true);
                SetConnected(true);
                byte[] buffer = new byte[descriptor.InputReportByteLength];
                while (!cancellationToken.IsCancellationRequested)
                {
                    int bytesRead = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    byte[] report = buffer.AsSpan(0, bytesRead).ToArray();
                    ReportReceived?.Invoke(
                        this,
                        new HidReportEventArgs(
                            descriptor.UsagePage,
                            descriptor.Usage,
                            report,
                            timeProvider.GetElapsedTime(startTimestamp, timeProvider.GetTimestamp())));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Diagnostic?.Invoke(this, $"HID read stopped: {exception.Message}");
            }
            finally
            {
                SetConnected(false);
            }
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
}

public static class HidDeviceDiscovery
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

    public static IReadOnlyList<HidInterfaceDescriptor> Enumerate(string hardwareId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareId);
        HidNativeMethods.HidDGetHidGuid(out Guid hidGuid);
        nint deviceInfoSet = HidNativeMethods.SetupDiGetClassDevs(
            ref hidGuid,
            nint.Zero,
            nint.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (deviceInfoSet == new nint(-1))
        {
            throw new InvalidOperationException($"SetupDiGetClassDevs failed, Win32={Marshal.GetLastWin32Error()}.");
        }

        try
        {
            List<HidInterfaceDescriptor> descriptors = [];
            for (uint index = 0; ; index++)
            {
                DeviceInterfaceData interfaceData = new()
                {
                    Size = (uint)Marshal.SizeOf<DeviceInterfaceData>()
                };
                if (!HidNativeMethods.SetupDiEnumDeviceInterfaces(
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

                    throw new InvalidOperationException($"SetupDiEnumDeviceInterfaces failed, Win32={error}.");
                }

                string path = GetInterfacePath(deviceInfoSet, ref interfaceData);
                if (path.Contains(hardwareId, StringComparison.OrdinalIgnoreCase))
                {
                    descriptors.Add(Inspect(path));
                }
            }

            return descriptors;
        }
        finally
        {
            HidNativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    public static SafeFileHandle OpenForRead(string path) => HidNativeMethods.CreateFile(
        path,
        GenericRead,
        FileShareRead | FileShareWrite,
        nint.Zero,
        OpenExisting,
        FileFlagOverlapped,
        nint.Zero);

    private static string GetInterfacePath(nint deviceInfoSet, ref DeviceInterfaceData interfaceData)
    {
        HidNativeMethods.SetupDiGetDeviceInterfaceDetail(
            deviceInfoSet,
            ref interfaceData,
            nint.Zero,
            0,
            out uint requiredSize,
            nint.Zero);
        if (requiredSize == 0)
        {
            throw new InvalidOperationException($"SetupDiGetDeviceInterfaceDetail(size) failed, Win32={Marshal.GetLastWin32Error()}.");
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)requiredSize));
        try
        {
            Marshal.WriteInt32(buffer, nint.Size == 8 ? 8 : 6);
            if (!HidNativeMethods.SetupDiGetDeviceInterfaceDetail(
                deviceInfoSet,
                ref interfaceData,
                buffer,
                requiredSize,
                out _,
                nint.Zero))
            {
                throw new InvalidOperationException($"SetupDiGetDeviceInterfaceDetail(data) failed, Win32={Marshal.GetLastWin32Error()}.");
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
        using SafeFileHandle metadataHandle = HidNativeMethods.CreateFile(
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
            return new HidInterfaceDescriptor(path, 0, 0, 0, 0, 0, false, error);
        }

        HidAttributes attributes = new() { Size = Marshal.SizeOf<HidAttributes>() };
        if (!HidNativeMethods.HidDGetAttributes(metadataHandle, ref attributes))
        {
            throw new InvalidOperationException($"HidD_GetAttributes failed, Win32={Marshal.GetLastWin32Error()}.");
        }

        if (!HidNativeMethods.HidDGetPreparsedData(metadataHandle, out nint preparsedData))
        {
            throw new InvalidOperationException($"HidD_GetPreparsedData failed, Win32={Marshal.GetLastWin32Error()}.");
        }

        HidCaps caps;
        try
        {
            int status = HidNativeMethods.HidPGetCaps(preparsedData, out caps);
            if (status != HidpStatusSuccess)
            {
                throw new InvalidOperationException($"HidP_GetCaps failed, NTSTATUS=0x{status:X8}.");
            }
        }
        finally
        {
            HidNativeMethods.HidDFreePreparsedData(preparsedData);
        }

        using SafeFileHandle readHandle = OpenForRead(path);
        int readError = readHandle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        return new HidInterfaceDescriptor(
            path,
            attributes.VendorId,
            attributes.ProductId,
            caps.UsagePage,
            caps.Usage,
            caps.InputReportByteLength,
            !readHandle.IsInvalid,
            readError);
    }
}

internal static class HidNativeMethods
{
    [DllImport("hid.dll", EntryPoint = "HidD_GetHidGuid")]
    internal static extern void HidDGetHidGuid(out Guid hidGuid);

    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode)]
    internal static extern nint SetupDiGetClassDevs(ref Guid classGuid, nint enumerator, nint parentWindow, uint flags);

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
