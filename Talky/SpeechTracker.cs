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
        
        private OpusDecoder _opusDecoder;

        public OpusDecoder OpusDecoder
        {
            get
            {
                if (_opusDecoder == null)
                {
                    // Use reflection to get the decoder
                    var decoderProperty = typeof(VoiceModuleBase).GetProperty(
                        "Decoder",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
                    );
                    if (decoderProperty == null)
                    {
                        return null;
                    }
                    _opusDecoder = decoderProperty.GetValue(player.VoiceModule) as OpusDecoder;
                    Debug.Assert(_opusDecoder != null, nameof(_opusDecoder) + " != null");
                }
                return _opusDecoder;
            }
        }
        
        public EmotionPresetType DefaultPreset
        {
            get => _defaultPreset;
            set
            {
                _defaultPreset = value;
            }
        }
        
        public long LastPacketTime { get; set; }
        
        public int LastLevel { get; private set; }
        public float TopVolume { get; private set; }
        
        private float lowThreshold = 0.01f;
        private float highThreshold = 0.1f;

        // Use this for initialization
        void Awake () {
            player = Player.Get(GetComponent<ReferenceHub>());
            LastLevel = -2;
            buffer = new PlaybackBuffer(4096,endlessTapeMode:true);
            TopVolume = 0.03f;
            if(Enum.TryParse<EmotionPresetType>(Plugin.Instance.Config!.DefaultEmotion, out var preset))
            {
                DefaultPreset = preset;
                player.ReferenceHub.ServerSetEmotionPreset(DefaultPreset);
            }
            else
            {
                DefaultPreset = EmotionPresetType.Neutral;
            }
            
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

                    float volume = 0;
                    if (Plugin.Instance.Config.CalculationType == "Average")
                    {
                        volume = CalculateVolume();
                    } else if (Plugin.Instance.Config.CalculationType == "Peak")
                    {
                        volume = CalculatePeakVolume();
                    }
                    else
                    {
                        volume = CalculateVolume();
                    }
                    
                    int level = 0;
                    if (volume < Plugin.Instance.Config.LowVolumeThreshold)
                    {
                        level = 0;
                    } else if (volume < Plugin.Instance.Config.HighVolumeThreshold)
                    {
                        level = 1;
                    }
                    else
                    {
                        level = 2;
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



        public float CalculateVolume()
        {
            float absTotal = 0;
            foreach(float sample in buffer.Buffer)
            {
                absTotal += Mathf.Abs(sample);
            }
            return absTotal / buffer.Buffer.Length;
        }
        
        public float CalculatePeakVolume()
        {
            float peak = 0f;
            foreach(float sample in buffer.Buffer)
            {
                float absSample = Mathf.Abs(sample);
                if (absSample > peak)
                {
                    peak = absSample;
                }
            }
            return peak;
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