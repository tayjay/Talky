using System;

namespace Talky.Security;

/// <summary>
/// Tracks repeated failures for one scheduled processing phase. Success in an unrelated idle or
/// lower-cadence phase does not erase the evidence needed to quarantine a recurring fault.
/// </summary>
public sealed class ConsecutiveFailureCircuitBreaker
{
    public const int FailureThreshold = 3;

    private Type _failureType;
    private int _phase;
    private int _failureCount;
    private bool _quarantined;

    public Type FailureType => _failureType;
    public int Phase => _phase;
    public int FailureCount => _failureCount;
    public bool IsQuarantined => _quarantined;

    public bool RecordFailure(Type failureType, int phase)
    {
        Type normalizedType = failureType ?? typeof(Exception);
        if (_quarantined)
            return false;

        if (_failureType == normalizedType && _phase == phase)
        {
            _failureCount++;
        }
        else
        {
            _failureType = normalizedType;
            _phase = phase;
            _failureCount = 1;
        }

        if (_failureCount < FailureThreshold)
            return false;

        _quarantined = true;
        return true;
    }

    public void RecordSuccess(int phase)
    {
        if (_quarantined || _failureType == null || _phase != phase)
            return;

        _failureType = null;
        _phase = 0;
        _failureCount = 0;
    }

    public void Reset()
    {
        _failureType = null;
        _phase = 0;
        _failureCount = 0;
        _quarantined = false;
    }
}
