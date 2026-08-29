using VoiceRemoteBridge.Core;

namespace VoiceRemoteBridge.Windows;

public sealed record AdapterExecutionResult(
    bool Succeeded,
    string Message,
    int Win32Error = 0)
{
    public static AdapterExecutionResult Success(string message) => new(true, message);

    public static AdapterExecutionResult Failure(string message, int win32Error = 0) =>
        new(false, message, win32Error);
}

public sealed class AdapterExecutionService : IAsyncDisposable
{
    private readonly Win32KeyInjector injector;
    private readonly Func<InputGuardProcessManager> guardFactory;
    private readonly GuardRestartPolicy guardRestartPolicy;
    private readonly TimeProvider timeProvider;
    private InputGuardProcessManager? guard;
    private AdapterProfile? activeProfile;
    private long activeEpoch;
    private DateTimeOffset guardStartedAt = DateTimeOffset.MinValue;
    private bool startingGuard;
    private bool disposed;

    public AdapterExecutionService(
        Func<InputGuardProcessManager> guardFactory,
        Win32KeyInjector? injector = null,
        GuardRestartPolicy? guardRestartPolicy = null,
        TimeProvider? timeProvider = null)
    {
        this.guardFactory = guardFactory ?? throw new ArgumentNullException(nameof(guardFactory));
        this.injector = injector ?? new Win32KeyInjector();
        this.guardRestartPolicy = guardRestartPolicy ?? new GuardRestartPolicy();
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler? GuardExited;

    public IReadOnlyCollection<ushort> LocallyHeldKeys => injector.HeldKeys;

    public bool GuardIsRunning => guard?.IsRunning == true;

    public GuardRestartSnapshot GuardRestart => guardRestartPolicy.Snapshot;

    public async Task<AdapterExecutionResult> StartAsync(
        AdapterProfile profile,
        long epoch,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(profile);
        IReadOnlyList<string> errors = profile.Validate();
        if (errors.Count > 0)
        {
            return AdapterExecutionResult.Failure(string.Join(" ", errors));
        }

        if (activeProfile is not null)
        {
            return AdapterExecutionResult.Failure("An adapter action is already active.");
        }

        KeyInjectionMode mode = ParseMode(profile.InjectionMode);
        AdapterExecutionResult result;
        if (profile.InjectionLifetime == InjectionLifetime.HeldAcrossSpeech)
        {
            try
            {
                await EnsureGuardAsync(cancellationToken).ConfigureAwait(false);
                GuardResponse response = await guard!.Client.HoldAsync(
                    epoch,
                    profile.StartChord,
                    mode,
                    cancellationToken).ConfigureAwait(false);
                result = response.Succeeded
                    ? AdapterExecutionResult.Success("InputGuard is holding the adapter chord.")
                    : AdapterExecutionResult.Failure(response.Message, response.Win32Error);
            }
            catch (Exception exception) when (
                exception is IOException or
                InvalidOperationException or
                OperationCanceledException or
                System.ComponentModel.Win32Exception)
            {
                result = AdapterExecutionResult.Failure($"InputGuard start failed: {exception.Message}");
            }
        }
        else
        {
            InjectionResult injection = injector.Tap(profile.StartChord, mode);
            result = FromInjection(injection, "Adapter start chord sent.");
        }

        if (result.Succeeded)
        {
            activeProfile = profile;
            activeEpoch = epoch;
        }

        return result;
    }

    public async Task<AdapterExecutionResult> RenewAsync(
        long epoch,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (activeProfile?.InjectionLifetime != InjectionLifetime.HeldAcrossSpeech || activeEpoch != epoch)
        {
            return AdapterExecutionResult.Success("No guard lease requires renewal.");
        }

        try
        {
            GuardResponse response = await guard!.Client.RenewAsync(epoch, cancellationToken).ConfigureAwait(false);
            return response.Succeeded
                ? AdapterExecutionResult.Success("InputGuard lease renewed.")
                : AdapterExecutionResult.Failure(response.Message, response.Win32Error);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        {
            return AdapterExecutionResult.Failure($"InputGuard lease renewal failed: {exception.Message}");
        }
    }

    public async Task<AdapterExecutionResult> StopAsync(
        AdapterProfile profile,
        long epoch,
        bool emergency,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(profile);
        if (activeProfile is null || activeEpoch != epoch)
        {
            return emergency
                ? await EmergencyReleaseAllAsync(epoch, cancellationToken).ConfigureAwait(false)
                : AdapterExecutionResult.Failure("The requested adapter epoch is not active.");
        }

        KeyInjectionMode mode = ParseMode(profile.InjectionMode);
        AdapterExecutionResult result;

        if (profile.InjectionLifetime == InjectionLifetime.HeldAcrossSpeech)
        {
            result = await ReleaseGuardAsync(epoch, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                InjectionResult fallback = injector.ReleaseSpecific(profile.StartChord, mode);
                result = fallback.Succeeded
                    ? AdapterExecutionResult.Success("InputGuard was unavailable; an explicit Key Up fallback was sent.")
                    : AdapterExecutionResult.Failure(
                        $"{result.Message} Key Up fallback failed: {fallback.Message}",
                        fallback.Win32Error);
            }
        }
        else if (profile.TriggerModel == TriggerModel.TapOnHold)
        {
            result = AdapterExecutionResult.Success("TapOnHold requires no release action.");
        }
        else
        {
            IReadOnlyList<ushort> stopChord = profile.TriggerModel == TriggerModel.StartStopPair
                ? profile.StopChord
                : profile.StartChord;
            InjectionResult injection = injector.Tap(stopChord, mode);
            result = FromInjection(injection, emergency
                ? "Emergency adapter stop chord sent."
                : "Adapter stop chord sent.");
        }

        if (result.Succeeded || emergency)
        {
            activeProfile = null;
            activeEpoch = 0;
        }

        return result;
    }

    public async Task<AdapterExecutionResult> AbandonStartAsync(
        long epoch,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (activeProfile is null)
        {
            return AdapterExecutionResult.Success("No active adapter start requires rollback.");
        }

        if (activeEpoch != epoch)
        {
            return AdapterExecutionResult.Failure("The requested adapter epoch is not active.");
        }

        List<string> failures = [];
        int win32Error = 0;
        if (activeProfile.InjectionLifetime == InjectionLifetime.HeldAcrossSpeech)
        {
            AdapterExecutionResult guardResult = await ReleaseGuardAsync(epoch, cancellationToken).ConfigureAwait(false);
            if (!guardResult.Succeeded)
            {
                InjectionResult fallback = injector.ReleaseSpecific(
                    activeProfile.StartChord,
                    ParseMode(activeProfile.InjectionMode));
                if (!fallback.Succeeded)
                {
                    failures.Add($"Key Up fallback failed: {fallback.Message}");
                    win32Error = fallback.Win32Error;
                }
            }
        }

        InjectionResult local = injector.ReleaseAll(KeyInjectionMode.VirtualKey);
        if (!local.Succeeded)
        {
            failures.Add(local.Message);
            win32Error = local.Win32Error;
        }

        activeProfile = null;
        activeEpoch = 0;
        return failures.Count == 0
            ? AdapterExecutionResult.Success("Adapter start state cleared without sending a stop chord.")
            : AdapterExecutionResult.Failure(string.Join(" ", failures), win32Error);
    }

    public async Task<AdapterExecutionResult> EmergencyReleaseAllAsync(
        long epoch,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        List<string> failures = [];
        int win32Error = 0;

        bool guardReleased = false;
        if (guard?.IsRunning == true)
        {
            AdapterExecutionResult guardResult = await ReleaseGuardAsync(epoch, cancellationToken).ConfigureAwait(false);
            if (!guardResult.Succeeded)
            {
                failures.Add(guardResult.Message);
                win32Error = guardResult.Win32Error;
            }
            else
            {
                guardReleased = true;
            }
        }

        if (!guardReleased && activeProfile?.InjectionLifetime == InjectionLifetime.HeldAcrossSpeech)
        {
            InjectionResult fallback = injector.ReleaseSpecific(
                activeProfile.StartChord,
                ParseMode(activeProfile.InjectionMode));
            if (!fallback.Succeeded)
            {
                failures.Add($"Key Up fallback failed: {fallback.Message}");
                win32Error = fallback.Win32Error;
            }
        }

        InjectionResult local = injector.ReleaseAll(KeyInjectionMode.VirtualKey);
        if (!local.Succeeded)
        {
            failures.Add(local.Message);
            win32Error = local.Win32Error;
        }

        activeProfile = null;
        activeEpoch = 0;
        return failures.Count == 0
            ? AdapterExecutionResult.Success("All registered injected keys were released.")
            : AdapterExecutionResult.Failure(string.Join(" ", failures), win32Error);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await EmergencyReleaseAllAsync(activeEpoch).ConfigureAwait(false);
        disposed = true;
        if (guard is not null)
        {
            guard.GuardExited -= Guard_ProcessExited;
            await guard.DisposeAsync().ConfigureAwait(false);
            guard = null;
        }
    }

    private async Task EnsureGuardAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        guardRestartPolicy.ObserveHealthy(now, guardStartedAt);
        if (guard?.IsRunning == true)
        {
            return;
        }

        if (!guardRestartPolicy.CanAttempt(now, out TimeSpan wait))
        {
            string detail = wait == Timeout.InfiniteTimeSpan
                ? "InputGuard reached five consecutive failures; restart the main app after correcting the cause."
                : $"InputGuard restart backoff is active for {Math.Ceiling(wait.TotalSeconds)} more second(s).";
            throw new InvalidOperationException(detail);
        }

        if (guard is not null)
        {
            guard.GuardExited -= Guard_ProcessExited;
            await guard.DisposeAsync().ConfigureAwait(false);
        }

        guard = guardFactory();
        guard.GuardExited += Guard_ProcessExited;
        startingGuard = true;
        try
        {
            await guard.StartAsync(cancellationToken).ConfigureAwait(false);
            guardStartedAt = timeProvider.GetUtcNow();
        }
        catch
        {
            guardRestartPolicy.RecordFailure(timeProvider.GetUtcNow());
            throw;
        }
        finally
        {
            startingGuard = false;
        }
    }

    private async Task<AdapterExecutionResult> ReleaseGuardAsync(
        long epoch,
        CancellationToken cancellationToken)
    {
        if (guard?.IsRunning != true)
        {
            return AdapterExecutionResult.Failure("InputGuard is not connected; local Key Up fallback was requested.");
        }

        try
        {
            GuardResponse response = await guard.Client.ReleaseAsync(epoch, cancellationToken).ConfigureAwait(false);
            return response.Succeeded
                ? AdapterExecutionResult.Success("InputGuard released the adapter chord.")
                : AdapterExecutionResult.Failure(response.Message, response.Win32Error);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        {
            return AdapterExecutionResult.Failure($"InputGuard release failed: {exception.Message}");
        }
    }

    private static AdapterExecutionResult FromInjection(InjectionResult injection, string successMessage) =>
        injection.Succeeded
            ? AdapterExecutionResult.Success(successMessage)
            : AdapterExecutionResult.Failure(injection.Message, injection.Win32Error);

    private static KeyInjectionMode ParseMode(string mode) => Enum.Parse<KeyInjectionMode>(mode, ignoreCase: false);

    private void Guard_ProcessExited(object? sender, EventArgs eventArgs)
    {
        if (!startingGuard)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            guardRestartPolicy.ObserveHealthy(now, guardStartedAt);
            guardRestartPolicy.RecordFailure(now);
        }

        if (activeProfile?.InjectionLifetime == InjectionLifetime.HeldAcrossSpeech)
        {
            injector.ReleaseSpecific(activeProfile.StartChord, ParseMode(activeProfile.InjectionMode));
        }

        GuardExited?.Invoke(this, EventArgs.Empty);
    }
}
