using System;
using Talky.Security;
using Talky.Settings;

namespace Talky.Tests;

internal static class HardeningPolicyTests
{
    public static int Run()
    {
        int assertions = 0;
        TestGrabAdmission(ref assertions);
        TestEmotionAdmission(ref assertions);
        TestSettingAdmissionAllocation(ref assertions);
        TestRecurringFailureQuarantine(ref assertions);
        TestFunctionalConfigValidation(ref assertions);
        return assertions;
    }

    private static void TestGrabAdmission(ref int assertions)
    {
        PlayerSettingAdmission state = new PlayerSettingAdmission();
        Equal(GrabAdmission.Accepted, state.ObserveGrab(true, 0d),
            "First grab edge is accepted", ref assertions);
        Equal(GrabAdmission.DuplicatePress, state.ObserveGrab(true, 0.1d),
            "Repeated pressed messages are not new edges", ref assertions);
        Equal(GrabAdmission.Released, state.ObserveGrab(false, 0.2d),
            "Release arms a future edge", ref assertions);
        Equal(GrabAdmission.Throttled, state.ObserveGrab(true, 1d),
            "A new edge inside the animation window is throttled", ref assertions);
        Equal(GrabAdmission.Released, state.ObserveGrab(false, 1.05d),
            "Release after throttle is observed", ref assertions);
        Equal(GrabAdmission.Accepted,
            state.ObserveGrab(true, PlayerSettingAdmission.GrabAnimationSeconds),
            "Animation boundary admits the next edge", ref assertions);
        Equal(GrabAdmission.InvalidTime, state.ObserveGrab(false, double.NaN),
            "Non-finite admission time fails closed", ref assertions);
    }

    private static void TestEmotionAdmission(ref int assertions)
    {
        PlayerSettingAdmission state = new PlayerSettingAdmission();
        Equal(EmotionAdmission.InvalidSelection, state.ObserveEmotion(-1, 0d),
            "Negative emotion is rejected", ref assertions);
        Equal(EmotionAdmission.InvalidSelection, state.ObserveEmotion(7, 0d),
            "Out-of-range emotion is rejected", ref assertions);
        Equal(EmotionAdmission.Applied, state.ObserveEmotion(1, 0d),
            "First valid emotion applies immediately", ref assertions);
        Equal(EmotionAdmission.Coalesced, state.ObserveEmotion(2, 0.1d),
            "Rapid emotion is coalesced", ref assertions);
        Equal(EmotionAdmission.Coalesced, state.ObserveEmotion(3, 0.2d),
            "Latest rapid emotion replaces the intermediate choice", ref assertions);
        True(!state.TryTakePendingEmotion(0.49d, out _),
            "Pending emotion waits for the half-second boundary", ref assertions);
        True(state.TryTakePendingEmotion(0.5d, out int pending) && pending == 3,
            "Only the latest coalesced emotion is applied", ref assertions);
        Equal(EmotionAdmission.Duplicate, state.ObserveEmotion(3, 0.6d),
            "Already-applied emotion does not rebroadcast", ref assertions);

        state.Reset();
        Equal(EmotionAdmission.Applied, state.ObserveEmotion(1, 1d),
            "Reset state accepts a fresh emotion", ref assertions);
        Equal(EmotionAdmission.Coalesced, state.ObserveEmotion(2, 1.1d),
            "Reset state still enforces cadence", ref assertions);
        Equal(EmotionAdmission.Duplicate, state.ObserveEmotion(1, 1.2d),
            "Returning to active emotion cancels an intermediate pending value", ref assertions);
        True(!state.HasPendingEmotion,
            "Canceled intermediate emotion is no longer pending", ref assertions);
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

    private static void TestSettingAdmissionAllocation(ref int assertions)
    {
        PlayerSettingAdmission state = new PlayerSettingAdmission();
        state.ObserveGrab(false, 0d);
        state.ObserveEmotion(0, 0d);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i <= 10000; i++)
        {
            double now = i * PlayerSettingAdmission.GrabAnimationSeconds;
            state.ObserveGrab(false, now);
            state.ObserveGrab(true, now);
            state.ObserveEmotion(i % 7, i * 0.5d);
            state.TryTakePendingEmotion(i * 0.5d, out _);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Equal(0L, allocated, "Setting admission hot path does not allocate", ref assertions);
    }

    private static void TestRecurringFailureQuarantine(ref int assertions)
    {
        ConsecutiveFailureCircuitBreaker breaker =
            new ConsecutiveFailureCircuitBreaker();
        const int facePhase = 3;
        const int idleFramePhase = 1;

        True(!breaker.RecordFailure(typeof(InvalidOperationException), facePhase),
            "First recurring failure is recorded", ref assertions);
        breaker.RecordSuccess(idleFramePhase);
        True(!breaker.RecordFailure(typeof(InvalidOperationException), facePhase),
            "Unrelated idle success does not erase a lower-cadence failure", ref assertions);
        breaker.RecordSuccess(idleFramePhase);
        True(breaker.RecordFailure(typeof(InvalidOperationException), facePhase),
            "Third same-type phase failure activates quarantine", ref assertions);
        True(breaker.IsQuarantined &&
             breaker.FailureCount == ConsecutiveFailureCircuitBreaker.FailureThreshold,
            "Quarantine retains triggering evidence", ref assertions);
        True(!breaker.RecordFailure(typeof(InvalidOperationException), facePhase),
            "Quarantine activates only once", ref assertions);

        breaker.Reset();
        True(!breaker.IsQuarantined && breaker.FailureCount == 0,
            "Round or reload reset clears recurring-failure state", ref assertions);
        breaker.RecordFailure(typeof(InvalidOperationException), facePhase);
        breaker.RecordSuccess(facePhase);
        True(breaker.FailureCount == 0,
            "Successful execution of the failed phase resets the streak", ref assertions);
        breaker.RecordFailure(typeof(InvalidOperationException), facePhase);
        breaker.RecordFailure(typeof(ArgumentException), facePhase);
        True(breaker.FailureCount == 1 && breaker.FailureType == typeof(ArgumentException),
            "A different exception type starts a new streak", ref assertions);
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
