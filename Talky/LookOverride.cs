using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using UnityEngine;

namespace Talky;

public class LookOverride : MonoBehaviour
{
    public Player player;

    public ReferenceHub hub => player.ReferenceHub;

    public List<Vector2> lookOffsets;
    
    public SpeechTracker speechTracker;

    public static Config TalkyConfig => Plugin.Instance.Config;
    
    public Vector2 LastLookOffset { get; private set; }
    
    public DateTime LastBusyTime { get; set; }

    public float Reflex, Attention;


    private void Awake()
    {
        player = Player.Get(GetComponent<ReferenceHub>());
        lookOffsets = new List<Vector2>();
        speechTracker = GetComponent<SpeechTracker>();
        LastLookOffset = Vector2.zero;
        LastBusyTime = DateTime.Now;
        Reflex = TalkyConfig.GlanceDynamicTraits ? UnityEngine.Random.Range(0.8f, 1.25f) : 1f; // How quickly a player will look at a target compared to others
        Attention = TalkyConfig.GlanceDynamicTraits ? UnityEngine.Random.Range(0.8f, 1.25f) : 1f; // How long a player will look at talking players compared to others
    }

    private void Update()
    {
        lookOffsets.Clear();
        
        if(DateTime.Now - LastBusyTime < TimeSpan.FromSeconds(0.1)) return;
        
        // Head bobbing based on speech volume
        if(TalkyConfig.EnableHeadBob)
            CalculateSpeechHeadBob();

        // Glance at nearby speaking players
        if(TalkyConfig.EnableGlancing)
            CalculateGlanceLook();
    }

    private void CalculateSpeechHeadBob()
    {
        if (!speechTracker.ShouldHeadBob)
        {
            return;
        }
        if (speechTracker.CurrentVolumeRatio < 0)
        {
            return;
        }
        
        lookOffsets.Add(new Vector2(0, (speechTracker.CurrentVolumeRatio -0.25f) * TalkyConfig.HeadBobAmount));
    }
    
    private void CalculateGlanceLook()
    {
        float maxDistance = TalkyConfig.GlaceRange;
        float fovDeg = TalkyConfig.GlaceFov;                  // widen this to allow more candidates (e.g., 140–170)
        long recentMs = TalkyConfig.GlanceMaxDuration;
        float lookGain = TalkyConfig.GlaceGain;               // increase to rotate more toward target
        float maxYaw = TalkyConfig.GlanceMaxYaw;                   // cap horizontal glance (degrees)
        float maxPitch = TalkyConfig.GlanceMaxPitch;                 // cap vertical glance (degrees)

        // Derived
        float minFrontDot = Mathf.Cos(0.5f * fovDeg * Mathf.Deg2Rad);

        // Early outs / locals
        var cam = player.Camera;
        Vector3 camPos = cam.position;
        Vector3 camFwd = cam.forward;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Find the best valid candidate
        Vector3 targetLook = default;
        bool found = false;
        Vector3 bestPos = default;
        float bestDot = -1f;
        float bestDist = float.MaxValue;

        foreach (var nearby in Player.List.Where(p => Vector3.Distance(player.Position, p.Position) <= maxDistance))
        {
            if (nearby == player) continue;
            float dist = Vector3.Distance(nearby.Position, player.Position);
            if (dist > maxDistance) continue;
            if ((nowMs - this.speechTracker.LastPacketTime) >= recentMs * Attention)
            {
                // This player is not talking, is anyone nearby talking?
                if (!nearby.ReferenceHub.TryGetComponent(out SpeechTracker st)) continue;

                // Must have spoken within recentMs
                if ((nowMs - st.LastPacketTime) > recentMs * Attention) continue;
            }
            else
            {
                // this player has recently talked, they are focused on talking
                continue;
            }
            

            Vector3 candidate = nearby.Camera.position;
            Vector3 dirWorld = (candidate - camPos).normalized;

            float frontDot = Vector3.Dot(camFwd, dirWorld);
            if (frontDot < minFrontDot) continue; // outside your frontal cone

            // Prefer the most centered (largest dot). Use a scoring system to appear more dynamic
            if((0.8f * frontDot - 0.2f * (dist/maxDistance)) > (0.8f * bestDot - 0.2f * (bestDist/maxDistance)))
            //if (frontDot > bestDot || (Mathf.Approximately(frontDot, bestDot) && dist < bestDist))
            {
                bestDot = frontDot;
                bestDist = dist;
                bestPos = candidate;
                found = true;
            }
        }

        if (found)
        {
            targetLook = bestPos;
        } else
        {
            return; // no valid target
        }
            

        // Convert to camera-local direction for yaw/pitch extraction
        Vector3 dirLocal = cam.InverseTransformDirection((targetLook - camPos).normalized);

        // Left/right (yaw), up/down (pitch)
        float yawDeg   = Mathf.Atan2(dirLocal.x, dirLocal.z) * Mathf.Rad2Deg;
        float pitchDeg = Mathf.Asin(Mathf.Clamp(dirLocal.y, -1f, 1f)) * Mathf.Rad2Deg;

        // Distance smoothing (smoothstep for a less "linear" feel)
        float d = Vector3.Distance(player.Position, targetLook);
        float t = 1f - Mathf.Clamp01(d / maxDistance);
        float distanceFactor = t * t * (3f - 2f * t); // smoothstep(0..1)

        // Front weighting based on how centered the target is within the FOV
        // Map [minFrontDot..1] -> [0..1], then bias with gamma < 1 to push harder
        float frontFactor = Mathf.InverseLerp(minFrontDot, 1f, Vector3.Dot(camFwd, (targetLook - camPos).normalized));
        float frontBias = Mathf.Pow(frontFactor, 0.5f); // sqrt bias: stronger pull toward target

        // Overall intensity
        float intensity = Mathf.Clamp01(lookGain * distanceFactor * frontBias);

        // Cap max contribution so glances stay subtle and natural
        float cappedYaw   = Mathf.Clamp(yawDeg,   -maxYaw,   maxYaw);
        float cappedPitch = Mathf.Clamp(pitchDeg, -maxPitch, maxPitch);

        // Add the offset (yaw, pitch) — note ordering!
        lookOffsets.Add(new Vector2(cappedYaw, cappedPitch) * intensity);
    }


    
    
    public Vector2 GetLookOffset()
    {
        Vector2 lookOffset = Vector2.zero;
        Vector2 goal = GetLookGoal();
        lookOffset = Vector2.Lerp(LastLookOffset, goal, Time.deltaTime * 7f * Reflex);
        LastLookOffset = lookOffset;
        return lookOffset;
    }

    public Vector2 GetLookGoal()
    {
        Vector2 totalOffset = Vector2.zero;
        foreach (var offset in lookOffsets)
        {
            totalOffset += offset;
        }
        return totalOffset;
    }
}