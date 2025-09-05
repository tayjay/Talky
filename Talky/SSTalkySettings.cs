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
            grabKeybind = new SSKeybindSetting(null, "Animate - Grab" , KeyCode.H, allowSpectatorTrigger: false,
                hint: "Reach out and grab something.");
            string[] emotions = Enum.GetNames(typeof(EmotionPresetType));
            defaultEmotionDropdown = new SSDropdownSetting(null, "Default Emotion", emotions, hint: "Default Emotion to display when not talking.");
            var settings = new ServerSpecificSettingBase[3]
            {
                (ServerSpecificSettingBase)new SSGroupHeader("Talky Settings"),
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
        
        public bool IsTalkyActive(ReferenceHub hub)
        {
            return ServerSpecificSettingsSync.GetSettingOfUser<SSTwoButtonsSetting>(hub, 0).SyncIsA;
        }

        public EmotionPresetType GetEmotionPreset(ReferenceHub hub)
        {
            EmotionPresetType type; 
            string setting = ServerSpecificSettingsSync.GetSettingOfUser<SSDropdownSetting>(hub, defaultEmotionDropdown.SettingId).SyncSelectionText;
            if (string.IsNullOrEmpty(setting))
                return EmotionPresetType.Neutral;
            Enum.TryParse<EmotionPresetType>(setting, out type);
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