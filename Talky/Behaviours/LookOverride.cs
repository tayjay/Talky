using System;
using System.Collections.Generic;
using LabApi.Features.Wrappers;
using PlayerRoles.FirstPersonControl;
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
    private float _lastBusyTime;
    private bool _disposed;
    private bool _glanceSelectedOnLastTick;

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
        _glanceSelectedOnLastTick = false;
        if (_disposed || _player == null || policy == null)
            return 0;

        if (!policy.EnableHeadMotion)
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

        if (_player.RoleBase is IFpcRole role && role.FpcModule.Motor.RotationDetected)
        {
            LastManualLookTime = now;
            ClearOffsets();
            return 0;
        }

        if (now - LastManualLookTime < 1f)
        {
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
            glanceCandidates = CalculateGlanceLook(recentSpeakers, now);
        }

        Vector2 goal = GetLookGoal();
        LastLookOffset = NextLookOffset;
        NextLookOffset = Vector2.Lerp(
            LastLookOffset,
            goal,
            Mathf.Clamp01(Mathf.Max(0f, deltaTime) * 7f));

        // Snap idle offsets to zero so inactive targets can be removed.
        if (goal == Vector2.zero && NextLookOffset.sqrMagnitude <= 0.0001f)
        {
            LastLookOffset = Vector2.zero;
            NextLookOffset = Vector2.zero;
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

        _glanceOffset = Vector2.zero;
        LastLookOffset = NextLookOffset;
        NextLookOffset = GetLookGoal();
        Plugin.Instance?.Runtime?.RefreshLookActivity(this);
    }

    internal void Reset()
    {
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
        }
    }

    private void OnDestroy()
    {
        Dispose();
    }

    private void ClearOffsets(bool notifyRuntime)
    {
        _headBobOffset = Vector2.zero;
        _glanceOffset = Vector2.zero;
        _reactOffset = Vector2.zero;
        LastLookOffset = Vector2.zero;
        NextLookOffset = Vector2.zero;

        if (notifyRuntime)
            Plugin.Instance?.Runtime?.RefreshLookActivity(this);
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
            (tracker.CurrentVolumeRatio - 0.25f) * config.HeadBobAmount);
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
