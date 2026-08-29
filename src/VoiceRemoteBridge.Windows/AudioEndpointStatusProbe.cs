using System.Runtime.InteropServices;
using System.Text;

namespace VoiceRemoteBridge.Windows;

public sealed record AudioEndpointStatus(
    bool Found,
    string FriendlyName,
    string EndpointId,
    bool IsMuted,
    float CurrentPeak,
    string Message);

public static class AudioEndpointStatusProbe
{
    private const uint DeviceStateActive = 0x00000001;
    private const uint StgmRead = 0;
    private const uint ClsctxAll = 23;
    private const ushort VtLpwstr = 31;
    private static readonly Guid AudioMeterInterfaceId = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    private static readonly Guid EndpointVolumeInterfaceId = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly AudioPropertyKey DeviceFriendlyName = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        14);

    public static AudioEndpointStatus FindCaptureEndpoint(string requestedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedName);
        int initializeResult = AudioEndpointNativeMethods.CoInitializeEx(nint.Zero, 0x2);
        bool uninitialize = initializeResult >= 0;
        if (initializeResult < 0 && initializeResult != unchecked((int)0x80010106))
        {
            Marshal.ThrowExceptionForHR(initializeResult);
        }

        IAudioDeviceEnumerator? enumerator = null;
        IAudioDeviceCollection? collection = null;
        IAudioDevice? selected = null;
        object? meterObject = null;
        object? volumeObject = null;
        try
        {
            enumerator = (IAudioDeviceEnumerator)(object)new AudioDeviceEnumeratorComObject();
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(AudioDataFlow.Capture, DeviceStateActive, out collection));
            Marshal.ThrowExceptionForHR(collection.GetCount(out uint count));
            string friendlyName = string.Empty;
            for (uint index = 0; index < count; index++)
            {
                Marshal.ThrowExceptionForHR(collection.Item(index, out IAudioDevice candidate));
                string candidateName = GetFriendlyName(candidate);
                if (candidateName.Contains(requestedName, StringComparison.OrdinalIgnoreCase))
                {
                    selected = candidate;
                    friendlyName = candidateName;
                    break;
                }

                Marshal.FinalReleaseComObject(candidate);
            }

            if (selected is null)
            {
                return new AudioEndpointStatus(
                    false,
                    string.Empty,
                    string.Empty,
                    false,
                    0,
                    $"未找到活动录音端点：{requestedName}。");
            }

            Marshal.ThrowExceptionForHR(selected.GetId(out string endpointId));
            Guid meterId = AudioMeterInterfaceId;
            Marshal.ThrowExceptionForHR(selected.Activate(ref meterId, ClsctxAll, nint.Zero, out meterObject));
            IAudioMeter meter = (IAudioMeter)meterObject;
            Marshal.ThrowExceptionForHR(meter.GetPeakValue(out float peak));

            Guid volumeId = EndpointVolumeInterfaceId;
            Marshal.ThrowExceptionForHR(selected.Activate(ref volumeId, ClsctxAll, nint.Zero, out volumeObject));
            IAudioEndpointVolume volume = (IAudioEndpointVolume)volumeObject;
            Marshal.ThrowExceptionForHR(volume.GetMute(out bool muted));
            return new AudioEndpointStatus(
                true,
                friendlyName,
                endpointId,
                muted,
                peak,
                muted ? "录音端点在线但处于静音状态。" : "录音端点在线且未静音。");
        }
        finally
        {
            ReleaseCom(volumeObject);
            ReleaseCom(meterObject);
            ReleaseCom(selected);
            ReleaseCom(collection);
            ReleaseCom(enumerator);
            if (uninitialize)
            {
                AudioEndpointNativeMethods.CoUninitialize();
            }
        }
    }

    internal static string GetFriendlyName(IAudioDevice device)
    {
        Marshal.ThrowExceptionForHR(device.OpenPropertyStore(StgmRead, out IAudioPropertyStore store));
        try
        {
            AudioPropertyKey key = DeviceFriendlyName;
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out AudioPropVariant value));
            try
            {
                return value.VarType == VtLpwstr && value.PointerValue != nint.Zero
                    ? Marshal.PtrToStringUni(value.PointerValue) ?? string.Empty
                    : string.Empty;
            }
            finally
            {
                AudioEndpointNativeMethods.PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(store);
        }
    }

    internal static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}

internal static class AudioEndpointNativeMethods
{
    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(nint reserved, uint coInit);

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(ref AudioPropVariant variant);
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct AudioPropertyKey
{
    internal AudioPropertyKey(Guid formatId, uint propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }

    internal readonly Guid FormatId;
    internal readonly uint PropertyId;
}

[StructLayout(LayoutKind.Explicit)]
internal struct AudioPropVariant
{
    [FieldOffset(0)]
    internal ushort VarType;

    [FieldOffset(8)]
    internal nint PointerValue;
}

internal enum AudioDataFlow
{
    Render,
    Capture,
    All
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal sealed class AudioDeviceEnumeratorComObject
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(AudioDataFlow dataFlow, uint stateMask, out IAudioDeviceCollection devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(AudioDataFlow dataFlow, int role, out IAudioDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IAudioDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(nint client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(nint client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int Item(uint index, out IAudioDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioDevice
{
    [PreserveSig]
    int Activate(
        ref Guid interfaceId,
        uint classContext,
        nint activationParameters,
        [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    [PreserveSig]
    int OpenPropertyStore(uint accessMode, out IAudioPropertyStore properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out uint state);
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioPropertyStore
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, out AudioPropertyKey key);

    [PreserveSig]
    int GetValue(ref AudioPropertyKey key, out AudioPropVariant value);

    [PreserveSig]
    int SetValue(ref AudioPropertyKey key, ref AudioPropVariant value);

    [PreserveSig]
    int Commit();
}

[ComImport]
[Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeter
{
    [PreserveSig]
    int GetPeakValue(out float peak);

    [PreserveSig]
    int GetMeteringChannelCount(out int count);

    [PreserveSig]
    int GetChannelsPeakValues(int count, [Out] float[] peaks);

    [PreserveSig]
    int QueryHardwareSupport(out int mask);
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(nint notify);

    [PreserveSig]
    int UnregisterControlChangeNotify(nint notify);

    [PreserveSig]
    int GetChannelCount(out uint count);

    [PreserveSig]
    int SetMasterVolumeLevel(float levelDb, nint eventContext);

    [PreserveSig]
    int SetMasterVolumeLevelScalar(float level, nint eventContext);

    [PreserveSig]
    int GetMasterVolumeLevel(out float levelDb);

    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);

    [PreserveSig]
    int SetChannelVolumeLevel(uint channel, float levelDb, nint eventContext);

    [PreserveSig]
    int SetChannelVolumeLevelScalar(uint channel, float level, nint eventContext);

    [PreserveSig]
    int GetChannelVolumeLevel(uint channel, out float levelDb);

    [PreserveSig]
    int GetChannelVolumeLevelScalar(uint channel, out float level);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, nint eventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
}
