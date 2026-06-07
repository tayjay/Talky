using System;
using LabApi.Features.Wrappers;
using PlayerRoles;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers;
using UnityEngine;
using VoiceChat.Codec;
using VoiceChat.Networking;
using Logger = LabApi.Features.Console.Logger;

namespace Talky.Behaviours;

/**
 * Tracks a player's speech and adjusts their mouth animations accordingly.
 */
public class SpeechTracker : MonoBehaviour
{
    private Player _player;

    private ReferenceHub Hub => _player.ReferenceHub;
    public Player Proxy { get; set; }
        
    private PlaybackBuffer _buffer;

    private float[] _samples;

    // Throttling - 30Hz is enough for mouth animations
    private float _lastUpdateTime;

    private EmotionPresetType _overridePreset = EmotionPresetType.Neutral;
    private float _overrideEndTime;

    private OpusDecoder OpusDecoder => _player.VoiceModule?.Decoder;

    public EmotionPresetType defaultPreset;
        
    public float LastPacketTime { get; set; }

    private SpeechLevel LastLevel { get; set; }
        
    public float CurrentVolumeRatio { get; set; }
        
    public bool ShouldHeadBob
    {
        get
        {
            if(!Plugin.Instance.Config.EnableHeadBobbing)
            {
                return false;
            }
            if (_player.IsHuman || _player.Role==RoleTypeId.Scp3114) // || player.Role == RoleTypeId.Scp953
            {
                return true;
            }
            return false;
        }
    }

    public bool ShouldAnimateFace { get; set; }


    // Use this for initialization
    private void Awake () {
        _player = Player.Get(GetComponent<ReferenceHub>());
        ShouldAnimateFace = Plugin.Instance.Settings.GetEnableTalking(_player?.ReferenceHub);
        LastLevel = SpeechLevel.Init;
        _buffer = new PlaybackBuffer(4800,endlessTapeMode:true);
        Proxy = null;
        defaultPreset = Plugin.Instance.Settings.GetEmotionPreset(Hub);
        _player?.Emotion = defaultPreset;
        _samples  = new float[480]; // 480 per 10ms voice packet
    }
	
    // Update is called once per frame
    void Update () {
        try
        {

            // Throttle updates to 30Hz - mouth animations don't need 60+ fps
            if (Time.time - _lastUpdateTime < Plugin.Instance.Config.SpeechUpdateInterval)
                return;
            _lastUpdateTime = Time.time;

            if (_overrideEndTime > Time.time)
            {
                //Currently in an override state, do nothing
                return;
            } else if (_overrideEndTime != 0)
            {
                //Just finished an override, need to reset to default preset
                _overrideEndTime = 0;
                LastLevel = SpeechLevel.Init;
            }

            // Fix default preset not applying on spawn.
            if (LastLevel == SpeechLevel.Init)
            {
                if(ShouldAnimateFace) _player.Emotion = defaultPreset;
                LastLevel = SpeechLevel.Reset;
            }

            //Updated with speech volume
            if ((Time.time - LastPacketTime) * 1000 > Plugin.Instance.Config.EmotionResetTime)
            {
                //Player has released talk button, should close mouth if they weren't done so already
                if (LastLevel != SpeechLevel.Reset)
                {
                    //hub.ServerSetEmotionPreset(DefaultPreset);
                    if(ShouldAnimateFace) _player.Emotion = defaultPreset;
                    LastLevel = SpeechLevel.Reset;
                    CurrentVolumeRatio = 0;
                }
                    
            }
            else
            {
                //Player is attempting to speak, need to check how loud they currently are to determine how their mouth should behave
                float dbVolume = 20f * Mathf.Log10(CalculateRmsVolume());
                    
                SpeechLevel level;
                if ( dbVolume < Plugin.Instance.Config.LowDbThreshold)
                {
                    level = SpeechLevel.Silent;
                    CurrentVolumeRatio = 0;
                }
                else if (dbVolume >= Plugin.Instance.Config.HighDbThreshold)
                {
                    level = SpeechLevel.Loud;
                    CurrentVolumeRatio = 1f;
                }
                else
                {
                    level = SpeechLevel.Quiet;
                    CurrentVolumeRatio = (float)((dbVolume - Plugin.Instance.Config.LowDbThreshold) / (Plugin.Instance.Config.HighDbThreshold - Plugin.Instance.Config.LowDbThreshold));
                }

                if (level != LastLevel)
                {
                    LastLevel = level;
                    if (!ShouldAnimateFace) return;
                    switch (level)
                    {
                        case SpeechLevel.Silent:
                            _player.Emotion = EmotionPresetType.Neutral;
                            break;
                        case SpeechLevel.Quiet:
                            _player.Emotion = EmotionPresetType.Happy;
                            break;
                        case SpeechLevel.Loud:
                            _player.Emotion = EmotionPresetType.Scared;
                            break;
                    }
                }
            }

        } catch (Exception e)
        {
            Logger.Debug(e, Plugin.Instance.Config.Debug);
        }
    }

    private float CalculateRmsVolume()
    {
        float sumOfSquares = 0f;
        foreach(float sample in _buffer.Buffer)
        {
            sumOfSquares += sample * sample;
        }
        return Mathf.Sqrt(sumOfSquares / _buffer.Buffer.Length);
    }
        
    /**
     * Called when a voice message is received from the player.
     */
    public void VoiceMessageReceived(byte[] data, int length)
    {
        if (Proxy != null)
        {
            if (!Plugin.Instance.VoiceChattingHandler.SpeechTrackerCache.TryGetValue(Proxy.NetworkId, out SpeechTracker tracker))
            {
                return;
            }
            tracker.VoiceMessageReceived(data, length);
            return;
        }
            
        try
        {
            int len = OpusDecoder.Decode(data, length, _samples);
            _buffer.Write(_samples, len);
            LastPacketTime = Time.time;
        } catch (Exception e)
        {
            Logger.Debug("Error decoding voice message: " + e, Plugin.Instance.Config.Debug);
        }
    }
        
    /**
     * Overrides the current emotion preset for a certain duration in milliseconds.
     * After the duration, the preset will revert to the user's default preset.
     */
    public void OverrideEmotion(EmotionPresetType preset, int durationMs)
    {
        OverrideEmotion(preset, durationMs / 1000f);
    }
        
    public void OverrideEmotion(EmotionPresetType preset, float durationSeconds)
    {
        if(!ShouldAnimateFace) return;
        _overridePreset = preset;
        _overrideEndTime = Time.time + durationSeconds;
        _player.Emotion = _overridePreset;
    }


    private enum SpeechLevel
    {
        Init = -2,
        Reset = -1,
        Silent = 0,
        Quiet = 1,
        Loud = 2
    }
        
}