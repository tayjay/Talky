using System;
using System.Diagnostics;
using GameCore;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Features.Console;
using LabApi.Features.Enums;
using LabApi.Features.Wrappers;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.Thirdperson;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers;
using PlayerRoles.PlayableScps.Scp3114;
using PlayerRoles.Voice;
using VoiceChat;
using VoiceChat.Codec;

namespace Talky
{
    public class VoiceChattingHandler
    {

        public void OnNewVoiceSending(PlayerSendingVoiceMessageEventArgs ev)
        {
            if (ev.Player.VoiceModule is HumanVoiceModule humanVoiceModule)
            {
                if (/*ev.Player.IsSpeaking && */ev.Player.RoleBase is IVoiceRole role)
                {
                    if (!ev.Player.ReferenceHub.TryGetComponent(out SpeechTracker tracker))
                    {
                        return;
                    }
                    
                    tracker.VoiceMessageReceived(ev.Message.Data, ev.Message.DataLength);
                }
            }

            // Debug code to make NPCs respond to voice chat
            /*foreach (Player npc in Player.DummyList)
            {
                if (npc.VoiceModule is HumanVoiceModule npchumanVoiceModule)
                {
                    if (/*ev.Player.IsSpeaking && #1#npc.RoleBase is IVoiceRole npcRole)
                    {
                        if (!npc.ReferenceHub.TryGetComponent(out SpeechTracker npctracker))
                        {
                            return;
                        }
                    
                        npctracker.VoiceMessageReceived(ev.Message.Data, ev.Message.DataLength);
                    }
                }
            }*/
        }

        public void OnSpawn(PlayerSpawnedEventArgs ev)
        {
            if(!ev.Player.ReferenceHub.TryGetComponent(out SpeechTracker tracker))
                ev.Player.ReferenceHub.gameObject.AddComponent<SpeechTracker>();
        }
        
        public void RegisterEvents()
        {
            // Register the event handler for voice messages
            LabApi.Events.Handlers.PlayerEvents.SendingVoiceMessage += OnNewVoiceSending;
            LabApi.Events.Handlers.PlayerEvents.Spawned += OnSpawn;
        }
        
        public void UnregisterEvents()
        {
            // Unregister the event handler for voice messages
            LabApi.Events.Handlers.PlayerEvents.SendingVoiceMessage -= OnNewVoiceSending;
            LabApi.Events.Handlers.PlayerEvents.Spawned -= OnSpawn;
        }
    }
}