using System;
using Talky.Performance;
using Talky.Settings;

namespace Talky.Runtime;

/// <summary>
/// Immutable runtime-facing policy. The performance governor maps its effective tier to this
/// compact shape so the frame loop never needs to inspect mutable governor state.
/// </summary>
public sealed class TalkyRuntimePolicy
{
    public TalkyRuntimePolicy(
        float speechInterval,
        float lookInterval,
        float glanceInterval,
        bool enableHeadMotion,
        bool enableHeadBobbing,
        bool enableGlancing)
    {
        SpeechInterval = ClampInterval(speechInterval, 1f / 240f);
        LookInterval = ClampInterval(lookInterval, 0f);
        GlanceInterval = ClampInterval(glanceInterval, 1f / 240f);
        EnableHeadMotion = enableHeadMotion;
        EnableHeadBobbing = enableHeadMotion && enableHeadBobbing;
        EnableGlancing = enableHeadMotion && enableGlancing;
    }

    public float SpeechInterval { get; }
    public float LookInterval { get; }
    public float GlanceInterval { get; }
    public bool EnableHeadMotion { get; }
    public bool EnableHeadBobbing { get; }
    public bool EnableGlancing { get; }

    public static TalkyRuntimePolicy FromConfig(Config config)
    {
        float speechInterval = config?.SpeechUpdateInterval ?? 0.033f;
        return new TalkyRuntimePolicy(
            speechInterval,
            0f,
            0.05f,
            true,
            config?.EnableHeadBobbing ?? true,
            config?.EnableGlancing ?? true);
    }

    public static TalkyRuntimePolicy FromEffective(EffectivePerformancePolicy policy)
    {
        if (policy == null)
            throw new ArgumentNullException(nameof(policy));

        return new TalkyRuntimePolicy(
            policy.FaceIntervalSeconds,
            policy.LookIntervalSeconds,
            policy.GlanceIntervalSeconds,
            policy.HeadMovementEnabled,
            policy.HeadBobbingEnabled,
            policy.GlancingEnabled);
    }

    private static float ClampInterval(float value, float fallback)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
            return fallback;
        return value;
    }
}
