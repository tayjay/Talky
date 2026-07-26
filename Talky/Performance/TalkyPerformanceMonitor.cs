using System;
using System.Diagnostics;
using LabApi.Features.Console;
using Talky.Settings;

namespace Talky.Performance;

/// <summary>
/// Production adapter around the allocation-conscious core profiler and adaptive governor.
/// All hot-path methods avoid formatting, LINQ, closures, and per-call object creation.
/// </summary>
public sealed class TalkyPerformanceMonitor : IDisposable
{
    public const int VoiceSampleEvery = 8;
    public const int SyncSampleEvery = 64;

    private const int HistorySeconds = 60;
    private const double DefaultSummarySeconds = 300d;

    private Config _config;
    private PerformanceProfiler _profiler;
    private AdaptivePerformanceGovernor _governor;
    private EffectivePerformancePolicy _currentPolicy;
    private readonly FrameHistogramAccumulator _summaryFrameHistogram =
        new FrameHistogramAccumulator();

    private double _lastNowSeconds;
    private bool _hasTimeOrigin;
    private bool _windowHasFrames;
    private bool _windowHealthEligible;
    private bool _profilingEnabled;
    private bool _frozen;
    private bool _disposed;
    private bool _failureLogged;

    // Five-minute aggregate. It contains counts only; no player identity enters the monitor.
    private double _summaryStartedAt;
    private double _summaryDurationSeconds;
    private double _summaryTalkyMilliseconds;
    private double _summaryWorstP95Milliseconds;
    private double _summaryVoiceMilliseconds;
    private double _summaryFaceMilliseconds;
    private double _summaryLookMilliseconds;
    private double _summarySchedulerMilliseconds;
    private double _summarySyncMilliseconds;
    private double _summaryProfilerMilliseconds;
    private double _summarySmoothDeltaMilliseconds;
    private double _summaryTargetTickrate;
    private long _summaryFrames;
    private long _summaryWindows;
    private long _summarySmoothSamples;
    private long _summaryVoicePackets;
    private long _summaryDecodedSamples;
    private long _summaryGlanceCandidates;
    private long _summarySyncEntries;
    private long _summaryActiveTargetHits;
    private long _summaryRewrites;
    private long _summaryEmotionWrites;
    private long _summaryPopulationSamples;
    private long _summaryPlayers;
    private long _summaryRecentSpeakers;
    private long _summaryActiveLooks;
    private double _lastFloorWarningAt = double.NegativeInfinity;
    private double _latestSmoothDeltaMilliseconds;
    private int _latestServerTickrate;
    private GovernorReason _lastTransitionReason;

    public TalkyPerformanceMonitor(Config config)
        : this(config, 0d, false)
    {
    }

    public TalkyPerformanceMonitor(Config config, double nowSeconds)
        : this(config, nowSeconds, true)
    {
    }

    private TalkyPerformanceMonitor(Config config, double nowSeconds, bool hasTimeOrigin)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        _config = config;
        _hasTimeOrigin = hasTimeOrigin && IsFinite(nowSeconds);
        _lastNowSeconds = _hasTimeOrigin ? nowSeconds : 0d;
        _profiler = CreateProfiler(config);
        _governor = new AdaptivePerformanceGovernor(config.Performance, _lastNowSeconds);
        _profilingEnabled = _governor.Settings.Mode != PerformanceMode.Disabled;
        _currentPolicy = EffectivePerformancePolicy.Create(config, _governor.CurrentTier);
        _lastTransitionReason = InitialReason(_governor.Settings);
        ResetWindowGate();
        ResetSummary(_lastNowSeconds);
    }

    /// <summary>
    /// The policy produced from the administrator's hard feature ceilings and the current
    /// adaptive tier. The value is cached so reading it from a hot path performs no work.
    /// </summary>
    public EffectivePerformancePolicy CurrentPolicy => _currentPolicy;

    public PerformanceTier Tier => _governor.CurrentTier;

    public bool IsFrozen => _frozen || _governor.IsFrozen;

    /// <summary>
    /// Starts a measurement. Passing zero or a negative sampling rate selects the production
    /// default: voice work is sampled 1/8, role-sync work 1/64, and batches exactly.
    /// A negative return value means this invocation was not sampled.
    /// </summary>
    public long Begin(PerformanceMetric metric, int sampleEvery = 0)
    {
        if (_frozen || !_profilingEnabled)
            return -1L;

        if (sampleEvery <= 0)
            sampleEvery = DefaultSampleEvery(metric);

        return _profiler.BeginSample(metric, sampleEvery);
    }

    /// <summary>
    /// Ends a measurement. PerformanceProfiler derives the sampling weight from exact call and
    /// sample counts; weight remains on this adapter API so call sites can state their cadence.
    /// </summary>
    public void End(PerformanceMetric metric, long started, int weight = 1)
    {
        if (_frozen || !_profilingEnabled || started < 0L)
            return;

        // Weight is intentionally not multiplied here. Multiplication would double-weight the
        // core profiler's call-count/sample-count estimate, especially at window boundaries.
        _ = weight;
        _profiler.EndSample(metric, started);
    }

    public void Count(PerformanceCounter counter)
    {
        if (!_frozen && _profilingEnabled)
            _profiler.Increment(counter);
    }

    public void Count(PerformanceCounter counter, long amount)
    {
        if (!_frozen && _profilingEnabled && amount != 0L)
            _profiler.Increment(counter, amount);
    }

    /// <summary>
    /// Records one raw, unsmoothed server frame and evaluates completed two-second windows.
    /// Idle and out-of-round frames remain diagnostic data but cannot drive governor changes.
    /// </summary>
    public void OnFrame(
        double now,
        double deltaMilliseconds,
        double smoothDeltaMilliseconds,
        int serverTickrate,
        bool idle,
        bool roundActive,
        int players,
        int recentSpeakers,
        int activeLooks)
    {
        if (_disposed)
            return;

        ObserveTime(now);
        if (_frozen || !_profilingEnabled)
            return;

        long controllerStarted = Stopwatch.GetTimestamp();
        long governorTicks = 0L;

        try
        {
            bool validFrame = IsFinite(deltaMilliseconds) && deltaMilliseconds >= 0d;
            if (!validFrame)
            {
                FreezeAfterFailure("invalid raw frame sample");
                return;
            }

            bool validSmooth =
                IsFinite(smoothDeltaMilliseconds) && smoothDeltaMilliseconds > 0d;
            if (validSmooth)
                _latestSmoothDeltaMilliseconds = smoothDeltaMilliseconds;
            if (serverTickrate > 0)
                _latestServerTickrate = serverTickrate;

            bool healthEligible = validFrame && !idle && roundActive && players > 0;

            _profiler.Increment(PerformanceCounter.TrackedPlayers, NonNegative(players));
            _profiler.Increment(
                PerformanceCounter.RecentSpeakers,
                NonNegative(recentSpeakers));
            _profiler.Increment(
                PerformanceCounter.ActiveLookTargets,
                NonNegative(activeLooks));

            if (validFrame)
            {
                _profiler.RecordFrameMilliseconds(deltaMilliseconds);
                if (!_windowHasFrames)
                {
                    _windowHasFrames = true;
                    _windowHealthEligible = healthEligible;
                }
                else
                {
                    // A window which overlaps idle/startup/round transition is never used as
                    // server-health evidence after the server becomes active again.
                    _windowHealthEligible &= healthEligible;
                }
            }

            if (healthEligible)
            {
                _summaryPopulationSamples++;
                _summaryPlayers += NonNegative(players);
                _summaryRecentSpeakers += NonNegative(recentSpeakers);
                _summaryActiveLooks += NonNegative(activeLooks);
                if (validSmooth && serverTickrate > 0)
                {
                    _summarySmoothSamples++;
                    _summarySmoothDeltaMilliseconds += smoothDeltaMilliseconds;
                    _summaryTargetTickrate += serverTickrate;
                }
            }

            if (!_profiler.TryCloseWindow(Stopwatch.GetTimestamp(), out PerformanceWindow window))
                return;

            bool profilingValid =
                window != null &&
                window.DurationSeconds > 0d &&
                window.FrameCount > 0L &&
                IsFinite(window.TalkyMillisecondsPerFrame) &&
                IsFinite(window.FrameP95Milliseconds);

            bool serverHealthEligible = profilingValid && _windowHasFrames && _windowHealthEligible;
            ResetWindowGate();

            if (!profilingValid)
            {
                FreezeAfterFailure("invalid completed profiler window");
                return;
            }

            GovernorSample sample = new GovernorSample(
                window.TalkyMillisecondsPerFrame,
                window.FrameP95Milliseconds,
                serverHealthEligible,
                true);

            long governorStarted = Stopwatch.GetTimestamp();
            GovernorDecision decision = _governor.Evaluate(sample, _lastNowSeconds);
            governorTicks = Stopwatch.GetTimestamp() - governorStarted;
            if (governorTicks > 0L)
                _profiler.RecordDurationTicks(PerformanceMetric.Governor, governorTicks);

            if (decision.Changed)
            {
                _currentPolicy = EffectivePerformancePolicy.Create(_config, decision.CurrentTier);
                _lastTransitionReason = decision.Reason;
                LogTransition(decision, window, players, recentSpeakers, activeLooks);
            }
            else if (decision.PressureAtFloor &&
                     _lastNowSeconds - _lastFloorWarningAt >= DefaultSummarySeconds)
            {
                _lastFloorWarningAt = _lastNowSeconds;
                Logger.Warn(
                    $"Performance pressure remains at the facial-animation floor " +
                    $"({PressureSource(window, serverHealthEligible)}): " +
                    $"talky={window.TalkyMillisecondsPerFrame:F3} ms/frame, " +
                    $"server_p95={window.FrameP95Milliseconds:F2} ms. " +
                    "Voice-driven facial animation remains enabled.");
            }

            Aggregate(window);
            TryLogSummary(_lastNowSeconds);
        }
        catch (Exception exception)
        {
            FreezeAfterFailure("internal error: " + exception.GetType().Name);
        }
        finally
        {
            if (!_disposed && _profiler != null)
            {
                long controllerTicks = Stopwatch.GetTimestamp() - controllerStarted - governorTicks;
                if (controllerTicks > 0L)
                    _profiler.RecordDurationTicks(PerformanceMetric.ProfilerOverhead, controllerTicks);
            }
        }
    }

    public void Reconfigure(Config config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));
        if (_disposed)
            return;

        _config = config;
        _frozen = false;
        _failureLogged = false;
        _governor.Reconfigure(config.Performance, _lastNowSeconds);
        _profilingEnabled = _governor.Settings.Mode != PerformanceMode.Disabled;
        _currentPolicy = EffectivePerformancePolicy.Create(config, _governor.CurrentTier);
        _lastTransitionReason = InitialReason(_governor.Settings);

        // Configuration reload is a cold path. Reset in place when dimensions are unchanged;
        // replace only when the configured window size changes.
        int windowSeconds = ValidatedWindowSeconds(config);
        if (_profiler.WindowSeconds == windowSeconds)
            _profiler.Reset();
        else
            _profiler = new PerformanceProfiler(windowSeconds, HistorySeconds);
        ResetWindowGate();
        ResetSummary(_lastNowSeconds);
    }

    /// <summary>
    /// Clears measurements at a round boundary while preserving the governor's protection tier.
    /// </summary>
    public void ResetForRound()
    {
        if (_disposed)
            return;

        _governor.ResetForRound(_lastNowSeconds);
        _currentPolicy = EffectivePerformancePolicy.Create(_config, _governor.CurrentTier);
        _profiler.Reset();
        ResetWindowGate();
        ResetSummary(_lastNowSeconds);
    }

    public void Freeze()
    {
        FreezeInternal("monitor frozen");
    }

    public void Freeze(string reason)
    {
        FreezeInternal(string.IsNullOrWhiteSpace(reason) ? "monitor frozen" : reason);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        FreezeInternal("monitor disposed");
    }

    private static PerformanceProfiler CreateProfiler(Config config)
    {
        return new PerformanceProfiler(ValidatedWindowSeconds(config), HistorySeconds);
    }

    private static int ValidatedWindowSeconds(Config config)
    {
        return PerformanceConfigValidator.Validate(config.Performance).Config.EvaluationWindowSeconds;
    }

    private static int DefaultSampleEvery(PerformanceMetric metric)
    {
        switch (metric)
        {
            case PerformanceMetric.VoicePacket:
            case PerformanceMetric.OpusDecode:
            case PerformanceMetric.EnergyUpdate:
                return VoiceSampleEvery;

            case PerformanceMetric.DirectRoleSync:
            case PerformanceMetric.CedModRoleSync:
            case PerformanceMetric.CedModPostfix:
                return SyncSampleEvery;

            default:
                return 1;
        }
    }

    private void Aggregate(PerformanceWindow window)
    {
        _summaryWindows++;
        _summaryDurationSeconds += window.DurationSeconds;
        _summaryFrames += window.FrameCount;
        _summaryTalkyMilliseconds += window.TotalTalkyMilliseconds;
        _summaryFrameHistogram.Add(window);
        if (window.FrameP95Milliseconds > _summaryWorstP95Milliseconds)
            _summaryWorstP95Milliseconds = window.FrameP95Milliseconds;

        _summaryVoiceMilliseconds +=
            window.GetMetric(PerformanceMetric.VoicePacket).EstimatedTotalMilliseconds;
        _summaryFaceMilliseconds +=
            window.GetMetric(PerformanceMetric.FaceBatch).EstimatedTotalMilliseconds;
        _summaryLookMilliseconds +=
            window.GetMetric(PerformanceMetric.LookBatch).EstimatedTotalMilliseconds;
        _summarySchedulerMilliseconds +=
            window.GetMetric(PerformanceMetric.SchedulerOverhead).EstimatedTotalMilliseconds;
        _summarySyncMilliseconds +=
            window.GetMetric(PerformanceMetric.DirectRoleSync).EstimatedTotalMilliseconds +
            window.GetMetric(PerformanceMetric.CedModRoleSync).EstimatedTotalMilliseconds +
            window.GetMetric(PerformanceMetric.CedModPostfix).EstimatedTotalMilliseconds;
        _summaryProfilerMilliseconds +=
            window.GetMetric(PerformanceMetric.ProfilerOverhead).EstimatedTotalMilliseconds +
            window.GetMetric(PerformanceMetric.Governor).EstimatedTotalMilliseconds;

        _summaryVoicePackets += window.GetCounter(PerformanceCounter.VoicePackets);
        _summaryDecodedSamples += window.GetCounter(PerformanceCounter.DecodedSamples);
        _summaryGlanceCandidates += window.GetCounter(PerformanceCounter.GlanceCandidates);
        _summarySyncEntries += window.GetCounter(PerformanceCounter.SyncEntries);
        _summaryActiveTargetHits += window.GetCounter(PerformanceCounter.ActiveTargetHits);
        _summaryRewrites += window.GetCounter(PerformanceCounter.Rewrites);
        _summaryEmotionWrites += window.GetCounter(PerformanceCounter.EmotionWrites);
    }

    private void TryLogSummary(double nowSeconds)
    {
        double interval = _governor.Settings.SummaryLogIntervalSeconds;
        if (interval == 0d)
            return;
        if (!IsFinite(interval) || interval < 0d)
            interval = DefaultSummarySeconds;

        if (nowSeconds - _summaryStartedAt < interval)
            return;

        if (_summaryWindows > 0L)
        {
            double talkyPerFrame = _summaryFrames > 0L
                ? _summaryTalkyMilliseconds / _summaryFrames
                : 0d;
            double talkyPerSecond = _summaryDurationSeconds > 0d
                ? _summaryTalkyMilliseconds / _summaryDurationSeconds
                : 0d;
            double averageFrame = _summaryFrameHistogram.AverageMilliseconds;
            double frameP50 = _summaryFrameHistogram.P50Milliseconds;
            double frameP95 = _summaryFrameHistogram.P95Milliseconds;
            double frameP99 = _summaryFrameHistogram.P99Milliseconds;
            double averagePlayers = _summaryPopulationSamples > 0L
                ? (double)_summaryPlayers / _summaryPopulationSamples
                : 0d;
            double averageSpeakers = _summaryPopulationSamples > 0L
                ? (double)_summaryRecentSpeakers / _summaryPopulationSamples
                : 0d;
            double averageLooks = _summaryPopulationSamples > 0L
                ? (double)_summaryActiveLooks / _summaryPopulationSamples
                : 0d;
            double averageSmoothDelta = _summarySmoothSamples > 0L
                ? _summarySmoothDeltaMilliseconds / _summarySmoothSamples
                : 0d;
            double smoothTps = averageSmoothDelta > 0d
                ? 1000d / averageSmoothDelta
                : 0d;
            double targetTps = _summarySmoothSamples > 0L
                ? _summaryTargetTickrate / _summarySmoothSamples
                : 0d;
            double voiceShare = Share(_summaryVoiceMilliseconds);
            double faceShare = Share(_summaryFaceMilliseconds);
            double lookShare = Share(_summaryLookMilliseconds);
            double schedulerShare = Share(_summarySchedulerMilliseconds);
            double syncShare = Share(_summarySyncMilliseconds);
            PerformanceTier configuredTier =
                _governor.Settings.Mode == PerformanceMode.Fixed
                    ? _governor.Settings.FixedTier
                    : PerformanceTier.Full;

            Logger.Info(
                $"Talky performance summary: mode={_governor.Settings.Mode}, " +
                $"configured_tier={configuredTier}, effective_tier={Tier}, " +
                $"last_trigger={_lastTransitionReason}, windows={_summaryWindows}, " +
                $"talky={talkyPerFrame:F3} ms/frame ({talkyPerSecond:F2} ms/s), " +
                $"frame_avg={averageFrame:F2} ms, " +
                $"frame_p50={frameP50:F2} ms, frame_p95={frameP95:F2} ms, " +
                $"frame_p99={frameP99:F2} ms, frame_max={_summaryFrameHistogram.MaxMilliseconds:F2} ms, " +
                $"worst_window_p95={_summaryWorstP95Milliseconds:F2} ms, " +
                $"smooth_tps={smoothTps:F1}/{targetTps:F1}, " +
                $"voice={PerFrame(_summaryVoiceMilliseconds):F3} ({voiceShare:F1}%), " +
                $"face={PerFrame(_summaryFaceMilliseconds):F3} ({faceShare:F1}%), " +
                $"look={PerFrame(_summaryLookMilliseconds):F3} ({lookShare:F1}%), " +
                $"scheduler={PerFrame(_summarySchedulerMilliseconds):F3} ({schedulerShare:F1}%), " +
                $"sync={PerFrame(_summarySyncMilliseconds):F3} ({syncShare:F1}%), " +
                $"profiler={PerFrame(_summaryProfilerMilliseconds):F3} ms/frame, " +
                $"players_avg={averagePlayers:F1}, speakers_avg={averageSpeakers:F1}, " +
                $"active_looks_avg={averageLooks:F1}, voice_packets={_summaryVoicePackets}, " +
                $"decoded_samples={_summaryDecodedSamples}, " +
                $"glance_candidates={_summaryGlanceCandidates}, sync_entries={_summarySyncEntries}, " +
                $"active_target_hits={_summaryActiveTargetHits}, rewrites={_summaryRewrites}, " +
                $"emotion_writes={_summaryEmotionWrites}.");
        }

        ResetSummary(nowSeconds);
    }

    private void LogTransition(
        GovernorDecision decision,
        PerformanceWindow window,
        int players,
        int recentSpeakers,
        int activeLooks)
    {
        double voice = window.GetMetric(PerformanceMetric.VoicePacket).EstimatedTotalMilliseconds /
                       window.FrameCount;
        double face = window.GetMetric(PerformanceMetric.FaceBatch).EstimatedTotalMilliseconds /
                      window.FrameCount;
        double look = window.GetMetric(PerformanceMetric.LookBatch).EstimatedTotalMilliseconds /
                      window.FrameCount;
        double scheduler =
            window.GetMetric(PerformanceMetric.SchedulerOverhead).EstimatedTotalMilliseconds /
            window.FrameCount;
        double sync =
            (window.GetMetric(PerformanceMetric.DirectRoleSync).EstimatedTotalMilliseconds +
             window.GetMetric(PerformanceMetric.CedModRoleSync).EstimatedTotalMilliseconds +
             window.GetMetric(PerformanceMetric.CedModPostfix).EstimatedTotalMilliseconds) /
            window.FrameCount;
        double profiler =
            (window.GetMetric(PerformanceMetric.ProfilerOverhead).EstimatedTotalMilliseconds +
             window.GetMetric(PerformanceMetric.Governor).EstimatedTotalMilliseconds) /
            window.FrameCount;
        double smoothTps = _latestSmoothDeltaMilliseconds > 0d
            ? 1000d / _latestSmoothDeltaMilliseconds
            : 0d;
        Logger.Warn(
            $"Talky performance tier {decision.PreviousTier} -> {decision.CurrentTier}: " +
            $"{decision.Reason}; talky={window.TalkyMillisecondsPerFrame:F3} ms/frame, " +
            $"{window.TalkyMillisecondsPerSecond:F2} ms/s, " +
            $"server_p50/p95/p99={window.FrameP50Milliseconds:F2}/" +
            $"{window.FrameP95Milliseconds:F2}/{window.FrameP99Milliseconds:F2} ms, " +
            $"server_max={window.FrameMaxMilliseconds:F2} ms, " +
            $"smooth_tps={smoothTps:F1}/{_latestServerTickrate}, " +
            $"voice/face/look/scheduler/sync={voice:F3}/{face:F3}/{look:F3}/" +
            $"{scheduler:F3}/{sync:F3} ms/frame, profiler={profiler:F3} ms/frame, " +
            $"players={NonNegative(players)}, " +
            $"recent_speakers={NonNegative(recentSpeakers)}, active_looks={NonNegative(activeLooks)}.");
    }

    private void FreezeInternal(string reason)
    {
        if (_frozen)
            return;

        _frozen = true;
        _profilingEnabled = false;
        _governor?.Freeze(reason);
        if (_config != null && _governor != null)
            _currentPolicy = EffectivePerformancePolicy.Create(_config, _governor.CurrentTier);
    }

    private void FreezeAfterFailure(string reason)
    {
        FreezeInternal(reason);
        if (_failureLogged)
            return;

        _failureLogged = true;
        Logger.Warn(
            $"Talky performance monitor froze after {reason}. " +
            "The current tier is retained; reload the configuration to retry profiling.");
    }

    private void ObserveTime(double nowSeconds)
    {
        if (!IsFinite(nowSeconds))
            return;

        if (!_hasTimeOrigin)
        {
            _hasTimeOrigin = true;
            _lastNowSeconds = nowSeconds;
            _governor.Reconfigure(_config.Performance, nowSeconds);
            _profilingEnabled = _governor.Settings.Mode != PerformanceMode.Disabled;
            _currentPolicy = EffectivePerformancePolicy.Create(_config, _governor.CurrentTier);
            _lastTransitionReason = InitialReason(_governor.Settings);
            ResetSummary(nowSeconds);
            return;
        }

        if (nowSeconds >= _lastNowSeconds)
            _lastNowSeconds = nowSeconds;
    }

    private static GovernorReason InitialReason(PerformanceConfig config)
    {
        switch (config.Mode)
        {
            case PerformanceMode.Disabled:
                return GovernorReason.ModeDisabled;
            case PerformanceMode.ObserveOnly:
                return GovernorReason.ObserveOnly;
            case PerformanceMode.Fixed:
                return GovernorReason.FixedTier;
            default:
                return GovernorReason.None;
        }
    }

    private void ResetWindowGate()
    {
        _windowHasFrames = false;
        _windowHealthEligible = false;
    }

    private void ResetSummary(double nowSeconds)
    {
        _summaryStartedAt = nowSeconds;
        _summaryDurationSeconds = 0d;
        _summaryTalkyMilliseconds = 0d;
        _summaryFrameHistogram.Reset();
        _summaryWorstP95Milliseconds = 0d;
        _summaryVoiceMilliseconds = 0d;
        _summaryFaceMilliseconds = 0d;
        _summaryLookMilliseconds = 0d;
        _summarySchedulerMilliseconds = 0d;
        _summarySyncMilliseconds = 0d;
        _summaryProfilerMilliseconds = 0d;
        _summarySmoothDeltaMilliseconds = 0d;
        _summaryTargetTickrate = 0d;
        _summaryFrames = 0L;
        _summaryWindows = 0L;
        _summarySmoothSamples = 0L;
        _summaryVoicePackets = 0L;
        _summaryDecodedSamples = 0L;
        _summaryGlanceCandidates = 0L;
        _summarySyncEntries = 0L;
        _summaryActiveTargetHits = 0L;
        _summaryRewrites = 0L;
        _summaryEmotionWrites = 0L;
        _summaryPopulationSamples = 0L;
        _summaryPlayers = 0L;
        _summaryRecentSpeakers = 0L;
        _summaryActiveLooks = 0L;
    }

    private static long NonNegative(int value)
    {
        return value > 0 ? value : 0L;
    }

    private double PerFrame(double totalMilliseconds)
    {
        return _summaryFrames > 0L ? totalMilliseconds / _summaryFrames : 0d;
    }

    private double Share(double subsystemMilliseconds)
    {
        return _summaryTalkyMilliseconds > 0d
            ? 100d * subsystemMilliseconds / _summaryTalkyMilliseconds
            : 0d;
    }

    private string PressureSource(PerformanceWindow window, bool serverHealthEligible)
    {
        bool hardTalky =
            window.TalkyMillisecondsPerFrame > _governor.Settings.HardTalkyBudgetMsPerFrame;
        bool softTalky =
            window.TalkyMillisecondsPerFrame > _governor.Settings.TalkyBudgetMsPerFrame;
        bool serverEmergency =
            serverHealthEligible &&
            window.FrameP95Milliseconds > _governor.Settings.ServerP95EmergencyMs;
        bool serverSoft =
            serverHealthEligible &&
            window.FrameP95Milliseconds > _governor.Settings.ServerP95DegradeMs;

        if ((hardTalky || softTalky) && (serverEmergency || serverSoft))
            return "Talky and server";
        if (hardTalky || softTalky)
            return "Talky";
        if (serverEmergency || serverSoft)
            return "server only; non-attributive";
        return "unattributed";
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

}
