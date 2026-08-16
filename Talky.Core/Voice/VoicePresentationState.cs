namespace Talky.Voice;

/// <summary>
/// Stores animation signal state while decoding remains owned by the transmitting player.
/// </summary>
public sealed class VoicePresentationState
{
    private double _rootMeanSquare;
    private double _lastActivitySeconds = -1d;
    private double _percentileLowDecibels = double.NaN;
    private double _percentileHighDecibels = double.NaN;
    private double _animationLowDecibels = double.NaN;
    private double _animationHighDecibels = double.NaN;

    public double RootMeanSquare => _rootMeanSquare;
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

        _rootMeanSquare = rootMeanSquare;
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

        _lastActivitySeconds = activitySeconds;
        return true;
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
        _rootMeanSquare = 0d;
        _lastActivitySeconds = -1d;
        _percentileLowDecibels = double.NaN;
        _percentileHighDecibels = double.NaN;
        _animationLowDecibels = double.NaN;
        _animationHighDecibels = double.NaN;
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}
