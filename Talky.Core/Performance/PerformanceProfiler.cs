using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Talky.Performance;

public readonly struct MetricSnapshot
{
    public const int ReliablePercentileSampleCount = 100;
    internal MetricSnapshot(
        long callCount,
        long sampleCount,
        double estimatedTotalMilliseconds,
        double averageMilliseconds,
        double p50Milliseconds,
        double p95Milliseconds,
        double p99Milliseconds,
        double maxMilliseconds)
    {
        CallCount = callCount;
        SampleCount = sampleCount;
        EstimatedTotalMilliseconds = estimatedTotalMilliseconds;
        AverageMilliseconds = averageMilliseconds;
        P50Milliseconds = p50Milliseconds;
        P95Milliseconds = p95Milliseconds;
        P99Milliseconds = p99Milliseconds;
        MaxMilliseconds = maxMilliseconds;
    }

    public long CallCount { get; }
    public long SampleCount { get; }
    public double EstimatedTotalMilliseconds { get; }
    public double AverageMilliseconds { get; }
    public double P50Milliseconds { get; }
    public double P95Milliseconds { get; }
    public double P99Milliseconds { get; }
    public double MaxMilliseconds { get; }
    public bool PercentilesLowConfidence => SampleCount < ReliablePercentileSampleCount;
}

/// <summary>
/// Exposes a completed profiler window backed by a reusable history slot.
/// </summary>
public sealed class PerformanceWindow
{
    internal const int HistogramBucketCount = 64;
    internal const int FrameHistogramBucketCount = 640;

    private readonly long[] _metricCalls = new long[(int)PerformanceMetric.Count];
    private readonly long[] _metricSamples = new long[(int)PerformanceMetric.Count];
    private readonly long[] _metricTicks = new long[(int)PerformanceMetric.Count];
    private readonly long[] _metricMaximumTicks = new long[(int)PerformanceMetric.Count];
    private readonly long[] _metricHistogram =
        new long[(int)PerformanceMetric.Count * HistogramBucketCount];
    private readonly long[] _counters = new long[(int)PerformanceCounter.Count];
    private readonly long[] _frameHistogram = new long[FrameHistogramBucketCount];

    private readonly long _frequency;
    private long _frameTicks;
    private long _frameMaximumTicks;

    internal PerformanceWindow(long frequency)
    {
        _frequency = frequency;
    }

    public long StartTimestamp { get; private set; }
    public long EndTimestamp { get; private set; }
    public long FrameCount { get; private set; }
    public double DurationSeconds =>
        EndTimestamp > StartTimestamp ? (EndTimestamp - StartTimestamp) / (double)_frequency : 0d;
    public double FrameAverageMilliseconds =>
        FrameCount == 0 ? 0d : TicksToMilliseconds(_frameTicks / (double)FrameCount);
    public double FrameP50Milliseconds => FramePercentile(0.50d);
    public double FrameP95Milliseconds => FramePercentile(0.95d);
    public double FrameP99Milliseconds => FramePercentile(0.99d);
    public double FrameMaxMilliseconds => TicksToMilliseconds(_frameMaximumTicks);

    public double TotalTalkyMilliseconds
    {
        get
        {
            double total = 0d;
            for (int i = 0; i < (int)PerformanceMetric.Count; i++)
            {
                PerformanceMetric metric = (PerformanceMetric)i;
                if (CountsTowardTalkyBudget(metric))
                    total += GetMetric(metric).EstimatedTotalMilliseconds;
            }

            return total;
        }
    }

    public double TalkyMillisecondsPerFrame =>
        FrameCount == 0 ? 0d : TotalTalkyMilliseconds / FrameCount;

    public double TalkyMillisecondsPerSecond =>
        DurationSeconds <= 0d ? 0d : TotalTalkyMilliseconds / DurationSeconds;

    public MetricSnapshot GetMetric(PerformanceMetric metric)
    {
        int index = MetricIndex(metric);
        long calls = _metricCalls[index];
        long samples = _metricSamples[index];
        long ticks = _metricTicks[index];
        double estimatedTicks = samples == 0 ? 0d : ticks * (calls / (double)samples);
        double averageTicks = samples == 0 ? 0d : ticks / (double)samples;

        return new MetricSnapshot(
            calls,
            samples,
            TicksToMilliseconds(estimatedTicks),
            TicksToMilliseconds(averageTicks),
            MetricPercentileMilliseconds(index, samples, 0.50d),
            MetricPercentileMilliseconds(index, samples, 0.95d),
            MetricPercentileMilliseconds(index, samples, 0.99d),
            TicksToMilliseconds(_metricMaximumTicks[index]));
    }

    public long GetCounter(PerformanceCounter counter)
    {
        return _counters[CounterIndex(counter)];
    }

    public static bool CountsTowardTalkyBudget(PerformanceMetric metric)
    {
        int index = (int)metric;
        if (index < 0 || index >= (int)PerformanceMetric.Count)
            return false;

        return PerformanceMetricCatalog.CountsTowardTalkyBudget(metric);
    }

    internal void Clear(long startTimestamp)
    {
        Array.Clear(_metricCalls, 0, _metricCalls.Length);
        Array.Clear(_metricSamples, 0, _metricSamples.Length);
        Array.Clear(_metricTicks, 0, _metricTicks.Length);
        Array.Clear(_metricMaximumTicks, 0, _metricMaximumTicks.Length);
        Array.Clear(_metricHistogram, 0, _metricHistogram.Length);
        Array.Clear(_counters, 0, _counters.Length);
        Array.Clear(_frameHistogram, 0, _frameHistogram.Length);
        StartTimestamp = startTimestamp;
        EndTimestamp = 0;
        FrameCount = 0;
        _frameTicks = 0;
        _frameMaximumTicks = 0;
    }

    internal void Close(long endTimestamp)
    {
        EndTimestamp = endTimestamp;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long IncrementMetricCall(int metricIndex)
    {
        return ++_metricCalls[metricIndex];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AddMetricSample(int metricIndex, long elapsedTicks)
    {
        if (elapsedTicks < 0)
            elapsedTicks = 0;

        _metricSamples[metricIndex]++;
        _metricTicks[metricIndex] += elapsedTicks;
        if (elapsedTicks > _metricMaximumTicks[metricIndex])
            _metricMaximumTicks[metricIndex] = elapsedTicks;

        int bucket = DurationHistogram.BucketForTicks(elapsedTicks, _frequency);
        _metricHistogram[metricIndex * HistogramBucketCount + bucket]++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void IncrementCounter(int counterIndex, long amount)
    {
        _counters[counterIndex] += amount;
    }

    internal void AddFrameTicks(long elapsedTicks)
    {
        if (elapsedTicks < 0)
            return;

        FrameCount++;
        _frameTicks += elapsedTicks;
        if (elapsedTicks > _frameMaximumTicks)
            _frameMaximumTicks = elapsedTicks;
        _frameHistogram[FrameDurationHistogram.BucketForTicks(elapsedTicks, _frequency)]++;
    }

    internal void AccumulateFrameHistogram(long[] destination)
    {
        for (int i = 0; i < FrameHistogramBucketCount; i++)
            destination[i] += _frameHistogram[i];
    }

    internal void AccumulateMetricHistogram(int metricIndex, long[] destination)
    {
        if (destination == null)
            throw new ArgumentNullException(nameof(destination));
        if (destination.Length < (int)PerformanceMetric.Count * HistogramBucketCount)
            throw new ArgumentException("Metric histogram destination is too small.", nameof(destination));

        int baseIndex = metricIndex * HistogramBucketCount;
        for (int bucket = 0; bucket < HistogramBucketCount; bucket++)
            destination[baseIndex + bucket] += _metricHistogram[baseIndex + bucket];
    }

    private double MetricPercentileMilliseconds(int metricIndex, long samples, double percentile)
    {
        if (samples == 0)
            return 0d;

        long target = (long)Math.Ceiling(samples * percentile);
        long cumulative = 0;
        int baseIndex = metricIndex * HistogramBucketCount;
        for (int bucket = 0; bucket < HistogramBucketCount; bucket++)
        {
            cumulative += _metricHistogram[baseIndex + bucket];
            if (cumulative >= target)
                return DurationHistogram.BucketUpperMilliseconds(bucket);
        }

        return TicksToMilliseconds(_metricMaximumTicks[metricIndex]);
    }

    private double FramePercentile(double percentile)
    {
        if (FrameCount == 0)
            return 0d;

        long target = (long)Math.Ceiling(FrameCount * percentile);
        long cumulative = 0;
        for (int bucket = 0; bucket < FrameHistogramBucketCount; bucket++)
        {
            cumulative += _frameHistogram[bucket];
            if (cumulative >= target)
            {
                if (bucket == FrameHistogramBucketCount - 1)
                    return FrameDurationHistogram.MaximumRepresentedMilliseconds;
                return FrameDurationHistogram.BucketUpperMilliseconds(bucket);
            }
        }

        return FrameMaxMilliseconds;
    }

    private double TicksToMilliseconds(double ticks)
    {
        return ticks * 1000d / _frequency;
    }

    private static int MetricIndex(PerformanceMetric metric)
    {
        int index = (int)metric;
        if (index < 0 || index >= (int)PerformanceMetric.Count)
            throw new ArgumentOutOfRangeException(nameof(metric));
        return index;
    }

    private static int CounterIndex(PerformanceCounter counter)
    {
        int index = (int)counter;
        if (index < 0 || index >= (int)PerformanceCounter.Count)
            throw new ArgumentOutOfRangeException(nameof(counter));
        return index;
    }
}

/// <summary>
/// Merges raw frame histograms while preserving percentile weighting.
/// </summary>
public sealed class FrameHistogramAccumulator
{
    private readonly long[] _histogram = new long[PerformanceWindow.FrameHistogramBucketCount];
    private double _totalMilliseconds;

    public long FrameCount { get; private set; }
    public double AverageMilliseconds =>
        FrameCount == 0 ? 0d : _totalMilliseconds / FrameCount;
    public double P50Milliseconds => Percentile(0.50d);
    public double P95Milliseconds => Percentile(0.95d);
    public double P99Milliseconds => Percentile(0.99d);
    public double MaxMilliseconds { get; private set; }

    public void Add(PerformanceWindow window)
    {
        if (window == null)
            throw new ArgumentNullException(nameof(window));
        if (window.FrameCount == 0)
            return;

        window.AccumulateFrameHistogram(_histogram);
        FrameCount += window.FrameCount;
        _totalMilliseconds += window.FrameAverageMilliseconds * window.FrameCount;
        if (window.FrameMaxMilliseconds > MaxMilliseconds)
            MaxMilliseconds = window.FrameMaxMilliseconds;
    }

    public void Reset()
    {
        Array.Clear(_histogram, 0, _histogram.Length);
        FrameCount = 0L;
        _totalMilliseconds = 0d;
        MaxMilliseconds = 0d;
    }

    private double Percentile(double percentile)
    {
        if (FrameCount == 0)
            return 0d;

        long target = (long)Math.Ceiling(FrameCount * percentile);
        long cumulative = 0L;
        for (int bucket = 0; bucket < _histogram.Length; bucket++)
        {
            cumulative += _histogram[bucket];
            if (cumulative < target)
                continue;

            if (bucket == _histogram.Length - 1)
                return FrameDurationHistogram.MaximumRepresentedMilliseconds;

            return FrameDurationHistogram.BucketUpperMilliseconds(bucket);
        }

        return MaxMilliseconds;
    }
}

/// <summary>
/// Records single-threaded performance data in fixed reusable storage.
/// </summary>
public sealed class PerformanceProfiler
{
    public const long NotSampled = -1L;

    private readonly PerformanceWindow[] _windows;
    private readonly long _frequency;
    private readonly long _windowTicks;
    private int _activeIndex;
    private int _windowGeneration;
    private PerformanceWindow _latestCompleted;

    public PerformanceProfiler(int windowSeconds = 2, int historySeconds = 60)
        : this(windowSeconds, historySeconds, Stopwatch.Frequency, Stopwatch.GetTimestamp())
    {
    }

    public PerformanceProfiler(
        int windowSeconds,
        int historySeconds,
        long timestampFrequency,
        long initialTimestamp)
    {
        if (windowSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        if (historySeconds < windowSeconds)
            throw new ArgumentOutOfRangeException(nameof(historySeconds));
        if (timestampFrequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency));

        _frequency = timestampFrequency;
        _windowTicks = checked(timestampFrequency * (long)windowSeconds);
        WindowSeconds = windowSeconds;
        HistorySeconds = historySeconds;

        int completedCapacity = (int)Math.Max(
            1L,
            ((long)historySeconds + windowSeconds - 1L) / windowSeconds);
        // Reserve one active slot while completed history remains readable.
        _windows = new PerformanceWindow[completedCapacity + 1];
        for (int i = 0; i < _windows.Length; i++)
            _windows[i] = new PerformanceWindow(timestampFrequency);

        Reset(initialTimestamp);
    }

    public int WindowSeconds { get; }
    public int HistorySeconds { get; }
    public int HistoryCapacity => _windows.Length - 1;
    public long TimestampFrequency => _frequency;
    public PerformanceWindow LatestCompleted => _latestCompleted;

    public static long TimestampNow()
    {
        return Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Counts the call and returns a timestamp when this invocation should be sampled.
    /// A return value of <see cref="NotSampled"/> must not be passed to <see cref="EndSample"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long BeginSample(PerformanceMetric metric, int sampleEvery = 1)
    {
        int index = MetricIndex(metric);
        if (sampleEvery <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleEvery));

        long call = _windows[_activeIndex].IncrementMetricCall(index);
        long sequence = call - 1L + _windowGeneration;
        if ((sampleEvery & (sampleEvery - 1)) == 0)
        {
            if ((sequence & (sampleEvery - 1L)) != 0L)
                return NotSampled;
        }
        else if (sequence % sampleEvery != 0L)
        {
            return NotSampled;
        }

        return Stopwatch.GetTimestamp();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EndSample(PerformanceMetric metric, long startedTimestamp)
    {
        if (startedTimestamp == NotSampled)
            return;

        long elapsed = Stopwatch.GetTimestamp() - startedTimestamp;
        _windows[_activeIndex].AddMetricSample(MetricIndex(metric), elapsed);
    }

    /// <summary>
    /// Records a caller-measured duration without reading the profiler clock.
    /// </summary>
    public void RecordDurationTicks(PerformanceMetric metric, long elapsedTicks)
    {
        int index = MetricIndex(metric);
        _windows[_activeIndex].IncrementMetricCall(index);
        _windows[_activeIndex].AddMetricSample(index, elapsedTicks);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Increment(PerformanceCounter counter, long amount = 1L)
    {
        _windows[_activeIndex].IncrementCounter(CounterIndex(counter), amount);
    }

    public void RecordFrameMilliseconds(double milliseconds)
    {
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0d)
            return;

        double ticks = milliseconds * _frequency / 1000d;
        long roundedTicks = ticks >= long.MaxValue ? long.MaxValue : (long)Math.Round(ticks);
        _windows[_activeIndex].AddFrameTicks(roundedTicks);
    }

    public bool TryCloseWindow(out PerformanceWindow window)
    {
        return TryCloseWindow(Stopwatch.GetTimestamp(), out window);
    }

    public bool TryCloseWindow(long timestamp, out PerformanceWindow window)
    {
        PerformanceWindow active = _windows[_activeIndex];
        if (timestamp < active.StartTimestamp ||
            timestamp - active.StartTimestamp < _windowTicks)
        {
            window = null;
            return false;
        }

        active.Close(timestamp);
        _latestCompleted = active;
        window = active;

        _activeIndex++;
        if (_activeIndex == _windows.Length)
            _activeIndex = 0;
        _windowGeneration++;
        PerformanceWindow next = _windows[_activeIndex];
        next.Clear(timestamp);
        return true;
    }

    public PerformanceWindow GetCompletedWindow(int age)
    {
        if (age < 0 || age >= HistoryCapacity || _latestCompleted == null)
            return null;

        int latestIndex = _activeIndex - 1;
        if (latestIndex < 0)
            latestIndex += _windows.Length;
        int index = latestIndex - age;
        while (index < 0)
            index += _windows.Length;

        PerformanceWindow value = _windows[index];
        return value.EndTimestamp == 0 ? null : value;
    }

    public void Reset()
    {
        Reset(Stopwatch.GetTimestamp());
    }

    public void Reset(long timestamp)
    {
        for (int i = 0; i < _windows.Length; i++)
            _windows[i].Clear(timestamp);

        _activeIndex = 0;
        _windowGeneration = 0;
        _latestCompleted = null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MetricIndex(PerformanceMetric metric)
    {
        int index = (int)metric;
        if (index < 0 || index >= (int)PerformanceMetric.Count)
            throw new ArgumentOutOfRangeException(nameof(metric));
        return index;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CounterIndex(PerformanceCounter counter)
    {
        int index = (int)counter;
        if (index < 0 || index >= (int)PerformanceCounter.Count)
            throw new ArgumentOutOfRangeException(nameof(counter));
        return index;
    }
}

internal static class DurationHistogram
{
    public static int BucketForTicks(long ticks, long frequency)
    {
        if (ticks <= 0)
            return 0;

        double nanosecondsDouble = ticks * (1000000000d / frequency);
        ulong nanoseconds = nanosecondsDouble >= ulong.MaxValue
            ? ulong.MaxValue
            : (ulong)Math.Ceiling(nanosecondsDouble);

        int bucket = 0;
        while (nanoseconds > 1UL && bucket < PerformanceWindow.HistogramBucketCount - 1)
        {
            nanoseconds >>= 1;
            bucket++;
        }

        return bucket;
    }

    public static double BucketUpperMilliseconds(int bucket)
    {
        if (bucket <= 0)
            return 0.000001d;
        if (bucket >= 62)
            return double.MaxValue;

        return Math.Pow(2d, bucket + 1) / 1000000d;
    }
}

internal static class FrameDurationHistogram
{
    private const int FineBucketCount = 512;
    private const double FineBucketWidthMilliseconds = 0.25d;
    private const double FineMaximumMilliseconds =
        FineBucketCount * FineBucketWidthMilliseconds;
    private const double TailBucketWidthMilliseconds = 16d;
    public const double MaximumRepresentedMilliseconds =
        FineMaximumMilliseconds +
        (PerformanceWindow.FrameHistogramBucketCount - FineBucketCount) *
        TailBucketWidthMilliseconds;

    public static int BucketForTicks(long ticks, long frequency)
    {
        if (ticks <= 0)
            return 0;

        double milliseconds = ticks * 1000d / frequency;
        int bucket;
        if (milliseconds < FineMaximumMilliseconds)
        {
            bucket = (int)(milliseconds / FineBucketWidthMilliseconds);
        }
        else if (milliseconds >= MaximumRepresentedMilliseconds)
        {
            bucket = PerformanceWindow.FrameHistogramBucketCount - 1;
        }
        else
        {
            bucket = FineBucketCount +
                     (int)((milliseconds - FineMaximumMilliseconds) /
                           TailBucketWidthMilliseconds);
        }

        return bucket < 0 ? 0 : bucket;
    }

    public static double BucketUpperMilliseconds(int bucket)
    {
        if (bucket < FineBucketCount)
            return (bucket + 1d) * FineBucketWidthMilliseconds;

        return FineMaximumMilliseconds +
               (bucket - FineBucketCount + 1d) * TailBucketWidthMilliseconds;
    }
}
