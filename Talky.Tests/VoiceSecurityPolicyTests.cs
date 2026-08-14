using System;
using Talky.Security;

namespace Talky.Tests;

internal static class VoiceSecurityPolicyTests
{
    private static int _assertions;

    public static int Run()
    {
        _assertions = 0;
        TestPacketBounds();
        TestRollingFailureBreaker();
        TestFailureAgingAndMonotonicTime();
        TestDiagnosticAggregation();
        TestHotPathAllocation();
        return _assertions;
    }

    private static void TestPacketBounds()
    {
        Equal(
            VoicePacketValidationResult.MissingData,
            VoicePacketBoundsPolicy.Validate(null, 1),
            "Null packet is rejected");
        Equal(
            VoicePacketValidationResult.NonPositiveLength,
            VoicePacketBoundsPolicy.Validate(Array.Empty<byte>(), 0),
            "Zero-length packet is rejected");
        Equal(
            VoicePacketValidationResult.NonPositiveLength,
            VoicePacketBoundsPolicy.Validate(new byte[1], -1),
            "Negative packet length is rejected");
        Equal(
            VoicePacketValidationResult.Valid,
            VoicePacketBoundsPolicy.Validate(new byte[1], 1),
            "One-byte packet is accepted");
        Equal(
            VoicePacketValidationResult.LengthExceedsBuffer,
            VoicePacketBoundsPolicy.Validate(new byte[7], 8),
            "Length beyond managed buffer is rejected");
        Equal(
            VoicePacketValidationResult.Valid,
            VoicePacketBoundsPolicy.Validate(new byte[VoicePacketBoundsPolicy.MaximumPacketBytes],
                VoicePacketBoundsPolicy.MaximumPacketBytes),
            "Protocol maximum is accepted");
        Equal(
            VoicePacketValidationResult.LengthExceedsProtocolMaximum,
            VoicePacketBoundsPolicy.Validate(
                new byte[VoicePacketBoundsPolicy.MaximumPacketBytes + 1],
                VoicePacketBoundsPolicy.MaximumPacketBytes + 1),
            "Length beyond protocol maximum is rejected");
    }

    private static void TestRollingFailureBreaker()
    {
        VoiceAnalysisCircuitBreaker breaker = new VoiceAnalysisCircuitBreaker();
        for (int i = 0; i < VoiceAnalysisCircuitBreaker.FailureThreshold - 1; i++)
        {
            Equal(
                VoiceAnalysisFailureDisposition.Recorded,
                breaker.RecordFailure(i * 0.2d),
                "Pre-threshold failure is recorded " + i);
            breaker.RecordSuccess(i * 0.2d + 0.1d);
        }

        Equal(
            VoiceAnalysisFailureDisposition.BreakerActivated,
            breaker.RecordFailure(1.4d),
            "Interleaved successes do not defeat rolling failure threshold");
        True(breaker.IsSuspended(1.5d), "Breaker suspends analysis after threshold");
        double suspendedUntil = breaker.SuspendedUntilSeconds;
        Equal(
            VoiceAnalysisFailureDisposition.IgnoredWhileSuspended,
            breaker.RecordFailure(5d),
            "Failure during suspension is ignored");
        Equal(suspendedUntil, breaker.SuspendedUntilSeconds, "Suspension is not extended");
        breaker.RecordSuccess(6d);
        True(breaker.IsSuspended(6d), "Success does not clear an active suspension");
        Equal(suspendedUntil, breaker.SuspendedUntilSeconds, "Success preserves fixed suspension deadline");
        True(!breaker.IsSuspended(11.4d), "Analysis resumes at fixed suspension deadline");
    }

    private static void TestFailureAgingAndMonotonicTime()
    {
        VoiceAnalysisCircuitBreaker breaker = new VoiceAnalysisCircuitBreaker();
        for (int i = 0; i < 7; i++)
            breaker.RecordFailure(i * 0.1d);

        breaker.RecordSuccess(2.7d);
        Equal(0, breaker.RecentFailureCount, "Success ages expired failures");
        Equal(
            VoiceAnalysisFailureDisposition.Recorded,
            breaker.RecordFailure(2.8d),
            "Expired window starts a fresh count");

        for (int i = 0; i < 7; i++)
            breaker.RecordFailure(3d + i * 0.1d);
        True(breaker.IsSuspended(3.7d), "Fresh rolling window activates breaker");
        double deadline = breaker.SuspendedUntilSeconds;
        True(breaker.IsSuspended(1d), "Backward time cannot shorten suspension");
        Equal(deadline, breaker.SuspendedUntilSeconds, "Backward time preserves deadline");

        breaker.Reset();
        True(!breaker.IsSuspended(0d), "Reset clears suspension and monotonic state");
    }

    private static void TestDiagnosticAggregation()
    {
        VoiceFailureDiagnosticGate gate = new VoiceFailureDiagnosticGate();
        True(gate.RecordFailure(10d, out int count), "First diagnostic is emitted");
        Equal(1, count, "First diagnostic count");
        True(!gate.RecordFailure(20d, out count), "Diagnostic is suppressed inside interval");
        True(!gate.RecordFailure(5d, out count), "Backward time cannot bypass throttle");
        True(gate.RecordFailure(70d, out count), "Diagnostic emits after one minute");
        Equal(3, count, "Suppressed failures are aggregated");
    }

    private static void TestHotPathAllocation()
    {
        byte[] packet = new byte[VoicePacketBoundsPolicy.MaximumPacketBytes];
        VoiceAnalysisCircuitBreaker breaker = new VoiceAnalysisCircuitBreaker();
        VoiceFailureDiagnosticGate gate = new VoiceFailureDiagnosticGate();

        VoicePacketBoundsPolicy.Validate(packet, packet.Length);
        breaker.RecordFailure(0d);
        breaker.RecordSuccess(0.1d);
        gate.RecordFailure(0d, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i <= 10000; i++)
        {
            VoicePacketBoundsPolicy.Validate(packet, packet.Length);
            double now = i * 0.01d;
            breaker.RecordFailure(now);
            breaker.RecordSuccess(now);
            gate.RecordFailure(now, out _);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Equal(0L, allocated, "Voice security policy hot paths do not allocate");
    }

    private static void True(bool condition, string name)
    {
        _assertions++;
        if (!condition)
            throw new InvalidOperationException(name);
    }

    private static void Equal<T>(T expected, T actual, string name)
    {
        _assertions++;
        if (!Equals(expected, actual))
            throw new InvalidOperationException(
                name + ": expected " + expected + ", got " + actual);
    }
}
