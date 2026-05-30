using System;
using System.Linq;
using LabApi.Features.Wrappers;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.Thirdperson;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.OverlayAnims;
using UnityEngine;
using UserSettings.ServerSpecific;

namespace Talky
{
    public class SSTalkySettings
    {
        private SSKeybindSetting grabKeybind;
        private SSDropdownSetting defaultEmotionDropdown;
        private SSTwoButtonsSetting enalbeTalking;
        public void Activate()
        {
            grabKeybind = new SSKeybindSetting(null, Plugin.Instance.Config.Translations.SSGrabLabel , KeyCode.H, allowSpectatorTrigger: false,
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
            
            defaultEmotionDropdown = new SSDropdownSetting(null, Plugin.Instance.Config.Translations.SSDefaultEmotionLabel, emotions, hint: Plugin.Instance.Config.Translations.SSDefaultEmotionHint);
            
            enalbeTalking = new SSTwoButtonsSetting(null, Plugin.Instance.Config.Translations.SSEnableTalkingLabel, Plugin.Instance.Config.Translations.SSDisabled, Plugin.Instance.Config.Translations.SSEnabled, true, Plugin.Instance.Config.Translations.SSEnableTalkingHint);

            var settings = new ServerSpecificSettingBase[4]
            {
                new SSGroupHeader(Plugin.Instance.Config.Translations.SSGroupLabel),
                defaultEmotionDropdown,
                grabKeybind,
                enalbeTalking,
            };
            
            
            if(ServerSpecificSettingsSync.DefinedSettings == null)
                ServerSpecificSettingsSync.DefinedSettings = settings;
            else
                ServerSpecificSettingsSync.DefinedSettings = ServerSpecificSettingsSync.DefinedSettings.Concat(settings).ToArray();
            ServerSpecificSettingsSync.SendToAll();
            ServerSpecificSettingsSync.ServerOnSettingValueReceived += ProcessUserInput;
        }

        public void Deactivate()
        {
            ServerSpecificSettingsSync.ServerOnSettingValueReceived -= ProcessUserInput;
        }

        public EmotionPresetType GetEmotionPreset(ReferenceHub hub)
        {
            EmotionPresetType type; 
            int setting = ServerSpecificSettingsSync.GetSettingOfUser<SSDropdownSetting>(hub, defaultEmotionDropdown.SettingId).SyncSelectionIndexRaw;
            type = (EmotionPresetType)setting;
            return type;
        }

        public bool GetEnableTalking(ReferenceHub hub)
        {
            return ServerSpecificSettingsSync.GetSettingOfUser<SSTwoButtonsSetting>(hub, enalbeTalking.SettingId).SyncIsA;
        }
        
        private void ProcessUserInput(ReferenceHub hub, ServerSpecificSettingBase setting)
        {
            if(hub==null || setting == null) return;
            // If the keybind for grab is pressed, and only when key is down. Event will trigger on key up as well otherwise.
            if (setting.SettingId == grabKeybind.SettingId && (setting is SSKeybindSetting kb && kb.SyncIsPressed))
            {
                if(!Plugin.Instance.Config.EnableGrabAnimation) return;
                var player = Player.Get(hub);
                if(player.GameObject.TryGetComponent<SpeechTracker>(out var tracker) && tracker.Proxy!=null)
                {
                    player = tracker.Proxy;
                }
                OverlayAnimationsSubcontroller subcontroller;
                if (!(player.ReferenceHub.roleManager.CurrentRole is IFpcRole currentRole) ||
                    !(currentRole.FpcModule.CharacterModelInstance is AnimatedCharacterModel
                        characterModelInstance) ||
                    !characterModelInstance.TryGetSubcontroller<OverlayAnimationsSubcontroller>(out subcontroller))
                {
                    // Non-animated character model speaking
                    return;
                }
                // Hardcoded value, this SearchCompleteAnimation in the array
                subcontroller._overlayAnimations[1].OnStarted();
                subcontroller._overlayAnimations[1].SendRpc();
                
                if(player.GameObject.TryGetComponent<LookOverride>(out var lookOverride))
                {
                    lookOverride.LastBusyTime = Time.time + 2;
                }
            } else if (setting.SettingId == defaultEmotionDropdown.SettingId)
            {
                var player = Player.Get(hub);
                EmotionPresetType preset = GetEmotionPreset(hub);
                player.Emotion = preset;
            } else if (setting.SettingId == enalbeTalking.SettingId)
            {
                var player = Player.Get(hub);
                if (setting is SSTwoButtonsSetting twoButtonsSetting)
                {
                    //bool enabled = twoButtonsSetting.SyncIsA;
                    EmotionPresetType preset = GetEmotionPreset(hub);
                    player.Emotion = preset;
                }
            }
        }
    }
}