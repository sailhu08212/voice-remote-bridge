using System.Diagnostics;
using System.Reflection;
using VoiceRemoteBridge.Core;
using VoiceRemoteBridge.Windows;

namespace VoiceRemoteBridge.Windows.Tests;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--fault-parent-child", StringComparer.OrdinalIgnoreCase))
        {
            return await RunParentCrashChildAsync(args).ConfigureAwait(false);
        }

        if (args.Contains("--fault-guard-child", StringComparer.OrdinalIgnoreCase))
        {
            return await RunGuardCrashChildAsync(args).ConfigureAwait(false);
        }

        try
        {
            ProtocolValidation();
            Console.WriteLine("PASS ProtocolValidation");
            StartupCommandBuilderAnalysis();
            Console.WriteLine("PASS StartupCommandBuilderAnalysis");
            StartupRegistrationRoundTrip();
            Console.WriteLine("PASS StartupRegistrationRoundTrip");
            VoiceButtonLearningAnalysis();
            Console.WriteLine("PASS VoiceButtonLearningAnalysis");
            AudioCarrierMetricsAnalysis();
            Console.WriteLine("PASS AudioCarrierMetricsAnalysis");
            GuardRestartBackoffPolicy();
            Console.WriteLine("PASS GuardRestartBackoffPolicy");
            await SettingsRoundTripAsync().ConfigureAwait(false);
            Console.WriteLine("PASS SettingsRoundTrip");
            VoiceUiWindowProbeDoesNotMatchMissingProcess();
            Console.WriteLine("PASS VoiceUiWindowProbeDoesNotMatchMissingProcess");
            await InputMethodSessionLifecycleAsync().ConfigureAwait(false);
            Console.WriteLine("PASS InputMethodSessionLifecycle");

            if (args.Contains("--input-method-smoke", StringComparer.OrdinalIgnoreCase))
            {
                InputMethodProfileSmoke();
                Console.WriteLine("PASS InputMethodProfileSmoke");
            }

            if (args.Contains("--input-method-activation-smoke", StringComparer.OrdinalIgnoreCase))
            {
                await InputMethodActivationSmokeAsync().ConfigureAwait(false);
                Console.WriteLine("PASS InputMethodActivationSmoke");
            }

            if (args.Contains("--input-method-cycle-smoke", StringComparer.OrdinalIgnoreCase))
            {
                await InputMethodCycleSmokeAsync().ConfigureAwait(false);
                Console.WriteLine("PASS InputMethodCycleSmoke");
            }

            if (args.Contains("--integration", StringComparer.OrdinalIgnoreCase))
            {
                await GuardRoundTripAsync(args).ConfigureAwait(false);
                Console.WriteLine("PASS GuardRoundTrip");
                await AdapterExecutionRoundTripAsync(args).ConfigureAwait(false);
                Console.WriteLine("PASS AdapterExecutionRoundTrip");
            }

            if (args.Contains("--hardware-smoke", StringComparer.OrdinalIgnoreCase))
            {
                await RawInputSourceStartsAndStopsAsync().ConfigureAwait(false);
                Console.WriteLine("PASS RawInputSourceStartsAndStops");
                AudioEndpointIsPresent();
                Console.WriteLine("PASS AudioEndpointIsPresent");
            }

            if (args.Contains("--fault-integration", StringComparer.OrdinalIgnoreCase))
            {
                await ParentCrashReleasesGuardAsync(args).ConfigureAwait(false);
                Console.WriteLine("PASS ParentCrashReleasesGuard");
                await GuardCrashTriggersMainFallbackAsync(args).ConfigureAwait(false);
                Console.WriteLine("PASS GuardCrashTriggersMainFallback");
            }

            Console.WriteLine("RESULT failed=0");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL {exception.Message}");
            return 1;
        }
    }

    private static void ProtocolValidation()
    {
        string pipe = GuardProtocol.BuildPipeName("abc_123-Z");
        Equal("VoiceRemoteBridge.InputGuard.abc_123-Z", pipe);
        Throws<ArgumentException>(() => GuardProtocol.BuildPipeName("bad token"));
    }

    private static void StartupCommandBuilderAnalysis()
    {
        string frameworkDependent = StartupCommandBuilder.Build(
            @"C:\Program Files\dotnet\dotnet.exe",
            @"D:\Voice App\VoiceRemoteBridge.App.dll");
        Equal(
            @"""C:\Program Files\dotnet\dotnet.exe"" ""D:\Voice App\VoiceRemoteBridge.App.dll"" --background",
            frameworkDependent);

        string appHost = StartupCommandBuilder.Build(
            @"D:\Voice App\VoiceRemoteBridge.App.exe",
            @"D:\Voice App\VoiceRemoteBridge.App.dll");
        Equal(@"""D:\Voice App\VoiceRemoteBridge.App.exe"" --background", appHost);
    }

    private static void StartupRegistrationRoundTrip()
    {
        const string command = @"""D:\Voice App\VoiceRemoteBridge.App.exe"" --background";
        MemoryStartupRegistrationStore store = new();
        WindowsStartupRegistration registration = new(store, command);

        StartupRegistrationState missing = registration.ReadState();
        False(missing.IsRegistered, "Missing startup item was reported as registered.");
        True(missing.Command is null, "Missing startup item returned a command.");

        StartupRegistrationResult enabled = registration.Apply(enabled: true);
        True(enabled.Succeeded, enabled.Message);
        True(enabled.Enabled, "Enable result was not marked enabled.");
        Equal(command, store.Command!);
        True(registration.ReadState().IsRegistered, "Written startup command did not verify.");

        store.Command = @"""D:\Old App\VoiceRemoteBridge.App.exe"" --background";
        StartupRegistrationState mismatch = registration.ReadState();
        False(mismatch.IsRegistered, "A different startup command was accepted as matching.");
        True(mismatch.Command is not null, "Mismatched startup command was hidden.");

        StartupRegistrationResult disabled = registration.Apply(enabled: false);
        True(disabled.Succeeded, disabled.Message);
        False(disabled.Enabled, "Disable result was marked enabled.");
        True(store.Command is null, "Disable did not remove the startup command.");

        store.ThrowAccessDenied = true;
        StartupRegistrationResult denied = registration.Apply(enabled: true);
        False(denied.Succeeded, "Access-denied write was reported as successful.");
        True(denied.Message.Contains("设置失败", StringComparison.Ordinal), denied.Message);
        StartupRegistrationState unreadable = registration.ReadState();
        True(unreadable.Error is not null, "Access-denied read did not return a diagnostic error.");
    }

    private static async Task SettingsRoundTripAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"VoiceRemoteBridge.Tests.{Guid.NewGuid():N}");
        string file = Path.Combine(directory, "settings.json");
        try
        {
            JsonSettingsStore store = new(file);
            SettingsLoadResult missing = await store.LoadAsync().ConfigureAwait(false);
            False(missing.LoadedExistingFile, "Missing settings file was reported as existing.");

            AppSettings expected = new()
            {
                InteractionMode = BridgeInteractionMode.VoiceCommandPressAgain,
                HoldThresholdMilliseconds = 175,
                MaximumSpeechSeconds = 60,
                AudioActivationTimeoutMilliseconds = 1_500,
                AudioActivationWarmupMilliseconds = 300,
                AudioActivationRmsThreshold = 0.0015,
                AudioActivationConsecutivePackets = 6,
                AudioHandoffMilliseconds = 450,
                StartWithWindows = true,
                SelectedAdapter = new AdapterProfile
                {
                    Id = "wetype",
                    DisplayName = "WeType",
                    TriggerModel = TriggerModel.Toggle,
                    InjectionLifetime = InjectionLifetime.AtomicBatch,
                    GuardPolicy = GuardPolicy.Optional,
                    StartChord = [0x11, 0x5B, 0x10],
                    RequiresActiveIme = "WeType",
                    InputMethodSwitch = new InputMethodSwitchOptions
                    {
                        TargetProfile = "WeType",
                        ActivationTimeoutMilliseconds = 900,
                        PostActivationDelayMilliseconds = 650,
                        RestoreDelayMilliseconds = 550,
                        AllowProfileEnablement = true
                    },
                    VoiceUiConfirmation = new VoiceUiConfirmationOptions
                    {
                        ProcessName = "wetype_update",
                        WindowClass = "wetype.flutter.setting",
                        TimeoutMilliseconds = 2_500
                    }
                }
            };
            await store.SaveAsync(expected).ConfigureAwait(false);
            SettingsLoadResult loaded = await store.LoadAsync().ConfigureAwait(false);
            True(loaded.LoadedExistingFile, "Saved settings were not loaded.");
            Equal(0, loaded.Errors.Count);
            Equal(175, loaded.Settings.HoldThresholdMilliseconds);
            Equal(60, loaded.Settings.MaximumSpeechSeconds);
            Equal(BridgeInteractionMode.VoiceCommandPressAgain, loaded.Settings.InteractionMode);
            Equal(1_500, loaded.Settings.AudioActivationTimeoutMilliseconds);
            Equal(300, loaded.Settings.AudioActivationWarmupMilliseconds);
            Equal(0.0015, loaded.Settings.AudioActivationRmsThreshold);
            Equal(6, loaded.Settings.AudioActivationConsecutivePackets);
            Equal(450, loaded.Settings.AudioHandoffMilliseconds);
            True(loaded.Settings.StartWithWindows, "StartWithWindows was not persisted.");
            Equal("WeType", loaded.Settings.SelectedAdapter!.InputMethodSwitch!.TargetProfile);
            Equal(900, loaded.Settings.SelectedAdapter.InputMethodSwitch.ActivationTimeoutMilliseconds);
            Equal(650, loaded.Settings.SelectedAdapter.InputMethodSwitch.PostActivationDelayMilliseconds);
            Equal(550, loaded.Settings.SelectedAdapter.InputMethodSwitch.RestoreDelayMilliseconds);
            True(
                loaded.Settings.SelectedAdapter.InputMethodSwitch.AllowProfileEnablement,
                "AllowProfileEnablement was not persisted.");
            Equal("wetype_update", loaded.Settings.SelectedAdapter.VoiceUiConfirmation!.ProcessName);
            Equal("wetype.flutter.setting", loaded.Settings.SelectedAdapter.VoiceUiConfirmation.WindowClass);
            Equal(2_500, loaded.Settings.SelectedAdapter.VoiceUiConfirmation.TimeoutMilliseconds);

            await File.WriteAllTextAsync(
                file,
                "{\"schemaVersion\":1,\"hardwareId\":\"VID_1915&PID_1025\",\"audioEndpointName\":\"SG Control Mic\"}")
                .ConfigureAwait(false);
            SettingsLoadResult legacy = await store.LoadAsync().ConfigureAwait(false);
            Equal(0, legacy.Errors.Count);
            Equal(BridgeInteractionMode.VoiceCommandPressAgain, legacy.Settings.InteractionMode);
            Equal(1_200, legacy.Settings.AudioActivationTimeoutMilliseconds);
            False(legacy.Settings.StartWithWindows, "Legacy settings unexpectedly enabled startup.");

            await File.WriteAllTextAsync(file, "{not-json").ConfigureAwait(false);
            SettingsLoadResult invalid = await store.LoadAsync().ConfigureAwait(false);
            True(invalid.Errors.Count > 0, "Invalid settings JSON was accepted.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void VoiceUiWindowProbeDoesNotMatchMissingProcess()
    {
        VoiceUiWindowMatch match = VoiceUiWindowProbe.FindVisible(new VoiceUiConfirmationOptions
        {
            ProcessName = $"VoiceRemoteBridge.Missing.{Guid.NewGuid():N}",
            WindowClass = "missing.window.class",
            TimeoutMilliseconds = 500
        });
        False(match.Found, "A missing voice UI process unexpectedly matched a window.");
    }

    private static async Task InputMethodSessionLifecycleAsync()
    {
        InputMethodProfileDescriptor original = new(
            2,
            0x0409,
            Guid.Empty,
            Guid.Empty,
            new nint(unchecked((int)0x04090409)),
            "Original layout");
        InputMethodProfileDescriptor target = new(
            1,
            0x0804,
            new Guid("86598FB9-66A2-463E-B9C2-AEB906D477AD"),
            new Guid("607FDF85-FCC8-4DBD-A365-41296F980C9C"),
            nint.Zero,
            "WeType");
        InputMethodSwitchOptions options = new()
        {
            TargetProfile = "WeType",
            ActivationTimeoutMilliseconds = 100,
            PostActivationDelayMilliseconds = 1_000,
            RestoreDelayMilliseconds = 500,
            AllowProfileEnablement = true
        };

        List<string> events = [];
        FakeInputMethodProfileManager manager = new(original, target, events);
        InputMethodSessionController controller = new(
            manager,
            (duration, _) =>
            {
                events.Add($"delay:{duration.TotalMilliseconds:F0}");
                return Task.CompletedTask;
            });
        InputMethodSessionResult begin = await controller.BeginAsync(7, options).ConfigureAwait(false);
        True(begin.Succeeded && begin.Changed, begin.Message);
        True(controller.HasActiveSession, "Successful switch did not retain the original profile snapshot.");
        Equal("activate:WeType:enable=True", events[0]);
        Equal("delay:1000", events[1]);

        InputMethodSessionResult restore = await controller.RestoreAsync(
            7,
            options,
            emergency: false).ConfigureAwait(false);
        True(restore.Succeeded && restore.Changed, restore.Message);
        Equal("delay:500", events[2]);
        Equal("activate:Original layout:enable=False", events[3]);
        True(manager.Active.IsSameProfile(original), "Normal completion did not restore the original profile.");
        False(controller.HasActiveSession, "Completed restore left a session active.");

        events.Clear();
        manager.Active = target;
        InputMethodSessionController alreadyActiveController = new(manager, (_, _) => Task.CompletedTask);
        InputMethodSessionResult alreadyActive = await alreadyActiveController.BeginAsync(8, options).ConfigureAwait(false);
        True(alreadyActive.Succeeded && !alreadyActive.Changed, alreadyActive.Message);
        await alreadyActiveController.RestoreAsync(8, options, emergency: false).ConfigureAwait(false);
        Equal(0, events.Count);

        events.Clear();
        manager.Active = original;
        manager.SuppressActivationFor = target;
        InputMethodSessionController failedController = new(manager, (_, _) => Task.CompletedTask);
        InputMethodSessionResult failed = await failedController.BeginAsync(9, options).ConfigureAwait(false);
        False(failed.Succeeded, "Unconfirmed target activation was accepted.");
        False(failedController.HasActiveSession, "Failed activation retained an active session.");
        Equal("activate:WeType:enable=True", events[0]);
        Equal("activate:Original layout:enable=False", events[^1]);
        True(manager.Active.IsSameProfile(original), "Failed activation did not roll back the original profile.");

        events.Clear();
        manager.SuppressActivationFor = null;
        InputMethodSessionController emergencyController = new(
            manager,
            (duration, _) =>
            {
                events.Add($"delay:{duration.TotalMilliseconds:F0}");
                return Task.CompletedTask;
            });
        True((await emergencyController.BeginAsync(10, options).ConfigureAwait(false)).Succeeded, "Emergency setup failed.");
        events.Clear();
        InputMethodSessionResult stale = await emergencyController.RestoreAsync(
            99,
            options,
            emergency: true).ConfigureAwait(false);
        False(stale.Succeeded, "A stale epoch was allowed to restore the profile.");
        True(emergencyController.HasActiveSession, "A stale restore discarded the live session.");
        InputMethodSessionResult emergency = await emergencyController.RestoreAsync(
            10,
            options,
            emergency: true).ConfigureAwait(false);
        True(emergency.Succeeded, emergency.Message);
        True(events.All(item => !item.StartsWith("delay:", StringComparison.Ordinal)), "Emergency restore waited for the commit delay.");

        manager.Active = original;
        manager.FailActivationFor = null;
        InputMethodSessionController restoreFailureController = new(manager, (_, _) => Task.CompletedTask);
        True((await restoreFailureController.BeginAsync(11, options).ConfigureAwait(false)).Succeeded, "Restore-failure setup failed.");
        manager.FailActivationFor = original;
        InputMethodSessionResult restoreFailure = await restoreFailureController.RestoreAsync(
            11,
            options,
            emergency: true).ConfigureAwait(false);
        False(restoreFailure.Succeeded, "Restore activation failure was reported as successful.");
        False(restoreFailureController.HasActiveSession, "Restore failure left a stale session active.");
    }

    private static void InputMethodProfileSmoke()
    {
        using InputMethodProfileManager manager = new();
        InputMethodProfileDescriptor active = manager.CaptureActiveProfile();
        Console.WriteLine($"INFO active-input-profile={active.Describe()}");
        foreach (InputMethodProfileDescriptor profile in manager.EnumerateInstalledProfiles())
        {
            Console.WriteLine(
                $"INFO installed-input-profile=type:{profile.ProfileType},lang:0x{profile.LanguageId:X4},class:{profile.ClassId:B},profile:{profile.ProfileId:B},flags-hkl:0x{profile.KeyboardLayout.ToInt64():X}");
        }

        foreach (InputMethodProfileDescriptor profile in manager.EnumerateInstalledProfiles(0x0804))
        {
            Console.WriteLine(
                $"INFO zh-input-profile=type:{profile.ProfileType},lang:0x{profile.LanguageId:X4},class:{profile.ClassId:B},profile:{profile.ProfileId:B},flags-hkl:0x{profile.KeyboardLayout.ToInt64():X}");
        }

        InputMethodProfileDescriptor weType = manager.FindInstalledProfile("WeType")
            ?? throw new InvalidOperationException("Installed WeType TSF profile was not discovered.");
        Equal(new Guid("86598FB9-66A2-463E-B9C2-AEB906D477AD"), weType.ClassId);
        Equal(new Guid("607FDF85-FCC8-4DBD-A365-41296F980C9C"), weType.ProfileId);
    }

    private static async Task InputMethodActivationSmokeAsync()
    {
        using InputMethodProfileManager manager = new();
        InputMethodProfileDescriptor original = manager.CaptureActiveProfile();
        Console.WriteLine($"INFO activation-original={original.Describe()},lang=0x{original.LanguageId:X4}");
        InputMethodSessionController controller = new(manager);
        InputMethodSwitchOptions options = new()
        {
            TargetProfile = "WeType",
            ActivationTimeoutMilliseconds = 1_500,
            PostActivationDelayMilliseconds = 0,
            RestoreDelayMilliseconds = 0,
            AllowProfileEnablement = true
        };
        InputMethodSessionResult begin = await controller.BeginAsync(1, options).ConfigureAwait(false);
        if (!begin.Succeeded)
        {
            throw new InvalidOperationException(begin.Message);
        }

        InputMethodSessionResult restore = await controller.RestoreAsync(
            1,
            options,
            emergency: true).ConfigureAwait(false);
        if (!restore.Succeeded)
        {
            throw new InvalidOperationException(restore.Message);
        }

        InputMethodProfileDescriptor restored = manager.CaptureActiveProfile();
        True(restored.IsSameProfile(original), "Activation smoke did not restore the original input profile.");
    }

    private static async Task InputMethodCycleSmokeAsync()
    {
        FocusSnapshot focus = ForegroundFocusProbe.Capture();
        True(focus.ForegroundThreadId != 0, "No foreground input thread was available for the cycle smoke test.");
        ActiveInputMethodSnapshot original = ActiveInputMethodProbe.Capture(focus.ForegroundThreadId);
        Console.WriteLine($"INFO cycle-original={DescribeInputMethod(original)}");
        if (original.Matches("WeType") || original.Matches("微信输入法"))
        {
            return;
        }

        Win32KeyInjector injector = new();
        bool restored = false;
        bool foundTarget = false;
        try
        {
            for (int attempt = 1; attempt <= 12; attempt++)
            {
                InjectionResult injection = injector.Tap([0x5B, 0x20], KeyInjectionMode.VirtualKey);
                True(injection.Succeeded, $"Win+Space injection failed: {injection.Message}");
                await Task.Delay(350).ConfigureAwait(false);
                ActiveInputMethodSnapshot current = ActiveInputMethodProbe.Capture(focus.ForegroundThreadId);
                Console.WriteLine($"INFO cycle-step-{attempt}={DescribeInputMethod(current)}");
                if (current.Matches("WeType") || current.Matches("微信输入法"))
                {
                    foundTarget = true;
                    break;
                }

                if (attempt > 1 && IsSameInputMethod(current, original))
                {
                    restored = true;
                    break;
                }
            }
        }
        finally
        {
            if (!restored)
            {
                for (int attempt = 1; attempt <= 12; attempt++)
                {
                    InjectionResult injection = injector.Tap([0x5B, 0x20], KeyInjectionMode.VirtualKey);
                    if (!injection.Succeeded)
                    {
                        break;
                    }

                    await Task.Delay(350).ConfigureAwait(false);
                    ActiveInputMethodSnapshot current = ActiveInputMethodProbe.Capture(focus.ForegroundThreadId);
                    Console.WriteLine($"INFO restore-step-{attempt}={DescribeInputMethod(current)}");
                    if (IsSameInputMethod(current, original))
                    {
                        restored = true;
                        break;
                    }
                }
            }
        }

        True(restored, "Input-method cycle smoke test could not confirm restoration of the original input method.");
        True(foundTarget, "Input-method cycle smoke test did not discover WeType in the system switch order.");
    }

    private static bool IsSameInputMethod(
        ActiveInputMethodSnapshot left,
        ActiveInputMethodSnapshot right) =>
        left.LayoutHandle == right.LayoutHandle &&
        string.Equals(left.Description, right.Description, StringComparison.OrdinalIgnoreCase);

    private static string DescribeInputMethod(ActiveInputMethodSnapshot value) =>
        $"layout:{value.LayoutId},description:{value.Description}";

    private static async Task GuardRoundTripAsync(string[] args)
    {
        GuardLaunchInfo launch = CreateGuardLaunch(args);
        await using InputGuardProcessManager manager = new(
            launch,
            TimeSpan.FromMilliseconds(600),
            TimeSpan.FromSeconds(5));
        Console.WriteLine("STEP guard-start");
        await manager.StartAsync().ConfigureAwait(false);

        Console.WriteLine("STEP hold-1");
        GuardResponse hold1 = await manager.Client.HoldAsync(1, [0x7C], KeyInjectionMode.VirtualKey).ConfigureAwait(false);
        Succeeded(hold1, "epoch 1 hold");
        True(hold1.KeysHeld, "Guard did not report a held key.");

        Console.WriteLine("STEP renew-1");
        GuardResponse renew = await manager.Client.RenewAsync(1).ConfigureAwait(false);
        Succeeded(renew, "epoch 1 renew");

        Console.WriteLine("STEP release-1");
        GuardResponse release1 = await manager.Client.ReleaseAsync(1).ConfigureAwait(false);
        Succeeded(release1, "epoch 1 release");
        False(release1.KeysHeld, "Guard still reported a held key after release.");

        Console.WriteLine("STEP hold-2");
        GuardResponse hold2 = await manager.Client.HoldAsync(2, [0x7C], KeyInjectionMode.VirtualKey).ConfigureAwait(false);
        Succeeded(hold2, "epoch 2 hold");
        Console.WriteLine("STEP stale-release");
        GuardResponse staleRelease = await manager.Client.ReleaseAsync(1).ConfigureAwait(false);
        Succeeded(staleRelease, "stale release");
        True(staleRelease.KeysHeld, "Stale release incorrectly cleared a newer hold.");
        Console.WriteLine("STEP release-2");
        Succeeded(await manager.Client.ReleaseAsync(2).ConfigureAwait(false), "epoch 2 release");

        Console.WriteLine("STEP hold-3");
        Succeeded(await manager.Client.HoldAsync(3, [0x7C], KeyInjectionMode.VirtualKey).ConfigureAwait(false), "epoch 3 hold");
        await Task.Delay(900).ConfigureAwait(false);
        Console.WriteLine("STEP ping-after-lease");
        GuardResponse afterLease = await manager.Client.PingAsync().ConfigureAwait(false);
        Succeeded(afterLease, "post-lease ping");
        False(afterLease.KeysHeld, "Expired lease did not release the held key.");

        Console.WriteLine("STEP guard-stop");
        await manager.StopAsync(3).ConfigureAwait(false);
    }

    private static void VoiceButtonLearningAnalysis()
    {
        VoiceButtonLearningEndpoint endpoint = new(
            "test",
            "Test endpoint",
            "VID_1915&PID_1025",
            HidTransport.HidInterface,
            0x0C,
            0x01,
            2);
        VoiceButtonLearningResult result = VoiceButtonLearningSession.Analyze(
            endpoint,
            [new byte[] { 0x00, 0x00 }],
            [new byte[] { 0x00, 0x10 }, new byte[] { 0x00, 0x10 }],
            [new byte[] { 0x00, 0x00 }]);
        True(result.Succeeded, result.Message);
        Equal("0010", result.Binding!.Pressed.MaskHex);
        RemoteSignalKind? decoded = new HidSignalDecoder(result.Binding).Decode(0x0C, 0x01, [0x00, 0x00]);
        True(decoded == RemoteSignalKind.Neutral, "Learned release pattern did not decode as neutral.");

        VoiceButtonLearningResult missingRelease = VoiceButtonLearningSession.Analyze(
            endpoint,
            [],
            [new byte[] { 0x00, 0x10 }],
            []);
        False(missingRelease.Succeeded, "A learning trace without release/neutral evidence was accepted.");

        VoiceButtonLearningResult missingPhysicalRelease = VoiceButtonLearningSession.Analyze(
            endpoint,
            [new byte[] { 0x00, 0x00 }],
            [new byte[] { 0x00, 0x10 }],
            []);
        False(missingPhysicalRelease.Succeeded, "A trace without a report in the physical-release phase was accepted.");

        VoiceButtonLearningResult pulseOnly = VoiceButtonLearningSession.Analyze(
            endpoint,
            [new byte[] { 0x00, 0x00 }],
            [new byte[] { 0x00, 0x10 }, new byte[] { 0x00, 0x00 }],
            [new byte[] { 0x00, 0x00 }]);
        False(pulseOnly.Succeeded, "A pulse that returned to neutral during the held phase was accepted as Push-to-Talk.");

        VoiceButtonLearningResult acceptedOneShot = VoiceButtonLearningSession.Analyze(
            endpoint,
            [new byte[] { 0x00, 0x00 }],
            [new byte[] { 0x00, 0x10 }, new byte[] { 0x00, 0x00 }],
            [new byte[] { 0x00, 0x00 }],
            allowOneShotVoice: true);
        True(acceptedOneShot.Succeeded, acceptedOneShot.Message);
        Equal("0010", acceptedOneShot.Binding!.Pressed.MaskHex);
        True(
            acceptedOneShot.Message.Contains("不代表物理松手", StringComparison.Ordinal),
            "One-shot learning result did not state its physical-release limitation.");
    }

    private static void AudioCarrierMetricsAnalysis()
    {
        AudioCarrierPacketMetrics zero = AudioCarrierMetricsCalculator.CalculateFloat32(new float[80]);
        Equal(0d, zero.Rms);
        Equal(0d, zero.NonZeroByteRatio);

        float[] carrier = Enumerable.Range(0, 80)
            .Select(index => index % 2 == 0 ? 0.004f : -0.004f)
            .ToArray();
        AudioCarrierPacketMetrics active = AudioCarrierMetricsCalculator.CalculateFloat32(carrier);
        True(active.Rms > 0.0039 && active.Rms < 0.0041, $"Unexpected carrier RMS: {active.Rms}.");
        True(active.NonZeroByteRatio >= 0.10, $"Unexpected non-zero ratio: {active.NonZeroByteRatio}.");

        AudioCarrierActivationOptions valid = new(
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(1_200),
            0.001,
            5);
        Equal(0, valid.Validate().Count);
        True(
            (valid with { WarmupDuration = TimeSpan.FromMilliseconds(1_200) }).Validate().Count > 0,
            "An activation warmup equal to the timeout was accepted.");
    }

    private static void GuardRestartBackoffPolicy()
    {
        GuardRestartPolicy policy = new();
        DateTimeOffset now = new(2026, 8, 24, 0, 0, 0, TimeSpan.Zero);
        True(policy.CanAttempt(now, out TimeSpan initialWait), "A new policy rejected its first attempt.");
        Equal(TimeSpan.Zero, initialWait);

        GuardRestartSnapshot first = policy.RecordFailure(now);
        Equal(1, first.ConsecutiveFailures);
        False(policy.CanAttempt(now + TimeSpan.FromMilliseconds(999), out _), "One-second backoff ended early.");
        True(policy.CanAttempt(now + TimeSpan.FromSeconds(1), out _), "One-second backoff did not end.");

        policy.RecordFailure(now + TimeSpan.FromSeconds(1));
        policy.RecordFailure(now + TimeSpan.FromSeconds(3));
        policy.RecordFailure(now + TimeSpan.FromSeconds(7));
        GuardRestartSnapshot fifth = policy.RecordFailure(now + TimeSpan.FromSeconds(15));
        True(fifth.LockedOut, "Five consecutive guard failures did not lock out restarts.");
        False(policy.CanAttempt(now + TimeSpan.FromHours(1), out TimeSpan lockedWait), "Locked-out guard was allowed to restart.");
        Equal(Timeout.InfiniteTimeSpan, lockedWait);

        policy.ObserveHealthy(now + TimeSpan.FromSeconds(61), now);
        True(policy.CanAttempt(now + TimeSpan.FromSeconds(61), out _), "Stable runtime did not reset guard failures.");
        Equal(0, policy.Snapshot.ConsecutiveFailures);
    }

    private static async Task AdapterExecutionRoundTripAsync(string[] args)
    {
        GuardLaunchInfo launch = CreateGuardLaunch(args);
        await using AdapterExecutionService service = new(
            () => new InputGuardProcessManager(
                launch,
                TimeSpan.FromMilliseconds(900),
                TimeSpan.FromSeconds(5)));
        AdapterProfile profile = new()
        {
            Id = "integration-f13",
            DisplayName = "Integration F13",
            TriggerModel = TriggerModel.PushToTalk,
            InjectionLifetime = InjectionLifetime.HeldAcrossSpeech,
            GuardPolicy = GuardPolicy.Required,
            StartChord = [0x7C]
        };
        AdapterExecutionResult start = await service.StartAsync(profile, 10).ConfigureAwait(false);
        True(start.Succeeded, start.Message);
        True(service.GuardIsRunning, "Adapter service did not start InputGuard.");
        True((await service.RenewAsync(10).ConfigureAwait(false)).Succeeded, "Adapter lease renewal failed.");
        AdapterExecutionResult stop = await service.StopAsync(profile, 10, emergency: false).ConfigureAwait(false);
        True(stop.Succeeded, stop.Message);
    }

    private static async Task RawInputSourceStartsAndStopsAsync()
    {
        IReadOnlyList<RawInputDeviceDescriptor> devices = RawInputReportSource.Enumerate("VID_1915&PID_1025");
        True(devices.Count > 0, "The target receiver was not present in Raw Input enumeration.");
        RawInputDeviceDescriptor selected = devices.First(item => item.UsagePage != 0 && item.Usage != 0);
        HidButtonBinding binding = new()
        {
            HardwareId = "VID_1915&PID_1025",
            Transport = HidTransport.RawInput,
            UsagePage = selected.UsagePage,
            Usage = selected.Usage,
            Pressed = new HidReportPattern { ValueHex = "00", MaskHex = "FF" },
            Released = new HidReportPattern { ValueHex = "01", MaskHex = "FF" }
        };
        await using RawInputReportSource source = new(binding);
        source.Start();
        await Task.Delay(250).ConfigureAwait(false);
        await source.StopAsync().ConfigureAwait(false);
    }

    private static void AudioEndpointIsPresent()
    {
        AudioEndpointStatus status = AudioEndpointStatusProbe.FindCaptureEndpoint("SG Control Mic");
        True(status.Found, status.Message);
        True(!string.IsNullOrWhiteSpace(status.EndpointId), "The audio endpoint had no id.");
        Console.WriteLine($"INFO AudioEndpoint muted={status.IsMuted} peak={status.CurrentPeak:F4}");
    }

    private static async Task ParentCrashReleasesGuardAsync(string[] args)
    {
        Console.WriteLine("STEP fault-parent-start");
        using Process child = StartFaultChild("--fault-parent-child", args);
        int guardProcessId = await ReadReadyGuardProcessIdAsync(child).ConfigureAwait(false);
        Console.WriteLine($"STEP fault-parent-ready guardPid={guardProcessId}");
        child.Kill(entireProcessTree: false);
        await child.WaitForExitAsync().ConfigureAwait(false);
        Console.WriteLine("STEP fault-parent-child-killed");

        try
        {
            using Process guard = Process.GetProcessById(guardProcessId);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            await guard.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // The guard may release and exit before the parent reacquires its process handle.
        }
        Console.WriteLine("STEP fault-parent-guard-exited");
        await WaitForKeyUpAsync(0x7C, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    private static async Task GuardCrashTriggersMainFallbackAsync(string[] args)
    {
        Console.WriteLine("STEP fault-guard-start");
        using Process child = StartFaultChild("--fault-guard-child", args);
        int guardProcessId = await ReadReadyGuardProcessIdAsync(child).ConfigureAwait(false);
        Console.WriteLine($"STEP fault-guard-ready guardPid={guardProcessId}");
        using (Process guard = Process.GetProcessById(guardProcessId))
        {
            guard.Kill(entireProcessTree: false);
            await guard.WaitForExitAsync().ConfigureAwait(false);
        }
        Console.WriteLine("STEP fault-guard-killed");

        string? handled = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        True(handled is not null && handled.StartsWith("CHILD_GUARD_HANDLED", StringComparison.Ordinal),
            $"Guard crash child did not report its fallback result: {handled}");
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Equal(0, child.ExitCode);
        Console.WriteLine($"STEP fault-guard-child-exited {handled}");
        await WaitForKeyUpAsync(0x7C, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    private static async Task<int> RunParentCrashChildAsync(string[] args)
    {
        GuardLaunchInfo launch = CreateGuardLaunch(args);
        await using InputGuardProcessManager manager = new(
            launch,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5));
        await manager.StartAsync().ConfigureAwait(false);
        Succeeded(await manager.Client.HoldAsync(100, [0x7C], KeyInjectionMode.VirtualKey).ConfigureAwait(false), "parent-crash hold");
        Console.WriteLine($"CHILD_READY guardPid={manager.ProcessId}");
        await Console.Out.FlushAsync().ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        return 1;
    }

    private static async Task<int> RunGuardCrashChildAsync(string[] args)
    {
        GuardLaunchInfo launch = CreateGuardLaunch(args);
        InputGuardProcessManager? launchedManager = null;
        TaskCompletionSource guardExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using AdapterExecutionService service = new(() =>
        {
            launchedManager = new InputGuardProcessManager(
                launch,
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(5));
            return launchedManager;
        });
        service.GuardExited += (_, _) => guardExited.TrySetResult();
        AdapterProfile profile = new()
        {
            Id = "guard-crash-f13",
            DisplayName = "Guard crash F13",
            TriggerModel = TriggerModel.PushToTalk,
            InjectionLifetime = InjectionLifetime.HeldAcrossSpeech,
            GuardPolicy = GuardPolicy.Required,
            StartChord = [0x7C]
        };
        AdapterExecutionResult start = await service.StartAsync(profile, 200).ConfigureAwait(false);
        True(start.Succeeded, start.Message);
        Console.WriteLine($"CHILD_READY guardPid={launchedManager!.ProcessId}");
        await Console.Out.FlushAsync().ConfigureAwait(false);
        await guardExited.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await WaitForKeyUpAsync(0x7C, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Console.WriteLine($"CHILD_GUARD_HANDLED failures={service.GuardRestart.ConsecutiveFailures}");
        await Console.Out.FlushAsync().ConfigureAwait(false);
        return service.GuardRestart.ConsecutiveFailures == 1 ? 0 : 1;
    }

    private static Process StartFaultChild(string mode, string[] args)
    {
        string guardExecutable = RequireOption(args, "--guard-exe");
        ProcessStartInfo startInfo = new()
        {
            FileName = Environment.ProcessPath ?? throw new InvalidOperationException("Current process path is unavailable."),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (string.Equals(Path.GetFileName(startInfo.FileName), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }

        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add("--guard-exe");
        startInfo.ArgumentList.Add(Path.GetFullPath(guardExecutable));
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start fault-test child.");
    }

    private static async Task<int> ReadReadyGuardProcessIdAsync(Process child)
    {
        string? ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        string error = child.HasExited
            ? await child.StandardError.ReadToEndAsync().ConfigureAwait(false)
            : "child still running";
        True(ready is not null && ready.StartsWith("CHILD_READY guardPid=", StringComparison.Ordinal),
            $"Fault-test child did not become ready: {ready}; stderr={error}");
        return int.Parse(ready!["CHILD_READY guardPid=".Length..], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task WaitForKeyUpAsync(ushort key, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (KeyboardStateProbe.IsDown(key) && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(20).ConfigureAwait(false);
        }

        False(KeyboardStateProbe.IsDown(key), $"Virtual key 0x{key:X2} remained down after fault recovery.");
    }

    private static string RequireOption(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }

        throw new ArgumentException($"Missing {name}.");
    }

    private static GuardLaunchInfo CreateGuardLaunch(string[] args)
    {
        string? guardExecutable = GetOption(args, "--guard-exe");
        if (guardExecutable is not null)
        {
            return new GuardLaunchInfo(Path.GetFullPath(guardExecutable), ["--input-guard"]);
        }

        string dotnetPath = RequireOption(args, "--dotnet");
        string guardDll = RequireOption(args, "--guard");
        return args.Contains("--embedded-guard", StringComparer.OrdinalIgnoreCase)
            ? new GuardLaunchInfo(dotnetPath, [guardDll, "--input-guard"])
            : new GuardLaunchInfo(dotnetPath, [guardDll]);
    }

    private static string? GetOption(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static void Succeeded(GuardResponse response, string operation)
    {
        if (!response.Succeeded)
        {
            throw new InvalidOperationException($"{operation} failed: {response.Message} Win32={response.Win32Error}");
        }
    }

    private sealed class MemoryStartupRegistrationStore : IStartupRegistrationStore
    {
        public string? Command { get; set; }

        public bool ThrowAccessDenied { get; set; }

        public string? Read()
        {
            ThrowIfDenied();
            return Command;
        }

        public void Write(string command)
        {
            ThrowIfDenied();
            Command = command;
        }

        public void Delete()
        {
            ThrowIfDenied();
            Command = null;
        }

        private void ThrowIfDenied()
        {
            if (ThrowAccessDenied)
            {
                throw new UnauthorizedAccessException("test access denied");
            }
        }
    }

    private sealed class FakeInputMethodProfileManager : IInputMethodProfileManager
    {
        private readonly InputMethodProfileDescriptor target;
        private readonly IList<string> events;

        internal FakeInputMethodProfileManager(
            InputMethodProfileDescriptor active,
            InputMethodProfileDescriptor target,
            IList<string> events)
        {
            Active = active;
            this.target = target;
            this.events = events;
        }

        internal InputMethodProfileDescriptor Active { get; set; }

        internal InputMethodProfileDescriptor? SuppressActivationFor { get; set; }

        internal InputMethodProfileDescriptor? FailActivationFor { get; set; }

        public InputMethodProfileDescriptor CaptureActiveProfile() => Active;

        public InputMethodProfileDescriptor? FindInstalledProfile(string targetProfile) =>
            string.Equals(targetProfile, target.Description, StringComparison.OrdinalIgnoreCase)
                ? target
                : null;

        public void ActivateProfile(InputMethodProfileDescriptor profile, bool enableIfNeeded)
        {
            events.Add($"activate:{profile.Describe()}:enable={enableIfNeeded}");
            if (FailActivationFor?.IsSameProfile(profile) == true)
            {
                throw new InvalidOperationException("test activation failure");
            }

            if (SuppressActivationFor?.IsSameProfile(profile) != true)
            {
                Active = profile;
            }
        }
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Equal<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
        }
    }

    private static void True(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool value, string message) => True(!value, message);
}
