using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using VoiceRemoteBridge.Core;
using VoiceRemoteBridge.Windows;

namespace VoiceRemoteBridge.App;

public partial class MainWindow : Window
{
    private static readonly IReadOnlyList<KeyValuePair<BridgeInteractionMode, string>> InteractionModes =
    [
        new(BridgeInteractionMode.VoiceCommandPressAgain, "首次长按说话，再按一次提交（当前遥控器）"),
        new(BridgeInteractionMode.PhysicalDownUp, "按住说话，物理松手提交（仅支持真实松手报告的硬件）")
    ];

    private readonly JsonSettingsStore settingsStore = JsonSettingsStore.CreateDefault();
    private readonly Win32KeyInjector injector = new();
    private readonly WindowsStartupRegistration startupRegistration =
        WindowsStartupRegistration.CreateForCurrentProcess();
    private AppSettings settings = new();
    private BridgeRuntime? runtime;
    private VoiceButtonLearningSession? learningSession;
    private bool shutdownCompleted;
    private bool sessionPaused;
    private bool restartAfterSessionResume;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    internal event EventHandler? HideRequested;

    internal event EventHandler? ExitRequested;

    internal bool AllowClose { get; set; }

    internal async void EmergencyStop()
    {
        if (runtime is not null)
        {
            try
            {
                await runtime.EmergencyStopAsync("用户请求紧急停止。");
            }
            catch (Exception exception)
            {
                AppendDiagnostic($"运行引擎紧急停止失败：{exception.Message}");
            }
        }

        InjectionResult result = injector.ReleaseAll(KeyInjectionMode.VirtualKey);
        AppendDiagnostic(result.Succeeded
            ? "紧急停止：主进程登记的按键已释放。"
            : $"紧急停止失败：{result.Message} Win32={result.Win32Error}");
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs eventArgs)
    {
        SettingsLoadResult result = await settingsStore.LoadAsync();
        settings = result.Settings;
        TriggerModelComboBox.ItemsSource = Enum.GetValues<TriggerModel>();
        TriggerModelComboBox.SelectedItem = TriggerModel.PushToTalk;
        InjectionModeComboBox.ItemsSource = Enum.GetNames<KeyInjectionMode>();
        InjectionModeComboBox.SelectedItem = KeyInjectionMode.VirtualKey.ToString();
        InteractionModeComboBox.ItemsSource = InteractionModes;
        InteractionModeComboBox.SelectedValue = settings.InteractionMode;
        HardwareIdTextBox.Text = settings.HardwareId;
        AudioEndpointTextBox.Text = settings.AudioEndpointName;
        HoldThresholdTextBox.Text = settings.HoldThresholdMilliseconds.ToString(CultureInfo.InvariantCulture);
        MaximumSpeechTextBox.Text = settings.MaximumSpeechSeconds.ToString(CultureInfo.InvariantCulture);
        AudioActivationTimeoutTextBox.Text = settings.AudioActivationTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture);
        AudioActivationWarmupTextBox.Text = settings.AudioActivationWarmupMilliseconds.ToString(CultureInfo.InvariantCulture);
        AudioActivationRmsTextBox.Text = settings.AudioActivationRmsThreshold.ToString("G", CultureInfo.InvariantCulture);
        AudioActivationPacketsTextBox.Text = settings.AudioActivationConsecutivePackets.ToString(CultureInfo.InvariantCulture);
        AudioHandoffTextBox.Text = settings.AudioHandoffMilliseconds.ToString(CultureInfo.InvariantCulture);
        StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;
        UpdateInteractionModeDescription();
        BindingStatusText.Text = settings.VoiceButton is null
            ? settings.InteractionMode == BridgeInteractionMode.VoiceCommandPressAgain
                ? "未学习：请采集语音键的一次 Voice Command 脉冲。"
                : "未学习：等待阶段 0A 的真实按下/松开报告。"
            : $"已配置：UsagePage=0x{settings.VoiceButton.UsagePage:X4}, Usage=0x{settings.VoiceButton.Usage:X4}";
        AdapterStatusText.Text = settings.SelectedAdapter is null
            ? "未选择：等待阶段 0B 实测矩阵。"
            : $"{settings.SelectedAdapter.DisplayName} / {settings.SelectedAdapter.TriggerModel} / {settings.SelectedAdapter.GuardPolicy}";
        LoadAdapterEditor(settings.SelectedAdapter);

        foreach (string error in result.Errors)
        {
            AppendDiagnostic($"设置错误：{error}");
        }

        StartupRegistrationResult startupResult = startupRegistration.Apply(settings.StartWithWindows);
        if (!startupResult.Succeeded)
        {
            AppendDiagnostic(startupResult.Message);
        }

        UpdateReadiness();
        await RefreshDevicesAsync();
        if (settings.VoiceButton is not null && settings.SelectedAdapter is not null)
        {
            await StartRuntimeAsync();
        }
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs eventArgs) => await RefreshDevicesAsync();

    private async Task RefreshDevicesAsync()
    {
        try
        {
            IReadOnlyList<HidInterfaceDescriptor> devices = HidDeviceDiscovery.Enumerate(HardwareIdTextBox.Text.Trim());
            StringBuilder details = new();
            foreach (HidInterfaceDescriptor device in devices)
            {
                details.AppendLine(
                    $"UsagePage=0x{device.UsagePage:X4} Usage=0x{device.Usage:X4} " +
                    $"Input={device.InputReportByteLength} Read={device.CanOpenForRead} Error={device.ReadOpenError}");
            }

            DeviceDetailsText.Text = devices.Count == 0 ? "未找到目标 HID 接口。" : details.ToString().TrimEnd();
            IReadOnlyList<VoiceButtonLearningEndpoint> endpoints = VoiceButtonLearningSession.Discover(
                HardwareIdTextBox.Text.Trim());
            LearningEndpointComboBox.ItemsSource = endpoints;
            if (endpoints.Count > 0)
            {
                LearningEndpointComboBox.SelectedIndex = 0;
            }
            AppendDiagnostic($"设备刷新：找到 {devices.Count} 个目标 HID 接口。");
            AppendDiagnostic($"语音键学习：找到 {endpoints.Count} 个可采集通道。");
        }
        catch (Exception exception)
        {
            DeviceDetailsText.Text = $"设备枚举失败：{exception.Message}";
            AppendDiagnostic(DeviceDetailsText.Text);
        }

        await Task.CompletedTask;
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!int.TryParse(HoldThresholdTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int threshold) ||
            !int.TryParse(MaximumSpeechTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maximumSpeech) ||
            !int.TryParse(AudioActivationTimeoutTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int activationTimeout) ||
            !int.TryParse(AudioActivationWarmupTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int activationWarmup) ||
            !double.TryParse(AudioActivationRmsTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double activationRms) ||
            !int.TryParse(AudioActivationPacketsTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int activationPackets) ||
            !int.TryParse(AudioHandoffTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int audioHandoff))
        {
            AppendDiagnostic("保存失败：请检查触发参数；除 RMS 阈值外都必须是整数，RMS 使用小数点。");
            return;
        }

        BridgeInteractionMode interactionMode = InteractionModeComboBox.SelectedValue is BridgeInteractionMode selectedMode
            ? selectedMode
            : settings.InteractionMode;

        AppSettings candidate = settings with
        {
            HardwareId = HardwareIdTextBox.Text.Trim(),
            AudioEndpointName = AudioEndpointTextBox.Text.Trim(),
            InteractionMode = interactionMode,
            HoldThresholdMilliseconds = threshold,
            MaximumSpeechSeconds = maximumSpeech,
            AudioActivationTimeoutMilliseconds = activationTimeout,
            AudioActivationWarmupMilliseconds = activationWarmup,
            AudioActivationRmsThreshold = activationRms,
            AudioActivationConsecutivePackets = activationPackets,
            AudioHandoffMilliseconds = audioHandoff,
            StartWithWindows = StartWithWindowsCheckBox.IsChecked == true
        };
        IReadOnlyList<string> errors = candidate.Validate();
        if (errors.Count > 0)
        {
            AppendDiagnostic("保存失败：" + string.Join("；", errors));
            return;
        }

        StartupRegistrationResult startupResult = startupRegistration.Apply(candidate.StartWithWindows);
        if (!startupResult.Succeeded)
        {
            AppendDiagnostic("保存失败：" + startupResult.Message);
            return;
        }

        try
        {
            await settingsStore.SaveAsync(candidate);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StartupRegistrationResult rollback = startupRegistration.Apply(settings.StartWithWindows);
            AppendDiagnostic(
                $"保存失败：{exception.Message}。启动项回滚{(rollback.Succeeded ? "成功" : "失败：" + rollback.Message)}");
            return;
        }

        bool restartRuntime = runtime is not null;
        await StopRuntimeAsync();
        settings = candidate;
        AppendDiagnostic($"设置已保存：{settingsStore.FilePath}；{startupResult.Message}");
        UpdateReadiness();
        if (restartRuntime && settings.VoiceButton is not null && settings.SelectedAdapter is not null)
        {
            await StartRuntimeAsync();
        }
    }

    private void TestF13_Click(object sender, RoutedEventArgs eventArgs)
    {
        InjectionResult result = injector.Tap([0x7C], KeyInjectionMode.VirtualKey);
        AppendDiagnostic(result.Succeeded
            ? "F13 测试成功：Key Down/Key Up 已在一个批次中发送。"
            : $"F13 测试失败：{result.Message} Win32={result.Win32Error}");
    }

    private async void SaveAdapter_Click(object sender, RoutedEventArgs eventArgs)
    {
        KeyChordParseResult start = KeyChordCodec.Parse(StartChordTextBox.Text);
        if (!start.Succeeded)
        {
            AppendDiagnostic($"适配器保存失败：{start.Error}");
            return;
        }

        TriggerModel trigger = TriggerModelComboBox.SelectedItem is TriggerModel selectedTrigger
            ? selectedTrigger
            : TriggerModel.PushToTalk;
        if (SelectedInteractionMode() == BridgeInteractionMode.VoiceCommandPressAgain &&
            trigger == TriggerModel.PushToTalk)
        {
            AppendDiagnostic(
                "适配器保存失败：再次按键提交模式禁止持续按住系统快捷键。请选择 Toggle，并使用已实测的 Ctrl+Win+Shift。");
            return;
        }

        KeyChordParseResult stop = KeyChordCodec.Parse(
            StopChordTextBox.Text,
            allowEmpty: trigger != TriggerModel.StartStopPair);
        if (!stop.Succeeded)
        {
            AppendDiagnostic($"适配器保存失败：{stop.Error}");
            return;
        }

        KeyChordParseResult forbidden = KeyChordCodec.Parse(
            ForbiddenModifiersTextBox.Text,
            allowEmpty: true);
        if (!forbidden.Succeeded)
        {
            AppendDiagnostic($"适配器保存失败：{forbidden.Error}");
            return;
        }

        if (string.IsNullOrWhiteSpace(AdapterNameTextBox.Text))
        {
            AppendDiagnostic("适配器保存失败：名称不能为空。");
            return;
        }

        string? requiredInputMethod = string.IsNullOrWhiteSpace(RequiredImeTextBox.Text)
            ? null
            : RequiredImeTextBox.Text.Trim();
        InputMethodSwitchOptions? inputMethodSwitch = null;
        if (SwitchInputMethodCheckBox.IsChecked == true)
        {
            if (requiredInputMethod is null)
            {
                AppendDiagnostic("适配器保存失败：启用自动切换时必须填写目标输入法名称或 Profile GUID。");
                return;
            }

            if (!int.TryParse(
                    InputMethodPostActivationDelayTextBox.Text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int postActivationDelayMilliseconds))
            {
                AppendDiagnostic("适配器保存失败：输入法切换后的启动等待必须是整数毫秒。");
                return;
            }

            if (!int.TryParse(
                    InputMethodRestoreDelayTextBox.Text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int restoreDelayMilliseconds))
            {
                AppendDiagnostic("适配器保存失败：输入法恢复等待必须是整数毫秒。");
                return;
            }

            inputMethodSwitch = new InputMethodSwitchOptions
            {
                TargetProfile = requiredInputMethod,
                PostActivationDelayMilliseconds = postActivationDelayMilliseconds,
                RestoreDelayMilliseconds = restoreDelayMilliseconds,
                AllowProfileEnablement = EnableInputMethodProfileCheckBox.IsChecked == true,
                RefreshWhenAlreadyActive = RefreshActiveInputMethodCheckBox.IsChecked == true
            };
        }

        VoiceUiConfirmationOptions? voiceUiConfirmation = null;
        if (ConfirmVoiceUiCheckBox.IsChecked == true)
        {
            if (!int.TryParse(
                    VoiceUiTimeoutTextBox.Text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int voiceUiTimeoutMilliseconds))
            {
                AppendDiagnostic("适配器保存失败：语音窗口启动确认等待必须是整数毫秒。");
                return;
            }

            voiceUiConfirmation = new VoiceUiConfirmationOptions
            {
                ProcessName = VoiceUiProcessTextBox.Text.Trim(),
                WindowClass = VoiceUiClassTextBox.Text.Trim(),
                TimeoutMilliseconds = voiceUiTimeoutMilliseconds
            };
        }

        (InjectionLifetime lifetime, GuardPolicy guardPolicy) = trigger switch
        {
            TriggerModel.PushToTalk => (InjectionLifetime.HeldAcrossSpeech, GuardPolicy.Required),
            TriggerModel.TapOnHold => (InjectionLifetime.AtomicBatch, GuardPolicy.Optional),
            TriggerModel.Toggle => (InjectionLifetime.AtomicBatch, GuardPolicy.Optional),
            TriggerModel.StartStopPair => (InjectionLifetime.StartStopStateful, GuardPolicy.Unsupported),
            _ => throw new ArgumentOutOfRangeException(nameof(trigger))
        };
        AdapterProfile profile = new()
        {
            Id = settings.SelectedAdapter?.Id ?? $"custom-{Guid.NewGuid():N}",
            DisplayName = AdapterNameTextBox.Text.Trim(),
            TriggerModel = trigger,
            InjectionLifetime = lifetime,
            GuardPolicy = guardPolicy,
            StartChord = start.Keys,
            StopChord = stop.Keys,
            ForbiddenModifiers = forbidden.Keys,
            RequiredProcesses = ParseProcessList(RequiredProcessesTextBox.Text),
            ConflictingListeners = ParseProcessList(ConflictingProcessesTextBox.Text),
            RequiresActiveIme = requiredInputMethod,
            InputMethodSwitch = inputMethodSwitch,
            VoiceUiConfirmation = voiceUiConfirmation,
            InjectionMode = InjectionModeComboBox.SelectedItem as string ?? KeyInjectionMode.VirtualKey.ToString()
        };
        IReadOnlyList<string> errors = profile.Validate();
        if (errors.Count > 0)
        {
            AppendDiagnostic("适配器保存失败：" + string.Join("；", errors));
            return;
        }

        AppSettings candidate = settings with { SelectedAdapter = profile };
        IReadOnlyList<string> settingsErrors = candidate.Validate();
        if (settingsErrors.Count > 0)
        {
            AppendDiagnostic("适配器保存失败：" + string.Join("；", settingsErrors));
            return;
        }

        await StopRuntimeAsync();
        settings = candidate;
        await settingsStore.SaveAsync(settings);
        AdapterStatusText.Text = $"{profile.DisplayName} / {profile.TriggerModel} / {profile.GuardPolicy}";
        AppendDiagnostic("适配器已保存并选中；只有完成真实开始/结束测试后才能标记为已验证。");
        UpdateReadiness();
    }

    private async void ClearAdapter_Click(object sender, RoutedEventArgs eventArgs)
    {
        await StopRuntimeAsync();
        settings = settings with { SelectedAdapter = null };
        await settingsStore.SaveAsync(settings);
        LoadAdapterEditor(null);
        AdapterStatusText.Text = "未选择：不会注入语音快捷键。";
        UpdateReadiness();
    }

    private async void TestAdapterStart_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (TriggerModelComboBox.SelectedItem is TriggerModel.PushToTalk)
        {
            await TestPushToTalkAsync();
            return;
        }

        TestEditorChord(start: true);
    }

    private void TestAdapterStop_Click(object sender, RoutedEventArgs eventArgs) => TestEditorChord(start: false);

    private void TriggerModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        bool pair = TriggerModelComboBox.SelectedItem is TriggerModel.StartStopPair;
        StopChordLabel.Visibility = pair ? Visibility.Visible : Visibility.Collapsed;
        StopChordTextBox.Visibility = pair ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InteractionModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs) =>
        UpdateInteractionModeDescription();

    private void EmergencyStop_Click(object sender, RoutedEventArgs eventArgs) => EmergencyStop();

    private async void BeginLearning_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (LearningEndpointComboBox.SelectedItem is not VoiceButtonLearningEndpoint endpoint)
        {
            AppendDiagnostic("学习启动失败：请先选择采集通道。");
            return;
        }

        await StopRuntimeAsync();
        await CancelLearningAsync();
        try
        {
            bool allowOneShotVoice = SelectedInteractionMode() == BridgeInteractionMode.VoiceCommandPressAgain;
            learningSession = new VoiceButtonLearningSession(endpoint, allowOneShotVoice);
            learningSession.Diagnostic += LearningSession_Diagnostic;
            learningSession.Start();
            SetLearningControls(VoiceButtonLearningPhase.Neutral);
            LearningInstructionText.Text = allowOneShotVoice
                ? "第 1 步：保持语音键松开约 1 秒，然后点击“2. 现在按住”。当前模式允许接收端发出一次短脉冲。"
                : "第 1 步：保持语音键完全松开约 1 秒，然后点击“2. 现在按住”。";
            AppendDiagnostic($"语音键学习已启动：{endpoint.DisplayName}");
        }
        catch (Exception exception)
        {
            AppendDiagnostic($"学习启动失败：{exception.Message}");
            await CancelLearningAsync();
        }
    }

    private void LearnPressed_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (learningSession is null)
        {
            return;
        }

        learningSession.BeginPressedPhase();
        SetLearningControls(VoiceButtonLearningPhase.Pressed);
        LearningInstructionText.Text = "第 2 步：现在按住遥控器语音键，保持约 1 秒，再点击“3. 现在松开”。";
    }

    private void LearnReleased_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (learningSession is null)
        {
            return;
        }

        learningSession.BeginReleasedPhase();
        SetLearningControls(VoiceButtonLearningPhase.Released);
        LearningInstructionText.Text = "第 3 步：现在松开语音键，等待约 1 秒，再点击“4. 完成学习”。";
    }

    private async void FinishLearning_Click(object sender, RoutedEventArgs eventArgs)
    {
        VoiceButtonLearningSession? current = learningSession;
        if (current is null)
        {
            return;
        }

        try
        {
            VoiceButtonLearningResult result = await current.CompleteAsync();
            AppendDiagnostic(
                $"学习结果：中性={result.NeutralReportCount}、按下={result.PressedReportCount}、松开={result.ReleasedReportCount}。{result.Message}");
            if (result.Succeeded && result.Binding is not null)
            {
                settings = settings with { VoiceButton = result.Binding };
                await settingsStore.SaveAsync(settings);
                BindingStatusText.Text =
                    $"已学习：{result.Binding.Transport} / UsagePage=0x{result.Binding.UsagePage:X4} / Usage=0x{result.Binding.Usage:X4}";
            }
        }
        catch (Exception exception)
        {
            AppendDiagnostic($"学习完成失败：{exception.Message}");
        }
        finally
        {
            await CancelLearningAsync();
            UpdateReadiness();
        }
    }

    private async void CancelLearning_Click(object sender, RoutedEventArgs eventArgs)
    {
        await CancelLearningAsync();
        LearningInstructionText.Text = "学习已取消；可以重新选择通道。";
    }

    private async void StartBridge_Click(object sender, RoutedEventArgs eventArgs) => await StartRuntimeAsync();

    private async void StopBridge_Click(object sender, RoutedEventArgs eventArgs) => await StopRuntimeAsync();

    private void RunDiagnostics_Click(object sender, RoutedEventArgs eventArgs)
    {
        AppendDiagnostic($"自检：HID 绑定={(settings.VoiceButton is null ? "缺失" : "已配置")}");
        AppendDiagnostic($"自检：语音适配器={(settings.SelectedAdapter is null ? "缺失" : "已配置")}");
        AppendDiagnostic($"自检：交互模式={settings.InteractionMode}");
        StartupRegistrationState startup = startupRegistration.ReadState();
        AppendDiagnostic(startup.Error is null
            ? $"自检：开机自启动设置={(settings.StartWithWindows ? "开启" : "关闭")} / " +
              $"Windows 注册={(startup.IsRegistered ? "匹配" : startup.Command is null ? "未注册" : "命令不匹配")}"
            : $"自检：{startup.Error}");
        AppendDiagnostic(
            $"自检：麦克风激活参数=等待 {settings.AudioActivationTimeoutMilliseconds} ms / " +
            $"预热 {settings.AudioActivationWarmupMilliseconds} ms / RMS {settings.AudioActivationRmsThreshold:G} / " +
            $"连续 {settings.AudioActivationConsecutivePackets} 包 / 交接 {settings.AudioHandoffMilliseconds} ms");
        AppendDiagnostic($"自检：主进程登记键数量={injector.HeldKeys.Count}");
        AppendDiagnostic($"自检：运行引擎={(runtime is null ? "未启动" : runtime.Snapshot.State)}");
        FocusSnapshot focus = ForegroundFocusProbe.Capture();
        ActiveInputMethodSnapshot inputMethod = ActiveInputMethodProbe.Capture(focus.ForegroundThreadId);
        AppendDiagnostic($"自检：前台输入法={inputMethod.Description} / LayoutId={inputMethod.LayoutId}");
        try
        {
            AudioEndpointStatus audio = AudioEndpointStatusProbe.FindCaptureEndpoint(settings.AudioEndpointName);
            AppendDiagnostic(audio.Found
                ? $"自检：录音端点={audio.FriendlyName} / 静音={audio.IsMuted} / 当前峰值={audio.CurrentPeak:F4}"
                : $"自检：{audio.Message}");
        }
        catch (Exception exception)
        {
            AppendDiagnostic($"自检：录音端点检查失败：{exception.Message}");
        }
        AppendDiagnostic("自检边界：网络、账号、云端识别质量由目标语音软件负责。");
    }

    private void ClearDiagnostics_Click(object sender, RoutedEventArgs eventArgs) => DiagnosticsTextBox.Clear();

    private void Hide_Click(object sender, RoutedEventArgs eventArgs) => HideRequested?.Invoke(this, EventArgs.Empty);

    private void Exit_Click(object sender, RoutedEventArgs eventArgs) => ExitRequested?.Invoke(this, EventArgs.Empty);

    private void MainWindow_Closing(object? sender, CancelEventArgs eventArgs)
    {
        if (AllowClose)
        {
            EmergencyStop();
            return;
        }

        eventArgs.Cancel = true;
        HideRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateReadiness()
    {
        bool ready = settings.VoiceButton is not null && settings.SelectedAdapter is not null;
        HeaderStatusText.Text = runtime is not null
            ? $"运行中：{runtime.Snapshot.State}"
            : ready ? "已配置，当前未运行" : "未武装：需要完成 0A/0B";
        FooterStatusText.Text = ready
            ? settings.InteractionMode == BridgeInteractionMode.VoiceCommandPressAgain
                ? "当前流程：首次长按说话，再按一次提交；尚未完成本版本真机验收。"
                : "当前流程：按住说话、物理松手提交；尚未完成真机验收。"
            : "安全状态：不会猜测语音键，也不会注入语音快捷键。";
        StartBridgeButton.IsEnabled = ready && runtime is null;
        StopBridgeButton.IsEnabled = runtime is not null;
    }

    internal async Task ShutdownAsync()
    {
        if (shutdownCompleted)
        {
            return;
        }

        await CancelLearningAsync();
        await StopRuntimeAsync();
        InjectionResult release = injector.ReleaseAll(KeyInjectionMode.VirtualKey);
        if (!release.Succeeded)
        {
            AppendDiagnostic($"退出释放失败：{release.Message} Win32={release.Win32Error}");
        }

        shutdownCompleted = true;
    }

    internal async void PauseForSession(string reason)
    {
        if (sessionPaused || shutdownCompleted)
        {
            return;
        }

        sessionPaused = true;
        restartAfterSessionResume = runtime is not null;
        AppendDiagnostic(reason);
        await CancelLearningAsync();
        await StopRuntimeAsync();
        EmergencyStop();
    }

    internal async void ResumeAfterSession(string reason)
    {
        if (!sessionPaused || shutdownCompleted)
        {
            return;
        }

        sessionPaused = false;
        bool shouldRestart = restartAfterSessionResume;
        restartAfterSessionResume = false;
        AppendDiagnostic(reason);
        await RefreshDevicesAsync();
        if (shouldRestart)
        {
            await StartRuntimeAsync();
        }
    }

    internal void ReportSystemEventSubscriptionFailure(string message) =>
        AppendDiagnostic($"系统会话事件订阅失败：{message}");

    private async Task StartRuntimeAsync()
    {
        if (runtime is not null)
        {
            return;
        }

        if (sessionPaused)
        {
            AppendDiagnostic("启动被拒绝：Windows 会话当前处于锁定、断开或休眠状态。");
            UpdateReadiness();
            return;
        }

        if (settings.VoiceButton is null || settings.SelectedAdapter is null)
        {
            AppendDiagnostic("启动被拒绝：必须先完成语音键学习和适配器验证。");
            UpdateReadiness();
            return;
        }

        try
        {
            BridgeRuntime candidate = new(settings);
            candidate.StatusChanged += Runtime_StatusChanged;
            await candidate.StartAsync();
            runtime = candidate;
            AppendDiagnostic("桥接运行引擎已启动。");
        }
        catch (Exception exception)
        {
            AppendDiagnostic($"桥接启动失败：{exception.Message}");
        }

        UpdateReadiness();
    }

    private async Task StopRuntimeAsync()
    {
        BridgeRuntime? current = runtime;
        if (current is null)
        {
            return;
        }

        runtime = null;
        current.StatusChanged -= Runtime_StatusChanged;
        try
        {
            await current.DisposeAsync();
            AppendDiagnostic("桥接运行引擎已停止，登记按键已释放。");
        }
        catch (Exception exception)
        {
            AppendDiagnostic($"桥接停止失败：{exception.Message}");
        }

        UpdateReadiness();
    }

    private void Runtime_StatusChanged(object? sender, RuntimeStatus status)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            string level = status.Level switch
            {
                RuntimeStatusLevel.Information => "信息",
                RuntimeStatusLevel.Warning => "警告",
                RuntimeStatusLevel.Error => "错误",
                _ => "状态"
            };
            AppendDiagnostic($"{level} [{status.Snapshot.State}] {status.Message}");
            HeaderStatusText.Text = $"运行中：{status.Snapshot.State}";
            FooterStatusText.Text = status.Message;
        });
    }

    private async Task CancelLearningAsync()
    {
        VoiceButtonLearningSession? current = learningSession;
        learningSession = null;
        if (current is not null)
        {
            current.Diagnostic -= LearningSession_Diagnostic;
            await current.DisposeAsync();
        }

        SetLearningControls(VoiceButtonLearningPhase.Completed);
    }

    private void LearningSession_Diagnostic(object? sender, string message) =>
        _ = Dispatcher.InvokeAsync(() => AppendDiagnostic($"学习诊断：{message}"));

    private BridgeInteractionMode SelectedInteractionMode() =>
        InteractionModeComboBox.SelectedValue is BridgeInteractionMode selectedMode
            ? selectedMode
            : settings.InteractionMode;

    private void UpdateInteractionModeDescription()
    {
        if (InteractionModeDescriptionText is null)
        {
            return;
        }

        InteractionModeDescriptionText.Text = SelectedInteractionMode() switch
        {
            BridgeInteractionMode.VoiceCommandPressAgain =>
                "第一次必须长按到遥控器麦克风激活；程序确认音频载波后启动语音软件。说完再按一次提交。为避免卡键，本模式只允许 Toggle/AtomicBatch 或 StartStopPair，不允许持续按住快捷键。",
            BridgeInteractionMode.PhysicalDownUp =>
                "只适用于能持续报告按下状态并在真实松手时另发松手报告的硬件；当前接收端不满足此条件。",
            _ => "未知交互模式。"
        };
    }

    private void SetLearningControls(VoiceButtonLearningPhase phase)
    {
        bool active = learningSession is not null;
        LearningEndpointComboBox.IsEnabled = !active;
        BeginLearningButton.IsEnabled = !active;
        LearnPressedButton.IsEnabled = active && phase == VoiceButtonLearningPhase.Neutral;
        LearnReleasedButton.IsEnabled = active && phase == VoiceButtonLearningPhase.Pressed;
        FinishLearningButton.IsEnabled = active && phase == VoiceButtonLearningPhase.Released;
        CancelLearningButton.IsEnabled = active;
    }

    private void AppendDiagnostic(string message)
    {
        DiagnosticsTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        DiagnosticsTextBox.ScrollToEnd();
    }

    private void LoadAdapterEditor(AdapterProfile? profile)
    {
        AdapterNameTextBox.Text = profile?.DisplayName ?? string.Empty;
        TriggerModelComboBox.SelectedItem = profile?.TriggerModel ?? TriggerModel.PushToTalk;
        StartChordTextBox.Text = profile is null ? string.Empty : KeyChordCodec.Format(profile.StartChord);
        StopChordTextBox.Text = profile is null ? string.Empty : KeyChordCodec.Format(profile.StopChord);
        InjectionModeComboBox.SelectedItem = profile?.InjectionMode ?? KeyInjectionMode.VirtualKey.ToString();
        RequiredProcessesTextBox.Text = profile is null ? string.Empty : string.Join(", ", profile.RequiredProcesses);
        ConflictingProcessesTextBox.Text = profile is null ? string.Empty : string.Join(", ", profile.ConflictingListeners);
        RequiredImeTextBox.Text = profile?.RequiresActiveIme ?? string.Empty;
        SwitchInputMethodCheckBox.IsChecked = profile?.InputMethodSwitch is not null;
        EnableInputMethodProfileCheckBox.IsChecked = profile?.InputMethodSwitch?.AllowProfileEnablement == true;
        RefreshActiveInputMethodCheckBox.IsChecked = profile?.InputMethodSwitch?.RefreshWhenAlreadyActive == true;
        InputMethodPostActivationDelayTextBox.Text = (
            profile?.InputMethodSwitch?.PostActivationDelayMilliseconds ?? 1_000).ToString(CultureInfo.InvariantCulture);
        InputMethodRestoreDelayTextBox.Text = (
            profile?.InputMethodSwitch?.RestoreDelayMilliseconds ?? 500).ToString(CultureInfo.InvariantCulture);
        ConfirmVoiceUiCheckBox.IsChecked = profile?.VoiceUiConfirmation is not null;
        VoiceUiProcessTextBox.Text = profile?.VoiceUiConfirmation?.ProcessName ?? "wetype_update";
        VoiceUiClassTextBox.Text = profile?.VoiceUiConfirmation?.WindowClass ?? "wetype.flutter.setting";
        VoiceUiTimeoutTextBox.Text = (
            profile?.VoiceUiConfirmation?.TimeoutMilliseconds ?? 2_500).ToString(CultureInfo.InvariantCulture);
        ForbiddenModifiersTextBox.Text = profile is null ? string.Empty : KeyChordCodec.Format(profile.ForbiddenModifiers);
    }

    private void TestEditorChord(bool start)
    {
        string text = start ? StartChordTextBox.Text : StopChordTextBox.Text;
        KeyChordParseResult parsed = KeyChordCodec.Parse(text);
        if (!parsed.Succeeded)
        {
            AppendDiagnostic($"快捷键实测失败：{parsed.Error}");
            return;
        }

        KeyInjectionMode mode = Enum.Parse<KeyInjectionMode>(
            InjectionModeComboBox.SelectedItem as string ?? KeyInjectionMode.VirtualKey.ToString());
        InjectionResult result = injector.Tap(parsed.Keys, mode);
        AppendDiagnostic(result.Succeeded
            ? $"已发送{(start ? "开始" : "停止")}快捷键：{KeyChordCodec.Format(parsed.Keys)}（Down/Up 同批次）。"
            : $"快捷键实测失败：{result.Message} Win32={result.Win32Error}");
    }

    private async Task TestPushToTalkAsync()
    {
        AdapterProfile? profile = settings.SelectedAdapter;
        if (profile is null || profile.TriggerModel != TriggerModel.PushToTalk)
        {
            AppendDiagnostic("持续按住实测前请先保存 PushToTalk 适配器。");
            return;
        }

        await StopRuntimeAsync();
        await using AdapterExecutionService service = new(GuardLaunchLocator.CreateManager);
        long epoch = DateTime.UtcNow.Ticks;
        AdapterExecutionResult start = await service.StartAsync(profile, epoch);
        if (!start.Succeeded)
        {
            AppendDiagnostic($"持续按住实测失败：{start.Message}");
            return;
        }

        AppendDiagnostic($"正在通过 InputGuard 按住 {KeyChordCodec.Format(profile.StartChord)}，1 秒后自动释放。");
        await Task.Delay(TimeSpan.FromSeconds(1));
        AdapterExecutionResult stop = await service.StopAsync(profile, epoch, emergency: false);
        AppendDiagnostic(stop.Succeeded
            ? "持续按住实测完成：InputGuard 已释放全部按键。"
            : $"持续按住实测停止失败：{stop.Message}");
    }

    private static IReadOnlyList<string> ParseProcessList(string text) => text
        .Split([',', ';', '，', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
