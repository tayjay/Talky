using System.Linq;
using System;
using LabApi.Features.Wrappers;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.Thirdperson;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.OverlayAnims;
using Talky.Behaviours;
using UnityEngine;
using UserSettings.ServerSpecific;
using Logger = LabApi.Features.Console.Logger;

namespace Talky.Settings;

public class SSTalkySettings
{
    private SSKeybindSetting _grabKeybind;
    private SSDropdownSetting _defaultEmotionDropdown;
    private SSTwoButtonsSetting _enalbeTalking;
    private ServerSpecificSettingBase[] _ownedSettings;
    private bool _active;

    public void Activate()
    {
        if (_active)
            return;

        _active = true;
        try
        {
            _grabKeybind = new SSKeybindSetting(null, Plugin.Instance.Config.Translations.SSGrabLabel , KeyCode.H, allowSpectatorTrigger: false,
                hint: Plugin.Instance.Config.Translations.SSGrabHint);
            string[] emotions =
            [
                Plugin.Instance.Config.Translations.Neutral,
                Plugin.Instance.Config.Translations.Happy,
                Plugin.Instance.Config.Translations.AwkwardSmile,
                Plugin.Instance.Config.Translations.Scared,
                Plugin.Instance.Config.Translations.Angry,
                Plugin.Instance.Config.Translations.Chad,
                Plugin.Instance.Config.Translations.Ogre
            ];

            _defaultEmotionDropdown = new SSDropdownSetting(null, Plugin.Instance.Config.Translations.SSDefaultEmotionLabel, emotions, hint: Plugin.Instance.Config.Translations.SSDefaultEmotionHint);

            _enalbeTalking = new SSTwoButtonsSetting(null, Plugin.Instance.Config.Translations.SSEnableTalkingLabel, Plugin.Instance.Config.Translations.SSDisabled, Plugin.Instance.Config.Translations.SSEnabled, true, Plugin.Instance.Config.Translations.SSEnableTalkingHint);

            _ownedSettings = new ServerSpecificSettingBase[]
            {
                new SSGroupHeader(Plugin.Instance.Config.Translations.SSGroupLabel),
                _defaultEmotionDropdown,
                _grabKeybind,
                _enalbeTalking,
            };

            if(ServerSpecificSettingsSync.DefinedSettings == null)
                ServerSpecificSettingsSync.DefinedSettings = _ownedSettings;
            else
                ServerSpecificSettingsSync.DefinedSettings = ServerSpecificSettingsSync.DefinedSettings.Concat(_ownedSettings).ToArray();
            ServerSpecificSettingsSync.SendToAll();
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
        if (!_active)
            return;

        _active = false;
        try
        {
            ServerSpecificSettingsSync.ServerOnSettingValueReceived -= ProcessUserInput;

            if (ServerSpecificSettingsSync.DefinedSettings != null && _ownedSettings != null)
            {
                ServerSpecificSettingsSync.DefinedSettings = ServerSpecificSettingsSync.DefinedSettings
                    .Where(setting => !IsOwnedSetting(setting))
                    .ToArray();
                ServerSpecificSettingsSync.SendToAll();
            }
        }
        catch (Exception exception)
        {
            Logger.Error("Talky failed to fully remove server-specific settings: " + exception);
        }
        finally
        {
            _ownedSettings = null;
            _grabKeybind = null;
            _defaultEmotionDropdown = null;
            _enalbeTalking = null;
        }
    }

    private bool IsOwnedSetting(ServerSpecificSettingBase setting)
    {
        for (int i = 0; i < _ownedSettings.Length; i++)
        {
            if (ReferenceEquals(_ownedSettings[i], setting))
                return true;
        }

        return false;
    }

    public EmotionPresetType GetEmotionPreset(ReferenceHub hub)
    {
        EmotionPresetType type; 
        int setting = ServerSpecificSettingsSync.GetSettingOfUser<SSDropdownSetting>(hub, _defaultEmotionDropdown.SettingId).SyncSelectionIndexRaw;
        type = (EmotionPresetType)setting;
        return type;
    }

    public bool GetEnableTalking(ReferenceHub hub)
    {
        return ServerSpecificSettingsSync.GetSettingOfUser<SSTwoButtonsSetting>(hub, _enalbeTalking.SettingId).SyncIsB;
    }
        
    private void ProcessUserInput(ReferenceHub hub, ServerSpecificSettingBase setting)
    {
        if(hub==null || setting == null) return;
        // If the keybind for grab is pressed, and only when key is down. Event will trigger on key up as well otherwise.
        if (setting.SettingId == _grabKeybind.SettingId && (setting is SSKeybindSetting kb && kb.SyncIsPressed))
        {
            if(!Plugin.Instance.Config.EnableGrabAnimation) return;
            var player = Player.Get(hub);
            if (Plugin.Instance?.Runtime?.TryGetSpeechTracker(
                    hub.netId,
                    out SpeechTracker tracker) == true &&
                tracker.Proxy != null &&
                !tracker.Proxy.IsDestroyed)
            {
                player = tracker.Proxy;
            }
            if (player == null || player.IsDestroyed)
                return;

            OverlayAnimationsSubcontroller subcontroller;
            if (!(player.ReferenceHub.roleManager.CurrentRole is IFpcRole currentRole) ||
                !(currentRole.FpcModule.CharacterModelInstance is AnimatedCharacterModel
                    characterModelInstance) ||
                !characterModelInstance.TryGetSubcontroller(out subcontroller))
            {
                // Non-animated character model speaking
                return;
            }
            // Hardcoded value, this SearchCompleteAnimation in the array
            subcontroller._overlayAnimations[1].OnStarted();
            subcontroller._overlayAnimations[1].SendRpc();
                
            if (Plugin.Instance?.Runtime?.TryGetLookOverride(
                    player.ReferenceHub.netId,
                    out LookOverride lookOverride) == true)
            {
                lookOverride.LastBusyTime = Time.time + 2;
            }
        } else if (setting.SettingId == _defaultEmotionDropdown.SettingId)
        {
            var player = Player.Get(hub);
            EmotionPresetType preset = GetEmotionPreset(hub);
            if (Plugin.Instance?.Runtime?.TryGetSpeechTracker(
                    hub.netId,
                    out SpeechTracker tracker) == true)
            {
                tracker.defaultPreset = preset;
            }
            player.Emotion = preset;
        } else if (setting.SettingId == _enalbeTalking.SettingId)
        {
            if (Plugin.Instance?.Runtime?.TryGetSpeechTracker(
                    hub.netId,
                    out SpeechTracker tracker) == true)
            {
                tracker.ShouldAnimateFace = GetEnableTalking(hub);
            }
        }
    }
}
