using System;
using System.ComponentModel;
using System.Text;
using Talky.Settings;

namespace Talky.Performance;

public enum PerformanceMode
{
    Automatic = 0,
    ObserveOnly = 1,
    Fixed = 2,
    Disabled = 3,
}

public enum PerformanceTier
{
    Full = 0,
    Economy = 1,
    NoGlancing = 2,
    FaceOnly = 3,
    FaceOnlyReducedRate = 4,
}

public sealed class PerformanceConfig
{
    [Description("Automatic adapts quality, 'ObserveOnly' profiles without adapting, 'Fixed' pins FixedTier, and 'Disabled' turns profiling and adaptation off.")]
    public PerformanceMode Mode { get; set; } = PerformanceMode.Automatic;

    [Description("Quality tier used when Mode is Fixed. (Full, Economy, NoGlancing, FaceOnly, FaceOnlyReducedRate)")]
    public PerformanceTier FixedTier { get; set; } = PerformanceTier.Full;

    [Description("Sustained Talky main-thread budget in milliseconds per server frame.")]
    public double TalkyBudgetMsPerFrame { get; set; } = 1.0d;

    [Description("One-window Talky emergency budget in milliseconds per server frame.")]
    public double HardTalkyBudgetMsPerFrame { get; set; } = 3.0d;

    [Description("Server p95 frame time which contributes to normal degradation.")]
    public double ServerP95DegradeMs { get; set; } = 25.0d;

    [Description("Server p95 frame time which triggers emergency degradation.")]
    public double ServerP95EmergencyMs { get; set; } = 33.3d;

    [Description("Server p95 frame time required for automatic recovery.")]
    public double ServerP95RecoveryMs { get; set; } = 20.0d;

    [Description("Length of each profiler and governor evaluation window.")]
    public int EvaluationWindowSeconds { get; set; } = 2;

    [Description("Consecutive soft-budget windows required before degrading one tier.")]
    public int ConsecutiveBreachWindows { get; set; } = 3;

    [Description("Consecutive server-emergency windows required before jumping to FaceOnly.")]
    public int EmergencyBreachWindows { get; set; } = 2;

    [Description("Seconds after startup or a round reset before automatic decisions begin.")]
    public int WarmupSeconds { get; set; } = 15;

    [Description("Minimum seconds between ordinary degradation transitions.")]
    public int TransitionCooldownSeconds { get; set; } = 20;

    [Description("Continuous healthy seconds required before restoring one tier.")]
    public int RecoveryHealthySeconds { get; set; } = 90;

    [Description("Talky must remain below this fraction of its soft budget to recover.")]
    public double RecoveryBudgetRatio { get; set; } = 0.50d;

    [Description("Minimum healthy probation in seconds after any recovery transition.")]
    public int RecoveryCooldownSeconds { get; set; } = 60;

    [Description("Eligible seconds to observe a restored tier before keeping it (10-60). An over-budget probe is rolled back.")]
    public int RecoveryProbeSeconds { get; set; } = 10;

    [Description("Maximum face evaluation rate in Economy and lower tiers.")]
    public int EconomyFaceHz { get; set; } = 20;

    [Description("Maximum glance evaluation rate in Economy.")]
    public int EconomyGlanceHz { get; set; } = 10;

    [Description("Maximum look update rate in Economy and NoGlancing.")]
    public int EconomyLookHz { get; set; } = 30;

    [Description("Face evaluation rate retained at the automatic quality floor.")]
    public int MinimumFaceHz { get; set; } = 15;

    [Description("Seconds between aggregate performance summaries. Zero disables periodic summaries.")]
    public int SummaryLogIntervalSeconds { get; set; } = 0;

    public PerformanceConfig Clone()
    {
        return new PerformanceConfig
        {
            Mode = Mode,
            FixedTier = FixedTier,
            TalkyBudgetMsPerFrame = TalkyBudgetMsPerFrame,
            HardTalkyBudgetMsPerFrame = HardTalkyBudgetMsPerFrame,
            ServerP95DegradeMs = ServerP95DegradeMs,
            ServerP95EmergencyMs = ServerP95EmergencyMs,
            ServerP95RecoveryMs = ServerP95RecoveryMs,
            EvaluationWindowSeconds = EvaluationWindowSeconds,
            ConsecutiveBreachWindows = ConsecutiveBreachWindows,
            EmergencyBreachWindows = EmergencyBreachWindows,
            WarmupSeconds = WarmupSeconds,
            TransitionCooldownSeconds = TransitionCooldownSeconds,
            RecoveryHealthySeconds = RecoveryHealthySeconds,
            RecoveryBudgetRatio = RecoveryBudgetRatio,
            RecoveryCooldownSeconds = RecoveryCooldownSeconds,
            RecoveryProbeSeconds = RecoveryProbeSeconds,
            EconomyFaceHz = EconomyFaceHz,
            EconomyGlanceHz = EconomyGlanceHz,
            EconomyLookHz = EconomyLookHz,
            MinimumFaceHz = MinimumFaceHz,
            SummaryLogIntervalSeconds = SummaryLogIntervalSeconds,
        };
    }
}

public sealed class PerformanceConfigValidationResult
{
    internal PerformanceConfigValidationResult(PerformanceConfig config, int correctionCount, string correctionSummary)
    {
        Config = config;
        CorrectionCount = correctionCount;
        CorrectionSummary = correctionSummary ?? string.Empty;
    }

    public PerformanceConfig Config { get; }
    public int CorrectionCount { get; }
    public bool WasCorrected => CorrectionCount != 0;
    public string CorrectionSummary { get; }
}

/// <summary>
/// Produces a safe snapshot at configuration boundaries. It never mutates the supplied
/// <see cref="PerformanceConfig"/>.
/// </summary>
public static class PerformanceConfigValidator
{
    public static PerformanceConfigValidationResult Validate(PerformanceConfig source)
    {
        int corrections = 0;
        StringBuilder summary = null;
        PerformanceConfig value;

        if (source == null)
        {
            value = new PerformanceConfig();
            AddCorrection(ref summary, ref corrections, "Performance");
        }
        else
        {
            value = source.Clone();
        }

        if (!Enum.IsDefined(typeof(PerformanceMode), value.Mode))
        {
            value.Mode = PerformanceMode.Automatic;
            AddCorrection(ref summary, ref corrections, nameof(value.Mode));
        }

        if (!Enum.IsDefined(typeof(PerformanceTier), value.FixedTier))
        {
            value.FixedTier = PerformanceTier.Full;
            AddCorrection(ref summary, ref corrections, nameof(value.FixedTier));
        }

        value.TalkyBudgetMsPerFrame = ClampFinite(
            value.TalkyBudgetMsPerFrame, 0.05d, 100d, 1d,
            nameof(value.TalkyBudgetMsPerFrame), ref summary, ref corrections);
        value.HardTalkyBudgetMsPerFrame = ClampFinite(
            value.HardTalkyBudgetMsPerFrame, value.TalkyBudgetMsPerFrame, 200d,
            Math.Max(3d, value.TalkyBudgetMsPerFrame),
            nameof(value.HardTalkyBudgetMsPerFrame), ref summary, ref corrections);

        value.ServerP95DegradeMs = ClampFinite(
            value.ServerP95DegradeMs, 5d, 1000d, 25d,
            nameof(value.ServerP95DegradeMs), ref summary, ref corrections);
        value.ServerP95EmergencyMs = ClampFinite(
            value.ServerP95EmergencyMs, value.ServerP95DegradeMs, 1000d,
            Math.Max(33.3d, value.ServerP95DegradeMs),
            nameof(value.ServerP95EmergencyMs), ref summary, ref corrections);
        value.ServerP95RecoveryMs = ClampFinite(
            value.ServerP95RecoveryMs, 1d, value.ServerP95DegradeMs, 20d,
            nameof(value.ServerP95RecoveryMs), ref summary, ref corrections);

        value.EvaluationWindowSeconds = Clamp(
            value.EvaluationWindowSeconds, 1, 60, 2,
            nameof(value.EvaluationWindowSeconds), ref summary, ref corrections);
        value.ConsecutiveBreachWindows = Clamp(
            value.ConsecutiveBreachWindows, 1, 120, 3,
            nameof(value.ConsecutiveBreachWindows), ref summary, ref corrections);
        value.EmergencyBreachWindows = Clamp(
            value.EmergencyBreachWindows, 1, 120, 2,
            nameof(value.EmergencyBreachWindows), ref summary, ref corrections);
        value.WarmupSeconds = Clamp(
            value.WarmupSeconds, 0, 600, 15,
            nameof(value.WarmupSeconds), ref summary, ref corrections);
        value.TransitionCooldownSeconds = Clamp(
            value.TransitionCooldownSeconds, 0, 3600, 20,
            nameof(value.TransitionCooldownSeconds), ref summary, ref corrections);
        value.RecoveryHealthySeconds = Clamp(
            value.RecoveryHealthySeconds, 1, 3600, 90,
            nameof(value.RecoveryHealthySeconds), ref summary, ref corrections);
        value.RecoveryBudgetRatio = ClampFinite(
            value.RecoveryBudgetRatio, 0.05d, 0.95d, 0.50d,
            nameof(value.RecoveryBudgetRatio), ref summary, ref corrections);
        value.RecoveryCooldownSeconds = Clamp(
            value.RecoveryCooldownSeconds, 0, 3600, 60,
            nameof(value.RecoveryCooldownSeconds), ref summary, ref corrections);
        value.RecoveryProbeSeconds = Clamp(
            value.RecoveryProbeSeconds, 10, 60, 10,
            nameof(value.RecoveryProbeSeconds), ref summary, ref corrections);

        value.EconomyFaceHz = Clamp(
            value.EconomyFaceHz, 15, 120, 20,
            nameof(value.EconomyFaceHz), ref summary, ref corrections);
        value.EconomyGlanceHz = Clamp(
            value.EconomyGlanceHz, 1, 120, 10,
            nameof(value.EconomyGlanceHz), ref summary, ref corrections);
        value.EconomyLookHz = Clamp(
            value.EconomyLookHz, 1, 120, 30,
            nameof(value.EconomyLookHz), ref summary, ref corrections);
        value.MinimumFaceHz = Clamp(
            value.MinimumFaceHz, 15, value.EconomyFaceHz, 15,
            nameof(value.MinimumFaceHz), ref summary, ref corrections);
        value.SummaryLogIntervalSeconds = Clamp(
            value.SummaryLogIntervalSeconds, 0, 86400, 0,
            nameof(value.SummaryLogIntervalSeconds), ref summary, ref corrections);

        return new PerformanceConfigValidationResult(
            value,
            corrections,
            summary == null ? string.Empty : summary.ToString());
    }

    public static PerformanceConfigValidationResult ValidateAndApply(Config config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        PerformanceConfigValidationResult result = Validate(config.Performance);
        config.Performance = result.Config;
        return result;
    }

    private static double ClampFinite(
        double value,
        double minimum,
        double maximum,
        double fallback,
        string name,
        ref StringBuilder summary,
        ref int corrections)
    {
        double corrected = value;
        if (double.IsNaN(corrected) || double.IsInfinity(corrected))
            corrected = fallback;
        if (corrected < minimum)
            corrected = minimum;
        if (corrected > maximum)
            corrected = maximum;

        if (corrected.Equals(value))
            return corrected;

        AddCorrection(ref summary, ref corrections, name);
        return corrected;
    }

    private static int Clamp(
        int value,
        int minimum,
        int maximum,
        int fallback,
        string name,
        ref StringBuilder summary,
        ref int corrections)
    {
        int corrected = value;
        if (minimum > maximum)
            corrected = fallback;
        else if (corrected < minimum)
            corrected = minimum;
        else if (corrected > maximum)
            corrected = maximum;

        if (corrected == value)
            return corrected;

        AddCorrection(ref summary, ref corrections, name);
        return corrected;
    }

    private static void AddCorrection(ref StringBuilder summary, ref int corrections, string name)
    {
        if (summary == null)
            summary = new StringBuilder();
        else
            summary.Append(", ");

        summary.Append(name);
        corrections++;
    }
}
