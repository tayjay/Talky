using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.Thirdperson;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.OverlayAnims;
using Talky.Behaviours;
using Talky.Performance;
using UnityEngine;
using UserSettings.ServerSpecific;
using Logger = LabApi.Features.Console.Logger;
using PerformanceCounter = Talky.Performance.PerformanceCounter;

namespace Talky.Settings;

public sealed class SSTalkySettings
{
    private const int MinEmotion = 0;
    private const int MaxEmotion = 6;
    private const float GrabCooldown = 1.111f;
    private const float EmotionCooldown = 0.5f;
    private const bool EnableTalkingByDefault = true;

    private static bool _grabAnimationAccessFailureLogged;

    // Edge state and cooldowns bound client-driven setting events.
    private readonly HashSet<uint> _grabHeld = new HashSet<uint>();
    private readonly Dictionary<uint, float> _nextGrabAt = new Dictionary<uint, float>(128);
    private readonly Dictionary<uint, float> _nextEmotionAt = new Dictionary<uint, float>(128);
    private readonly Dictionary<uint, PendingDefaultPreset> _pendingDefaultPresets =
        new Dictionary<uint, PendingDefaultPreset>(128);
    private readonly List<uint> _dueDefaultPresetKeys = new List<uint>(128);

    private SSKeybindSetting _grabKeybind;
    private SSDropdownSetting _defaultEmotionDropdown;
    private SSTwoButtonsSetting _enableTalking;
    private ServerSpecificSettingBase[] _ownedSettings;
    private bool _active;
    private bool _eventSubscribed;

    public void Activate()
    {
        if (_active)
            return;

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
                EnableTalkingByDefault,
                translations.SSEnableTalkingHint);

            _ownedSettings = new ServerSpecificSettingBase[]
            {
                new SSGroupHeader(translations.SSGroupLabel),
                _defaultEmotionDropdown,
                _grabKeybind,
                _enableTalking,
            };

            // Append rather than assign: DefinedSettings is shared with every other plugin.
            ServerSpecificSettingsSync.DefinedSettings =
                ServerSpecificSettingsSync.DefinedSettings == null
                    ? _ownedSettings
                    : ServerSpecificSettingsSync.DefinedSettings.Concat(_ownedSettings).ToArray();
            ServerSpecificSettingsSync.SendToAll();

            // Record ownership before subscribing so partial subscriptions remain removable.
            _eventSubscribed = true;
            ServerSpecificSettingsSync.ServerOnSettingValueReceived += ProcessUserInput;
        }
        catch
        {
            Deactivate();
            throw;
        }
    }

    public void Deactivate()
    {
        if (!_active && !_eventSubscribed && _ownedSettings == null)
            return;

        _active = false;
        ResetPlayerState();

        if (_eventSubscribed)
        {
            try
            {
                ServerSpecificSettingsSync.ServerOnSettingValueReceived -= ProcessUserInput;
                _eventSubscribed = false;
            }
            catch (Exception exception)
            {
                Logger.Error("Failed to unsubscribe its settings handler: " + exception);
            }
        }

        // Skip shared-array repair and rebroadcast when no settings were added.
        if (_ownedSettings != null)
        {
            try
            {
                if (ServerSpecificSettingsSync.DefinedSettings != null)
                {
                    ServerSpecificSettingsSync.DefinedSettings =
                        ServerSpecificSettingsSync.DefinedSettings
                            .Where(setting => !IsOwnedSetting(setting))
                            .ToArray();
                }

                ServerSpecificSettingsSync.SendToAll();
            }
            catch (Exception exception)
            {
                Logger.Error(
                    "Failed to fully remove its server-specific settings: " + exception);
            }
        }

        // Clear local references even if shared-state cleanup fails; inactive handlers are inert.
        _ownedSettings = null;
        _grabKeybind = null;
        _defaultEmotionDropdown = null;
        _enableTalking = null;
    }

    public void ForgetPlayer(uint networkId)
    {
        _grabHeld.Remove(networkId);
        _nextGrabAt.Remove(networkId);
        _nextEmotionAt.Remove(networkId);
        _pendingDefaultPresets.Remove(networkId);
    }

    public void ResetPlayerState()
    {
        _grabHeld.Clear();
        _nextGrabAt.Clear();
        _nextEmotionAt.Clear();
        _pendingDefaultPresets.Clear();
        _dueDefaultPresetKeys.Clear();
    }

    internal void ProcessPendingDefaultPresets(float now)
    {
        if (!_active || _pendingDefaultPresets.Count == 0)
            return;

        _dueDefaultPresetKeys.Clear();
        foreach (KeyValuePair<uint, PendingDefaultPreset> pair in _pendingDefaultPresets)
        {
            if (now >= pair.Value.DueTime)
                _dueDefaultPresetKeys.Add(pair.Key);
        }

        for (int index = 0; index < _dueDefaultPresetKeys.Count; index++)
        {
            uint networkId = _dueDefaultPresetKeys[index];
            if (!_pendingDefaultPresets.TryGetValue(networkId, out PendingDefaultPreset pending))
                continue;

            _pendingDefaultPresets.Remove(networkId);
            Player player = pending.Player;
            if (player == null || player.IsDestroyed || player.NetworkId != networkId)
                continue;

            if (Plugin.Instance?.Runtime?.TryGetSpeechTracker(
                    networkId,
                    out SpeechTracker tracker) == true)
            {
                tracker.RequestDefaultPreset(pending.Preset, now, EmotionCooldown);
            }
            else
            {
                ApplyUntrackedDefaultPreset(player, pending.Preset, now);
            }
        }

        _dueDefaultPresetKeys.Clear();
    }

    private void ApplyUntrackedDefaultPreset(Player player, EmotionPresetType preset, float now)
    {
        _nextEmotionAt[player.NetworkId] = now + EmotionCooldown;
        if (player.Emotion != preset)
            player.Emotion = preset;
    }

    private void RequestUntrackedDefaultPreset(Player player, EmotionPresetType preset, float now)
    {
        uint networkId = player.NetworkId;
        if (_nextEmotionAt.TryGetValue(networkId, out float emotionReadyAt) &&
            now < emotionReadyAt)
        {
            _pendingDefaultPresets[networkId] =
                new PendingDefaultPreset(player, preset, emotionReadyAt);
            return;
        }

        _pendingDefaultPresets.Remove(networkId);
        ApplyUntrackedDefaultPreset(player, preset, now);
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

        SSTwoButtonsSetting setting;
        bool hasReceivedSetting =
            ServerSpecificSettingsSync.TryGetSettingOfUser(
                hub,
                _enableTalking.SettingId,
                out setting);
        return ResolveEnableTalking(hasReceivedSetting, setting);
    }

    private static bool ResolveEnableTalking(
        bool hasReceivedSetting,
        SSTwoButtonsSetting setting)
    {
        return !hasReceivedSetting || setting == null || setting.SyncIsB;
    }

    private void ProcessUserInput(ReferenceHub hub, ServerSpecificSettingBase setting)
    {
        // Keep this handler exception-free because the setting event is a global multicast.
        try
        {
            ProcessUserInputCore(hub, setting);
        }
        catch (Exception exception)
        {
            try
            {
                Logger.Error("Failed to process server-specific settings input: " + exception);
            }
            catch
            {
                // Do not let diagnostic logging break the global multicast either.
            }
        }
    }

    private void ProcessUserInputCore(ReferenceHub hub, ServerSpecificSettingBase setting)
    {
        if (!_active || hub == null || setting == null)
            return;

        TalkyPerformanceMonitor monitor = Plugin.Instance?.PerformanceMonitor;
        uint networkId = hub.netId;
        float now = Time.time;

        // Match both id and type because reloads can reuse setting ids.
        if (_grabKeybind != null && setting.SettingId == _grabKeybind.SettingId &&
            setting is SSKeybindSetting keybind)
        {
            if (!keybind.SyncIsPressed)
            {
                _grabHeld.Remove(networkId);
                return;
            }

            // Treat keybind input as an edge to prevent repeated broadcasts.
            if (!_grabHeld.Add(networkId))
                return;

            if (_nextGrabAt.TryGetValue(networkId, out float grabReadyAt) && now < grabReadyAt)
            {
                monitor?.Count(PerformanceCounter.GrabThrottled);
                return;
            }

            _nextGrabAt[networkId] = now + GrabCooldown;
            if (Plugin.Instance?.Config?.EnableGrabAnimation == true)
                PlayGrabAnimation(hub);
            return;
        }

        if (_defaultEmotionDropdown != null &&
            setting.SettingId == _defaultEmotionDropdown.SettingId &&
            setting is SSDropdownSetting dropdown)
        {
            // Use the game's clamped selection rather than raw client input.
            int selection = dropdown.SyncSelectionIndexValidated;
            if (!IsValidEmotion(selection))
            {
                monitor?.Count(PerformanceCounter.InvalidEmotionSelections);
                return;
            }

            EmotionPresetType preset = (EmotionPresetType)selection;

            if (Plugin.Instance?.Runtime?.TryGetSpeechTracker(
                    networkId,
                    out SpeechTracker tracker) == true)
            {
                _pendingDefaultPresets.Remove(networkId);
                tracker.RequestDefaultPreset(preset, now, EmotionCooldown);
                return;
            }

            Player player = Player.Get(hub);
            if (player == null || player.IsDestroyed)
                return;

            RequestUntrackedDefaultPreset(player, preset, now);

            return;
        }

        if (_enableTalking != null && setting.SettingId == _enableTalking.SettingId &&
            setting is SSTwoButtonsSetting talkingSetting &&
            Plugin.Instance?.Runtime?.TryGetSpeechTracker(
                networkId,
                out SpeechTracker talkingTracker) == true)
        {
            talkingTracker.ShouldAnimateFace = talkingSetting.SyncIsB;
        }
    }

    private static void PlayGrabAnimation(ReferenceHub hub)
    {
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
            !model.TryGetSubcontroller(out OverlayAnimationsSubcontroller subcontroller))
        {
            return;
        }

        OverlayAnimationsBase animation = subcontroller._overlayAnimations != null && subcontroller._overlayAnimations.Length > 1
            ? subcontroller._overlayAnimations[1]
            : null;
        if( animation == null)
        {
            if (!_grabAnimationAccessFailureLogged)
            {
                Logger.Error("Failed to access grab animation subcontroller.");
                _grabAnimationAccessFailureLogged = true;
            }
            return;
        }

        animation.OnStarted();
        animation.SendRpc();

        if (Plugin.Instance?.Runtime?.TryGetLookOverride(
                player.ReferenceHub.netId,
                out LookOverride lookOverride) == true)
        {
            // Suppress glancing for exactly as long as the animation occupies the player.
            lookOverride.LastBusyTime = Time.time + GrabCooldown;
        }
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
        selection >= MinEmotion && selection <= MaxEmotion;

    private readonly struct PendingDefaultPreset
    {
        public PendingDefaultPreset(Player player, EmotionPresetType preset, float dueTime)
        {
            Player = player;
            Preset = preset;
            DueTime = dueTime;
        }

        public Player Player { get; }
        public EmotionPresetType Preset { get; }
        public float DueTime { get; }
    }
}
