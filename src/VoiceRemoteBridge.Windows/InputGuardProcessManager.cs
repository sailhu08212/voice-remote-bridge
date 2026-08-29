using System.Diagnostics;
using VoiceRemoteBridge.Core;

namespace VoiceRemoteBridge.Windows;

public sealed record GuardLaunchInfo(
    string FileName,
    IReadOnlyList<string> PrefixArguments);

public sealed class InputGuardProcessManager : IAsyncDisposable
{
    private readonly GuardLaunchInfo launchInfo;
    private readonly TimeSpan leaseDuration;
    private readonly TimeSpan connectTimeout;
    private Process? process;
    private InputGuardClient? client;
    private string? sessionToken;
    private bool disposed;

    public InputGuardProcessManager(
        GuardLaunchInfo launchInfo,
        TimeSpan leaseDuration,
        TimeSpan connectTimeout)
    {
        this.launchInfo = launchInfo ?? throw new ArgumentNullException(nameof(launchInfo));
        this.leaseDuration = leaseDuration > TimeSpan.Zero
            ? leaseDuration
            : throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        this.connectTimeout = connectTimeout > TimeSpan.Zero
            ? connectTimeout
            : throw new ArgumentOutOfRangeException(nameof(connectTimeout));
    }

    public event EventHandler? GuardExited;

    public bool IsRunning => process is { HasExited: false } && client?.IsConnected == true;

    public int? ProcessId => process is { HasExited: false } ? process.Id : null;

    public InputGuardClient Client => client ?? throw new InvalidOperationException("InputGuard is not running.");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsRunning)
        {
            return;
        }

        await CleanupProcessAsync().ConfigureAwait(false);
        sessionToken = Guid.NewGuid().ToString("N");
        ProcessStartInfo startInfo = new()
        {
            FileName = launchInfo.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in launchInfo.PrefixArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--session");
        startInfo.ArgumentList.Add(sessionToken);
        startInfo.ArgumentList.Add("--parent");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--lease-ms");
        startInfo.ArgumentList.Add(checked((int)leaseDuration.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture));

        process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start InputGuard.");
        process.EnableRaisingEvents = true;
        process.Exited += ProcessExited;
        client = new InputGuardClient(GuardProtocol.BuildPipeName(sessionToken));
        try
        {
            await client.ConnectAsync(connectTimeout, cancellationToken).ConfigureAwait(false);
            GuardResponse ping = await client.PingAsync(cancellationToken).ConfigureAwait(false);
            if (!ping.Succeeded)
            {
                throw new InvalidOperationException($"InputGuard ping failed: {ping.Message}");
            }
        }
        catch
        {
            await CleanupProcessAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync(long epoch, CancellationToken cancellationToken = default)
    {
        if (client?.IsConnected == true)
        {
            try
            {
                await client.ShutdownAsync(epoch, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
            }
        }

        if (process is { HasExited: false })
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                // Do not kill here: a still-running guard is safer than removing the last process
                // capable of releasing a held key. Cleanup on disposal only after release is proven.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await StopAsync(epoch: long.MaxValue).ConfigureAwait(false);
        await CleanupProcessAsync().ConfigureAwait(false);
    }

    private async Task CleanupProcessAsync()
    {
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            client = null;
        }

        if (process is not null)
        {
            process.Exited -= ProcessExited;
            process.Dispose();
            process = null;
        }

        sessionToken = null;
    }

    private void ProcessExited(object? sender, EventArgs eventArgs) => GuardExited?.Invoke(this, EventArgs.Empty);
}
