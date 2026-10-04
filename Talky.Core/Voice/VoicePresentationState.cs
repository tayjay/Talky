using System;

namespace Talky.Voice;

/// <summary>
/// Stores animation signal state while decoding remains owned by the transmitting player.
/// </summary>
public sealed class VoicePresentationState
{
    private double _meanSquare;
    private double _rootMeanSquare;
    private bool _rootMeanSquareIsCurrent = true;
    private double _lastActivitySeconds = -1d;
    private double _percentileLowDecibels = double.NaN;
    private double _percentileHighDecibels = double.NaN;
    private double _animationLowDecibels = double.NaN;
    private double _animationHighDecibels = double.NaN;

    public double MeanSquare => _meanSquare;
    public double RootMeanSquare
    {
        get
        {
            if (!_rootMeanSquareIsCurrent)
            {
                _rootMeanSquare = Math.Sqrt(_meanSquare);
                _rootMeanSquareIsCurrent = true;
            }

            return _rootMeanSquare;
        }
    }
    public double LastActivitySeconds => _lastActivitySeconds;
    public bool HasPercentileRange =>
        IsFinite(_percentileLowDecibels) &&
        IsFinite(_percentileHighDecibels) &&
        _percentileHighDecibels > _percentileLowDecibels;
    public bool HasAdaptiveRange =>
        IsFinite(_animationLowDecibels) &&
        IsFinite(_animationHighDecibels) &&
        _animationHighDecibels > _animationLowDecibels;
    public double PercentileLowDecibels => _percentileLowDecibels;
    public double PercentileHighDecibels => _percentileHighDecibels;
    public double AnimationLowDecibels => _animationLowDecibels;
    public double AnimationHighDecibels => _animationHighDecibels;

    public bool TryPresent(int decodedSampleCount, double rootMeanSquare, double activitySeconds)
    {
        return TryPresent(
            decodedSampleCount,
            rootMeanSquare,
            double.NaN,
            double.NaN,
            double.NaN,
            double.NaN,
            activitySeconds);
    }

    public bool TryPresent(
        int decodedSampleCount,
        double rootMeanSquare,
        double animationLowDecibels,
        double animationHighDecibels,
        double activitySeconds)
    {
        return TryPresent(
            decodedSampleCount,
            rootMeanSquare,
            double.NaN,
            double.NaN,
            animationLowDecibels,
            animationHighDecibels,
            activitySeconds);
    }

    public bool TryPresent(
        int decodedSampleCount,
        double rootMeanSquare,
        double percentileLowDecibels,
        double percentileHighDecibels,
        double animationLowDecibels,
        double animationHighDecibels,
        double activitySeconds)
    {
        if (decodedSampleCount <= 0)
            return false;

        _meanSquare = rootMeanSquare * rootMeanSquare;
        _rootMeanSquare = rootMeanSquare;
        _rootMeanSquareIsCurrent = true;
        StoreRanges(
            percentileLowDecibels,
            percentileHighDecibels,
            animationLowDecibels,
            animationHighDecibels);
        _lastActivitySeconds = activitySeconds;
        return true;
    }

    public bool TryPresentMeanSquare(
        int decodedSampleCount,
        double meanSquare,
        double activitySeconds)
    {
        return TryPresentMeanSquare(
            decodedSampleCount,
            meanSquare,
            double.NaN,
            double.NaN,
            double.NaN,
            double.NaN,
            activitySeconds);
    }

    public bool TryPresentMeanSquare(
        int decodedSampleCount,
        double meanSquare,
        double animationLowDecibels,
        double animationHighDecibels,
        double activitySeconds)
    {
        return TryPresentMeanSquare(
            decodedSampleCount,
            meanSquare,
            double.NaN,
            double.NaN,
            animationLowDecibels,
            animationHighDecibels,
            activitySeconds);
    }

    public bool TryPresentMeanSquare(
        int decodedSampleCount,
        double meanSquare,
        double percentileLowDecibels,
        double percentileHighDecibels,
        double animationLowDecibels,
        double animationHighDecibels,
        double activitySeconds)
    {
        if (decodedSampleCount <= 0 ||
            !IsFinite(meanSquare) ||
            meanSquare < 0d)
        {
            return false;
        }

        _meanSquare = meanSquare;
        _rootMeanSquare = 0d;
        _rootMeanSquareIsCurrent = meanSquare == 0d;
        StoreRanges(
            percentileLowDecibels,
            percentileHighDecibels,
            animationLowDecibels,
            animationHighDecibels);
        _lastActivitySeconds = activitySeconds;
        return true;
    }

    private void StoreRanges(
        double percentileLowDecibels,
        double percentileHighDecibels,
        double animationLowDecibels,
        double animationHighDecibels)
    {
        if (IsFinite(percentileLowDecibels) &&
            IsFinite(percentileHighDecibels) &&
            percentileHighDecibels > percentileLowDecibels)
        {
            _percentileLowDecibels = percentileLowDecibels;
            _percentileHighDecibels = percentileHighDecibels;
        }
        else
        {
            _percentileLowDecibels = double.NaN;
            _percentileHighDecibels = double.NaN;
        }

        if (IsFinite(animationLowDecibels) &&
            IsFinite(animationHighDecibels) &&
            animationHighDecibels > animationLowDecibels)
        {
            _animationLowDecibels = animationLowDecibels;
            _animationHighDecibels = animationHighDecibels;
        }
        else
        {
            _animationLowDecibels = double.NaN;
            _animationHighDecibels = double.NaN;
        }
    }

    public void SetLastActivitySeconds(double activitySeconds)
    {
        _lastActivitySeconds = activitySeconds;
    }

    public bool WasRecentlyActive(double nowSeconds, double durationMilliseconds)
    {
        return _lastActivitySeconds >= 0d &&
               (nowSeconds - _lastActivitySeconds) * 1000d <= durationMilliseconds;
    }

    public void Reset()
    {
        _meanSquare = 0d;
        _rootMeanSquare = 0d;
        _rootMeanSquareIsCurrent = true;
        _lastActivitySeconds = -1d;
        _percentileLowDecibels = double.NaN;
        _percentileHighDecibels = double.NaN;
        _animationLowDecibels = double.NaN;
        _animationHighDecibels = double.NaN;
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}
