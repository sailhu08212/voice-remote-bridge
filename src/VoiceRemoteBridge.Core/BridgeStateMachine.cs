namespace VoiceRemoteBridge.Core;

public sealed class BridgeStateMachine
{
    private readonly BridgeTiming timing;
    private readonly BridgeInteractionMode interactionMode;
    private BridgeState state = BridgeState.Unarmed;
    private bool physicalNeutral;
    private bool thresholdEvaluationRequested;
    private long epoch;
    private TimeSpan lastTimestamp;
    private TimeSpan? candidateSince;
    private TimeSpan? startingSince;
    private TimeSpan? speakingSince;
    private TimeSpan? stoppingSince;
    private TimeSpan cooldownUntil;
    private RearmTarget rearmTarget = RearmTarget.Latched;
    private string lastReason = "Waiting for a neutral device report.";

    public BridgeStateMachine(
        BridgeTiming timing,
        BridgeInteractionMode interactionMode = BridgeInteractionMode.PhysicalDownUp)
    {
        this.timing = timing ?? throw new ArgumentNullException(nameof(timing));
        this.interactionMode = interactionMode;
    }

    public BridgeSnapshot Snapshot => new(
        state,
        epoch,
        physicalNeutral,
        lastTimestamp,
        candidateSince,
        startingSince,
        speakingSince,
        stoppingSince,
        cooldownUntil,
        lastReason);

    public IReadOnlyList<BridgeAction> HandleSignal(RemoteSignalKind signal, TimeSpan now)
    {
        EnsureMonotonic(now);
        List<BridgeAction> actions = [];

        switch (signal)
        {
            case RemoteSignalKind.DeviceConnected:
                if (state is BridgeState.Unarmed or BridgeState.Latched)
                {
                    lastReason = "Device connected; waiting for neutral state.";
                }

                break;

            case RemoteSignalKind.DeviceDisconnected:
                physicalNeutral = false;
                HandleDisconnect(now, actions);
                break;

            case RemoteSignalKind.Neutral:
            case RemoteSignalKind.Released:
                physicalNeutral = true;
                HandleNeutral(now, actions);
                break;

            case RemoteSignalKind.Pressed:
                physicalNeutral = false;
                HandlePressed(now, actions);
                break;

            case RemoteSignalKind.Repeated:
                physicalNeutral = false;
                HandleRepeated(now, actions);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(signal), signal, "Unknown remote signal.");
        }

        return actions;
    }

    public IReadOnlyList<BridgeAction> Tick(TimeSpan now)
    {
        EnsureMonotonic(now);
        List<BridgeAction> actions = [];

        if (state == BridgeState.Candidate &&
            candidateSince is { } candidateStart &&
            now - candidateStart >= timing.HoldThreshold &&
            !thresholdEvaluationRequested)
        {
            thresholdEvaluationRequested = true;
            actions.Add(new BridgeAction(BridgeActionKind.EvaluatePreflight, epoch, "Hold threshold reached."));
        }
        else if (state == BridgeState.Speaking &&
                 speakingSince is { } speechStart &&
                 now - speechStart >= timing.MaximumSpeechDuration)
        {
            BeginStopping(
                now,
                RearmTarget.Latched,
                emergency: false,
                "已达到本软件设置的单次最长听写安全时限，正在停止并提交。",
                actions);
        }
        else if (state == BridgeState.Stopping &&
                 stoppingSince is { } stopStart &&
                 now - stopStart >= timing.StopTimeout)
        {
            actions.Add(new BridgeAction(BridgeActionKind.EmergencyStopAdapter, epoch, "Adapter stop timed out."));
            TransitionAfterStop(now, "Adapter stop timed out; emergency release requested.");
        }
        else if (state == BridgeState.Latched && physicalNeutral && now >= cooldownUntil)
        {
            EnterIdle("Neutral state confirmed after latch.");
        }

        return actions;
    }

    public IReadOnlyList<BridgeAction> CompletePreflight(long evaluatedEpoch, PreflightDecision decision, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(decision);
        EnsureMonotonic(now);
        if (state != BridgeState.Candidate || evaluatedEpoch != epoch || !thresholdEvaluationRequested)
        {
            return [];
        }

        if (!decision.IsReady || !decision.FocusUnchanged)
        {
            state = BridgeState.Latched;
            cooldownUntil = now + timing.RetriggerCooldown;
            candidateSince = null;
            thresholdEvaluationRequested = false;
            lastReason = string.IsNullOrWhiteSpace(decision.FailureReason)
                ? "Candidate preflight failed."
                : decision.FailureReason;
            return [new BridgeAction(BridgeActionKind.Notify, epoch, lastReason)];
        }

        state = BridgeState.Starting;
        startingSince = now;
        candidateSince = null;
        thresholdEvaluationRequested = false;
        lastReason = "Adapter start requested.";
        return [new BridgeAction(BridgeActionKind.StartAdapter, epoch, lastReason)];
    }

    public IReadOnlyList<BridgeAction> AdapterStarted(long startedEpoch, TimeSpan now)
    {
        EnsureMonotonic(now);
        if (state != BridgeState.Starting || startedEpoch != epoch)
        {
            return [];
        }

        state = BridgeState.Speaking;
        startingSince = null;
        speakingSince = now;
        lastReason = "Adapter start confirmed.";
        return [];
    }

    public IReadOnlyList<BridgeAction> AdapterStartFailed(long failedEpoch, string reason, TimeSpan now)
    {
        EnsureMonotonic(now);
        if (state != BridgeState.Starting || failedEpoch != epoch)
        {
            return [];
        }

        string failureReason = string.IsNullOrWhiteSpace(reason)
            ? "Adapter start was not confirmed."
            : reason;
        List<BridgeAction> actions = [];
        AbortStarting(now, failureReason, RearmTarget.Idle, actions);
        return actions;
    }

    public IReadOnlyList<BridgeAction> AdapterStopped(long stoppedEpoch, TimeSpan now)
    {
        EnsureMonotonic(now);
        if (state != BridgeState.Stopping || stoppedEpoch != epoch)
        {
            return [];
        }

        TransitionAfterStop(now, "Adapter confirmed stopped.");
        return [];
    }

    public IReadOnlyList<BridgeAction> EmergencyStop(string reason, TimeSpan now)
    {
        EnsureMonotonic(now);
        List<BridgeAction> actions = [];
        switch (state)
        {
            case BridgeState.Starting:
                AbortStarting(now, reason, RearmTarget.Latched, actions);
                break;
            case BridgeState.Speaking:
                BeginStopping(now, RearmTarget.Latched, emergency: true, reason, actions);
                break;
            case BridgeState.Stopping:
                rearmTarget = RearmTarget.Latched;
                actions.Add(new BridgeAction(BridgeActionKind.EmergencyStopAdapter, epoch, reason));
                lastReason = reason;
                break;
            case BridgeState.Candidate:
                state = BridgeState.Latched;
                candidateSince = null;
                thresholdEvaluationRequested = false;
                cooldownUntil = now + timing.RetriggerCooldown;
                lastReason = reason;
                actions.Add(new BridgeAction(BridgeActionKind.Notify, epoch, reason));
                break;
            case BridgeState.Idle:
                state = physicalNeutral ? BridgeState.Idle : BridgeState.Latched;
                cooldownUntil = now + timing.RetriggerCooldown;
                lastReason = reason;
                break;
            default:
                lastReason = reason;
                break;
        }

        return actions;
    }

    public void Fault(string reason, TimeSpan now)
    {
        EnsureMonotonic(now);
        state = BridgeState.Faulted;
        candidateSince = null;
        startingSince = null;
        speakingSince = null;
        stoppingSince = null;
        thresholdEvaluationRequested = false;
        lastReason = reason;
    }

    public void ResetFromFault(TimeSpan now)
    {
        EnsureMonotonic(now);
        state = BridgeState.Unarmed;
        physicalNeutral = false;
        lastReason = "Fault reset; waiting for neutral state.";
    }

    private void HandlePressed(TimeSpan now, ICollection<BridgeAction> actions)
    {
        if (interactionMode == BridgeInteractionMode.VoiceCommandPressAgain)
        {
            if (state == BridgeState.Speaking)
            {
                BeginStopping(
                    now,
                    RearmTarget.Idle,
                    emergency: false,
                    "Second Voice Command pulse received; submitting the active voice session.",
                    actions);
                return;
            }

            if (state == BridgeState.Starting)
            {
                AbortStarting(
                    now,
                    "A second Voice Command pulse arrived before the voice UI was confirmed; start abandoned.",
                    RearmTarget.Idle,
                    actions);
                return;
            }

            if (state == BridgeState.Candidate)
            {
                state = BridgeState.Latched;
                candidateSince = null;
                thresholdEvaluationRequested = false;
                cooldownUntil = now + timing.RetriggerCooldown;
                lastReason = "A second Voice Command pulse arrived before microphone activation; candidate cancelled.";
                actions.Add(new BridgeAction(BridgeActionKind.Notify, epoch, lastReason));
                return;
            }
        }

        if (state != BridgeState.Idle)
        {
            return;
        }

        if (now < cooldownUntil)
        {
            state = BridgeState.Latched;
            lastReason = "Press ignored during retrigger cooldown; waiting for release.";
            return;
        }

        epoch++;
        state = BridgeState.Candidate;
        candidateSince = now;
        thresholdEvaluationRequested = false;
        lastReason = "Candidate started; focus snapshot requested.";
        actions.Add(new BridgeAction(BridgeActionKind.CaptureFocus, epoch, lastReason));
    }

    private void HandleNeutral(TimeSpan now, ICollection<BridgeAction> actions)
    {
        switch (state)
        {
            case BridgeState.Unarmed:
                EnterIdle("Initial neutral state confirmed.");
                break;
            case BridgeState.Candidate:
                if (interactionMode == BridgeInteractionMode.VoiceCommandPressAgain)
                {
                    lastReason = "One-shot neutral received; waiting for microphone activation.";
                }
                else
                {
                    EnterIdle("Released before hold threshold.");
                }
                break;
            case BridgeState.Speaking:
                if (interactionMode == BridgeInteractionMode.VoiceCommandPressAgain)
                {
                    if (speakingSince is { } speechStart && now - speechStart >= timing.HoldThreshold)
                    {
                        BeginStopping(
                            now,
                            RearmTarget.Idle,
                            emergency: false,
                            "Fail-safe neutral received after speech started; submitting the active voice session.",
                            actions);
                    }
                    else
                    {
                        lastReason = "Early one-shot neutral ignored during the speech-start debounce window.";
                    }
                }
                else
                {
                    BeginStopping(now, RearmTarget.Idle, emergency: false, "Physical release received.", actions);
                }
                break;
            case BridgeState.Starting:
                if (interactionMode == BridgeInteractionMode.VoiceCommandPressAgain)
                {
                    lastReason = "One-shot neutral received while the voice UI is being confirmed.";
                }
                else
                {
                    AbortStarting(
                        now,
                        "Physical release received before the voice UI was confirmed; start abandoned.",
                        RearmTarget.Idle,
                        actions);
                }

                break;
            case BridgeState.Latched when now >= cooldownUntil:
                EnterIdle("Release received after latch.");
                break;
            case BridgeState.Latched:
                lastReason = "Release received; waiting for cooldown.";
                break;
            case BridgeState.Stopping:
                lastReason = "Release recorded while adapter is stopping.";
                break;
        }
    }

    private void HandleRepeated(TimeSpan now, ICollection<BridgeAction> actions)
    {
        if (interactionMode == BridgeInteractionMode.VoiceCommandPressAgain && state == BridgeState.Starting)
        {
            AbortStarting(
                now,
                "Repeated Voice Command report arrived before the voice UI was confirmed; start abandoned.",
                RearmTarget.Latched,
                actions);
            return;
        }

        if (interactionMode == BridgeInteractionMode.VoiceCommandPressAgain &&
            state == BridgeState.Speaking &&
            speakingSince is { } speechStart &&
            now - speechStart >= timing.HoldThreshold)
        {
            BeginStopping(
                now,
                RearmTarget.Latched,
                emergency: false,
                "Repeated Voice Command report received after speech started; submitting as a fail-safe.",
                actions);
            return;
        }

        if (state == BridgeState.Latched)
        {
            lastReason = "Repeated report ignored while latched.";
        }
    }

    private void HandleDisconnect(TimeSpan now, ICollection<BridgeAction> actions)
    {
        switch (state)
        {
            case BridgeState.Starting:
                AbortStarting(now, "Remote receiver disconnected during adapter startup.", RearmTarget.Unarmed, actions);
                break;
            case BridgeState.Speaking:
                BeginStopping(now, RearmTarget.Unarmed, emergency: true, "Remote receiver disconnected.", actions);
                break;
            case BridgeState.Stopping:
                rearmTarget = RearmTarget.Unarmed;
                actions.Add(new BridgeAction(BridgeActionKind.EmergencyStopAdapter, epoch, "Remote receiver disconnected while stopping."));
                lastReason = "Remote receiver disconnected while stopping.";
                break;
            case BridgeState.Candidate:
            case BridgeState.Idle:
            case BridgeState.Latched:
                state = BridgeState.Unarmed;
                candidateSince = null;
                thresholdEvaluationRequested = false;
                cooldownUntil = now + timing.RetriggerCooldown;
                lastReason = "Remote receiver disconnected; waiting for reconnect and neutral state.";
                break;
        }
    }

    private void BeginStopping(
        TimeSpan now,
        RearmTarget target,
        bool emergency,
        string reason,
        ICollection<BridgeAction> actions)
    {
        state = BridgeState.Stopping;
        rearmTarget = target;
        stoppingSince = now;
        speakingSince = null;
        cooldownUntil = now + timing.RetriggerCooldown;
        lastReason = reason;
        actions.Add(new BridgeAction(
            emergency ? BridgeActionKind.EmergencyStopAdapter : BridgeActionKind.StopAdapter,
            epoch,
            reason));
    }

    private void AbortStarting(
        TimeSpan now,
        string reason,
        RearmTarget target,
        ICollection<BridgeAction> actions)
    {
        startingSince = null;
        speakingSince = null;
        candidateSince = null;
        thresholdEvaluationRequested = false;
        cooldownUntil = now + timing.RetriggerCooldown;
        rearmTarget = target;
        lastReason = reason;

        if (target == RearmTarget.Unarmed)
        {
            state = BridgeState.Unarmed;
            physicalNeutral = false;
        }
        else
        {
            state = BridgeState.Latched;
        }

        actions.Add(new BridgeAction(BridgeActionKind.AbandonAdapterStart, epoch, reason));
    }

    private void TransitionAfterStop(TimeSpan now, string reason)
    {
        stoppingSince = null;
        speakingSince = null;
        lastReason = reason;

        if (rearmTarget == RearmTarget.Unarmed)
        {
            state = BridgeState.Unarmed;
            physicalNeutral = false;
            return;
        }

        if (rearmTarget == RearmTarget.Idle && physicalNeutral && now >= cooldownUntil)
        {
            EnterIdle(reason);
            return;
        }

        state = BridgeState.Latched;
    }

    private void EnterIdle(string reason)
    {
        state = BridgeState.Idle;
        physicalNeutral = true;
        candidateSince = null;
        startingSince = null;
        speakingSince = null;
        stoppingSince = null;
        thresholdEvaluationRequested = false;
        lastReason = reason;
    }

    private void EnsureMonotonic(TimeSpan now)
    {
        if (now < lastTimestamp)
        {
            throw new ArgumentOutOfRangeException(nameof(now), "Timestamps must be monotonic.");
        }

        lastTimestamp = now;
    }

    private enum RearmTarget
    {
        Idle,
        Latched,
        Unarmed
    }
}
