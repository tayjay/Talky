using System;
using Talky.Compatibility;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace Talky.Performance;

/// <summary>
/// Immutable, process-local diagnostics published after a completed profiler window. The schema
/// contains only aggregate operational data: it never contains player identifiers or voice data.
/// </summary>
public sealed class TalkyDiagnosticsSnapshot
{
    public const int CurrentSchemaVersion = 1;
    public const int RollingHorizonSeconds = 60;

    internal TalkyDiagnosticsSnapshot(
        double publishedAtSeconds,
        int evaluationWindowSeconds,
        PerformanceMode mode,
        PerformanceTier effectiveTier,
        PerformanceTier committedTier,
        bool recoveryProbeActive,
        bool profilingEnabled,
        bool frozen,
        TalkyDiagnosticsInterval latestWindow,
        TalkyDiagnosticsInterval rolling60Seconds,
        TalkyDiagnosticsInterval sinceEnable,
        TalkyDiagnosticsHighWater highWater,
        TalkyDiagnosticsSystem system,
        TalkyDiagnosticsTransitions transitions,
        TalkyDiagnosticsIdentity identity)
    {
        SchemaVersion = CurrentSchemaVersion;
        PublishedAtSeconds = publishedAtSeconds;
        EvaluationWindowSeconds = evaluationWindowSeconds;
        Mode = mode;
        EffectiveTier = effectiveTier;
        CommittedTier = committedTier;
        RecoveryProbeActive = recoveryProbeActive;
        ProfilingEnabled = profilingEnabled;
        Frozen = frozen;
        LatestWindow = latestWindow ?? throw new ArgumentNullException(nameof(latestWindow));
        Rolling60Seconds = rolling60Seconds ??
                           throw new ArgumentNullException(nameof(rolling60Seconds));
        SinceEnable = sinceEnable ?? throw new ArgumentNullException(nameof(sinceEnable));
        HighWater = highWater ?? throw new ArgumentNullException(nameof(highWater));
        System = system ?? throw new ArgumentNullException(nameof(system));
        Transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    public int SchemaVersion { get; }
    public double PublishedAtSeconds { get; }
    public int EvaluationWindowSeconds { get; }
    public PerformanceMode Mode { get; }
    public PerformanceTier EffectiveTier { get; }
    public PerformanceTier CommittedTier { get; }
    public bool RecoveryProbeActive { get; }
    public bool ProfilingEnabled { get; }
    public bool Frozen { get; }
    public TalkyDiagnosticsInterval LatestWindow { get; }
    public TalkyDiagnosticsInterval Rolling60Seconds { get; }
    public TalkyDiagnosticsInterval SinceEnable { get; }
    public TalkyDiagnosticsHighWater HighWater { get; }
    public TalkyDiagnosticsSystem System { get; }
    public TalkyDiagnosticsTransitions Transitions { get; }
    public TalkyDiagnosticsIdentity Identity { get; }
    /// <summary>
    /// False in schema v1: Talky observes logical sync/RPC work above Mirror and installs no
    /// transport listener, so it does not claim packet or wire-byte counts.
    /// </summary>
    public bool NetworkTransportMetricsAvailable => false;
}

/// <summary>Immutable aggregate for one diagnostics interval.</summary>
public sealed class TalkyDiagnosticsInterval
{
    private readonly MetricSnapshot[] _metrics;
    private readonly long[] _counters;
    private readonly double[] _subsystemMilliseconds;

    internal TalkyDiagnosticsInterval(
        long windowCount,
        double durationSeconds,
        long frameCount,
        double totalTalkyMilliseconds,
        double frameAverageMilliseconds,
        double frameP50Milliseconds,
        double frameP95Milliseconds,
        double frameP99Milliseconds,
        double frameMaxMilliseconds,
        double worstWindowP95Milliseconds,
        MetricSnapshot[] metrics,
        long[] counters,
        double[] subsystemMilliseconds)
    {
        WindowCount = windowCount;
        DurationSeconds = durationSeconds;
        FrameCount = frameCount;
        TotalTalkyMilliseconds = totalTalkyMilliseconds;
        FrameAverageMilliseconds = frameAverageMilliseconds;
        FrameP50Milliseconds = frameP50Milliseconds;
        FrameP95Milliseconds = frameP95Milliseconds;
        FrameP99Milliseconds = frameP99Milliseconds;
        FrameMaxMilliseconds = frameMaxMilliseconds;
        WorstWindowP95Milliseconds = worstWindowP95Milliseconds;
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _counters = counters ?? throw new ArgumentNullException(nameof(counters));
        _subsystemMilliseconds = subsystemMilliseconds ??
                                 throw new ArgumentNullException(nameof(subsystemMilliseconds));
        VoicePayloadHistogram = new TalkyDiagnosticsVoicePayloadHistogram(
            _counters[(int)PerformanceCounter.VoicePayloadBytes1To64],
            _counters[(int)PerformanceCounter.VoicePayloadBytes65To128],
            _counters[(int)PerformanceCounter.VoicePayloadBytes129To256],
            _counters[(int)PerformanceCounter.VoicePayloadBytes257To384],
            _counters[(int)PerformanceCounter.VoicePayloadBytes385To512]);
    }

    public long WindowCount { get; }
    public double DurationSeconds { get; }
    public long FrameCount { get; }
    public double TotalTalkyMilliseconds { get; }
    public double TalkyMillisecondsPerFrame =>
        FrameCount > 0L ? TotalTalkyMilliseconds / FrameCount : 0d;
    public double TalkyMillisecondsPerSecond =>
        DurationSeconds > 0d ? TotalTalkyMilliseconds / DurationSeconds : 0d;
    public double FrameAverageMilliseconds { get; }
    public double FrameP50Milliseconds { get; }
    public double FrameP95Milliseconds { get; }
    public double FrameP99Milliseconds { get; }
    public double FrameMaxMilliseconds { get; }
    public double WorstWindowP95Milliseconds { get; }
    public double AveragePlayers => AveragePerFrame(PerformanceCounter.TrackedPlayers);
    public double AverageRecentSpeakers => AveragePerFrame(PerformanceCounter.RecentSpeakers);
    public double AverageActiveLooks => AveragePerFrame(PerformanceCounter.ActiveLookTargets);
    public long PendingActionsCurrent => GetCounter(PerformanceCounter.PendingActionsCurrent);
    public long SimultaneousSpeakersCurrent =>
        GetCounter(PerformanceCounter.SimultaneousSpeakersCurrent);
    public long SpeechTrackersCurrent => GetCounter(PerformanceCounter.SpeechTrackersCurrent);
    public long LookTrackersCurrent => GetCounter(PerformanceCounter.LookTrackersCurrent);
    public long OwnedSubscriptionGroupsCurrent =>
        GetCounter(PerformanceCounter.OwnedSubscriptionGroupsCurrent);
    public bool FramePercentilesLowConfidence =>
        FrameCount < MetricSnapshot.ReliablePercentileSampleCount;
    public TalkyDiagnosticsVoicePayloadHistogram VoicePayloadHistogram { get; }
    /// <summary>Logical sync records processed by plugin hooks; this is not transport packets.</summary>
    public long LogicalSyncRecords => GetCounter(PerformanceCounter.LogicalSyncRecords);
    /// <summary>Logical records rewritten by Talky; this is not transport packets sent.</summary>
    public long LogicalSyncRewrites => GetCounter(PerformanceCounter.LogicalSyncRewrites);

    public MetricSnapshot GetMetric(PerformanceMetric metric)
    {
        int index = (int)metric;
        if (index < 0 || index >= _metrics.Length)
            throw new ArgumentOutOfRangeException(nameof(metric));
        return _metrics[index];
    }

    public long GetCounter(PerformanceCounter counter)
    {
        int index = (int)counter;
        if (index < 0 || index >= _counters.Length)
            throw new ArgumentOutOfRangeException(nameof(counter));
        return _counters[index];
    }

    public double GetCounterPerSecond(PerformanceCounter counter)
    {
        return DurationSeconds > 0d ? GetCounter(counter) / DurationSeconds : 0d;
    }

    public double GetSubsystemMilliseconds(PerformanceSubsystem subsystem)
    {
        int index = (int)subsystem;
        if (index < 0 || index >= _subsystemMilliseconds.Length)
            throw new ArgumentOutOfRangeException(nameof(subsystem));
        return _subsystemMilliseconds[index];
    }

    public double GetSubsystemMillisecondsPerFrame(PerformanceSubsystem subsystem)
    {
        return FrameCount > 0L ? GetSubsystemMilliseconds(subsystem) / FrameCount : 0d;
    }

    private double AveragePerFrame(PerformanceCounter counter)
    {
        return FrameCount > 0L ? GetCounter(counter) / (double)FrameCount : 0d;
    }

    internal TalkyDiagnosticsInterval WithCounters(long[] counters)
    {
        return new TalkyDiagnosticsInterval(
            WindowCount,
            DurationSeconds,
            FrameCount,
            TotalTalkyMilliseconds,
            FrameAverageMilliseconds,
            FrameP50Milliseconds,
            FrameP95Milliseconds,
            FrameP99Milliseconds,
            FrameMaxMilliseconds,
            WorstWindowP95Milliseconds,
            _metrics,
            counters,
            _subsystemMilliseconds);
    }
}

/// <summary>Exact valid voice-payload counts in protocol-sized byte bands.</summary>
public sealed class TalkyDiagnosticsVoicePayloadHistogram
{
    internal TalkyDiagnosticsVoicePayloadHistogram(
        long bytes1To64,
        long bytes65To128,
        long bytes129To256,
        long bytes257To384,
        long bytes385To512)
    {
        Bytes1To64 = bytes1To64;
        Bytes65To128 = bytes65To128;
        Bytes129To256 = bytes129To256;
        Bytes257To384 = bytes257To384;
        Bytes385To512 = bytes385To512;
    }

    public long Bytes1To64 { get; }
    public long Bytes65To128 { get; }
    public long Bytes129To256 { get; }
    public long Bytes257To384 { get; }
    public long Bytes385To512 { get; }
    public long Total =>
        Bytes1To64 + Bytes65To128 + Bytes129To256 + Bytes257To384 + Bytes385To512;
}

/// <summary>Maximum observations in the current round.</summary>
public sealed class TalkyDiagnosticsHighWater
{
    internal TalkyDiagnosticsHighWater(
        double talkyMillisecondsPerFrame,
        double talkyMillisecondsPerSecond,
        double frameP95Milliseconds,
        double frameMaxMilliseconds,
        int players,
        int recentSpeakers,
        int activeLooks,
        double voicePacketsPerSecond,
        double voiceBytesPerSecond,
        double settingsResponsesPerSecond,
        double animationRpcSendsPerSecond,
        double logicalSyncRecordsPerSecond,
        double logicalSyncRewritesPerSecond,
        int simultaneousSpeakers,
        int speechTrackers,
        int lookTrackers,
        long pendingActionsCurrentMaximum,
        long pendingActionsHighWater)
    {
        TalkyMillisecondsPerFrame = talkyMillisecondsPerFrame;
        TalkyMillisecondsPerSecond = talkyMillisecondsPerSecond;
        FrameP95Milliseconds = frameP95Milliseconds;
        FrameMaxMilliseconds = frameMaxMilliseconds;
        Players = players;
        RecentSpeakers = recentSpeakers;
        ActiveLooks = activeLooks;
        VoicePacketsPerSecond = voicePacketsPerSecond;
        VoiceBytesPerSecond = voiceBytesPerSecond;
        SettingsResponsesPerSecond = settingsResponsesPerSecond;
        AnimationRpcSendsPerSecond = animationRpcSendsPerSecond;
        LogicalSyncRecordsPerSecond = logicalSyncRecordsPerSecond;
        LogicalSyncRewritesPerSecond = logicalSyncRewritesPerSecond;
        SimultaneousSpeakers = simultaneousSpeakers;
        SpeechTrackers = speechTrackers;
        LookTrackers = lookTrackers;
        PendingActionsCurrentMaximum = pendingActionsCurrentMaximum;
        PendingActionsHighWater = pendingActionsHighWater;
    }

    public double TalkyMillisecondsPerFrame { get; }
    public double TalkyMillisecondsPerSecond { get; }
    public double FrameP95Milliseconds { get; }
    public double FrameMaxMilliseconds { get; }
    public int Players { get; }
    public int RecentSpeakers { get; }
    public int ActiveLooks { get; }
    public double VoicePacketsPerSecond { get; }
    public double VoiceBytesPerSecond { get; }
    public double SettingsResponsesPerSecond { get; }
    public double AnimationRpcSendsPerSecond { get; }
    public double LogicalSyncRecordsPerSecond { get; }
    public double LogicalSyncRewritesPerSecond { get; }
    public int SimultaneousSpeakers { get; }
    public int SpeechTrackers { get; }
    public int LookTrackers { get; }
    public long PendingActionsCurrentMaximum { get; }
    public long PendingActionsHighWater { get; }
}

/// <summary>
/// Cold-sampled host/process health. CPU, memory, and GC values cover the whole dedicated-server
/// process and must not be interpreted as Talky-only attribution.
/// </summary>
public sealed class TalkyDiagnosticsSystem
{
    internal TalkyDiagnosticsSystem(
        bool processMetricsAvailable,
        double actualTickRate,
        double targetTickRate,
        double processCpuPercent,
        long residentSetBytes,
        long managedHeapBytes,
        int generation0Collections,
        int generation1Collections,
        int generation2Collections,
        int generation0CollectionsSinceEnable,
        int generation1CollectionsSinceEnable,
        int generation2CollectionsSinceEnable)
    {
        ProcessMetricsAvailable = processMetricsAvailable;
        ActualTickRate = actualTickRate;
        TargetTickRate = targetTickRate;
        ProcessCpuPercent = processCpuPercent;
        ResidentSetBytes = residentSetBytes;
        ManagedHeapBytes = managedHeapBytes;
        Generation0Collections = generation0Collections;
        Generation1Collections = generation1Collections;
        Generation2Collections = generation2Collections;
        Generation0CollectionsSinceEnable = generation0CollectionsSinceEnable;
        Generation1CollectionsSinceEnable = generation1CollectionsSinceEnable;
        Generation2CollectionsSinceEnable = generation2CollectionsSinceEnable;
    }

    public bool ProcessMetricsAvailable { get; }
    public double ActualTickRate { get; }
    public double TargetTickRate { get; }
    public double ProcessCpuPercent { get; }
    public long ResidentSetBytes { get; }
    public long ManagedHeapBytes { get; }
    public int Generation0Collections { get; }
    public int Generation1Collections { get; }
    public int Generation2Collections { get; }
    public int Generation0CollectionsSinceEnable { get; }
    public int Generation1CollectionsSinceEnable { get; }
    public int Generation2CollectionsSinceEnable { get; }
}

public sealed class TalkyDiagnosticsTransitions
{
    internal TalkyDiagnosticsTransitions(
        long total,
        long degradations,
        long recoveries,
        long emergencies,
        long recoveryProbeAccepted,
        long recoveryProbeRejected,
        GovernorReason lastReason)
    {
        Total = total;
        Degradations = degradations;
        Recoveries = recoveries;
        Emergencies = emergencies;
        RecoveryProbeAccepted = recoveryProbeAccepted;
        RecoveryProbeRejected = recoveryProbeRejected;
        LastReason = lastReason;
    }

    public long Total { get; }
    public long Degradations { get; }
    public long Recoveries { get; }
    public long Emergencies { get; }
    public long RecoveryProbeAccepted { get; }
    public long RecoveryProbeRejected { get; }
    public GovernorReason LastReason { get; }
}

public enum TalkyDiagnosticsLifecycleState
{
    Unknown = 0,
    Enabling = 1,
    Enabled = 2,
    Disabling = 3,
    Disabled = 4,
    Faulted = 5,
}

public enum TalkyDiagnosticsTrustState
{
    Unknown = 0,
    NotPresent = 1,
    Verified = 2,
    Rejected = 3,
    Failed = 4,
}

/// <summary>Aggregate lifecycle and compatibility identity without plugin paths or user data.</summary>
public sealed class TalkyDiagnosticsIdentity
{
    internal TalkyDiagnosticsIdentity(
        TalkyDiagnosticsLifecycleState lifecycle,
        TalkyDiagnosticsTrustState harmony,
        TalkyDiagnosticsTrustState cedMod,
        bool cedModSyncModeKnown,
        CedModLookSyncMode cedModSyncMode)
    {
        Lifecycle = lifecycle;
        Harmony = harmony;
        CedMod = cedMod;
        CedModSyncModeKnown = cedModSyncModeKnown;
        CedModSyncMode = cedModSyncMode;
    }

    public TalkyDiagnosticsLifecycleState Lifecycle { get; }
    public TalkyDiagnosticsTrustState Harmony { get; }
    public TalkyDiagnosticsTrustState CedMod { get; }
    public bool CedModSyncModeKnown { get; }
    public CedModLookSyncMode CedModSyncMode { get; }
}

/// <summary>
/// Single-threaded collector used by the server adapter to turn completed profiler windows into
/// immutable schema-v1 publications. Snapshot readers never receive this mutable collector.
/// </summary>
public sealed class TalkyDiagnosticsCollector : IDisposable
{
    private readonly PerformanceDiagnosticsAccumulator _latest =
        new PerformanceDiagnosticsAccumulator();
    private readonly PerformanceDiagnosticsAccumulator _rolling =
        new PerformanceDiagnosticsAccumulator();
    private readonly PerformanceDiagnosticsAccumulator _sinceEnable =
        new PerformanceDiagnosticsAccumulator();
    private readonly TalkyDiagnosticsHighWaterTracker _highWater =
        new TalkyDiagnosticsHighWaterTracker();
    private readonly long[] _exactSinceEnableCounters =
        new long[(int)PerformanceCounter.Count];
    private bool _hasExactCounterObservations;
    private TalkyDiagnosticsInterval _lastCompletedLatest;
    private TalkyDiagnosticsInterval _lastCompletedRolling;
    private readonly DiagnosticsProcess _process;
    private readonly int _initialGeneration0Collections;
    private readonly int _initialGeneration1Collections;
    private readonly int _initialGeneration2Collections;
    private int _previousGeneration0Collections;
    private int _previousGeneration1Collections;
    private int _previousGeneration2Collections;
    private TimeSpan _previousProcessorTime;
    private double _previousProcessSampleAt = double.NaN;
    private bool _processMetricsAvailable;
    private bool _disposed;

    private long _transitionTotal;
    private long _transitionDegradations;
    private long _transitionRecoveries;
    private long _transitionEmergencies;
    private long _recoveryProbeAccepted;
    private long _recoveryProbeRejected;
    private GovernorReason _lastGovernorReason;

    private TalkyDiagnosticsLifecycleState _lifecycleState;
    private TalkyDiagnosticsTrustState _harmonyState;
    private TalkyDiagnosticsTrustState _cedModState;
    private bool _cedModSyncModeKnown;
    private CedModLookSyncMode _cedModSyncMode;

    public TalkyDiagnosticsCollector()
    {
        _initialGeneration0Collections = _previousGeneration0Collections = GC.CollectionCount(0);
        _initialGeneration1Collections = _previousGeneration1Collections = GC.CollectionCount(1);
        _initialGeneration2Collections = _previousGeneration2Collections = GC.CollectionCount(2);
        try
        {
            _process = DiagnosticsProcess.GetCurrentProcess();
            _previousProcessorTime = _process.TotalProcessorTime;
            _processMetricsAvailable = true;
        }
        catch
        {
            _process = null;
            _processMetricsAvailable = false;
        }
    }

    public void ObserveFrame(int players, int recentSpeakers, int activeLooks)
    {
        _highWater.ObserveFrame(
            NonNegative(players),
            NonNegative(recentSpeakers),
            NonNegative(activeLooks));
    }

    /// <summary>
    /// Records a cheap exact counter independently of timing/profiling state. The main adapter
    /// calls this for every outcome so security and lifecycle totals remain observable when
    /// profiling is disabled, frozen, or its active timing window never closes.
    /// </summary>
    public void RecordCounter(PerformanceCounter counter, long amount)
    {
        int index = (int)counter;
        if (index < 0 || index >= _exactSinceEnableCounters.Length)
            throw new ArgumentOutOfRangeException(nameof(counter));

        switch (PerformanceCounterCatalog.Aggregation(counter))
        {
            case PerformanceCounterAggregation.Last:
                _exactSinceEnableCounters[index] = amount > 0L ? amount : 0L;
                break;
            case PerformanceCounterAggregation.Maximum:
                if (amount > _exactSinceEnableCounters[index])
                    _exactSinceEnableCounters[index] = amount;
                break;
            default:
                _exactSinceEnableCounters[index] = SaturatingAdd(
                    _exactSinceEnableCounters[index],
                    amount);
                break;
        }

        _highWater.ObserveCounter(counter, amount);
        _hasExactCounterObservations = true;
    }

    public void ObserveGovernorDecision(GovernorDecision decision)
    {
        _lastGovernorReason = decision.Reason;
        if (decision.Reason == GovernorReason.RecoveryProbeSucceeded)
            _recoveryProbeAccepted++;
        else if (decision.Reason == GovernorReason.RecoveryProbeFailed)
            _recoveryProbeRejected++;

        if (!decision.Changed)
            return;

        _transitionTotal++;
        if (decision.CurrentTier > decision.PreviousTier)
            _transitionDegradations++;
        else
            _transitionRecoveries++;
        if (decision.Reason == GovernorReason.HardTalkyBudget ||
            decision.Reason == GovernorReason.ServerEmergency)
        {
            _transitionEmergencies++;
        }
    }

    public void SetIdentity(
        TalkyDiagnosticsLifecycleState lifecycle,
        TalkyDiagnosticsTrustState harmony,
        TalkyDiagnosticsTrustState cedMod,
        bool cedModSyncModeKnown,
        CedModLookSyncMode cedModSyncMode)
    {
        _lifecycleState = lifecycle;
        _harmonyState = harmony;
        _cedModState = cedMod;
        _cedModSyncModeKnown = cedModSyncModeKnown;
        _cedModSyncMode = cedModSyncMode;
    }

    public void SetLifecycleState(TalkyDiagnosticsLifecycleState lifecycle)
    {
        _lifecycleState = lifecycle;
    }

    public void SetCompatibilityIdentity(
        TalkyDiagnosticsTrustState harmony,
        TalkyDiagnosticsTrustState cedMod,
        bool cedModSyncModeKnown,
        CedModLookSyncMode cedModSyncMode)
    {
        _harmonyState = harmony;
        _cedModState = cedMod;
        _cedModSyncModeKnown = cedModSyncModeKnown;
        _cedModSyncMode = cedModSyncMode;
    }

    public TalkyDiagnosticsSnapshot Publish(
        PerformanceProfiler profiler,
        PerformanceWindow window,
        double publishedAtSeconds,
        int evaluationWindowSeconds,
        PerformanceMode mode,
        PerformanceTier effectiveTier,
        PerformanceTier committedTier,
        bool recoveryProbeActive,
        bool profilingEnabled,
        bool frozen,
        double actualTickRate = 0d,
        double targetTickRate = 0d)
    {
        if (profiler == null)
            throw new ArgumentNullException(nameof(profiler));
        if (window == null)
            throw new ArgumentNullException(nameof(window));

        _latest.Reset();
        _latest.AddWindow(window);
        TalkyDiagnosticsInterval latestInterval = _latest.CreateInterval();

        _rolling.Reset();
        long horizonTicks = checked(
            profiler.TimestampFrequency * (long)TalkyDiagnosticsSnapshot.RollingHorizonSeconds);
        long cutoff = window.EndTimestamp > horizonTicks
            ? window.EndTimestamp - horizonTicks
            : 0L;

        // Add oldest to newest so latest-value gauges retain the newest completed observation.
        // Only complete windows wholly contained by the horizon are included. This keeps exact
        // event counts and never labels (for example) 80 seconds from two 40-second windows as a
        // 60-second aggregate. DurationSeconds exposes the exact covered duration when the
        // configured window does not divide 60 evenly.
        for (int age = profiler.HistoryCapacity - 1; age >= 0; age--)
        {
            PerformanceWindow historical = profiler.GetCompletedWindow(age);
            if (historical == null || historical.StartTimestamp < cutoff)
                continue;
            _rolling.AddWindow(historical);
        }

        _sinceEnable.AddWindow(window);
        TalkyDiagnosticsInterval rollingInterval = _rolling.CreateInterval();
        TalkyDiagnosticsInterval sinceEnableInterval = WithExactCounters(
            _sinceEnable.CreateInterval());
        _highWater.ObserveWindow(latestInterval);

        _lastCompletedLatest = latestInterval;
        _lastCompletedRolling = rollingInterval;
        return new TalkyDiagnosticsSnapshot(
            publishedAtSeconds,
            evaluationWindowSeconds,
            mode,
            effectiveTier,
            committedTier,
            recoveryProbeActive,
            profilingEnabled,
            frozen,
            latestInterval,
            rollingInterval,
            sinceEnableInterval,
            _highWater.CreateSnapshot(),
            CaptureSystem(publishedAtSeconds, actualTickRate, targetTickRate),
            CreateTransitions(),
            CreateIdentity());
    }

    /// <summary>
    /// Publishes an allocation-bounded state heartbeat when profiling is explicitly disabled or
    /// frozen. No synthetic timings or event counts are invented; prior since-enable/high-water
    /// aggregates remain available.
    /// </summary>
    public TalkyDiagnosticsSnapshot PublishHeartbeat(
        double publishedAtSeconds,
        int evaluationWindowSeconds,
        PerformanceMode mode,
        PerformanceTier effectiveTier,
        PerformanceTier committedTier,
        bool recoveryProbeActive,
        bool profilingEnabled,
        bool frozen,
        bool retainCompletedIntervals = false,
        double actualTickRate = 0d,
        double targetTickRate = 0d)
    {
        TalkyDiagnosticsInterval latestInterval;
        TalkyDiagnosticsInterval rollingInterval;
        TalkyDiagnosticsInterval sinceEnableInterval;
        if (retainCompletedIntervals && _lastCompletedLatest != null)
        {
            latestInterval = _lastCompletedLatest;
            rollingInterval = _lastCompletedRolling;
            sinceEnableInterval = WithExactCounters(_sinceEnable.CreateInterval());
        }
        else
        {
            _latest.Reset();
            _rolling.Reset();
            latestInterval = _latest.CreateInterval();
            rollingInterval = _rolling.CreateInterval();
            sinceEnableInterval = WithExactCounters(_sinceEnable.CreateInterval());
        }

        return new TalkyDiagnosticsSnapshot(
            publishedAtSeconds,
            evaluationWindowSeconds,
            mode,
            effectiveTier,
            committedTier,
            recoveryProbeActive,
            profilingEnabled,
            frozen,
            latestInterval,
            rollingInterval,
            sinceEnableInterval,
            _highWater.CreateSnapshot(),
            CaptureSystem(publishedAtSeconds, actualTickRate, targetTickRate),
            CreateTransitions(),
            CreateIdentity());
    }

    /// <summary>Forgets completed-window views after a profiler reset without erasing since-enable data.</summary>
    public void ClearCompletedIntervals()
    {
        _lastCompletedLatest = null;
        _lastCompletedRolling = null;
    }

    /// <summary>Starts a new round high-water scope without erasing since-enable totals.</summary>
    public void ResetRoundHighWater()
    {
        _highWater.Reset();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            _process?.Dispose();
        }
        catch
        {
            // Diagnostics teardown must never interfere with plugin teardown.
        }
    }

    private TalkyDiagnosticsSystem CaptureSystem(
        double publishedAtSeconds,
        double actualTickRate,
        double targetTickRate)
    {
        int generation0 = GC.CollectionCount(0);
        int generation1 = GC.CollectionCount(1);
        int generation2 = GC.CollectionCount(2);
        int generation0Delta = NonNegativeDelta(generation0, _previousGeneration0Collections);
        int generation1Delta = NonNegativeDelta(generation1, _previousGeneration1Collections);
        int generation2Delta = NonNegativeDelta(generation2, _previousGeneration2Collections);
        _previousGeneration0Collections = generation0;
        _previousGeneration1Collections = generation1;
        _previousGeneration2Collections = generation2;

        long managedHeapBytes = 0L;
        try
        {
            managedHeapBytes = GC.GetTotalMemory(false);
        }
        catch
        {
            // A missing process sample is represented by ProcessMetricsAvailable=false.
        }

        long residentSetBytes = 0L;
        double processCpuPercent = 0d;
        bool available = _processMetricsAvailable && !_disposed && _process != null;
        if (available)
        {
            try
            {
                _process.Refresh();
                residentSetBytes = _process.WorkingSet64;
                TimeSpan processorTime = _process.TotalProcessorTime;
                double elapsedSeconds = publishedAtSeconds - _previousProcessSampleAt;
                if (elapsedSeconds > 0d &&
                    !double.IsNaN(_previousProcessSampleAt) &&
                    !double.IsInfinity(elapsedSeconds))
                {
                    double processorSeconds =
                        (processorTime - _previousProcessorTime).TotalSeconds;
                    processCpuPercent = 100d * processorSeconds /
                                        (elapsedSeconds * Math.Max(1, Environment.ProcessorCount));
                    if (processCpuPercent < 0d || double.IsNaN(processCpuPercent))
                        processCpuPercent = 0d;
                    else if (processCpuPercent > 100d || double.IsInfinity(processCpuPercent))
                        processCpuPercent = 100d;
                }

                _previousProcessorTime = processorTime;
                _previousProcessSampleAt = publishedAtSeconds;
            }
            catch
            {
                available = false;
                _processMetricsAvailable = false;
                residentSetBytes = 0L;
                processCpuPercent = 0d;
            }
        }

        return new TalkyDiagnosticsSystem(
            available,
            SanitizeNonNegative(actualTickRate),
            SanitizeNonNegative(targetTickRate),
            processCpuPercent,
            residentSetBytes > 0L ? residentSetBytes : 0L,
            managedHeapBytes > 0L ? managedHeapBytes : 0L,
            generation0Delta,
            generation1Delta,
            generation2Delta,
            NonNegativeDelta(generation0, _initialGeneration0Collections),
            NonNegativeDelta(generation1, _initialGeneration1Collections),
            NonNegativeDelta(generation2, _initialGeneration2Collections));
    }

    private TalkyDiagnosticsTransitions CreateTransitions()
    {
        return new TalkyDiagnosticsTransitions(
            _transitionTotal,
            _transitionDegradations,
            _transitionRecoveries,
            _transitionEmergencies,
            _recoveryProbeAccepted,
            _recoveryProbeRejected,
            _lastGovernorReason);
    }

    private TalkyDiagnosticsIdentity CreateIdentity()
    {
        return new TalkyDiagnosticsIdentity(
            _lifecycleState,
            _harmonyState,
            _cedModState,
            _cedModSyncModeKnown,
            _cedModSyncMode);
    }

    private static int NonNegativeDelta(int current, int previous)
    {
        return current >= previous ? current - previous : 0;
    }

    private static double SanitizeNonNegative(double value)
    {
        return value >= 0d && !double.IsNaN(value) && !double.IsInfinity(value) ? value : 0d;
    }

    private static int NonNegative(int value)
    {
        return value > 0 ? value : 0;
    }

    private TalkyDiagnosticsInterval WithExactCounters(TalkyDiagnosticsInterval interval)
    {
        if (!_hasExactCounterObservations)
            return interval;

        var counters = new long[_exactSinceEnableCounters.Length];
        Array.Copy(_exactSinceEnableCounters, counters, counters.Length);
        return interval.WithCounters(counters);
    }

    private static long SaturatingAdd(long current, long amount)
    {
        if (amount > 0L && current > long.MaxValue - amount)
            return long.MaxValue;
        if (amount < 0L && current < long.MinValue - amount)
            return long.MinValue;
        return current + amount;
    }
}

/// <summary>
/// Reusable cold-path aggregator. Adding windows and constructing a publication happens once per
/// evaluation window, never once per voice packet or sync record.
/// </summary>
internal sealed class PerformanceDiagnosticsAccumulator
{
    private readonly FrameHistogramAccumulator _frameHistogram = new FrameHistogramAccumulator();
    private readonly long[] _metricCalls = new long[(int)PerformanceMetric.Count];
    private readonly long[] _metricSamples = new long[(int)PerformanceMetric.Count];
    private readonly double[] _metricEstimatedMilliseconds = new double[(int)PerformanceMetric.Count];
    private readonly double[] _metricSampleMilliseconds = new double[(int)PerformanceMetric.Count];
    private readonly double[] _metricMaxMilliseconds = new double[(int)PerformanceMetric.Count];
    private readonly long[] _metricHistogram =
        new long[(int)PerformanceMetric.Count * PerformanceWindow.HistogramBucketCount];
    private readonly long[] _counters = new long[(int)PerformanceCounter.Count];
    private readonly double[] _subsystemMilliseconds =
        new double[(int)PerformanceSubsystem.Count];

    private long _windows;
    private long _frames;
    private double _durationSeconds;
    private double _talkyMilliseconds;
    private double _worstWindowP95Milliseconds;

    public bool HasWindows => _windows != 0L;

    public void AddWindow(PerformanceWindow window)
    {
        if (window == null)
            throw new ArgumentNullException(nameof(window));

        _windows++;
        _frames += window.FrameCount;
        _durationSeconds += window.DurationSeconds;
        _talkyMilliseconds += window.TotalTalkyMilliseconds;
        _frameHistogram.Add(window);
        if (window.FrameP95Milliseconds > _worstWindowP95Milliseconds)
            _worstWindowP95Milliseconds = window.FrameP95Milliseconds;

        PerformanceBreakdown breakdown = PerformanceBreakdown.FromWindow(window);
        for (int index = 1; index < (int)PerformanceSubsystem.Count; index++)
        {
            PerformanceSubsystem subsystem = (PerformanceSubsystem)index;
            _subsystemMilliseconds[index] += breakdown.GetMilliseconds(subsystem);
        }

        for (int index = 0; index < (int)PerformanceMetric.Count; index++)
        {
            PerformanceMetric metric = (PerformanceMetric)index;
            MetricSnapshot value = window.GetMetric(metric);
            _metricCalls[index] += value.CallCount;
            _metricSamples[index] += value.SampleCount;
            _metricEstimatedMilliseconds[index] += value.EstimatedTotalMilliseconds;
            _metricSampleMilliseconds[index] += value.AverageMilliseconds * value.SampleCount;
            if (value.MaxMilliseconds > _metricMaxMilliseconds[index])
                _metricMaxMilliseconds[index] = value.MaxMilliseconds;
            window.AccumulateMetricHistogram(index, _metricHistogram);
        }

        for (int index = 0; index < (int)PerformanceCounter.Count; index++)
        {
            PerformanceCounter counter = (PerformanceCounter)index;
            long value = window.GetCounter(counter);
            switch (PerformanceCounterCatalog.Aggregation(counter))
            {
                case PerformanceCounterAggregation.Last:
                    _counters[index] = value;
                    break;
                case PerformanceCounterAggregation.Maximum:
                    if (value > _counters[index])
                        _counters[index] = value;
                    break;
                default:
                    _counters[index] += value;
                    break;
            }
        }
    }

    public TalkyDiagnosticsInterval CreateInterval()
    {
        var metrics = new MetricSnapshot[(int)PerformanceMetric.Count];
        for (int index = 0; index < metrics.Length; index++)
        {
            long samples = _metricSamples[index];
            metrics[index] = new MetricSnapshot(
                _metricCalls[index],
                samples,
                _metricEstimatedMilliseconds[index],
                samples > 0L ? _metricSampleMilliseconds[index] / samples : 0d,
                MetricPercentileMilliseconds(index, samples, 0.50d),
                MetricPercentileMilliseconds(index, samples, 0.95d),
                MetricPercentileMilliseconds(index, samples, 0.99d),
                _metricMaxMilliseconds[index]);
        }

        var counters = new long[_counters.Length];
        Array.Copy(_counters, counters, counters.Length);
        var subsystems = new double[_subsystemMilliseconds.Length];
        Array.Copy(_subsystemMilliseconds, subsystems, subsystems.Length);

        return new TalkyDiagnosticsInterval(
            _windows,
            _durationSeconds,
            _frames,
            _talkyMilliseconds,
            _frameHistogram.AverageMilliseconds,
            _frameHistogram.P50Milliseconds,
            _frameHistogram.P95Milliseconds,
            _frameHistogram.P99Milliseconds,
            _frameHistogram.MaxMilliseconds,
            _worstWindowP95Milliseconds,
            metrics,
            counters,
            subsystems);
    }

    public void Reset()
    {
        _windows = 0L;
        _frames = 0L;
        _durationSeconds = 0d;
        _talkyMilliseconds = 0d;
        _worstWindowP95Milliseconds = 0d;
        Array.Clear(_metricCalls, 0, _metricCalls.Length);
        Array.Clear(_metricSamples, 0, _metricSamples.Length);
        Array.Clear(_metricEstimatedMilliseconds, 0, _metricEstimatedMilliseconds.Length);
        Array.Clear(_metricSampleMilliseconds, 0, _metricSampleMilliseconds.Length);
        Array.Clear(_metricMaxMilliseconds, 0, _metricMaxMilliseconds.Length);
        Array.Clear(_metricHistogram, 0, _metricHistogram.Length);
        Array.Clear(_counters, 0, _counters.Length);
        Array.Clear(_subsystemMilliseconds, 0, _subsystemMilliseconds.Length);
        _frameHistogram.Reset();
    }

    private double MetricPercentileMilliseconds(
        int metricIndex,
        long sampleCount,
        double percentile)
    {
        if (sampleCount == 0L)
            return 0d;

        long target = (long)Math.Ceiling(sampleCount * percentile);
        long cumulative = 0L;
        int baseIndex = metricIndex * PerformanceWindow.HistogramBucketCount;
        for (int bucket = 0; bucket < PerformanceWindow.HistogramBucketCount; bucket++)
        {
            cumulative += _metricHistogram[baseIndex + bucket];
            if (cumulative >= target)
                return DurationHistogram.BucketUpperMilliseconds(bucket);
        }

        return _metricMaxMilliseconds[metricIndex];
    }
}

internal sealed class TalkyDiagnosticsHighWaterTracker
{
    private double _talkyMillisecondsPerFrame;
    private double _talkyMillisecondsPerSecond;
    private double _frameP95Milliseconds;
    private double _frameMaxMilliseconds;
    private int _players;
    private int _recentSpeakers;
    private int _activeLooks;
    private double _voicePacketsPerSecond;
    private double _voiceBytesPerSecond;
    private double _settingsResponsesPerSecond;
    private double _animationRpcSendsPerSecond;
    private double _logicalSyncRecordsPerSecond;
    private double _logicalSyncRewritesPerSecond;
    private int _simultaneousSpeakers;
    private int _speechTrackers;
    private int _lookTrackers;
    private long _pendingActionsCurrentMaximum;
    private long _pendingActionsHighWater;

    public void ObserveFrame(int players, int recentSpeakers, int activeLooks)
    {
        if (players > _players)
            _players = players;
        if (recentSpeakers > _recentSpeakers)
            _recentSpeakers = recentSpeakers;
        if (activeLooks > _activeLooks)
            _activeLooks = activeLooks;
    }

    public void ObserveCounter(PerformanceCounter counter, long value)
    {
        switch (counter)
        {
            case PerformanceCounter.PendingActionsCurrent:
                if (value > _pendingActionsCurrentMaximum)
                    _pendingActionsCurrentMaximum = value;
                break;
            case PerformanceCounter.PendingActionsHighWater:
                if (value > _pendingActionsHighWater)
                    _pendingActionsHighWater = value;
                break;
            case PerformanceCounter.SimultaneousSpeakersCurrent:
            case PerformanceCounter.SimultaneousSpeakersHighWater:
                if (value > _simultaneousSpeakers)
                    _simultaneousSpeakers = ToInt32Saturated(value);
                break;
            case PerformanceCounter.SpeechTrackersCurrent:
                if (value > _speechTrackers)
                    _speechTrackers = ToInt32Saturated(value);
                break;
            case PerformanceCounter.LookTrackersCurrent:
                if (value > _lookTrackers)
                    _lookTrackers = ToInt32Saturated(value);
                break;
        }
    }

    public void ObserveWindow(TalkyDiagnosticsInterval window)
    {
        if (window == null)
            throw new ArgumentNullException(nameof(window));

        Max(ref _talkyMillisecondsPerFrame, window.TalkyMillisecondsPerFrame);
        Max(ref _talkyMillisecondsPerSecond, window.TalkyMillisecondsPerSecond);
        Max(ref _frameP95Milliseconds, window.FrameP95Milliseconds);
        Max(ref _frameMaxMilliseconds, window.FrameMaxMilliseconds);
        Max(ref _voicePacketsPerSecond, window.GetCounterPerSecond(PerformanceCounter.VoicePackets));
        Max(ref _voiceBytesPerSecond, window.GetCounterPerSecond(PerformanceCounter.VoiceBytes));
        Max(ref _settingsResponsesPerSecond, window.GetCounterPerSecond(PerformanceCounter.SettingsResponses));
        Max(ref _animationRpcSendsPerSecond, window.GetCounterPerSecond(PerformanceCounter.AnimationRpcSends));
        Max(ref _logicalSyncRecordsPerSecond, window.GetCounterPerSecond(PerformanceCounter.LogicalSyncRecords));
        Max(ref _logicalSyncRewritesPerSecond, window.GetCounterPerSecond(PerformanceCounter.LogicalSyncRewrites));

        long simultaneousSpeakers = window.GetCounter(
            PerformanceCounter.SimultaneousSpeakersHighWater);
        if (simultaneousSpeakers > _simultaneousSpeakers)
            _simultaneousSpeakers = ToInt32Saturated(simultaneousSpeakers);
        long speechTrackers = window.GetCounter(PerformanceCounter.SpeechTrackersCurrent);
        if (speechTrackers > _speechTrackers)
            _speechTrackers = ToInt32Saturated(speechTrackers);
        long lookTrackers = window.GetCounter(PerformanceCounter.LookTrackersCurrent);
        if (lookTrackers > _lookTrackers)
            _lookTrackers = ToInt32Saturated(lookTrackers);

        long current = window.GetCounter(PerformanceCounter.PendingActionsCurrent);
        if (current > _pendingActionsCurrentMaximum)
            _pendingActionsCurrentMaximum = current;
        long highWater = window.GetCounter(PerformanceCounter.PendingActionsHighWater);
        if (highWater > _pendingActionsHighWater)
            _pendingActionsHighWater = highWater;
    }

    public TalkyDiagnosticsHighWater CreateSnapshot()
    {
        return new TalkyDiagnosticsHighWater(
            _talkyMillisecondsPerFrame,
            _talkyMillisecondsPerSecond,
            _frameP95Milliseconds,
            _frameMaxMilliseconds,
            _players,
            _recentSpeakers,
            _activeLooks,
            _voicePacketsPerSecond,
            _voiceBytesPerSecond,
            _settingsResponsesPerSecond,
            _animationRpcSendsPerSecond,
            _logicalSyncRecordsPerSecond,
            _logicalSyncRewritesPerSecond,
            _simultaneousSpeakers,
            _speechTrackers,
            _lookTrackers,
            _pendingActionsCurrentMaximum,
            _pendingActionsHighWater);
    }

    public void Reset()
    {
        _talkyMillisecondsPerFrame = 0d;
        _talkyMillisecondsPerSecond = 0d;
        _frameP95Milliseconds = 0d;
        _frameMaxMilliseconds = 0d;
        _players = 0;
        _recentSpeakers = 0;
        _activeLooks = 0;
        _voicePacketsPerSecond = 0d;
        _voiceBytesPerSecond = 0d;
        _settingsResponsesPerSecond = 0d;
        _animationRpcSendsPerSecond = 0d;
        _logicalSyncRecordsPerSecond = 0d;
        _logicalSyncRewritesPerSecond = 0d;
        _simultaneousSpeakers = 0;
        _speechTrackers = 0;
        _lookTrackers = 0;
        _pendingActionsCurrentMaximum = 0L;
        _pendingActionsHighWater = 0L;
    }

    private static void Max(ref double destination, double value)
    {
        if (value > destination && !double.IsNaN(value) && !double.IsInfinity(value))
            destination = value;
    }

    private static int ToInt32Saturated(long value)
    {
        if (value <= 0L)
            return 0;
        return value >= int.MaxValue ? int.MaxValue : (int)value;
    }
}
