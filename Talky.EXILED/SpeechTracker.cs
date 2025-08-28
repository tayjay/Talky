using System;
using Exiled.API.Features;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.Thirdperson;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers;
using PlayerRoles.PlayableScps.Scp3114;
using UnityEngine;
using VoiceChat.Networking;

namespace Talky.EXILED;

public class SpeechTracker : MonoBehaviour
    {
        public Player player;
        public ReferenceHub hub => player.ReferenceHub;
        
        public PlaybackBuffer buffer;
        private EmotionPresetType _defaultPreset = EmotionPresetType.Neutral;
        
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
                /*if (player.Role == RoleTypeId.Tutorial)
                {
                    Logger.Debug($"Tutorial IsFpcRole: {hub.roleManager.CurrentRole is IFpcRole} IsAnimatedModel: {(hub.roleManager.CurrentRole as IFpcRole)?.FpcModule.CharacterModelInstance is AnimatedCharacterModel} HasEmotionSubcontroller: {((hub.roleManager.CurrentRole as IFpcRole)?.FpcModule.CharacterModelInstance as AnimatedCharacterModel)?.TryGetSubcontroller<EmotionSubcontroller>(out subcontroller)}");
                }*/
                if ((hub.roleManager.CurrentRole is IFpcRole currentSkeleRole) &&
                    (currentSkeleRole.FpcModule.CharacterModelInstance is Scp3114Model
                        skeleModelInstance) &&
                    skeleModelInstance.RpcTarget.TryGetSubcontroller<EmotionSubcontroller>(out EmotionSubcontroller skeleSubcontroller))
                {
                    // This is a skeleton
                    
                    
                }
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
                float volume = CalculateVolume();
                int level = 0;
                if (volume < Talky.EXILED.Plugin.Instance.Config.LowVolumeThreshold)
                {
                    level = 0;
                } else if (volume < Talky.EXILED.Plugin.Instance.Config.HighVolumeThreshold)
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
    }