using System.Reflection;
using Microsoft.Win32;

namespace VoiceRemoteBridge.Windows;

public interface IStartupRegistrationStore
{
    string? Read();

    void Write(string command);

    void Delete();
}

public sealed record StartupRegistrationState(
    bool IsRegistered,
    string? Command,
    string? Error = null);

public sealed record StartupRegistrationResult(
    bool Succeeded,
    bool Enabled,
    string Message,
    string? Command = null);

public static class StartupCommandBuilder
{
    public const string BackgroundArgument = "--background";

    public static string Build(string processPath, string? entryAssemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        List<string> arguments = [Quote(Path.GetFullPath(processPath))];
        string processName = Path.GetFileName(processPath);
        if (string.Equals(processName, "dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(processName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entryAssemblyPath);
            arguments.Add(Quote(Path.GetFullPath(entryAssemblyPath)));
        }

        arguments.Add(BackgroundArgument);
        return string.Join(' ', arguments);
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}

public sealed class WindowsStartupRegistration
{
    public const string ValueName = "VoiceRemoteBridge";
    private readonly IStartupRegistrationStore store;
    private readonly string command;

    public WindowsStartupRegistration(IStartupRegistrationStore store, string command)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        this.command = command;
    }

    public static WindowsStartupRegistration CreateForCurrentProcess()
    {
        string processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Current process path is unavailable.");
        string? entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
        string launchCommand = StartupCommandBuilder.Build(processPath, entryAssemblyPath);
        return new WindowsStartupRegistration(new CurrentUserRunStartupStore(), launchCommand);
    }

    public StartupRegistrationState ReadState()
    {
        try
        {
            string? current = store.Read();
            return new StartupRegistrationState(
                string.Equals(current, command, StringComparison.OrdinalIgnoreCase),
                current);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            IOException or
            System.Security.SecurityException)
        {
            return new StartupRegistrationState(
                false,
                null,
                $"无法读取开机自启动设置：{exception.Message}");
        }
    }

    public StartupRegistrationResult Apply(bool enabled)
    {
        try
        {
            if (enabled)
            {
                store.Write(command);
            }
            else
            {
                store.Delete();
            }

            string? actual = store.Read();
            bool matches = enabled
                ? string.Equals(actual, command, StringComparison.OrdinalIgnoreCase)
                : actual is null;
            return matches
                ? new StartupRegistrationResult(
                    true,
                    enabled,
                    enabled ? "开机自启动已开启。" : "开机自启动已关闭。",
                    actual)
                : new StartupRegistrationResult(
                    false,
                    enabled,
                    "Windows 启动项写入后校验失败。",
                    actual);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            IOException or
            System.Security.SecurityException)
        {
            return new StartupRegistrationResult(
                false,
                enabled,
                $"开机自启动设置失败：{exception.Message}");
        }
    }

    private sealed class CurrentUserRunStartupStore : IStartupRegistrationStore
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public string? Read()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }

        public void Write(string command)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new IOException("无法打开当前用户的 Windows 启动项。");
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }

        public void Delete()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
