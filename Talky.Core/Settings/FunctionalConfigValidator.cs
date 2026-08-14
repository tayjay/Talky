using System;
using System.Text;

namespace Talky.Settings;

/// <summary>
/// Normalizes administrator-controlled functional settings before any Unity or networking code
/// consumes them. Invalid values fall back to documented defaults instead of being clamped to an
/// attacker- or typo-selected extreme.
/// </summary>
public static class FunctionalConfigValidator
{
    public const int MaximumTranslationLength = 128;

    public static FunctionalConfigValidationResult ValidateAndApply(Config config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        Config defaults = new Config();
        int corrections = 0;
        StringBuilder summary = null;

        config.LowDbThreshold = Normalize(
            config.LowDbThreshold, -160d, 0d, defaults.LowDbThreshold,
            nameof(config.LowDbThreshold), ref corrections, ref summary);
        config.HighDbThreshold = Normalize(
            config.HighDbThreshold, -160d, 0d, defaults.HighDbThreshold,
            nameof(config.HighDbThreshold), ref corrections, ref summary);
        if (config.LowDbThreshold >= config.HighDbThreshold)
        {
            config.LowDbThreshold = defaults.LowDbThreshold;
            config.HighDbThreshold = defaults.HighDbThreshold;
            AddCorrection(
                nameof(config.LowDbThreshold) + "/" + nameof(config.HighDbThreshold),
                ref corrections,
                ref summary);
        }

        config.EmotionResetTime = Normalize(
            config.EmotionResetTime, 0, 60000, defaults.EmotionResetTime,
            nameof(config.EmotionResetTime), ref corrections, ref summary);
        config.SpeechUpdateInterval = Normalize(
            config.SpeechUpdateInterval, 0.01f, 1f, defaults.SpeechUpdateInterval,
            nameof(config.SpeechUpdateInterval), ref corrections, ref summary);
        config.MiniumDamageForReaction = Normalize(
            config.MiniumDamageForReaction, 0, 5000, defaults.MiniumDamageForReaction,
            nameof(config.MiniumDamageForReaction), ref corrections, ref summary);
        config.HeadBobAmount = Normalize(
            config.HeadBobAmount, 0f, 90f, defaults.HeadBobAmount,
            nameof(config.HeadBobAmount), ref corrections, ref summary);
        config.GlaceRange = Normalize(
            config.GlaceRange, 0f, 100f, defaults.GlaceRange,
            nameof(config.GlaceRange), ref corrections, ref summary);
        config.GlaceFov = Normalize(
            config.GlaceFov, 0f, 360f, defaults.GlaceFov,
            nameof(config.GlaceFov), ref corrections, ref summary);
        config.GlaceGain = Normalize(
            config.GlaceGain, 0f, 20f, defaults.GlaceGain,
            nameof(config.GlaceGain), ref corrections, ref summary);
        config.GlanceMaxYaw = Normalize(
            config.GlanceMaxYaw, 0f, 180f, defaults.GlanceMaxYaw,
            nameof(config.GlanceMaxYaw), ref corrections, ref summary);
        config.GlanceMaxPitch = Normalize(
            config.GlanceMaxPitch, 0f, 90f, defaults.GlanceMaxPitch,
            nameof(config.GlanceMaxPitch), ref corrections, ref summary);
        config.GlanceMaxDuration = Normalize(
            config.GlanceMaxDuration, 0L, 60000L, defaults.GlanceMaxDuration,
            nameof(config.GlanceMaxDuration), ref corrections, ref summary);

        if (config.Performance == null)
        {
            config.Performance = defaults.Performance;
            AddCorrection(nameof(config.Performance), ref corrections, ref summary);
        }

        if (config.Translations == null)
        {
            config.Translations = defaults.Translations;
            AddCorrection(nameof(config.Translations), ref corrections, ref summary);
        }
        else
        {
            NormalizeTranslations(
                config.Translations,
                defaults.Translations,
                ref corrections,
                ref summary);
        }

        return new FunctionalConfigValidationResult(
            corrections,
            summary?.ToString() ?? string.Empty);
    }

    private static void NormalizeTranslations(
        TranslationsConfig value,
        TranslationsConfig defaults,
        ref int corrections,
        ref StringBuilder summary)
    {
        value.SSGroupLabel = NormalizeText(value.SSGroupLabel, defaults.SSGroupLabel,
            nameof(value.SSGroupLabel), ref corrections, ref summary);
        value.SSGrabLabel = NormalizeText(value.SSGrabLabel, defaults.SSGrabLabel,
            nameof(value.SSGrabLabel), ref corrections, ref summary);
        value.SSGrabHint = NormalizeText(value.SSGrabHint, defaults.SSGrabHint,
            nameof(value.SSGrabHint), ref corrections, ref summary);
        value.SSDefaultEmotionLabel = NormalizeText(
            value.SSDefaultEmotionLabel, defaults.SSDefaultEmotionLabel,
            nameof(value.SSDefaultEmotionLabel), ref corrections, ref summary);
        value.SSDefaultEmotionHint = NormalizeText(
            value.SSDefaultEmotionHint, defaults.SSDefaultEmotionHint,
            nameof(value.SSDefaultEmotionHint), ref corrections, ref summary);
        value.SSEnableTalkingLabel = NormalizeText(
            value.SSEnableTalkingLabel, defaults.SSEnableTalkingLabel,
            nameof(value.SSEnableTalkingLabel), ref corrections, ref summary);
        value.SSEnableTalkingHint = NormalizeText(
            value.SSEnableTalkingHint, defaults.SSEnableTalkingHint,
            nameof(value.SSEnableTalkingHint), ref corrections, ref summary);
        value.Neutral = NormalizeText(value.Neutral, defaults.Neutral,
            nameof(value.Neutral), ref corrections, ref summary);
        value.Happy = NormalizeText(value.Happy, defaults.Happy,
            nameof(value.Happy), ref corrections, ref summary);
        value.AwkwardSmile = NormalizeText(value.AwkwardSmile, defaults.AwkwardSmile,
            nameof(value.AwkwardSmile), ref corrections, ref summary);
        value.Scared = NormalizeText(value.Scared, defaults.Scared,
            nameof(value.Scared), ref corrections, ref summary);
        value.Angry = NormalizeText(value.Angry, defaults.Angry,
            nameof(value.Angry), ref corrections, ref summary);
        value.Chad = NormalizeText(value.Chad, defaults.Chad,
            nameof(value.Chad), ref corrections, ref summary);
        value.Ogre = NormalizeText(value.Ogre, defaults.Ogre,
            nameof(value.Ogre), ref corrections, ref summary);
        value.SSEnabled = NormalizeText(value.SSEnabled, defaults.SSEnabled,
            nameof(value.SSEnabled), ref corrections, ref summary);
        value.SSDisabled = NormalizeText(value.SSDisabled, defaults.SSDisabled,
            nameof(value.SSDisabled), ref corrections, ref summary);
    }

    private static string NormalizeText(
        string value,
        string fallback,
        string name,
        ref int corrections,
        ref StringBuilder summary)
    {
        string normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized))
            normalized = fallback;
        else if (normalized.Length > MaximumTranslationLength)
            normalized = normalized.Substring(0, MaximumTranslationLength);

        if (!string.Equals(value, normalized, StringComparison.Ordinal))
            AddCorrection(name, ref corrections, ref summary);
        return normalized;
    }

    private static double Normalize(
        double value,
        double minimum,
        double maximum,
        double fallback,
        string name,
        ref int corrections,
        ref StringBuilder summary)
    {
        if (!double.IsNaN(value) && !double.IsInfinity(value) &&
            value >= minimum && value <= maximum)
        {
            return value;
        }

        AddCorrection(name, ref corrections, ref summary);
        return fallback;
    }

    private static float Normalize(
        float value,
        float minimum,
        float maximum,
        float fallback,
        string name,
        ref int corrections,
        ref StringBuilder summary)
    {
        if (!float.IsNaN(value) && !float.IsInfinity(value) &&
            value >= minimum && value <= maximum)
        {
            return value;
        }

        AddCorrection(name, ref corrections, ref summary);
        return fallback;
    }

    private static int Normalize(
        int value,
        int minimum,
        int maximum,
        int fallback,
        string name,
        ref int corrections,
        ref StringBuilder summary)
    {
        if (value >= minimum && value <= maximum)
            return value;

        AddCorrection(name, ref corrections, ref summary);
        return fallback;
    }

    private static long Normalize(
        long value,
        long minimum,
        long maximum,
        long fallback,
        string name,
        ref int corrections,
        ref StringBuilder summary)
    {
        if (value >= minimum && value <= maximum)
            return value;

        AddCorrection(name, ref corrections, ref summary);
        return fallback;
    }

    private static void AddCorrection(
        string name,
        ref int corrections,
        ref StringBuilder summary)
    {
        if (summary == null)
            summary = new StringBuilder();
        else if (summary.Length != 0)
            summary.Append(", ");

        summary.Append(name);
        corrections++;
    }
}

public sealed class FunctionalConfigValidationResult
{
    internal FunctionalConfigValidationResult(int correctionCount, string correctionSummary)
    {
        CorrectionCount = correctionCount;
        CorrectionSummary = correctionSummary ?? string.Empty;
    }

    public int CorrectionCount { get; }
    public string CorrectionSummary { get; }
    public bool WasCorrected => CorrectionCount != 0;
}
