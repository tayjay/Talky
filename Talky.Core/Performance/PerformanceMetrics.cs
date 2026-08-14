using System;
using System.Runtime.CompilerServices;

namespace Talky.Performance;

/// <summary>
/// Timed work recorded by <see cref="PerformanceProfiler"/>.
/// Outer batch metrics are used for budget attribution; nested metrics remain diagnostic.
/// </summary>
public enum PerformanceMetric
{
    VoicePacket = 0,
    OpusDecode = 1,
    EnergyUpdate = 2,
    FaceBatch = 3,
    LookBatch = 4,
    GlanceBatch = 5,
    DirectRoleSync = 6,
    CedModRoleSync = 7,
    CedModPostfix = 8,
    SchedulerOverhead = 9,
    Governor = 10,
    ProfilerOverhead = 11,
    DirectVisibilitySync = 12,
    DirectVisibilityActive = 13,
    DirectRoleSyncActive = 14,
    DirectRoleSyncRewrite = 15,
    CedModRoleSyncActive = 16,
    CedModPostfixRewrite = 17,
    SettingsResponse = 18,
    AnimationRpc = 19,
    PendingActionDrain = 20,
    Lifecycle = 21,
    ConfigReload = 22,
    Count = 23,
}

public enum PerformanceCounter
{
    VoicePackets = 0,
    DecodedSamples = 1,
    TrackedPlayers = 2,
    RecentSpeakers = 3,
    GlanceCandidates = 4,
    ActiveLookTargets = 5,
    SyncEntries = 6,
    ActiveTargetHits = 7,
    Rewrites = 8,
    EmotionWrites = 9,
    VoiceBytes = 10,
    VoiceInvalidPackets = 11,
    VoiceDecodeSuccesses = 12,
    VoiceDecodeFailures = 13,
    VoiceAnalysisSkips = 14,
    VoiceBreakerActivations = 15,
    RemoteChannelGlanceRejections = 16,
    SettingsResponses = 17,
    InvalidEmotionSelections = 18,
    GrabAccepted = 19,
    GrabDuplicate = 20,
    GrabThrottled = 21,
    EmotionCoalesced = 22,
    EmotionApplied = 23,
    AnimationRpcSends = 24,
    AnimationRpcLogicalRecipients = 25,
    PendingActionsQueued = 26,
    PendingActionsCoalesced = 27,
    PendingActionsCurrent = 28,
    PendingActionsSamples = 29,
    PendingActionsHighWater = 30,
    RuntimeUpdateFailures = 31,
    RuntimeQuarantines = 32,
    ConfigCorrections = 33,
    CleanupFailures = 34,
    DirectVisibilityHooks = 35,
    DirectRoleSyncHooks = 36,
    CedModPrefixHooks = 37,
    CedModPostfixHooks = 38,
    SyncFastPathBypasses = 39,
    LogicalSyncRecords = 40,
    LogicalSyncRewrites = 41,
    SyncNonFiniteOffsets = 42,
    SyncCacheMisses = 43,
    SyncCacheMismatches = 44,
    AnimationRpcThrottled = 45,
    LifecycleEnableAttempts = 46,
    LifecycleEnableFailures = 47,
    LifecycleDisableAttempts = 48,
    LifecycleCleanupAttempts = 49,
    VoicePayloadBytes1To64 = 50,
    VoicePayloadBytes65To128 = 51,
    VoicePayloadBytes129To256 = 52,
    VoicePayloadBytes257To384 = 53,
    VoicePayloadBytes385To512 = 54,
    OwnedSubscriptionGroupsCurrent = 55,
    SimultaneousSpeakersCurrent = 56,
    SimultaneousSpeakersHighWater = 57,
    FaceTicks = 58,
    LookTicks = 59,
    GlanceTicks = 60,
    GlanceSelections = 61,
    SpeechTrackersCurrent = 62,
    LookTrackersCurrent = 63,
    Count = 64,
}

/// <summary>
/// Defines how a counter is combined across completed windows. Most values are event totals;
/// point-in-time queue depth and high-water observations deliberately use different semantics.
/// </summary>
public enum PerformanceCounterAggregation
{
    Sum = 0,
    Last = 1,
    Maximum = 2,
}

public static class PerformanceCounterCatalog
{
    public static PerformanceCounterAggregation Aggregation(PerformanceCounter counter)
    {
        switch (counter)
        {
            case PerformanceCounter.PendingActionsCurrent:
            case PerformanceCounter.OwnedSubscriptionGroupsCurrent:
            case PerformanceCounter.SimultaneousSpeakersCurrent:
            case PerformanceCounter.SpeechTrackersCurrent:
            case PerformanceCounter.LookTrackersCurrent:
                return PerformanceCounterAggregation.Last;
            case PerformanceCounter.SimultaneousSpeakersHighWater:
                return PerformanceCounterAggregation.Maximum;
            case PerformanceCounter.PendingActionsHighWater:
                return PerformanceCounterAggregation.Maximum;
            default:
                Validate(counter);
                return PerformanceCounterAggregation.Sum;
        }
    }

    private static void Validate(PerformanceCounter counter)
    {
        int index = (int)counter;
        if (index < 0 || index >= (int)PerformanceCounter.Count)
            throw new ArgumentOutOfRangeException(nameof(counter));
    }
}

/// <summary>
/// Top-level groups shown in transition and periodic performance reports.
/// <see cref="None"/> identifies nested diagnostics which would otherwise double-count work.
/// </summary>
public enum PerformanceSubsystem
{
    None = 0,
    Voice = 1,
    Face = 2,
    Look = 3,
    Scheduler = 4,
    Sync = 5,
    // Frame-controller and governor measurements only. Begin/End/Count instrumentation at
    // individual call sites is deliberately excluded, so this is a monitoring lower bound.
    Monitoring = 6,
    Count = 7,
}

public readonly struct PerformanceMetricDefinition
{
    internal PerformanceMetricDefinition(
        bool countsTowardTalkyBudget,
        int defaultSampleEvery,
        PerformanceSubsystem summarySubsystem)
    {
        CountsTowardTalkyBudget = countsTowardTalkyBudget;
        DefaultSampleEvery = defaultSampleEvery;
        SummarySubsystem = summarySubsystem;
    }

    public bool CountsTowardTalkyBudget { get; }
    public int DefaultSampleEvery { get; }
    public PerformanceSubsystem SummarySubsystem { get; }
}

/// <summary>
/// Single source of truth for budget attribution, production sampling, and report grouping.
/// </summary>
public static class PerformanceMetricCatalog
{
    public const int VoiceSampleEvery = 8;
    public const int SyncSampleEvery = 64;
    public const int ActiveSyncSampleEvery = 8;

    // Enum order is intentional and covered by a complete table test.
    private static readonly PerformanceMetricDefinition[] Definitions =
    {
        // Counts toward budget, default sampling, top-level report group
        new PerformanceMetricDefinition(true, VoiceSampleEvery, PerformanceSubsystem.Voice), // VoicePacket
        new PerformanceMetricDefinition(false, VoiceSampleEvery, PerformanceSubsystem.None), // OpusDecode
        new PerformanceMetricDefinition(false, VoiceSampleEvery, PerformanceSubsystem.None), // EnergyUpdate
        new PerformanceMetricDefinition(true, 1, PerformanceSubsystem.Face), // FaceBatch
        new PerformanceMetricDefinition(true, 1, PerformanceSubsystem.Look), // LookBatch
        new PerformanceMetricDefinition(false, 1, PerformanceSubsystem.None), // GlanceBatch
        new PerformanceMetricDefinition(true, SyncSampleEvery, PerformanceSubsystem.Sync), // DirectRoleSync
        new PerformanceMetricDefinition(true, SyncSampleEvery, PerformanceSubsystem.Sync), // CedModRoleSync
        new PerformanceMetricDefinition(true, SyncSampleEvery, PerformanceSubsystem.Sync), // CedModPostfix
        new PerformanceMetricDefinition(true, 1, PerformanceSubsystem.Scheduler), // SchedulerOverhead
        new PerformanceMetricDefinition(false, 1, PerformanceSubsystem.Monitoring), // Governor
        new PerformanceMetricDefinition(false, 1, PerformanceSubsystem.Monitoring), // ProfilerOverhead
        new PerformanceMetricDefinition(true, SyncSampleEvery, PerformanceSubsystem.Sync), // DirectVisibilitySync
        new PerformanceMetricDefinition(false, ActiveSyncSampleEvery, PerformanceSubsystem.None), // DirectVisibilityActive
        new PerformanceMetricDefinition(false, ActiveSyncSampleEvery, PerformanceSubsystem.None), // DirectRoleSyncActive
        new PerformanceMetricDefinition(false, ActiveSyncSampleEvery, PerformanceSubsystem.None), // DirectRoleSyncRewrite
        new PerformanceMetricDefinition(false, ActiveSyncSampleEvery, PerformanceSubsystem.None), // CedModRoleSyncActive
        new PerformanceMetricDefinition(false, ActiveSyncSampleEvery, PerformanceSubsystem.None), // CedModPostfixRewrite
        // Server-specific settings callbacks are top-level client-driven Talky work. Attribute
        // them to the scheduler/event subsystem; AnimationRpc remains nested to avoid counting
        // the same response twice.
        new PerformanceMetricDefinition(true, VoiceSampleEvery, PerformanceSubsystem.Scheduler), // SettingsResponse
        new PerformanceMetricDefinition(false, VoiceSampleEvery, PerformanceSubsystem.None), // AnimationRpc
        new PerformanceMetricDefinition(false, 1, PerformanceSubsystem.None), // PendingActionDrain
        new PerformanceMetricDefinition(false, 1, PerformanceSubsystem.None), // Lifecycle
        new PerformanceMetricDefinition(false, 1, PerformanceSubsystem.None), // ConfigReload
    };

    public static PerformanceMetricDefinition Get(PerformanceMetric metric)
    {
        return Definitions[MetricIndex(metric)];
    }

    public static bool CountsTowardTalkyBudget(PerformanceMetric metric)
    {
        return Definitions[MetricIndex(metric)].CountsTowardTalkyBudget;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DefaultSampleEvery(PerformanceMetric metric)
    {
        return Definitions[MetricIndex(metric)].DefaultSampleEvery;
    }

    public static PerformanceSubsystem SummarySubsystem(PerformanceMetric metric)
    {
        return Definitions[MetricIndex(metric)].SummarySubsystem;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MetricIndex(PerformanceMetric metric)
    {
        int index = (int)metric;
        if (index < 0 || index >= Definitions.Length)
            throw new ArgumentOutOfRangeException(nameof(metric));
        return index;
    }
}

/// <summary>
/// Allocation-free top-level view of one completed profiler window.
/// </summary>
public readonly struct PerformanceBreakdown
{
    private PerformanceBreakdown(
        double voiceMilliseconds,
        double faceMilliseconds,
        double lookMilliseconds,
        double schedulerMilliseconds,
        double syncMilliseconds,
        double monitoringMilliseconds)
    {
        VoiceMilliseconds = voiceMilliseconds;
        FaceMilliseconds = faceMilliseconds;
        LookMilliseconds = lookMilliseconds;
        SchedulerMilliseconds = schedulerMilliseconds;
        SyncMilliseconds = syncMilliseconds;
        MonitoringMilliseconds = monitoringMilliseconds;
    }

    public double VoiceMilliseconds { get; }
    public double FaceMilliseconds { get; }
    public double LookMilliseconds { get; }
    public double SchedulerMilliseconds { get; }
    public double SyncMilliseconds { get; }
    public double MonitoringMilliseconds { get; }

    public static PerformanceBreakdown FromWindow(PerformanceWindow window)
    {
        if (window == null)
            throw new ArgumentNullException(nameof(window));

        double voice = 0d;
        double face = 0d;
        double look = 0d;
        double scheduler = 0d;
        double sync = 0d;
        double monitoring = 0d;

        for (int index = 0; index < (int)PerformanceMetric.Count; index++)
        {
            PerformanceMetric metric = (PerformanceMetric)index;
            PerformanceSubsystem subsystem = PerformanceMetricCatalog.SummarySubsystem(metric);
            if (subsystem == PerformanceSubsystem.None)
                continue;

            double milliseconds = window.GetMetric(metric).EstimatedTotalMilliseconds;
            switch (subsystem)
            {
                case PerformanceSubsystem.Voice:
                    voice += milliseconds;
                    break;
                case PerformanceSubsystem.Face:
                    face += milliseconds;
                    break;
                case PerformanceSubsystem.Look:
                    look += milliseconds;
                    break;
                case PerformanceSubsystem.Scheduler:
                    scheduler += milliseconds;
                    break;
                case PerformanceSubsystem.Sync:
                    sync += milliseconds;
                    break;
                case PerformanceSubsystem.Monitoring:
                    monitoring += milliseconds;
                    break;
            }
        }

        return new PerformanceBreakdown(voice, face, look, scheduler, sync, monitoring);
    }

    public double GetMilliseconds(PerformanceSubsystem subsystem)
    {
        switch (subsystem)
        {
            case PerformanceSubsystem.None:
                return 0d;
            case PerformanceSubsystem.Voice:
                return VoiceMilliseconds;
            case PerformanceSubsystem.Face:
                return FaceMilliseconds;
            case PerformanceSubsystem.Look:
                return LookMilliseconds;
            case PerformanceSubsystem.Scheduler:
                return SchedulerMilliseconds;
            case PerformanceSubsystem.Sync:
                return SyncMilliseconds;
            case PerformanceSubsystem.Monitoring:
                return MonitoringMilliseconds;
            default:
                throw new ArgumentOutOfRangeException(nameof(subsystem));
        }
    }
}
