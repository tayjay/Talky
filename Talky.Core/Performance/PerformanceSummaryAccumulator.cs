using System;
using System.Runtime.CompilerServices;

namespace Talky.Performance;

/// <summary>
/// Reusable accumulator for periodic performance reports.
/// Window and population updates allocate nothing; formatting occurs only on the cold log path.
/// </summary>
public sealed class PerformanceSummaryAccumulator
{
    private readonly FrameHistogramAccumulator _frameHistogram = new FrameHistogramAccumulator();
    private readonly double[] _subsystemMilliseconds =
        new double[(int)PerformanceSubsystem.Count];
    private readonly long[] _counters = new long[(int)PerformanceCounter.Count];

    private double _durationSeconds;
    private double _talkyMilliseconds;
    private double _worstWindowP95Milliseconds;
    private double _smoothDeltaMilliseconds;
    private double _targetTickrate;
    private long _frames;
    private long _windows;
    private long _smoothSamples;
    private long _populationSamples;
    private long _players;
    private long _recentSpeakers;
    private long _activeLooks;
    private bool _hasTierObservations;
    private PerformanceTier _firstObservedTier;
    private PerformanceTier _lastObservedTier;
    private long _policyChanges;

    public double StartedAt { get; private set; }
    public long WindowCount => _windows;
    public long FrameCount => _frames;
    public bool HasWindows => _windows != 0L;
    public double DurationSeconds => _durationSeconds;
    public double TotalTalkyMilliseconds => _talkyMilliseconds;
    public double TalkyMillisecondsPerFrame =>
        _frames > 0L ? _talkyMilliseconds / _frames : 0d;
    public double TalkyMillisecondsPerSecond =>
        _durationSeconds > 0d ? _talkyMilliseconds / _durationSeconds : 0d;
    public double FrameAverageMilliseconds => _frameHistogram.AverageMilliseconds;
    public double FrameP50Milliseconds => _frameHistogram.P50Milliseconds;
    public double FrameP95Milliseconds => _frameHistogram.P95Milliseconds;
    public double FrameP99Milliseconds => _frameHistogram.P99Milliseconds;
    public double FrameMaxMilliseconds => _frameHistogram.MaxMilliseconds;
    public double WorstWindowP95Milliseconds => _worstWindowP95Milliseconds;
    public double AveragePlayers =>
        _populationSamples > 0L ? (double)_players / _populationSamples : 0d;
    public double AverageRecentSpeakers =>
        _populationSamples > 0L ? (double)_recentSpeakers / _populationSamples : 0d;
    public double AverageActiveLooks =>
        _populationSamples > 0L ? (double)_activeLooks / _populationSamples : 0d;
    public double SmoothTickrate
    {
        get
        {
            double averageDelta = _smoothSamples > 0L
                ? _smoothDeltaMilliseconds / _smoothSamples
                : 0d;
            return averageDelta > 0d ? 1000d / averageDelta : 0d;
        }
    }
    public double TargetTickrate =>
        _smoothSamples > 0L ? _targetTickrate / _smoothSamples : 0d;
    public bool HasTierObservations => _hasTierObservations;
    public PerformanceTier FirstObservedTier => _firstObservedTier;
    public PerformanceTier LastObservedTier => _lastObservedTier;
    public long PolicyChangeCount => _policyChanges;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ObserveTier(PerformanceTier tier)
    {
        if (tier < PerformanceTier.Full || tier > PerformanceTier.FaceOnlyReducedRate)
            throw new ArgumentOutOfRangeException(nameof(tier));

        if (!_hasTierObservations)
        {
            _hasTierObservations = true;
            _firstObservedTier = tier;
            _lastObservedTier = tier;
            return;
        }

        if (_lastObservedTier != tier)
        {
            _lastObservedTier = tier;
            _policyChanges++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordEligibleFrame(
        int players,
        int recentSpeakers,
        int activeLooks,
        double smoothDeltaMilliseconds,
        int serverTickrate,
        bool hasTickrateSample)
    {
        _populationSamples++;
        _players += NonNegative(players);
        _recentSpeakers += NonNegative(recentSpeakers);
        _activeLooks += NonNegative(activeLooks);

        if (hasTickrateSample)
        {
            _smoothSamples++;
            _smoothDeltaMilliseconds += smoothDeltaMilliseconds;
            _targetTickrate += serverTickrate;
        }
    }

    public void AddWindow(PerformanceWindow window, PerformanceBreakdown breakdown)
    {
        if (window == null)
            throw new ArgumentNullException(nameof(window));

        _windows++;
        _durationSeconds += window.DurationSeconds;
        _frames += window.FrameCount;
        _talkyMilliseconds += window.TotalTalkyMilliseconds;
        _frameHistogram.Add(window);
        if (window.FrameP95Milliseconds > _worstWindowP95Milliseconds)
            _worstWindowP95Milliseconds = window.FrameP95Milliseconds;

        for (int index = 1; index < (int)PerformanceSubsystem.Count; index++)
        {
            PerformanceSubsystem subsystem = (PerformanceSubsystem)index;
            _subsystemMilliseconds[index] += breakdown.GetMilliseconds(subsystem);
        }

        for (int index = 0; index < (int)PerformanceCounter.Count; index++)
        {
            _counters[index] += window.GetCounter((PerformanceCounter)index);
        }
    }

    public double GetSubsystemMilliseconds(PerformanceSubsystem subsystem)
    {
        return _subsystemMilliseconds[SubsystemIndex(subsystem)];
    }

    public double GetSubsystemMillisecondsPerFrame(PerformanceSubsystem subsystem)
    {
        return _frames > 0L ? GetSubsystemMilliseconds(subsystem) / _frames : 0d;
    }

    public double GetSubsystemShare(PerformanceSubsystem subsystem)
    {
        return _talkyMilliseconds > 0d
            ? 100d * GetSubsystemMilliseconds(subsystem) / _talkyMilliseconds
            : 0d;
    }

    public long GetCounter(PerformanceCounter counter)
    {
        return _counters[CounterIndex(counter)];
    }

    public string CreateLogMessage(
        PerformanceMode mode,
        PerformanceTier configuredTier,
        PerformanceTier effectiveTier,
        GovernorReason lastTransitionReason)
    {
        return CreateLogMessage(
            mode,
            configuredTier,
            effectiveTier,
            effectiveTier,
            false,
            lastTransitionReason);
    }

    public string CreateLogMessage(
        PerformanceMode mode,
        PerformanceTier configuredTier,
        PerformanceTier effectiveTier,
        PerformanceTier committedTier,
        bool recoveryProbeActive,
        GovernorReason lastTransitionReason)
    {
        string observedTierSpan = !_hasTierObservations
            ? "unavailable"
            : _policyChanges == 0L
                ? _firstObservedTier.ToString()
                : $"{_firstObservedTier}->{_lastObservedTier}";
        long policyChanges = _policyChanges;
        if (_hasTierObservations && _lastObservedTier != effectiveTier)
            policyChanges++;

        return
            $"Talky performance summary: mode={mode}, " +
            $"configured_tier={configuredTier}, effective_tier={effectiveTier}, " +
            $"committed_tier={committedTier}, " +
            $"recovery_probe={(recoveryProbeActive ? "active" : "none")}, " +
            $"observed_tier_span={observedTierSpan}, policy_changes={policyChanges}, " +
            $"last_trigger={lastTransitionReason}, windows={WindowCount}, " +
            $"talky={TalkyMillisecondsPerFrame:F3} ms/frame " +
            $"({TalkyMillisecondsPerSecond:F2} ms/s), " +
            $"frame_avg={FrameAverageMilliseconds:F2} ms, " +
            $"frame_p50={FrameP50Milliseconds:F2} ms, " +
            $"frame_p95={FrameP95Milliseconds:F2} ms, " +
            $"frame_p99={FrameP99Milliseconds:F2} ms, " +
            $"frame_max={FrameMaxMilliseconds:F2} ms, " +
            $"worst_window_p95={WorstWindowP95Milliseconds:F2} ms, " +
            $"smooth_tps={SmoothTickrate:F1}/{TargetTickrate:F1}, " +
            $"voice={GetSubsystemMillisecondsPerFrame(PerformanceSubsystem.Voice):F3} " +
            $"({GetSubsystemShare(PerformanceSubsystem.Voice):F1}%), " +
            $"face={GetSubsystemMillisecondsPerFrame(PerformanceSubsystem.Face):F3} " +
            $"({GetSubsystemShare(PerformanceSubsystem.Face):F1}%), " +
            $"look={GetSubsystemMillisecondsPerFrame(PerformanceSubsystem.Look):F3} " +
            $"({GetSubsystemShare(PerformanceSubsystem.Look):F1}%), " +
            $"scheduler={GetSubsystemMillisecondsPerFrame(PerformanceSubsystem.Scheduler):F3} " +
            $"({GetSubsystemShare(PerformanceSubsystem.Scheduler):F1}%), " +
            $"sync={GetSubsystemMillisecondsPerFrame(PerformanceSubsystem.Sync):F3} " +
            $"({GetSubsystemShare(PerformanceSubsystem.Sync):F1}%), " +
            $"frame_monitor_lower_bound=" +
            $"{GetSubsystemMillisecondsPerFrame(PerformanceSubsystem.Monitoring):F3} ms/frame, " +
            $"players_avg={AveragePlayers:F1}, speakers_avg={AverageRecentSpeakers:F1}, " +
            $"active_looks_avg={AverageActiveLooks:F1}, " +
            $"voice_packets={GetCounter(PerformanceCounter.VoicePackets)}, " +
            $"decoded_samples={GetCounter(PerformanceCounter.DecodedSamples)}, " +
            $"glance_candidates={GetCounter(PerformanceCounter.GlanceCandidates)}, " +
            $"sync_entries={GetCounter(PerformanceCounter.SyncEntries)}, " +
            $"active_target_hits={GetCounter(PerformanceCounter.ActiveTargetHits)}, " +
            $"rewrites={GetCounter(PerformanceCounter.Rewrites)}, " +
            $"emotion_writes={GetCounter(PerformanceCounter.EmotionWrites)}.";
    }

    public void Reset(double nowSeconds)
    {
        StartedAt = nowSeconds;
        _durationSeconds = 0d;
        _talkyMilliseconds = 0d;
        _worstWindowP95Milliseconds = 0d;
        _smoothDeltaMilliseconds = 0d;
        _targetTickrate = 0d;
        _frames = 0L;
        _windows = 0L;
        _smoothSamples = 0L;
        _populationSamples = 0L;
        _players = 0L;
        _recentSpeakers = 0L;
        _activeLooks = 0L;
        _hasTierObservations = false;
        _firstObservedTier = PerformanceTier.Full;
        _lastObservedTier = PerformanceTier.Full;
        _policyChanges = 0L;
        Array.Clear(_subsystemMilliseconds, 0, _subsystemMilliseconds.Length);
        Array.Clear(_counters, 0, _counters.Length);
        _frameHistogram.Reset();
    }

    private static int SubsystemIndex(PerformanceSubsystem subsystem)
    {
        int index = (int)subsystem;
        if (index < 0 || index >= (int)PerformanceSubsystem.Count)
            throw new ArgumentOutOfRangeException(nameof(subsystem));
        return index;
    }

    private static int CounterIndex(PerformanceCounter counter)
    {
        int index = (int)counter;
        if (index < 0 || index >= (int)PerformanceCounter.Count)
            throw new ArgumentOutOfRangeException(nameof(counter));
        return index;
    }

    private static long NonNegative(int value)
    {
        return value > 0 ? value : 0L;
    }

}
