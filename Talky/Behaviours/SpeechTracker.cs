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
using PerformanceCounter = Talky.Performance.PerformanceCounter;

namespace Talky.Behaviours;

/// <summary>
/// Stores voice-animation state for one player; the shared runtime owns ticking.
/// </summary>
public sealed class SpeechTracker : MonoBehaviour
{
    private const int SpeechRangeObservationSamples = RollingVoiceEnergy.DefaultCapacity;
    private const float SpeechRangeContinuityGapSeconds = 0.25f;
    private const float MinimumAdaptiveVolumeRatio = 0.15f;
    private const int DecodeFailureThreshold = 8;
    private const float DecodeSuspensionSeconds = 10f;

    private readonly RollingVoiceEnergy _energy = new RollingVoiceEnergy();
    private readonly float[] _samples = new float[480];
    private readonly RollingSpeechPercentiles _speechPercentiles =
        new RollingSpeechPercentiles();
    private readonly VoicePresentationState _voicePresentation =
        new VoicePresentationState();

    private Player _player;
    private OpusDecoder _decoder;
    private SpeechLevel _lastLevel = SpeechLevel.Init;
    private EmotionPresetType _overridePreset = EmotionPresetType.Neutral;
    private float _overrideEndTime;
    private EmotionPresetType _appliedPreset;
    private bool _hasAppliedPreset;
    private bool _shouldAnimateFace = true;
    private int _decodeFailures;
    private float _decodeSuspendedUntil;
    private bool _disposed;
    private SpeechObservationCadence _speechRangeCadence =
        new SpeechObservationCadence(SpeechRangeObservationSamples);
    private float _lastSourcePacketTime = -1f;
    private AudibleSpeechActivity _audibleSpeechActivity;

    internal int RuntimeIndex { get; set; } = -1;
    internal int RecentSpeakerIndex { get; set; } = -1;
    internal bool IsDisposed => _disposed;
    internal bool HasGlanceSnapshot { get; private set; }
    internal Vector3 GlancePosition { get; private set; }
    internal Vector3 GlanceCameraPosition { get; private set; }
    internal bool LastPresentedVoiceWasAudible =>
        _audibleSpeechActivity.LastObservationWasAudible;

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
    public bool HasRollingSpeechRange => _speechPercentiles.IsReady;
    public int RollingSpeechObservationCount => _speechPercentiles.Count;
    public double RollingLowDecibels => _speechPercentiles.LowPercentileDecibels;
    public double RollingHighDecibels => _speechPercentiles.HighPercentileDecibels;

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

    
    private OpusDecoder OpusDecoder
    {
        get
        {
            if (_decoder != null)
                return _decoder;
            if (_disposed || _player == null || _player.IsDestroyed)
                return null;

            try
            {
                // Access relies on the publicized Assembly-CSharp reference and unsafe compilation.
                _decoder = Player.VoiceModule?.Decoder;
            }
            catch (Exception exception)
            {
                Logger.Debug(exception, Plugin.Instance?.Config?.Debug == true);
                return null;
            }

            return _decoder;
        }
    }

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
        ResetSpeechRangeObservationWindow();
        _voicePresentation.Reset();
        ResetAudibleSpeech();
        CaptureGlanceSnapshot();
    }

    /// <summary>
    /// Advances face state at the runtime-selected cadence.
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
            bool hasAdaptiveRange = _voicePresentation.HasAdaptiveRange;
            double fallbackLow = NormalizeSpeechFloor(lowThreshold);
            double fallbackHigh = NormalizeSpeechCeiling(highThreshold, fallbackLow);
            SpeechAnimationDecision animation = SpeechAnimationMath.Evaluate(
                dbVolume,
                fallbackLow,
                fallbackHigh,
                hasAdaptiveRange,
                _voicePresentation.PercentileLowDecibels,
                _voicePresentation.PercentileHighDecibels,
                _voicePresentation.AnimationLowDecibels,
                _voicePresentation.AnimationHighDecibels,
                MinimumAdaptiveVolumeRatio);
            CurrentVolumeRatio = animation.VolumeRatio;
            SpeechLevel level;
            switch (animation.Level)
            {
                case SpeechAnimationLevel.Silent:
                    level = SpeechLevel.Silent;
                    break;
                case SpeechAnimationLevel.Loud:
                    level = SpeechLevel.Loud;
                    break;
                default:
                    level = SpeechLevel.Quiet;
                    break;
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
    /// Forwards externally supplied voice data through the shared runtime.
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
        int decoded = DecodeVoicePacket(data, length, now);
        PresentVoiceFrom(this, decoded, now);
    }

    internal int DecodeVoicePacket(byte[] data, int length, float now)
    {
        // Opus reads the pinned buffer natively, so validate bounds before crossing the native boundary.
        if (_disposed || _player == null || _player.IsDestroyed ||
            data == null || length <= 0 || length > data.Length)
            return 0;

        if (now < _decodeSuspendedUntil)
            return 0;

        TalkyPerformanceMonitor monitor = Plugin.Instance?.PerformanceMonitor;
        monitor?.Count(PerformanceCounter.VoiceBytes, length);

        try
        {
            OpusDecoder decoder = OpusDecoder;
            if (decoder == null)
                return 0;

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

            if (decoded <= 0)
            {
                monitor?.Count(PerformanceCounter.VoiceDecodeFailures);
                RecordDecodeFailure(now);
                return 0;
            }

            long energyStarted = monitor?.Begin(PerformanceMetric.EnergyUpdate) ?? -1L;
            try
            {
                PrepareSpeechRangeWrite(now);
                _energy.Write(_samples, decoded);
                ObserveSpeechRange(decoded);
            }
            finally
            {
                monitor?.End(PerformanceMetric.EnergyUpdate, energyStarted);
            }

            _decodeFailures = 0;
            monitor?.Count(PerformanceCounter.VoiceDecodeSuccesses);
            return decoded;
        }
        catch (Exception exception)
        {
            monitor?.Count(PerformanceCounter.VoiceDecodeFailures);
            RecordDecodeFailure(now);
            // Avoid building per-packet diagnostics when debug logging is disabled.
            if (Plugin.Instance?.Config?.Debug == true)
                Logger.Debug("Voice decode failed: " + exception, true);
            return 0;
        }
    }

    // Back off after repeated decoder failures instead of retrying every packet.
    private void RecordDecodeFailure(float now)
    {
        if (++_decodeFailures < DecodeFailureThreshold)
            return;

        _decodeFailures = 0;
        _decodeSuspendedUntil = now + DecodeSuspensionSeconds;
    }

    internal bool PresentVoiceFrom(SpeechTracker source, int decodedSamples, float now)
    {
        if (_disposed || source == null || source._disposed)
            return false;

        double sourceRootMeanSquare = source.RootMeanSquare;
        bool presented = _voicePresentation.TryPresent(
            decodedSamples,
            sourceRootMeanSquare,
            source._speechPercentiles.LowPercentileDecibels,
            source._speechPercentiles.HighPercentileDecibels,
            source._speechPercentiles.AnimationLowDecibels,
            source._speechPercentiles.AnimationHighDecibels,
            now);
        if (!presented)
            return false;

        Settings.Config config = Plugin.Instance?.Config;
        double activityFloor = NormalizeSpeechFloor(config?.LowDbThreshold ?? -80d);
        bool audible = SpeechAnimationMath.IsAtOrAboveActivityFloor(
            source._energy.Decibels,
            activityFloor);
        _audibleSpeechActivity.Observe(now, audible);
        return true;
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
        return _audibleSpeechActivity.WasRecentlyAudible(
            now,
            durationMilliseconds);
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
        _speechPercentiles.Reset();
        ResetSpeechRangeObservationWindow();
        _voicePresentation.Reset();
        _decodeFailures = 0;
        _decodeSuspendedUntil = 0f;
        ResetAudibleSpeech();
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

        // Mark disposed before restoring the default face for live players.
        _disposed = true;
        try
        {
            Reset();
        }
        catch (Exception exception)
        {
            // Continue releasing ownership if the final emotion reset fails.
            Logger.Debug(exception, Plugin.Instance?.Config?.Debug == true);
        }
        finally
        {
            RuntimeIndex = -1;
            RecentSpeakerIndex = -1;
            Proxy = null;
        }
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

    private void PrepareSpeechRangeWrite(float now)
    {
        if (_lastSourcePacketTime < 0f ||
            now < _lastSourcePacketTime ||
            now - _lastSourcePacketTime > SpeechRangeContinuityGapSeconds)
        {
            _energy.Reset();
            _speechRangeCadence.Reset();
        }

        _lastSourcePacketTime = now;
    }

    private void ObserveSpeechRange(int decodedSamples)
    {
        if (decodedSamples <= 0)
            return;

        if (!_speechRangeCadence.Advance(decodedSamples))
            return;

        double decibels = _energy.Decibels;
        Settings.Config config = Plugin.Instance?.Config;
        double eligibilityFloor = NormalizeSpeechFloor(config?.LowDbThreshold ?? -80d);
        if (SpeechAnimationMath.IsAtOrAboveActivityFloor(decibels, eligibilityFloor))
            _speechPercentiles.Observe(decibels);
    }

    private void ResetSpeechRangeObservationWindow()
    {
        _speechRangeCadence.Reset();
        _lastSourcePacketTime = -1f;
    }

    private void ResetAudibleSpeech()
    {
        _audibleSpeechActivity.Reset();
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private static double NormalizeSpeechFloor(double decibels)
    {
        if (!IsFinite(decibels))
            return -80d;

        return Math.Max(
            RollingSpeechPercentiles.DefaultMinimumDecibels,
            Math.Min(RollingSpeechPercentiles.DefaultMaximumDecibels, decibels));
    }

    private static double NormalizeSpeechCeiling(double decibels, double floor)
    {
        if (!IsFinite(decibels))
            decibels = -30d;

        double ceiling = Math.Max(
            RollingSpeechPercentiles.DefaultMinimumDecibels,
            Math.Min(RollingSpeechPercentiles.DefaultMaximumDecibels, decibels));
        if (ceiling <= floor)
        {
            ceiling = Math.Min(
                RollingSpeechPercentiles.DefaultMaximumDecibels,
                floor + RollingSpeechPercentiles.DefaultMinimumSpanDecibels);
        }

        return ceiling;
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
