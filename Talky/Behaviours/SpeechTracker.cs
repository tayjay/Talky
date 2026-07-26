using System;
using LabApi.Features.Wrappers;
using PlayerRoles;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers;
using Talky.Performance;
using Talky.Settings;
using Talky.Voice;
using UnityEngine;
using VoiceChat.Codec;
using Logger = LabApi.Features.Console.Logger;

namespace Talky.Behaviours;

/// <summary>
/// Holds the voice-animation state for one player. The shared <see cref="Talky.Runtime.TalkyRuntime"/>
/// ticks this component; it deliberately has no Unity Update method.
/// </summary>
public sealed class SpeechTracker : MonoBehaviour
{
    private readonly RollingVoiceEnergy _energy = new RollingVoiceEnergy(4800);
    private readonly float[] _samples = new float[480];
    private readonly VoicePresentationState _voicePresentation =
        new VoicePresentationState();

    private Player _player;
    private SpeechLevel _lastLevel = SpeechLevel.Init;
    private EmotionPresetType _overridePreset = EmotionPresetType.Neutral;
    private float _overrideEndTime;
    private EmotionPresetType _appliedPreset;
    private bool _hasAppliedPreset;
    private bool _shouldAnimateFace = true;
    private bool _disposed;

    internal int RuntimeIndex { get; set; } = -1;
    internal int RecentSpeakerIndex { get; set; } = -1;
    internal bool IsDisposed => _disposed;
    internal bool HasGlanceSnapshot { get; private set; }
    internal Vector3 GlancePosition { get; private set; }
    internal Vector3 GlanceCameraPosition { get; private set; }

    public Player Player => _player;
    public ReferenceHub Hub => _player?.ReferenceHub;
    public Player Proxy { get; set; }
    public EmotionPresetType defaultPreset = EmotionPresetType.Neutral;
    public float LastPacketTime
    {
        get => (float)_voicePresentation.LastActivitySeconds;
        set => _voicePresentation.SetLastActivitySeconds(value);
    }
    public float CurrentVolumeRatio { get; private set; }
    public double RootMeanSquare => _energy.RootMeanSquare;

    public bool ShouldAnimateFace
    {
        get => _shouldAnimateFace;
        set
        {
            if (_shouldAnimateFace == value)
                return;

            _shouldAnimateFace = value;
            if (value)
            {
                _hasAppliedPreset = false;
                _lastLevel = SpeechLevel.Init;
            }
        }
    }

    public bool HasRecentSpeech
    {
        get
        {
            ConfigSnapshot(out int resetTime, out _, out _);
            return WasRecentlySpeaking(Time.time, resetTime);
        }
    }

    public bool ShouldHeadBob
    {
        get
        {
            if (_player == null || Plugin.Instance?.Config?.EnableHeadBobbing != true)
                return false;

            return _player.IsHuman || _player.Role == RoleTypeId.Scp3114;
        }
    }

    private OpusDecoder OpusDecoder => _player?.VoiceModule?.Decoder;

    private void Awake()
    {
        _player = Player.Get(GetComponent<ReferenceHub>());
    }

    internal void Initialize(Player player)
    {
        if (player == null)
            return;

        _disposed = false;
        _player = player;
        RefreshSettings();
    }

    internal void RefreshSettings()
    {
        if (_player?.ReferenceHub == null)
            return;

        try
        {
            SSTalkySettings settings = Plugin.Instance?.Settings;
            if (settings != null)
            {
                ShouldAnimateFace = settings.GetEnableTalking(_player.ReferenceHub);
                defaultPreset = settings.GetEmotionPreset(_player.ReferenceHub);
            }
        }
        catch (Exception exception)
        {
            Logger.Debug(exception, Plugin.Instance?.Config?.Debug == true);
        }

        _lastLevel = SpeechLevel.Init;
        _hasAppliedPreset = false;
    }

    internal void OnSpawned(float now)
    {
        RefreshSettings();
        OverrideEmotion(defaultPreset, 0.1f);
        LastPacketTime = -1f;
        CurrentVolumeRatio = 0f;
        _energy.Reset();
        _voicePresentation.Reset();
        CaptureGlanceSnapshot();
    }

    /// <summary>
    /// Called by the central runtime at the currently effective face cadence.
    /// </summary>
    public void TickSpeech(float now)
    {
        ConfigSnapshot(out int resetTime, out double lowThreshold, out double highThreshold);
        TickSpeech(now, resetTime, lowThreshold, highThreshold);
    }

    internal void TickSpeech(
        float now,
        int resetTime,
        double lowThreshold,
        double highThreshold)
    {
        if (_disposed || _player == null)
            return;

        try
        {
            if (_overrideEndTime > now)
                return;

            if (_overrideEndTime != 0f)
            {
                _overrideEndTime = 0f;
                _lastLevel = SpeechLevel.Init;
            }

            if (_lastLevel == SpeechLevel.Init)
            {
                ApplyEmotion(defaultPreset);
                _lastLevel = SpeechLevel.Reset;
            }

            if (!WasRecentlySpeaking(now, resetTime))
            {
                if (_lastLevel != SpeechLevel.Reset || CurrentVolumeRatio != 0f)
                {
                    ApplyEmotion(defaultPreset);
                    _lastLevel = SpeechLevel.Reset;
                    CurrentVolumeRatio = 0f;
                }

                return;
            }

            double rms = _voicePresentation.RootMeanSquare;
            double dbVolume = rms > 0d ? 20d * Math.Log10(rms) : double.NegativeInfinity;
            SpeechLevel level;

            if (dbVolume < lowThreshold)
            {
                level = SpeechLevel.Silent;
                CurrentVolumeRatio = 0f;
            }
            else if (dbVolume >= highThreshold)
            {
                level = SpeechLevel.Loud;
                CurrentVolumeRatio = 1f;
            }
            else
            {
                level = SpeechLevel.Quiet;
                double thresholdSpan = highThreshold - lowThreshold;
                CurrentVolumeRatio = thresholdSpan > 0d
                    ? Mathf.Clamp01((float)((dbVolume - lowThreshold) / thresholdSpan))
                    : 1f;
            }

            if (level == _lastLevel)
                return;

            _lastLevel = level;
            switch (level)
            {
                case SpeechLevel.Silent:
                    ApplyEmotion(EmotionPresetType.Neutral);
                    break;
                case SpeechLevel.Quiet:
                    ApplyEmotion(EmotionPresetType.Happy);
                    break;
                case SpeechLevel.Loud:
                    ApplyEmotion(EmotionPresetType.Scared);
                    break;
            }
        }
        catch (Exception exception)
        {
            Logger.Debug(exception, Plugin.Instance?.Config?.Debug == true);
        }
    }

    /// <summary>
    /// Compatibility entry point. Normal voice traffic enters through <see cref="Talky.Runtime.TalkyRuntime"/>.
    /// </summary>
    public void VoiceMessageReceived(byte[] data, int length)
    {
        Talky.Runtime.TalkyRuntime runtime = Plugin.Instance?.Runtime;
        if (runtime != null)
        {
            runtime.HandleVoice(this, data, length);
            return;
        }

        float now = Time.time;
        int decoded = DecodeVoicePacket(data, length);
        PresentVoiceFrom(this, decoded, now);
    }

    internal int DecodeVoicePacket(byte[] data, int length)
    {
        if (_disposed || _player == null || _player.IsDestroyed ||
            data == null || length <= 0)
            return 0;

        try
        {
            OpusDecoder decoder = OpusDecoder;
            if (decoder == null)
                return 0;

            TalkyPerformanceMonitor monitor = Plugin.Instance?.PerformanceMonitor;
            long decodeStarted = monitor?.Begin(PerformanceMetric.OpusDecode) ?? -1L;
            int decoded;
            try
            {
                decoded = decoder.Decode(data, length, _samples);
            }
            finally
            {
                monitor?.End(PerformanceMetric.OpusDecode, decodeStarted);
            }

            if (decoded > 0)
            {
                long energyStarted = monitor?.Begin(PerformanceMetric.EnergyUpdate) ?? -1L;
                try
                {
                    _energy.Write(_samples, decoded);
                }
                finally
                {
                    monitor?.End(PerformanceMetric.EnergyUpdate, energyStarted);
                }
            }

            return decoded;
        }
        catch (Exception exception)
        {
            Logger.Debug("Error decoding voice message: " + exception, Plugin.Instance?.Config?.Debug == true);
            return 0;
        }
    }

    internal bool PresentVoiceFrom(SpeechTracker source, int decodedSamples, float now)
    {
        if (_disposed || source == null || source._disposed)
            return false;

        return _voicePresentation.TryPresent(decodedSamples, source.RootMeanSquare, now);
    }

    public void OverrideEmotion(EmotionPresetType preset, int durationMs) =>
        OverrideEmotion(preset, durationMs / 1000f);

    public void OverrideEmotion(EmotionPresetType preset, float durationSeconds)
    {
        if (!ShouldAnimateFace || _disposed)
            return;

        _overridePreset = preset;
        _overrideEndTime = Time.time + Mathf.Max(0f, durationSeconds);
        ApplyEmotion(_overridePreset);
    }

    internal bool WasRecentlySpeaking(float now, float durationMilliseconds)
    {
        return _voicePresentation.WasRecentlyActive(now, durationMilliseconds);
    }

    internal void CaptureGlanceSnapshot()
    {
        HasGlanceSnapshot = false;
        if (_player == null || _player.ReferenceHub == null || _player.Camera == null)
            return;

        GlancePosition = _player.Position;
        GlanceCameraPosition = _player.Camera.position;
        HasGlanceSnapshot = true;
    }

    public void Reset()
    {
        _energy.Reset();
        _voicePresentation.Reset();
        CurrentVolumeRatio = 0f;
        _overrideEndTime = 0f;
        _lastLevel = SpeechLevel.Init;
        _hasAppliedPreset = false;
        RecentSpeakerIndex = -1;
        HasGlanceSnapshot = false;
        ApplyEmotion(defaultPreset);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Reset();
        _disposed = true;
        RuntimeIndex = -1;
        RecentSpeakerIndex = -1;
        Proxy = null;
    }

    private void OnDestroy()
    {
        Dispose();
    }

    private void ApplyEmotion(EmotionPresetType preset)
    {
        if (!ShouldAnimateFace || _player == null)
            return;

        if (_hasAppliedPreset && _appliedPreset == preset)
            return;

        _player.Emotion = preset;
        _appliedPreset = preset;
        _hasAppliedPreset = true;
        Plugin.Instance?.Runtime?.RecordEmotionWrite();
    }

    private static void ConfigSnapshot(out int resetTime, out double lowThreshold, out double highThreshold)
    {
        Settings.Config config = Plugin.Instance?.Config;
        resetTime = config?.EmotionResetTime ?? 500;
        lowThreshold = config?.LowDbThreshold ?? -80d;
        highThreshold = config?.HighDbThreshold ?? -30d;
    }

    private enum SpeechLevel
    {
        Init = -2,
        Reset = -1,
        Silent = 0,
        Quiet = 1,
        Loud = 2,
    }
}
