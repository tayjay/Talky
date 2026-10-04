using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Talky.Performance;
using Talky.Voice;

namespace Talky.Benchmarks;

internal static class Program
{
    private const int WindowSize = 4800;
    private const int PacketSize = 480;
    private const int TargetGlanceVisitsPerScenario = 20000000;
    private const int TargetSyncPairVisitsPerScenario = 5000000;
    private const int SyncMeasurementSamples = 40;
    private const int BenchmarkFrameRate = 60;
    private const int SyncCallbacksPerPairPerFrame = 2;
    private const int VoicePacketsPerSecond = 100;
    private static readonly int[] TypicalPlayerCounts = { 5, 10, 15, 20, 25, 30, 60, 100 };
    private static readonly int[] SyncActiveTargetCounts = { 0, 1, 8, -1 };
    private static double _sink;
    private static long _workSink;

    private static void Main()
    {
        Console.WriteLine("Talky performance microbenchmark (.NET Framework 4.8, x64)");
        Console.WriteLine("Informational only; run several times on an otherwise idle host.");
        Console.WriteLine();

        float[] packet = CreatePacket();
        RunRmsBenchmark(packet);
        Console.WriteLine();
        RunSpeechPercentileBenchmark();
        Console.WriteLine();
        RunGlanceBenchmark();
        Console.WriteLine();
        RunSyncRewriteKernelBenchmark();
        Console.WriteLine();
        RunProfilerBenchmark();
        GC.KeepAlive(_sink);
        GC.KeepAlive(_workSink);
    }

    private static void RunRmsBenchmark(float[] packet)
    {
        const int measurementSamples = 40;
        const int iterationsPerSample = 5000;
        ScalarRollingVoiceEnergy baselineEnergy = new ScalarRollingVoiceEnergy(WindowSize);
        RollingVoiceEnergy optimizedEnergy = new RollingVoiceEnergy(WindowSize);
        VoicePresentationState baselinePresentation = new VoicePresentationState();
        VoicePresentationState optimizedPresentation = new VoicePresentationState();
        double activityFloorMeanSquare =
            SpeechAnimationMath.MeanSquareFromDecibels(-80d);

        for (int i = 0; i < 1000; i++)
        {
            RunScalarVoiceEnergyPacket(
                baselineEnergy,
                baselinePresentation,
                packet,
                i);
            RunOptimizedVoiceEnergyPacket(
                optimizedEnergy,
                optimizedPresentation,
                packet,
                activityFloorMeanSquare,
                i);
        }
        if (Math.Abs(baselineEnergy.RootMeanSquare - optimizedEnergy.RootMeanSquare) > 1e-8d)
            throw new InvalidOperationException("Voice-energy benchmark kernels are not equivalent.");

        CollectForMeasurement();

        Stopwatch stopwatch = new Stopwatch();
        double[] baselineSamples = new double[measurementSamples];
        double[] optimizedSamples = new double[measurementSamples];
        long baselineAllocated = 0L;
        long optimizedAllocated = 0L;
        for (int sample = 0; sample < measurementSamples; sample++)
        {
            VoiceEnergyMeasurement baseline;
            VoiceEnergyMeasurement optimized;
            if ((sample & 1) == 0)
            {
                baseline = MeasureScalarVoiceEnergy(
                    baselineEnergy,
                    baselinePresentation,
                    packet,
                    iterationsPerSample,
                    stopwatch);
                optimized = MeasureOptimizedVoiceEnergy(
                    optimizedEnergy,
                    optimizedPresentation,
                    packet,
                    activityFloorMeanSquare,
                    iterationsPerSample,
                    stopwatch);
            }
            else
            {
                optimized = MeasureOptimizedVoiceEnergy(
                    optimizedEnergy,
                    optimizedPresentation,
                    packet,
                    activityFloorMeanSquare,
                    iterationsPerSample,
                    stopwatch);
                baseline = MeasureScalarVoiceEnergy(
                    baselineEnergy,
                    baselinePresentation,
                    packet,
                    iterationsPerSample,
                    stopwatch);
            }

            baselineSamples[sample] = baseline.NanosecondsPerPacket;
            optimizedSamples[sample] = optimized.NanosecondsPerPacket;
            baselineAllocated += baseline.AllocatedBytes;
            optimizedAllocated += optimized.AllocatedBytes;
            _sink += baseline.Checksum + optimized.Checksum;
        }

        Array.Sort(baselineSamples);
        Array.Sort(optimizedSamples);
        double baselineMedian = SortedPercentile(baselineSamples, 0.5d);
        double baselineP95 = SortedPercentile(baselineSamples, 0.95d);
        double optimizedMedian = SortedPercentile(optimizedSamples, 0.5d);
        double optimizedP95 = SortedPercentile(optimizedSamples, 0.95d);
        if (baselineAllocated != 0L || optimizedAllocated != 0L)
            throw new InvalidOperationException("Voice-energy kernels allocated during measurement.");

        Console.WriteLine(
            "Voice energy production-cadence kernel: {0} paired samples x {1:N0} packets",
            measurementSamples,
            iterationsPerSample);
        Console.WriteLine(
            "  Scalar baseline   : {0,10:N2} ns median, {1,10:N2} ns p95, {2:N0} B",
            baselineMedian,
            baselineP95,
            baselineAllocated);
        Console.WriteLine(
            "  Block/mean-square : {0,10:N2} ns median, {1,10:N2} ns p95, {2:N0} B",
            optimizedMedian,
            optimizedP95,
            optimizedAllocated);
        Console.WriteLine(
            "  Measured gain     : {0,10:N2}x median, {1,10:N2}x p95",
            baselineMedian / Math.Max(optimizedMedian, double.Epsilon),
            baselineP95 / Math.Max(optimizedP95, double.Epsilon));
        Console.WriteLine(
            "  Scope             : 480-sample write, per-packet presentation/audibility, " +
            "10 Hz calibration read, and modeled 30 Hz face read; Opus excluded");

        RunVoiceEnergyResetBenchmark(packet, measurementSamples, stopwatch);
    }

    private static VoiceEnergyMeasurement MeasureScalarVoiceEnergy(
        ScalarRollingVoiceEnergy energy,
        VoicePresentationState presentation,
        float[] packet,
        int iterations,
        Stopwatch stopwatch)
    {
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        double checksum = 0d;
        stopwatch.Restart();
        for (int i = 0; i < iterations; i++)
            checksum += RunScalarVoiceEnergyPacket(energy, presentation, packet, i);
        stopwatch.Stop();
        return new VoiceEnergyMeasurement(
            stopwatch.Elapsed.TotalMilliseconds * 1000000d / iterations,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
            checksum);
    }

    private static VoiceEnergyMeasurement MeasureOptimizedVoiceEnergy(
        RollingVoiceEnergy energy,
        VoicePresentationState presentation,
        float[] packet,
        double activityFloorMeanSquare,
        int iterations,
        Stopwatch stopwatch)
    {
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        double checksum = 0d;
        stopwatch.Restart();
        for (int i = 0; i < iterations; i++)
        {
            checksum += RunOptimizedVoiceEnergyPacket(
                energy,
                presentation,
                packet,
                activityFloorMeanSquare,
                i);
        }
        stopwatch.Stop();
        return new VoiceEnergyMeasurement(
            stopwatch.Elapsed.TotalMilliseconds * 1000000d / iterations,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
            checksum);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double RunScalarVoiceEnergyPacket(
        ScalarRollingVoiceEnergy energy,
        VoicePresentationState presentation,
        float[] packet,
        int sequence)
    {
        energy.Write(packet, PacketSize);
        double rms = energy.RootMeanSquare;
        presentation.TryPresent(PacketSize, rms, sequence);
        double decibels = energy.Decibels;
        bool audible = SpeechAnimationMath.IsAtOrAboveActivityFloor(decibels, -80d);
        double checksum = audible ? rms : 0d;
        if (sequence % 10 == 9)
            checksum += energy.Decibels;
        if (sequence % 10 < 3)
            checksum += presentation.RootMeanSquare;
        return checksum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double RunOptimizedVoiceEnergyPacket(
        RollingVoiceEnergy energy,
        VoicePresentationState presentation,
        float[] packet,
        double activityFloorMeanSquare,
        int sequence)
    {
        energy.Write(packet, PacketSize);
        double meanSquare = energy.MeanSquare;
        presentation.TryPresentMeanSquare(PacketSize, meanSquare, sequence);
        bool audible = SpeechAnimationMath.IsMeanSquareAtOrAboveActivityFloor(
            meanSquare,
            activityFloorMeanSquare);
        double checksum = audible ? meanSquare : 0d;
        if (sequence % 10 == 9)
            checksum += energy.Decibels;
        if (sequence % 10 < 3)
            checksum += presentation.RootMeanSquare;
        return checksum;
    }

    private static void RunVoiceEnergyResetBenchmark(
        float[] packet,
        int measurementSamples,
        Stopwatch stopwatch)
    {
        const int resetsPerSample = 2000;
        ScalarRollingVoiceEnergy baseline = new ScalarRollingVoiceEnergy(WindowSize);
        RollingVoiceEnergy optimized = new RollingVoiceEnergy(WindowSize);
        double[] baselineSamples = new double[measurementSamples];
        double[] optimizedSamples = new double[measurementSamples];
        for (int sample = 0; sample < measurementSamples; sample++)
        {
            if ((sample & 1) == 0)
            {
                baselineSamples[sample] = MeasureScalarResets(
                    baseline,
                    packet,
                    resetsPerSample,
                    stopwatch);
                optimizedSamples[sample] = MeasureOptimizedResets(
                    optimized,
                    packet,
                    resetsPerSample,
                    stopwatch);
            }
            else
            {
                optimizedSamples[sample] = MeasureOptimizedResets(
                    optimized,
                    packet,
                    resetsPerSample,
                    stopwatch);
                baselineSamples[sample] = MeasureScalarResets(
                    baseline,
                    packet,
                    resetsPerSample,
                    stopwatch);
            }
        }

        Array.Sort(baselineSamples);
        Array.Sort(optimizedSamples);
        Console.WriteLine(
            "  Reset + first frame: scalar {0:N2}/{1:N2} ns med/p95; " +
            "O(1) {2:N2}/{3:N2} ns; gain {4:N2}x/{5:N2}x",
            SortedPercentile(baselineSamples, 0.5d),
            SortedPercentile(baselineSamples, 0.95d),
            SortedPercentile(optimizedSamples, 0.5d),
            SortedPercentile(optimizedSamples, 0.95d),
            SortedPercentile(baselineSamples, 0.5d) /
            Math.Max(SortedPercentile(optimizedSamples, 0.5d), double.Epsilon),
            SortedPercentile(baselineSamples, 0.95d) /
            Math.Max(SortedPercentile(optimizedSamples, 0.95d), double.Epsilon));
    }

    private static double MeasureScalarResets(
        ScalarRollingVoiceEnergy energy,
        float[] packet,
        int iterations,
        Stopwatch stopwatch)
    {
        stopwatch.Restart();
        for (int i = 0; i < iterations; i++)
        {
            energy.Reset();
            energy.Write(packet, PacketSize);
            _sink += energy.MeanSquare;
        }
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds * 1000000d / iterations;
    }

    private static double MeasureOptimizedResets(
        RollingVoiceEnergy energy,
        float[] packet,
        int iterations,
        Stopwatch stopwatch)
    {
        stopwatch.Restart();
        for (int i = 0; i < iterations; i++)
        {
            energy.Reset();
            energy.Write(packet, PacketSize);
            _sink += energy.MeanSquare;
        }
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds * 1000000d / iterations;
    }

    private static void RunSpeechPercentileBenchmark()
    {
        const int observations = 100000;
        RollingSpeechPercentiles percentiles = new RollingSpeechPercentiles();
        for (int i = 0; i < percentiles.Capacity * 2; i++)
            percentiles.Observe(-80d + i % 50);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Stopwatch stopwatch = Stopwatch.StartNew();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < observations; i++)
        {
            percentiles.Observe(-85d + i % 70);
            _sink += percentiles.AnimationLowDecibels;
            _sink += percentiles.AnimationHighDecibels;
        }

        stopwatch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        double microsecondsPerObservation =
            stopwatch.Elapsed.TotalMilliseconds * 1000d / observations;

        Console.WriteLine(
            "Rolling speech percentiles: {0:N0} observations ({1}-sample player window)",
            observations,
            percentiles.Capacity);
        Console.WriteLine(
            "  Total             : {0,10:N2} ms",
            stopwatch.Elapsed.TotalMilliseconds);
        Console.WriteLine(
            "  Average           : {0,10:N3} us/observation",
            microsecondsPerObservation);
        Console.WriteLine(
            "  Steady allocation : {0,10:N0} B",
            allocated);
        Console.WriteLine(
            "  Production cadence: 10 observations/s per continuously speaking player");
    }

    private static void RunGlanceBenchmark()
    {
        int[] activeSpeakers = { 0, 1, 4, 8 };
        const int glanceHz = 20;

        Console.WriteLine("Glance scan benchmark (legacy all-player scan vs recent-speaker index)");
        Console.WriteLine("Each row warms both paths, then executes enough full batches for about 20M legacy visits.");
        Console.WriteLine(
            "Sync model: {0} callbacks per ordered player pair per {1} Hz frame.",
            SyncCallbacksPerPairPerFrame,
            BenchmarkFrameRate);
        Console.WriteLine(
            " players speakers | legacy ms / B | indexed ms / B | speedup | legacy cand/s | indexed cand/s | sync entries/s");
        for (int p = 0; p < TypicalPlayerCounts.Length; p++)
        {
            int players = TypicalPlayerCounts[p];
            for (int s = 0; s < activeSpeakers.Length; s++)
            {
                int speakers = Math.Min(players, activeSpeakers[s]);
                BenchmarkPlayer[] state = CreateGlancePlayers(players, speakers);
                int[] recentIndices = CreateRecentIndices(speakers);
                int legacyVisitsPerBatch = players * (players - 1);
                int repetitions = Math.Max(
                    100,
                    TargetGlanceVisitsPerScenario / Math.Max(1, legacyVisitsPerBatch));

                for (int warmup = 0; warmup < 100; warmup++)
                {
                    ScanResult legacyWarmup = ScanAllPlayers(state);
                    ScanResult indexedWarmup = ScanRecentSpeakers(state, recentIndices);
                    _sink += legacyWarmup.Score + indexedWarmup.Score;
                    _workSink += legacyWarmup.Visits + indexedWarmup.Visits;
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                Stopwatch stopwatch = new Stopwatch();
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                stopwatch.Start();
                for (int iteration = 0; iteration < repetitions; iteration++)
                {
                    ScanResult result = ScanAllPlayers(state);
                    _sink += result.Score;
                    _workSink += result.Visits;
                }
                stopwatch.Stop();
                double legacyMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                long legacyAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

                allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                stopwatch.Restart();
                for (int iteration = 0; iteration < repetitions; iteration++)
                {
                    ScanResult result = ScanRecentSpeakers(state, recentIndices);
                    _sink += result.Score;
                    _workSink += result.Visits;
                }
                stopwatch.Stop();
                double indexedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                long indexedAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

                long legacyCandidates = (long)players * (players - 1) * glanceHz;
                long indexedCandidates = (long)speakers * (players - 1) * glanceHz;
                long syncEntries =
                    (long)SyncCallbacksPerPairPerFrame * BenchmarkFrameRate *
                    players * (players - 1);
                Console.WriteLine(
                    " {0,7} {1,8} | {2,7:N2}/{3,3:N0} | {4,8:N2}/{5,3:N0} | {6,7:N2}x | {7,13:N0} | {8,14:N0} | {9,14:N0}",
                    players,
                    speakers,
                    legacyMilliseconds,
                    legacyAllocated,
                    indexedMilliseconds,
                    indexedAllocated,
                    legacyMilliseconds / Math.Max(indexedMilliseconds, double.Epsilon),
                    legacyCandidates,
                    indexedCandidates,
                    syncEntries);
            }
        }
    }

    private static void RunSyncRewriteKernelBenchmark()
    {
        ValidateSyncRewriteKernels();

        Console.WriteLine("Sync rewrite kernel benchmark (legacy post-write rebuild vs pre-construction look selector)");
        Console.WriteLine(
            "Synthetic allocation-free FpcSyncData analogue; excludes event dispatch, Harmony, dictionaries, Unity, and serialization.");
        Console.WriteLine(
            "Each row warms both paths, then records {0} paired samples totaling about {1:N0} ordered receiver-target visits per path.",
            SyncMeasurementSamples,
            TargetSyncPairVisitsPerScenario);
        Console.WriteLine(
            "Both paths include the game's common construction/cache/buffer write. On active pairs, legacy then resets the cache, reconstructs and caches again, and overwrites the buffer; selector applies the look before the common construction and adds no second record or write.");
        Console.WriteLine(
            " players active | legacy med/p95 ns / B | selector med/p95 ns / B | med/p95 gain | result | rewrites/batch");

        for (int p = 0; p < TypicalPlayerCounts.Length; p++)
        {
            int players = TypicalPlayerCounts[p];
            int orderedPairs = players * (players - 1);
            int repetitionsPerSample = Math.Max(
                10,
                TargetSyncPairVisitsPerScenario /
                Math.Max(1, orderedPairs * SyncMeasurementSamples));

            for (int a = 0; a < SyncActiveTargetCounts.Length; a++)
            {
                int requestedActive = SyncActiveTargetCounts[a];
                int activeTargets = requestedActive < 0
                    ? players
                    : Math.Min(players, requestedActive);
                string activeLabel = requestedActive < 0
                    ? "all"
                    : requestedActive > players
                        ? requestedActive + "(" + activeTargets + ")"
                        : requestedActive.ToString();

                SyncKernelState legacyState = new SyncKernelState(players, activeTargets);
                SyncKernelState selectorState = new SyncKernelState(players, activeTargets);
                for (int warmup = 0; warmup < 100; warmup++)
                {
                    SyncKernelResult legacyWarmup = RunLegacySyncKernel(legacyState);
                    SyncKernelResult selectorWarmup = RunSelectorSyncKernel(selectorState);
                    _workSink += legacyWarmup.Checksum + selectorWarmup.Checksum;
                }

                CollectForMeasurement();

                Stopwatch stopwatch = new Stopwatch();
                double[] legacySamples = new double[SyncMeasurementSamples];
                double[] selectorSamples = new double[SyncMeasurementSamples];
                long legacyChecksum = 0L;
                long legacyRewrites = 0L;
                long selectorChecksum = 0L;
                long selectorRewrites = 0L;
                long legacyAllocated = 0L;
                long selectorAllocated = 0L;
                for (int sample = 0; sample < SyncMeasurementSamples; sample++)
                {
                    SyncKernelMeasurement legacy;
                    SyncKernelMeasurement selector;
                    if ((sample & 1) == 0)
                    {
                        legacy = MeasureLegacySyncKernel(
                            legacyState,
                            repetitionsPerSample,
                            orderedPairs,
                            stopwatch);
                        selector = MeasureSelectorSyncKernel(
                            selectorState,
                            repetitionsPerSample,
                            orderedPairs,
                            stopwatch);
                    }
                    else
                    {
                        selector = MeasureSelectorSyncKernel(
                            selectorState,
                            repetitionsPerSample,
                            orderedPairs,
                            stopwatch);
                        legacy = MeasureLegacySyncKernel(
                            legacyState,
                            repetitionsPerSample,
                            orderedPairs,
                            stopwatch);
                    }

                    legacySamples[sample] = legacy.NanosecondsPerPair;
                    selectorSamples[sample] = selector.NanosecondsPerPair;
                    legacyChecksum += legacy.Checksum;
                    selectorChecksum += selector.Checksum;
                    legacyRewrites += legacy.Rewrites;
                    selectorRewrites += selector.Rewrites;
                    legacyAllocated += legacy.AllocatedBytes;
                    selectorAllocated += selector.AllocatedBytes;
                }

                Array.Sort(legacySamples);
                Array.Sort(selectorSamples);
                double legacyMedian = SortedPercentile(legacySamples, 0.5d);
                double legacyP95 = SortedPercentile(legacySamples, 0.95d);
                double selectorMedian = SortedPercentile(selectorSamples, 0.5d);
                double selectorP95 = SortedPercentile(selectorSamples, 0.95d);

                VerifyLiveLookRestored(legacyState);
                VerifyLiveLookRestored(selectorState);
                long expectedRewritesPerBatch = (long)activeTargets * (players - 1);
                long expectedMeasuredRewrites =
                    expectedRewritesPerBatch * repetitionsPerSample * SyncMeasurementSamples;
                if (legacyRewrites != expectedMeasuredRewrites ||
                    selectorRewrites != expectedMeasuredRewrites)
                {
                    throw new InvalidOperationException(
                        "Sync rewrite benchmark produced an unexpected rewrite count.");
                }
                if (legacyChecksum != selectorChecksum)
                {
                    throw new InvalidOperationException(
                        "Measured sync kernels produced different output checksums.");
                }
                if (legacyAllocated != 0L || selectorAllocated != 0L)
                {
                    throw new InvalidOperationException(
                        "Sync rewrite kernels allocated during steady-state measurement.");
                }

                _workSink += legacyChecksum + selectorChecksum +
                             legacyRewrites + selectorRewrites;
                string comparison = selectorMedian < legacyMedian && selectorP95 < legacyP95
                    ? "faster"
                    : selectorMedian <= legacyMedian * 1.02d && selectorP95 <= legacyP95 * 1.02d
                        ? "noise"
                        : "REGRESSION";
                Console.WriteLine(
                    " {0,7} {1,6} | {2,8:N2}/{3,8:N2}/{4,3:N0} | {5,8:N2}/{6,8:N2}/{7,3:N0} | {8,5:N2}x/{9,5:N2}x | {10,10} | {11,14:N0}",
                    players,
                    activeLabel,
                    legacyMedian,
                    legacyP95,
                    legacyAllocated,
                    selectorMedian,
                    selectorP95,
                    selectorAllocated,
                    legacyMedian / Math.Max(selectorMedian, double.Epsilon),
                    legacyP95 / Math.Max(selectorP95, double.Epsilon),
                    comparison,
                    expectedRewritesPerBatch);
            }
        }
    }

    private static SyncKernelMeasurement MeasureLegacySyncKernel(
        SyncKernelState state,
        int repetitions,
        int orderedPairs,
        Stopwatch stopwatch)
    {
        long checksum = 0L;
        long rewrites = 0L;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Restart();
        for (int iteration = 0; iteration < repetitions; iteration++)
        {
            SyncKernelResult result = RunLegacySyncKernel(state);
            checksum += result.Checksum;
            rewrites += result.Rewrites;
        }
        stopwatch.Stop();
        return new SyncKernelMeasurement(
            stopwatch.Elapsed.TotalMilliseconds * 1000000d /
            ((long)repetitions * orderedPairs),
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
            rewrites,
            checksum);
    }

    private static SyncKernelMeasurement MeasureSelectorSyncKernel(
        SyncKernelState state,
        int repetitions,
        int orderedPairs,
        Stopwatch stopwatch)
    {
        long checksum = 0L;
        long rewrites = 0L;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Restart();
        for (int iteration = 0; iteration < repetitions; iteration++)
        {
            SyncKernelResult result = RunSelectorSyncKernel(state);
            checksum += result.Checksum;
            rewrites += result.Rewrites;
        }
        stopwatch.Stop();
        return new SyncKernelMeasurement(
            stopwatch.Elapsed.TotalMilliseconds * 1000000d /
            ((long)repetitions * orderedPairs),
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
            rewrites,
            checksum);
    }

    private static double SortedPercentile(double[] sorted, double percentile)
    {
        int index = (int)Math.Ceiling(sorted.Length * percentile) - 1;
        return sorted[Math.Max(0, Math.Min(sorted.Length - 1, index))];
    }

    private static void ValidateSyncRewriteKernels()
    {
        SyncKernelState legacy = new SyncKernelState(10, 8);
        SyncKernelState selector = new SyncKernelState(10, 8);
        SyncKernelResult legacyResult = RunLegacySyncKernel(legacy);
        SyncKernelResult selectorResult = RunSelectorSyncKernel(selector);
        if (legacyResult.Rewrites != selectorResult.Rewrites)
            throw new InvalidOperationException("Sync kernels disagree on rewrite count.");

        for (int i = 0; i < legacy.Output.Length; i++)
        {
            if (!legacy.Output[i].FieldsEqual(selector.Output[i]) ||
                !legacy.Previous[i].FieldsEqual(selector.Previous[i]))
            {
                throw new InvalidOperationException(
                    "Legacy rebuild and returned-record selector produced different sync data.");
            }
        }

        VerifyLiveLookRestored(legacy);
        VerifyLiveLookRestored(selector);

        SyncKernelState inactiveLegacy = new SyncKernelState(10, 0);
        SyncKernelState inactiveSelector = new SyncKernelState(10, 0);
        SyncKernelResult inactiveLegacyResult = RunLegacySyncKernel(inactiveLegacy);
        SyncKernelResult inactiveSelectorResult = RunSelectorSyncKernel(inactiveSelector);
        if (inactiveLegacyResult.Rewrites != 0 || inactiveSelectorResult.Rewrites != 0)
            throw new InvalidOperationException("Inactive sync kernel unexpectedly rewrote data.");
        if (inactiveLegacyResult.Checksum != inactiveSelectorResult.Checksum)
            throw new InvalidOperationException("Inactive sync kernels produced different checksums.");
        for (int i = 0; i < inactiveLegacy.Output.Length; i++)
        {
            if (!inactiveLegacy.Output[i].FieldsEqual(inactiveSelector.Output[i]) ||
                !inactiveLegacy.Previous[i].FieldsEqual(inactiveSelector.Previous[i]))
            {
                throw new InvalidOperationException(
                    "Inactive legacy and selector kernels produced different sync data.");
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SyncKernelResult RunLegacySyncKernel(SyncKernelState state)
    {
        int players = state.PlayerCount;
        int rewrites = 0;
        long checksum = 0L;
        for (int receiver = 0; receiver < players; receiver++)
        {
            int pairBase = receiver * players;
            for (int target = 0; target < players; target++)
            {
                if (receiver == target)
                    continue;

                int pairIndex = pairBase + target;
                SyntheticSyncData template = state.Original[pairIndex];
                SyntheticSyncData previous = state.Previous[pairIndex];

                // Model the game's original helper and fanout writes, which happen on both paths.
                SyntheticSyncData common = ConstructSyncData(
                    previous,
                    template,
                    state.LiveHorizontal[target],
                    state.LiveVertical[target]);
                state.Previous[pairIndex] = common;
                state.Output[pairIndex] = common;
                if (!state.HasActiveTargets || !state.ActiveTargets[target])
                {
                    checksum += common.Horizontal + common.Vertical + common.Position;
                    continue;
                }

                // The event path restores the pre-helper cache entry before rebuilding with a
                // temporary live-look mutation. GetNewSyncData then writes the adjusted cache,
                // and the handler overwrites the already-populated fanout buffer.
                state.Previous[pairIndex] = previous;
                int originalHorizontal = state.LiveHorizontal[target];
                int originalVertical = state.LiveVertical[target];
                SyntheticSyncData rewritten;
                try
                {
                    state.LiveHorizontal[target] =
                        originalHorizontal + state.OffsetHorizontal[target];
                    state.LiveVertical[target] =
                        originalVertical + state.OffsetVertical[target];
                    rewritten = ConstructSyncData(
                        state.Previous[pairIndex],
                        template,
                        state.LiveHorizontal[target],
                        state.LiveVertical[target]);
                }
                finally
                {
                    state.LiveHorizontal[target] = originalHorizontal;
                    state.LiveVertical[target] = originalVertical;
                }

                state.Previous[pairIndex] = rewritten;
                state.Output[pairIndex] = rewritten;
                checksum += rewritten.Horizontal + rewritten.Vertical + rewritten.Position;
                rewrites++;
            }
        }

        return new SyncKernelResult(rewrites, checksum);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SyncKernelResult RunSelectorSyncKernel(SyncKernelState state)
    {
        int players = state.PlayerCount;
        int rewrites = 0;
        long checksum = 0L;
        for (int receiver = 0; receiver < players; receiver++)
        {
            int pairBase = receiver * players;
            for (int target = 0; target < players; target++)
            {
                if (receiver == target)
                    continue;

                int pairIndex = pairBase + target;
                SyntheticSyncData template = state.Original[pairIndex];
                SyntheticSyncData previous = state.Previous[pairIndex];
                bool active = state.HasActiveTargets && state.ActiveTargets[target];
                int serializedHorizontal = state.LiveHorizontal[target];
                int serializedVertical = state.LiveVertical[target];
                if (active)
                {
                    serializedHorizontal += state.OffsetHorizontal[target];
                    serializedVertical += state.OffsetVertical[target];
                }

                // Harmony selects the presentation-only look before the game's one normal
                // construction/cache write/buffer write, so Talky adds no second record write.
                SyntheticSyncData serialized = ConstructSyncData(
                    previous,
                    template,
                    serializedHorizontal,
                    serializedVertical);
                state.Previous[pairIndex] = serialized;
                state.Output[pairIndex] = serialized;
                checksum += serialized.Horizontal + serialized.Vertical + serialized.Position;
                if (active)
                    rewrites++;
            }
        }

        return new SyncKernelResult(rewrites, checksum);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static SyntheticSyncData ConstructSyncData(
        SyntheticSyncData previous,
        SyntheticSyncData original,
        int liveHorizontal,
        int liveVertical)
    {
        int state = previous.State + (original.State - previous.State);
        int flags = previous.Flags + (original.Flags - previous.Flags);
        int position = previous.Position + (original.Position - previous.Position);
        int horizontal = previous.Horizontal + (liveHorizontal - previous.Horizontal);
        int vertical = previous.Vertical + (liveVertical - previous.Vertical);
        return new SyntheticSyncData(
            state,
            flags,
            position,
            horizontal,
            vertical,
            original.Sequence);
    }

    private static void VerifyLiveLookRestored(SyncKernelState state)
    {
        for (int i = 0; i < state.PlayerCount; i++)
        {
            if (state.LiveHorizontal[i] != state.BaselineHorizontal[i] ||
                state.LiveVertical[i] != state.BaselineVertical[i])
            {
                throw new InvalidOperationException(
                    "Legacy sync kernel failed to restore live look state.");
            }
        }
    }

    private static void CollectForMeasurement()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void RunProfilerBenchmark()
    {
        const int iterations = 1000000;
        RunVoiceMonitoringBenchmark(iterations);
        Console.WriteLine();
        RunFrameMonitoringBenchmark(iterations);
        Console.WriteLine();

        PerformanceProfiler profiler = new PerformanceProfiler();
        Stopwatch stopwatch = new Stopwatch();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Start();
        for (int i = 0; i < iterations; i++)
        {
            long started = profiler.BeginSample(PerformanceMetric.DirectRoleSync, 64);
            profiler.EndSample(PerformanceMetric.DirectRoleSync, started);
            profiler.Increment(Talky.Performance.PerformanceCounter.SyncEntries);
        }
        stopwatch.Stop();
        double directTotalMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        double microsecondsPerCall = directTotalMilliseconds * 1000d / iterations;

        PerformanceProfiler cedModProfiler = new PerformanceProfiler();
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Restart();
        for (int i = 0; i < iterations; i++)
        {
            long prefix = cedModProfiler.BeginSample(PerformanceMetric.CedModRoleSync, 64);
            cedModProfiler.Increment(Talky.Performance.PerformanceCounter.SyncEntries);
            cedModProfiler.EndSample(PerformanceMetric.CedModRoleSync, prefix);
            long postfix = cedModProfiler.BeginSample(PerformanceMetric.CedModPostfix, 64);
            cedModProfiler.EndSample(PerformanceMetric.CedModPostfix, postfix);
        }
        stopwatch.Stop();
        long cedModAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        double cedModMicrosecondsPerPair = stopwatch.Elapsed.TotalMilliseconds * 1000d / iterations;

        Console.WriteLine("Profiler: {0:N0} sampled callback observations", iterations);
        Console.WriteLine("  Direct total     : {0,10:N2} ms", directTotalMilliseconds);
        Console.WriteLine("  Average           : {0,10:N3} us/call", microsecondsPerCall);
        Console.WriteLine("  Steady allocation : {0,10:N0} B", allocated);
        Console.WriteLine(
            "  Estimated callback-sampling overhead at {0} Hz " +
            "({1} callbacks/ordered pair/frame):",
            BenchmarkFrameRate,
            SyncCallbacksPerPairPerFrame);
        Console.WriteLine("   players | direct ms/frame | CedMod prefix+postfix ms/frame");
        for (int i = 0; i < TypicalPlayerCounts.Length; i++)
        {
            WriteProfilerOverhead(
                TypicalPlayerCounts[i],
                microsecondsPerCall,
                cedModMicrosecondsPerPair);
        }

        Console.WriteLine(
            "  CedMod pair allocation: {0:N0} B; average {1:N3} us/pair",
            cedModAllocated,
            cedModMicrosecondsPerPair);
    }

    private static void WriteProfilerOverhead(
        int players,
        double directMicrosecondsPerCall,
        double cedModMicrosecondsPerPair)
    {
        long callbacksPerFrame =
            (long)players * (players - 1) * SyncCallbacksPerPairPerFrame;
        double directMillisecondsPerFrame =
            directMicrosecondsPerCall * callbacksPerFrame / 1000d;
        double cedModMillisecondsPerFrame =
            cedModMicrosecondsPerPair * callbacksPerFrame / 1000d;
        Console.WriteLine(
            "   {0,7} | {1,15:N4} | {2,32:N4}",
            players,
            directMillisecondsPerFrame,
            cedModMillisecondsPerFrame);
    }

    private static void RunVoiceMonitoringBenchmark(int iterations)
    {
        PerformanceProfiler profiler = new PerformanceProfiler(60, 60);
        for (int i = 0; i < 10000; i++)
            RecordVoiceMonitoring(profiler);
        profiler.Reset();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Stopwatch stopwatch = new Stopwatch();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Start();
        for (int i = 0; i < iterations; i++)
            RecordVoiceMonitoring(profiler);
        stopwatch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        double nanosecondsPerPacket =
            stopwatch.Elapsed.TotalMilliseconds * 1000000d / iterations;
        double millisecondsPerFramePerSpeaker =
            nanosecondsPerPacket * VoicePacketsPerSecond /
            BenchmarkFrameRate / 1000000d;

        Console.WriteLine(
            "Voice monitoring core: {0:N0} packets, production sampling 1/{1}",
            iterations,
            PerformanceMetricCatalog.VoiceSampleEvery);
        Console.WriteLine(
            "  Average           : {0,10:N2} ns/packet",
            nanosecondsPerPacket);
        Console.WriteLine(
            "  At {0} packets/s  : {1,10:N6} ms/frame/speaker at {2} Hz",
            VoicePacketsPerSecond,
            millisecondsPerFramePerSpeaker,
            BenchmarkFrameRate);
        Console.WriteLine(
            "  Steady allocation : {0,10:N0} B",
            allocated);
        Console.WriteLine(
            "  Scope              : PerformanceProfiler calls and counters; " +
            "adapter/singleton access excluded");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RecordVoiceMonitoring(PerformanceProfiler profiler)
    {
        long voiceStarted = profiler.BeginSample(
            PerformanceMetric.VoicePacket,
            PerformanceMetricCatalog.VoiceSampleEvery);
        profiler.Increment(Talky.Performance.PerformanceCounter.VoicePackets);

        long decodeStarted = profiler.BeginSample(
            PerformanceMetric.OpusDecode,
            PerformanceMetricCatalog.VoiceSampleEvery);
        profiler.EndSample(PerformanceMetric.OpusDecode, decodeStarted);

        long energyStarted = profiler.BeginSample(
            PerformanceMetric.EnergyUpdate,
            PerformanceMetricCatalog.VoiceSampleEvery);
        profiler.EndSample(PerformanceMetric.EnergyUpdate, energyStarted);

        profiler.Increment(
            Talky.Performance.PerformanceCounter.DecodedSamples,
            PacketSize);
        profiler.EndSample(PerformanceMetric.VoicePacket, voiceStarted);
    }

    private static void RunFrameMonitoringBenchmark(int iterations)
    {
        PerformanceProfiler profiler = new PerformanceProfiler(60, 60);
        PerformanceSummaryAccumulator summary = new PerformanceSummaryAccumulator();
        for (int i = 0; i < 10000; i++)
            RecordFrameMonitoring(profiler, summary);
        profiler.Reset();
        summary.Reset(0d);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Stopwatch stopwatch = new Stopwatch();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Start();
        for (int i = 0; i < iterations; i++)
            RecordFrameMonitoring(profiler, summary);
        stopwatch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        double nanosecondsPerFrame =
            stopwatch.Elapsed.TotalMilliseconds * 1000000d / iterations;

        Console.WriteLine(
            "Frame-monitor core: {0:N0} eligible steady-state frames",
            iterations);
        Console.WriteLine(
            "  Average           : {0,10:N2} ns/frame ({1:N6} ms/frame)",
            nanosecondsPerFrame,
            nanosecondsPerFrame / 1000000d);
        Console.WriteLine(
            "  Steady allocation : {0,10:N0} B",
            allocated);
        Console.WriteLine(
            "  Scope              : frame counters/histogram, summary population, " +
            "window check, and self-timing");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RecordFrameMonitoring(
        PerformanceProfiler profiler,
        PerformanceSummaryAccumulator summary)
    {
        _sink += Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        long controllerStarted = Stopwatch.GetTimestamp();

        profiler.Increment(Talky.Performance.PerformanceCounter.TrackedPlayers, 30);
        profiler.Increment(Talky.Performance.PerformanceCounter.RecentSpeakers, 4);
        profiler.Increment(Talky.Performance.PerformanceCounter.ActiveLookTargets, 8);
        profiler.RecordFrameMilliseconds(1000d / BenchmarkFrameRate);
        summary.RecordEligibleFrame(
            30,
            4,
            8,
            1000d / BenchmarkFrameRate,
            BenchmarkFrameRate,
            true);

        profiler.TryCloseWindow(Stopwatch.GetTimestamp(), out _);
        long controllerTicks = Stopwatch.GetTimestamp() - controllerStarted;
        if (controllerTicks > 0L)
        {
            profiler.RecordDurationTicks(
                PerformanceMetric.ProfilerOverhead,
                controllerTicks);
        }
    }

    private static float[] CreatePacket()
    {
        float[] result = new float[PacketSize];
        Random random = new Random(42);
        for (int i = 0; i < result.Length; i++)
            result[i] = (float)(random.NextDouble() * 0.2d - 0.1d);
        return result;
    }

    private static BenchmarkPlayer[] CreateGlancePlayers(int playerCount, int activeSpeakers)
    {
        BenchmarkPlayer[] result = new BenchmarkPlayer[playerCount];
        Random random = new Random(playerCount * 397 ^ activeSpeakers * 7919);
        for (int i = 0; i < result.Length; i++)
        {
            float x = (float)(random.NextDouble() * 16d - 8d);
            float y = (float)(random.NextDouble() * 3d - 1.5d);
            float z = (float)(random.NextDouble() * 16d - 8d);
            result[i] = new BenchmarkPlayer(
                x,
                y,
                z,
                x,
                y + 1.6f,
                z,
                i < activeSpeakers);
        }

        return result;
    }

    private static int[] CreateRecentIndices(int activeSpeakers)
    {
        int[] result = new int[activeSpeakers];
        for (int i = 0; i < result.Length; i++)
            result[i] = i;
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ScanResult ScanAllPlayers(BenchmarkPlayer[] players)
    {
        long visits = 0L;
        double scoreSum = 0d;
        for (int viewer = 0; viewer < players.Length; viewer++)
        {
            float bestScore = float.MinValue;
            for (int candidate = 0; candidate < players.Length; candidate++)
            {
                if (candidate == viewer)
                    continue;

                visits++;
                if (!players[candidate].Recent)
                    continue;

                float score;
                if (TryScore(players[viewer], players[candidate], out score) && score > bestScore)
                    bestScore = score;
            }

            if (bestScore != float.MinValue)
                scoreSum += bestScore;
        }

        return new ScanResult(visits, scoreSum);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ScanResult ScanRecentSpeakers(BenchmarkPlayer[] players, int[] recentIndices)
    {
        long visits = 0L;
        double scoreSum = 0d;
        for (int viewer = 0; viewer < players.Length; viewer++)
        {
            float bestScore = float.MinValue;
            for (int i = 0; i < recentIndices.Length; i++)
            {
                int candidate = recentIndices[i];
                if (candidate == viewer)
                    continue;

                visits++;
                float score;
                if (TryScore(players[viewer], players[candidate], out score) && score > bestScore)
                    bestScore = score;
            }

            if (bestScore != float.MinValue)
                scoreSum += bestScore;
        }

        return new ScanResult(visits, scoreSum);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryScore(BenchmarkPlayer viewer, BenchmarkPlayer candidate, out float score)
    {
        const float maxDistance = 7f;
        const float maxDistanceSquared = maxDistance * maxDistance;
        const float minimumFrontDot = 0.3420201433f; // cos(70 degrees), matching a 140-degree FOV.

        float positionX = candidate.PositionX - viewer.PositionX;
        float positionY = candidate.PositionY - viewer.PositionY;
        float positionZ = candidate.PositionZ - viewer.PositionZ;
        float distanceSquared =
            positionX * positionX + positionY * positionY + positionZ * positionZ;
        if (distanceSquared > maxDistanceSquared)
        {
            score = 0f;
            return false;
        }

        float cameraX = candidate.CameraX - viewer.CameraX;
        float cameraY = candidate.CameraY - viewer.CameraY;
        float cameraZ = candidate.CameraZ - viewer.CameraZ;
        float cameraDistanceSquared = cameraX * cameraX + cameraY * cameraY + cameraZ * cameraZ;
        if (cameraDistanceSquared <= 0.000001f)
        {
            score = 0f;
            return false;
        }

        float inverseDistance = 1f / (float)Math.Sqrt(cameraDistanceSquared);
        float frontDot = cameraZ * inverseDistance;
        if (frontDot < minimumFrontDot)
        {
            score = 0f;
            return false;
        }

        score = 0.8f * frontDot - 0.2f * ((float)Math.Sqrt(distanceSquared) / maxDistance);
        return true;
    }

    private sealed class SyncKernelState
    {
        public SyncKernelState(int playerCount, int activeTargetCount)
        {
            PlayerCount = playerCount;
            HasActiveTargets = activeTargetCount > 0;
            ActiveTargets = new bool[playerCount];
            BaselineHorizontal = new int[playerCount];
            BaselineVertical = new int[playerCount];
            LiveHorizontal = new int[playerCount];
            LiveVertical = new int[playerCount];
            OffsetHorizontal = new int[playerCount];
            OffsetVertical = new int[playerCount];

            for (int target = 0; target < playerCount; target++)
            {
                ActiveTargets[target] = target < activeTargetCount;
                BaselineHorizontal[target] = 1000 + target * 13;
                BaselineVertical[target] = -500 + target * 7;
                LiveHorizontal[target] = BaselineHorizontal[target];
                LiveVertical[target] = BaselineVertical[target];
                OffsetHorizontal[target] = 1 + target % 5;
                OffsetVertical[target] = -1 - target % 3;
            }

            int pairCount = playerCount * playerCount;
            Original = new SyntheticSyncData[pairCount];
            Previous = new SyntheticSyncData[pairCount];
            Output = new SyntheticSyncData[pairCount];
            for (int receiver = 0; receiver < playerCount; receiver++)
            {
                int pairBase = receiver * playerCount;
                for (int target = 0; target < playerCount; target++)
                {
                    int pairIndex = pairBase + target;
                    SyntheticSyncData original = new SyntheticSyncData(
                        (receiver + target) & 3,
                        (receiver * 17 + target * 31) & 7,
                        receiver * 10000 + target * 101,
                        BaselineHorizontal[target],
                        BaselineVertical[target],
                        unchecked((uint)(pairIndex + 1)));
                    Original[pairIndex] = original;
                    Previous[pairIndex] = new SyntheticSyncData(
                        original.State - 1,
                        original.Flags ^ 1,
                        original.Position - 3,
                        original.Horizontal - 2,
                        original.Vertical + 2,
                        original.Sequence - 1u);
                    Output[pairIndex] = original;
                }
            }
        }

        public readonly int PlayerCount;
        public readonly bool HasActiveTargets;
        public readonly bool[] ActiveTargets;
        public readonly int[] BaselineHorizontal;
        public readonly int[] BaselineVertical;
        public readonly int[] LiveHorizontal;
        public readonly int[] LiveVertical;
        public readonly int[] OffsetHorizontal;
        public readonly int[] OffsetVertical;
        public readonly SyntheticSyncData[] Original;
        public readonly SyntheticSyncData[] Previous;
        public readonly SyntheticSyncData[] Output;
    }

    private readonly struct SyntheticSyncData
    {
        public SyntheticSyncData(
            int state,
            int flags,
            int position,
            int horizontal,
            int vertical,
            uint sequence)
        {
            State = state;
            Flags = flags;
            Position = position;
            Horizontal = horizontal;
            Vertical = vertical;
            Sequence = sequence;
        }

        public int State { get; }
        public int Flags { get; }
        public int Position { get; }
        public int Horizontal { get; }
        public int Vertical { get; }
        public uint Sequence { get; }

        public bool FieldsEqual(SyntheticSyncData other)
        {
            return State == other.State &&
                   Flags == other.Flags &&
                   Position == other.Position &&
                   Horizontal == other.Horizontal &&
                   Vertical == other.Vertical &&
                   Sequence == other.Sequence;
        }
    }

    private readonly struct SyncKernelResult
    {
        public SyncKernelResult(int rewrites, long checksum)
        {
            Rewrites = rewrites;
            Checksum = checksum;
        }

        public int Rewrites { get; }
        public long Checksum { get; }
    }

    private readonly struct SyncKernelMeasurement
    {
        public SyncKernelMeasurement(
            double nanosecondsPerPair,
            long allocatedBytes,
            long rewrites,
            long checksum)
        {
            NanosecondsPerPair = nanosecondsPerPair;
            AllocatedBytes = allocatedBytes;
            Rewrites = rewrites;
            Checksum = checksum;
        }

        public double NanosecondsPerPair { get; }
        public long AllocatedBytes { get; }
        public long Rewrites { get; }
        public long Checksum { get; }
    }

    private sealed class ScalarRollingVoiceEnergy
    {
        private readonly float[] _squaredSamples;
        private int _writeIndex;
        private double _sumSquares;

        public ScalarRollingVoiceEnergy(int capacity)
        {
            _squaredSamples = new float[capacity];
        }

        public double MeanSquare =>
            (_sumSquares > 0d ? _sumSquares : 0d) / _squaredSamples.Length;
        public double RootMeanSquare => Math.Sqrt(MeanSquare);
        public double Decibels
        {
            get
            {
                double rms = RootMeanSquare;
                return rms > 0d
                    ? 20d * Math.Log10(rms)
                    : double.NegativeInfinity;
            }
        }

        public void Write(float[] samples, int count)
        {
            double sum = _sumSquares;
            for (int i = 0; i < count; i++)
            {
                float square = samples[i] * samples[i];
                sum += square - _squaredSamples[_writeIndex];
                _squaredSamples[_writeIndex] = square;
                _writeIndex++;
                if (_writeIndex == _squaredSamples.Length)
                    _writeIndex = 0;
            }

            _sumSquares = sum > 0d ? sum : 0d;
        }

        public void Reset()
        {
            Array.Clear(_squaredSamples, 0, _squaredSamples.Length);
            _writeIndex = 0;
            _sumSquares = 0d;
        }
    }

    private readonly struct VoiceEnergyMeasurement
    {
        public VoiceEnergyMeasurement(
            double nanosecondsPerPacket,
            long allocatedBytes,
            double checksum)
        {
            NanosecondsPerPacket = nanosecondsPerPacket;
            AllocatedBytes = allocatedBytes;
            Checksum = checksum;
        }

        public double NanosecondsPerPacket { get; }
        public long AllocatedBytes { get; }
        public double Checksum { get; }
    }

    private readonly struct BenchmarkPlayer
    {
        public BenchmarkPlayer(
            float positionX,
            float positionY,
            float positionZ,
            float cameraX,
            float cameraY,
            float cameraZ,
            bool recent)
        {
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            CameraX = cameraX;
            CameraY = cameraY;
            CameraZ = cameraZ;
            Recent = recent;
        }

        public float PositionX { get; }
        public float PositionY { get; }
        public float PositionZ { get; }
        public float CameraX { get; }
        public float CameraY { get; }
        public float CameraZ { get; }
        public bool Recent { get; }
    }

    private readonly struct ScanResult
    {
        public ScanResult(long visits, double score)
        {
            Visits = visits;
            Score = score;
        }

        public long Visits { get; }
        public double Score { get; }
    }
}
