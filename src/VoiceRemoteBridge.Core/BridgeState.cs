namespace VoiceRemoteBridge.Core;

public enum BridgeState
{
    Unarmed,
    Idle,
    Candidate,
    Starting,
    Speaking,
    Stopping,
    Latched,
    Faulted
}

public enum RemoteSignalKind
{
    Pressed,
    Repeated,
    Released,
    Neutral,
    DeviceConnected,
    DeviceDisconnected
}

public enum BridgeInteractionMode
{
    PhysicalDownUp,
    VoiceCommandPressAgain
}

public enum TriggerModel
{
    PushToTalk,
    Toggle,
    TapOnHold,
    StartStopPair
}

public enum InjectionLifetime
{
    HeldAcrossSpeech,
    AtomicBatch,
    StartStopStateful
}

public enum GuardPolicy
{
    Required,
    Optional,
    Unsupported
}

