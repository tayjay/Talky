using System;
using System.Globalization;
using System.IO;
using Talky.Performance;
using Talky.Voice;

namespace Talky.Tests;

internal static class RollingSpeechPercentilesTests
{
    private static int _assertions;
    private static double _measurementSink;

    public static int Run()
    {
        _assertions = 0;
        TestWarmupAndPercentiles();
        TestRollingEvictionAndWhisperAdaptation();
        TestDefaultWhisperAdaptationBoundaries();
        TestReferenceEquivalenceAcrossWraps();
        TestObservationCadence();
        TestMinimumAnimationSpan();
        TestBriefLoudBurstAgainstModerateHistory();
        TestSustainedLoudMovesUpperPercentile();
        TestSustainedWhisperMovesPercentiles();
        TestQuantizedFaceComparisons();
        TestFractionalActivityFloorComparisons();
        TestHotMicNoiseThenWhisper();
        TestHotMicPcmTransition();
        TestAudibleSpeechActivity();
        TestRejectedLevelsAndReset();
        TestConstructorBounds();
        TestPresentationSnapshot();
        TestSteadyStateAllocation();
        WriteSimulationReportIfRequested();
        return _assertions;
    }

    private static void TestWarmupAndPercentiles()
    {
        RollingSpeechPercentiles percentiles = new RollingSpeechPercentiles(
            capacity: 10,
            minimumObservations: 4,
            lowPercentile: 0.20d,
            highPercentile: 0.80d,
            minimumSpanDecibels: 2d);

        True(percentiles.Observe(-60d), "Speech percentile accepts finite level");
        True(percentiles.Observe(-50d), "Speech percentile accepts second level");
        True(percentiles.Observe(-40d), "Speech percentile accepts third level");
        True(!percentiles.IsReady, "Speech percentile waits for warmup");
        True(double.IsNaN(percentiles.LowPercentileDecibels), "Warmup has no low percentile");

        percentiles.Observe(-30d);
        percentiles.Observe(-20d);
        True(percentiles.IsReady, "Speech percentile becomes ready");
        Equal(5, percentiles.Count, "Speech percentile counts observations");
        Near(-59.5d, percentiles.LowPercentileDecibels, 0d, "Nearest-rank Q20");
        Near(-29.5d, percentiles.HighPercentileDecibels, 0d, "Nearest-rank Q80");

        RollingSpeechPercentiles outliers = new RollingSpeechPercentiles(
            capacity: 10,
            minimumObservations: 10);
        double[] observations =
        {
            -96d, -50d, -50d, -50d, -50d,
            -40d, -40d, -40d, -40d, 0d,
        };
        for (int i = 0; i < observations.Length; i++)
            outliers.Observe(observations[i]);

        Near(-49.5d, outliers.LowPercentileDecibels, 0d, "Low percentile rejects lone floor outlier");
        Near(-39.5d, outliers.HighPercentileDecibels, 0d, "High percentile rejects lone peak outlier");
    }

    private static void TestRollingEvictionAndWhisperAdaptation()
    {
        RollingSpeechPercentiles percentiles = new RollingSpeechPercentiles(
            capacity: 5,
            minimumObservations: 5,
            lowPercentile: 0.20d,
            highPercentile: 0.80d,
            minimumSpanDecibels: 8d);
        double[] initial = { -60d, -50d, -40d, -30d, -20d };
        for (int i = 0; i < initial.Length; i++)
            percentiles.Observe(initial[i]);

        Near(-59.5d, percentiles.LowPercentileDecibels, 0d, "Rolling range initial low");
        Near(-29.5d, percentiles.HighPercentileDecibels, 0d, "Rolling range initial high");

        percentiles.Observe(-10d);
        Equal(5, percentiles.Count, "Rolling range remains bounded");
        Near(-49.5d, percentiles.LowPercentileDecibels, 0d, "Rolling range evicts oldest low");
        Near(-19.5d, percentiles.HighPercentileDecibels, 0d, "Rolling range includes recent high");

        for (int i = 0; i < 5; i++)
            percentiles.Observe(-70d);

        Near(-69.5d, percentiles.LowPercentileDecibels, 0d, "Sustained whisper replaces old low");
        Near(-69.5d, percentiles.HighPercentileDecibels, 0d, "Sustained whisper replaces old high");
        Near(8d, percentiles.AnimationHighDecibels - percentiles.AnimationLowDecibels, 0d, "Whisper retains animation span");
    }

    private static void TestMinimumAnimationSpan()
    {
        RollingSpeechPercentiles middle = CreateConstantRange(-60d);
        Near(-59.5d, middle.LowPercentileDecibels, 0d, "Constant speech raw low");
        Near(-59.5d, middle.HighPercentileDecibels, 0d, "Constant speech raw high");
        Near(-63.5d, middle.AnimationLowDecibels, 0d, "Constant speech expands low");
        Near(-55.5d, middle.AnimationHighDecibels, 0d, "Constant speech expands high");

        RollingSpeechPercentiles upper = CreateConstantRange(-1d);
        Near(-8d, upper.AnimationLowDecibels, 0d, "Upper range shifts inside ceiling");
        Near(0d, upper.AnimationHighDecibels, 0d, "Upper range clamps ceiling");

        RollingSpeechPercentiles lower = CreateConstantRange(-95d);
        Near(-96d, lower.AnimationLowDecibels, 0d, "Lower range clamps floor");
        Near(-88d, lower.AnimationHighDecibels, 0d, "Lower range shifts inside floor");
    }

    private static void TestObservationCadence()
    {
        SpeechObservationCadence cadence = new SpeechObservationCadence(4800);
        Equal(4800, cadence.RequiredSamples, "Speech cadence window size");
        True(!cadence.Advance(0), "Speech cadence rejects empty decode");
        for (int packet = 0; packet < 9; packet++)
            True(!cadence.Advance(480), "Speech cadence waits for complete first window");
        Equal(4320, cadence.FreshSamples, "Speech cadence tracks fresh samples");
        True(cadence.Advance(480), "Speech cadence observes tenth packet");

        for (int packet = 0; packet < 9; packet++)
            True(!cadence.Advance(480), "Speech cadence waits for next non-overlapping window");
        True(cadence.Advance(480), "Speech cadence observes next tenth packet");

        cadence.Reset();
        Equal(0, cadence.FreshSamples, "Speech cadence gap reset clears freshness");
        for (int packet = 0; packet < 9; packet++)
            True(!cadence.Advance(480), "Speech cadence re-primes after gap");
        True(cadence.Advance(480), "Speech cadence observes after gap re-prime");
    }

    private static void TestBriefLoudBurstAgainstModerateHistory()
    {
        RollingSpeechPercentiles percentiles = CreateFilledRange(-45d);
        Near(-44.5d, percentiles.LowPercentileDecibels, 0d, "Moderate history Q20");
        Near(-44.5d, percentiles.HighPercentileDecibels, 0d, "Moderate history Q80");

        SpeechAnimationDecision moderate = EvaluateCurrentAnimation(-45d, percentiles);
        Equal(SpeechAnimationLevel.Quiet, moderate.Level, "Moderate history animates at quiet level");
        Near(0.521875d, moderate.VolumeRatio, 0.000001d, "Moderate history head-bob ratio");

        for (int observation = 0; observation < 3; observation++)
        {
            percentiles.Observe(-15d);
            Near(-44.5d, percentiles.HighPercentileDecibels, 0d, "Brief loud burst does not immediately move Q80");
            True(-15d > percentiles.HighPercentileDecibels, "Brief loud volume remains above Q80");

            SpeechAnimationDecision loud = EvaluateCurrentAnimation(-15d, percentiles);
            Equal(SpeechAnimationLevel.Loud, loud.Level, "Brief loud volume animates at loud level");
            Near(1d, loud.VolumeRatio, 0d, "Brief loud volume reaches full head bob");
        }
    }

    private static void TestSustainedLoudMovesUpperPercentile()
    {
        RollingSpeechPercentiles percentiles = CreateFilledRange(-45d);
        for (int observation = 0; observation < 12; observation++)
            percentiles.Observe(-15d);

        Near(-44.5d, percentiles.HighPercentileDecibels, 0d, "Q80 retains moderate history through twelve loud replacements");
        percentiles.Observe(-15d);
        Near(-14.5d, percentiles.HighPercentileDecibels, 0d, "Q80 moves on thirteenth loud replacement");

        SpeechAnimationDecision adapted = EvaluateCurrentAnimation(-15d, percentiles);
        Near(0.985833333d, adapted.VolumeRatio, 0.000001d, "Sustained loud speech remains near full head bob after Q80 adapts");
        Equal(
            SpeechAnimationLevel.Loud,
            adapted.Level,
            "Live loud level uses the same histogram bin center as Q80");
    }

    private static void TestSustainedWhisperMovesPercentiles()
    {
        RollingSpeechPercentiles percentiles = CreateFilledRange(-45d);
        percentiles.Observe(-70d);
        SpeechAnimationDecision initialWhisper = EvaluateCurrentAnimation(-70d, percentiles);
        Equal(SpeechAnimationLevel.Silent, initialWhisper.Level, "Initial whisper remains below moderate-history Q20");
        Near(0d, initialWhisper.VolumeRatio, 0d, "Initial whisper has no head bob");

        for (int observation = 1; observation < 12; observation++)
            percentiles.Observe(-70d);
        Near(-69.5d, percentiles.LowPercentileDecibels, 0d, "Q20 moves on twelfth whisper replacement");
        Near(-44.5d, percentiles.HighPercentileDecibels, 0d, "Q80 retains moderate history after twelve whispers");
        Equal(
            SpeechAnimationLevel.Quiet,
            EvaluateCurrentAnimation(-70d, percentiles).Level,
            "Live whisper uses the same histogram bin center as Q20");
        Near(
            0.15d,
            EvaluateCurrentAnimation(-70d, percentiles).VolumeRatio,
            0.000001d,
            "Newly adapted whisper receives minimum head-bob drive");

        for (int observation = 12; observation < 48; observation++)
            percentiles.Observe(-70d);
        Near(-69.5d, percentiles.HighPercentileDecibels, 0d, "Q80 moves after forty-eight whisper replacements");

        SpeechAnimationDecision adaptedWhisper = EvaluateCurrentAnimation(-70d, percentiles);
        Equal(SpeechAnimationLevel.Quiet, adaptedWhisper.Level, "Collapsed whisper percentiles use expanded animation anchors");
        Near(0.521875d, adaptedWhisper.VolumeRatio, 0.000001d, "Adapted whisper restores head bob");
    }

    private static void TestQuantizedFaceComparisons()
    {
        Near(
            -69.5d,
            SpeechAnimationMath.QuantizeForPercentileComparison(-70d),
            0d,
            "Live comparison uses -70 dB bin center");
        Near(
            -14.5d,
            SpeechAnimationMath.QuantizeForPercentileComparison(-15d),
            0d,
            "Live comparison uses -15 dB bin center");
        Near(
            0d,
            SpeechAnimationMath.QuantizeForPercentileComparison(3d),
            0d,
            "Live comparison clamps above histogram ceiling");
        True(
            double.IsNegativeInfinity(
                SpeechAnimationMath.QuantizeForPercentileComparison(double.NaN)),
            "NaN live comparison fails closed");
    }

    private static void TestFractionalActivityFloorComparisons()
    {
        SpeechAnimationDecision belowFloor = SpeechAnimationMath.Evaluate(
            -80.4d,
            -80.3d,
            -30.3d,
            false,
            double.NaN,
            double.NaN,
            double.NaN,
            double.NaN,
            0.15f);
        Equal(SpeechAnimationLevel.Silent, belowFloor.Level, "Raw level below fractional floor is silent");
        Near(0d, belowFloor.VolumeRatio, 0d, "Below fractional floor has no animation");

        SpeechAnimationDecision aboveFloor = SpeechAnimationMath.Evaluate(
            -80.2d,
            -80.3d,
            -30.3d,
            false,
            double.NaN,
            double.NaN,
            double.NaN,
            double.NaN,
            0.15f);
        Equal(SpeechAnimationLevel.Quiet, aboveFloor.Level, "Raw level above fractional floor is active");
        True(aboveFloor.VolumeRatio > 0f, "Fractional floor does not lose active fallback speech to bin quantization");

        SpeechAnimationDecision learned = SpeechAnimationMath.Evaluate(
            -80.2d,
            -80.3d,
            -30.3d,
            true,
            -80.5d,
            -70.5d,
            -80.5d,
            -70.5d,
            0.15f);
        Equal(SpeechAnimationLevel.Quiet, learned.Level, "Learned percentile accepts matching quantized floor bin");
        True(learned.VolumeRatio >= 0.15f, "Learned fractional-floor speech keeps adaptive animation");
    }

    private static void TestHotMicNoiseThenWhisper()
    {
        const double activityFloor = -80d;
        RollingSpeechPercentiles percentiles = new RollingSpeechPercentiles();

        for (int observation = 0; observation < 60; observation++)
        {
            double background = -90d;
            if (SpeechAnimationMath.IsAtOrAboveActivityFloor(background, activityFloor))
                percentiles.Observe(background);

            SpeechAnimationDecision decision =
                EvaluateCurrentAnimation(background, percentiles);
            Equal(0, percentiles.Count, "Below-floor hot mic does not age speech history");
            Equal(SpeechAnimationLevel.Silent, decision.Level, "Below-floor hot mic remains silent");
            Near(0d, decision.VolumeRatio, 0d, "Below-floor hot mic has no head-bob drive");
        }

        for (int observation = 0; observation < 9; observation++)
        {
            ObserveIfAboveActivityFloor(percentiles, -70d, activityFloor);
            SpeechAnimationDecision decision =
                EvaluateCurrentAnimation(-70d, percentiles);
            Equal(SpeechAnimationLevel.Quiet, decision.Level, "Cold-start whisper reaches quiet face level");
            Near(0.2d, decision.VolumeRatio, 0.000001d, "Cold-start whisper uses fallback animation range");
        }

        ObserveIfAboveActivityFloor(percentiles, -70d, activityFloor);
        True(percentiles.IsReady, "Whisper profile becomes ready on tenth accepted observation");
        Equal(10, percentiles.Count, "Only whisper observations populate hot-mic profile");
        Near(-69.5d, percentiles.LowPercentileDecibels, 0d, "Whisper profile Q20");
        Near(-69.5d, percentiles.HighPercentileDecibels, 0d, "Whisper profile Q80");

        SpeechAnimationDecision adapted =
            EvaluateCurrentAnimation(-70d, percentiles);
        Equal(SpeechAnimationLevel.Quiet, adapted.Level, "Adapted whisper remains quiet");
        Near(0.521875d, adapted.VolumeRatio, 0.000001d, "Adapted whisper drives head bob");
    }

    private static void TestHotMicPcmTransition()
    {
        const double activityFloor = -80d;
        RollingVoiceEnergy energy = new RollingVoiceEnergy();
        float[] backgroundPacket = CreateConstantDecibelPacket(-90d);
        float[] whisperPacket = CreateConstantDecibelPacket(-70d);

        for (int packet = 0; packet < 10; packet++)
            energy.Write(backgroundPacket, backgroundPacket.Length);
        Near(-90d, energy.Decibels, 0.0001d, "Hot-mic PCM fills rolling energy at background level");
        True(
            !SpeechAnimationMath.IsAtOrAboveActivityFloor(energy.Decibels, activityFloor),
            "Steady background PCM stays below activity floor");

        energy.Write(whisperPacket, whisperPacket.Length);
        True(
            SpeechAnimationMath.IsAtOrAboveActivityFloor(energy.Decibels, activityFloor),
            "First 10 ms whisper packet raises rolling RMS above activity floor");
        for (int packet = 1; packet < 10; packet++)
            energy.Write(whisperPacket, whisperPacket.Length);
        Near(-70d, energy.Decibels, 0.0001d, "Whisper PCM fills rolling energy at whisper level");

        for (int packet = 0; packet < 9; packet++)
        {
            energy.Write(backgroundPacket, backgroundPacket.Length);
            True(
                SpeechAnimationMath.IsAtOrAboveActivityFloor(energy.Decibels, activityFloor),
                "Rolling RMS retains some whisper energy during bounded decay");
        }

        energy.Write(backgroundPacket, backgroundPacket.Length);
        Near(-90d, energy.Decibels, 0.0001d, "Rolling RMS returns to background within 100 ms");
        True(
            !SpeechAnimationMath.IsAtOrAboveActivityFloor(energy.Decibels, activityFloor),
            "Returned background PCM drops below activity floor");
    }

    private static void TestAudibleSpeechActivity()
    {
        AudibleSpeechActivity activity = new AudibleSpeechActivity();
        True(!activity.LastObservationWasAudible, "Cold audible activity has no audible packet");
        True(!activity.WasRecentlyAudible(1d, 1000d), "Cold audible activity is not recent");

        activity.Observe(1d, false);
        True(!activity.LastObservationWasAudible, "Background packet is not audible");
        Near(-1d, activity.LastAudibleSeconds, 0d, "Background packet does not create audible time");

        activity.Observe(2d, true);
        True(activity.LastObservationWasAudible, "Whisper packet is audible");
        Near(2d, activity.LastAudibleSeconds, 0d, "Whisper records audible time");

        activity.Observe(2.1d, false);
        True(!activity.LastObservationWasAudible, "Later background packet is not audible");
        Near(2d, activity.LastAudibleSeconds, 0d, "Background does not refresh audible time");
        True(activity.WasRecentlyAudible(2.5d, 1000d), "Audible grace period remains recent");
        True(!activity.WasRecentlyAudible(3.1d, 1000d), "Background cannot extend audible grace period");

        activity.Observe(double.NaN, true);
        True(!activity.LastObservationWasAudible, "Invalid audible timestamp fails closed");
        Near(2d, activity.LastAudibleSeconds, 0d, "Invalid timestamp preserves last valid audible time");

        activity.Reset();
        True(!activity.LastObservationWasAudible, "Audible activity reset clears packet flag");
        Near(-1d, activity.LastAudibleSeconds, 0d, "Audible activity reset clears timestamp");
    }

    private static void TestDefaultWhisperAdaptationBoundaries()
    {
        RollingSpeechPercentiles percentiles = new RollingSpeechPercentiles();
        for (int i = 0; i < percentiles.Capacity; i++)
            percentiles.Observe(-40d);

        Near(-39.5d, percentiles.LowPercentileDecibels, 0d, "Default range starts at normal low");
        Near(-39.5d, percentiles.HighPercentileDecibels, 0d, "Default range starts at normal high");

        for (int i = 0; i < 11; i++)
            percentiles.Observe(-70d);
        Near(-39.5d, percentiles.LowPercentileDecibels, 0d, "Q20 waits for twelve recent whispers");

        percentiles.Observe(-70d);
        Near(-69.5d, percentiles.LowPercentileDecibels, 0d, "Q20 adapts on twelfth recent whisper");
        Near(-39.5d, percentiles.HighPercentileDecibels, 0d, "Q80 retains normal high initially");

        for (int i = 12; i < 47; i++)
            percentiles.Observe(-70d);
        Near(-39.5d, percentiles.HighPercentileDecibels, 0d, "Q80 waits for forty-eight recent whispers");

        percentiles.Observe(-70d);
        Near(-69.5d, percentiles.HighPercentileDecibels, 0d, "Q80 adapts on forty-eighth recent whisper");
    }

    private static void TestReferenceEquivalenceAcrossWraps()
    {
        const int capacity = 17;
        const int minimumObservations = 5;
        RollingSpeechPercentiles percentiles = new RollingSpeechPercentiles(
            capacity: capacity,
            minimumObservations: minimumObservations,
            lowPercentile: 0.20d,
            highPercentile: 0.80d,
            minimumSpanDecibels: 1d);
        double[] reference = new double[capacity];
        double[] sorted = new double[capacity];
        int writeIndex = 0;
        int count = 0;

        for (int observation = 0; observation < 200; observation++)
        {
            double decibels = -90d + observation * 37 % 80;
            double quantized = decibels + 0.5d;
            percentiles.Observe(decibels);
            reference[writeIndex] = quantized;
            writeIndex = (writeIndex + 1) % capacity;
            if (count < capacity)
                count++;

            if (count < minimumObservations)
                continue;

            Array.Copy(reference, sorted, count);
            Array.Sort(sorted, 0, count);
            int lowIndex = (int)Math.Ceiling(0.20d * count) - 1;
            int highIndex = (int)Math.Ceiling(0.80d * count) - 1;
            Near(sorted[lowIndex], percentiles.LowPercentileDecibels, 0d, "Rolling Q20 reference equivalence");
            Near(sorted[highIndex], percentiles.HighPercentileDecibels, 0d, "Rolling Q80 reference equivalence");
        }
    }

    private static void TestRejectedLevelsAndReset()
    {
        RollingSpeechPercentiles percentiles = new RollingSpeechPercentiles();
        True(!percentiles.Observe(double.NaN), "Speech percentile rejects NaN");
        True(!percentiles.Observe(double.NegativeInfinity), "Speech percentile rejects silence");
        True(!percentiles.Observe(double.PositiveInfinity), "Speech percentile rejects infinity");
        True(!percentiles.Observe(-96.1d), "Speech percentile rejects below floor");
        Equal(0, percentiles.Count, "Rejected levels do not age history");

        True(percentiles.Observe(3d), "Speech percentile accepts clipped finite level");
        Equal(1, percentiles.Count, "Accepted clipped level is counted");
        percentiles.Reset();
        Equal(0, percentiles.Count, "Speech percentile reset clears count");
        True(!percentiles.IsReady, "Speech percentile reset clears readiness");
        True(double.IsNaN(percentiles.AnimationLowDecibels), "Speech percentile reset clears range");
    }

    private static void TestConstructorBounds()
    {
        Throws<ArgumentOutOfRangeException>(
            () => new RollingSpeechPercentiles(binWidthDecibels: 97d),
            "Speech percentile rejects bin wider than domain");
        Throws<ArgumentOutOfRangeException>(
            () => new RollingSpeechPercentiles(lowPercentile: 0.8d, highPercentile: 0.8d),
            "Speech percentile rejects unordered quantiles");
        Throws<ArgumentOutOfRangeException>(
            () => new RollingSpeechPercentiles(capacity: 5, minimumObservations: 6),
            "Speech percentile rejects warmup above capacity");
        Throws<ArgumentOutOfRangeException>(
            () => new SpeechObservationCadence(0),
            "Speech cadence rejects empty window");
    }

    private static void TestPresentationSnapshot()
    {
        VoicePresentationState presentation = new VoicePresentationState();
        True(
            presentation.TryPresent(
                480,
                0.125d,
                -55d,
                -35d,
                -60d,
                -30d,
                10d),
            "Adaptive presentation accepts decoded voice");
        True(presentation.HasAdaptiveRange, "Adaptive presentation carries source range");
        True(presentation.HasPercentileRange, "Adaptive presentation carries raw percentiles");
        Near(-55d, presentation.PercentileLowDecibels, 0d, "Adaptive presentation percentile low");
        Near(-35d, presentation.PercentileHighDecibels, 0d, "Adaptive presentation percentile high");
        Near(-60d, presentation.AnimationLowDecibels, 0d, "Adaptive presentation low");
        Near(-30d, presentation.AnimationHighDecibels, 0d, "Adaptive presentation high");

        True(
            !presentation.TryPresent(0, 0.9d, -85d, -10d, -90d, -5d, 11d),
            "Failed decode does not replace adaptive presentation");
        Near(0.125d, presentation.RootMeanSquare, 0d, "Failed adaptive presentation keeps RMS");
        Near(-55d, presentation.PercentileLowDecibels, 0d, "Failed adaptive presentation keeps percentiles");
        Near(-60d, presentation.AnimationLowDecibels, 0d, "Failed adaptive presentation keeps range");

        presentation.TryPresent(480, 0.25d, -70d, -50d, -75d, -45d, 12d);
        Near(-70d, presentation.PercentileLowDecibels, 0d, "Latest source replaces shared percentile low");
        Near(-50d, presentation.PercentileHighDecibels, 0d, "Latest source replaces shared percentile high");
        Near(-75d, presentation.AnimationLowDecibels, 0d, "Latest source replaces shared low");
        Near(-45d, presentation.AnimationHighDecibels, 0d, "Latest source replaces shared high");

        presentation.TryPresent(480, 0.5d, 13d);
        True(!presentation.HasAdaptiveRange, "Fallback presentation clears old adaptive range");
        True(!presentation.HasPercentileRange, "Fallback presentation clears old percentiles");

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++)
        {
            presentation.TryPresent(480, 0.25d, -70d, -50d, -75d, -45d, i);
            _measurementSink += presentation.PercentileLowDecibels;
            _measurementSink += presentation.AnimationHighDecibels;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Equal(0L, allocated, "Adaptive presentation steady-state allocation");

        presentation.Reset();
        True(!presentation.HasAdaptiveRange, "Presentation reset clears adaptive range");
        True(!presentation.HasPercentileRange, "Presentation reset clears percentiles");
    }

    private static void TestSteadyStateAllocation()
    {
        RollingSpeechPercentiles percentiles = new RollingSpeechPercentiles();
        for (int i = 0; i < percentiles.Capacity; i++)
            percentiles.Observe(-70d + i % 30);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++)
        {
            percentiles.Observe(-80d + i % 50);
            SpeechAnimationDecision decision =
                EvaluateCurrentAnimation(-80d + i % 50, percentiles);
            _measurementSink += percentiles.AnimationLowDecibels;
            _measurementSink += percentiles.AnimationHighDecibels;
            _measurementSink += decision.VolumeRatio;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Equal(0L, allocated, "Speech percentile steady-state allocation");
        GC.KeepAlive(_measurementSink);
    }

    private static RollingSpeechPercentiles CreateConstantRange(double decibels)
    {
        RollingSpeechPercentiles result = new RollingSpeechPercentiles(
            capacity: 10,
            minimumObservations: 10);
        for (int i = 0; i < 10; i++)
            result.Observe(decibels);
        return result;
    }

    private static RollingSpeechPercentiles CreateFilledRange(double decibels)
    {
        RollingSpeechPercentiles result = new RollingSpeechPercentiles();
        for (int observation = 0; observation < result.Capacity; observation++)
            result.Observe(decibels);
        return result;
    }

    private static float[] CreateConstantDecibelPacket(double decibels)
    {
        float amplitude = (float)Math.Pow(10d, decibels / 20d);
        float[] packet = new float[480];
        for (int sample = 0; sample < packet.Length; sample++)
            packet[sample] = amplitude;
        return packet;
    }

    private static bool ObserveIfAboveActivityFloor(
        RollingSpeechPercentiles percentiles,
        double decibels,
        double activityFloor)
    {
        if (!SpeechAnimationMath.IsAtOrAboveActivityFloor(decibels, activityFloor))
            return false;

        percentiles.Observe(decibels);
        return true;
    }

    private static SpeechAnimationDecision EvaluateCurrentAnimation(
        double currentDecibels,
        RollingSpeechPercentiles percentiles)
    {
        return SpeechAnimationMath.Evaluate(
            currentDecibels,
            -80d,
            -30d,
            percentiles.IsReady,
            percentiles.LowPercentileDecibels,
            percentiles.HighPercentileDecibels,
            percentiles.AnimationLowDecibels,
            percentiles.AnimationHighDecibels,
            0.15f);
    }

    private static void WriteSimulationReportIfRequested()
    {
        string outputPath =
            Environment.GetEnvironmentVariable("TALKY_PERCENTILE_SIMULATION_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputPath))
            return;

        string directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using StreamWriter writer = new StreamWriter(outputPath, false);
        writer.WriteLine(
            "scenario,step,seconds,current_db,accepted,observation_count,q20_db,q80_db,animation_low_db,animation_high_db,volume_ratio,head_bob_target,level");
        WriteScenario(writer, "brief-loud-burst", 3, -15d);
        WriteScenario(writer, "sustained-loud", 30, -15d);
        WriteScenario(writer, "sustained-whisper", 60, -70d);
        WriteHotMicScenario(writer);
    }

    private static void WriteScenario(
        TextWriter writer,
        string scenario,
        int transitionObservations,
        double transitionDecibels)
    {
        RollingSpeechPercentiles percentiles = new RollingSpeechPercentiles();
        int step = 0;
        for (int observation = 0; observation < 60; observation++)
            WriteSimulationObservation(writer, scenario, percentiles, ref step, -45d, -80d);
        for (int observation = 0; observation < transitionObservations; observation++)
            WriteSimulationObservation(
                writer,
                scenario,
                percentiles,
                ref step,
                transitionDecibels,
                -80d);
        for (int observation = 0; observation < 90 - transitionObservations; observation++)
            WriteSimulationObservation(writer, scenario, percentiles, ref step, -45d, -80d);
    }

    private static void WriteHotMicScenario(TextWriter writer)
    {
        const string scenario = "hot-mic-then-whisper";
        RollingSpeechPercentiles percentiles = new RollingSpeechPercentiles();
        int step = 0;
        for (int observation = 0; observation < 60; observation++)
            WriteSimulationObservation(writer, scenario, percentiles, ref step, -90d, -80d);
        for (int observation = 0; observation < 60; observation++)
            WriteSimulationObservation(writer, scenario, percentiles, ref step, -70d, -80d);
        for (int observation = 0; observation < 30; observation++)
            WriteSimulationObservation(writer, scenario, percentiles, ref step, -90d, -80d);
    }

    private static void WriteSimulationObservation(
        TextWriter writer,
        string scenario,
        RollingSpeechPercentiles percentiles,
        ref int step,
        double currentDecibels,
        double activityFloor)
    {
        bool accepted = ObserveIfAboveActivityFloor(
            percentiles,
            currentDecibels,
            activityFloor);
        SpeechAnimationDecision decision = EvaluateCurrentAnimation(currentDecibels, percentiles);
        double headBobTarget = decision.VolumeRatio > 0f
            ? (decision.VolumeRatio - 0.25d) * 10d
            : 0d;
        writer.WriteLine(
            string.Join(
                ",",
                scenario,
                step.ToString(CultureInfo.InvariantCulture),
                (step / 10d).ToString("0.0", CultureInfo.InvariantCulture),
                currentDecibels.ToString("0.0", CultureInfo.InvariantCulture),
                accepted ? "true" : "false",
                percentiles.Count.ToString(CultureInfo.InvariantCulture),
                FormatSimulationNumber(percentiles.LowPercentileDecibels),
                FormatSimulationNumber(percentiles.HighPercentileDecibels),
                FormatSimulationNumber(percentiles.AnimationLowDecibels),
                FormatSimulationNumber(percentiles.AnimationHighDecibels),
                decision.VolumeRatio.ToString("0.000000", CultureInfo.InvariantCulture),
                headBobTarget.ToString("0.000000", CultureInfo.InvariantCulture),
                decision.Level.ToString()));
        step++;
    }

    private static string FormatSimulationNumber(double value) =>
        double.IsNaN(value)
            ? string.Empty
            : value.ToString("0.0", CultureInfo.InvariantCulture);

    private static void True(bool condition, string message)
    {
        _assertions++;
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Equal(int expected, int actual, string message)
    {
        _assertions++;
        if (expected != actual)
            throw new InvalidOperationException(message + ": expected " + expected + ", actual " + actual);
    }

    private static void Equal(long expected, long actual, string message)
    {
        _assertions++;
        if (expected != actual)
            throw new InvalidOperationException(message + ": expected " + expected + ", actual " + actual);
    }

    private static void Equal(
        SpeechAnimationLevel expected,
        SpeechAnimationLevel actual,
        string message)
    {
        _assertions++;
        if (expected != actual)
        {
            throw new InvalidOperationException(
                message + ": expected " + expected + ", actual " + actual);
        }
    }

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        _assertions++;
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException(
                message + ": expected " + expected + ", actual " + actual);
        }
    }

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        _assertions++;
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
