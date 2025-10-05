using System;
using System.Diagnostics;
using GameCore;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Features.Console;
using LabApi.Features.Enums;
using LabApi.Features.Wrappers;
using MEC;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.Thirdperson;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers;
using PlayerRoles.PlayableScps.Scp3114;
using PlayerRoles.Voice;
using PlayerStatsSystem;
using VoiceChat;
using VoiceChat.Codec;

namespace Talky
{
    public class VoiceChattingHandler
    {

        /**
         * When the player talks and sends a voice message to the server.
         */
        public void OnNewVoiceSending(PlayerSendingVoiceMessageEventArgs ev)
        {
            if (ev.Player.VoiceModule is HumanVoiceModule humanVoiceModule)
            {
                if (ev.Player.RoleBase is IVoiceRole role)
                {
                    if (!ev.Player.ReferenceHub.TryGetComponent(out SpeechTracker tracker))
                    {
                        return;
                    }
                    tracker.VoiceMessageReceived(ev.Message.Data, ev.Message.DataLength);
                }
            }
        }

        /**
         * When the player is hurt.
         */
        public void OnHurt(PlayerHurtEventArgs ev)
        {
            if(!Plugin.Instance.Config.EnableReactionOnHurt) return;
            if (!ev.Player.ReferenceHub.TryGetComponent(out SpeechTracker tracker))
            {
                return;
            }

            int damage = 1000;
            if (ev.DamageHandler is StandardDamageHandler standardDamageHandler)
            {
                damage = (int)Math.Floor(standardDamageHandler.DealtHealthDamage);
            }
            if(damage <=Plugin.Instance.Config.MiniumDamageForReaction ) return;

            tracker.OverrideEmotion(EmotionPresetType.Angry, Math.Min(1000, damage*100));
        }

		

        /**
         * When the player spawns, add the SpeechTracker component if it doesn't already exist.
         */
        public void OnSpawn(PlayerSpawnedEventArgs ev)
        {
            if(!ev.Player.ReferenceHub.TryGetComponent(out SpeechTracker tracker))
                ev.Player.ReferenceHub.gameObject.AddComponent<SpeechTracker>();
        }

        /**
         * When the player uses an item (Medkit, SCP207, etc.), make them emote accordingly.
         */
        public void OnUsingItem(PlayerUsingItemEventArgs ev)
        {
            if(!Plugin.Instance.Config.EnableEmoteOnConsumables) return;
            float waitTime = 0;
            int openTime = 0;
            switch (ev.UsableItem.Type)
            {
                case ItemType.Adrenaline:
                    waitTime = 1f;
                    openTime = 1000;
                    break;
                case ItemType.SCP500:
                case ItemType.Painkillers:
                    waitTime = 1f;
                    openTime = 500;
                    break;
                case ItemType.SCP207:
                case ItemType.AntiSCP207:
                    waitTime = 1f;
                    openTime = 1000;
                    break;
                case ItemType.Medkit:
                    waitTime = 4f;
                    openTime = 500;
                    break;
                default:
                    return;
            }
            
            Timing.CallDelayed(waitTime, () =>
            {
                if(!ev.IsAllowed) return;
                if(ev.UsableItem is not { IsUsing: true }) return;
                if(!ev.Player.ReferenceHub.TryGetComponent(out SpeechTracker tracker)) return;
                tracker.OverrideEmotion(EmotionPresetType.Scared, openTime);
                if (ev.UsableItem.Type == ItemType.AntiSCP207)
                {
                    Timing.CallDelayed(2.4f, () =>
                    {
                        tracker.OverrideEmotion(EmotionPresetType.AwkwardSmile, 500);
                    });
                } else if( ev.UsableItem.Type == ItemType.SCP207)
                {
                    Timing.CallDelayed(2.5f, () =>
                    {
                        tracker.OverrideEmotion(EmotionPresetType.Happy, 500);
                    });
                }
            });
            
        }
        
        public void RegisterEvents()
        {
            // Register the event handler for voice messages
            LabApi.Events.Handlers.PlayerEvents.SendingVoiceMessage += OnNewVoiceSending;
            LabApi.Events.Handlers.PlayerEvents.Spawned += OnSpawn;
            LabApi.Events.Handlers.PlayerEvents.Hurt += OnHurt;
            LabApi.Events.Handlers.PlayerEvents.UsingItem += OnUsingItem;
        }
        
        public void UnregisterEvents()
        {
            // Unregister the event handler for voice messages
            LabApi.Events.Handlers.PlayerEvents.SendingVoiceMessage -= OnNewVoiceSending;
            LabApi.Events.Handlers.PlayerEvents.Spawned -= OnSpawn;
            LabApi.Events.Handlers.PlayerEvents.Hurt -= OnHurt;
            LabApi.Events.Handlers.PlayerEvents.UsingItem -= OnUsingItem;
        }
    }
}