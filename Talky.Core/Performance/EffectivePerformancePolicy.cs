using System;
using Talky.Settings;

namespace Talky.Performance;

/// <summary>
/// Immutable behavior that lets the current tier suppress, but never enable, configured features.
/// </summary>
public sealed class EffectivePerformancePolicy
{
    private EffectivePerformancePolicy(
        PerformanceTier tier,
        float faceIntervalSeconds,
        float glanceIntervalSeconds,
        float lookIntervalSeconds,
        bool headMovementEnabled,
        bool headBobbingEnabled,
        bool glancingEnabled,
        bool spectatorHeadMovementsEnabled,
        bool faceReactionsEnabled,
        bool consumableEmotesEnabled)
    {
        Tier = tier;
        FaceIntervalSeconds = faceIntervalSeconds;
        GlanceIntervalSeconds = glanceIntervalSeconds;
        LookIntervalSeconds = lookIntervalSeconds;
        HeadMovementEnabled = headMovementEnabled;
        HeadBobbingEnabled = headBobbingEnabled;
        GlancingEnabled = glancingEnabled;
        SpectatorHeadMovementsEnabled = spectatorHeadMovementsEnabled;
        FaceReactionsEnabled = faceReactionsEnabled;
        ConsumableEmotesEnabled = consumableEmotesEnabled;
    }

    public PerformanceTier Tier { get; }
    public bool FaceAnimationEnabled => true;
    public bool DecodeVoiceEnabled => true;
    public float FaceIntervalSeconds { get; }
    public float GlanceIntervalSeconds { get; }
    public float LookIntervalSeconds { get; }
    public bool HeadMovementEnabled { get; }
    public bool HeadBobbingEnabled { get; }
    public bool GlancingEnabled { get; }
    public bool SpectatorHeadMovementsEnabled { get; }
    public bool FaceReactionsEnabled { get; }
    public bool ConsumableEmotesEnabled { get; }

    public static EffectivePerformancePolicy Create(Config config, PerformanceTier tier)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));
        if (!Enum.IsDefined(typeof(PerformanceTier), tier))
            tier = PerformanceTier.Full;

        PerformanceConfig performance = PerformanceConfigValidator.Validate(config.Performance).Config;

        float configuredFaceInterval = config.SpeechUpdateInterval;
        if (float.IsNaN(configuredFaceInterval) ||
            float.IsInfinity(configuredFaceInterval) ||
            configuredFaceInterval <= 0f)
        {
            configuredFaceInterval = 0.033f;
        }

        bool economyOrLower = tier >= PerformanceTier.Economy;
        bool noGlancingOrLower = tier >= PerformanceTier.NoGlancing;
        bool faceOnlyOrLower = tier >= PerformanceTier.FaceOnly;

        float faceInterval = configuredFaceInterval;
        if (economyOrLower)
            faceInterval = Math.Max(faceInterval, 1f / performance.EconomyFaceHz);
        if (tier == PerformanceTier.FaceOnlyReducedRate)
            faceInterval = Math.Max(faceInterval, 1f / performance.MinimumFaceHz);

        float glanceInterval = economyOrLower
            ? 1f / performance.EconomyGlanceHz
            : 0.05f;
        float lookInterval = economyOrLower
            ? 1f / performance.EconomyLookHz
            : 0f;

        bool headBobbing = config.EnableHeadBobbing && !faceOnlyOrLower;
        bool glancing = config.EnableGlancing && !noGlancingOrLower;
        bool headMovement = !faceOnlyOrLower && (headBobbing || glancing || config.EnableReactionOnHurt);

        return new EffectivePerformancePolicy(
            tier,
            faceInterval,
            glanceInterval,
            lookInterval,
            headMovement,
            headBobbing,
            glancing,
            config.EnabledSpectatorHeadMovements && tier == PerformanceTier.Full,
            config.EnableReactionOnHurt,
            config.EnableEmoteOnConsumables);
    }
}
