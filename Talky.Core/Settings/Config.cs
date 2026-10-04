using System;
using System.ComponentModel;
using System.Text;
using Talky.Performance;

namespace Talky.Settings;

public class Config
{
    [Description("Speech activity floor and fallback low threshold while a player's rolling range warms up.")]
    public double LowDbThreshold { get; set; } = -80.0;

    [Description("Fallback high dB threshold used while a player's rolling speech range warms up.")]
    public double HighDbThreshold { get; set; } = -30.0;

    [Description("Time in milliseconds for the mouth to reset to default after the player stops talking. Default is 500ms.")]
    public int EmotionResetTime { get; set; } = 500;

    [Description("How often to update volume. Default is 0.033f (30Hz)")]
    public float SpeechUpdateInterval { get; set; } = 0.033f;

    [Description("Allow players to use the Animate - Grab kebind to make their character reach out. Default is true.")]
    public bool EnableGrabAnimation { get; set; } = true;

    [Description("Should the player react with a specific emotion when hurt? Default is true.")]
    public bool EnableReactionOnHurt { get; set; } = true;
    [Description("How much damage must be taken in a single hit to trigger a hurt reaction. Default is 5.")]
    public int MiniumDamageForReaction { get; set; } = 5;

    [Description("Should the player open their mouth when consuming items (Medkits, SCP207, etc.)? Default is true.")]
    public bool EnableEmoteOnConsumables { get; set; } = true;

    [Description("Enable or disable head bobbing while talking. Default is true.")]
    public bool EnableHeadBobbing { get; set; } = true;
    [Description("How much should the player bob their head while talking. Default is 10.")]

    public float HeadBobAmount { get; set; } = 10;

    [Description("Enable or disable glancing towards the direction of the voice source while talking. Adds more immersion at cost of performance on large servers. Default is true.")]
    public bool EnableGlancing { get; set; } = true;

    [Description("The maximum range at which glancing will occur. Default is 7 meters.")]
    public float GlaceRange { get; set; } = 7f;

    [Description("The field of view angle within which glancing will occur. Default is 140 degrees.")]
    public float GlaceFov { get; set; } = 140f;

    [Description("The speed at which the player glances towards the voice source. Default is 1.65.")]
    public float GlaceGain { get; set; } = 1.5f;

    [Description("The maximum yaw angle for glancing. Default is 40 degrees.")]
    public float GlanceMaxYaw { get; set; } = 40f;

    [Description("The maximum pitch angle for glancing. Default is 35 degrees.")]
    public float GlanceMaxPitch { get; set; } = 35f;

    [Description("The maximum duration (in milliseconds) to look at voice source after they stop talking. Default is 1000ms.")]
    public long GlanceMaxDuration { get; set; } = 1000;

    [Description("Should Spectators see client-side head movements? Default is false.")]
    public bool EnabledSpectatorHeadMovements { get; set; } = false;

    [Description("Prefer structurally validated Harmony look-sync patches. When enabled, an incompatible method shape fails closed instead of entering the legacy return-valued RoleSyncEvent path. Set false only to opt into that interoperability-limited fallback. Default is true.")]
    public bool PreferHarmonyLookSync { get; set; } = true;

    [Description("Performance profiling and automatic quality controls.")]
    public PerformanceConfig Performance { get; set; } = new PerformanceConfig();

    [Description("Talky plugin translations")]
    public TranslationsConfig Translations { get; set; } = new TranslationsConfig();

    [Description("Enable or disable debug logs. Default is false.")]
    public virtual bool Debug { get; set; } = false;

}

public sealed class TalkyConfigValidationResult
{
    internal TalkyConfigValidationResult(int correctionCount, string correctionSummary)
    {
        CorrectionCount = correctionCount;
        CorrectionSummary = correctionSummary ?? string.Empty;
    }

    public int CorrectionCount { get; }
    public bool WasCorrected => CorrectionCount != 0;
    public string CorrectionSummary { get; }
}

/// <summary>
/// Applies bounded startup semantics to settings consumed by animation and hot paths.
/// </summary>
public static class TalkyConfigValidator
{
    public static TalkyConfigValidationResult ValidateAndApply(Config config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        int corrections = 0;
        StringBuilder summary = null;

        config.LowDbThreshold = ClampFinite(
            config.LowDbThreshold,
            -160d,
            -1d,
            -80d,
            nameof(config.LowDbThreshold),
            ref summary,
            ref corrections);
        config.HighDbThreshold = ClampFinite(
            config.HighDbThreshold,
            -159d,
            20d,
            -30d,
            nameof(config.HighDbThreshold),
            ref summary,
            ref corrections);
        if (config.HighDbThreshold <= config.LowDbThreshold)
        {
            config.HighDbThreshold = Math.Min(20d, config.LowDbThreshold + 1d);
            AddCorrection(ref summary, ref corrections, nameof(config.HighDbThreshold));
        }

        config.EmotionResetTime = Clamp(
            config.EmotionResetTime,
            0,
            60000,
            nameof(config.EmotionResetTime),
            ref summary,
            ref corrections);
        config.SpeechUpdateInterval = ClampFinite(
            config.SpeechUpdateInterval,
            1f / 240f,
            1f,
            0.033f,
            nameof(config.SpeechUpdateInterval),
            ref summary,
            ref corrections);
        config.MiniumDamageForReaction = Clamp(
            config.MiniumDamageForReaction,
            0,
            1000,
            nameof(config.MiniumDamageForReaction),
            ref summary,
            ref corrections);
        config.HeadBobAmount = ClampFinite(
            config.HeadBobAmount,
            0f,
            90f,
            10f,
            nameof(config.HeadBobAmount),
            ref summary,
            ref corrections);
        config.GlaceRange = ClampFinite(
            config.GlaceRange,
            0f,
            100f,
            7f,
            nameof(config.GlaceRange),
            ref summary,
            ref corrections);
        config.GlaceFov = ClampFinite(
            config.GlaceFov,
            0f,
            360f,
            140f,
            nameof(config.GlaceFov),
            ref summary,
            ref corrections);
        config.GlaceGain = ClampFinite(
            config.GlaceGain,
            0f,
            20f,
            1.5f,
            nameof(config.GlaceGain),
            ref summary,
            ref corrections);
        config.GlanceMaxYaw = ClampFinite(
            config.GlanceMaxYaw,
            0f,
            180f,
            40f,
            nameof(config.GlanceMaxYaw),
            ref summary,
            ref corrections);
        config.GlanceMaxPitch = ClampFinite(
            config.GlanceMaxPitch,
            0f,
            90f,
            35f,
            nameof(config.GlanceMaxPitch),
            ref summary,
            ref corrections);
        config.GlanceMaxDuration = Clamp(
            config.GlanceMaxDuration,
            0L,
            60000L,
            nameof(config.GlanceMaxDuration),
            ref summary,
            ref corrections);

        return new TalkyConfigValidationResult(
            corrections,
            summary == null ? string.Empty : summary.ToString());
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

    private static float ClampFinite(
        float value,
        float minimum,
        float maximum,
        float fallback,
        string name,
        ref StringBuilder summary,
        ref int corrections)
    {
        float corrected = value;
        if (float.IsNaN(corrected) || float.IsInfinity(corrected))
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
        string name,
        ref StringBuilder summary,
        ref int corrections)
    {
        int corrected = Math.Max(minimum, Math.Min(maximum, value));
        if (corrected == value)
            return corrected;

        AddCorrection(ref summary, ref corrections, name);
        return corrected;
    }

    private static long Clamp(
        long value,
        long minimum,
        long maximum,
        string name,
        ref StringBuilder summary,
        ref int corrections)
    {
        long corrected = Math.Max(minimum, Math.Min(maximum, value));
        if (corrected == value)
            return corrected;

        AddCorrection(ref summary, ref corrections, name);
        return corrected;
    }

    private static void AddCorrection(
        ref StringBuilder summary,
        ref int corrections,
        string name)
    {
        if (summary == null)
            summary = new StringBuilder();
        else
            summary.Append(", ");

        summary.Append(name);
        corrections++;
    }
}
