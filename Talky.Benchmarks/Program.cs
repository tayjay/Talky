using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Talky.Performance;

namespace Talky.Benchmarks;

internal static class Program
{
    private const int WindowSize = 4800;
    private const int PacketSize = 480;
    private const int PacketIterations = 10000;
    private const int TargetGlanceVisitsPerScenario = 20000000;
    private static readonly int[] TypicalPlayerCounts = { 5, 10, 15, 20, 25, 30 };
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
        RunGlanceBenchmark();
        Console.WriteLine();
        RunProfilerBenchmark();
        GC.KeepAlive(_sink);
        GC.KeepAlive(_workSink);
    }

    private static void RunRmsBenchmark(float[] packet)
    {
        LegacyVoiceEnergy legacy = new LegacyVoiceEnergy(WindowSize);
        RollingVoiceEnergy rolling = new RollingVoiceEnergy(WindowSize);

        for (int i = 0; i < 100; i++)
        {
            legacy.WriteAndMeasure(packet, PacketSize);
            rolling.Write(packet, PacketSize);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Stopwatch stopwatch = new Stopwatch();
        long legacyAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Start();
        for (int i = 0; i < PacketIterations; i++)
            _sink += legacy.WriteAndMeasure(packet, PacketSize);
        stopwatch.Stop();
        long legacyAllocated = GC.GetAllocatedBytesForCurrentThread() - legacyAllocatedBefore;
        double legacyMs = stopwatch.Elapsed.TotalMilliseconds;

        long rollingAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        stopwatch.Restart();
        for (int i = 0; i < PacketIterations; i++)
        {
            rolling.Write(packet, PacketSize);
            _sink += rolling.RootMeanSquare;
        }
        stopwatch.Stop();
        long rollingAllocated = GC.GetAllocatedBytesForCurrentThread() - rollingAllocatedBefore;
        double rollingMs = stopwatch.Elapsed.TotalMilliseconds;

        Console.WriteLine("RMS: {0:N0} packets x {1:N0} samples, {2:N0}-sample window", PacketIterations, PacketSize, WindowSize);
        Console.WriteLine("  Legacy full scan : {0,10:N2} ms, {1,8:N0} B allocated", legacyMs, legacyAllocated);
        Console.WriteLine("  Rolling sum      : {0,10:N2} ms, {1,8:N0} B allocated", rollingMs, rollingAllocated);
        Console.WriteLine("  Measured speedup : {0,10:N2}x", legacyMs / Math.Max(rollingMs, double.Epsilon));
        Console.WriteLine("  Typical continuous speech: legacy 144,000 squares/s at 30 evaluations/s;");
        Console.WriteLine("                             rolling 48,000 squares/s at 100 x 480-sample packets/s");
    }

    private static void RunGlanceBenchmark()
    {
        int[] activeSpeakers = { 0, 1, 4, 8 };
        const int glanceHz = 20;
        const int syncHz = 60;

        Console.WriteLine("Glance scan benchmark (legacy all-player scan vs recent-speaker index)");
        Console.WriteLine("Each row warms both paths, then executes enough full batches for about 20M legacy visits.");
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
                long syncEntries = 2L * syncHz * players * (players - 1);
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

    private static void RunProfilerBenchmark()
    {
        const int iterations = 1000000;
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
        Console.WriteLine("  Estimated callback-sampling overhead at 60Hz:");
        Console.WriteLine("   players | direct ms/frame | CedMod prefix+postfix ms/frame");
        for (int i = 0; i < TypicalPlayerCounts.Length; i++)
        {
            WriteProfilerOverhead(
                TypicalPlayerCounts[i],
                microsecondsPerCall,
                cedModMicrosecondsPerPair);
        }

        Console.WriteLine("  Original stress-ceiling check:");
        WriteProfilerOverhead(100, microsecondsPerCall, cedModMicrosecondsPerPair);
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
        int pairs = players * (players - 1);
        double directMillisecondsPerFrame = directMicrosecondsPerCall * pairs / 1000d;
        double cedModMillisecondsPerFrame = cedModMicrosecondsPerPair * pairs / 1000d;
        Console.WriteLine(
            "   {0,7} | {1,15:N4} | {2,32:N4}",
            players,
            directMillisecondsPerFrame,
            cedModMillisecondsPerFrame);
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

    private sealed class LegacyVoiceEnergy
    {
        private readonly float[] _window;
        private int _writeIndex;

        public LegacyVoiceEnergy(int capacity)
        {
            _window = new float[capacity];
        }

        public double WriteAndMeasure(float[] samples, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _window[_writeIndex] = samples[i];
                _writeIndex++;
                if (_writeIndex == _window.Length)
                    _writeIndex = 0;
            }

            double sum = 0d;
            for (int i = 0; i < _window.Length; i++)
                sum += _window[i] * _window[i];
            return Math.Sqrt(sum / _window.Length);
        }
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
