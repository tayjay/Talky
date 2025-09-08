using System;
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

namespace Talky
{
    public class SpeechTracker : MonoBehaviour
    {
        public Player player;
        public ReferenceHub hub => player.ReferenceHub;
        
        public PlaybackBuffer buffer;
        private EmotionPresetType _defaultPreset = EmotionPresetType.Neutral;
        

        public OpusDecoder OpusDecoder
        {
            get
            {
                return player.VoiceModule.Decoder;
            }
        }

        public EmotionPresetType DefaultPreset => Plugin.Instance.settings.GetEmotionPreset(hub);
        /*{
            get => _defaultPreset;
            set
            {
                _defaultPreset = value;
            }
        }*/
        
        public long LastPacketTime { get; set; }
        
        public int LastLevel { get; private set; }
        public float TopVolume { get; private set; }
        
        public float HighestVolume { get; private set; }    


        // Use this for initialization
        void Awake () {
            player = Player.Get(GetComponent<ReferenceHub>());
            LastLevel = -2;
            buffer = new PlaybackBuffer(4096,endlessTapeMode:true);
            TopVolume = 0.03f;
            HighestVolume = 0f;
            hub.ServerSetEmotionPreset(DefaultPreset);
            /*if(Enum.TryParse<EmotionPresetType>(Plugin.Instance.Config!.DefaultEmotion, out var preset))
            {
                DefaultPreset = preset;
                player.ReferenceHub.ServerSetEmotionPreset(DefaultPreset);
            }
            else
            {
                DefaultPreset = EmotionPresetType.Neutral;
            }*/
            
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
                    // Non-animated character model speaking
                    
                    return;
                }
                
                //Updated with speech volume
                if (/*!player.IsSpeaking*/DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-LastPacketTime>Plugin.Instance.Config.EmotionResetTime)
                {
                    //Player has released talk button, should close mouth if they weren't done so already
                    if (LastLevel != -1)
                    {
                        hub.ServerSetEmotionPreset(DefaultPreset);
                        LastLevel = -1;
                        //Logger.Info("Set "+ DefaultPreset+" for " + player.Nickname);
                    }
                    
                }
                else
                {
                    //Player is attempting to speak, need to check how loud they currently are to determine how their mouth should behave
                    
                    float volume = CalculateRMSVolume();
                    float dbVolume = 20f * Mathf.Log10(volume);
                    //Logger.Debug("Volume: " + volume + " dB: " + dbVolume + " for " + player.Nickname);
                    
                    
                    
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
        
        
        public void VoiceMessageReceived(byte[] data, int length)
        {
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
                //Logger.Error(e);
            }
        }
    }
}