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
    public class SSTalkySettings : SSTextAreaExample
    {
        private SSKeybindSetting grabKeybind;
        private SSDropdownSetting defaultEmotionDropdown;
        public override void Activate()
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
            var settings = new ServerSpecificSettingBase[3]
            {
                (ServerSpecificSettingBase)new SSGroupHeader(Plugin.Instance.Config.Translations.SSGroupLabel),
                (ServerSpecificSettingBase)defaultEmotionDropdown,
                (ServerSpecificSettingBase)grabKeybind
            };
            
            if(ServerSpecificSettingsSync.DefinedSettings == null)
                ServerSpecificSettingsSync.DefinedSettings = settings;
            else
                ServerSpecificSettingsSync.DefinedSettings = ServerSpecificSettingsSync.DefinedSettings.Concat(settings).ToArray();
            ServerSpecificSettingsSync.SendToAll();
            ServerSpecificSettingsSync.ServerOnSettingValueReceived += new Action<ReferenceHub, ServerSpecificSettingBase>(this.ProcessUserInput);
        }

        public override void Deactivate()
        {
            ServerSpecificSettingsSync.ServerOnSettingValueReceived -= new Action<ReferenceHub, ServerSpecificSettingBase>(this.ProcessUserInput);
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
            if (setting.SettingId == grabKeybind.SettingId && (setting is SSKeybindSetting kb && kb.SyncIsPressed))
            {
                if(!Plugin.Instance.Config.EnableGrabAnimation) return;
                var player = Player.Get(hub);
                //Logger.Debug("Grab key pressed by " + player.DisplayName);
                OverlayAnimationsSubcontroller subcontroller;
                if (!(player.ReferenceHub.roleManager.CurrentRole is IFpcRole currentRole) ||
                    !(currentRole.FpcModule.CharacterModelInstance is AnimatedCharacterModel
                        characterModelInstance) ||
                    !characterModelInstance.TryGetSubcontroller<OverlayAnimationsSubcontroller>(out subcontroller))
                {
                    // Non-animated character model speaking
                    //Logger.Debug("Failed to get OverlayAnimationsSubcontroller from NPC");
                    return;
                }
                //subcontroller._overlayAnimations[1].IsPlaying = true;
                subcontroller._overlayAnimations[1].OnStarted();
                subcontroller._overlayAnimations[1].SendRpc();
            } else if (setting.SettingId == defaultEmotionDropdown.SettingId)
            {
                var player = Player.Get(hub);
                EmotionPresetType preset = GetEmotionPreset(hub);
                player.ReferenceHub.ServerSetEmotionPreset(preset);
                /*if (player.ReferenceHub.TryGetComponent(out SpeechTracker tracker))
                {
                    tracker.DefaultPreset = preset;
                }*/
                //Logger.Debug($"Set default emotion for {player.DisplayName} to {preset}");
                
            }
        }
    }
}