using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using LabApi.Features.Wrappers;
using PlayerRoles.FirstPersonControl;
using Talky.Performance;
using Talky.Runtime;
using Talky.Settings;
using UnityEngine;

namespace Talky.Behaviours;

/// <summary>
/// Stores head-look state for one player; the shared runtime owns ticking.
/// </summary>
public sealed class LookOverride : MonoBehaviour
{
    private Player _player;
    private SpeechTracker _speechTracker;
    private Vector2 _headBobOffset;
    private Vector2 _glanceOffset;
    private Vector2 _reactOffset;
    private FpcMouseLook _serializedMouseLook;
    private FirstPersonMovementModule _serializedMovement;
    private float _lastBusyTime;
    private int _zeroCorrectionArmedFrame = -1;
    private bool _disposed;
    private bool _glanceSelectedOnLastTick;
    private bool _zeroCorrectionPending;
    private bool _zeroCorrectionObserved;

    internal int RuntimeIndex { get; set; } = -1;
    internal bool IsDisposed => _disposed;
    internal bool GlanceSelectedOnLastTick => _glanceSelectedOnLastTick;

    public Player Player => _player;
    public ReferenceHub Hub => _player?.ReferenceHub;
    public Vector2 NextLookOffset { get; private set; }
    public Vector2 LastLookOffset { get; private set; }
    public float LastManualLookTime { get; set; }

    public float LastBusyTime
    {
        get => _lastBusyTime;
        set
        {
            if (_disposed)
                return;

            _lastBusyTime = value;
            if (value > Time.time)
                ClearOffsets();
        }
    }

    public bool HasActiveOffsets =>
        NextLookOffset != Vector2.zero || LastLookOffset != Vector2.zero;

    /// <summary>
    /// Produces a look object used only while constructing outbound sync data.
    /// The authoritative movement module's mouse-look state is never modified.
    /// </summary>
    internal FpcMouseLook GetSerializedMouseLook(
        FpcMouseLook liveMouseLook,
        FirstPersonMovementModule movement,
        Vector2 offset,
        out bool allocated)
    {
        allocated = false;
        if (_disposed || liveMouseLook == null || movement == null)
        {
            return liveMouseLook;
        }

        ReferenceHub hub = Hub;
        if (hub == null || hub.isLocalPlayer)
            return liveMouseLook;

        float horizontal = liveMouseLook.CurrentHorizontal + offset.x;
        float vertical = liveMouseLook.CurrentVertical + offset.y;
        if (float.IsNaN(horizontal) || float.IsInfinity(horizontal) ||
            float.IsNaN(vertical) || float.IsInfinity(vertical))
        {
            return liveMouseLook;
        }

        if (_serializedMouseLook == null ||
            !ReferenceEquals(_serializedMovement, movement))
        {
            _serializedMouseLook = new FpcMouseLook(hub, movement);
            _serializedMovement = movement;
            allocated = true;
        }

        _serializedMouseLook.CurrentHorizontal = horizontal;
        _serializedMouseLook.CurrentVertical = vertical;
        return _serializedMouseLook;
    }

    /// <summary>
    /// Records that the pending zero correction reached an outbound sync path.
    /// Clearing remains deferred to a later look tick so the current fanout can
    /// present the correction to every receiver.
    /// </summary>
    internal void MarkZeroCorrectionObserved()
    {
        if (!_disposed &&
            _zeroCorrectionPending &&
            NextLookOffset == Vector2.zero &&
            LastLookOffset != Vector2.zero)
        {
            _zeroCorrectionObserved = true;
        }
    }

    /// <summary>
    /// Returns the current presentation offsets, expiring an observed or bounded
    /// zero correction even when ordinary look ticking is disabled by policy.
    /// </summary>
    internal bool TryGetSerializedLookOffsets(
        int currentFrame,
        out Vector2 nextOffset,
        out Vector2 previousOffset)
    {
        TryExpireZeroCorrection(currentFrame);

        nextOffset = NextLookOffset;
        previousOffset = LastLookOffset;
        return !_disposed &&
               (nextOffset != Vector2.zero || previousOffset != Vector2.zero);
    }

    /// <summary>
    /// Expires a bounded return-to-zero marker independently of outbound fanout.
    /// This prevents excluded-only targets from remaining active when head ticking
    /// is disabled by the performance policy.
    /// </summary>
    internal bool TryExpireZeroCorrection(int currentFrame)
    {
        if (!_zeroCorrectionPending ||
            ShouldRetainZeroCorrection(
                _zeroCorrectionObserved,
                _zeroCorrectionArmedFrame,
                currentFrame))
        {
            return false;
        }

        HardResetLookOffsets();
        return true;
    }

    private static Config TalkyConfig => Plugin.Instance?.Config;

    private void Awake()
    {
        _player = Player.Get(GetComponent<ReferenceHub>());
        Initialize(_player);
    }

    internal void Initialize(Player player)
    {
        if (player == null)
            return;

        _player = player;
        _disposed = false;
        ClearSerializedMouseLook();
        ClearOffsets(false);
        _lastBusyTime = Time.time;
        LastManualLookTime = Time.time;
    }

    internal void BindSpeechTracker(SpeechTracker tracker)
    {
        _speechTracker = tracker;
    }

    internal void OnSpawned(float now)
    {
        ClearSerializedMouseLook();
        ClearOffsets(false);
        _lastBusyTime = now;
        LastManualLookTime = now;
    }

    /// <summary>
    /// Advances head state using the caller's pruned recent-speaker list.
    /// </summary>
    public int TickLook(
        float now,
        float deltaTime,
        List<SpeechTracker> recentSpeakers,
        bool calculateGlance,
        TalkyRuntimePolicy policy)
    {
        return TickLook(
            now,
            deltaTime,
            recentSpeakers,
            calculateGlance,
            policy,
            false,
            out _);
    }

    internal int TickLook(
        float now,
        float deltaTime,
        List<SpeechTracker> recentSpeakers,
        bool calculateGlance,
        TalkyRuntimePolicy policy,
        bool measureGlance,
        out long glanceDurationTicks)
    {
        glanceDurationTicks = 0L;
        _glanceSelectedOnLastTick = false;
        if (_disposed || _player == null || policy == null)
            return 0;

        if (_zeroCorrectionPending)
        {
            if (!TryExpireZeroCorrection(Time.frameCount))
                return 0;
        }

        if (!policy.EnableHeadMotion)
        {
            ClearOffsets();
            return 0;
        }

        if (!(_player.RoleBase is IFpcRole role))
        {
            ClearOffsets();
            return 0;
        }

        float reactionLerp = Mathf.Clamp01(Mathf.Max(0f, deltaTime) * 3f);
        _reactOffset = _reactOffset.sqrMagnitude > 0.0025f
            ? Vector2.Lerp(_reactOffset, Vector2.zero, reactionLerp)
            : Vector2.zero;

        if (now - _lastBusyTime < 0.1f)
        {
            ClearOffsets();
            return 0;
        }

        if (role.FpcModule.Motor.RotationDetected)
        {
            Plugin.Instance?.PerformanceMonitor?.Count(
                PerformanceCounter.ManualLookDetections);
            Plugin.Instance?.PerformanceMonitor?.Count(
                PerformanceCounter.ManualLookSuppressedTicks);
            LastManualLookTime = now;
            ClearOffsets();
            return 0;
        }

        if (now - LastManualLookTime < 1f)
        {
            Plugin.Instance?.PerformanceMonitor?.Count(
                PerformanceCounter.ManualLookSuppressedTicks);
            ClearOffsets();
            return 0;
        }

        _headBobOffset = Vector2.zero;
        if (policy.EnableHeadBobbing)
            CalculateSpeechHeadBob(now);

        int glanceCandidates = 0;
        if (!policy.EnableGlancing)
        {
            _glanceOffset = Vector2.zero;
        }
        else if (calculateGlance)
        {
            _glanceOffset = Vector2.zero;
            if (measureGlance)
            {
                long glanceStarted = Stopwatch.GetTimestamp();
                try
                {
                    glanceCandidates = CalculateGlanceLook(recentSpeakers, now);
                }
                finally
                {
                    glanceDurationTicks = Stopwatch.GetTimestamp() - glanceStarted;
                }
            }
            else
            {
                glanceCandidates = CalculateGlanceLook(recentSpeakers, now);
            }
        }

        Vector2 goal = GetLookGoal();
        LastLookOffset = NextLookOffset;
        NextLookOffset = Vector2.Lerp(
            LastLookOffset,
            goal,
            Mathf.Clamp01(Mathf.Max(0f, deltaTime) * 7f));

        // Present zero while retaining the previously serialized offset for one
        // fanout. The next eligible look tick removes the correction marker.
        if (goal == Vector2.zero && NextLookOffset.sqrMagnitude <= 0.0001f)
        {
            NextLookOffset = Vector2.zero;
            if (IsFinite(LastLookOffset) && LastLookOffset != Vector2.zero)
                ArmZeroCorrection(LastLookOffset);
            else
                ClearZeroCorrectionTracking();
        }

        return glanceCandidates;
    }

    public void CauseReact(float damage)
    {
        Config config = TalkyConfig;
        if (_disposed || config == null ||
            Plugin.Instance?.Runtime?.IsEnabled != true)
            return;

        _reactOffset = new Vector2(0f, Math.Min(config.HeadBobAmount, damage));
    }

    public void ClearOffsets()
    {
        ClearOffsets(true);
    }

    internal void ClearGlanceOffset()
    {
        if (_glanceOffset == Vector2.zero)
            return;

        Vector2 previousOffset = CurrentCorrectionSource();
        _glanceOffset = Vector2.zero;
        ClearZeroCorrectionTracking();
        LastLookOffset = previousOffset;
        NextLookOffset = GetLookGoal();
        if (NextLookOffset == Vector2.zero &&
            IsFinite(previousOffset) &&
            previousOffset != Vector2.zero)
        {
            ArmZeroCorrection(previousOffset);
        }

        Plugin.Instance?.Runtime?.RefreshLookActivity(this);
    }

    internal void Reset()
    {
        ClearSerializedMouseLook();
        ClearOffsets(false);
        _lastBusyTime = Time.time;
        LastManualLookTime = Time.time;
    }

    internal void Dispose()
    {
        if (_disposed)
            return;

        // Mark disposed before teardown so stale callbacks are inert.
        _disposed = true;
        try
        {
            ClearOffsets(false);
        }
        finally
        {
            RuntimeIndex = -1;
            _speechTracker = null;
            ClearSerializedMouseLook();
        }
    }

    private void OnDestroy()
    {
        Dispose();
    }

    private void ClearOffsets(bool notifyRuntime)
    {
        Vector2 correctionSource = CurrentCorrectionSource();
        bool preservePendingCorrection =
            notifyRuntime &&
            _zeroCorrectionPending &&
            NextLookOffset == Vector2.zero &&
            IsFinite(LastLookOffset) &&
            LastLookOffset != Vector2.zero;

        _headBobOffset = Vector2.zero;
        _glanceOffset = Vector2.zero;
        _reactOffset = Vector2.zero;

        if (!notifyRuntime)
        {
            HardResetLookOffsets();
        }
        else if (!preservePendingCorrection)
        {
            if (IsFinite(correctionSource) && correctionSource != Vector2.zero)
                ArmZeroCorrection(correctionSource);
            else
                HardResetLookOffsets();
        }

        if (notifyRuntime)
            Plugin.Instance?.Runtime?.RefreshLookActivity(this);
    }

    private Vector2 CurrentCorrectionSource() =>
        NextLookOffset != Vector2.zero ? NextLookOffset : LastLookOffset;

    private void ArmZeroCorrection(Vector2 previousOffset)
    {
        LastLookOffset = previousOffset;
        NextLookOffset = Vector2.zero;
        _zeroCorrectionPending = true;
        _zeroCorrectionObserved = false;
        _zeroCorrectionArmedFrame = Time.frameCount;
    }

    private void HardResetLookOffsets()
    {
        LastLookOffset = Vector2.zero;
        NextLookOffset = Vector2.zero;
        ClearZeroCorrectionTracking();
    }

    private void ClearZeroCorrectionTracking()
    {
        _zeroCorrectionPending = false;
        _zeroCorrectionObserved = false;
        _zeroCorrectionArmedFrame = -1;
    }

    internal static bool ShouldRetainZeroCorrection(
        bool observed,
        int armedFrame,
        int currentFrame)
    {
        uint frameAge = unchecked((uint)(currentFrame - armedFrame));
        return frameAge <= 1u && (!observed || frameAge == 0u);
    }

    private static bool IsFinite(Vector2 value) =>
        !float.IsNaN(value.x) &&
        !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) &&
        !float.IsInfinity(value.y);

    private void ClearSerializedMouseLook()
    {
        _serializedMouseLook = null;
        _serializedMovement = null;
    }

    private void CalculateSpeechHeadBob(float now)
    {
        Config config = TalkyConfig;
        SpeechTracker tracker = _speechTracker;
        if (config == null || tracker == null || !tracker.ShouldHeadBob ||
            !tracker.WasRecentlySpeaking(now, config.EmotionResetTime) ||
            tracker.CurrentVolumeRatio <= 0f)
        {
            return;
        }

        _headBobOffset = new Vector2(
            0f,
            tracker.CurrentHeadBobDrive * config.HeadBobAmount);
    }

    private int CalculateGlanceLook(List<SpeechTracker> recentSpeakers, float now)
    {
        Config config = TalkyConfig;
        if (config == null || recentSpeakers == null || recentSpeakers.Count == 0 ||
            _player.Camera == null)
        {
            return 0;
        }

        float maxDistance = config.GlaceRange;
        if (maxDistance <= 0f)
            return 0;

        float minFrontDot = Mathf.Cos(0.5f * config.GlaceFov * Mathf.Deg2Rad);
        float maxDistSqr = maxDistance * maxDistance;
        Vector3 myPos = _player.Position;
        Transform camera = _player.Camera;
        Vector3 cameraPosition = camera.position;
        Vector3 cameraForward = camera.forward;

        bool found = false;
        Vector3 bestDirection = default;
        Vector3 bestCameraPosition = default;
        float bestFrontDot = 0f;
        float bestScore = float.MinValue;
        int candidates = 0;

        for (int i = 0; i < recentSpeakers.Count; i++)
        {
            SpeechTracker tracker = recentSpeakers[i];
            if (tracker == null || tracker.Player == null ||
                tracker.Player.NetworkId == _player.NetworkId ||
                !tracker.HasGlanceSnapshot ||
                !tracker.WasRecentlySpeaking(now, config.GlanceMaxDuration))
            {
                continue;
            }

            candidates++;

            Vector3 positionDelta = tracker.GlancePosition - myPos;
            float distanceSqr = positionDelta.sqrMagnitude;
            if (distanceSqr > maxDistSqr)
                continue;

            Vector3 cameraDelta = tracker.GlanceCameraPosition - cameraPosition;
            float cameraDistanceSqr = cameraDelta.sqrMagnitude;
            if (cameraDistanceSqr <= 0.000001f)
                continue;

            float inverseCameraDistance = 1f / Mathf.Sqrt(cameraDistanceSqr);
            Vector3 direction = cameraDelta * inverseCameraDistance;
            float frontDot = Vector3.Dot(cameraForward, direction);
            if (frontDot < minFrontDot)
                continue;

            float distance = Mathf.Sqrt(distanceSqr);
            float score = 0.8f * frontDot - 0.2f * (distance / maxDistance);
            if (score <= bestScore)
                continue;

            bestScore = score;
            bestDirection = direction;
            bestCameraPosition = tracker.GlanceCameraPosition;
            bestFrontDot = frontDot;
            found = true;
        }

        if (!found)
            return candidates;

        _glanceSelectedOnLastTick = true;

        Vector3 localDirection = camera.InverseTransformDirection(bestDirection);
        float yaw = Mathf.Atan2(localDirection.x, localDirection.z) * Mathf.Rad2Deg;
        float pitch = Mathf.Asin(Mathf.Clamp(localDirection.y, -1f, 1f)) * Mathf.Rad2Deg;

        float distanceToTarget = (myPos - bestCameraPosition).magnitude;
        float proximity = 1f - Mathf.Clamp01(distanceToTarget / maxDistance);
        float distanceFactor = proximity * proximity * (3f - 2f * proximity);
        float frontFactor = Mathf.InverseLerp(minFrontDot, 1f, bestFrontDot);
        float frontBias = Mathf.Sqrt(Mathf.Max(0f, frontFactor));
        float intensity = Mathf.Clamp01(config.GlaceGain * distanceFactor * frontBias);

        _glanceOffset = new Vector2(
            Mathf.Clamp(yaw, -config.GlanceMaxYaw, config.GlanceMaxYaw),
            Mathf.Clamp(pitch, -config.GlanceMaxPitch, config.GlanceMaxPitch)) * intensity;
        return candidates;
    }

    private Vector2 GetLookGoal() => _headBobOffset + _glanceOffset + _reactOffset;
}
