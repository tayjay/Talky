using System;

namespace Talky.Performance;

public enum PerformanceImpactOutcome
{
    Observed = 0,
    RecoveryAccepted = 1,
    RecoveryRejected = 2,
    Interrupted = 3,
}

/// <summary>
/// Normalized measurements from one side of a tier transition. Server percentiles are merged
/// from raw frame histograms rather than averaged from per-window percentile values.
/// </summary>
public readonly struct PerformanceImpactSnapshot
{
    internal PerformanceImpactSnapshot(
        long windowCount,
        long frameCount,
        double durationSeconds,
        double talkyMillisecondsPerFrame,
        double frameP95Milliseconds,
        double frameP99Milliseconds,
        double voiceMillisecondsPerFrame,
        double faceMillisecondsPerFrame,
        double lookMillisecondsPerFrame,
        double schedulerMillisecondsPerFrame,
        double syncMillisecondsPerFrame,
        double averagePlayers,
        double averageRecentSpeakers,
        double averageActiveLooks,
        double voicePacketsPerSecond,
        double syncEntriesPerSecond,
        double rewritesPerSecond)
    {
        WindowCount = windowCount;
        FrameCount = frameCount;
        DurationSeconds = durationSeconds;
        TalkyMillisecondsPerFrame = talkyMillisecondsPerFrame;
        FrameP95Milliseconds = frameP95Milliseconds;
        FrameP99Milliseconds = frameP99Milliseconds;
        VoiceMillisecondsPerFrame = voiceMillisecondsPerFrame;
        FaceMillisecondsPerFrame = faceMillisecondsPerFrame;
        LookMillisecondsPerFrame = lookMillisecondsPerFrame;
        SchedulerMillisecondsPerFrame = schedulerMillisecondsPerFrame;
        SyncMillisecondsPerFrame = syncMillisecondsPerFrame;
        AveragePlayers = averagePlayers;
        AverageRecentSpeakers = averageRecentSpeakers;
        AverageActiveLooks = averageActiveLooks;
        VoicePacketsPerSecond = voicePacketsPerSecond;
        SyncEntriesPerSecond = syncEntriesPerSecond;
        RewritesPerSecond = rewritesPerSecond;
    }

    public long WindowCount { get; }
    public long FrameCount { get; }
    public bool HasData => WindowCount > 0L && FrameCount > 0L;
    public double DurationSeconds { get; }
    public double TalkyMillisecondsPerFrame { get; }
    public double FrameP95Milliseconds { get; }
    public double FrameP99Milliseconds { get; }
    public double VoiceMillisecondsPerFrame { get; }
    public double FaceMillisecondsPerFrame { get; }
    public double LookMillisecondsPerFrame { get; }
    public double SchedulerMillisecondsPerFrame { get; }
    public double SyncMillisecondsPerFrame { get; }
    public double AveragePlayers { get; }
    public double AverageRecentSpeakers { get; }
    public double AverageActiveLooks { get; }
    public double VoicePacketsPerSecond { get; }
    public double SyncEntriesPerSecond { get; }
    public double RewritesPerSecond { get; }
}

public readonly struct PerformanceTransitionImpact
{
    internal PerformanceTransitionImpact(
        long sequence,
        PerformanceTier fromTier,
        PerformanceTier toTier,
        GovernorReason transitionReason,
        GovernorReason completionReason,
        PerformanceImpactOutcome outcome,
        double startedAt,
        double completedAt,
        double observationSeconds,
        PerformanceImpactSnapshot before,
        PerformanceImpactSnapshot after)
    {
        Sequence = sequence;
        FromTier = fromTier;
        ToTier = toTier;
        TransitionReason = transitionReason;
        CompletionReason = completionReason;
        Outcome = outcome;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        ObservationSeconds = observationSeconds;
        Before = before;
        After = after;
    }

    public long Sequence { get; }
    public PerformanceTier FromTier { get; }
    public PerformanceTier ToTier { get; }
    public GovernorReason TransitionReason { get; }
    public GovernorReason CompletionReason { get; }
    public PerformanceImpactOutcome Outcome { get; }
    public double StartedAt { get; }
    public double CompletedAt { get; }
    public double ObservationSeconds { get; }
    public PerformanceImpactSnapshot Before { get; }
    public PerformanceImpactSnapshot After { get; }
    public bool HasCompleteBaseline =>
        Before.DurationSeconds >= ObservationSeconds;
    public bool HasCompleteAfter =>
        After.DurationSeconds >= ObservationSeconds;
}

/// <summary>
/// Keeps a bounded scalar/reference history around rare tier transitions. Completed profiler
/// windows are copied into reusable accumulators before their profiler-ring slots can be reused.
/// No player identities are retained.
/// </summary>
public sealed class PerformanceTransitionTracker
{
    private struct WindowObservation
    {
        public PerformanceWindow Window;
        public long EndTimestamp;
        public PerformanceBreakdown Breakdown;
        public PerformanceTier Tier;
        public bool ServerHealthEligible;
    }

    private sealed class ImpactAccumulator
    {
        private readonly PerformanceSummaryAccumulator _summary =
            new PerformanceSummaryAccumulator();

        public double DurationSeconds => _summary.DurationSeconds;

        public void Add(
            PerformanceWindow window,
            PerformanceBreakdown breakdown)
        {
            _summary.AddWindow(window, breakdown);
        }

        public PerformanceImpactSnapshot Snapshot()
        {
            double duration = _summary.DurationSeconds;
            double inverseDuration = duration > 0d ? 1d / duration : 0d;
            double inverseFrameCount =
                _summary.FrameCount > 0L ? 1d / _summary.FrameCount : 0d;

            return new PerformanceImpactSnapshot(
                _summary.WindowCount,
                _summary.FrameCount,
                duration,
                _summary.TalkyMillisecondsPerFrame,
                _summary.FrameP95Milliseconds,
                _summary.FrameP99Milliseconds,
                _summary.GetSubsystemMillisecondsPerFrame(PerformanceSubsystem.Voice),
                _summary.GetSubsystemMillisecondsPerFrame(PerformanceSubsystem.Face),
                _summary.GetSubsystemMillisecondsPerFrame(PerformanceSubsystem.Look),
                _summary.GetSubsystemMillisecondsPerFrame(PerformanceSubsystem.Scheduler),
                _summary.GetSubsystemMillisecondsPerFrame(PerformanceSubsystem.Sync),
                _summary.GetCounter(PerformanceCounter.TrackedPlayers) * inverseFrameCount,
                _summary.GetCounter(PerformanceCounter.RecentSpeakers) * inverseFrameCount,
                _summary.GetCounter(PerformanceCounter.ActiveLookTargets) * inverseFrameCount,
                _summary.GetCounter(PerformanceCounter.VoicePackets) * inverseDuration,
                _summary.GetCounter(PerformanceCounter.SyncEntries) * inverseDuration,
                _summary.GetCounter(PerformanceCounter.Rewrites) * inverseDuration);
        }

        public void Reset(double nowSeconds)
        {
            _summary.Reset(nowSeconds);
        }
    }

    private readonly WindowObservation[] _history;
    private readonly ImpactAccumulator _before = new ImpactAccumulator();
    private readonly ImpactAccumulator _after = new ImpactAccumulator();

    private int _historyWriteIndex;
    private int _historyCount;
    private long _nextSequence;
    private long _activeSequence;
    private PerformanceTier _fromTier;
    private PerformanceTier _toTier;
    private GovernorReason _transitionReason;
    private double _startedAt;
    private double _observationSeconds;

    public PerformanceTransitionTracker(int historyCapacity = 60)
    {
        if (historyCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(historyCapacity));

        _history = new WindowObservation[historyCapacity];
    }

    public bool IsActive { get; private set; }
    public bool IsRecoveryObservation =>
        IsActive && _transitionReason == GovernorReason.Recovery;
    public PerformanceTier FromTier => _fromTier;
    public PerformanceTier ToTier => _toTier;

    public void RecordWindow(
        PerformanceWindow window,
        PerformanceBreakdown breakdown,
        PerformanceTier tier,
        bool serverHealthEligible)
    {
        if (window == null)
            throw new ArgumentNullException(nameof(window));
        if (!IsValidTier(tier))
            throw new ArgumentOutOfRangeException(nameof(tier));

        if (IsActive && tier == _toTier)
        {
            if (serverHealthEligible)
            {
                _after.Add(window, breakdown);
            }
            else
            {
                // Impact and recovery comparisons only use a continuous active-server period.
                _after.Reset(0d);
            }
        }

        _history[_historyWriteIndex] = new WindowObservation
        {
            Window = window,
            EndTimestamp = window.EndTimestamp,
            Breakdown = breakdown,
            Tier = tier,
            ServerHealthEligible = serverHealthEligible,
        };

        _historyWriteIndex++;
        if (_historyWriteIndex == _history.Length)
            _historyWriteIndex = 0;
        if (_historyCount < _history.Length)
            _historyCount++;
    }

    public void Start(
        PerformanceTier fromTier,
        PerformanceTier toTier,
        GovernorReason transitionReason,
        double nowSeconds,
        int observationSeconds)
    {
        if (!IsValidTier(fromTier))
            throw new ArgumentOutOfRangeException(nameof(fromTier));
        if (!IsValidTier(toTier))
            throw new ArgumentOutOfRangeException(nameof(toTier));
        if (fromTier == toTier)
            throw new ArgumentException("A transition must change tiers.", nameof(toTier));
        if (!IsFinite(nowSeconds))
            throw new ArgumentOutOfRangeException(nameof(nowSeconds));
        if (observationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(observationSeconds));

        IsActive = false;
        _before.Reset(nowSeconds);
        _after.Reset(nowSeconds);

        int index = _historyWriteIndex;
        for (int age = 0; age < _historyCount; age++)
        {
            index--;
            if (index < 0)
                index = _history.Length - 1;

            WindowObservation observation = _history[index];
            if (observation.Window == null ||
                observation.Window.EndTimestamp != observation.EndTimestamp ||
                observation.Tier != fromTier ||
                !observation.ServerHealthEligible)
            {
                break;
            }

            _before.Add(observation.Window, observation.Breakdown);
            if (_before.DurationSeconds >= observationSeconds)
                break;
        }

        _activeSequence = ++_nextSequence;
        _fromTier = fromTier;
        _toTier = toTier;
        _transitionReason = transitionReason;
        _startedAt = nowSeconds;
        _observationSeconds = observationSeconds;
        IsActive = true;
    }

    public bool TryComplete(double nowSeconds, out PerformanceTransitionImpact impact)
    {
        if (!IsActive ||
            !IsFinite(nowSeconds) ||
            nowSeconds - _startedAt < _observationSeconds ||
            _after.DurationSeconds < _observationSeconds)
        {
            impact = default;
            return false;
        }

        return TryFinish(
            nowSeconds,
            PerformanceImpactOutcome.Observed,
            GovernorReason.None,
            out impact);
    }

    public bool TryFinish(
        double nowSeconds,
        PerformanceImpactOutcome outcome,
        GovernorReason completionReason,
        out PerformanceTransitionImpact impact)
    {
        if (!IsActive || !IsFinite(nowSeconds))
        {
            impact = default;
            return false;
        }

        impact = new PerformanceTransitionImpact(
            _activeSequence,
            _fromTier,
            _toTier,
            _transitionReason,
            completionReason,
            outcome,
            _startedAt,
            nowSeconds,
            _observationSeconds,
            _before.Snapshot(),
            _after.Snapshot());

        IsActive = false;
        _before.Reset(nowSeconds);
        _after.Reset(nowSeconds);
        return true;
    }

    public void Reset()
    {
        Array.Clear(_history, 0, _history.Length);
        _historyWriteIndex = 0;
        _historyCount = 0;
        IsActive = false;
        _before.Reset(0d);
        _after.Reset(0d);
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static bool IsValidTier(PerformanceTier tier)
    {
        return tier >= PerformanceTier.Full &&
               tier <= PerformanceTier.FaceOnlyReducedRate;
    }
}
