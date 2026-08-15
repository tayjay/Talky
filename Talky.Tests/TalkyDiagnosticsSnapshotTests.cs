using System;
using Talky.Performance;

namespace Talky.Tests;

internal static class TalkyDiagnosticsSnapshotTests
{
    private static int _assertions;
    private static double _measurementSink;

    public static int Run()
    {
        _assertions = 0;
        TestDefaultLoggingInterval();
        TestGaugeAggregationInWindow();
        TestImmutableIntervalAggregation();
        TestRoundHighWaterResetPreservesTotals();
        TestNonDivisorRollingHorizonNeverOverstatesDuration();
        TestExactCountersSurviveDisabledAndPartialWindows();
        return _assertions;
    }

    private static void TestDefaultLoggingInterval()
    {
        Equal(300, new PerformanceConfig().SummaryLogIntervalSeconds, "Default summary interval");

        PerformanceConfigValidationResult validation = PerformanceConfigValidator.Validate(
            new PerformanceConfig { SummaryLogIntervalSeconds = -1 });
        Equal(0, validation.Config.SummaryLogIntervalSeconds, "Negative summary interval clamps to zero");
    }

    private static void TestGaugeAggregationInWindow()
    {
        var profiler = new PerformanceProfiler(2, 60, 1000L, 0L);
        profiler.SetGauge(PerformanceCounter.PendingActionsCurrent, 2L);
        profiler.SetGauge(PerformanceCounter.PendingActionsCurrent, 7L);
        profiler.ObserveHighWater(PerformanceCounter.PendingActionsHighWater, 3L);
        profiler.ObserveHighWater(PerformanceCounter.PendingActionsHighWater, 9L);
        profiler.ObserveHighWater(PerformanceCounter.PendingActionsHighWater, 4L);
        profiler.SetGauge(PerformanceCounter.SimultaneousSpeakersCurrent, 3L);
        profiler.ObserveHighWater(PerformanceCounter.SimultaneousSpeakersHighWater, 6L);
        profiler.SetGauge(PerformanceCounter.SpeechTrackersCurrent, 12L);
        profiler.SetGauge(PerformanceCounter.LookTrackersCurrent, 11L);
        profiler.SetGauge(PerformanceCounter.OwnedSubscriptionGroupsCurrent, 5L);
        True(profiler.TryCloseWindow(2000L, out PerformanceWindow window), "Gauge window closes");
        Equal(7L, window.GetCounter(PerformanceCounter.PendingActionsCurrent), "Gauge retains latest value");
        Equal(9L, window.GetCounter(PerformanceCounter.PendingActionsHighWater), "Gauge retains maximum value");
        True(profiler.TryCloseWindow(4000L, out PerformanceWindow carried), "Unchanged gauge window closes");
        Equal(7L, carried.GetCounter(PerformanceCounter.PendingActionsCurrent), "Latest gauge carries across windows");
        Equal(7L, carried.GetCounter(PerformanceCounter.PendingActionsHighWater), "New window maximum is seeded from current depth");
        Equal(3L, carried.GetCounter(PerformanceCounter.SimultaneousSpeakersCurrent), "Speaker gauge carries across windows");
        Equal(3L, carried.GetCounter(PerformanceCounter.SimultaneousSpeakersHighWater), "New speaker maximum is seeded from current speakers");
        Equal(12L, carried.GetCounter(PerformanceCounter.SpeechTrackersCurrent), "Speech tracker gauge carries across windows");
        Equal(11L, carried.GetCounter(PerformanceCounter.LookTrackersCurrent), "Look tracker gauge carries across windows");
        Equal(5L, carried.GetCounter(PerformanceCounter.OwnedSubscriptionGroupsCurrent), "Subscription gauge carries across windows");
        Throws<ArgumentException>(
            () => profiler.SetGauge(PerformanceCounter.VoicePackets, 1L),
            "Sum counter cannot be used as latest gauge");
        Throws<ArgumentException>(
            () => profiler.ObserveHighWater(PerformanceCounter.VoicePackets, 1L),
            "Sum counter cannot be used as maximum gauge");
    }

    private static void TestImmutableIntervalAggregation()
    {
        var profiler = new PerformanceProfiler(2, 60, 1000L, 0L);
        profiler.RecordDurationTicks(PerformanceMetric.DirectVisibilitySync, 2L);
        profiler.Increment(PerformanceCounter.LogicalSyncRecords, 8L);
        profiler.SetGauge(PerformanceCounter.PendingActionsCurrent, 3L);
        profiler.ObserveHighWater(PerformanceCounter.PendingActionsHighWater, 5L);
        profiler.SetGauge(PerformanceCounter.SimultaneousSpeakersCurrent, 2L);
        profiler.ObserveHighWater(PerformanceCounter.SimultaneousSpeakersHighWater, 4L);
        profiler.SetGauge(PerformanceCounter.SpeechTrackersCurrent, 12L);
        profiler.SetGauge(PerformanceCounter.LookTrackersCurrent, 11L);
        profiler.RecordFrameMilliseconds(20d);
        True(profiler.TryCloseWindow(2000L, out PerformanceWindow first), "First interval window closes");

        var collector = new TalkyDiagnosticsCollector();
        collector.ObserveFrame(12, 4, 3);
        collector.SetIdentity(
            TalkyDiagnosticsLifecycleState.Enabled,
            TalkyDiagnosticsTrustState.Verified,
            TalkyDiagnosticsTrustState.NotPresent,
            true,
            Talky.Compatibility.CedModLookSyncMode.StockSync);
        var governor = new AdaptivePerformanceGovernor(
            new PerformanceConfig
            {
                WarmupSeconds = 0,
                HardTalkyBudgetMsPerFrame = 1d,
            },
            0d);
        collector.ObserveGovernorDecision(
            governor.Evaluate(new GovernorSample(2d, 10d, true, true, 2d), 2d));
        collector.Publish(
            profiler,
            first,
            2d,
            2,
            PerformanceMode.ObserveOnly,
            PerformanceTier.Full,
            PerformanceTier.Full,
            false,
            true,
            false,
            50d,
            60d);

        profiler.RecordDurationTicks(PerformanceMetric.DirectVisibilitySync, 4L);
        profiler.Increment(PerformanceCounter.LogicalSyncRecords, 12L);
        profiler.SetGauge(PerformanceCounter.PendingActionsCurrent, 1L);
        profiler.ObserveHighWater(PerformanceCounter.PendingActionsHighWater, 7L);
        profiler.SetGauge(PerformanceCounter.SimultaneousSpeakersCurrent, 3L);
        profiler.ObserveHighWater(PerformanceCounter.SimultaneousSpeakersHighWater, 5L);
        profiler.SetGauge(PerformanceCounter.SpeechTrackersCurrent, 10L);
        profiler.SetGauge(PerformanceCounter.LookTrackersCurrent, 9L);
        profiler.RecordFrameMilliseconds(30d);
        True(profiler.TryCloseWindow(4000L, out PerformanceWindow second), "Second interval window closes");

        TalkyDiagnosticsSnapshot snapshot = collector.Publish(
            profiler,
            second,
            4d,
            2,
            PerformanceMode.ObserveOnly,
            PerformanceTier.Full,
            PerformanceTier.Full,
            false,
            true,
            false,
            48d,
            60d);
        TalkyDiagnosticsInterval interval = snapshot.SinceEnable;

        Equal(1, snapshot.SchemaVersion, "Diagnostics schema remains v1");
        Equal(2L, interval.WindowCount, "Interval window count");
        Equal(2L, interval.FrameCount, "Interval frame count");
        Equal(20L, interval.GetCounter(PerformanceCounter.LogicalSyncRecords), "Interval sums events");
        Equal(1L, interval.GetCounter(PerformanceCounter.PendingActionsCurrent), "Interval retains newest gauge");
        Equal(7L, interval.GetCounter(PerformanceCounter.PendingActionsHighWater), "Interval retains maximum gauge");
        Equal(3L, interval.SimultaneousSpeakersCurrent, "Interval exposes current simultaneous speakers");
        Equal(10L, interval.SpeechTrackersCurrent, "Interval exposes current speech trackers");
        Equal(9L, interval.LookTrackersCurrent, "Interval exposes current look trackers");
        Equal(2L, interval.GetMetric(PerformanceMetric.DirectVisibilitySync).CallCount, "Interval metric calls");
        Near(25d, interval.FrameAverageMilliseconds, 0.000001d, "Interval merges frame histogram totals");
        Near(5d, interval.GetCounterPerSecond(PerformanceCounter.LogicalSyncRecords), 0.000001d, "Interval rate uses duration");
        Equal(12L, snapshot.LatestWindow.GetCounter(PerformanceCounter.LogicalSyncRecords), "Latest window remains distinct");
        Equal(20L, snapshot.Rolling60Seconds.GetCounter(PerformanceCounter.LogicalSyncRecords), "Rolling interval includes history");
        Equal(12, snapshot.HighWater.Players, "High water observes aggregate-only player count");
        True(interval.FramePercentilesLowConfidence, "Sparse frame percentiles are low confidence");
        True(
            interval.GetMetric(PerformanceMetric.DirectVisibilitySync).PercentilesLowConfidence,
            "Sparse metric percentiles are low confidence");
        True(
            interval.GetMetric(PerformanceMetric.DirectVisibilitySync).P50Milliseconds > 0d,
            "Metric snapshot exposes p50");
        True(
            interval.GetMetric(PerformanceMetric.DirectVisibilitySync).P99Milliseconds >=
            interval.GetMetric(PerformanceMetric.DirectVisibilitySync).P50Milliseconds,
            "Metric p99 is not below p50");
        Equal(5, snapshot.HighWater.SimultaneousSpeakers, "High water exposes simultaneous speakers");
        Equal(12, snapshot.HighWater.SpeechTrackers, "High water exposes speech trackers");
        Equal(11, snapshot.HighWater.LookTrackers, "High water exposes look trackers");
        Near(48d, snapshot.System.ActualTickRate, 0d, "Snapshot exposes actual tick rate");
        Near(60d, snapshot.System.TargetTickRate, 0d, "Snapshot exposes target tick rate");
        True(snapshot.System.ManagedHeapBytes >= 0L, "Snapshot exposes process managed heap");
        Equal(1L, snapshot.Transitions.Total, "Snapshot aggregates tier transitions");
        Equal(1L, snapshot.Transitions.Emergencies, "Snapshot aggregates emergency transitions");
        Equal(
            TalkyDiagnosticsLifecycleState.Enabled,
            snapshot.Identity.Lifecycle,
            "Snapshot exposes lifecycle identity");
        Equal(
            TalkyDiagnosticsTrustState.Verified,
            snapshot.Identity.Harmony,
            "Snapshot exposes Harmony trust identity");

        _measurementSink += snapshot.LatestWindow.GetCounter(PerformanceCounter.LogicalSyncRecords);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 10000; iteration++)
        {
            TalkyDiagnosticsSnapshot current = snapshot;
            _measurementSink += current.PublishedAtSeconds;
            _measurementSink += current.LatestWindow.LogicalSyncRecords;
            _measurementSink += current.Rolling60Seconds.GetMetric(
                PerformanceMetric.DirectVisibilitySync).AverageMilliseconds;
            _measurementSink += current.System.ProcessCpuPercent;
            _measurementSink += current.Transitions.Total;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Equal(0L, allocated, "Immutable snapshot reads allocate nothing");

        TalkyDiagnosticsSnapshot disabled = collector.PublishHeartbeat(
            6d,
            2,
            PerformanceMode.Disabled,
            PerformanceTier.Full,
            PerformanceTier.Full,
            false,
            false,
            false,
            actualTickRate: 45d,
            targetTickRate: 60d);
        True(!disabled.ProfilingEnabled, "Disabled heartbeat reports profiling state");
        Equal(0L, disabled.LatestWindow.WindowCount, "Disabled heartbeat invents no latest timing");
        Equal(2L, disabled.SinceEnable.WindowCount, "Disabled heartbeat retains since-enable history");
    }

    private static void TestRoundHighWaterResetPreservesTotals()
    {
        var profiler = new PerformanceProfiler(2, 60, 1000L, 0L);
        profiler.Increment(PerformanceCounter.VoicePackets, 4L);
        profiler.RecordFrameMilliseconds(10d);
        True(profiler.TryCloseWindow(2000L, out PerformanceWindow window), "Round reset source closes");

        var collector = new TalkyDiagnosticsCollector();
        collector.ObserveFrame(20, 6, 4);
        TalkyDiagnosticsSnapshot before = collector.Publish(
            profiler,
            window,
            2d,
            2,
            PerformanceMode.ObserveOnly,
            PerformanceTier.Full,
            PerformanceTier.Full,
            false,
            true,
            false);
        Equal(20, before.HighWater.Players, "Round high water records current round");
        Equal(4L, before.SinceEnable.GetCounter(PerformanceCounter.VoicePackets), "Round source total recorded");

        collector.ResetRoundHighWater();
        collector.ClearCompletedIntervals();
        TalkyDiagnosticsSnapshot reset = collector.PublishHeartbeat(
            2d,
            2,
            PerformanceMode.ObserveOnly,
            PerformanceTier.Full,
            PerformanceTier.Full,
            false,
            true,
            false);
        Equal(0, reset.HighWater.Players, "Round reset clears high water");
        Equal(4L, reset.SinceEnable.GetCounter(PerformanceCounter.VoicePackets), "Round reset preserves since-enable totals");

        collector.ObserveFrame(3, 2, 1);
        TalkyDiagnosticsSnapshot nextRound = collector.PublishHeartbeat(
            3d,
            2,
            PerformanceMode.ObserveOnly,
            PerformanceTier.Full,
            PerformanceTier.Full,
            false,
            true,
            false);
        Equal(3, nextRound.HighWater.Players, "Next round establishes independent high water");
    }

    private static void TestNonDivisorRollingHorizonNeverOverstatesDuration()
    {
        var profiler = new PerformanceProfiler(40, 120, 1000L, 0L);
        var collector = new TalkyDiagnosticsCollector();

        profiler.Increment(PerformanceCounter.SettingsResponses, 40L);
        profiler.RecordFrameMilliseconds(10d);
        True(profiler.TryCloseWindow(40000L, out PerformanceWindow first), "First 40-second window closes");
        collector.Publish(
            profiler,
            first,
            40d,
            40,
            PerformanceMode.ObserveOnly,
            PerformanceTier.Full,
            PerformanceTier.Full,
            false,
            true,
            false);

        profiler.Increment(PerformanceCounter.SettingsResponses, 80L);
        profiler.RecordFrameMilliseconds(10d);
        True(profiler.TryCloseWindow(80000L, out PerformanceWindow second), "Second 40-second window closes");
        TalkyDiagnosticsSnapshot snapshot = collector.Publish(
            profiler,
            second,
            80d,
            40,
            PerformanceMode.ObserveOnly,
            PerformanceTier.Full,
            PerformanceTier.Full,
            false,
            true,
            false);

        Equal(1L, snapshot.Rolling60Seconds.WindowCount, "Rolling horizon excludes partial older window");
        Near(40d, snapshot.Rolling60Seconds.DurationSeconds, 0d, "Rolling interval exposes exact covered duration");
        Equal(80L, snapshot.Rolling60Seconds.GetCounter(PerformanceCounter.SettingsResponses), "Rolling counts remain exact");
        Equal(120L, snapshot.SinceEnable.GetCounter(PerformanceCounter.SettingsResponses), "Since-enable retains both windows");
    }

    private static void TestExactCountersSurviveDisabledAndPartialWindows()
    {
        var collector = new TalkyDiagnosticsCollector();
        collector.RecordCounter(PerformanceCounter.VoiceInvalidPackets, 1L);
        collector.RecordCounter(PerformanceCounter.CleanupFailures, 2L);
        collector.RecordCounter(PerformanceCounter.PendingActionsCurrent, 4L);
        collector.RecordCounter(PerformanceCounter.PendingActionsCurrent, 1L);
        collector.RecordCounter(PerformanceCounter.PendingActionsHighWater, 7L);
        collector.RecordCounter(PerformanceCounter.PendingActionsHighWater, 3L);
        collector.RecordCounter(PerformanceCounter.VoicePayloadBytes1To64, 3L);
        collector.RecordCounter(PerformanceCounter.VoicePayloadBytes129To256, 2L);
        collector.RecordCounter(PerformanceCounter.SimultaneousSpeakersCurrent, 3L);
        collector.RecordCounter(PerformanceCounter.SpeechTrackersCurrent, 9L);
        collector.RecordCounter(PerformanceCounter.LookTrackersCurrent, 8L);
        collector.RecordCounter(PerformanceCounter.OwnedSubscriptionGroupsCurrent, 5L);

        TalkyDiagnosticsSnapshot disabled = collector.PublishHeartbeat(
            2d,
            2,
            PerformanceMode.Disabled,
            PerformanceTier.Full,
            PerformanceTier.Full,
            false,
            false,
            false);
        Equal(1L, disabled.SinceEnable.GetCounter(PerformanceCounter.VoiceInvalidPackets), "Disabled mode retains invalid voice total");
        Equal(2L, disabled.SinceEnable.GetCounter(PerformanceCounter.CleanupFailures), "Disabled mode retains cleanup failures");
        Equal(1L, disabled.SinceEnable.GetCounter(PerformanceCounter.PendingActionsCurrent), "Exact gauge retains latest value");
        Equal(7L, disabled.SinceEnable.GetCounter(PerformanceCounter.PendingActionsHighWater), "Exact maximum retains high water");
        Equal(3L, disabled.SinceEnable.VoicePayloadHistogram.Bytes1To64, "Payload histogram retains small packets");
        Equal(2L, disabled.SinceEnable.VoicePayloadHistogram.Bytes129To256, "Payload histogram retains medium packets");
        Equal(5L, disabled.SinceEnable.VoicePayloadHistogram.Total, "Payload histogram exposes total observations");
        Equal(3L, disabled.SinceEnable.SimultaneousSpeakersCurrent, "Disabled heartbeat exposes speaker gauge");
        Equal(9L, disabled.SinceEnable.SpeechTrackersCurrent, "Disabled heartbeat exposes speech trackers");
        Equal(8L, disabled.SinceEnable.LookTrackersCurrent, "Disabled heartbeat exposes look trackers");
        Equal(5L, disabled.SinceEnable.OwnedSubscriptionGroupsCurrent, "Disabled heartbeat exposes subscriptions");
        Equal(3, disabled.HighWater.SimultaneousSpeakers, "Disabled mode retains round speaker high water");
        Equal(9, disabled.HighWater.SpeechTrackers, "Disabled mode retains round speech-tracker high water");
        Equal(8, disabled.HighWater.LookTrackers, "Disabled mode retains round look-tracker high water");
        Equal(4L, disabled.HighWater.PendingActionsCurrentMaximum, "Disabled mode retains round queue high water");
        Equal(7L, disabled.HighWater.PendingActionsHighWater, "Disabled mode retains exact pending high water");

        collector.RecordCounter(PerformanceCounter.CleanupFailures, 1L);
        TalkyDiagnosticsSnapshot partial = collector.PublishHeartbeat(
            2d,
            2,
            PerformanceMode.ObserveOnly,
            PerformanceTier.Full,
            PerformanceTier.Full,
            false,
            true,
            false,
            retainCompletedIntervals: true);
        Equal(3L, partial.SinceEnable.GetCounter(PerformanceCounter.CleanupFailures), "Partial active window counter is immediately visible");

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 10000; iteration++)
            collector.RecordCounter(PerformanceCounter.SettingsResponses, 1L);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Equal(0L, allocated, "Exact counter hot path allocates nothing");
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
            throw new InvalidOperationException(name + ": expected " + expected + ", got " + actual);
    }

    private static void Near(double expected, double actual, double tolerance, string name)
    {
        _assertions++;
        if (double.IsNaN(actual) || Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException(name + ": expected " + expected + ", got " + actual);
    }

    private static void Throws<TException>(Action action, string name)
        where TException : Exception
    {
        _assertions++;
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(name);
    }
}
