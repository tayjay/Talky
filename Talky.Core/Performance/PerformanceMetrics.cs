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
    Count = 12,
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
    Count = 10,
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
