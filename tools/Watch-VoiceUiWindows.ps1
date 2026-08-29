[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [ValidateRange(1, 1800)]
    [int]$DurationSeconds = 300,

    [ValidateRange(25, 1000)]
    [int]$PollMilliseconds = 100
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public sealed class VoiceUiWindowDescriptor
{
    public long Handle { get; set; }
    public long ParentHandle { get; set; }
    public int ProcessId { get; set; }
    public string ProcessName { get; set; }
    public string ClassName { get; set; }
    public bool Visible { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }

    public string Signature
    {
        get
        {
            return string.Format(
                "{0:X}:{1}:{2}:{3}:{4}:{5},{6},{7},{8}",
                ParentHandle,
                ProcessId,
                ProcessName,
                ClassName,
                Visible,
                Left,
                Top,
                Right,
                Bottom);
        }
    }
}

public static class VoiceUiDesktopProbe
{
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        IntPtr state);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);

    public static VoiceUiWindowDescriptor[] Capture()
    {
        List<VoiceUiWindowDescriptor> result = new List<VoiceUiWindowDescriptor>();
        Dictionary<int, string> processNames = new Dictionary<int, string>();
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                processNames[process.Id] = process.ProcessName;
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        EnumWindows(
            (window, state) =>
            {
                NativeRectangle rectangle;
                if (!GetWindowRect(window, out rectangle))
                {
                    return true;
                }

                int width = rectangle.Right - rectangle.Left;
                int height = rectangle.Bottom - rectangle.Top;
                if (width <= 0 || height <= 0 || width > 1600 || height > 1600)
                {
                    return true;
                }

                uint processId;
                GetWindowThreadProcessId(window, out processId);
                string processName = "unknown";
                string capturedProcessName;
                if (processNames.TryGetValue((int)processId, out capturedProcessName))
                {
                    processName = capturedProcessName;
                }

                StringBuilder className = new StringBuilder(256);
                GetClassName(window, className, className.Capacity);
                result.Add(new VoiceUiWindowDescriptor
                {
                    Handle = window.ToInt64(),
                    ParentHandle = GetParent(window).ToInt64(),
                    ProcessId = (int)processId,
                    ProcessName = processName,
                    ClassName = className.ToString(),
                    Visible = IsWindowVisible(window),
                    Left = rectangle.Left,
                    Top = rectangle.Top,
                    Right = rectangle.Right,
                    Bottom = rectangle.Bottom
                });
                return true;
            }, IntPtr.Zero);
        return result.ToArray();
    }
}
'@

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutput)
if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
    throw 'OutputPath must include a directory.'
}

[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$utf8 = [System.Text.UTF8Encoding]::new($false)
$writer = [System.IO.StreamWriter]::new($resolvedOutput, $false, $utf8)
try {
    $known = @{}
    foreach ($window in [VoiceUiDesktopProbe]::Capture()) {
        $known[[string]$window.Handle] = $window
    }

    $header = [ordered]@{
        timestamp = [DateTimeOffset]::Now.ToString('O')
        event = 'baseline'
        windowCount = $known.Count
    }
    $writer.WriteLine(($header | ConvertTo-Json -Compress))
    $writer.Flush()

    $deadline = [DateTimeOffset]::Now.AddSeconds($DurationSeconds)
    while ([DateTimeOffset]::Now -lt $deadline) {
        $current = @{}
        foreach ($window in [VoiceUiDesktopProbe]::Capture()) {
            $current[[string]$window.Handle] = $window
        }

        foreach ($key in $current.Keys) {
            $window = $current[$key]
            if (-not $known.ContainsKey($key)) {
                $eventName = 'added'
            }
            elseif ($known[$key].Signature -ne $window.Signature) {
                $eventName = 'changed'
            }
            else {
                continue
            }

            $entry = [ordered]@{
                timestamp = [DateTimeOffset]::Now.ToString('O')
                event = $eventName
                handle = ('0x{0:X}' -f $window.Handle)
                parentHandle = ('0x{0:X}' -f $window.ParentHandle)
                processId = $window.ProcessId
                processName = $window.ProcessName
                className = $window.ClassName
                visible = $window.Visible
                rectangle = @($window.Left, $window.Top, $window.Right, $window.Bottom)
            }
            $writer.WriteLine(($entry | ConvertTo-Json -Compress))
        }

        foreach ($key in $known.Keys) {
            if ($current.ContainsKey($key)) {
                continue
            }

            $window = $known[$key]
            $entry = [ordered]@{
                timestamp = [DateTimeOffset]::Now.ToString('O')
                event = 'removed'
                handle = ('0x{0:X}' -f $window.Handle)
                parentHandle = ('0x{0:X}' -f $window.ParentHandle)
                processId = $window.ProcessId
                processName = $window.ProcessName
                className = $window.ClassName
                visible = $window.Visible
                rectangle = @($window.Left, $window.Top, $window.Right, $window.Bottom)
            }
            $writer.WriteLine(($entry | ConvertTo-Json -Compress))
        }

        $writer.Flush()
        $known = $current
        Start-Sleep -Milliseconds $PollMilliseconds
    }
}
finally {
    $writer.Dispose()
}
