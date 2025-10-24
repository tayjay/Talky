using System;
using AdminToys;
using GameCore;
using LabApi.Features.Wrappers;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.Thirdperson;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers;
using PlayerRoles.PlayableScps.Scp3114;
using PlayerRoles.Voice;
using UnityEngine;
using VoiceChat.Codec;
using VoiceChat.Networking;
using Logger = LabApi.Features.Console.Logger;
using PrimitiveObjectToy = LabApi.Features.Wrappers.PrimitiveObjectToy;

namespace Talky
{
    /**
     * Tracks a player's speech and adjusts their mouth animations accordingly.
     */
    public class SpeechTracker : MonoBehaviour
    {
        public Player player;

        public ReferenceHub hub => player.ReferenceHub;
        public Player Proxy { get; set; }
        
        public PlaybackBuffer buffer;
        private EmotionPresetType _defaultPreset = EmotionPresetType.Neutral;
        
        private EmotionPresetType _overridePreset = EmotionPresetType.Neutral;
        private long _overrideEndTime = 0;
        
        
        public OpusDecoder OpusDecoder
        {
            get
            {
                return player.VoiceModule.Decoder;
            }
        }

        public EmotionPresetType DefaultPreset => Plugin.Instance.Settings.GetEmotionPreset(hub);
        
        public long LastPacketTime { get; set; }
        
        public int LastLevel { get; private set; }



        // Use this for initialization
        void Awake () {
            player = Player.Get(GetComponent<ReferenceHub>());
            LastLevel = -2;
            buffer = new PlaybackBuffer(4096,endlessTapeMode:true);
            Proxy = null;
            hub.ServerSetEmotionPreset(DefaultPreset);
        }
	
        // Update is called once per frame
        void Update () {
            try
            {
                EmotionSubcontroller subcontroller;
                if (!(hub.roleManager.CurrentRole is IFpcRole currentRole) ||
                    !(currentRole.FpcModule.CharacterModelInstance is AnimatedCharacterModel
                        characterModelInstance) ||
                    !characterModelInstance.TryGetSubcontroller<EmotionSubcontroller>(out subcontroller))
                {
                    // Non-animated character model speaking, can delete self.
#if EXILED
                    Exiled.API.Features.Log.Debug("Removing SpeechTracker from non-animated model player " + player.Nickname);
#else
                    Logger.Debug("Removing SpeechTracker from non-animated model player " + player.Nickname, Plugin.Instance.Config.Debug);
#endif
                    Destroy(this);
                    return;
                }
                
                if (_overrideEndTime > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                {
                    //Currently in an override state, do nothing
                    return;
                } else if (_overrideEndTime != 0)
                {
                    //Just finished an override, need to reset to default preset
                    _overrideEndTime = 0;
                    LastLevel = -2;
                }
                
                //Updated with speech volume
                if (/*!player.IsSpeaking*/DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-LastPacketTime>Plugin.Instance.Config.EmotionResetTime)
                {
                    //Player has released talk button, should close mouth if they weren't done so already
                    if (LastLevel != -1)
                    {
                        hub.ServerSetEmotionPreset(DefaultPreset);
                        LastLevel = -1;
                    }
                    
                }
                else
                {
                    //Player is attempting to speak, need to check how loud they currently are to determine how their mouth should behave
                    
                    float volume = CalculateRMSVolume();
                    float dbVolume = 20f * Mathf.Log10(volume);
                    
                    int level = 0;
                    if ( dbVolume < Plugin.Instance.Config.LowDbThreshold)
                    {
                        level = 0;
                    }
                    else if (dbVolume >= Plugin.Instance.Config.HighDbThreshold)
                    {
                        level = 2;
                    }
                    else
                    {
                        level = 1;
                    }

                    if (level != LastLevel)
                    {
                        LastLevel = level;
                        switch (level)
                        {
                            case 0:
                                hub.ServerSetEmotionPreset(EmotionPresetType.Neutral);
                                break;
                            case 1:
                                hub.ServerSetEmotionPreset(EmotionPresetType.Happy);
                                break;
                            case 2:
                                hub.ServerSetEmotionPreset(EmotionPresetType.Scared);
                                break;
                        }
                    }
                }

            } catch (Exception e)
            {
                //Logger.Error(e);
            }
        }
        
        public float CalculateRMSVolume()
        {
            float sumOfSquares = 0f;
            foreach(float sample in buffer.Buffer)
            {
                sumOfSquares += sample * sample;
            }
            return Mathf.Sqrt(sumOfSquares / buffer.Buffer.Length);
        }
        
        /**
         * Called when a voice message is received from the player.
         */
        public void VoiceMessageReceived(byte[] data, int length)
        {
            if (Proxy != null)
            {
                if (!Proxy.ReferenceHub.TryGetComponent(out SpeechTracker tracker))
                {
                    return;
                }
                tracker.VoiceMessageReceived(data, length);
                return;
            }
            try
            {
                if (!(player.VoiceModule is HumanVoiceModule humanVoiceModule))
                {
                    return;
                }
                
                float[] samples = new float[1024]; //480
                int len = OpusDecoder.Decode(data, length, samples);
                buffer.Write(samples,len);
                LastPacketTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            } catch (Exception e)
            {
#if EXILED
                Exiled.API.Features.Log.Debug("Error decoding voice message: " + e);
#else
                Logger.Debug("Error decoding voice message: " + e, Plugin.Instance.Config.Debug);
#endif
            }
        }
        
        /**
         * Overrides the current emotion preset for a certain duration in milliseconds.
         * After the duration, the preset will revert to the user's default preset.
         */
        public void OverrideEmotion(EmotionPresetType preset, int durationMs)
        {
            _overridePreset = preset;
            _overrideEndTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + durationMs;
            hub.ServerSetEmotionPreset(_overridePreset);
#if EXILED
            Exiled.API.Features.Log.Debug("Overriding emotion preset to " + preset + " for " + durationMs + "ms");
#else
            Logger.Debug("Overriding emotion preset to " + preset + " for " + durationMs + "ms", Plugin.Instance.Config.Debug);
#endif
        }
        
    }
}