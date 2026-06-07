using System;
using LabApi.Features.Wrappers;
using PlayerRoles.FirstPersonControl;
using UnityEngine;

namespace Talky;

public class LookOverride : MonoBehaviour
{
    private Player _player;

    public ReferenceHub Hub => _player.ReferenceHub;

    private Vector2 _headBobOffset;
    private Vector2 _glanceOffset;
    private Vector2 _reactOffset;

    // Throttling for glance calculation
    private float _lastGlanceCalcTime;
    private const float GlanceCalcInterval = 0.05f; // 20Hz instead of every frame

    //public SpeechTracker speechTracker;

    private static Config TalkyConfig => Plugin.Instance.Config;

    public Vector2 NextLookOffset { get; private set; }
    public Vector2 LastLookOffset { get; private set; }

    public float LastBusyTime { get; set; }
    public float LastManualLookTime { get; set; }

    //public float Attention;


    private void Awake()
    {
        _player = Player.Get(GetComponent<ReferenceHub>());
        _headBobOffset = Vector2.zero;
        _glanceOffset = Vector2.zero;
        _reactOffset = Vector2.zero;
        //speechTracker = GetComponent<SpeechTracker>();
        LastLookOffset = Vector2.zero;
        LastBusyTime = Time.time;
        LastManualLookTime = Time.time;
        //Reflex = TalkyConfig.GlanceDynamicTraits ? Random.Range(0.9f, 1.25f) : 1f; // How quickly a player will look at a target compared to others
        //Attention = TalkyConfig.GlanceDynamicTraits ? Random.Range(0.9f, 1.25f) : 1f; // How long a player will look at talking players compared to others
    }

    private void Update()
    {
        _reactOffset = Vector3.Distance(_reactOffset,Vector3.zero) > 0.05f ? Vector3.Lerp(_reactOffset, Vector3.zero, Time.deltaTime*3) : Vector3.zero;
        if (Time.time - LastBusyTime < 0.1f)
        {
            _glanceOffset = Vector2.zero;
            _headBobOffset = Vector2.zero;
            return;
        }

        // Head bobbing based on speech volume (runs every frame for smooth animation)
        _headBobOffset = Vector2.zero;
        if (TalkyConfig.EnableHeadBobbing)
            CalculateSpeechHeadBob();
        
        if ((_player.RoleBase is IFpcRole role) && role.FpcModule.Motor.RotationDetected)
        {
            _glanceOffset = Vector2.zero;
            LastManualLookTime  = Time.time;
        }
        
        if (Time.time  - LastManualLookTime < 1f) return;
        
        // Glance at nearby speaking players (throttled to reduce CPU usage)
        if (TalkyConfig.EnableGlancing && Time.time - _lastGlanceCalcTime >= GlanceCalcInterval)
        {
            _glanceOffset = Vector2.zero;
            CalculateGlanceLook();
            _lastGlanceCalcTime = Time.time;
        }
        LastLookOffset = NextLookOffset;
        NextLookOffset = UpdateLookOffset();
    }

    public void CauseReact(float damage)
    {
        _reactOffset = new Vector2(0, Math.Min(TalkyConfig.HeadBobAmount,damage));
    }

    private void CalculateSpeechHeadBob()
    {
        if (!Plugin.Instance.VoiceChattingHandler.SpeechTrackerCache.TryGetValue(_player.NetworkId,
                out SpeechTracker tracker)) return;
        
        if (!tracker.ShouldHeadBob)
        {
            return;
        }
        if (tracker.CurrentVolumeRatio < 0)
        {
            return;
        }

        _headBobOffset = new Vector2(0, (tracker.CurrentVolumeRatio - 0.25f) * TalkyConfig.HeadBobAmount);
    }

    private void CalculateGlanceLook()
    {
        float maxDistance = TalkyConfig.GlaceRange;
        float fovDeg = TalkyConfig.GlaceFov;                  // widen this to allow more candidates (e.g., 140–170)
        long recentMs = TalkyConfig.GlanceMaxDuration;
        float lookGain = TalkyConfig.GlaceGain;               // increase to rotate more toward target
        float maxYaw = TalkyConfig.GlanceMaxYaw;              // cap horizontal glance (degrees)
        float maxPitch = TalkyConfig.GlanceMaxPitch;          // cap vertical glance (degrees)

        // Derived
        float minFrontDot = Mathf.Cos(0.5f * fovDeg * Mathf.Deg2Rad);
        float maxDistSqr = maxDistance * maxDistance;

        // Cache player state
        var cam = _player.Camera;
        Vector3 myPos = _player.Position;
        Vector3 camPos = cam.position;
        Vector3 camFwd = cam.forward;
        float currentTime = Time.time;

        // Find the best valid candidate using snapshots
        bool found = false;
        Vector3 bestPos = default;
        float bestScore = float.MinValue;

        //todo: Find a way to knock this down to O(n) instead of O(n^2)
        foreach (var checkingPlayer in Player.List)
        {
            if (checkingPlayer.NetworkId == _player.NetworkId) continue;
            if (!Plugin.Instance.VoiceChattingHandler.SpeechTrackerCache.TryGetValue(checkingPlayer.NetworkId, out var tracker))
                continue;
            Vector3 nearbyPos = checkingPlayer.Position;
            float distSqr = (nearbyPos - myPos).sqrMagnitude;
            if (distSqr > maxDistSqr) continue;
            
            
            // Must have spoken within recentMs
            if (tracker.LastPacketTime < 0) continue;
            if ((currentTime - tracker.LastPacketTime) * 1000 > recentMs) continue;

            Vector3 candidate = checkingPlayer.Camera.position;
            Vector3 dirWorld = (candidate - camPos).normalized;

            float frontDot = Vector3.Dot(camFwd, dirWorld);
            if (frontDot < minFrontDot) continue; // outside your frontal cone

            // Calculate distance for scoring (use sqrt only here where needed)
            float dist = Mathf.Sqrt(distSqr);

            // Prefer the most centered (largest dot). Use a scoring system to appear more dynamic
            float score = 0.8f * frontDot - 0.2f * (dist / maxDistance);
            if (score > bestScore)
            {
                bestScore = score;
                bestPos = candidate;
                found = true;
            }
        }

        if (!found)
        {
            return; // no valid target
        }
        Vector3 targetLook = bestPos;

        // Convert to camera-local direction for yaw/pitch extraction
        Vector3 dirLocal = cam.InverseTransformDirection((targetLook - camPos).normalized);

        // Left/right (yaw), up/down (pitch)
        float yawDeg = Mathf.Atan2(dirLocal.x, dirLocal.z) * Mathf.Rad2Deg;
        float pitchDeg = Mathf.Asin(Mathf.Clamp(dirLocal.y, -1f, 1f)) * Mathf.Rad2Deg;

        // Distance smoothing (smoothstep for a less "linear" feel)
        float d = (myPos - targetLook).magnitude;
        float t = 1f - Mathf.Clamp01(d / maxDistance);
        float distanceFactor = t * t * (3f - 2f * t); // smoothstep(0..1)

        // Front weighting based on how centered the target is within the FOV
        // Map [minFrontDot..1] -> [0..1], then bias with gamma < 1 to push harder
        float frontFactor = Mathf.InverseLerp(minFrontDot, 1f, Vector3.Dot(camFwd, (targetLook - camPos).normalized));
        float frontBias = Mathf.Pow(frontFactor, 0.5f); // sqrt bias: stronger pull toward target

        // Overall intensity
        float intensity = Mathf.Clamp01(lookGain * distanceFactor * frontBias);

        // Cap max contribution so glances stay subtle and natural
        float cappedYaw = Mathf.Clamp(yawDeg, -maxYaw, maxYaw);
        float cappedPitch = Mathf.Clamp(pitchDeg, -maxPitch, maxPitch);

        // Store the offset (yaw, pitch)
        _glanceOffset = new Vector2(cappedYaw, cappedPitch) * intensity;
    }

    private Vector2 UpdateLookOffset()
    {
        Vector2 goal = GetLookGoal();
        var lookOffset = Vector2.Lerp(LastLookOffset, goal, Time.deltaTime * 7f);
        return lookOffset;
    }

    private Vector2 GetLookGoal()
    {
        return _headBobOffset + _glanceOffset + _reactOffset;
    }
}