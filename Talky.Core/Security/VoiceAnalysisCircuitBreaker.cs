using System;

namespace Talky.Security;

public enum VoiceAnalysisFailureDisposition
{
    Recorded = 0,
    BreakerActivated = 1,
    IgnoredWhileSuspended = 2,
}

/// <summary>
/// Per-speaker rolling failure window for containing malformed voice analysis traffic.
/// Callers supply monotonic seconds so this policy remains deterministic and Unity-independent.
/// </summary>
public sealed class VoiceAnalysisCircuitBreaker
{
    public const int FailureThreshold = 8;
    public const double FailureWindowSeconds = 2d;
    public const double SuspensionSeconds = 10d;

    private readonly double[] _failureTimes = new double[FailureThreshold];
    private int _firstFailureIndex;
    private int _failureCount;
    private double _suspendedUntil = double.NegativeInfinity;
    private double _lastObserved = double.NegativeInfinity;

    public int RecentFailureCount => _failureCount;
    public double SuspendedUntilSeconds => _suspendedUntil;

    public bool IsSuspended(double nowSeconds)
    {
        double now = ObserveMonotonic(nowSeconds);
        if (now < _suspendedUntil)
            return true;

        if (!double.IsNegativeInfinity(_suspendedUntil))
        {
            _suspendedUntil = double.NegativeInfinity;
            ClearFailures();
        }

        PruneExpiredFailures(now);
        return false;
    }

    public VoiceAnalysisFailureDisposition RecordFailure(double nowSeconds)
    {
        double now = ObserveMonotonic(nowSeconds);
        if (now < _suspendedUntil)
            return VoiceAnalysisFailureDisposition.IgnoredWhileSuspended;

        if (!double.IsNegativeInfinity(_suspendedUntil))
        {
            _suspendedUntil = double.NegativeInfinity;
            ClearFailures();
        }

        PruneExpiredFailures(now);
        int writeIndex = (_firstFailureIndex + _failureCount) % FailureThreshold;
        _failureTimes[writeIndex] = now;
        _failureCount++;

        if (_failureCount < FailureThreshold)
            return VoiceAnalysisFailureDisposition.Recorded;

        _suspendedUntil = now + SuspensionSeconds;
        ClearFailures();
        return VoiceAnalysisFailureDisposition.BreakerActivated;
    }

    /// <summary>
    /// A successful packet does not erase recent failures, which prevents alternating valid and
    /// malformed packets from evading the rate breaker. It does age expired failures promptly.
    /// </summary>
    public void RecordSuccess(double nowSeconds)
    {
        double now = ObserveMonotonic(nowSeconds);
        if (now >= _suspendedUntil && !double.IsNegativeInfinity(_suspendedUntil))
        {
            _suspendedUntil = double.NegativeInfinity;
            ClearFailures();
        }

        if (double.IsNegativeInfinity(_suspendedUntil))
            PruneExpiredFailures(now);
    }

    public void Reset()
    {
        ClearFailures();
        _suspendedUntil = double.NegativeInfinity;
        _lastObserved = double.NegativeInfinity;
    }

    private void PruneExpiredFailures(double now)
    {
        while (_failureCount > 0 &&
               now - _failureTimes[_firstFailureIndex] > FailureWindowSeconds)
        {
            _firstFailureIndex = (_firstFailureIndex + 1) % FailureThreshold;
            _failureCount--;
        }

        if (_failureCount == 0)
            _firstFailureIndex = 0;
    }

    private void ClearFailures()
    {
        _firstFailureIndex = 0;
        _failureCount = 0;
    }

    private double ObserveMonotonic(double nowSeconds)
    {
        double now = nowSeconds;
        if (double.IsNaN(now) || double.IsInfinity(now))
            now = double.IsNegativeInfinity(_lastObserved) ? 0d : _lastObserved;
        else if (!double.IsNegativeInfinity(_lastObserved) && now < _lastObserved)
            now = _lastObserved;

        _lastObserved = now;
        return now;
    }
}
