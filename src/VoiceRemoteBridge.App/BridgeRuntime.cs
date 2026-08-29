using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using VoiceRemoteBridge.Core;
using VoiceRemoteBridge.Windows;

namespace VoiceRemoteBridge.App;

internal enum RuntimeStatusLevel
{
    Information,
    Warning,
    Error
}

internal sealed record RuntimeStatus(
    RuntimeStatusLevel Level,
    string Message,
    BridgeSnapshot Snapshot);

internal sealed class BridgeRuntime : IAsyncDisposable
{
    private readonly AppSettings settings;
    private readonly AdapterProfile profile;
    private readonly HidSignalDecoder decoder;
    private readonly IHidReportSource reportSource;
    private readonly AdapterExecutionService execution;
    private readonly InputMethodProfileManager inputMethodProfileManager;
    private readonly InputMethodSessionController inputMethodSession;
    private readonly BridgeStateMachine stateMachine;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Channel<RuntimeMessage> messages = Channel.CreateUnbounded<RuntimeMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource tickLifetime = new();
    private readonly Dictionary<long, FocusSnapshot> focusSnapshots = [];
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private Task? messageLoop;
    private Task? tickLoop;
    private AudioCarrierActivationSession? activationSession;
    private BridgeSnapshot? lastPublishedSnapshot;
    private TimeSpan lastLeaseRenewal;
    private TimeSpan? voiceUiConfirmationDeadline;
    private long pendingStartEpoch;
    private bool started;
    private bool disposed;

    internal BridgeRuntime(AppSettings settings)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        IReadOnlyList<string> errors = settings.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(settings));
        }

        HidButtonBinding binding = settings.VoiceButton
            ?? throw new ArgumentException("A learned voice-button binding is required.", nameof(settings));
        profile = settings.SelectedAdapter
            ?? throw new ArgumentException("A validated adapter profile is required.", nameof(settings));
        decoder = new HidSignalDecoder(binding);
        reportSource = binding.Transport switch
        {
            HidTransport.HidInterface => new HidInterfaceReportSource(binding),
            HidTransport.RawInput => new RawInputReportSource(binding),
            _ => throw new ArgumentOutOfRangeException(nameof(binding.Transport))
        };
        execution = new AdapterExecutionService(GuardLaunchLocator.CreateManager);
        inputMethodProfileManager = new InputMethodProfileManager();
        inputMethodSession = new InputMethodSessionController(inputMethodProfileManager);
        stateMachine = new BridgeStateMachine(settings.ToTiming(), settings.InteractionMode);
    }

    internal event EventHandler<RuntimeStatus>? StatusChanged;

    internal BridgeSnapshot Snapshot => stateMachine.Snapshot;

    internal async Task StartAsync()
    {
        await lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started)
            {
                return;
            }

            reportSource.ReportReceived += ReportSource_ReportReceived;
            reportSource.ConnectionChanged += ReportSource_ConnectionChanged;
            reportSource.Diagnostic += ReportSource_Diagnostic;
            execution.GuardExited += Execution_GuardExited;
            messageLoop = Task.Run(ProcessMessagesAsync);
            tickLoop = Task.Run(() => GenerateTicksAsync(tickLifetime.Token));
            reportSource.Start();
            started = true;
            string mode = settings.InteractionMode == BridgeInteractionMode.VoiceCommandPressAgain
                ? "再次按键提交"
                : "物理按住/松手";
            Publish(RuntimeStatusLevel.Information, $"运行引擎已启动；交互模式：{mode}。", force: true);
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    internal async Task EmergencyStopAsync(string reason)
    {
        if (!started || disposed)
        {
            return;
        }

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!messages.Writer.TryWrite(RuntimeMessage.Emergency(reason, completion)))
        {
            await execution.EmergencyReleaseAllAsync(stateMachine.Snapshot.Epoch).ConfigureAwait(false);
            await RestoreInputMethodAsync(stateMachine.Snapshot.Epoch, emergency: true).ConfigureAwait(false);
            return;
        }

        await completion.Task.ConfigureAwait(false);
    }

    internal async Task StopAsync()
    {
        await lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!started)
            {
                return;
            }

            await reportSource.StopAsync().ConfigureAwait(false);
            await EmergencyStopAsync("程序正在退出。 ").ConfigureAwait(false);
            await ReleaseActivationSessionAsync().ConfigureAwait(false);
            tickLifetime.Cancel();
            if (tickLoop is not null)
            {
                try
                {
                    await tickLoop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            messages.Writer.TryComplete();
            if (messageLoop is not null)
            {
                await messageLoop.ConfigureAwait(false);
            }

            reportSource.ReportReceived -= ReportSource_ReportReceived;
            reportSource.ConnectionChanged -= ReportSource_ConnectionChanged;
            reportSource.Diagnostic -= ReportSource_Diagnostic;
            execution.GuardExited -= Execution_GuardExited;
            started = false;
        }
        finally
        {
            lifecycleLock.Release();
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
        await reportSource.DisposeAsync().ConfigureAwait(false);
        await execution.DisposeAsync().ConfigureAwait(false);
        inputMethodProfileManager.Dispose();
        tickLifetime.Dispose();
        lifecycleLock.Dispose();
    }

    private async Task ProcessMessagesAsync()
    {
        await foreach (RuntimeMessage message in messages.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (message.Kind == RuntimeMessageKind.Signal)
                {
                    Publish(
                        RuntimeStatusLevel.Information,
                        $"遥控器事件：{DescribeSignal(message.Signal)}。",
                        force: true);
                }

                IReadOnlyList<BridgeAction> actions = message.Kind switch
                {
                    RuntimeMessageKind.Signal => stateMachine.HandleSignal(message.Signal, clock.Elapsed),
                    RuntimeMessageKind.Tick => stateMachine.Tick(clock.Elapsed),
                    RuntimeMessageKind.Emergency => stateMachine.EmergencyStop(message.Reason, clock.Elapsed),
                    _ => throw new ArgumentOutOfRangeException(nameof(message))
                };
                await ExecuteActionsAsync(actions).ConfigureAwait(false);

                if (message.Kind == RuntimeMessageKind.Tick)
                {
                    await PollAdapterStartConfirmationAsync().ConfigureAwait(false);
                }

                if (message.Kind == RuntimeMessageKind.Tick &&
                    stateMachine.Snapshot.State == BridgeState.Speaking &&
                    clock.Elapsed - lastLeaseRenewal >= TimeSpan.FromSeconds(1))
                {
                    lastLeaseRenewal = clock.Elapsed;
                    AdapterExecutionResult renewal = await execution.RenewAsync(
                        stateMachine.Snapshot.Epoch).ConfigureAwait(false);
                    if (!renewal.Succeeded)
                    {
                        Publish(RuntimeStatusLevel.Error, renewal.Message, force: true);
                        IReadOnlyList<BridgeAction> emergency = stateMachine.EmergencyStop(
                            "InputGuard 租约续期失败。",
                            clock.Elapsed);
                        await ExecuteActionsAsync(emergency).ConfigureAwait(false);
                    }
                }

                PublishStateIfChanged();
                message.Completion?.TrySetResult();
            }
            catch (Exception exception)
            {
                try
                {
                    stateMachine.Fault($"运行引擎错误：{exception.Message}", clock.Elapsed);
                    ClearPendingStartConfirmation();
                    focusSnapshots.Clear();
                    await execution.EmergencyReleaseAllAsync(stateMachine.Snapshot.Epoch).ConfigureAwait(false);
                    await RestoreInputMethodAsync(stateMachine.Snapshot.Epoch, emergency: true).ConfigureAwait(false);
                }
                catch (Exception releaseException)
                {
                    Publish(
                        RuntimeStatusLevel.Error,
                        $"运行引擎错误且紧急释放失败：{releaseException.Message}",
                        force: true);
                }

                Publish(RuntimeStatusLevel.Error, stateMachine.Snapshot.LastReason, force: true);
                message.Completion?.TrySetException(exception);
            }
        }
    }

    private async Task ExecuteActionsAsync(IReadOnlyList<BridgeAction> actions)
    {
        foreach (BridgeAction action in actions)
        {
            switch (action.Kind)
            {
                case BridgeActionKind.CaptureFocus:
                    focusSnapshots[action.Epoch] = ForegroundFocusProbe.Capture();
                    break;

                case BridgeActionKind.EvaluatePreflight:
                    PreflightDecision decision = await EvaluatePreflightAsync(action.Epoch).ConfigureAwait(false);
                    IReadOnlyList<BridgeAction> followUp = stateMachine.CompletePreflight(
                        action.Epoch,
                        decision,
                        clock.Elapsed);
                    if (stateMachine.Snapshot.State != BridgeState.Starting)
                    {
                        focusSnapshots.Remove(action.Epoch);
                    }

                    await ExecuteActionsAsync(followUp).ConfigureAwait(false);
                    break;

                case BridgeActionKind.StartAdapter:
                    if (profile.VoiceUiConfirmation is { } preStartConfirmation &&
                        VoiceUiWindowProbe.FindVisible(preStartConfirmation).Found)
                    {
                        IReadOnlyList<BridgeAction> alreadyVisible = stateMachine.AdapterStartFailed(
                            action.Epoch,
                            "发送开始快捷键前已存在可见的目标语音窗口；为避免 Toggle 反向关闭，已取消本轮。",
                            clock.Elapsed);
                        await ExecuteActionsAsync(alreadyVisible).ConfigureAwait(false);
                        break;
                    }

                    AdapterExecutionResult start = await execution.StartAsync(profile, action.Epoch).ConfigureAwait(false);
                    if (!start.Succeeded)
                    {
                        await ReleaseActivationSessionAsync().ConfigureAwait(false);
                        Publish(RuntimeStatusLevel.Error, $"适配器启动失败：{start.Message}", force: true);
                        IReadOnlyList<BridgeAction> failed = stateMachine.AdapterStartFailed(
                            action.Epoch,
                            "适配器启动失败，已取消本轮。",
                            clock.Elapsed);
                        await ExecuteActionsAsync(failed).ConfigureAwait(false);
                    }
                    else
                    {
                        lastLeaseRenewal = clock.Elapsed;
                        Publish(RuntimeStatusLevel.Information, start.Message, force: true);
                        if (activationSession is not null && settings.AudioHandoffMilliseconds > 0)
                        {
                            await Task.Delay(settings.AudioHandoffMilliseconds).ConfigureAwait(false);
                        }

                        await ReleaseActivationSessionAsync().ConfigureAwait(false);
                        if (profile.VoiceUiConfirmation is { } confirmation)
                        {
                            pendingStartEpoch = action.Epoch;
                            voiceUiConfirmationDeadline = clock.Elapsed +
                                TimeSpan.FromMilliseconds(confirmation.TimeoutMilliseconds);
                            Publish(
                                RuntimeStatusLevel.Information,
                                $"正在确认语音界面已启动：{confirmation.ProcessName} / {confirmation.WindowClass}，最长等待 {confirmation.TimeoutMilliseconds} ms。",
                                force: true);
                        }
                        else
                        {
                            stateMachine.AdapterStarted(action.Epoch, clock.Elapsed);
                            focusSnapshots.Remove(action.Epoch);
                        }
                    }

                    break;

                case BridgeActionKind.AbandonAdapterStart:
                    ClearPendingStartConfirmation();
                    focusSnapshots.Remove(action.Epoch);
                    await ReleaseActivationSessionAsync().ConfigureAwait(false);
                    AdapterExecutionResult abandoned = await execution
                        .AbandonStartAsync(action.Epoch)
                        .ConfigureAwait(false);
                    Publish(
                        abandoned.Succeeded ? RuntimeStatusLevel.Warning : RuntimeStatusLevel.Error,
                        abandoned.Succeeded
                            ? $"{action.Reason} {abandoned.Message}"
                            : $"启动回滚失败：{abandoned.Message}",
                        force: true);
                    await RestoreInputMethodAsync(action.Epoch, emergency: true).ConfigureAwait(false);
                    break;

                case BridgeActionKind.StopAdapter:
                case BridgeActionKind.EmergencyStopAdapter:
                    await ReleaseActivationSessionAsync().ConfigureAwait(false);
                    bool emergencyStop = action.Kind == BridgeActionKind.EmergencyStopAdapter;
                    ClearPendingStartConfirmation();
                    focusSnapshots.Remove(action.Epoch);
                    AdapterExecutionResult stop = await execution.StopAsync(
                        profile,
                        action.Epoch,
                        emergencyStop).ConfigureAwait(false);
                    if (!stop.Succeeded)
                    {
                        Publish(RuntimeStatusLevel.Error, $"适配器停止失败：{stop.Message}", force: true);
                        AdapterExecutionResult release = await execution.EmergencyReleaseAllAsync(action.Epoch).ConfigureAwait(false);
                        if (!release.Succeeded)
                        {
                            Publish(RuntimeStatusLevel.Error, release.Message, force: true);
                        }
                    }
                    else
                    {
                        Publish(RuntimeStatusLevel.Information, stop.Message, force: true);
                    }

                    await RestoreInputMethodAsync(action.Epoch, emergencyStop).ConfigureAwait(false);

                    IReadOnlyList<BridgeAction> stopped = stateMachine.AdapterStopped(action.Epoch, clock.Elapsed);
                    await ExecuteActionsAsync(stopped).ConfigureAwait(false);
                    break;

                case BridgeActionKind.Notify:
                    Publish(RuntimeStatusLevel.Warning, action.Reason, force: true);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(action.Kind), action.Kind, "Unknown bridge action.");
            }
        }
    }

    private async Task<PreflightDecision> EvaluatePreflightAsync(long epoch)
    {
        if (!focusSnapshots.TryGetValue(epoch, out FocusSnapshot? originalFocus))
        {
            return PreflightDecision.NotReady("缺少长按开始时的焦点快照。");
        }

        FocusSnapshot currentFocus = ForegroundFocusProbe.Capture();
        if (!ForegroundFocusProbe.IsUnchanged(originalFocus, currentFocus))
        {
            return PreflightDecision.FocusChanged("长按期间输入焦点发生变化，已拒绝启动语音输入。");
        }

        string[] missingProcesses = profile.RequiredProcesses.Where(name => !IsProcessRunning(name)).ToArray();
        if (missingProcesses.Length > 0)
        {
            return PreflightDecision.NotReady($"缺少目标进程：{string.Join(", ", missingProcesses)}。");
        }

        string[] conflicts = profile.ConflictingListeners.Where(IsProcessRunning).ToArray();
        if (conflicts.Length > 0)
        {
            return PreflightDecision.NotReady($"检测到已知快捷键冲突进程：{string.Join(", ", conflicts)}。");
        }

        if (profile.InputMethodSwitch is null && !string.IsNullOrWhiteSpace(profile.RequiresActiveIme))
        {
            ActiveInputMethodSnapshot activeInputMethod = ActiveInputMethodProbe.Capture(
                currentFocus.ForegroundThreadId);
            if (!activeInputMethod.Matches(profile.RequiresActiveIme))
            {
                string current = string.IsNullOrWhiteSpace(activeInputMethod.Description)
                    ? activeInputMethod.LayoutId
                    : $"{activeInputMethod.Description} ({activeInputMethod.LayoutId})";
                return PreflightDecision.NotReady(
                    $"当前输入法 {current} 与适配器要求 {profile.RequiresActiveIme} 不匹配。");
            }
        }

        ModifierSafetyResult modifiers = ModifierSafetyProbe.Evaluate(profile);
        if (!modifiers.IsSafe)
        {
            return PreflightDecision.NotReady(modifiers.Message);
        }

        AudioEndpointStatus audio;
        try
        {
            audio = AudioEndpointStatusProbe.FindCaptureEndpoint(settings.AudioEndpointName);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            return PreflightDecision.NotReady($"录音端点检查失败：{exception.Message}");
        }
        if (!audio.Found)
        {
            return PreflightDecision.NotReady(audio.Message);
        }

        if (audio.IsMuted)
        {
            return PreflightDecision.NotReady($"录音端点 {audio.FriendlyName} 当前已静音。");
        }

        if (settings.InteractionMode != BridgeInteractionMode.VoiceCommandPressAgain)
        {
            return await PrepareInputMethodAsync(epoch, originalFocus).ConfigureAwait(false);
        }

        await ReleaseActivationSessionAsync().ConfigureAwait(false);
        AudioCarrierActivationOptions activationOptions = new(
            TimeSpan.FromMilliseconds(settings.AudioActivationWarmupMilliseconds),
            TimeSpan.FromMilliseconds(settings.AudioActivationTimeoutMilliseconds),
            settings.AudioActivationRmsThreshold,
            settings.AudioActivationConsecutivePackets);
        activationSession = new AudioCarrierActivationSession(settings.AudioEndpointName, activationOptions);
        Publish(
            RuntimeStatusLevel.Information,
            "正在确认遥控器麦克风已被长按激活，请继续按住语音键……",
            force: true);
        AudioCarrierActivationResult activation;
        try
        {
            activation = await activationSession
                .WaitForActivationAsync(tickLifetime.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is COMException or
            InvalidOperationException or
            NotSupportedException or
            TimeoutException)
        {
            await ReleaseActivationSessionAsync().ConfigureAwait(false);
            return PreflightDecision.NotReady($"麦克风激活检查失败：{exception.Message}");
        }

        if (!activation.Activated)
        {
            await ReleaseActivationSessionAsync().ConfigureAwait(false);
            return PreflightDecision.NotReady(
                $"未检测到持续麦克风载波，按短按/未激活处理；最大 RMS={activation.MaximumRms:F6}。");
        }

        Publish(
            RuntimeStatusLevel.Information,
            $"麦克风载波已确认：{activation.FormatDescription}，最大 RMS={activation.MaximumRms:F6}。",
            force: true);
        return await PrepareInputMethodAsync(epoch, originalFocus).ConfigureAwait(false);
    }

    private async Task PollAdapterStartConfirmationAsync()
    {
        VoiceUiConfirmationOptions? confirmation = profile.VoiceUiConfirmation;
        BridgeSnapshot snapshot = stateMachine.Snapshot;
        if (confirmation is null ||
            snapshot.State != BridgeState.Starting ||
            pendingStartEpoch == 0 ||
            pendingStartEpoch != snapshot.Epoch ||
            voiceUiConfirmationDeadline is null)
        {
            return;
        }

        if (!focusSnapshots.TryGetValue(pendingStartEpoch, out FocusSnapshot? originalFocus) ||
            !ForegroundFocusProbe.IsUnchanged(originalFocus, ForegroundFocusProbe.Capture()))
        {
            IReadOnlyList<BridgeAction> focusFailed = stateMachine.AdapterStartFailed(
                pendingStartEpoch,
                "语音界面确认期间输入焦点发生变化，已取消本轮。",
                clock.Elapsed);
            await ExecuteActionsAsync(focusFailed).ConfigureAwait(false);
            return;
        }

        VoiceUiWindowMatch match;
        try
        {
            match = VoiceUiWindowProbe.FindVisible(confirmation);
        }
        catch (Exception exception) when (exception is ArgumentException or System.ComponentModel.Win32Exception)
        {
            IReadOnlyList<BridgeAction> probeFailed = stateMachine.AdapterStartFailed(
                pendingStartEpoch,
                $"语音界面确认检查失败：{exception.Message}",
                clock.Elapsed);
            await ExecuteActionsAsync(probeFailed).ConfigureAwait(false);
            return;
        }

        if (match.Found)
        {
            long confirmedEpoch = pendingStartEpoch;
            ClearPendingStartConfirmation();
            focusSnapshots.Remove(confirmedEpoch);
            stateMachine.AdapterStarted(confirmedEpoch, clock.Elapsed);
            Publish(
                RuntimeStatusLevel.Information,
                $"语音界面已确认启动：{confirmation.ProcessName} / {match.WindowClass}。",
                force: true);
            return;
        }

        if (clock.Elapsed >= voiceUiConfirmationDeadline.Value)
        {
            IReadOnlyList<BridgeAction> timedOut = stateMachine.AdapterStartFailed(
                pendingStartEpoch,
                "在规定时间内未确认语音界面启动，已取消本轮；不会自动重发 Toggle 快捷键。",
                clock.Elapsed);
            await ExecuteActionsAsync(timedOut).ConfigureAwait(false);
        }
    }

    private void ClearPendingStartConfirmation()
    {
        pendingStartEpoch = 0;
        voiceUiConfirmationDeadline = null;
    }

    private async Task<PreflightDecision> PrepareInputMethodAsync(long epoch, FocusSnapshot originalFocus)
    {
        InputMethodSwitchOptions? options = profile.InputMethodSwitch;
        if (options is null)
        {
            return PreflightDecision.Ready;
        }

        FocusSnapshot currentFocus = ForegroundFocusProbe.Capture();
        if (!ForegroundFocusProbe.IsUnchanged(originalFocus, currentFocus))
        {
            await ReleaseActivationSessionAsync().ConfigureAwait(false);
            return PreflightDecision.FocusChanged("输入法切换前输入焦点发生变化，已拒绝启动语音输入。");
        }

        InputMethodSessionResult result = await inputMethodSession.BeginAsync(
            epoch,
            options,
            tickLifetime.Token).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            await ReleaseActivationSessionAsync().ConfigureAwait(false);
            return PreflightDecision.NotReady(result.Message);
        }

        currentFocus = ForegroundFocusProbe.Capture();
        if (!ForegroundFocusProbe.IsUnchanged(originalFocus, currentFocus))
        {
            await ReleaseActivationSessionAsync().ConfigureAwait(false);
            await RestoreInputMethodAsync(epoch, emergency: true).ConfigureAwait(false);
            return PreflightDecision.FocusChanged("输入法切换及就绪等待后输入焦点发生变化，已恢复原输入法并拒绝启动语音输入。");
        }

        Publish(RuntimeStatusLevel.Information, result.Message, force: true);
        return PreflightDecision.Ready;
    }

    private async Task RestoreInputMethodAsync(long epoch, bool emergency)
    {
        InputMethodSwitchOptions? options = profile.InputMethodSwitch;
        if (options is null || !inputMethodSession.HasActiveSession)
        {
            return;
        }

        InputMethodSessionResult result = await inputMethodSession.RestoreAsync(
            epoch,
            options,
            emergency,
            CancellationToken.None).ConfigureAwait(false);
        Publish(
            result.Succeeded ? RuntimeStatusLevel.Information : RuntimeStatusLevel.Error,
            result.Message,
            force: true);
    }

    private async ValueTask ReleaseActivationSessionAsync()
    {
        AudioCarrierActivationSession? session = activationSession;
        activationSession = null;
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task GenerateTicksAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(25));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!messages.Writer.TryWrite(RuntimeMessage.Tick()))
            {
                return;
            }
        }
    }

    private void ReportSource_ReportReceived(object? sender, HidReportEventArgs eventArgs)
    {
        RemoteSignalKind? signal = decoder.Decode(
            eventArgs.UsagePage,
            eventArgs.Usage,
            eventArgs.Report);
        if (signal is { } value)
        {
            messages.Writer.TryWrite(RuntimeMessage.ForSignal(value));
        }
    }

    private void ReportSource_ConnectionChanged(object? sender, bool connected)
    {
        if (!connected)
        {
            decoder.Reset();
        }

        messages.Writer.TryWrite(RuntimeMessage.ForSignal(
            connected ? RemoteSignalKind.DeviceConnected : RemoteSignalKind.DeviceDisconnected));
    }

    private void ReportSource_Diagnostic(object? sender, string message) =>
        Publish(RuntimeStatusLevel.Warning, message, force: true);

    private void Execution_GuardExited(object? sender, EventArgs eventArgs)
    {
        messages.Writer.TryWrite(RuntimeMessage.Emergency(
            "InputGuard 意外退出；已停止新触发并请求 Key Up 兜底。",
            completion: null));
    }

    private void PublishStateIfChanged()
    {
        BridgeSnapshot snapshot = stateMachine.Snapshot;
        if (lastPublishedSnapshot?.State != snapshot.State ||
            !string.Equals(lastPublishedSnapshot.LastReason, snapshot.LastReason, StringComparison.Ordinal))
        {
            RuntimeStatusLevel level = snapshot.State == BridgeState.Faulted
                ? RuntimeStatusLevel.Error
                : RuntimeStatusLevel.Information;
            Publish(level, snapshot.LastReason, force: true);
        }
    }

    private void Publish(RuntimeStatusLevel level, string message, bool force)
    {
        BridgeSnapshot snapshot = stateMachine.Snapshot;
        if (!force && lastPublishedSnapshot == snapshot)
        {
            return;
        }

        lastPublishedSnapshot = snapshot;
        StatusChanged?.Invoke(this, new RuntimeStatus(level, message.Trim(), snapshot));
    }

    private static bool IsProcessRunning(string configuredName)
    {
        string processName = Path.GetFileNameWithoutExtension(configuredName.Trim());
        if (processName.Length == 0)
        {
            return false;
        }

        Process[] processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static string DescribeSignal(RemoteSignalKind signal) => signal switch
    {
        RemoteSignalKind.DeviceConnected => "设备已连接",
        RemoteSignalKind.DeviceDisconnected => "设备已断开",
        RemoteSignalKind.Neutral => "Neutral",
        RemoteSignalKind.Pressed => "Pressed",
        RemoteSignalKind.Repeated => "Repeated",
        RemoteSignalKind.Released => "Released",
        _ => signal.ToString()
    };

    private enum RuntimeMessageKind
    {
        Signal,
        Tick,
        Emergency
    }

    private sealed record RuntimeMessage(
        RuntimeMessageKind Kind,
        RemoteSignalKind Signal,
        string Reason,
        TaskCompletionSource? Completion)
    {
        internal static RuntimeMessage ForSignal(RemoteSignalKind signal) =>
            new(RuntimeMessageKind.Signal, signal, string.Empty, null);

        internal static RuntimeMessage Tick() =>
            new(RuntimeMessageKind.Tick, default, string.Empty, null);

        internal static RuntimeMessage Emergency(string reason, TaskCompletionSource? completion) =>
            new(RuntimeMessageKind.Emergency, default, reason, completion);
    }
}
