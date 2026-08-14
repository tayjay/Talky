using System;

namespace Talky.Security;

/// <summary>
/// Allocation-free failure aggregation for a caller-owned, privacy-safe diagnostic message.
/// </summary>
public sealed class VoiceFailureDiagnosticGate
{
    public const double ReportIntervalSeconds = 60d;

    private int _pendingFailures;
    private double _lastReportAt = double.NegativeInfinity;
    private double _lastObserved = double.NegativeInfinity;

    public bool RecordFailure(double nowSeconds, out int aggregatedFailures)
    {
        double now = ObserveMonotonic(nowSeconds);
        if (_pendingFailures < int.MaxValue)
            _pendingFailures++;

        if (!double.IsNegativeInfinity(_lastReportAt) &&
            now - _lastReportAt < ReportIntervalSeconds)
        {
            aggregatedFailures = 0;
            return false;
        }

        _lastReportAt = now;
        aggregatedFailures = _pendingFailures;
        _pendingFailures = 0;
        return true;
    }

    public void Reset()
    {
        _pendingFailures = 0;
        _lastReportAt = double.NegativeInfinity;
        _lastObserved = double.NegativeInfinity;
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
