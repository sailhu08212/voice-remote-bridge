using VoiceRemoteBridge.Core;

namespace VoiceRemoteBridge.Core.Tests;

internal static class Program
{
    private static readonly List<(string Name, Action Body)> Tests =
    [
        ("InitialNeutralArmsBridge", InitialNeutralArmsBridge),
        ("ShortPressDoesNotStart", ShortPressDoesNotStart),
        ("LongPressStartsAndReleaseStops", LongPressStartsAndReleaseStops),
        ("VoiceCommandOneShotNeutralDoesNotCancelCandidate", VoiceCommandOneShotNeutralDoesNotCancelCandidate),
        ("VoiceCommandSecondPressStopsSession", VoiceCommandSecondPressStopsSession),
        ("VoiceCommandLateNeutralStopsSession", VoiceCommandLateNeutralStopsSession),
        ("VoiceCommandRepeatedReportStopsSession", VoiceCommandRepeatedReportStopsSession),
        ("VoiceCommandFailedActivationReturnsToIdle", VoiceCommandFailedActivationReturnsToIdle),
        ("AdapterStartMustBeConfirmedBeforeSpeaking", AdapterStartMustBeConfirmedBeforeSpeaking),
        ("AdapterStartFailureAbandonsWithoutStop", AdapterStartFailureAbandonsWithoutStop),
        ("StaleAdapterConfirmationIsIgnored", StaleAdapterConfirmationIsIgnored),
        ("MaximumDurationLatchesUntilRelease", MaximumDurationLatchesUntilRelease),
        ("RepeatedReportCannotStartFromIdleOrLatch", RepeatedReportCannotStartFromIdleOrLatch),
        ("FocusChangeAbortsCandidate", FocusChangeAbortsCandidate),
        ("DeviceDisconnectEmergencyStopsAndUnarms", DeviceDisconnectEmergencyStopsAndUnarms),
        ("StopTimeoutEmergencyReleases", StopTimeoutEmergencyReleases),
        ("StalePreflightResultIsIgnored", StalePreflightResultIsIgnored),
        ("NonMonotonicTimestampIsRejected", NonMonotonicTimestampIsRejected),
        ("AdapterProfileEnforcesGuardForHeldKeys", AdapterProfileEnforcesGuardForHeldKeys),
        ("AdapterProfileRejectsInconsistentTriggerModels", AdapterProfileRejectsInconsistentTriggerModels),
        ("VoiceCommandSettingsRejectHeldAdapter", VoiceCommandSettingsRejectHeldAdapter),
        ("InputMethodSwitchOptionsAreValidated", InputMethodSwitchOptionsAreValidated),
        ("VoiceUiConfirmationOptionsAreValidated", VoiceUiConfirmationOptionsAreValidated),
        ("DefaultMaximumSpeechIsFiveMinutes", DefaultMaximumSpeechIsFiveMinutes),
        ("KeyChordCodecParsesAndFormatsSupportedKeys", KeyChordCodecParsesAndFormatsSupportedKeys),
        ("KeyChordCodecRejectsUnknownAndDuplicateKeys", KeyChordCodecRejectsUnknownAndDuplicateKeys),
        ("HidSignalDecoderDistinguishesPressRepeatAndRelease", HidSignalDecoderDistinguishesPressRepeatAndRelease),
        ("HidReportMaskIgnoresUnboundBits", HidReportMaskIgnoresUnboundBits),
        ("OneThousandDistinctMutatedSequencesPreserveInvariants", OneThousandDistinctMutatedSequencesPreserveInvariants)
    ];

    private static int Main()
    {
        int failed = 0;
        DateTimeOffset started = DateTimeOffset.Now;
        foreach ((string name, Action body) in Tests)
        {
            try
            {
                body();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
            }
        }

        Console.WriteLine($"RESULT total={Tests.Count} passed={Tests.Count - failed} failed={failed} started={started:O}");
        return failed == 0 ? 0 : 1;
    }

    private static void InitialNeutralArmsBridge()
    {
        BridgeStateMachine machine = CreateMachine();
        Equal(BridgeState.Unarmed, machine.Snapshot.State);
        NoActions(machine.HandleSignal(RemoteSignalKind.Neutral, Ms(0)));
        Equal(BridgeState.Idle, machine.Snapshot.State);
        True(machine.Snapshot.PhysicalNeutral, "Bridge must record the neutral state.");
    }

    private static void ShortPressDoesNotStart()
    {
        BridgeStateMachine machine = ArmedMachine();
        HasAction(machine.HandleSignal(RemoteSignalKind.Pressed, Ms(10)), BridgeActionKind.CaptureFocus);
        NoActions(machine.HandleSignal(RemoteSignalKind.Released, Ms(80)));
        Equal(BridgeState.Idle, machine.Snapshot.State);
        NoActions(machine.Tick(Ms(500)));
    }

    private static void LongPressStartsAndReleaseStops()
    {
        BridgeStateMachine machine = ArmedMachine();
        machine.HandleSignal(RemoteSignalKind.Pressed, Ms(10));
        NoActions(machine.Tick(Ms(109)));
        BridgeAction evaluate = SingleAction(machine.Tick(Ms(110)), BridgeActionKind.EvaluatePreflight);
        HasAction(machine.CompletePreflight(evaluate.Epoch, PreflightDecision.Ready, Ms(110)), BridgeActionKind.StartAdapter);
        Equal(BridgeState.Starting, machine.Snapshot.State);
        machine.AdapterStarted(evaluate.Epoch, Ms(111));
        Equal(BridgeState.Speaking, machine.Snapshot.State);

        HasAction(machine.HandleSignal(RemoteSignalKind.Released, Ms(500)), BridgeActionKind.StopAdapter);
        Equal(BridgeState.Stopping, machine.Snapshot.State);
        machine.AdapterStopped(evaluate.Epoch, Ms(501));
        Equal(BridgeState.Latched, machine.Snapshot.State);
        machine.Tick(Ms(800));
        Equal(BridgeState.Idle, machine.Snapshot.State);
    }

    private static void VoiceCommandOneShotNeutralDoesNotCancelCandidate()
    {
        BridgeStateMachine machine = VoiceCommandMachine();
        machine.HandleSignal(RemoteSignalKind.Neutral, Ms(0));
        HasAction(machine.HandleSignal(RemoteSignalKind.Pressed, Ms(10)), BridgeActionKind.CaptureFocus);
        NoActions(machine.HandleSignal(RemoteSignalKind.Neutral, Ms(11)));
        Equal(BridgeState.Candidate, machine.Snapshot.State);

        BridgeAction evaluate = SingleAction(machine.Tick(Ms(110)), BridgeActionKind.EvaluatePreflight);
        HasAction(
            machine.CompletePreflight(evaluate.Epoch, PreflightDecision.Ready, Ms(111)),
            BridgeActionKind.StartAdapter);
        Equal(BridgeState.Starting, machine.Snapshot.State);
        machine.AdapterStarted(evaluate.Epoch, Ms(112));
        Equal(BridgeState.Speaking, machine.Snapshot.State);
    }

    private static void VoiceCommandSecondPressStopsSession()
    {
        BridgeStateMachine machine = VoiceCommandMachine();
        machine.HandleSignal(RemoteSignalKind.Neutral, Ms(0));
        machine.HandleSignal(RemoteSignalKind.Pressed, Ms(10));
        machine.HandleSignal(RemoteSignalKind.Neutral, Ms(11));
        BridgeAction evaluate = SingleAction(machine.Tick(Ms(110)), BridgeActionKind.EvaluatePreflight);
        machine.CompletePreflight(evaluate.Epoch, PreflightDecision.Ready, Ms(111));
        machine.AdapterStarted(evaluate.Epoch, Ms(112));

        HasAction(machine.HandleSignal(RemoteSignalKind.Pressed, Ms(500)), BridgeActionKind.StopAdapter);
        Equal(BridgeState.Stopping, machine.Snapshot.State);
        machine.AdapterStopped(evaluate.Epoch, Ms(501));
        Equal(BridgeState.Latched, machine.Snapshot.State);
        machine.HandleSignal(RemoteSignalKind.Neutral, Ms(502));
        machine.Tick(Ms(800));
        Equal(BridgeState.Idle, machine.Snapshot.State);
    }

    private static void VoiceCommandLateNeutralStopsSession()
    {
        BridgeStateMachine machine = SpeakingVoiceCommandMachine(out long epoch);
        NoActions(machine.HandleSignal(RemoteSignalKind.Neutral, Ms(150)));
        Equal(BridgeState.Speaking, machine.Snapshot.State);

        HasAction(machine.HandleSignal(RemoteSignalKind.Neutral, Ms(212)), BridgeActionKind.StopAdapter);
        Equal(BridgeState.Stopping, machine.Snapshot.State);
        machine.AdapterStopped(epoch, Ms(213));
        Equal(BridgeState.Latched, machine.Snapshot.State);
    }

    private static void VoiceCommandRepeatedReportStopsSession()
    {
        BridgeStateMachine machine = SpeakingVoiceCommandMachine(out _);
        HasAction(machine.HandleSignal(RemoteSignalKind.Repeated, Ms(212)), BridgeActionKind.StopAdapter);
        Equal(BridgeState.Stopping, machine.Snapshot.State);
    }

    private static void VoiceCommandFailedActivationReturnsToIdle()
    {
        BridgeStateMachine machine = VoiceCommandMachine();
        machine.HandleSignal(RemoteSignalKind.Neutral, Ms(0));
        machine.HandleSignal(RemoteSignalKind.Pressed, Ms(10));
        machine.HandleSignal(RemoteSignalKind.Neutral, Ms(11));
        BridgeAction evaluate = SingleAction(machine.Tick(Ms(110)), BridgeActionKind.EvaluatePreflight);
        HasAction(
            machine.CompletePreflight(evaluate.Epoch, PreflightDecision.NotReady("Microphone not active."), Ms(111)),
            BridgeActionKind.Notify);
        Equal(BridgeState.Latched, machine.Snapshot.State);
        machine.Tick(Ms(411));
        Equal(BridgeState.Idle, machine.Snapshot.State);
    }

    private static void AdapterStartMustBeConfirmedBeforeSpeaking()
    {
        BridgeStateMachine machine = VoiceCommandMachine();
        machine.HandleSignal(RemoteSignalKind.Neutral, Ms(0));
        machine.HandleSignal(RemoteSignalKind.Pressed, Ms(10));
        machine.HandleSignal(RemoteSignalKind.Neutral, Ms(11));
        BridgeAction evaluate = SingleAction(machine.Tick(Ms(110)), BridgeActionKind.EvaluatePreflight);
        HasAction(
            machine.CompletePreflight(evaluate.Epoch, PreflightDecision.Ready, Ms(111)),
            BridgeActionKind.StartAdapter);
        Equal(BridgeState.Starting, machine.Snapshot.State);
        True(machine.Snapshot.SpeakingSince is null, "Speaking timer started before voice UI confirmation.");

        IReadOnlyList<BridgeAction> secondPulse = machine.HandleSignal(RemoteSignalKind.Pressed, Ms(200));
        HasAction(secondPulse, BridgeActionKind.AbandonAdapterStart);
        False(
            secondPulse.Any(action => action.Kind is BridgeActionKind.StopAdapter or BridgeActionKind.EmergencyStopAdapter),
            "A pulse during Starting sent a stop chord.");
        Equal(BridgeState.Latched, machine.Snapshot.State);
    }

    private static void AdapterStartFailureAbandonsWithoutStop()
    {
        BridgeStateMachine machine = VoiceCommandMachine();
        machine.HandleSignal(RemoteSignalKind.Neutral, Ms(0));
        machine.HandleSignal(RemoteSignalKind.Pressed, Ms(10));
        machine.HandleSignal(RemoteSignalKind.Neutral, Ms(11));
        BridgeAction evaluate = SingleAction(machine.Tick(Ms(110)), BridgeActionKind.EvaluatePreflight);
        machine.CompletePreflight(evaluate.Epoch, PreflightDecision.Ready, Ms(111));

        IReadOnlyList<BridgeAction> failed = machine.AdapterStartFailed(
            evaluate.Epoch,
            "Voice UI not found.",
            Ms(200));
        HasAction(failed, BridgeActionKind.AbandonAdapterStart);
        False(
            failed.Any(action => action.Kind is BridgeActionKind.StopAdapter or BridgeActionKind.EmergencyStopAdapter),
            "Startup failure sent a Toggle stop chord.");
        Equal(BridgeState.Latched, machine.Snapshot.State);
        machine.Tick(Ms(500));
        Equal(BridgeState.Idle, machine.Snapshot.State);
    }

    private static void StaleAdapterConfirmationIsIgnored()
    {
        BridgeStateMachine machine = VoiceCommandMachine();
        machine.HandleSignal(RemoteSignalKind.Neutral, Ms(0));
        machine.HandleSignal(RemoteSignalKind.Pressed, Ms(10));
        machine.HandleSignal(RemoteSignalKind.Neutral, Ms(11));
        BridgeAction first = SingleAction(machine.Tick(Ms(110)), BridgeActionKind.EvaluatePreflight);
        machine.CompletePreflight(first.Epoch, PreflightDecision.Ready, Ms(111));
        machine.AdapterStartFailed(first.Epoch, "timeout", Ms(200));
        machine.Tick(Ms(500));

        machine.HandleSignal(RemoteSignalKind.Pressed, Ms(510));
        Equal(BridgeState.Candidate, machine.Snapshot.State);
        NoActions(machine.AdapterStarted(first.Epoch, Ms(511)));
        Equal(BridgeState.Candidate, machine.Snapshot.State);
    }

    private static void MaximumDurationLatchesUntilRelease()
    {
        BridgeStateMachine machine = SpeakingMachine(out long epoch, speechStartedAtMs: 110);
        HasAction(machine.Tick(Ms(1_110)), BridgeActionKind.StopAdapter);
        Equal(BridgeState.Stopping, machine.Snapshot.State);
        machine.AdapterStopped(epoch, Ms(1_111));
        Equal(BridgeState.Latched, machine.Snapshot.State);

        NoActions(machine.HandleSignal(RemoteSignalKind.Repeated, Ms(1_200)));
        NoActions(machine.HandleSignal(RemoteSignalKind.Pressed, Ms(1_201)));
        Equal(BridgeState.Latched, machine.Snapshot.State);

        machine.HandleSignal(RemoteSignalKind.Released, Ms(1_500));
        Equal(BridgeState.Idle, machine.Snapshot.State);
    }

    private static void RepeatedReportCannotStartFromIdleOrLatch()
    {
        BridgeStateMachine machine = ArmedMachine();
        NoActions(machine.HandleSignal(RemoteSignalKind.Repeated, Ms(10)));
        Equal(BridgeState.Idle, machine.Snapshot.State);

        machine.HandleSignal(RemoteSignalKind.Pressed, Ms(20));
        machine.EmergencyStop("test", Ms(30));
        Equal(BridgeState.Latched, machine.Snapshot.State);
        NoActions(machine.HandleSignal(RemoteSignalKind.Repeated, Ms(40)));
        NoActions(machine.Tick(Ms(500)));
        Equal(BridgeState.Latched, machine.Snapshot.State);
    }

    private static void FocusChangeAbortsCandidate()
    {
        BridgeStateMachine machine = ArmedMachine();
        machine.HandleSignal(RemoteSignalKind.Pressed, Ms(10));
        BridgeAction evaluate = SingleAction(machine.Tick(Ms(110)), BridgeActionKind.EvaluatePreflight);
        HasAction(
            machine.CompletePreflight(evaluate.Epoch, PreflightDecision.FocusChanged(), Ms(110)),
            BridgeActionKind.Notify);
        Equal(BridgeState.Latched, machine.Snapshot.State);
        NoActions(machine.HandleSignal(RemoteSignalKind.Pressed, Ms(120)));
    }

    private static void DeviceDisconnectEmergencyStopsAndUnarms()
    {
        BridgeStateMachine machine = SpeakingMachine(out long epoch, speechStartedAtMs: 110);
        HasAction(
            machine.HandleSignal(RemoteSignalKind.DeviceDisconnected, Ms(200)),
            BridgeActionKind.EmergencyStopAdapter);
        machine.AdapterStopped(epoch, Ms(201));
        Equal(BridgeState.Unarmed, machine.Snapshot.State);
        machine.HandleSignal(RemoteSignalKind.DeviceConnected, Ms(300));
        Equal(BridgeState.Unarmed, machine.Snapshot.State);
        machine.HandleSignal(RemoteSignalKind.Neutral, Ms(301));
        Equal(BridgeState.Idle, machine.Snapshot.State);
    }

    private static void StopTimeoutEmergencyReleases()
    {
        BridgeStateMachine machine = SpeakingMachine(out _, speechStartedAtMs: 110);
        machine.HandleSignal(RemoteSignalKind.Released, Ms(200));
        HasAction(machine.Tick(Ms(400)), BridgeActionKind.EmergencyStopAdapter);
        Equal(BridgeState.Latched, machine.Snapshot.State);
        machine.Tick(Ms(500));
        Equal(BridgeState.Idle, machine.Snapshot.State);
    }

    private static void StalePreflightResultIsIgnored()
    {
        BridgeStateMachine machine = ArmedMachine();
        machine.HandleSignal(RemoteSignalKind.Pressed, Ms(10));
        BridgeAction evaluate = SingleAction(machine.Tick(Ms(110)), BridgeActionKind.EvaluatePreflight);
        machine.HandleSignal(RemoteSignalKind.Released, Ms(111));
        NoActions(machine.CompletePreflight(evaluate.Epoch, PreflightDecision.Ready, Ms(112)));
        Equal(BridgeState.Idle, machine.Snapshot.State);
    }

    private static void NonMonotonicTimestampIsRejected()
    {
        BridgeStateMachine machine = ArmedMachine();
        machine.Tick(Ms(10));
        Throws<ArgumentOutOfRangeException>(() => machine.Tick(Ms(9)));
    }

    private static void AdapterProfileEnforcesGuardForHeldKeys()
    {
        AdapterProfile invalid = new()
        {
            Id = "invalid",
            DisplayName = "Invalid",
            TriggerModel = TriggerModel.PushToTalk,
            InjectionLifetime = InjectionLifetime.HeldAcrossSpeech,
            GuardPolicy = GuardPolicy.Optional,
            StartChord = [0x7C]
        };
        Contains(invalid.Validate(), "HeldAcrossSpeech requires GuardPolicy.Required.");

        AdapterProfile valid = invalid with { Id = "valid", DisplayName = "Valid", GuardPolicy = GuardPolicy.Required };
        Equal(0, valid.Validate().Count);
    }

    private static void AdapterProfileRejectsInconsistentTriggerModels()
    {
        AdapterProfile tapWithGuard = new()
        {
            Id = "tap",
            DisplayName = "Tap",
            TriggerModel = TriggerModel.TapOnHold,
            InjectionLifetime = InjectionLifetime.AtomicBatch,
            GuardPolicy = GuardPolicy.Required,
            StartChord = [0x7C]
        };
        Contains(tapWithGuard.Validate(), "The current guard protocol only supports HeldAcrossSpeech adapters.");

        AdapterProfile invalidPair = tapWithGuard with
        {
            Id = "pair",
            DisplayName = "Pair",
            TriggerModel = TriggerModel.StartStopPair,
            InjectionLifetime = InjectionLifetime.AtomicBatch,
            GuardPolicy = GuardPolicy.Optional,
            StopChord = [0x7D]
        };
        Contains(invalidPair.Validate(), "StartStopPair requires StartStopStateful injection lifetime.");
    }

    private static void VoiceCommandSettingsRejectHeldAdapter()
    {
        AdapterProfile held = new()
        {
            Id = "held",
            DisplayName = "Held chord",
            TriggerModel = TriggerModel.PushToTalk,
            InjectionLifetime = InjectionLifetime.HeldAcrossSpeech,
            GuardPolicy = GuardPolicy.Required,
            StartChord = [0x11, 0x5B]
        };
        AppSettings unsafeSettings = new()
        {
            InteractionMode = BridgeInteractionMode.VoiceCommandPressAgain,
            SelectedAdapter = held
        };
        True(
            unsafeSettings.Validate().Any(error => error.Contains("cannot use HeldAcrossSpeech", StringComparison.Ordinal)),
            "VoiceCommandPressAgain accepted an adapter that holds modifier keys across speech.");

        AdapterProfile toggle = held with
        {
            Id = "toggle",
            DisplayName = "Toggle chord",
            TriggerModel = TriggerModel.Toggle,
            InjectionLifetime = InjectionLifetime.AtomicBatch,
            GuardPolicy = GuardPolicy.Optional,
            StartChord = [0x11, 0x5B, 0x10]
        };
        AppSettings safeSettings = unsafeSettings with { SelectedAdapter = toggle };
        Equal(0, safeSettings.Validate().Count);
    }

    private static void InputMethodSwitchOptionsAreValidated()
    {
        InputMethodSwitchOptions defaults = new() { TargetProfile = "WeType" };
        Equal(1_000, defaults.PostActivationDelayMilliseconds);
        True(
            defaults.RefreshWhenAlreadyActive is null,
            "Unspecified TSF refresh must remain distinguishable for settings migration.");

        InputMethodSwitchOptions invalid = new()
        {
            TargetProfile = " ",
            ActivationTimeoutMilliseconds = 99,
            PostActivationDelayMilliseconds = 5_001,
            RestoreDelayMilliseconds = 5_001
        };
        Equal(4, invalid.Validate().Count);

        AdapterProfile mismatched = new()
        {
            Id = "ime-switch",
            DisplayName = "IME switch",
            TriggerModel = TriggerModel.Toggle,
            InjectionLifetime = InjectionLifetime.AtomicBatch,
            GuardPolicy = GuardPolicy.Optional,
            StartChord = [0x7C],
            RequiresActiveIme = "WeType",
            InputMethodSwitch = new InputMethodSwitchOptions
            {
                TargetProfile = "Different profile"
            }
        };
        Contains(
            mismatched.Validate(),
            "Required active IME and input-method switch target must identify the same profile.");

        AdapterProfile valid = mismatched with
        {
            InputMethodSwitch = new InputMethodSwitchOptions
            {
                TargetProfile = "WeType",
                ActivationTimeoutMilliseconds = 1_000,
                PostActivationDelayMilliseconds = 1_000,
                RestoreDelayMilliseconds = 500
            }
        };
        Equal(0, valid.Validate().Count);
    }

    private static void VoiceUiConfirmationOptionsAreValidated()
    {
        VoiceUiConfirmationOptions valid = new()
        {
            ProcessName = "wetype_update",
            WindowClass = "wetype.flutter.setting",
            TimeoutMilliseconds = 2_500
        };
        Equal(0, valid.Validate().Count);

        VoiceUiConfirmationOptions invalid = valid with
        {
            ProcessName = " ",
            WindowClass = "",
            TimeoutMilliseconds = 10_001
        };
        Equal(3, invalid.Validate().Count);
    }

    private static void DefaultMaximumSpeechIsFiveMinutes() =>
        Equal(300, new AppSettings().MaximumSpeechSeconds);

    private static void KeyChordCodecParsesAndFormatsSupportedKeys()
    {
        KeyChordParseResult result = KeyChordCodec.Parse("Ctrl + Win + F13");
        True(result.Succeeded, result.Error);
        Equal(3, result.Keys.Count);
        Equal((ushort)0x11, result.Keys[0]);
        Equal((ushort)0x5B, result.Keys[1]);
        Equal((ushort)0x7C, result.Keys[2]);
        Equal("Ctrl+Win+F13", KeyChordCodec.Format(result.Keys));

        KeyChordParseResult raw = KeyChordCodec.Parse("VK_2E+0x41");
        True(raw.Succeeded, raw.Error);
        Equal((ushort)0x2E, raw.Keys[0]);
        Equal((ushort)'A', raw.Keys[1]);
    }

    private static void KeyChordCodecRejectsUnknownAndDuplicateKeys()
    {
        False(KeyChordCodec.Parse("Ctrl+Ctrl").Succeeded, "Duplicate modifiers were accepted.");
        False(KeyChordCodec.Parse("VoiceMagic").Succeeded, "Unknown key name was accepted.");
        True(KeyChordCodec.Parse(string.Empty, allowEmpty: true).Succeeded, "Allowed empty chord was rejected.");
    }

    private static void HidSignalDecoderDistinguishesPressRepeatAndRelease()
    {
        HidButtonBinding binding = CreateBinding();
        HidSignalDecoder decoder = new(binding);
        Equal(RemoteSignalKind.Neutral, decoder.Decode(1, 128, [0x03, 0x00])!.Value);
        Equal(RemoteSignalKind.Pressed, decoder.Decode(1, 128, [0x03, 0x01])!.Value);
        Equal(RemoteSignalKind.Repeated, decoder.Decode(1, 128, [0x03, 0x01])!.Value);
        Equal(RemoteSignalKind.Released, decoder.Decode(1, 128, [0x03, 0x00])!.Value);
        True(decoder.Decode(12, 1, [0x03, 0x01]) is null, "Decoder accepted a different HID usage.");
    }

    private static void HidReportMaskIgnoresUnboundBits()
    {
        HidButtonBinding binding = CreateBinding() with
        {
            Pressed = new HidReportPattern { ValueHex = "0301", MaskHex = "FF01" },
            Released = new HidReportPattern { ValueHex = "0300", MaskHex = "FF01" }
        };
        Equal(0, binding.Validate().Count);
        HidSignalDecoder decoder = new(binding);
        Equal(RemoteSignalKind.Pressed, decoder.Decode(1, 128, [0x03, 0xF1])!.Value);
        Equal(RemoteSignalKind.Released, decoder.Decode(1, 128, [0x03, 0xA0])!.Value);
    }

    private static void OneThousandDistinctMutatedSequencesPreserveInvariants()
    {
        const int sequenceCount = 1_000;
        HashSet<string> signatures = new(StringComparer.Ordinal);
        for (int sequenceIndex = 0; sequenceIndex < sequenceCount; sequenceIndex++)
        {
            Random random = new(0x5EED + sequenceIndex * 7_919);
            BridgeStateMachine machine = new(new BridgeTiming(Ms(5), Ms(50), Ms(3), Ms(7)));
            bool injectedKeyHeld = false;
            long now = 0;
            List<int> signature = [];
            Execute(machine.HandleSignal(RemoteSignalKind.Neutral, Ms(now)), ref injectedKeyHeld);

            int steps = random.Next(30, 81);
            for (int step = 0; step < steps; step++)
            {
                now += random.Next(0, 11);
                int operation = random.Next(0, 10);
                signature.Add(operation * 10_000 + (int)now);
                IReadOnlyList<BridgeAction> actions = operation switch
                {
                    0 => machine.HandleSignal(RemoteSignalKind.Pressed, Ms(now)),
                    1 => machine.HandleSignal(RemoteSignalKind.Repeated, Ms(now)),
                    2 => machine.HandleSignal(RemoteSignalKind.Released, Ms(now)),
                    3 => machine.HandleSignal(RemoteSignalKind.Neutral, Ms(now)),
                    4 => machine.HandleSignal(RemoteSignalKind.DeviceDisconnected, Ms(now)),
                    5 => machine.HandleSignal(RemoteSignalKind.DeviceConnected, Ms(now)),
                    6 => machine.Tick(Ms(now)),
                    7 => machine.EmergencyStop("mutated emergency", Ms(now)),
                    8 => machine.AdapterStopped(machine.Snapshot.Epoch, Ms(now)),
                    _ => machine.Tick(Ms(now))
                };

                BridgeAction? preflight = actions.FirstOrDefault(action => action.Kind == BridgeActionKind.EvaluatePreflight);
                Execute(actions, ref injectedKeyHeld);
                if (preflight is not null)
                {
                    PreflightDecision decision = random.Next(0, 4) switch
                    {
                        0 => PreflightDecision.FocusChanged(),
                        1 => PreflightDecision.NotReady("mutated not ready"),
                        _ => PreflightDecision.Ready
                    };
                    Execute(machine.CompletePreflight(preflight.Epoch, decision, Ms(now)), ref injectedKeyHeld);
                    if (machine.Snapshot.State == BridgeState.Starting && random.Next(0, 2) == 0)
                    {
                        Execute(machine.AdapterStarted(preflight.Epoch, Ms(now)), ref injectedKeyHeld);
                    }
                }
            }

            now += 1;
            Execute(machine.EmergencyStop("property cleanup", Ms(now)), ref injectedKeyHeld);
            if (machine.Snapshot.State == BridgeState.Stopping)
            {
                now += 1;
                Execute(machine.AdapterStopped(machine.Snapshot.Epoch, Ms(now)), ref injectedKeyHeld);
            }

            now += 1;
            Execute(machine.HandleSignal(RemoteSignalKind.Neutral, Ms(now)), ref injectedKeyHeld);
            now += 20;
            Execute(machine.Tick(Ms(now)), ref injectedKeyHeld);

            False(injectedKeyHeld, $"Sequence {sequenceIndex} left an injected key held.");
            Equal(BridgeState.Idle, machine.Snapshot.State, $"Sequence {sequenceIndex} did not return to Idle.");
            True(signatures.Add(string.Join(',', signature)), $"Sequence {sequenceIndex} duplicated an earlier mutation.");
        }

        Equal(sequenceCount, signatures.Count);
    }

    private static BridgeStateMachine ArmedMachine()
    {
        BridgeStateMachine machine = CreateMachine();
        machine.HandleSignal(RemoteSignalKind.Neutral, Ms(0));
        return machine;
    }

    private static BridgeStateMachine SpeakingMachine(out long epoch, long speechStartedAtMs)
    {
        BridgeStateMachine machine = ArmedMachine();
        machine.HandleSignal(RemoteSignalKind.Pressed, Ms(10));
        BridgeAction evaluate = SingleAction(machine.Tick(Ms(110)), BridgeActionKind.EvaluatePreflight);
        machine.CompletePreflight(evaluate.Epoch, PreflightDecision.Ready, Ms(speechStartedAtMs));
        machine.AdapterStarted(evaluate.Epoch, Ms(speechStartedAtMs));
        epoch = evaluate.Epoch;
        return machine;
    }

    private static BridgeStateMachine CreateMachine() => new(
        new BridgeTiming(Ms(100), Ms(1_000), Ms(300), Ms(200)));

    private static BridgeStateMachine VoiceCommandMachine() => new(
        new BridgeTiming(Ms(100), Ms(1_000), Ms(300), Ms(200)),
        BridgeInteractionMode.VoiceCommandPressAgain);

    private static BridgeStateMachine SpeakingVoiceCommandMachine(out long epoch)
    {
        BridgeStateMachine machine = VoiceCommandMachine();
        machine.HandleSignal(RemoteSignalKind.Neutral, Ms(0));
        machine.HandleSignal(RemoteSignalKind.Pressed, Ms(10));
        machine.HandleSignal(RemoteSignalKind.Neutral, Ms(11));
        BridgeAction evaluate = SingleAction(machine.Tick(Ms(110)), BridgeActionKind.EvaluatePreflight);
        machine.CompletePreflight(evaluate.Epoch, PreflightDecision.Ready, Ms(111));
        machine.AdapterStarted(evaluate.Epoch, Ms(111));
        epoch = evaluate.Epoch;
        return machine;
    }

    private static HidButtonBinding CreateBinding() => new()
    {
        UsagePage = 1,
        Usage = 128,
        Pressed = new HidReportPattern { ValueHex = "0301", MaskHex = "FFFF" },
        Released = new HidReportPattern { ValueHex = "0300", MaskHex = "FFFF" }
    };

    private static void Execute(IReadOnlyList<BridgeAction> actions, ref bool injectedKeyHeld)
    {
        foreach (BridgeAction action in actions)
        {
            switch (action.Kind)
            {
                case BridgeActionKind.StartAdapter:
                    False(injectedKeyHeld, "StartAdapter was emitted while a key was already held.");
                    injectedKeyHeld = true;
                    break;
                case BridgeActionKind.StopAdapter:
                case BridgeActionKind.EmergencyStopAdapter:
                case BridgeActionKind.AbandonAdapterStart:
                    injectedKeyHeld = false;
                    break;
            }
        }
    }

    private static TimeSpan Ms(long value) => TimeSpan.FromMilliseconds(value);

    private static BridgeAction SingleAction(IReadOnlyList<BridgeAction> actions, BridgeActionKind kind)
    {
        Equal(1, actions.Count, $"Expected one {kind} action.");
        Equal(kind, actions[0].Kind);
        return actions[0];
    }

    private static void HasAction(IReadOnlyList<BridgeAction> actions, BridgeActionKind kind) =>
        True(actions.Any(action => action.Kind == kind), $"Expected action {kind}.");

    private static void NoActions(IReadOnlyList<BridgeAction> actions) =>
        Equal(0, actions.Count, "Expected no actions.");

    private static void Contains(IReadOnlyList<string> values, string expected) =>
        True(values.Contains(expected, StringComparer.Ordinal), $"Expected collection to contain '{expected}'.");

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

    private static void Equal<T>(T expected, T actual, string? message = null)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(message ?? $"Expected '{expected}', actual '{actual}'.");
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
