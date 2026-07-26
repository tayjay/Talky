namespace Talky.Voice;

/// <summary>
/// Stores the decoded signal snapshot used to animate one visual player.
/// Decoder and rolling-energy ownership remain with the transmitting player.
/// </summary>
public sealed class VoicePresentationState
{
    private double _rootMeanSquare;
    private double _lastActivitySeconds = -1d;

    public double RootMeanSquare => _rootMeanSquare;
    public double LastActivitySeconds => _lastActivitySeconds;

    public bool TryPresent(int decodedSampleCount, double rootMeanSquare, double activitySeconds)
    {
        if (decodedSampleCount <= 0)
            return false;

        _rootMeanSquare = rootMeanSquare;
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
    }
}
