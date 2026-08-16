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

    // Edge state and cooldowns for client-controlled settings. ServerOnSettingValueReceived is
    // driven entirely by the client, so a modified one can send as fast as it likes.
    private readonly HashSet<uint> _grabHeld = new HashSet<uint>();
    private readonly Dictionary<uint, float> _nextGrabAt = new Dictionary<uint, float>(128);
    private readonly Dictionary<uint, float> _nextEmotionAt = new Dictionary<uint, float>(128);

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
                true,
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

            // Set before subscribing so a throw from += cannot leave the flag lying.
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
                Logger.Error("Talky failed to unsubscribe its settings handler: " + exception);
            }
        }

        // Nothing owned means nothing was ever appended, so there is no shared array to repair
        // and no reason to rebroadcast the whole settings list to every client.
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
                    "Talky failed to fully remove its server-specific settings: " + exception);
            }
        }

        // Released regardless of the outcome above. The !_active check is what makes a surviving
        // subscription inert, so holding these references buys nothing.
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
    }

    public void ResetPlayerState()
    {
        _grabHeld.Clear();
        _nextGrabAt.Clear();
        _nextEmotionAt.Clear();
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
        // ServerOnSettingValueReceived is a global multicast: a throw here aborts dispatch for
        // every plugin whose handler is registered after Talky's.
        if (!_active || hub == null || setting == null)
            return;

        TalkyPerformanceMonitor monitor = Plugin.Instance?.PerformanceMonitor;
        uint networkId = hub.netId;
        float now = Time.time;

        // The concrete-type test is not redundant with the id test: Deactivate rewrites the
        // shared DefinedSettings array, so ids can collide across a Talky reload.
        if (_grabKeybind != null && setting.SettingId == _grabKeybind.SettingId &&
            setting is SSKeybindSetting keybind)
        {
            if (!keybind.SyncIsPressed)
            {
                _grabHeld.Remove(networkId);
                return;
            }

            // Edge-triggered. Without this, a client repeating pressed=true turns each message
            // into a server-wide OverlayAnimationsBase.SendRpc.
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
            // Validated, not raw: the raw index is unclamped client input, and range-checking it
            // would discard a selection the game itself would have clamped into range.
            int selection = dropdown.SyncSelectionIndexValidated;
            if (!IsValidEmotion(selection))
            {
                monitor?.Count(PerformanceCounter.InvalidEmotionSelections);
                return;
            }

            EmotionPresetType preset = (EmotionPresetType)selection;

            // The tracker default is a local field read at the player's next spawn, so it costs
            // nothing and is never rate limited. Only the replicated write below is.
            if (Plugin.Instance?.Runtime?.TryGetSpeechTracker(
                    networkId,
                    out SpeechTracker tracker) == true)
            {
                tracker.defaultPreset = preset;
            }

            if (_nextEmotionAt.TryGetValue(networkId, out float emotionReadyAt) &&
                now < emotionReadyAt)
            {
                return;
            }

            _nextEmotionAt[networkId] = now + EmotionCooldown;
            Player player = Player.Get(hub);

            // Skip the replicated write when it would change nothing: a client resending an
            // unchanged selection should cost zero network traffic, not one broadcast per window.
            if (player != null && !player.IsDestroyed && player.Emotion != preset)
                player.Emotion = preset;

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

        // _overlayAnimations is a publicized private field on a Northwood type that is free to
        // change shape, so index 1 is checked rather than assumed.
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
}
