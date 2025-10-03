using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.Thirdperson;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.OverlayAnims;
using UnityEngine;
using UserSettings.ServerSpecific;
using UserSettings.ServerSpecific.Examples;
using Logger = LabApi.Features.Console.Logger;

namespace Talky
{
    public class SSTalkySettings
    {
        private SSKeybindSetting grabKeybind;
        private SSDropdownSetting defaultEmotionDropdown;
        public void Activate()
        {
            grabKeybind = new SSKeybindSetting(null, Plugin.Config.Translations.SSGrabLabel , KeyCode.H, allowSpectatorTrigger: false,
                hint: Plugin.Config.Translations.SSGrabHint);
            string[] emotions =
            [
                Plugin.Config.Translations.Neutral,
                Plugin.Config.Translations.Happy,
                Plugin.Config.Translations.AwkwardSmile,
                Plugin.Config.Translations.Scared,
                Plugin.Config.Translations.Angry,
                Plugin.Config.Translations.Chad,
                Plugin.Config.Translations.Ogre
            ];
            
            defaultEmotionDropdown = new SSDropdownSetting(null, Plugin.Config.Translations.SSDefaultEmotionLabel, emotions, hint: Plugin.Config.Translations.SSDefaultEmotionHint);
            var settings = new ServerSpecificSettingBase[3]
            {
                new SSGroupHeader(Plugin.Config.Translations.SSGroupLabel),
                defaultEmotionDropdown,
                grabKeybind
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
        
        private void ProcessUserInput(ReferenceHub hub, ServerSpecificSettingBase setting)
        {
            if(hub==null || setting == null) return;
            // If the keybind for grab is pressed, and only when key is down. Event will trigger on key up as well otherwise.
            if (setting.SettingId == grabKeybind.SettingId && (setting is SSKeybindSetting kb && kb.SyncIsPressed))
            {
                if(!Plugin.Config.EnableGrabAnimation) return;
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
            } else if (setting.SettingId == defaultEmotionDropdown.SettingId)
            {
                var player = Player.Get(hub);
                EmotionPresetType preset = GetEmotionPreset(hub);
                player.ReferenceHub.ServerSetEmotionPreset(preset);

            }
        }
    }
}