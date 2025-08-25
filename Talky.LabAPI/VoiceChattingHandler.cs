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
using PlayerRoles.Voice;
using VoiceChat.Codec;

namespace Talky.LabAPI
{
    public class VoiceChattingHandler
    {
        public void OnVoiceMessageReceived(PlayerSendingVoiceMessageEventArgs ev)
        { 
            // Handle the voice message received event
            // You can access the player and message from the event args
            var player = ev.Player;
            var message = ev.Message;
            
            try
            {
                ReferenceHub hub = ev.Player.ReferenceHub;
                EmotionSubcontroller subcontroller;
                if (!(hub.roleManager.CurrentRole is IFpcRole currentRole) ||
                    !(currentRole.FpcModule.CharacterModelInstance is AnimatedCharacterModel
                        characterModelInstance) ||
                    !characterModelInstance.TryGetSubcontroller<EmotionSubcontroller>(out subcontroller))
                {
                    // Non-animated character model speaking
                    //ev.Player.ShowHint("You cannot animate",1f);
                }
                else
                {
                    if (ev.Player.VoiceModule is HumanVoiceModule humanVoiceModule)
                    {
                        //Logger.Info("Player: " + ev.Player.Nickname + " is speaking: "+ ev.Player.IsSpeaking);
                        if (/*ev.Player.IsSpeaking && */ev.Player.RoleBase is IVoiceRole role)
                        {
                            if (!ev.Player.ReferenceHub.TryGetComponent(out SpeechTracker tracker))
                            {
                                return;
                            }
                            
                            // Use reflection to get the decoder
                            var decoderProperty = typeof(VoiceModuleBase).GetProperty(
                                "Decoder",
                                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
                            );
                            if (decoderProperty == null)
                            {
                                Logger.Error("Could not find Decoder field in VoiceModuleBase");
                                return;
                            }
                            var decoder = decoderProperty.GetValue(ev.Player.VoiceModule) as OpusDecoder;
                            Debug.Assert(decoder != null, nameof(decoder) + " != null");
                            
                            float[] samples = new float[1024]; //480
                            int len = decoder.Decode(ev.Message.Data, ev.Message.DataLength, samples);
                            tracker.buffer.Write(samples,len);
                            tracker.LastPacketTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        }
                    }
                }
            } catch (Exception e)
            {
                Logger.Error(e);
            }
        }

        /*public void OnCommandExecuted(CommandExecutedEventArgs ev)
        {
            Logger.Debug($"CommandExecuted: {ev.Command}");
            if (ev.CommandType != CommandType.Client) return;
            if (ev.CommandName.ToLower().Equals("emotion"))
            {
                if (!ev.ExecutedSuccessfully)
                {
                    Logger.Error("Failed to execute command: " + ev.CommandName);
                    Logger.Error(ev.Response);
                    return;
                }
                if(Player.Get(ev.Sender)== null) return;
                if (!Player.Get(ev.Sender).ReferenceHub.TryGetComponent(out SpeechTracker tracker))
                {
                    return;
                }
                Enum.TryParse(ev.Arguments.At(0), out EmotionPresetType emotion);
                tracker.DefaultPreset = emotion;
            }
        }*/

        public void OnSpawn(PlayerSpawnedEventArgs ev)
        {
            //if(!Plugin.settings.IsTalkyActive(ev.Player.ReferenceHub)) return;
            if(!ev.Player.ReferenceHub.TryGetComponent(out SpeechTracker tracker))
                ev.Player.ReferenceHub.gameObject.AddComponent<SpeechTracker>();
        }
        
        public void RegisterEvents()
        {
            // Register the event handler for voice messages
            LabApi.Events.Handlers.PlayerEvents.SendingVoiceMessage += OnVoiceMessageReceived;
            LabApi.Events.Handlers.PlayerEvents.Spawned += OnSpawn;
        }
        
        public void UnregisterEvents()
        {
            // Unregister the event handler for voice messages
            LabApi.Events.Handlers.PlayerEvents.SendingVoiceMessage -= OnVoiceMessageReceived;
            LabApi.Events.Handlers.PlayerEvents.Spawned -= OnSpawn;
        }
    }
}