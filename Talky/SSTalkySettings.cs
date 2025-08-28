using System;
using System.Collections.Generic;
using UserSettings.ServerSpecific;
using UserSettings.ServerSpecific.Examples;

namespace Talky
{
    public class SSTalkySettings : SSTextAreaExample
    {
        public override void Activate()
        {
            ServerSpecificSettingsSync.DefinedSettings = new ServerSpecificSettingBase[2]
            {
                (ServerSpecificSettingBase)new SSGroupHeader("Talky Settings"),
                (ServerSpecificSettingBase)new SSTwoButtonsSetting(null, "Active", "Enabled", "Disabled", false,
                    "Would you like your face to animate for other players?")
            };
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
        
        private void ProcessUserInput(ReferenceHub hub, ServerSpecificSettingBase setting)
        {
            switch (setting.SettingId)
            {
                case 0:
                    SSTwoButtonsSetting sstbs = setting as SSTwoButtonsSetting;
                    // Change the active state for the player talking
                    if(sstbs != null && !sstbs.SyncIsA)
                    {
                        if(hub.TryGetComponent(out SpeechTracker tracker))
                            UnityEngine.Object.Destroy(tracker);
                        return;
                    }
                    else
                    {
                        if (!hub.TryGetComponent(out SpeechTracker tracker))
                        {
                            hub.gameObject.AddComponent<SpeechTracker>();
                        }
                    }
                    
                    
                    break;
            }
        }
    }
}