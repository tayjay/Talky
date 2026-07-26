using System;

namespace Talky.Performance;

public enum GovernorReason
{
    None = 0,
    Warmup = 1,
    ModeDisabled = 2,
    ObserveOnly = 3,
    FixedTier = 4,
    SoftTalkyBudget = 5,
    SoftServerBudget = 6,
    HardTalkyBudget = 7,
    ServerEmergency = 8,
    Recovery = 9,
    Cooldown = 10,
    Healthy = 11,
    PressureAtFloor = 12,
    Frozen = 13,
    InvalidProfile = 14,
}

public readonly struct GovernorSample
{
    public GovernorSample(
        double talkyMsPerFrame,
        double serverP95FrameMs,
        bool serverHealthEligible,
        bool profilingValid = true)
    {
        TalkyMsPerFrame = talkyMsPerFrame;
        ServerP95FrameMs = serverP95FrameMs;
        ServerHealthEligible = serverHealthEligible;
        ProfilingValid = profilingValid;
    }

    public double TalkyMsPerFrame { get; }
    public double ServerP95FrameMs { get; }
    public bool ServerHealthEligible { get; }
    public bool ProfilingValid { get; }
}

public readonly struct GovernorDecision
{
    internal GovernorDecision(
        PerformanceTier previousTier,
        PerformanceTier currentTier,
        GovernorReason reason,
        bool pressureAtFloor)
    {
        PreviousTier = previousTier;
        CurrentTier = currentTier;
        Reason = reason;
        PressureAtFloor = pressureAtFloor;
    }

    public PerformanceTier PreviousTier { get; }
    public PerformanceTier CurrentTier { get; }
    public bool Changed => PreviousTier != CurrentTier;
    public GovernorReason Reason { get; }
    public bool PressureAtFloor { get; }
}

/// <summary>
/// Pure adaptive-quality state machine. Time is supplied by the caller, making transitions
/// deterministic and independent of Unity or wall-clock APIs.
/// </summary>
public sealed class AdaptivePerformanceGovernor
{
    private PerformanceConfig _config;
    private double _warmupStartedAt;
    private double _lastTransitionAt;
    private int _softBreachWindows;
    private int _serverEmergencyWindows;
    private int _healthySeconds;
    private bool _lastTransitionWasRecovery;

    public AdaptivePerformanceGovernor(PerformanceConfig config, double nowSeconds)
    {
        _config = PerformanceConfigValidator.Validate(config).Config;
        CurrentTier = InitialTier(_config);
        _warmupStartedAt = SanitizeTime(nowSeconds);
        _lastTransitionAt = double.NegativeInfinity;
    }

    public PerformanceTier CurrentTier { get; private set; }
    public PerformanceConfig Settings => _config;
    public bool IsFrozen { get; private set; }
    public string FrozenReason { get; private set; } = string.Empty;

    public GovernorDecision Evaluate(GovernorSample sample, double nowSeconds)
    {
        PerformanceTier previous = CurrentTier;
        nowSeconds = SanitizeTime(nowSeconds);

        if (IsFrozen)
            return Decision(previous, GovernorReason.Frozen);

        if (!IsValid(sample))
        {
            Freeze("Invalid profiler sample.");
            return Decision(previous, GovernorReason.InvalidProfile);
        }

        switch (_config.Mode)
        {
            case PerformanceMode.Disabled:
                ResetEvidence();
                return Decision(previous, GovernorReason.ModeDisabled);
            case PerformanceMode.ObserveOnly:
                ResetEvidence();
                return Decision(previous, GovernorReason.ObserveOnly);
            case PerformanceMode.Fixed:
                ResetEvidence();
                if (CurrentTier != _config.FixedTier)
                    CurrentTier = _config.FixedTier;
                return Decision(previous, GovernorReason.FixedTier);
        }

        if (nowSeconds - _warmupStartedAt < _config.WarmupSeconds)
        {
            ResetEvidence();
            return Decision(previous, GovernorReason.Warmup);
        }

        bool hardTalky = sample.TalkyMsPerFrame > _config.HardTalkyBudgetMsPerFrame;
        bool serverEmergency = sample.ServerHealthEligible &&
                               sample.ServerP95FrameMs > _config.ServerP95EmergencyMs;

        if (serverEmergency)
            _serverEmergencyWindows++;
        else
            _serverEmergencyWindows = 0;

        if (hardTalky)
        {
            PerformanceTier target = EmergencyTarget(CurrentTier);
            if (target == CurrentTier)
            {
                ResetEvidence();
                return new GovernorDecision(previous, CurrentTier, GovernorReason.PressureAtFloor, true);
            }

            TransitionTo(target, nowSeconds);
            return Decision(previous, GovernorReason.HardTalkyBudget);
        }

        if (_serverEmergencyWindows >= _config.EmergencyBreachWindows)
        {
            PerformanceTier target = EmergencyTarget(CurrentTier);
            if (target == CurrentTier)
            {
                ResetEvidence();
                return new GovernorDecision(previous, CurrentTier, GovernorReason.PressureAtFloor, true);
            }

            TransitionTo(target, nowSeconds);
            return Decision(previous, GovernorReason.ServerEmergency);
        }

        bool talkyBreach = sample.TalkyMsPerFrame > _config.TalkyBudgetMsPerFrame;
        bool serverBreach = sample.ServerHealthEligible &&
                            sample.ServerP95FrameMs > _config.ServerP95DegradeMs;
        if (talkyBreach || serverBreach)
        {
            _healthySeconds = 0;
            _softBreachWindows++;

            if (_softBreachWindows < _config.ConsecutiveBreachWindows)
                return Decision(previous, talkyBreach ? GovernorReason.SoftTalkyBudget : GovernorReason.SoftServerBudget);

            if (nowSeconds - _lastTransitionAt < _config.TransitionCooldownSeconds)
                return Decision(previous, GovernorReason.Cooldown);

            if (CurrentTier >= PerformanceTier.FaceOnlyReducedRate)
            {
                ResetEvidence();
                return new GovernorDecision(previous, CurrentTier, GovernorReason.PressureAtFloor, true);
            }

            TransitionTo((PerformanceTier)((int)CurrentTier + 1), nowSeconds, false);
            return Decision(previous, talkyBreach ? GovernorReason.SoftTalkyBudget : GovernorReason.SoftServerBudget);
        }

        _softBreachWindows = 0;
        _serverEmergencyWindows = 0;

        bool talkyHealthy = sample.TalkyMsPerFrame <
                            _config.TalkyBudgetMsPerFrame * _config.RecoveryBudgetRatio;
        bool serverHealthy = sample.ServerHealthEligible &&
                             sample.ServerP95FrameMs < _config.ServerP95RecoveryMs;
        if (!talkyHealthy || !serverHealthy || CurrentTier == PerformanceTier.Full)
        {
            _healthySeconds = 0;
            return Decision(previous, GovernorReason.None);
        }

        _healthySeconds += _config.EvaluationWindowSeconds;
        int requiredHealthySeconds = _lastTransitionWasRecovery
            ? _config.RecoveryCooldownSeconds
            : _config.RecoveryHealthySeconds;
        if (_healthySeconds < requiredHealthySeconds)
            return Decision(previous, GovernorReason.Healthy);

        if (nowSeconds - _lastTransitionAt < _config.RecoveryCooldownSeconds)
            return Decision(previous, GovernorReason.Cooldown);

        TransitionTo((PerformanceTier)((int)CurrentTier - 1), nowSeconds, true);
        return Decision(previous, GovernorReason.Recovery);
    }

    public void Reconfigure(PerformanceConfig config, double nowSeconds)
    {
        _config = PerformanceConfigValidator.Validate(config).Config;
        IsFrozen = false;
        FrozenReason = string.Empty;

        if (_config.Mode == PerformanceMode.Fixed)
            CurrentTier = _config.FixedTier;
        else if (_config.Mode != PerformanceMode.Automatic)
            CurrentTier = PerformanceTier.Full;

        _warmupStartedAt = SanitizeTime(nowSeconds);
        _lastTransitionAt = double.NegativeInfinity;
        _lastTransitionWasRecovery = false;
        ResetEvidence();
    }

    /// <summary>
    /// Starts a fresh warmup and discards window evidence while deliberately preserving the tier.
    /// </summary>
    public void ResetForRound(double nowSeconds)
    {
        _warmupStartedAt = SanitizeTime(nowSeconds);
        _lastTransitionWasRecovery = false;
        ResetEvidence();
    }

    public void Freeze(string reason)
    {
        IsFrozen = true;
        FrozenReason = string.IsNullOrWhiteSpace(reason) ? "Performance profiling failed." : reason;
        ResetEvidence();
    }

    private static bool IsValid(GovernorSample sample)
    {
        if (!sample.ProfilingValid ||
            !IsFinite(sample.TalkyMsPerFrame) ||
            sample.TalkyMsPerFrame < 0d)
        {
            return false;
        }

        return !sample.ServerHealthEligible ||
               (IsFinite(sample.ServerP95FrameMs) && sample.ServerP95FrameMs >= 0d);
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static double SanitizeTime(double value)
    {
        return IsFinite(value) ? value : 0d;
    }

    private static PerformanceTier InitialTier(PerformanceConfig config)
    {
        return config.Mode == PerformanceMode.Fixed ? config.FixedTier : PerformanceTier.Full;
    }

    private static PerformanceTier EmergencyTarget(PerformanceTier current)
    {
        if (current < PerformanceTier.FaceOnly)
            return PerformanceTier.FaceOnly;
        if (current < PerformanceTier.FaceOnlyReducedRate)
            return PerformanceTier.FaceOnlyReducedRate;
        return current;
    }

    private GovernorDecision Decision(PerformanceTier previous, GovernorReason reason)
    {
        return new GovernorDecision(previous, CurrentTier, reason, false);
    }

    private void TransitionTo(PerformanceTier tier, double nowSeconds, bool recovery = false)
    {
        if (tier < PerformanceTier.Full)
            tier = PerformanceTier.Full;
        if (tier > PerformanceTier.FaceOnlyReducedRate)
            tier = PerformanceTier.FaceOnlyReducedRate;

        CurrentTier = tier;
        _lastTransitionAt = nowSeconds;
        _lastTransitionWasRecovery = recovery;
        ResetEvidence();
    }

    private void ResetEvidence()
    {
        _softBreachWindows = 0;
        _serverEmergencyWindows = 0;
        _healthySeconds = 0;
    }
}
