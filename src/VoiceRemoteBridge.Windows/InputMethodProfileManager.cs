using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using VoiceRemoteBridge.Core;

namespace VoiceRemoteBridge.Windows;

public sealed record InputMethodProfileDescriptor(
    uint ProfileType,
    ushort LanguageId,
    Guid ClassId,
    Guid ProfileId,
    nint KeyboardLayout,
    string Description,
    uint Flags = 0)
{
    public bool IsSameProfile(InputMethodProfileDescriptor other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ProfileType != other.ProfileType || LanguageId != other.LanguageId)
        {
            return false;
        }

        return ProfileType == InputMethodProfileManager.KeyboardLayoutProfileType
            ? KeyboardLayout == other.KeyboardLayout
            : ClassId == other.ClassId && ProfileId == other.ProfileId;
    }

    public string Describe()
    {
        if (!string.IsNullOrWhiteSpace(Description))
        {
            return Description;
        }

        return ProfileType == InputMethodProfileManager.KeyboardLayoutProfileType
            ? $"HKL 0x{KeyboardLayout.ToInt64():X}"
            : $"{ClassId:B}/{ProfileId:B}";
    }
}

public interface IInputMethodProfileManager
{
    InputMethodProfileDescriptor CaptureActiveProfile();

    InputMethodProfileDescriptor? FindInstalledProfile(string targetProfile);

    InputMethodProfileDescriptor? FindEnabledFallbackProfile(InputMethodProfileDescriptor targetProfile);

    void ActivateProfile(InputMethodProfileDescriptor profile, bool enableIfNeeded);
}

public sealed class InputMethodProfileManager : IInputMethodProfileManager, IDisposable
{
    internal const uint InputProcessorProfileType = 0x0001;
    internal const uint KeyboardLayoutProfileType = 0x0002;
    private const uint ActivateForProcess = 0x10000000;
    private const uint ActivateForSession = 0x20000000;
    private const uint EnableProfile = 0x00000001;
    private const uint IgnoreCurrentInputLanguage = 0x00000004;
    private const uint ProfileEnabled = 0x00000002;
    private const int RpcChangedMode = unchecked((int)0x80010106);
    private const string TipRegistryPath = @"SOFTWARE\Microsoft\CTF\TIP";
    private static readonly Guid KeyboardCategory = new("34745C63-B2F0-4784-8B67-5E12C8701A31");
    private readonly BlockingCollection<Action> staWork = new();
    private readonly ManualResetEventSlim staReady = new();
    private readonly Thread staThread;
    private Exception? staInitializationError;
    private bool disposed;

    public InputMethodProfileManager()
    {
        staThread = new Thread(RunStaWork)
        {
            IsBackground = true,
            Name = "VoiceRemoteBridge.TsfProfile"
        };
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staReady.Wait();
        if (staInitializationError is not null)
        {
            disposed = true;
            staWork.CompleteAdding();
            staThread.Join();
            staReady.Dispose();
            staWork.Dispose();
            throw new InvalidOperationException(
                "无法初始化 Windows TSF 工作线程。",
                staInitializationError);
        }
    }

    public InputMethodProfileDescriptor CaptureActiveProfile() => InvokeOnSta(CaptureActiveProfileCore);

    public InputMethodProfileDescriptor? FindInstalledProfile(string targetProfile)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetProfile);
        string target = targetProfile.Trim();
        List<InputMethodProfileDescriptor> exactMatches = [];
        List<InputMethodProfileDescriptor> partialMatches = [];

        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey? tipKey = baseKey.OpenSubKey(TipRegistryPath, writable: false);
            if (tipKey is null)
            {
                continue;
            }

            foreach (InputMethodProfileDescriptor candidate in EnumerateTipProfiles(tipKey))
            {
                if (IsExactMatch(candidate, target))
                {
                    AddDistinct(exactMatches, candidate);
                }
                else if (candidate.Description.Contains(target, StringComparison.OrdinalIgnoreCase))
                {
                    AddDistinct(partialMatches, candidate);
                }
            }
        }

        IReadOnlyList<InputMethodProfileDescriptor> matches = exactMatches.Count > 0
            ? exactMatches
            : partialMatches;
        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"输入法标识“{target}”匹配到多个已安装 Profile，请改用精确 Profile GUID。")
        };
    }

    public void ActivateProfile(InputMethodProfileDescriptor profile, bool enableIfNeeded)
    {
        ArgumentNullException.ThrowIfNull(profile);
        InvokeOnSta(() =>
        {
            ActivateProfileCore(profile, enableIfNeeded);
            return true;
        });
    }

    public InputMethodProfileDescriptor? FindEnabledFallbackProfile(
        InputMethodProfileDescriptor targetProfile)
    {
        ArgumentNullException.ThrowIfNull(targetProfile);
        return InvokeOnSta(() => EnumerateInstalledProfilesCore(0)
            .Where(profile =>
                !profile.IsSameProfile(targetProfile) &&
                (profile.Flags & ProfileEnabled) != 0)
            .OrderBy(profile => profile.LanguageId == targetProfile.LanguageId ? 0 : 1)
            .ThenBy(profile => profile.ProfileType == KeyboardLayoutProfileType ? 0 : 1)
            .FirstOrDefault());
    }

    public IReadOnlyList<InputMethodProfileDescriptor> EnumerateInstalledProfiles(ushort languageId = 0) =>
        InvokeOnSta(() => EnumerateInstalledProfilesCore(languageId));

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        staWork.CompleteAdding();
        if (Thread.CurrentThread != staThread)
        {
            staThread.Join();
        }

        staWork.Dispose();
        staReady.Dispose();
    }

    private static InputMethodProfileDescriptor CaptureActiveProfileCore()
    {
        ExecuteWithProfileManager(manager =>
        {
            Guid category = KeyboardCategory;
            Marshal.ThrowExceptionForHR(manager.GetActiveProfile(ref category, out TfInputProcessorProfile profile));
            return ToDescriptor(profile, string.Empty);
        }, out InputMethodProfileDescriptor? result);
        return result!;
    }

    private static void ActivateProfileCore(
        InputMethodProfileDescriptor profile,
        bool enableIfNeeded)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.ProfileType == InputProcessorProfileType && enableIfNeeded)
        {
            EnsureLegacyLanguageProfileEnabled(profile);
        }

        ExecuteWithProfileManager(manager =>
        {
            Guid classId = profile.ClassId;
            Guid profileId = profile.ProfileId;
            if (profile.ProfileType == InputProcessorProfileType && !enableIfNeeded)
            {
                int lookupResult = manager.GetProfile(
                    profile.ProfileType,
                    profile.LanguageId,
                    ref classId,
                    ref profileId,
                    profile.KeyboardLayout,
                    out _);
                if (lookupResult < 0)
                {
                    ActivateLegacyLanguageProfile(profile);
                    return true;
                }
            }

            uint flags = ActivateForProcess | ActivateForSession | IgnoreCurrentInputLanguage;
            if (enableIfNeeded && profile.ProfileType == InputProcessorProfileType)
            {
                flags |= EnableProfile;
            }

            int result = manager.ActivateProfile(
                profile.ProfileType,
                profile.LanguageId,
                ref classId,
                ref profileId,
                profile.KeyboardLayout,
                flags);
            if (result < 0)
            {
                string operation = enableIfNeeded
                    ? "TSF ActivateProfile with enablement failed."
                    : "TSF ActivateProfile failed.";
                throw new COMException(operation, result);
            }

            if (result == 1)
            {
                throw new InvalidOperationException("TSF reports that the requested input profile is not enabled.");
            }

            return true;
        }, out bool _);
    }

    private static void ActivateLegacyLanguageProfile(InputMethodProfileDescriptor profile)
    {
        IInputProcessorProfiles? profiles = null;
        try
        {
            profiles = (IInputProcessorProfiles)(object)new InputProcessorProfilesComObject();
            Guid classId = profile.ClassId;
            Guid profileId = profile.ProfileId;
            int enabledResult = profiles.IsEnabledLanguageProfile(
                ref classId,
                profile.LanguageId,
                ref profileId,
                out bool enabled);
            if (enabledResult < 0)
            {
                throw new COMException("TSF IsEnabledLanguageProfile failed.", enabledResult);
            }

            if (!enabled)
            {
                throw new InvalidOperationException("TSF reports that the requested input profile is not enabled.");
            }

            int languageResult = profiles.GetCurrentLanguage(out ushort currentLanguage);
            if (languageResult < 0)
            {
                throw new COMException("TSF GetCurrentLanguage failed.", languageResult);
            }

            if (currentLanguage != profile.LanguageId)
            {
                throw new InvalidOperationException(
                    $"当前输入语言 0x{currentLanguage:X4} 与目标 0x{profile.LanguageId:X4} 不一致。");
            }

            int activationResult = profiles.ActivateLanguageProfile(
                ref classId,
                profile.LanguageId,
                ref profileId);
            if (activationResult < 0)
            {
                throw new COMException("TSF ActivateLanguageProfile failed.", activationResult);
            }
        }
        finally
        {
            AudioEndpointStatusProbe.ReleaseCom(profiles);
        }
    }

    private static void EnsureLegacyLanguageProfileEnabled(InputMethodProfileDescriptor profile)
    {
        IInputProcessorProfiles? profiles = null;
        try
        {
            profiles = (IInputProcessorProfiles)(object)new InputProcessorProfilesComObject();
            Guid classId = profile.ClassId;
            Guid profileId = profile.ProfileId;
            int enableResult = profiles.EnableLanguageProfile(
                ref classId,
                profile.LanguageId,
                ref profileId,
                enable: true);
            if (enableResult < 0)
            {
                throw new COMException("TSF EnableLanguageProfile failed.", enableResult);
            }

            int enabledResult = profiles.IsEnabledLanguageProfile(
                ref classId,
                profile.LanguageId,
                ref profileId,
                out bool enabled);
            if (enabledResult < 0)
            {
                throw new COMException("TSF IsEnabledLanguageProfile failed after enablement.", enabledResult);
            }

            if (!enabled)
            {
                throw new InvalidOperationException("TSF did not confirm the requested input profile as enabled.");
            }
        }
        finally
        {
            AudioEndpointStatusProbe.ReleaseCom(profiles);
        }
    }

    private static IReadOnlyList<InputMethodProfileDescriptor> EnumerateInstalledProfilesCore(ushort languageId)
    {
        ExecuteWithProfileManager(manager =>
        {
            int result = manager.EnumProfiles(languageId, out IEnumInputProcessorProfiles profiles);
            if (result < 0)
            {
                throw new COMException("TSF EnumProfiles failed.", result);
            }

            List<InputMethodProfileDescriptor> values = [];
            try
            {
                while (true)
                {
                    int nextResult = profiles.Next(1, out TfInputProcessorProfile profile, out uint fetched);
                    if (nextResult < 0)
                    {
                        throw new COMException("TSF profile enumeration failed.", nextResult);
                    }

                    if (fetched != 1)
                    {
                        break;
                    }

                    values.Add(ToDescriptor(profile, string.Empty));
                }
            }
            finally
            {
                AudioEndpointStatusProbe.ReleaseCom(profiles);
            }

            return (IReadOnlyList<InputMethodProfileDescriptor>)values;
        }, out IReadOnlyList<InputMethodProfileDescriptor>? result);
        return result!;
    }

    private TResult InvokeOnSta<TResult>(Func<TResult> action)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Thread.CurrentThread == staThread)
        {
            return action();
        }

        TaskCompletionSource<TResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        staWork.Add(() =>
        {
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return completion.Task.GetAwaiter().GetResult();
    }

    private void RunStaWork()
    {
        int initializeResult = AudioEndpointNativeMethods.CoInitializeEx(nint.Zero, 0x2);
        bool uninitialize = initializeResult >= 0;
        ITextServiceThreadManager? threadManager = null;
        bool activated = false;
        try
        {
            if (initializeResult < 0)
            {
                Marshal.ThrowExceptionForHR(initializeResult);
            }

            _ = InputMethodProfileNativeMethods.PeekMessage(
                out _,
                nint.Zero,
                0,
                0,
                0);
            threadManager = (ITextServiceThreadManager)(object)new TextServiceThreadManagerComObject();
            Marshal.ThrowExceptionForHR(threadManager.Activate(out _));
            activated = true;
            staReady.Set();
            foreach (Action action in staWork.GetConsumingEnumerable())
            {
                action();
            }
        }
        catch (Exception exception)
        {
            staInitializationError = exception;
            staReady.Set();
        }
        finally
        {
            if (activated && threadManager is not null)
            {
                _ = threadManager.Deactivate();
            }

            AudioEndpointStatusProbe.ReleaseCom(threadManager);
            if (uninitialize)
            {
                AudioEndpointNativeMethods.CoUninitialize();
            }
        }
    }

    private static IEnumerable<InputMethodProfileDescriptor> EnumerateTipProfiles(RegistryKey tipKey)
    {
        foreach (string classIdText in tipKey.GetSubKeyNames())
        {
            if (!Guid.TryParse(classIdText, out Guid classId))
            {
                continue;
            }

            using RegistryKey? languageProfiles = tipKey.OpenSubKey(
                $@"{classIdText}\LanguageProfile",
                writable: false);
            if (languageProfiles is null)
            {
                continue;
            }

            foreach (string languageText in languageProfiles.GetSubKeyNames())
            {
                if (!TryParseLanguageId(languageText, out ushort languageId))
                {
                    continue;
                }

                using RegistryKey? languageKey = languageProfiles.OpenSubKey(languageText, writable: false);
                if (languageKey is null)
                {
                    continue;
                }

                foreach (string profileIdText in languageKey.GetSubKeyNames())
                {
                    if (!Guid.TryParse(profileIdText, out Guid profileId))
                    {
                        continue;
                    }

                    using RegistryKey? profileKey = languageKey.OpenSubKey(profileIdText, writable: false);
                    if (profileKey is null || IsExplicitlyDisabled(profileKey))
                    {
                        continue;
                    }

                    string description = profileKey.GetValue("Description") as string ?? string.Empty;
                    string displayDescription = ResolveDisplayDescription(
                        profileKey.GetValue("Display Description") as string);
                    string combined = string.Join(
                        " / ",
                        new[] { description, displayDescription }
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Distinct(StringComparer.OrdinalIgnoreCase));
                    yield return new InputMethodProfileDescriptor(
                        InputProcessorProfileType,
                        languageId,
                        classId,
                        profileId,
                        nint.Zero,
                        combined);
                }
            }
        }
    }

    private static bool IsExplicitlyDisabled(RegistryKey profileKey)
    {
        object? value = profileKey.GetValue("Enable");
        return value is not null && Convert.ToInt32(value, CultureInfo.InvariantCulture) == 0;
    }

    private static string ResolveDisplayDescription(string? displayDescription)
    {
        if (string.IsNullOrWhiteSpace(displayDescription) || !displayDescription.StartsWith('@'))
        {
            return displayDescription ?? string.Empty;
        }

        StringBuilder buffer = new(512);
        int result = InputMethodProfileNativeMethods.SHLoadIndirectString(
            displayDescription,
            buffer,
            (uint)buffer.Capacity,
            nint.Zero);
        return result >= 0 ? buffer.ToString() : string.Empty;
    }

    private static bool IsExactMatch(InputMethodProfileDescriptor candidate, string target) =>
        string.Equals(candidate.Description, target, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(candidate.ClassId.ToString("B"), target, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(candidate.ClassId.ToString("D"), target, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(candidate.ProfileId.ToString("B"), target, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(candidate.ProfileId.ToString("D"), target, StringComparison.OrdinalIgnoreCase) ||
        candidate.Description.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => string.Equals(value, target, StringComparison.OrdinalIgnoreCase));

    private static void AddDistinct(
        ICollection<InputMethodProfileDescriptor> profiles,
        InputMethodProfileDescriptor candidate)
    {
        if (!profiles.Any(existing => existing.IsSameProfile(candidate)))
        {
            profiles.Add(candidate);
        }
    }

    private static bool TryParseLanguageId(string value, out ushort languageId)
    {
        string normalized = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value[2..]
            : value;
        return ushort.TryParse(
            normalized,
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture,
            out languageId);
    }

    private static InputMethodProfileDescriptor ToDescriptor(
        TfInputProcessorProfile profile,
        string description) =>
        new(
            profile.ProfileType,
            profile.LanguageId,
            profile.ClassId,
            profile.ProfileId,
            profile.KeyboardLayout,
            description,
            profile.Flags);

    private static void ExecuteWithProfileManager<TResult>(
        Func<IInputProcessorProfileManager, TResult> action,
        out TResult result)
    {
        int initializeResult = AudioEndpointNativeMethods.CoInitializeEx(nint.Zero, 0x2);
        bool uninitialize = initializeResult >= 0;
        if (initializeResult < 0 && initializeResult != RpcChangedMode)
        {
            Marshal.ThrowExceptionForHR(initializeResult);
        }

        IInputProcessorProfileManager? manager = null;
        try
        {
            manager = (IInputProcessorProfileManager)(object)new InputProcessorProfilesComObject();
            result = action(manager);
        }
        finally
        {
            AudioEndpointStatusProbe.ReleaseCom(manager);
            if (uninitialize)
            {
                AudioEndpointNativeMethods.CoUninitialize();
            }
        }
    }
}

public sealed record InputMethodSessionResult(bool Succeeded, bool Changed, string Message);

public sealed class InputMethodSessionController
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private readonly IInputMethodProfileManager profileManager;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private Session? session;

    public InputMethodSessionController(
        IInputMethodProfileManager profileManager,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.profileManager = profileManager ?? throw new ArgumentNullException(nameof(profileManager));
        this.delay = delay ?? Task.Delay;
    }

    public bool HasActiveSession => session is not null;

    public async Task<InputMethodSessionResult> BeginAsync(
        long epoch,
        InputMethodSwitchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<string> errors = options.Validate();
        if (errors.Count > 0)
        {
            return new InputMethodSessionResult(false, false, string.Join(" ", errors));
        }

        if (session is not null)
        {
            return new InputMethodSessionResult(false, false, "上一轮输入法切换会话尚未恢复。");
        }

        InputMethodProfileDescriptor? original = null;
        bool activationAttempted = false;
        try
        {
            original = profileManager.CaptureActiveProfile();
            InputMethodProfileDescriptor? target = profileManager.FindInstalledProfile(options.TargetProfile);
            if (target is null)
            {
                return new InputMethodSessionResult(
                    false,
                    false,
                    $"未找到已安装的目标输入法 Profile：{options.TargetProfile}。");
            }

            if (original.IsSameProfile(target))
            {
                if (options.RefreshWhenAlreadyActive == true)
                {
                    InputMethodProfileDescriptor? fallback =
                        profileManager.FindEnabledFallbackProfile(target);
                    if (fallback is null)
                    {
                        return new InputMethodSessionResult(
                            false,
                            false,
                            "目标输入法需要刷新，但未找到已启用的临时输入 Profile；已取消本轮。");
                    }

                    activationAttempted = true;
                    profileManager.ActivateProfile(fallback, enableIfNeeded: false);
                    bool fallbackConfirmed = await ConfirmActiveAsync(
                        fallback,
                        options.ActivationTimeoutMilliseconds,
                        cancellationToken).ConfigureAwait(false);
                    if (!fallbackConfirmed)
                    {
                        InputMethodSessionResult rollback = await RestoreAfterFailedBeginAsync(original, options)
                            .ConfigureAwait(false);
                        return new InputMethodSessionResult(
                            false,
                            false,
                            $"切换到临时输入 Profile {fallback.Describe()} 后未在时限内确认。{rollback.Message}");
                    }

                    profileManager.ActivateProfile(target, options.AllowProfileEnablement);
                    bool targetConfirmed = await ConfirmActiveAsync(
                        target,
                        options.ActivationTimeoutMilliseconds,
                        cancellationToken).ConfigureAwait(false);
                    if (!targetConfirmed)
                    {
                        InputMethodSessionResult rollback = await RestoreAfterFailedBeginAsync(original, options)
                            .ConfigureAwait(false);
                        return new InputMethodSessionResult(
                            false,
                            false,
                            $"刷新后切回 {target.Describe()} 未在时限内确认。{rollback.Message}");
                    }

                    if (options.PostActivationDelayMilliseconds > 0)
                    {
                        await delay(
                            TimeSpan.FromMilliseconds(options.PostActivationDelayMilliseconds),
                            cancellationToken).ConfigureAwait(false);
                    }

                    session = new Session(epoch, original, Changed: false);
                    return new InputMethodSessionResult(
                        true,
                        false,
                        $"目标输入法 TSF 会话已刷新：{fallback.Describe()} → {target.Describe()}；已等待 {options.PostActivationDelayMilliseconds} ms 让快捷键监听就绪。");
                }

                session = new Session(epoch, original, Changed: false);
                return new InputMethodSessionResult(
                    true,
                    false,
                    $"目标输入法已处于激活状态：{target.Describe()}。");
            }

            activationAttempted = true;
            profileManager.ActivateProfile(target, options.AllowProfileEnablement);
            bool confirmed = await ConfirmActiveAsync(
                target,
                options.ActivationTimeoutMilliseconds,
                cancellationToken).ConfigureAwait(false);
            if (!confirmed)
            {
                InputMethodSessionResult rollback = await RestoreAfterFailedBeginAsync(original, options)
                    .ConfigureAwait(false);
                return new InputMethodSessionResult(
                    false,
                    false,
                    $"切换到 {target.Describe()} 后未在时限内确认。{rollback.Message}");
            }

            if (options.PostActivationDelayMilliseconds > 0)
            {
                await delay(
                    TimeSpan.FromMilliseconds(options.PostActivationDelayMilliseconds),
                    cancellationToken).ConfigureAwait(false);
            }

            session = new Session(epoch, original, Changed: true);
            return new InputMethodSessionResult(
                true,
                true,
                $"已切换并确认输入法：{target.Describe()}；已等待 {options.PostActivationDelayMilliseconds} ms 让快捷键监听就绪。");
        }
        catch (Exception exception) when (
            exception is COMException or
            InvalidOperationException or
            UnauthorizedAccessException or
            IOException or
            OperationCanceledException)
        {
            string rollbackMessage = string.Empty;
            if (activationAttempted && original is not null)
            {
                InputMethodSessionResult rollback = await RestoreAfterFailedBeginAsync(original, options)
                    .ConfigureAwait(false);
                rollbackMessage = $"{rollback.Message}";
            }

            return new InputMethodSessionResult(
                false,
                false,
                $"输入法切换失败：{DescribeException(exception)}。{rollbackMessage}".Trim());
        }
    }

    public async Task<InputMethodSessionResult> RestoreAsync(
        long epoch,
        InputMethodSwitchOptions options,
        bool emergency,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Session? current = session;
        if (current is null)
        {
            return new InputMethodSessionResult(true, false, "本轮没有需要恢复的输入法。");
        }

        if (current.Epoch != epoch)
        {
            return new InputMethodSessionResult(false, false, "拒绝用过期会话恢复输入法。");
        }

        session = null;
        if (!current.Changed || !options.RestoreAfterStop)
        {
            return new InputMethodSessionResult(true, false, "本轮输入法未改变，无需恢复。");
        }

        try
        {
            if (!emergency && options.RestoreDelayMilliseconds > 0)
            {
                await delay(
                    TimeSpan.FromMilliseconds(options.RestoreDelayMilliseconds),
                    cancellationToken).ConfigureAwait(false);
            }

            profileManager.ActivateProfile(current.Original, enableIfNeeded: false);
            bool confirmed = await ConfirmActiveAsync(
                current.Original,
                options.ActivationTimeoutMilliseconds,
                cancellationToken).ConfigureAwait(false);
            return confirmed
                ? new InputMethodSessionResult(
                    true,
                    true,
                    $"已恢复原输入法：{current.Original.Describe()}。")
                : new InputMethodSessionResult(
                    false,
                    true,
                    $"已请求恢复原输入法，但未在时限内确认：{current.Original.Describe()}。");
        }
        catch (Exception exception) when (
            exception is COMException or
            InvalidOperationException or
            UnauthorizedAccessException or
            IOException or
            OperationCanceledException)
        {
            return new InputMethodSessionResult(
                false,
                true,
                $"恢复原输入法失败：{DescribeException(exception)}");
        }
    }

    private async Task<InputMethodSessionResult> RestoreAfterFailedBeginAsync(
        InputMethodProfileDescriptor original,
        InputMethodSwitchOptions options)
    {
        try
        {
            profileManager.ActivateProfile(original, enableIfNeeded: false);
            bool confirmed = await ConfirmActiveAsync(
                original,
                options.ActivationTimeoutMilliseconds,
                CancellationToken.None).ConfigureAwait(false);
            return confirmed
                ? new InputMethodSessionResult(true, true, "已回滚到原输入法。")
                : new InputMethodSessionResult(false, true, "已请求回滚，但未确认原输入法恢复。");
        }
        catch (Exception exception) when (
            exception is COMException or
            InvalidOperationException or
            UnauthorizedAccessException or
            IOException)
        {
            return new InputMethodSessionResult(false, true, $"回滚原输入法失败：{DescribeException(exception)}");
        }
    }

    private static string DescribeException(Exception exception) => exception is COMException
        ? $"{exception.Message} (HRESULT=0x{exception.HResult:X8})"
        : exception.Message;

    private async Task<bool> ConfirmActiveAsync(
        InputMethodProfileDescriptor expected,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        int attempts = Math.Max(1, (int)Math.Ceiling(timeoutMilliseconds / PollInterval.TotalMilliseconds));
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InputMethodProfileDescriptor active = profileManager.CaptureActiveProfile();
            if (active.IsSameProfile(expected))
            {
                return true;
            }

            if (attempt + 1 < attempts)
            {
                await delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    private sealed record Session(long Epoch, InputMethodProfileDescriptor Original, bool Changed);
}

internal static class InputMethodProfileNativeMethods
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHLoadIndirectString(
        string source,
        StringBuilder outputBuffer,
        uint outputBufferCharacters,
        nint reserved);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessage(
        out NativeMessage message,
        nint window,
        uint minimumMessage,
        uint maximumMessage,
        uint removeMessage);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMessage
{
    internal nint Window;
    internal uint Message;
    internal nuint WParam;
    internal nint LParam;
    internal uint Time;
    internal NativePoint Point;
    internal uint Private;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    internal int X;
    internal int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TfInputProcessorProfile
{
    internal uint ProfileType;
    internal ushort LanguageId;
    internal Guid ClassId;
    internal Guid ProfileId;
    internal Guid CategoryId;
    internal nint SubstituteKeyboardLayout;
    internal uint Capabilities;
    internal nint KeyboardLayout;
    internal uint Flags;
}

[ComImport]
[Guid("33C53A50-F456-4884-B049-85FD643ECFED")]
internal sealed class InputProcessorProfilesComObject
{
}

[ComImport]
[Guid("1F02B6C5-7842-4EE6-8A0B-9A24183A95CA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInputProcessorProfiles
{
    [PreserveSig]
    int Register(ref Guid classId);

    [PreserveSig]
    int Unregister(ref Guid classId);

    [PreserveSig]
    int AddLanguageProfile(
        ref Guid classId,
        ushort languageId,
        ref Guid profileId,
        string description,
        uint descriptionLength,
        string iconFile,
        uint iconFileLength,
        uint iconIndex);

    [PreserveSig]
    int RemoveLanguageProfile(ref Guid classId, ushort languageId, ref Guid profileId);

    [PreserveSig]
    int EnumInputProcessorInfo(out nint profiles);

    [PreserveSig]
    int GetDefaultLanguageProfile(
        ushort languageId,
        ref Guid categoryId,
        out Guid classId,
        out Guid profileId);

    [PreserveSig]
    int SetDefaultLanguageProfile(ushort languageId, ref Guid classId, ref Guid profileId);

    [PreserveSig]
    int ActivateLanguageProfile(ref Guid classId, ushort languageId, ref Guid profileId);

    [PreserveSig]
    int GetActiveLanguageProfile(ref Guid classId, out ushort languageId, out Guid profileId);

    [PreserveSig]
    int GetLanguageProfileDescription(
        ref Guid classId,
        ushort languageId,
        ref Guid profileId,
        [MarshalAs(UnmanagedType.BStr)] out string description);

    [PreserveSig]
    int GetCurrentLanguage(out ushort languageId);

    [PreserveSig]
    int ChangeCurrentLanguage(ushort languageId);

    [PreserveSig]
    int GetLanguageList(out nint languageIds, out uint count);

    [PreserveSig]
    int EnumLanguageProfiles(ushort languageId, out nint profiles);

    [PreserveSig]
    int EnableLanguageProfile(
        ref Guid classId,
        ushort languageId,
        ref Guid profileId,
        [MarshalAs(UnmanagedType.Bool)] bool enable);

    [PreserveSig]
    int IsEnabledLanguageProfile(
        ref Guid classId,
        ushort languageId,
        ref Guid profileId,
        [MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [PreserveSig]
    int EnableLanguageProfileByDefault(
        ref Guid classId,
        ushort languageId,
        ref Guid profileId,
        [MarshalAs(UnmanagedType.Bool)] bool enable);

    [PreserveSig]
    int SubstituteKeyboardLayout(
        ref Guid classId,
        ushort languageId,
        ref Guid profileId,
        nint keyboardLayout);
}

[ComImport]
[Guid("71C6E74C-0F28-11D8-A82A-00065B84435C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInputProcessorProfileManager
{
    [PreserveSig]
    int ActivateProfile(
        uint profileType,
        ushort languageId,
        ref Guid classId,
        ref Guid profileId,
        nint keyboardLayout,
        uint flags);

    [PreserveSig]
    int DeactivateProfile(
        uint profileType,
        ushort languageId,
        ref Guid classId,
        ref Guid profileId,
        nint keyboardLayout,
        uint flags);

    [PreserveSig]
    int GetProfile(
        uint profileType,
        ushort languageId,
        ref Guid classId,
        ref Guid profileId,
        nint keyboardLayout,
        out TfInputProcessorProfile profile);

    [PreserveSig]
    int EnumProfiles(ushort languageId, out IEnumInputProcessorProfiles profiles);

    [PreserveSig]
    int ReleaseInputProcessor(ref Guid classId, uint flags);

    [PreserveSig]
    int RegisterProfile(
        ref Guid classId,
        ushort languageId,
        ref Guid profileId,
        string description,
        uint descriptionLength,
        string iconFile,
        uint iconFileLength,
        uint iconIndex,
        nint substituteKeyboardLayout,
        uint preferredLayout,
        [MarshalAs(UnmanagedType.Bool)] bool enabledByDefault,
        uint flags);

    [PreserveSig]
    int UnregisterProfile(ref Guid classId, ushort languageId, ref Guid profileId, uint flags);

    [PreserveSig]
    int GetActiveProfile(ref Guid categoryId, out TfInputProcessorProfile profile);
}

[ComImport]
[Guid("71C6E74D-0F28-11D8-A82A-00065B84435C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumInputProcessorProfiles
{
    [PreserveSig]
    int Clone(out IEnumInputProcessorProfiles profiles);

    [PreserveSig]
    int Next(uint count, out TfInputProcessorProfile profile, out uint fetched);

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int Skip(uint count);
}

[ComImport]
[Guid("529A9E6B-6587-4F23-AB9E-9C7D683E3C50")]
internal sealed class TextServiceThreadManagerComObject
{
}

[ComImport]
[Guid("AA80E801-2021-11D2-93E0-0060B067B86E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITextServiceThreadManager
{
    [PreserveSig]
    int Activate(out uint clientId);

    [PreserveSig]
    int Deactivate();
}
