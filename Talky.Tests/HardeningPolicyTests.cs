using System;
using Talky.Settings;

namespace Talky.Tests;

internal static class HardeningPolicyTests
{
    public static int Run()
    {
        int assertions = 0;
        TestFunctionalConfigValidation(ref assertions);
        return assertions;
    }

    private static void TestFunctionalConfigValidation(ref int assertions)
    {
        Config config = new Config
        {
            LowDbThreshold = double.NaN,
            HighDbThreshold = double.PositiveInfinity,
            EmotionResetTime = -1,
            SpeechUpdateInterval = float.NaN,
            MiniumDamageForReaction = int.MaxValue,
            HeadBobAmount = float.PositiveInfinity,
            GlaceRange = -1f,
            GlaceFov = 361f,
            GlaceGain = -1f,
            GlanceMaxYaw = 181f,
            GlanceMaxPitch = 91f,
            GlanceMaxDuration = long.MaxValue,
            Performance = null,
            Translations = new TranslationsConfig
            {
                SSGroupLabel = null,
                SSGrabLabel = "   ",
                SSGrabHint = new string('x', FunctionalConfigValidator.MaximumTranslationLength + 10),
            },
        };

        FunctionalConfigValidationResult result =
            FunctionalConfigValidator.ValidateAndApply(config);
        Config defaults = new Config();

        True(result.WasCorrected && result.CorrectionCount >= 15,
            "Invalid functional configuration reports corrections", ref assertions);
        Equal(defaults.LowDbThreshold, config.LowDbThreshold,
            "Invalid low threshold falls back", ref assertions);
        Equal(defaults.HighDbThreshold, config.HighDbThreshold,
            "Invalid high threshold falls back", ref assertions);
        Equal(defaults.EmotionResetTime, config.EmotionResetTime,
            "Invalid reset duration falls back", ref assertions);
        Equal(defaults.SpeechUpdateInterval, config.SpeechUpdateInterval,
            "Invalid cadence falls back", ref assertions);
        Equal(defaults.HeadBobAmount, config.HeadBobAmount,
            "Non-finite head bob falls back", ref assertions);
        Equal(defaults.GlaceRange, config.GlaceRange,
            "Negative glance range falls back", ref assertions);
        True(config.Performance != null,
            "Missing performance configuration is restored", ref assertions);
        Equal(defaults.Translations.SSGroupLabel, config.Translations.SSGroupLabel,
            "Null translation falls back", ref assertions);
        Equal(defaults.Translations.SSGrabLabel, config.Translations.SSGrabLabel,
            "Whitespace translation falls back", ref assertions);
        Equal(FunctionalConfigValidator.MaximumTranslationLength,
            config.Translations.SSGrabHint.Length,
            "Long translation is bounded", ref assertions);

        Config inverted = new Config { LowDbThreshold = -20d, HighDbThreshold = -80d };
        FunctionalConfigValidationResult invertedResult =
            FunctionalConfigValidator.ValidateAndApply(inverted);
        True(invertedResult.WasCorrected &&
             inverted.LowDbThreshold == defaults.LowDbThreshold &&
             inverted.HighDbThreshold == defaults.HighDbThreshold,
            "Inverted dB anchors reset as a pair", ref assertions);

        Config valid = new Config();
        FunctionalConfigValidationResult validResult =
            FunctionalConfigValidator.ValidateAndApply(valid);
        True(!validResult.WasCorrected,
            "Default functional configuration remains unchanged", ref assertions);
    }

    private static void True(bool value, string message, ref int assertions)
    {
        assertions++;
        if (!value)
            throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message, ref int assertions)
    {
        assertions++;
        if (!Equals(expected, actual))
        {
            throw new InvalidOperationException(
                message + ": expected=" + expected + ", actual=" + actual);
        }
    }
}
