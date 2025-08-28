using System;
using Exiled.API.Features;
using LabApi.Events.Arguments.PlayerEvents;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.Thirdperson;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers;
using PlayerRoles.PlayableScps.Scp3114;
using PlayerRoles.Voice;
using UnityEngine;
using VoiceChat;
using VoiceChat.Codec;

namespace Talky.EXILED;

public class VoiceChattingHandler
    {
        public void OnVoiceMessageSending(PlayerSendingVoiceMessageEventArgs ev)
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
                }
                else
                {
                    if (ev.Player.VoiceModule is HumanVoiceModule humanVoiceModule)
                    {
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
                                return;
                            }
                            var decoder = decoderProperty.GetValue(ev.Player.VoiceModule) as OpusDecoder;
                            Debug.Assert(decoder != null, nameof(decoder) + " != null");
                            
                            float[] samples = new float[1024]; //480
                            int len = decoder.Decode(ev.Message.Data, ev.Message.DataLength, samples);
                            tracker.buffer.Write(samples,len);
                            tracker.LastPacketTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                            // Debug section ///////////////
                            /*foreach (Player dummy in Player.DummyList)
                            {
                                if (!dummy.ReferenceHub.TryGetComponent(out SpeechTracker tracker1))
                                {
                                    continue;
                                }
                                tracker1.LastPacketTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                tracker1.buffer.Write(samples,len);
                            }*/
                            //////////////////////////////////
                        }
                    }
                }
            } catch (Exception e)
            {
                //Logger.Error(e);
            }
        }

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
                    
                    // Use reflection to get the decoder
                    var decoderProperty = typeof(VoiceModuleBase).GetProperty(
                        "Decoder",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
                    );
                    if (decoderProperty == null)
                    {
                        return;
                    }
                    var decoder = decoderProperty.GetValue(ev.Player.VoiceModule) as OpusDecoder;
                    Debug.Assert(decoder != null, nameof(decoder) + " != null");
                    
                    float[] samples = new float[1024]; //480
                    int len = decoder.Decode(ev.Message.Data, ev.Message.DataLength, samples);
                    tracker.buffer.Write(samples,len);
                    tracker.LastPacketTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    
                }
            } else if (ev.Player.VoiceModule is Scp3114VoiceModule scp3114VoiceModule && ev.Message.Channel == VoiceChatChannel.Proximity)
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

        public void OnSpawn(PlayerSpawnedEventArgs ev)
        {
            if(!ev.Player.ReferenceHub.TryGetComponent(out SpeechTracker tracker))
                ev.Player.ReferenceHub.gameObject.AddComponent<SpeechTracker>();
        }
        
        public void RegisterEvents()
        {
            // Register the event handler for voice messages
            LabApi.Events.Handlers.PlayerEvents.SendingVoiceMessage += OnVoiceMessageSending;
            LabApi.Events.Handlers.PlayerEvents.Spawned += OnSpawn;
        }
        
        public void UnregisterEvents()
        {
            // Unregister the event handler for voice messages
            LabApi.Events.Handlers.PlayerEvents.SendingVoiceMessage -= OnVoiceMessageSending;
            LabApi.Events.Handlers.PlayerEvents.Spawned -= OnSpawn;
        }
    }