namespace VoiceRemoteBridge.Core;

public sealed record AdapterProfile
{
    public int SchemaVersion { get; init; } = 1;

    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required TriggerModel TriggerModel { get; init; }

    public required InjectionLifetime InjectionLifetime { get; init; }

    public required GuardPolicy GuardPolicy { get; init; }

    public required IReadOnlyList<ushort> StartChord { get; init; }

    public IReadOnlyList<ushort> StopChord { get; init; } = [];

    public IReadOnlyList<ushort> ForbiddenModifiers { get; init; } = [];

    public IReadOnlyList<string> RequiredProcesses { get; init; } = [];

    public IReadOnlyList<string> ConflictingListeners { get; init; } = [];

    public string? RequiresActiveIme { get; init; }

    public InputMethodSwitchOptions? InputMethodSwitch { get; init; }

    public VoiceUiConfirmationOptions? VoiceUiConfirmation { get; init; }

    public string InjectionMode { get; init; } = "VirtualKey";

    public IReadOnlyList<string> Validate()
    {
        List<string> errors = [];
        if (SchemaVersion != 1)
        {
            errors.Add($"Unsupported schema version: {SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(Id))
        {
            errors.Add("Adapter id is required.");
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            errors.Add("Display name is required.");
        }

        if (StartChord.Count == 0)
        {
            errors.Add("Start chord must contain at least one key.");
        }

        if (StartChord.Count != StartChord.Distinct().Count())
        {
            errors.Add("Start chord contains duplicate keys.");
        }

        if (StopChord.Count != StopChord.Distinct().Count())
        {
            errors.Add("Stop chord contains duplicate keys.");
        }

        if (TriggerModel == TriggerModel.PushToTalk && InjectionLifetime != InjectionLifetime.HeldAcrossSpeech)
        {
            errors.Add("PushToTalk requires HeldAcrossSpeech injection lifetime.");
        }

        if (TriggerModel == TriggerModel.TapOnHold && InjectionLifetime != InjectionLifetime.AtomicBatch)
        {
            errors.Add("TapOnHold requires AtomicBatch injection lifetime.");
        }

        if (TriggerModel == TriggerModel.StartStopPair && InjectionLifetime != InjectionLifetime.StartStopStateful)
        {
            errors.Add("StartStopPair requires StartStopStateful injection lifetime.");
        }

        if (InjectionLifetime == InjectionLifetime.HeldAcrossSpeech && GuardPolicy != GuardPolicy.Required)
        {
            errors.Add("HeldAcrossSpeech requires GuardPolicy.Required.");
        }

        if (GuardPolicy == GuardPolicy.Required && InjectionLifetime != InjectionLifetime.HeldAcrossSpeech)
        {
            errors.Add("The current guard protocol only supports HeldAcrossSpeech adapters.");
        }

        if (TriggerModel == TriggerModel.StartStopPair && StopChord.Count == 0)
        {
            errors.Add("StartStopPair requires a stop chord.");
        }

        if (TriggerModel != TriggerModel.StartStopPair && StopChord.Count > 0)
        {
            errors.Add("A stop chord is only valid for StartStopPair.");
        }

        if (InjectionMode is not ("VirtualKey" or "ScanCode"))
        {
            errors.Add("Injection mode must be VirtualKey or ScanCode.");
        }

        if (InputMethodSwitch is not null)
        {
            errors.AddRange(InputMethodSwitch.Validate());
            if (!string.IsNullOrWhiteSpace(RequiresActiveIme) &&
                !string.Equals(
                    RequiresActiveIme.Trim(),
                    InputMethodSwitch.TargetProfile.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Required active IME and input-method switch target must identify the same profile.");
            }
        }

        if (VoiceUiConfirmation is not null)
        {
            errors.AddRange(VoiceUiConfirmation.Validate());
        }

        return errors;
    }
}
