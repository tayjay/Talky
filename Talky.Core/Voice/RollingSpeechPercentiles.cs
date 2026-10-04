using System;

namespace Talky.Voice;

/// <summary>
/// Tracks rolling histogram percentiles for one player's recent speech levels.
/// </summary>
public sealed class RollingSpeechPercentiles
{
    public const int DefaultCapacity = 60;
    public const int DefaultMinimumObservations = 10;
    public const double DefaultLowPercentile = 0.20d;
    public const double DefaultHighPercentile = 0.80d;
    public const double DefaultMinimumSpanDecibels = 8d;
    public const double DefaultMinimumDecibels = -96d;
    public const double DefaultMaximumDecibels = 0d;
    public const double DefaultBinWidthDecibels = 1d;

    private readonly byte[] _observations;
    private readonly ushort[] _histogram;
    private readonly int _minimumObservations;
    private readonly double _lowPercentile;
    private readonly double _highPercentile;
    private readonly double _minimumSpanDecibels;
    private readonly double _minimumDecibels;
    private readonly double _maximumDecibels;
    private readonly double _binWidthDecibels;

    private int _writeIndex;
    private int _count;

    public RollingSpeechPercentiles(
        int capacity = DefaultCapacity,
        int minimumObservations = DefaultMinimumObservations,
        double lowPercentile = DefaultLowPercentile,
        double highPercentile = DefaultHighPercentile,
        double minimumSpanDecibels = DefaultMinimumSpanDecibels,
        double minimumDecibels = DefaultMinimumDecibels,
        double maximumDecibels = DefaultMaximumDecibels,
        double binWidthDecibels = DefaultBinWidthDecibels)
    {
        if (capacity <= 0 || capacity > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (minimumObservations <= 0 || minimumObservations > capacity)
            throw new ArgumentOutOfRangeException(nameof(minimumObservations));
        if (!IsFinite(lowPercentile) || lowPercentile <= 0d || lowPercentile >= 1d)
            throw new ArgumentOutOfRangeException(nameof(lowPercentile));
        if (!IsFinite(highPercentile) ||
            highPercentile <= lowPercentile ||
            highPercentile >= 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(highPercentile));
        }
        if (!IsFinite(minimumDecibels) ||
            !IsFinite(maximumDecibels) ||
            maximumDecibels <= minimumDecibels)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDecibels));
        }
        if (!IsFinite(binWidthDecibels) ||
            binWidthDecibels <= 0d ||
            binWidthDecibels > maximumDecibels - minimumDecibels)
        {
            throw new ArgumentOutOfRangeException(nameof(binWidthDecibels));
        }
        if (!IsFinite(minimumSpanDecibels) ||
            minimumSpanDecibels <= 0d ||
            minimumSpanDecibels > maximumDecibels - minimumDecibels)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumSpanDecibels));
        }

        int binCount =
            (int)Math.Floor((maximumDecibels - minimumDecibels) / binWidthDecibels) + 1;
        if (binCount <= 0 || binCount > byte.MaxValue + 1)
            throw new ArgumentOutOfRangeException(nameof(binWidthDecibels));

        _observations = new byte[capacity];
        _histogram = new ushort[binCount];
        _minimumObservations = minimumObservations;
        _lowPercentile = lowPercentile;
        _highPercentile = highPercentile;
        _minimumSpanDecibels = minimumSpanDecibels;
        _minimumDecibels = minimumDecibels;
        _maximumDecibels = maximumDecibels;
        _binWidthDecibels = binWidthDecibels;
    }

    public int Capacity => _observations.Length;
    public int Count => _count;
    public bool IsReady => _count >= _minimumObservations;

    public double LowPercentileDecibels { get; private set; } = double.NaN;

    public double HighPercentileDecibels { get; private set; } = double.NaN;

    public double AnimationLowDecibels { get; private set; } = double.NaN;

    public double AnimationHighDecibels { get; private set; } = double.NaN;

    /// <summary>
    /// Observes a speech level; rejected values do not age the existing window.
    /// </summary>
    public bool Observe(double decibels)
    {
        if (!IsFinite(decibels) || decibels < _minimumDecibels)
            return false;

        if (decibels > _maximumDecibels)
            decibels = _maximumDecibels;

        int bin = (int)((decibels - _minimumDecibels) / _binWidthDecibels);
        if (bin >= _histogram.Length)
            bin = _histogram.Length - 1;

        if (_count == _observations.Length)
        {
            int previousBin = _observations[_writeIndex];
            _histogram[previousBin]--;
        }
        else
        {
            _count++;
        }

        _observations[_writeIndex] = (byte)bin;
        _histogram[bin]++;
        _writeIndex++;
        if (_writeIndex == _observations.Length)
            _writeIndex = 0;

        Recalculate();
        return true;
    }

    public void Reset()
    {
        Array.Clear(_observations, 0, _observations.Length);
        Array.Clear(_histogram, 0, _histogram.Length);
        _writeIndex = 0;
        _count = 0;
        LowPercentileDecibels = double.NaN;
        HighPercentileDecibels = double.NaN;
        AnimationLowDecibels = double.NaN;
        AnimationHighDecibels = double.NaN;
    }

    private void Recalculate()
    {
        if (!IsReady)
        {
            LowPercentileDecibels = double.NaN;
            HighPercentileDecibels = double.NaN;
            AnimationLowDecibels = double.NaN;
            AnimationHighDecibels = double.NaN;
            return;
        }

        double low = ReadPercentile(_lowPercentile);
        double high = ReadPercentile(_highPercentile);
        LowPercentileDecibels = low;
        HighPercentileDecibels = high;

        if (high - low < _minimumSpanDecibels)
        {
            double center = (low + high) * 0.5d;
            low = center - _minimumSpanDecibels * 0.5d;
            high = center + _minimumSpanDecibels * 0.5d;
        }

        if (low < _minimumDecibels)
        {
            high += _minimumDecibels - low;
            low = _minimumDecibels;
        }

        if (high > _maximumDecibels)
        {
            low -= high - _maximumDecibels;
            high = _maximumDecibels;
        }

        AnimationLowDecibels = Math.Max(_minimumDecibels, low);
        AnimationHighDecibels = Math.Min(_maximumDecibels, high);
    }

    private double ReadPercentile(double percentile)
    {
        int rank = Math.Max(1, (int)Math.Ceiling(percentile * _count));
        int cumulative = 0;
        for (int bin = 0; bin < _histogram.Length; bin++)
        {
            cumulative += _histogram[bin];
            if (cumulative >= rank)
            {
                return Math.Min(
                    _maximumDecibels,
                    _minimumDecibels + (bin + 0.5d) * _binWidthDecibels);
            }
        }

        return _maximumDecibels;
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}

/// <summary>
/// Tracks complete, non-overlapping analysis windows in a decoded sample stream.
/// </summary>
public struct SpeechObservationCadence
{
    private readonly int _requiredSamples;
    private int _freshSamples;
    private int _samplesSinceObservation;

    public SpeechObservationCadence(int requiredSamples)
    {
        if (requiredSamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(requiredSamples));

        _requiredSamples = requiredSamples;
        _freshSamples = 0;
        _samplesSinceObservation = 0;
    }

    public int RequiredSamples => _requiredSamples;
    public int FreshSamples => _freshSamples;

    public bool Advance(int decodedSamples)
    {
        if (_requiredSamples <= 0)
            throw new InvalidOperationException("Speech observation cadence was not initialized.");
        if (decodedSamples <= 0)
            return false;

        _freshSamples = Math.Min(_requiredSamples, _freshSamples + decodedSamples);
        _samplesSinceObservation += decodedSamples;
        if (_freshSamples < _requiredSamples ||
            _samplesSinceObservation < _requiredSamples)
        {
            return false;
        }

        _samplesSinceObservation %= _requiredSamples;
        return true;
    }

    public void Reset()
    {
        _freshSamples = 0;
        _samplesSinceObservation = 0;
    }
}

/// <summary>
/// Separates above-floor voice activity from packet activity so background noise cannot sustain animations.
/// </summary>
public struct AudibleSpeechActivity
{
    private double _lastAudibleSeconds;
    private bool _hasAudibleActivity;

    public bool LastObservationWasAudible { get; private set; }
    public double LastAudibleSeconds =>
        _hasAudibleActivity ? _lastAudibleSeconds : -1d;

    public void Observe(double activitySeconds, bool audible)
    {
        LastObservationWasAudible =
            audible &&
            !double.IsNaN(activitySeconds) &&
            !double.IsInfinity(activitySeconds);
        if (!LastObservationWasAudible)
            return;

        _lastAudibleSeconds = activitySeconds;
        _hasAudibleActivity = true;
    }

    public bool WasRecentlyAudible(double nowSeconds, double durationMilliseconds)
    {
        return _hasAudibleActivity &&
               (nowSeconds - _lastAudibleSeconds) * 1000d <= durationMilliseconds;
    }

    public void Reset()
    {
        _lastAudibleSeconds = 0d;
        _hasAudibleActivity = false;
        LastObservationWasAudible = false;
    }
}

public enum SpeechAnimationLevel
{
    Silent,
    Quiet,
    Loud,
}

public readonly struct SpeechAnimationDecision
{
    public SpeechAnimationDecision(SpeechAnimationLevel level, float volumeRatio)
        : this(
            level,
            volumeRatio,
            SpeechAnimationMath.CalculateHeadBobDrive(volumeRatio))
    {
    }

    public SpeechAnimationDecision(
        SpeechAnimationLevel level,
        float volumeRatio,
        float headBobDrive)
    {
        Level = level;
        VolumeRatio = volumeRatio;
        HeadBobDrive = headBobDrive;
    }

    public SpeechAnimationLevel Level { get; }
    public float VolumeRatio { get; }
    public float HeadBobDrive { get; }
}

/// <summary>
/// Maps speech levels to animation intensity using learned or fallback thresholds.
/// </summary>
public static class SpeechAnimationMath
{
    public const float HeadBobNeutralVolumeRatio = 0.25f;

    public static SpeechAnimationDecision Evaluate(
        double currentDecibels,
        double fallbackLowDecibels,
        double fallbackHighDecibels,
        bool hasAdaptiveRange,
        double percentileLowDecibels,
        double percentileHighDecibels,
        double animationLowDecibels,
        double animationHighDecibels,
        float minimumAdaptiveVolumeRatio)
    {
        double animationLow = hasAdaptiveRange
            ? animationLowDecibels
            : fallbackLowDecibels;
        double animationHigh = hasAdaptiveRange
            ? animationHighDecibels
            : fallbackHighDecibels;
        bool hasPercentileRange = hasAdaptiveRange &&
                                  IsFinite(percentileLowDecibels) &&
                                  IsFinite(percentileHighDecibels) &&
                                  percentileHighDecibels > percentileLowDecibels;
        double faceLow = hasPercentileRange
            ? percentileLowDecibels
            : animationLow;
        double faceHigh = hasPercentileRange
            ? percentileHighDecibels
            : animationHigh;
        faceHigh = Math.Max(faceLow, faceHigh);

        if (!IsAtOrAboveActivityFloor(currentDecibels, fallbackLowDecibels))
            return new SpeechAnimationDecision(SpeechAnimationLevel.Silent, 0f);

        double comparisonDecibels = hasAdaptiveRange
            ? QuantizeForPercentileComparison(currentDecibels)
            : currentDecibels;
        if (comparisonDecibels < faceLow)
            return new SpeechAnimationDecision(SpeechAnimationLevel.Silent, 0f);

        double span = animationHigh - animationLow;
        float normalizedVolume = span > 0d
            ? (float)((currentDecibels - animationLow) / span)
            : 1f;
        if (normalizedVolume < 0f)
            normalizedVolume = 0f;
        else if (normalizedVolume > 1f)
            normalizedVolume = 1f;

        float volumeRatio = hasAdaptiveRange
            ? minimumAdaptiveVolumeRatio +
              (1f - minimumAdaptiveVolumeRatio) * normalizedVolume
            : normalizedVolume;
        SpeechAnimationLevel level = comparisonDecibels >= faceHigh
            ? SpeechAnimationLevel.Loud
            : SpeechAnimationLevel.Quiet;
        return new SpeechAnimationDecision(level, volumeRatio);
    }

    /// <summary>
    /// Maps the public 0..1 presentation ratio to signed head-bob drive.
    /// Silent input remains exactly zero; quiet audible input may intentionally
    /// produce a small negative pitch below the neutral ratio.
    /// </summary>
    public static float CalculateHeadBobDrive(float volumeRatio)
    {
        if (float.IsNaN(volumeRatio) || float.IsInfinity(volumeRatio) || volumeRatio <= 0f)
            return 0f;

        if (volumeRatio > 1f)
            volumeRatio = 1f;
        return volumeRatio - HeadBobNeutralVolumeRatio;
    }

    /// <summary>
    /// Aligns comparisons with the percentile histogram's 1 dB bins.
    /// </summary>
    public static double QuantizeForPercentileComparison(double decibels)
    {
        if (double.IsNaN(decibels))
            return double.NegativeInfinity;
        if (decibels < RollingSpeechPercentiles.DefaultMinimumDecibels)
            return decibels;
        if (decibels >= RollingSpeechPercentiles.DefaultMaximumDecibels)
            return RollingSpeechPercentiles.DefaultMaximumDecibels;

        int bin = (int)(
            (decibels - RollingSpeechPercentiles.DefaultMinimumDecibels) /
            RollingSpeechPercentiles.DefaultBinWidthDecibels);
        return RollingSpeechPercentiles.DefaultMinimumDecibels +
               (bin + 0.5d) * RollingSpeechPercentiles.DefaultBinWidthDecibels;
    }

    public static bool IsAtOrAboveActivityFloor(
        double decibels,
        double activityFloorDecibels) =>
        IsFinite(decibels) &&
        IsFinite(activityFloorDecibels) &&
        decibels >= activityFloorDecibels;

    public static double MeanSquareFromDecibels(double decibels) =>
        IsFinite(decibels)
            ? Math.Pow(10d, decibels / 10d)
            : double.NaN;

    public static bool IsMeanSquareAtOrAboveActivityFloor(
        double meanSquare,
        double activityFloorMeanSquare) =>
        IsFinite(meanSquare) &&
        meanSquare >= 0d &&
        IsFinite(activityFloorMeanSquare) &&
        activityFloorMeanSquare >= 0d &&
        meanSquare >= activityFloorMeanSquare;

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}
