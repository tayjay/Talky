using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LabApi.Features.Wrappers;
using Mirror;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.Thirdperson;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.OverlayAnims;
using Talky.Behaviours;
using Talky.Performance;
using Talky.Security;
using UnityEngine;
using UserSettings.ServerSpecific;
using PerformanceCounter = Talky.Performance.PerformanceCounter;

namespace Talky.Settings;

public sealed class SSTalkySettings
{
    private readonly Dictionary<uint, PlayerSettingAdmission> _admissionByNetworkId =
        new Dictionary<uint, PlayerSettingAdmission>(128);

    private SSKeybindSetting _grabKeybind;
    private SSDropdownSetting _defaultEmotionDropdown;
    private SSTwoButtonsSetting _enableTalking;
    private ServerSpecificSettingBase[] _ownedSettings;
    private bool _active;
    private bool _eventSubscribed;
    private bool _settingsPublished;
    private bool _cleanupPending;
    private int _pendingEmotionCount;

    public bool HasCleanupResidue =>
        _cleanupPending || _eventSubscribed || _settingsPublished || _ownedSettings != null;
    public int PendingEmotionCount => _pendingEmotionCount;

    public void Activate()
    {
        if (_active)
            return;
        if (HasCleanupResidue)
        {
            throw new InvalidOperationException(
                "Talky server-specific settings still own state from an incomplete cleanup.");
        }

        _active = true;
        try
        {
            TranslationsConfig translations = Plugin.Instance.Config.Translations;
            _grabKeybind = new SSKeybindSetting(
                null,
                translations.SSGrabLabel,
                KeyCode.H,
                allowSpectatorTrigger: false,
                hint: translations.SSGrabHint);

            string[] emotions =
            {
                translations.Neutral,
                translations.Happy,
                translations.AwkwardSmile,
                translations.Scared,
                translations.Angry,
                translations.Chad,
                translations.Ogre,
            };

            _defaultEmotionDropdown = new SSDropdownSetting(
                null,
                translations.SSDefaultEmotionLabel,
                emotions,
                hint: translations.SSDefaultEmotionHint);
            _enableTalking = new SSTwoButtonsSetting(
                null,
                translations.SSEnableTalkingLabel,
                translations.SSDisabled,
                translations.SSEnabled,
                true,
                translations.SSEnableTalkingHint);

            _ownedSettings = new ServerSpecificSettingBase[]
            {
                new SSGroupHeader(translations.SSGroupLabel),
                _defaultEmotionDropdown,
                _grabKeybind,
                _enableTalking,
            };

            ServerSpecificSettingsSync.DefinedSettings =
                ServerSpecificSettingsSync.DefinedSettings == null
                    ? _ownedSettings
                    : ServerSpecificSettingsSync.DefinedSettings.Concat(_ownedSettings).ToArray();
            _settingsPublished = true;
            ServerSpecificSettingsSync.SendToAll();

            _eventSubscribed = true;
            ServerSpecificSettingsSync.ServerOnSettingValueReceived += ProcessUserInput;
        }
        catch (Exception activationException)
        {
            try
            {
                Deactivate();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "Talky settings activation and rollback both failed.",
                    activationException,
                    cleanupException);
            }

            throw;
        }
    }

    public void Deactivate()
    {
        if (!_active && !HasCleanupResidue)
            return;

        _active = false;
        _admissionByNetworkId.Clear();
        _pendingEmotionCount = 0;
        Plugin.Instance?.Runtime?.ObservePendingActionDepth();
        Exception failure = null;

        if (_eventSubscribed)
        {
            try
            {
                ServerSpecificSettingsSync.ServerOnSettingValueReceived -= ProcessUserInput;
                _eventSubscribed = false;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        if (_settingsPublished)
        {
            try
            {
                if (ServerSpecificSettingsSync.DefinedSettings != null && _ownedSettings != null)
                {
                    ServerSpecificSettingsSync.DefinedSettings =
                        ServerSpecificSettingsSync.DefinedSettings
                            .Where(setting => !IsOwnedSetting(setting))
                            .ToArray();
                }

                ServerSpecificSettingsSync.SendToAll();
                _settingsPublished = false;
            }
            catch (Exception exception)
            {
                failure = failure == null
                    ? exception
                    : new AggregateException(failure, exception);
            }
        }

        if (failure != null)
        {
            _cleanupPending = true;
            throw new InvalidOperationException(
                "Talky could not fully remove its server-specific settings.",
                failure);
        }

        _cleanupPending = false;
        _ownedSettings = null;
        _grabKeybind = null;
        _defaultEmotionDropdown = null;
        _enableTalking = null;
    }

    public void ForgetPlayer(uint networkId)
    {
        if (_admissionByNetworkId.TryGetValue(
                networkId,
                out PlayerSettingAdmission admission) &&
            admission.HasPendingEmotion)
        {
            _pendingEmotionCount--;
        }
        _admissionByNetworkId.Remove(networkId);
        Plugin.Instance?.Runtime?.ObservePendingActionDepth();
    }

    public void ResetPlayerState()
    {
        _admissionByNetworkId.Clear();
        _pendingEmotionCount = 0;
        Plugin.Instance?.Runtime?.ObservePendingActionDepth();
    }

    /// <summary>
    /// Applies the latest coalesced emotion selection. Called from Talky's existing main-thread
    /// scheduler; it does no work unless a player's half-second admission window has elapsed.
    /// </summary>
    public void ProcessPendingEmotionChanges(double nowSeconds)
    {
        if (!_active || _admissionByNetworkId.Count == 0 ||
            Plugin.Instance?.Runtime?.IsAnimationQuarantined == true)
            return;

        bool depthChanged = false;
        foreach (KeyValuePair<uint, PlayerSettingAdmission> pair in _admissionByNetworkId)
        {
            if (pair.Value.TryTakePendingEmotion(nowSeconds, out int selection))
            {
                _pendingEmotionCount--;
                depthChanged = true;
                ApplyEmotion(pair.Key, (EmotionPresetType)selection);
            }
        }

        if (depthChanged)
            Plugin.Instance?.Runtime?.ObservePendingActionDepth();
    }

    public EmotionPresetType GetEmotionPreset(ReferenceHub hub)
    {
        if (hub == null || _defaultEmotionDropdown == null)
            return EmotionPresetType.Neutral;

        SSDropdownSetting setting =
            ServerSpecificSettingsSync.GetSettingOfUser<SSDropdownSetting>(
                hub,
                _defaultEmotionDropdown.SettingId);
        int selection = setting?.SyncSelectionIndexValidated ?? 0;
        return IsValidEmotion(selection)
            ? (EmotionPresetType)selection
            : EmotionPresetType.Neutral;
    }

    public bool GetEnableTalking(ReferenceHub hub)
    {
        if (hub == null || _enableTalking == null)
            return true;

        SSTwoButtonsSetting setting =
            ServerSpecificSettingsSync.GetSettingOfUser<SSTwoButtonsSetting>(
                hub,
                _enableTalking.SettingId);
        return setting?.SyncIsB ?? true;
    }

    private void ProcessUserInput(ReferenceHub hub, ServerSpecificSettingBase setting)
    {
        if (!_active || hub == null || setting == null || !IsTalkySetting(setting))
            return;

        TalkyPerformanceMonitor monitor = Plugin.Instance?.PerformanceMonitor;
        monitor?.Count(PerformanceCounter.SettingsResponses);
        long started = monitor?.Begin(PerformanceMetric.SettingsResponse) ??
                       PerformanceProfiler.NotSampled;
        try
        {
            ProcessUserInputCore(hub, setting, monitor);
        }
        finally
        {
            monitor?.End(PerformanceMetric.SettingsResponse, started);
        }
    }

    private void ProcessUserInputCore(
        ReferenceHub hub,
        ServerSpecificSettingBase setting,
        TalkyPerformanceMonitor monitor)
    {

        double nowSeconds = MonotonicSeconds();

        if (_grabKeybind != null && setting.SettingId == _grabKeybind.SettingId &&
            setting is SSKeybindSetting keybind)
        {
            if (Plugin.Instance?.Runtime?.IsAnimationQuarantined == true)
            {
                monitor?.Count(PerformanceCounter.AnimationRpcThrottled);
                return;
            }

            PlayerSettingAdmission admission = GetAdmission(hub.netId);
            GrabAdmission result = admission.ObserveGrab(keybind.SyncIsPressed, nowSeconds);
            if (result == GrabAdmission.Throttled || result == GrabAdmission.InvalidTime)
                monitor?.Count(PerformanceCounter.GrabThrottled);
            else if (result == GrabAdmission.DuplicatePress)
                monitor?.Count(PerformanceCounter.GrabDuplicate);

            if (result != GrabAdmission.Accepted)
            {
                return;
            }

            monitor?.Count(PerformanceCounter.GrabAccepted);
            if (Plugin.Instance?.Config?.EnableGrabAnimation == true)
                PlayGrabAnimation(hub, monitor);
            return;
        }

        if (_defaultEmotionDropdown != null &&
            setting.SettingId == _defaultEmotionDropdown.SettingId &&
            setting is SSDropdownSetting dropdown)
        {
            int rawSelection = dropdown.SyncSelectionIndexRaw;
            int validatedSelection = dropdown.SyncSelectionIndexValidated;
            if (!IsValidEmotion(rawSelection) || !IsValidEmotion(validatedSelection))
            {
                monitor?.Count(PerformanceCounter.InvalidEmotionSelections);
                return;
            }

            if (Plugin.Instance?.Runtime?.IsAnimationQuarantined == true)
                return;

            PlayerSettingAdmission admission = GetAdmission(hub.netId);
            bool hadPending = admission.HasPendingEmotion;
            EmotionAdmission result =
                admission.ObserveEmotion(validatedSelection, nowSeconds);
            bool hasPending = admission.HasPendingEmotion;
            if (hadPending != hasPending)
            {
                _pendingEmotionCount += hasPending ? 1 : -1;
                Plugin.Instance?.Runtime?.ObservePendingActionDepth();
                if (hadPending)
                    monitor?.Count(PerformanceCounter.PendingActionsCoalesced);
            }
            if (result == EmotionAdmission.Applied)
                ApplyEmotion(hub, (EmotionPresetType)validatedSelection);
            else if (result == EmotionAdmission.Coalesced)
            {
                monitor?.Count(PerformanceCounter.EmotionCoalesced);
                monitor?.Count(
                    hadPending
                        ? PerformanceCounter.PendingActionsCoalesced
                        : PerformanceCounter.PendingActionsQueued);
            }
            return;
        }

        if (_enableTalking != null && setting.SettingId == _enableTalking.SettingId &&
            setting is SSTwoButtonsSetting talkingSetting &&
            Plugin.Instance?.Runtime?.TryGetSpeechTracker(
                hub.netId,
                out SpeechTracker tracker) == true)
        {
            tracker.ShouldAnimateFace = talkingSetting.SyncIsB;
        }
    }

    private static void PlayGrabAnimation(
        ReferenceHub hub,
        TalkyPerformanceMonitor monitor)
    {
        if (Plugin.Instance?.Runtime?.IsAnimationQuarantined == true)
            return;

        Player player = Player.Get(hub);
        if (Plugin.Instance?.Runtime?.TryGetSpeechTracker(
                hub.netId,
                out SpeechTracker tracker) == true &&
            tracker.Proxy != null &&
            !tracker.Proxy.IsDestroyed)
        {
            player = tracker.Proxy;
        }

        if (player == null || player.IsDestroyed || player.ReferenceHub == null)
            return;

        if (!(player.ReferenceHub.roleManager.CurrentRole is IFpcRole currentRole) ||
            !(currentRole.FpcModule.CharacterModelInstance is AnimatedCharacterModel model) ||
            !model.TryGetSubcontroller(out OverlayAnimationsSubcontroller subcontroller) ||
            subcontroller._overlayAnimations == null ||
            subcontroller._overlayAnimations.Length <= 1 ||
            subcontroller._overlayAnimations[1] == null)
        {
            return;
        }

        OverlayAnimationsBase animation = subcontroller._overlayAnimations[1];
        long started = monitor?.Begin(PerformanceMetric.AnimationRpc) ??
                       PerformanceProfiler.NotSampled;
        try
        {
            long readyRecipients = CountReadyNetworkRecipients();
            animation.OnStarted();
            animation.SendRpc();
            monitor?.Count(PerformanceCounter.AnimationRpcSends);
            monitor?.Count(
                PerformanceCounter.AnimationRpcLogicalRecipients,
                readyRecipients);
        }
        finally
        {
            monitor?.End(PerformanceMetric.AnimationRpc, started);
        }

        if (Plugin.Instance?.Runtime?.TryGetLookOverride(
                player.ReferenceHub.netId,
                out LookOverride lookOverride) == true)
        {
            lookOverride.LastBusyTime =
                Time.time + (float)PlayerSettingAdmission.GrabAnimationSeconds;
        }
    }

    /// <summary>
    /// Matches Mirror's SendToReady recipient predicate. This remains a logical recipient count:
    /// SendRpc returning does not expose serialized bytes, packets, queues, delivery, or drops.
    /// </summary>
    private static long CountReadyNetworkRecipients()
    {
        long recipients = 0L;
        foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
        {
            if (connection != null && connection.isReady)
                recipients++;
        }

        return recipients;
    }

    private static void ApplyEmotion(ReferenceHub hub, EmotionPresetType preset)
    {
        if (hub == null || Plugin.Instance?.Runtime?.IsAnimationQuarantined == true)
            return;

        Player player = Player.Get(hub);
        if (Plugin.Instance?.Runtime?.TryGetSpeechTracker(
                hub.netId,
                out SpeechTracker tracker) == true)
        {
            tracker.defaultPreset = preset;
        }

        if (player != null && !player.IsDestroyed)
        {
            player.Emotion = preset;
            Plugin.Instance?.PerformanceMonitor?.Count(PerformanceCounter.EmotionApplied);
        }
    }

    private static void ApplyEmotion(uint networkId, EmotionPresetType preset)
    {
        if (Plugin.Instance?.Runtime?.IsAnimationQuarantined == true)
            return;

        if (Plugin.Instance?.Runtime?.TryGetSpeechTracker(
                networkId,
                out SpeechTracker tracker) != true ||
            tracker?.Player == null ||
            tracker.Player.IsDestroyed)
        {
            return;
        }

        tracker.defaultPreset = preset;
        tracker.Player.Emotion = preset;
        Plugin.Instance?.PerformanceMonitor?.Count(PerformanceCounter.EmotionApplied);
    }

    private PlayerSettingAdmission GetAdmission(uint networkId)
    {
        if (!_admissionByNetworkId.TryGetValue(networkId, out PlayerSettingAdmission admission))
        {
            admission = new PlayerSettingAdmission();
            _admissionByNetworkId.Add(networkId, admission);
        }

        return admission;
    }

    private bool IsTalkySetting(ServerSpecificSettingBase setting)
    {
        int settingId = setting.SettingId;
        return (_grabKeybind != null &&
                settingId == _grabKeybind.SettingId &&
                setting is SSKeybindSetting) ||
               (_defaultEmotionDropdown != null &&
                settingId == _defaultEmotionDropdown.SettingId &&
                setting is SSDropdownSetting) ||
               (_enableTalking != null &&
                settingId == _enableTalking.SettingId &&
                setting is SSTwoButtonsSetting);
    }

    private bool IsOwnedSetting(ServerSpecificSettingBase setting)
    {
        if (_ownedSettings == null)
            return false;

        for (int i = 0; i < _ownedSettings.Length; i++)
        {
            if (ReferenceEquals(_ownedSettings[i], setting))
                return true;
        }

        return false;
    }

    private static bool IsValidEmotion(int selection) =>
        selection >= PlayerSettingAdmission.MinimumEmotionIndex &&
        selection <= PlayerSettingAdmission.MaximumEmotionIndex;

    private static double MonotonicSeconds() =>
        Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
