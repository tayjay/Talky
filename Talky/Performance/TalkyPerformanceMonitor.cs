using System;
using System.Diagnostics;
using LabApi.Features.Console;
using Talky.Settings;

namespace Talky.Performance;

/// <summary>
/// Production adapter around the allocation-conscious core profiler and adaptive governor.
/// All hot-path methods avoid formatting, LINQ, closures, and per-call object creation.
///
/// Callback/frame samples -> profiler window -> governor decision -> cached runtime policy.
/// Completed windows also feed one reusable periodic-summary accumulator.
/// </summary>
public sealed class TalkyPerformanceMonitor : IDisposable
{
    public const int VoiceSampleEvery = PerformanceMetricCatalog.VoiceSampleEvery;
    public const int SyncSampleEvery = PerformanceMetricCatalog.SyncSampleEvery;

    private const int HistorySeconds = 60;
    private const double SummaryFallbackSeconds = 300d;
    private const double FloorWarningIntervalSeconds = 300d;

    private Config _config;
    private PerformanceProfiler _profiler;
    private AdaptivePerformanceGovernor _governor;
    private EffectivePerformancePolicy _currentPolicy;
    private readonly PerformanceSummaryAccumulator _summary =
        new PerformanceSummaryAccumulator();
    private readonly PerformanceTransitionTracker _transitions =
        new PerformanceTransitionTracker();

    private double _lastNowSeconds;
    private bool _hasTimeOrigin;
    private bool _windowHasFrames;
    private bool _windowHealthEligible;
    private bool _profilingEnabled;
    private bool _frozen;
    private bool _disposed;
    private bool _failureLogged;

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
        RefreshCurrentPolicy();
        _lastTransitionReason = InitialReason(_governor.Settings);
        ResetWindowGate();
        _summary.Reset(_lastNowSeconds);
        _transitions.Reset();
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
            sampleEvery = PerformanceMetricCatalog.DefaultSampleEvery(metric);

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
        Count(counter, 1L);
    }

    public void Count(PerformanceCounter counter, long amount)
    {
        if (_frozen || !_profilingEnabled)
            return;

        switch (PerformanceCounterCatalog.Aggregation(counter))
        {
            case PerformanceCounterAggregation.Last:
                _profiler.SetGauge(counter, amount > 0L ? amount : 0L);
                break;
            case PerformanceCounterAggregation.Maximum:
                if (amount > 0L)
                    _profiler.ObserveHighWater(counter, amount);
                break;
            default:
                if (amount != 0L)
                    _profiler.Increment(counter, amount);
                break;
        }
    }

    public void SetGauge(PerformanceCounter counter, long value)
    {
        if (!_frozen && _profilingEnabled)
            _profiler.SetGauge(counter, value > 0L ? value : 0L);
    }

    public void ObserveHighWater(PerformanceCounter counter, long value)
    {
        if (!_frozen && _profilingEnabled && value > 0L)
            _profiler.ObserveHighWater(counter, value);
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
        ObserveServerRates(smoothDeltaMilliseconds, serverTickrate);
        if (_frozen || !_profilingEnabled)
            return;

        long controllerStarted = Stopwatch.GetTimestamp();
        long governorTicks = 0L;

        try
        {
            if (!RecordFrame(
                    deltaMilliseconds,
                    smoothDeltaMilliseconds,
                    serverTickrate,
                    idle,
                    roundActive,
                    players,
                    recentSpeakers,
                    activeLooks))
            {
                return;
            }

            if (!_profiler.TryCloseWindow(Stopwatch.GetTimestamp(), out PerformanceWindow window))
                return;

            EvaluateWindow(
                window,
                players,
                recentSpeakers,
                activeLooks,
                ref governorTicks);
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

    private bool RecordFrame(
        double deltaMilliseconds,
        double smoothDeltaMilliseconds,
        int serverTickrate,
        bool idle,
        bool roundActive,
        int players,
        int recentSpeakers,
        int activeLooks)
    {
        if (!IsFinite(deltaMilliseconds) || deltaMilliseconds < 0d)
        {
            FreezeAfterFailure("invalid raw frame sample");
            return false;
        }

        bool validSmooth =
            IsFinite(smoothDeltaMilliseconds) && smoothDeltaMilliseconds > 0d;

        bool healthEligible = !idle && roundActive && players > 0;

        long trackedPlayers = NonNegative(players);
        long trackedSpeakers = NonNegative(recentSpeakers);
        long trackedLooks = NonNegative(activeLooks);
        _profiler.Increment(PerformanceCounter.TrackedPlayers, trackedPlayers);
        _profiler.Increment(
            PerformanceCounter.RecentSpeakers,
            trackedSpeakers);
        _profiler.Increment(
            PerformanceCounter.ActiveLookTargets,
            trackedLooks);
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

        if (healthEligible)
        {
            _summary.RecordEligibleFrame(
                players,
                recentSpeakers,
                activeLooks,
                smoothDeltaMilliseconds,
                serverTickrate,
                validSmooth && serverTickrate > 0);
        }

        return true;
    }

    private void EvaluateWindow(
        PerformanceWindow window,
        int players,
        int recentSpeakers,
        int activeLooks,
        ref long governorTicks)
    {
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

        PerformanceBreakdown breakdown = PerformanceBreakdown.FromWindow(window);
        PerformanceTier observedTier = Tier;
        _transitions.RecordWindow(
            window,
            breakdown,
            observedTier,
            serverHealthEligible);

        GovernorSample sample = new GovernorSample(
            window.TalkyMillisecondsPerFrame,
            window.FrameP95Milliseconds,
            serverHealthEligible,
            true,
            window.DurationSeconds);

        long governorStarted = Stopwatch.GetTimestamp();
        GovernorDecision decision = _governor.Evaluate(sample, _lastNowSeconds);
        governorTicks = Stopwatch.GetTimestamp() - governorStarted;
        if (governorTicks > 0L)
            _profiler.RecordDurationTicks(PerformanceMetric.Governor, governorTicks);

        FinishTransitionImpact(decision);
        if (decision.Changed)
        {
            RefreshCurrentPolicy();
            _lastTransitionReason = decision.Reason;
            LogTransition(
                decision,
                window,
                breakdown,
                players,
                recentSpeakers,
                activeLooks);
            _transitions.Start(
                decision.PreviousTier,
                decision.CurrentTier,
                decision.Reason,
                _lastNowSeconds,
                _governor.Settings.RecoveryProbeSeconds);
        }
        else if (decision.Reason == GovernorReason.RecoveryProbeSucceeded)
        {
            _lastTransitionReason = decision.Reason;
        }
        else if (decision.PressureAtFloor &&
                 _lastNowSeconds - _lastFloorWarningAt >= FloorWarningIntervalSeconds)
        {
            _lastFloorWarningAt = _lastNowSeconds;
            Logger.Warn(
                $"Performance pressure remains at the facial-animation floor " +
                $"({PressureSource(window, serverHealthEligible)}): " +
                $"talky={window.TalkyMillisecondsPerFrame:F3} ms/frame, " +
                $"server_p95={window.FrameP95Milliseconds:F2} ms. " +
                "Voice-driven facial animation remains enabled.");
        }

        _summary.ObserveTier(observedTier);
        _summary.AddWindow(window, breakdown);
        TryLogSummary(_lastNowSeconds);
    }

    public void Reconfigure(Config config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));
        if (_disposed)
            return;

        EffectivePerformancePolicy previousPolicy = _currentPolicy;
        PerformanceMode previousMode = _governor.Settings.Mode;
        PerformanceTier previousTier = Tier;
        PerformanceTier previousCommittedTier = _governor.CommittedTier;
        bool previousProbeActive = _governor.IsRecoveryProbeActive;

        _config = config;
        _frozen = false;
        _failureLogged = false;
        _governor.Reconfigure(config.Performance, _lastNowSeconds);
        _profilingEnabled = _governor.Settings.Mode != PerformanceMode.Disabled;
        RefreshCurrentPolicy();
        _lastTransitionReason = InitialReason(_governor.Settings);
        LogConfigurationReload(
            previousPolicy,
            previousMode,
            previousTier,
            previousCommittedTier,
            previousProbeActive);

        // Configuration reload is a cold path. Reset in place when dimensions are unchanged;
        // replace only when the configured window size changes.
        int windowSeconds = ValidatedWindowSeconds(config);
        if (_profiler.WindowSeconds == windowSeconds)
            _profiler.Reset();
        else
            _profiler = new PerformanceProfiler(windowSeconds, HistorySeconds);
        ResetWindowGate();
        _summary.Reset(_lastNowSeconds);
        _transitions.Reset();
        ResetServerRateSnapshots();
    }

    /// <summary>
    /// Clears measurements at a round boundary while preserving the governor's protection tier.
    /// </summary>
    public void ResetForRound()
    {
        if (_disposed)
            return;

        _governor.ResetForRound(_lastNowSeconds);
        RefreshCurrentPolicy();
        _profiler.Reset();
        ResetWindowGate();
        _summary.Reset(_lastNowSeconds);
        _transitions.Reset();
        _lastTransitionReason = InitialReason(_governor.Settings);
        ResetServerRateSnapshots();
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

    private void LogConfigurationReload(
        EffectivePerformancePolicy previousPolicy,
        PerformanceMode previousMode,
        PerformanceTier previousTier,
        PerformanceTier previousCommittedTier,
        bool previousProbeActive)
    {
        string changes = previousPolicy == null
            ? "unavailable"
            : PerformanceTransitionReportFormatter.DescribePolicyChanges(
                previousPolicy,
                _currentPolicy);
        PerformanceConfig settings = _governor.Settings;

        Logger.Info(
            $"Talky performance configuration reloaded: " +
            $"mode={previousMode}->{settings.Mode}, " +
            $"effective_tier={previousTier}->{Tier}, " +
            $"committed_tier={previousCommittedTier}->{_governor.CommittedTier}, " +
            $"recovery_probe={(previousProbeActive ? "cancelled" : "none")}, " +
            $"changes=[{changes}], window={settings.EvaluationWindowSeconds}s, " +
            $"probe={settings.RecoveryProbeSeconds}s, " +
            $"talky_budget={settings.TalkyBudgetMsPerFrame:F3}/" +
            $"{settings.HardTalkyBudgetMsPerFrame:F3} ms/frame, " +
            $"server_p95_recover/degrade/emergency=" +
            $"{settings.ServerP95RecoveryMs:F2}/{settings.ServerP95DegradeMs:F2}/" +
            $"{settings.ServerP95EmergencyMs:F2} ms.");
    }

    private static PerformanceProfiler CreateProfiler(Config config)
    {
        return new PerformanceProfiler(ValidatedWindowSeconds(config), HistorySeconds);
    }

    private static int ValidatedWindowSeconds(Config config)
    {
        return PerformanceConfigValidator.Validate(config.Performance).Config.EvaluationWindowSeconds;
    }

    private void TryLogSummary(double nowSeconds)
    {
        double interval = _governor.Settings.SummaryLogIntervalSeconds;
        if (interval == 0d)
            return;
        if (!IsFinite(interval) || interval < 0d)
            interval = SummaryFallbackSeconds;

        if (nowSeconds - _summary.StartedAt < interval)
            return;

        if (_summary.HasWindows)
        {
            PerformanceTier configuredTier =
                _governor.Settings.Mode == PerformanceMode.Fixed
                    ? _governor.Settings.FixedTier
                    : PerformanceTier.Full;

            Logger.Info(_summary.CreateLogMessage(
                _governor.Settings.Mode,
                configuredTier,
                Tier,
                _governor.CommittedTier,
                _governor.IsRecoveryProbeActive,
                _lastTransitionReason));
        }

        _summary.Reset(nowSeconds);
    }

    private void FinishTransitionImpact(GovernorDecision decision)
    {
        if (!_transitions.IsActive)
            return;

        PerformanceTransitionImpact impact;
        if (_transitions.IsRecoveryObservation)
        {
            if (decision.Reason == GovernorReason.RecoveryProbeSucceeded)
            {
                if (_transitions.TryFinish(
                        _lastNowSeconds,
                        PerformanceImpactOutcome.RecoveryAccepted,
                        decision.Reason,
                        out impact))
                {
                    LogTransitionImpact(impact);
                }
            }
            else if (decision.Changed &&
                     _transitions.TryFinish(
                         _lastNowSeconds,
                         PerformanceImpactOutcome.RecoveryRejected,
                         decision.Reason,
                         out impact))
            {
                LogTransitionImpact(impact);
            }

            return;
        }

        if (_transitions.TryComplete(_lastNowSeconds, out impact))
        {
            LogTransitionImpact(impact);
            return;
        }

        if (decision.Changed &&
            _transitions.TryFinish(
                _lastNowSeconds,
                PerformanceImpactOutcome.Interrupted,
                decision.Reason,
                out impact))
        {
            LogTransitionImpact(impact);
        }
    }

    private void LogTransitionImpact(PerformanceTransitionImpact impact)
    {
        string message =
            PerformanceTransitionReportFormatter.CreateLogMessage(_config, impact);

        if (!impact.Before.HasData ||
            !impact.After.HasData ||
            !impact.HasCompleteBaseline ||
            !impact.HasCompleteAfter ||
            impact.Outcome == PerformanceImpactOutcome.RecoveryRejected ||
            impact.Outcome == PerformanceImpactOutcome.Interrupted)
        {
            Logger.Warn(message);
        }
        else
        {
            Logger.Info(message);
        }
    }

    private void LogTransition(
        GovernorDecision decision,
        PerformanceWindow window,
        PerformanceBreakdown breakdown,
        int players,
        int recentSpeakers,
        int activeLooks)
    {
        double voice = breakdown.VoiceMilliseconds / window.FrameCount;
        double face = breakdown.FaceMilliseconds / window.FrameCount;
        double look = breakdown.LookMilliseconds / window.FrameCount;
        double scheduler = breakdown.SchedulerMilliseconds / window.FrameCount;
        double sync = breakdown.SyncMilliseconds / window.FrameCount;
        double frameMonitorLowerBound = breakdown.MonitoringMilliseconds / window.FrameCount;
        double smoothTps = _latestSmoothDeltaMilliseconds > 0d
            ? 1000d / _latestSmoothDeltaMilliseconds
            : 0d;
        Logger.Warn(
            $"Talky performance tier {decision.PreviousTier} -> {decision.CurrentTier}: " +
            $"{decision.Reason}; state=" +
            $"{(_governor.IsRecoveryProbeActive ? "probe" : "committed")}, " +
            $"committed_tier={_governor.CommittedTier}, " +
            $"talky={window.TalkyMillisecondsPerFrame:F3} ms/frame, " +
            $"{window.TalkyMillisecondsPerSecond:F2} ms/s, " +
            $"server_p50/p95/p99={window.FrameP50Milliseconds:F2}/" +
            $"{window.FrameP95Milliseconds:F2}/{window.FrameP99Milliseconds:F2} ms, " +
            $"server_max={window.FrameMaxMilliseconds:F2} ms, " +
            $"smooth_tps={smoothTps:F1}/{_latestServerTickrate}, " +
            $"voice/face/look/scheduler/sync={voice:F3}/{face:F3}/{look:F3}/" +
            $"{scheduler:F3}/{sync:F3} ms/frame, " +
            $"frame_monitor_lower_bound={frameMonitorLowerBound:F3} ms/frame, " +
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
        _transitions.Reset();
        if (_config != null && _governor != null)
            RefreshCurrentPolicy();
    }

    private void FreezeAfterFailure(string reason)
    {
        FreezeInternal(reason);
        if (_failureLogged)
            return;

        _failureLogged = true;
        Logger.Warn(
            $"Talky performance monitor froze after {reason}. " +
            "The committed tier is retained; reload the configuration to retry profiling.");
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
            RefreshCurrentPolicy();
            _lastTransitionReason = InitialReason(_governor.Settings);
            _summary.Reset(nowSeconds);
            _transitions.Reset();
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

    private void RefreshCurrentPolicy()
    {
        _currentPolicy = EffectivePerformancePolicy.Create(_config, _governor.CurrentTier);
    }

    private void ResetWindowGate()
    {
        _windowHasFrames = false;
        _windowHealthEligible = false;
    }

    private void ResetServerRateSnapshots()
    {
        _latestSmoothDeltaMilliseconds = 0d;
        _latestServerTickrate = 0;
        _lastFloorWarningAt = double.NegativeInfinity;
    }

    private static long NonNegative(int value)
    {
        return value > 0 ? value : 0L;
    }

    private void ObserveServerRates(double smoothDeltaMilliseconds, int serverTickrate)
    {
        if (IsFinite(smoothDeltaMilliseconds) && smoothDeltaMilliseconds > 0d)
            _latestSmoothDeltaMilliseconds = smoothDeltaMilliseconds;
        if (serverTickrate > 0)
            _latestServerTickrate = serverTickrate;
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
