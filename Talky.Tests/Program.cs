using System;
using System.Collections.Generic;
using Talky.Performance;
using Talky.Settings;
using Talky.Voice;

namespace Talky.Tests;

internal static class Program
{
    private static int _assertions;
    private static double _measurementSink;

    private static int Main()
    {
        try
        {
            TestRollingVoiceEnergy();
            _assertions += RollingSpeechPercentilesTests.Run();
            _assertions += CedModCompatibilityTests.Run();
            _assertions += VoiceSecurityPolicyTests.Run();
            _assertions += HardeningPolicyTests.Run();
            _assertions += TalkyDiagnosticsSnapshotTests.Run();
            TestVoicePresentationState();
            TestConfigurationAndPolicy();
            TestRecentSpeakerExpiry();
            TestGlanceSelectionEquivalence();
            TestGovernor();
            TestGovernorModesAndBoundaries();
            TestGovernorRecoveryHysteresis();
            TestGovernorRecoveryProbeLifecycle();
            TestGovernorFullRecoverySequence();
            TestProfiler();
            TestMetricCatalogAndSummary();
            TestPerformanceTransitionTracker();

            Console.WriteLine("PASS: " + _assertions + " deterministic assertions.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }

    private static void TestRollingVoiceEnergy()
    {
        const int capacity = 4800;
        RollingVoiceEnergy rolling = new RollingVoiceEnergy(capacity);
        float[] reference = new float[capacity];
        float[] packet = new float[480];
        Random random = new Random(1337);
        int writeIndex = 0;

        Equal(capacity, rolling.Capacity, "RMS capacity");
        Near(0d, rolling.RootMeanSquare, 0d, "Initial RMS");
        True(double.IsNegativeInfinity(rolling.Decibels), "Silence is negative infinity dB");

        for (int packetIndex = 0; packetIndex < 80; packetIndex++)
        {
            int count = 80 + random.Next(packet.Length - 79);
            for (int i = 0; i < count; i++)
                packet[i] = (float)(random.NextDouble() * 2d - 1d);

            rolling.Write(packet, count);
            for (int i = 0; i < count; i++)
            {
                float value = packet[i];
                reference[writeIndex] = value * value;
                writeIndex++;
                if (writeIndex == capacity)
                    writeIndex = 0;
            }

            double sum = 0d;
            for (int i = 0; i < reference.Length; i++)
                sum += reference[i];
            double expected = Math.Sqrt(sum / capacity);
            Near(expected, rolling.RootMeanSquare, 1e-8d, "Rolling RMS equivalence");
        }

        rolling.Reset();
        Near(0d, rolling.RootMeanSquare, 0d, "RMS reset");
        Throws<ArgumentOutOfRangeException>(() => rolling.Write(packet, -1), "RMS rejects negative count");

        rolling.Write(packet, packet.Length);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++)
        {
            rolling.Write(packet, packet.Length);
            _measurementSink += rolling.RootMeanSquare;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Equal(0L, allocated, "Rolling RMS steady-state allocation");
    }

    private static void TestVoicePresentationState()
    {
        VoicePresentationState presentation = new VoicePresentationState();

        Near(0d, presentation.RootMeanSquare, 0d, "Voice presentation starts silent");
        Near(-1d, presentation.LastActivitySeconds, 0d, "Voice presentation starts inactive");
        True(!presentation.WasRecentlyActive(0d, 500d), "Empty voice presentation is inactive");

        True(!presentation.TryPresent(0, 0.9d, 9d), "Failed decode does not refresh presentation");
        Near(0d, presentation.RootMeanSquare, 0d, "Failed decode leaves presentation silent");
        Near(-1d, presentation.LastActivitySeconds, 0d, "Failed decode leaves activity unchanged");

        True(presentation.TryPresent(480, 0.125d, 10d), "Decoded packet presents voice");
        Near(0.125d, presentation.RootMeanSquare, 0d, "Presentation copies decoded RMS");
        Near(10d, presentation.LastActivitySeconds, 0d, "Presentation copies packet time");
        True(presentation.WasRecentlyActive(10.499d, 500d), "Voice presentation active inside expiry");
        True(!presentation.WasRecentlyActive(10.501d, 500d), "Voice presentation expires outside duration");

        True(presentation.TryPresent(480, 0.75d, 11d), "Second source presents voice");
        Near(0.75d, presentation.RootMeanSquare, 0d, "Latest source snapshot wins shared proxy");
        Near(11d, presentation.LastActivitySeconds, 0d, "Latest source timestamp wins shared proxy");

        VoicePresentationState oldProxy = new VoicePresentationState();
        VoicePresentationState newProxy = new VoicePresentationState();
        oldProxy.TryPresent(480, 0.25d, 20d);
        newProxy.TryPresent(480, 0.5d, 20.1d);
        Near(0.25d, oldProxy.RootMeanSquare, 0d, "Old proxy retains its final snapshot");
        Near(0.5d, newProxy.RootMeanSquare, 0d, "New proxy receives the next snapshot");
        True(!oldProxy.WasRecentlyActive(20.501d, 500d), "Old proxy naturally expires");

        presentation.TryPresent(480, 0.1d, 30d);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++)
        {
            presentation.TryPresent(480, (i & 1) == 0 ? 0.1d : 0.9d, i);
            if (presentation.WasRecentlyActive(i, 500d))
                _measurementSink += presentation.RootMeanSquare;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Equal(0L, allocated, "Voice presentation steady-state allocation");

        presentation.Reset();
        Near(0d, presentation.RootMeanSquare, 0d, "Voice presentation reset clears RMS");
        Near(-1d, presentation.LastActivitySeconds, 0d, "Voice presentation reset clears activity");
    }

    private static void TestConfigurationAndPolicy()
    {
        PerformanceConfig defaults = new PerformanceConfig();
        Equal(PerformanceMode.Automatic, defaults.Mode, "Default mode");
        Equal(PerformanceTier.Full, defaults.FixedTier, "Default fixed tier");
        Near(1d, defaults.TalkyBudgetMsPerFrame, 0d, "Default soft budget");
        Near(3d, defaults.HardTalkyBudgetMsPerFrame, 0d, "Default hard budget");
        Near(25d, defaults.ServerP95DegradeMs, 0d, "Default server degrade");
        Near(33.3d, defaults.ServerP95EmergencyMs, 0d, "Default server emergency");
        Near(20d, defaults.ServerP95RecoveryMs, 0d, "Default server recovery");
        Equal(2, defaults.EvaluationWindowSeconds, "Default window");
        Equal(3, defaults.ConsecutiveBreachWindows, "Default soft windows");
        Equal(2, defaults.EmergencyBreachWindows, "Default emergency windows");
        Equal(15, defaults.WarmupSeconds, "Default warmup");
        Equal(20, defaults.TransitionCooldownSeconds, "Default transition cooldown");
        Equal(90, defaults.RecoveryHealthySeconds, "Default recovery duration");
        Near(0.5d, defaults.RecoveryBudgetRatio, 0d, "Default recovery ratio");
        Equal(60, defaults.RecoveryCooldownSeconds, "Default recovery cooldown");
        Equal(10, defaults.RecoveryProbeSeconds, "Default recovery probe");
        Equal(20, defaults.EconomyFaceHz, "Default economy face rate");
        Equal(10, defaults.EconomyGlanceHz, "Default economy glance rate");
        Equal(30, defaults.EconomyLookHz, "Default economy look rate");
        Equal(15, defaults.MinimumFaceHz, "Default minimum face rate");
        Equal(300, defaults.SummaryLogIntervalSeconds, "Default summary interval");

        PerformanceConfig invalid = new PerformanceConfig
        {
            TalkyBudgetMsPerFrame = double.NaN,
            HardTalkyBudgetMsPerFrame = -1d,
            ServerP95DegradeMs = 30d,
            ServerP95EmergencyMs = 10d,
            ServerP95RecoveryMs = 100d,
            EvaluationWindowSeconds = 0,
            RecoveryBudgetRatio = 2d,
            RecoveryProbeSeconds = 1,
            EconomyFaceHz = 8,
            MinimumFaceHz = 60,
            SummaryLogIntervalSeconds = -1,
        };
        PerformanceConfigValidationResult validation = PerformanceConfigValidator.Validate(invalid);
        True(validation.WasCorrected, "Invalid config reports corrections");
        True(validation.CorrectionSummary.Contains(nameof(invalid.TalkyBudgetMsPerFrame)), "Correction names budget");
        Near(1d, validation.Config.TalkyBudgetMsPerFrame, 0d, "NaN budget fallback");
        True(validation.Config.HardTalkyBudgetMsPerFrame >= validation.Config.TalkyBudgetMsPerFrame, "Hard >= soft");
        True(validation.Config.ServerP95EmergencyMs >= validation.Config.ServerP95DegradeMs, "Emergency >= degrade");
        True(validation.Config.ServerP95RecoveryMs <= validation.Config.ServerP95DegradeMs, "Recovery <= degrade");
        True(validation.Config.MinimumFaceHz <= validation.Config.EconomyFaceHz, "Floor rate <= economy");
        True(validation.Config.MinimumFaceHz >= 15, "Face floor cannot be configured below 15Hz");
        True(validation.Config.EconomyFaceHz >= 15, "Economy face rate cannot undercut hard floor");
        Equal(10, validation.Config.RecoveryProbeSeconds, "Recovery probe enforces ten-second minimum");
        Equal(0, validation.Config.SummaryLogIntervalSeconds, "Invalid summary interval falls back off");
        PerformanceConfig customProbe = new PerformanceConfig { RecoveryProbeSeconds = 45 };
        Equal(45, customProbe.Clone().RecoveryProbeSeconds, "Recovery probe clone preserves custom value");
        Equal(
            45,
            PerformanceConfigValidator.Validate(customProbe).Config.RecoveryProbeSeconds,
            "Recovery probe validation preserves in-range custom value");
        Equal(
            60,
            PerformanceConfigValidator.Validate(
                new PerformanceConfig { RecoveryProbeSeconds = 61 }).Config.RecoveryProbeSeconds,
            "Recovery probe stays within retained impact history");

        Config root = new Config();
        EffectivePerformancePolicy full = EffectivePerformancePolicy.Create(root, PerformanceTier.Full);
        True(full.FaceAnimationEnabled && full.DecodeVoiceEnabled, "Full face and decode");
        True(full.HeadBobbingEnabled && full.GlancingEnabled, "Full configured head features");
        Near(0.033d, full.FaceIntervalSeconds, 1e-7d, "Full configured face interval");
        Near(0.05d, full.GlanceIntervalSeconds, 1e-7d, "Full glance interval");
        Near(0d, full.LookIntervalSeconds, 0d, "Full look runs per frame");

        EffectivePerformancePolicy economy = EffectivePerformancePolicy.Create(root, PerformanceTier.Economy);
        Near(0.05d, economy.FaceIntervalSeconds, 1e-7d, "Economy face at most 20Hz");
        Near(0.10d, economy.GlanceIntervalSeconds, 1e-7d, "Economy glance at most 10Hz");
        Near(1d / 30d, economy.LookIntervalSeconds, 1e-7d, "Economy look at most 30Hz");
        True(!economy.SpectatorHeadMovementsEnabled, "Economy suppresses spectator delivery");

        EffectivePerformancePolicy noGlance = EffectivePerformancePolicy.Create(root, PerformanceTier.NoGlancing);
        True(!noGlance.GlancingEnabled && noGlance.HeadBobbingEnabled, "NoGlancing preserves head bob");

        EffectivePerformancePolicy faceOnly = EffectivePerformancePolicy.Create(root, PerformanceTier.FaceOnly);
        True(!faceOnly.HeadMovementEnabled, "FaceOnly suppresses head movement");
        True(faceOnly.FaceAnimationEnabled, "FaceOnly preserves face");

        EffectivePerformancePolicy floor = EffectivePerformancePolicy.Create(root, PerformanceTier.FaceOnlyReducedRate);
        Near(1d / 15d, floor.FaceIntervalSeconds, 1e-7d, "Face floor is 15Hz");
        True(floor.FaceAnimationEnabled && floor.DecodeVoiceEnabled, "Floor preserves face decode");

        Config invalidFloorRoot = new Config();
        invalidFloorRoot.Performance.EconomyFaceHz = 1;
        invalidFloorRoot.Performance.MinimumFaceHz = 1;
        Near(
            1d / 15d,
            EffectivePerformancePolicy.Create(invalidFloorRoot, PerformanceTier.FaceOnlyReducedRate).FaceIntervalSeconds,
            1e-7d,
            "Effective policy enforces 15Hz even before corrected config is applied");

        root.EnableHeadBobbing = false;
        root.EnableGlancing = false;
        root.EnabledSpectatorHeadMovements = false;
        root.EnableReactionOnHurt = false;
        root.EnableEmoteOnConsumables = false;
        full = EffectivePerformancePolicy.Create(root, PerformanceTier.Full);
        True(!full.HeadBobbingEnabled && !full.GlancingEnabled, "Policy never re-enables configured-off features");

        foreach (PerformanceTier tier in Enum.GetValues(typeof(PerformanceTier)))
        {
            EffectivePerformancePolicy policy = EffectivePerformancePolicy.Create(root, tier);
            True(policy.FaceAnimationEnabled && policy.DecodeVoiceEnabled, tier + " preserves voice-driven faces");
            True(!policy.HeadBobbingEnabled, tier + " does not re-enable head bob");
            True(!policy.GlancingEnabled, tier + " does not re-enable glancing");
            True(!policy.SpectatorHeadMovementsEnabled, tier + " does not re-enable spectator delivery");
            True(!policy.FaceReactionsEnabled, tier + " does not re-enable reactions");
            True(!policy.ConsumableEmotesEnabled, tier + " does not re-enable consumable emotes");
        }

        root.EnabledSpectatorHeadMovements = true;
        True(
            EffectivePerformancePolicy.Create(root, PerformanceTier.Full).SpectatorHeadMovementsEnabled,
            "Full tier respects configured spectator delivery");
        True(
            !EffectivePerformancePolicy.Create(root, PerformanceTier.Economy).SpectatorHeadMovementsEnabled,
            "Economy suppresses configured spectator delivery");
    }

    private static void TestRecentSpeakerExpiry()
    {
        const double now = 10d;
        const double durationMilliseconds = 1000d;

        True(!IsRecentSpeech(-1d, now, durationMilliseconds), "Never-spoken player is not recent");
        True(IsRecentSpeech(9d, now, durationMilliseconds), "Exact expiry boundary remains recent");
        True(!IsRecentSpeech(8.999d, now, durationMilliseconds), "Speaker expires immediately beyond boundary");
        True(IsRecentSpeech(9.75d, now, durationMilliseconds), "Speaker inside window remains recent");

        TestRecentSpeakerSet set = new TestRecentSpeakerSet();
        TestSpeaker exactBoundary = new TestSpeaker(1, 9d);
        TestSpeaker expired = new TestSpeaker(2, 8.999d);
        TestSpeaker newest = new TestSpeaker(3, 9.75d);
        TestSpeaker never = new TestSpeaker(4, -1d);

        True(set.Add(exactBoundary), "Recent set adds a speaker");
        True(set.Add(expired), "Recent set adds expired entry before pruning");
        True(set.Add(newest), "Recent set adds newest speaker");
        True(set.Add(never), "Recent set adds never-spoken entry before pruning");
        True(!set.Add(newest), "Recent set rejects duplicate speaker");

        set.Prune(now, durationMilliseconds);
        Equal(2, set.Count, "Prune keeps only recent speakers");
        True(set.Contains(exactBoundary) && set.Contains(newest), "Prune keeps boundary and newest speakers");
        Equal(-1, expired.Index, "Prune clears expired dense index");
        Equal(-1, never.Index, "Prune clears never-spoken dense index");
        set.AssertDenseIndices();

        exactBoundary.LastPacketSeconds = 8d;
        set.Prune(now, durationMilliseconds);
        Equal(1, set.Count, "Second prune expires prior boundary speaker");
        True(set.Contains(newest), "Swap removal preserves remaining speaker");
        Equal(0, newest.Index, "Swap removal repairs moved dense index");
        Equal(-1, exactBoundary.Index, "Swap removal clears removed dense index");
    }

    private static void TestGlanceSelectionEquivalence()
    {
        const double now = 20d;
        const double recentMilliseconds = 1000d;
        Random random = new Random(8128);

        for (int scene = 0; scene < 80; scene++)
        {
            int playerCount = 4 + random.Next(37);
            GlancePlayer[] players = new GlancePlayer[playerCount];
            for (int i = 0; i < players.Length; i++)
            {
                double x = random.NextDouble() * 18d - 9d;
                double y = random.NextDouble() * 4d - 2d;
                double z = random.NextDouble() * 18d - 9d;
                bool recent = random.Next(4) == 0;
                players[i] = new GlancePlayer(
                    i + 1,
                    x,
                    y,
                    z,
                    x,
                    y + 1.6d,
                    z,
                    recent ? now - random.NextDouble() * 0.999d : now - 1.001d - random.NextDouble(),
                    true);
            }

            int[] recentIndices = BuildRecentIndices(players, now, recentMilliseconds);
            for (int viewer = 0; viewer < players.Length; viewer++)
            {
                GlanceSelection brute = SelectGlanceBruteForce(
                    players,
                    viewer,
                    now,
                    recentMilliseconds,
                    7d,
                    140d);
                GlanceSelection indexed = SelectGlanceIndexed(
                    players,
                    recentIndices,
                    viewer,
                    now,
                    recentMilliseconds,
                    7d,
                    140d);

                Equal(brute.PlayerId, indexed.PlayerId, "Indexed glance chooses brute-force target");
                Near(brute.Score, indexed.Score, 1e-12d, "Indexed glance preserves target score");
                True(
                    indexed.CandidateVisits <= brute.CandidateVisits,
                    "Indexed glance never visits more candidates than brute force");
            }
        }

        GlancePlayer[] boundary =
        {
            new GlancePlayer(1, 0d, 0d, 0d, 0d, 1.6d, 0d, now, true),
            new GlancePlayer(2, 0d, 0d, 4d, 0d, 1.6d, 4d, now - 1d, true),
            new GlancePlayer(3, 0d, 0d, -2d, 0d, 1.6d, -2d, now, true),
            new GlancePlayer(4, 8d, 0d, 1d, 8d, 1.6d, 1d, now, true),
            new GlancePlayer(5, 1d, 0d, 3d, 1d, 1.6d, 3d, now - 1.001d, true),
        };
        int[] boundaryRecent = BuildRecentIndices(boundary, now, recentMilliseconds);
        GlanceSelection boundaryBrute = SelectGlanceBruteForce(
            boundary, 0, now, recentMilliseconds, 7d, 140d);
        GlanceSelection boundaryIndexed = SelectGlanceIndexed(
            boundary, boundaryRecent, 0, now, recentMilliseconds, 7d, 140d);
        Equal(2, boundaryBrute.PlayerId, "Boundary scene selects exact-expiry frontal speaker");
        Equal(boundaryBrute.PlayerId, boundaryIndexed.PlayerId, "Boundary indexed selection matches");
    }

    private static void TestGovernor()
    {
        PerformanceConfig config = FastGovernorConfig();
        AdaptivePerformanceGovernor governor = new AdaptivePerformanceGovernor(config, 0d);
        GovernorSample soft = new GovernorSample(1.1d, 16d, true);

        True(!governor.Evaluate(soft, 2d).Changed, "Soft breach 1 waits");
        True(!governor.Evaluate(soft, 4d).Changed, "Soft breach 2 waits");
        GovernorDecision decision = governor.Evaluate(soft, 6d);
        True(decision.Changed, "Soft breach 3 degrades");
        Equal(PerformanceTier.Economy, governor.CurrentTier, "Soft degradation one tier");
        Equal(GovernorReason.SoftTalkyBudget, decision.Reason, "Talky attribution");

        governor = new AdaptivePerformanceGovernor(config, 0d);
        decision = governor.Evaluate(new GovernorSample(3.1d, 16d, true), 2d);
        Equal(PerformanceTier.FaceOnly, governor.CurrentTier, "Hard Talky jump");
        Equal(GovernorReason.HardTalkyBudget, decision.Reason, "Hard Talky reason");
        decision = governor.Evaluate(new GovernorSample(3.1d, 16d, true), 4d);
        Equal(PerformanceTier.FaceOnlyReducedRate, governor.CurrentTier, "Continued hard pressure reaches face rate floor");
        decision = governor.Evaluate(new GovernorSample(3.1d, 16d, true), 6d);
        True(decision.PressureAtFloor && !decision.Changed, "Pressure at hard face floor is reported");

        governor = new AdaptivePerformanceGovernor(config, 0d);
        True(!governor.Evaluate(new GovernorSample(0.1d, 34d, true), 2d).Changed, "Server emergency waits one");
        decision = governor.Evaluate(new GovernorSample(0.1d, 34d, true), 4d);
        Equal(PerformanceTier.FaceOnly, governor.CurrentTier, "Server emergency jump");
        Equal(GovernorReason.ServerEmergency, decision.Reason, "Server-only attribution");

        governor.ResetForRound(10d);
        Equal(PerformanceTier.FaceOnly, governor.CurrentTier, "Round reset preserves tier");

        GovernorSample healthy = new GovernorSample(0.25d, 16d, true);
        True(!governor.Evaluate(healthy, 12d).Changed, "Recovery healthy window 1");
        decision = governor.Evaluate(healthy, 14d);
        True(decision.Changed, "Recovery probe starts after configured healthy duration");
        Equal(PerformanceTier.NoGlancing, governor.CurrentTier, "Recovery probe restores one tier");
        Equal(PerformanceTier.FaceOnly, governor.CommittedTier, "Recovery probe retains committed tier");
        True(governor.IsRecoveryProbeActive, "Recovery remains provisional during observation");

        governor.Evaluate(healthy, 16d);
        governor.Evaluate(healthy, 18d);
        governor.Evaluate(healthy, 20d);
        governor.Evaluate(healthy, 22d);
        decision = governor.Evaluate(healthy, 24d);
        Equal(GovernorReason.RecoveryProbeSucceeded, decision.Reason, "Recovery probe success event");
        True(!decision.Changed, "Committing a recovery probe does not reapply its policy");
        Equal(PerformanceTier.NoGlancing, governor.CommittedTier, "Successful probe commits restored tier");
        True(!governor.IsRecoveryProbeActive, "Successful probe clears provisional state");

        governor.Evaluate(new GovernorSample(double.NaN, 0d, false), 26d);
        True(governor.IsFrozen, "Invalid profiler sample freezes governor");
        Equal(PerformanceTier.NoGlancing, governor.CurrentTier, "Freeze preserves tier");

        config.Mode = PerformanceMode.Fixed;
        config.FixedTier = PerformanceTier.NoGlancing;
        governor.Reconfigure(config, 30d);
        True(!governor.IsFrozen, "Reconfigure clears fault freeze");
        Equal(PerformanceTier.NoGlancing, governor.CurrentTier, "Fixed mode applies tier");
        Equal(GovernorReason.FixedTier, governor.Evaluate(soft, 32d).Reason, "Fixed mode does not adapt");
    }

    private static void TestGovernorModesAndBoundaries()
    {
        GovernorSample hard = new GovernorSample(3.01d, 16d, true);

        PerformanceConfig warmupConfig = FastGovernorConfig();
        warmupConfig.WarmupSeconds = 4;
        AdaptivePerformanceGovernor governor = new AdaptivePerformanceGovernor(warmupConfig, 0d);
        Equal(GovernorReason.Warmup, governor.Evaluate(hard, 3.999d).Reason, "Warmup suppresses emergency transition");
        Equal(PerformanceTier.Full, governor.CurrentTier, "Warmup preserves full tier");
        True(governor.Evaluate(hard, 4d).Changed, "Warmup ends exactly at configured boundary");
        Equal(PerformanceTier.FaceOnly, governor.CurrentTier, "Post-warmup hard pressure jumps");

        governor.ResetForRound(5d);
        Equal(PerformanceTier.FaceOnly, governor.CurrentTier, "Round reset preserves degraded tier");
        Equal(GovernorReason.Warmup, governor.Evaluate(hard, 8d).Reason, "Round reset restarts warmup");
        governor.Evaluate(hard, 9d);
        Equal(PerformanceTier.FaceOnlyReducedRate, governor.CurrentTier, "Round warmup boundary allows further protection");

        PerformanceConfig disabled = FastGovernorConfig();
        disabled.Mode = PerformanceMode.Disabled;
        governor = new AdaptivePerformanceGovernor(disabled, 0d);
        Equal(GovernorReason.ModeDisabled, governor.Evaluate(hard, 100d).Reason, "Disabled mode reports disabled");
        Equal(PerformanceTier.Full, governor.CurrentTier, "Disabled mode never adapts");

        PerformanceConfig observe = FastGovernorConfig();
        observe.Mode = PerformanceMode.ObserveOnly;
        governor = new AdaptivePerformanceGovernor(observe, 0d);
        for (int i = 0; i < 10; i++)
            Equal(GovernorReason.ObserveOnly, governor.Evaluate(hard, i * 2d).Reason, "Observe-only mode reports observation");
        Equal(PerformanceTier.Full, governor.CurrentTier, "Observe-only mode never adapts");

        PerformanceConfig fixedConfig = FastGovernorConfig();
        fixedConfig.Mode = PerformanceMode.Fixed;
        fixedConfig.FixedTier = PerformanceTier.NoGlancing;
        governor = new AdaptivePerformanceGovernor(fixedConfig, 0d);
        Equal(PerformanceTier.NoGlancing, governor.CurrentTier, "Fixed mode starts at configured tier");
        for (int i = 0; i < 5; i++)
            governor.Evaluate(i % 2 == 0 ? hard : new GovernorSample(0d, 1d, true), i * 2d);
        Equal(PerformanceTier.NoGlancing, governor.CurrentTier, "Fixed mode ignores both pressure and health");

        PerformanceConfig serverConfig = FastGovernorConfig();
        governor = new AdaptivePerformanceGovernor(serverConfig, 0d);
        GovernorSample softServer = new GovernorSample(0.1d, 25.01d, true);
        governor.Evaluate(softServer, 2d);
        governor.Evaluate(softServer, 4d);
        GovernorDecision decision = governor.Evaluate(softServer, 6d);
        Equal(PerformanceTier.Economy, governor.CurrentTier, "Sustained soft server pressure degrades one tier");
        Equal(GovernorReason.SoftServerBudget, decision.Reason, "Soft server pressure remains server-attributed");

        governor = new AdaptivePerformanceGovernor(serverConfig, 0d);
        GovernorSample ineligibleServer = new GovernorSample(0.1d, 100d, false);
        for (int i = 1; i <= 6; i++)
            governor.Evaluate(ineligibleServer, i * 2d);
        Equal(PerformanceTier.Full, governor.CurrentTier, "Ineligible server frames never drive degradation");

        governor = new AdaptivePerformanceGovernor(serverConfig, 0d);
        decision = governor.Evaluate(
            new GovernorSample(serverConfig.HardTalkyBudgetMsPerFrame, 16d, true),
            2d);
        True(!decision.Changed, "Exact hard Talky threshold is not an emergency");
        Equal(GovernorReason.SoftTalkyBudget, decision.Reason, "Exact hard threshold remains a soft breach");

        governor = new AdaptivePerformanceGovernor(serverConfig, 0d);
        GovernorSample exactServerEmergency = new GovernorSample(
            0.1d,
            serverConfig.ServerP95EmergencyMs,
            true);
        governor.Evaluate(exactServerEmergency, 2d);
        governor.Evaluate(exactServerEmergency, 4d);
        Equal(PerformanceTier.Full, governor.CurrentTier, "Exact server emergency threshold does not emergency-jump");

        PerformanceConfig cooldown = FastGovernorConfig();
        cooldown.TransitionCooldownSeconds = 10;
        governor = new AdaptivePerformanceGovernor(cooldown, 0d);
        GovernorSample softTalky = new GovernorSample(1.1d, 16d, true);
        governor.Evaluate(softTalky, 2d);
        governor.Evaluate(softTalky, 4d);
        governor.Evaluate(softTalky, 6d);
        Equal(PerformanceTier.Economy, governor.CurrentTier, "Initial normal transition");
        governor.Evaluate(softTalky, 8d);
        governor.Evaluate(softTalky, 10d);
        decision = governor.Evaluate(softTalky, 12d);
        Equal(GovernorReason.Cooldown, decision.Reason, "Transition cooldown blocks second normal transition");
        Equal(PerformanceTier.Economy, governor.CurrentTier, "Cooldown holds tier");
        decision = governor.Evaluate(softTalky, 16d);
        True(decision.Changed, "Transition allowed exactly at cooldown boundary");
        Equal(PerformanceTier.NoGlancing, governor.CurrentTier, "Cooldown transition advances one tier");
    }

    private static void TestGovernorRecoveryHysteresis()
    {
        PerformanceConfig config = FastGovernorConfig();
        config.RecoveryHealthySeconds = 6;
        config.RecoveryCooldownSeconds = 4;
        AdaptivePerformanceGovernor governor = new AdaptivePerformanceGovernor(config, 0d);
        governor.Evaluate(new GovernorSample(3.1d, 16d, true), 0d);
        Equal(PerformanceTier.FaceOnly, governor.CurrentTier, "Recovery test begins degraded");

        governor.Evaluate(new GovernorSample(0.5d, 16d, true), 2d);
        governor.Evaluate(new GovernorSample(0.1d, 20d, true), 4d);
        governor.Evaluate(new GovernorSample(0.1d, 16d, false), 6d);
        Equal(PerformanceTier.FaceOnly, governor.CurrentTier, "Recovery thresholds are strict and require eligible server health");

        GovernorSample healthy = new GovernorSample(0.49d, 19.99d, true);
        governor.Evaluate(healthy, 8d);
        governor.Evaluate(healthy, 10d);
        governor.Evaluate(new GovernorSample(0.6d, 16d, true), 12d);
        governor.Evaluate(healthy, 14d);
        governor.Evaluate(healthy, 16d);
        GovernorDecision decision = governor.Evaluate(healthy, 18d);
        True(decision.Changed, "Continuous healthy duration starts recovery after interrupted evidence resets");
        Equal(GovernorReason.Recovery, decision.Reason, "Recovery transition reason");
        Equal(PerformanceTier.NoGlancing, governor.CurrentTier, "Recovery probe restores exactly one tier");
        Equal(PerformanceTier.FaceOnly, governor.CommittedTier, "Probe source remains committed");

        governor.Evaluate(healthy, 20d);
        governor.Evaluate(healthy, 22d);
        governor.Evaluate(healthy, 24d);
        governor.Evaluate(healthy, 26d);
        decision = governor.Evaluate(healthy, 28d);
        Equal(GovernorReason.RecoveryProbeSucceeded, decision.Reason, "Ten healthy probe seconds commit recovery");
        Equal(PerformanceTier.NoGlancing, governor.CommittedTier, "Committed tier advances once");

        governor.Evaluate(healthy, 30d);
        governor.Evaluate(healthy, 32d);
        governor.Evaluate(healthy, 34d);
        governor.Evaluate(healthy, 36d);
        decision = governor.Evaluate(healthy, 38d);
        True(decision.Changed, "Next recovery waits for the ten-second retry gate");
        Equal(PerformanceTier.Economy, governor.CurrentTier, "Next probe restores one additional tier");

        decision = governor.Evaluate(new GovernorSample(1.1d, 16d, true), 40d);
        True(decision.Changed, "Over-budget recovery probe rolls back immediately");
        Equal(GovernorReason.RecoveryProbeFailed, decision.Reason, "Probe rollback reason");
        Equal(PerformanceTier.NoGlancing, governor.CurrentTier, "Failed probe restores committed tier");
        Equal(PerformanceTier.NoGlancing, governor.CommittedTier, "Failed probe does not commit");
        True(!governor.IsRecoveryProbeActive, "Failed probe clears provisional state");

        governor.Evaluate(healthy, 42d);
        governor.Evaluate(healthy, 44d);
        governor.Evaluate(healthy, 46d);
        True(!governor.Evaluate(healthy, 48d).Changed, "Failed probe cannot retry before ten seconds");
        True(governor.Evaluate(healthy, 50d).Changed, "Failed probe retries at ten-second boundary");
    }

    private static void TestGovernorRecoveryProbeLifecycle()
    {
        PerformanceConfig config = FastGovernorConfig();
        config.RecoveryHealthySeconds = 2;
        config.TransitionCooldownSeconds = 300;
        AdaptivePerformanceGovernor governor = new AdaptivePerformanceGovernor(config, 0d);
        GovernorSample healthy = new GovernorSample(0.25d, 16d, true, true, 2d);

        governor.Evaluate(new GovernorSample(3.1d, 16d, true), 0d);
        GovernorDecision decision = governor.Evaluate(healthy, 2d);
        True(decision.Changed && governor.IsRecoveryProbeActive, "Healthy headroom starts probe");
        Equal(PerformanceTier.FaceOnly, governor.CommittedTier, "Probe starts from committed floor");

        decision = governor.Evaluate(
            new GovernorSample(config.TalkyBudgetMsPerFrame, config.ServerP95DegradeMs, true, true, 2d),
            4d);
        Equal(GovernorReason.RecoveryProbe, decision.Reason, "Exact soft budgets remain probe-healthy");

        decision = governor.Evaluate(new GovernorSample(1.01d, 16d, true, true, 2d), 6d);
        Equal(GovernorReason.RecoveryProbeFailed, decision.Reason, "Soft probe miss bypasses ordinary cooldown");
        Equal(PerformanceTier.FaceOnly, governor.CurrentTier, "Soft probe miss rolls back source tier");

        governor.Evaluate(healthy, 8d);
        governor.Evaluate(healthy, 10d);
        governor.Evaluate(healthy, 12d);
        governor.Evaluate(healthy, 14d);
        True(!governor.Evaluate(healthy, 15.999d).Changed, "Retry gate is strict before ten seconds");
        decision = governor.Evaluate(healthy, 16d);
        True(decision.Changed && governor.IsRecoveryProbeActive, "Retry gate opens exactly at ten seconds");

        governor.Evaluate(healthy, 18d);
        governor.Evaluate(healthy, 20d);
        governor.Evaluate(healthy, 22d);
        governor.Evaluate(healthy, 24d);
        decision = governor.Evaluate(healthy, 26d);
        Equal(GovernorReason.RecoveryProbeSucceeded, decision.Reason, "Probe commits after ten eligible seconds");

        governor.Evaluate(healthy, 28d);
        governor.Evaluate(healthy, 30d);
        governor.Evaluate(healthy, 32d);
        governor.Evaluate(healthy, 34d);
        decision = governor.Evaluate(healthy, 36d);
        True(decision.Changed && governor.IsRecoveryProbeActive, "Next tier begins as a probe");
        PerformanceTier committedBeforeReset = governor.CommittedTier;
        governor.ResetForRound(37d);
        True(!governor.IsRecoveryProbeActive, "Round reset cancels active recovery probe");
        Equal(committedBeforeReset, governor.CurrentTier, "Round reset rolls back uncommitted tier");

        decision = governor.Evaluate(healthy, 39d);
        True(decision.Changed && governor.IsRecoveryProbeActive, "Recovery can probe again after round reset");
        governor.Freeze("test");
        True(governor.IsFrozen && !governor.IsRecoveryProbeActive, "Freeze cancels active recovery probe");
        Equal(governor.CommittedTier, governor.CurrentTier, "Freeze retains only committed tier");

        governor.Reconfigure(config, 50d);
        decision = governor.Evaluate(healthy, 52d);
        True(decision.Changed && governor.IsRecoveryProbeActive, "Reconfigure restarts recovery evidence");
        governor.Reconfigure(config, 53d);
        True(!governor.IsRecoveryProbeActive, "Reconfigure cancels active recovery probe");
        Equal(governor.CommittedTier, governor.CurrentTier, "Reconfigure rolls back provisional tier");

        governor.Evaluate(healthy, 55d);
        governor.Evaluate(healthy, 57d);
        True(governor.IsRecoveryProbeActive, "Probe restarts for ineligible-window test");
        governor.Evaluate(healthy, 59d);
        governor.Evaluate(healthy, 61d);
        governor.Evaluate(healthy, 63d);
        Equal(
            GovernorReason.RecoveryProbe,
            governor.Evaluate(new GovernorSample(0.25d, 0d, false, true, 2d), 65d).Reason,
            "Ineligible window does not commit probe");
        governor.Evaluate(healthy, 67d);
        governor.Evaluate(healthy, 69d);
        governor.Evaluate(healthy, 71d);
        governor.Evaluate(healthy, 73d);
        decision = governor.Evaluate(healthy, 75d);
        Equal(GovernorReason.RecoveryProbeSucceeded, decision.Reason, "Probe needs ten fresh eligible seconds");

        config = FastGovernorConfig();
        config.RecoveryHealthySeconds = 2;
        governor = new AdaptivePerformanceGovernor(config, 0d);
        governor.Evaluate(new GovernorSample(3.1d, 16d, true), 0d);
        governor.Evaluate(healthy, 2d);
        decision = governor.Evaluate(
            new GovernorSample(0.25d, 16d, true, true, 10d),
            2.1d);
        Equal(GovernorReason.RecoveryProbe, decision.Reason, "Probe cannot commit before wall-clock minimum");
        decision = governor.Evaluate(healthy, 12d);
        Equal(GovernorReason.RecoveryProbeSucceeded, decision.Reason, "Probe requires sampled and elapsed duration");

        config = FastGovernorConfig();
        config.RecoveryHealthySeconds = 10;
        governor = new AdaptivePerformanceGovernor(config, 0d);
        governor.Evaluate(new GovernorSample(3.1d, 16d, true), 0d);
        for (int windowIndex = 1; windowIndex < 5; windowIndex++)
        {
            True(
                !governor.Evaluate(healthy, windowIndex * 2d).Changed,
                "Ten-second recovery gate waits for all five windows");
        }
        decision = governor.Evaluate(healthy, 10d);
        True(decision.Changed, "Ten-second recovery gate opens on fifth window");

        config = FastGovernorConfig();
        config.RecoveryHealthySeconds = 2;
        governor = new AdaptivePerformanceGovernor(config, 0d);
        governor.Evaluate(new GovernorSample(3.1d, 16d, true), 0d);
        governor.Evaluate(healthy, 2d);
        GovernorSample serverEmergency =
            new GovernorSample(0.25d, 34d, true, true, 2d);
        decision = governor.Evaluate(serverEmergency, 4d);
        Equal(GovernorReason.RecoveryProbeFailed, decision.Reason, "First server emergency rejects probe");
        decision = governor.Evaluate(serverEmergency, 6d);
        Equal(GovernorReason.ServerEmergency, decision.Reason, "Emergency evidence survives probe rollback");
        Equal(PerformanceTier.FaceOnlyReducedRate, governor.CurrentTier, "Second emergency reaches floor");

        governor = new AdaptivePerformanceGovernor(config, 0d);
        governor.Evaluate(new GovernorSample(3.1d, 16d, true), 0d);
        governor.Evaluate(healthy, 2d);
        decision = governor.Evaluate(new GovernorSample(3.1d, 16d, true, true, 2d), 4d);
        Equal(GovernorReason.HardTalkyBudget, decision.Reason, "Hard pressure interrupts recovery probe");
        Equal(PerformanceTier.FaceOnly, governor.CurrentTier, "Hard probe interruption commits protected tier");

        governor = new AdaptivePerformanceGovernor(config, 0d);
        governor.Evaluate(new GovernorSample(3.1d, 16d, true), 0d);
        governor.Evaluate(healthy, 2d);
        decision = governor.Evaluate(
            new GovernorSample(0.25d, 16d, true, true, double.NaN),
            4d);
        Equal(GovernorReason.InvalidProfile, decision.Reason, "Invalid probe duration freezes governor");
        True(governor.IsFrozen, "Invalid probe duration enters fail-safe");
        Equal(governor.CommittedTier, governor.CurrentTier, "Invalid probe duration rolls back provisional tier");
    }

    private static void TestGovernorFullRecoverySequence()
    {
        PerformanceConfig config = FastGovernorConfig();
        config.RecoveryHealthySeconds = 2;
        AdaptivePerformanceGovernor governor = new AdaptivePerformanceGovernor(config, 0d);
        GovernorSample healthy = new GovernorSample(0.25d, 16d, true, true, 2d);

        governor.Evaluate(new GovernorSample(3.1d, 16d, true), 0d);
        governor.Evaluate(new GovernorSample(3.1d, 16d, true), 2d);
        Equal(
            PerformanceTier.FaceOnlyReducedRate,
            governor.CommittedTier,
            "Full recovery sequence starts at facial rate floor");

        Config root = new Config
        {
            Performance = config,
            EnabledSpectatorHeadMovements = true,
        };
        PerformanceTier[] restoredTiers =
        {
            PerformanceTier.FaceOnly,
            PerformanceTier.NoGlancing,
            PerformanceTier.Economy,
            PerformanceTier.Full,
        };

        double now = 2d;
        for (int tierIndex = 0; tierIndex < restoredTiers.Length; tierIndex++)
        {
            int guard = 0;
            GovernorDecision decision;
            do
            {
                now += 2d;
                decision = governor.Evaluate(healthy, now);
                guard++;
                if (guard > 10)
                    throw new InvalidOperationException("Recovery probe did not start in bounded time.");
            }
            while (!decision.Changed);

            PerformanceTier target = restoredTiers[tierIndex];
            Equal(target, governor.CurrentTier, "Recovery sequence probes expected next tier");
            True(governor.IsRecoveryProbeActive, "Recovery sequence tier is provisional");

            for (int probeWindow = 0; probeWindow < 5; probeWindow++)
            {
                now += 2d;
                decision = governor.Evaluate(healthy, now);
            }

            Equal(
                GovernorReason.RecoveryProbeSucceeded,
                decision.Reason,
                "Recovery sequence commits after ten seconds");
            Equal(target, governor.CommittedTier, "Recovery sequence commits expected tier");

            EffectivePerformancePolicy policy =
                EffectivePerformancePolicy.Create(root, target);
            switch (target)
            {
                case PerformanceTier.FaceOnly:
                    True(!policy.HeadMovementEnabled, "FaceOnly recovery keeps head motion off");
                    Near(1d / 20d, policy.FaceIntervalSeconds, 1e-7d, "FaceOnly restores economy face rate");
                    break;
                case PerformanceTier.NoGlancing:
                    True(policy.HeadBobbingEnabled, "NoGlancing recovery restores head bob");
                    True(!policy.GlancingEnabled, "NoGlancing recovery still suppresses glancing");
                    break;
                case PerformanceTier.Economy:
                    True(policy.GlancingEnabled, "Economy recovery restores glancing");
                    True(!policy.SpectatorHeadMovementsEnabled, "Economy recovery keeps spectator sync off");
                    break;
                case PerformanceTier.Full:
                    True(policy.SpectatorHeadMovementsEnabled, "Full recovery restores configured spectator sync");
                    Near(0.033d, policy.FaceIntervalSeconds, 1e-7d, "Full recovery restores configured face rate");
                    break;
            }
        }
    }

    private static void TestProfiler()
    {
        PerformanceProfiler profiler = new PerformanceProfiler(2, 60, 1000L, 0L);
        Equal(30, profiler.HistoryCapacity, "Profiler retains 60 seconds of completed windows");
        profiler.RecordDurationTicks(PerformanceMetric.VoicePacket, 10L);
        profiler.RecordDurationTicks(PerformanceMetric.VoicePacket, 20L);
        profiler.RecordDurationTicks(PerformanceMetric.OpusDecode, 100L);
        profiler.RecordDurationTicks(PerformanceMetric.Governor, 100L);
        profiler.RecordDurationTicks(PerformanceMetric.ProfilerOverhead, 100L);
        profiler.RecordDurationTicks(PerformanceMetric.CedModPostfix, 5L);
        profiler.Increment(PerformanceCounter.VoicePackets, 2L);
        profiler.Increment(PerformanceCounter.DecodedSamples, 960L);
        profiler.RecordFrameMilliseconds(10d);
        profiler.RecordFrameMilliseconds(30d);

        True(!profiler.TryCloseWindow(1999L, out _), "Window remains open before duration");
        True(profiler.TryCloseWindow(2000L, out PerformanceWindow window), "Window closes on duration");
        Equal(2L, window.GetMetric(PerformanceMetric.VoicePacket).CallCount, "Profiler metric calls");
        Equal(2L, window.GetMetric(PerformanceMetric.VoicePacket).SampleCount, "Profiler metric samples");
        Near(30d, window.GetMetric(PerformanceMetric.VoicePacket).EstimatedTotalMilliseconds, 1e-9d, "Profiler total");
        Near(35d, window.TotalTalkyMilliseconds, 1e-9d, "Nested and controller costs excluded");
        Near(17.5d, window.TalkyMillisecondsPerFrame, 1e-9d, "Talky milliseconds per frame");
        True(
            PerformanceWindow.CountsTowardTalkyBudget(PerformanceMetric.CedModPostfix),
            "CedMod postfix contributes to Talky budget");
        Near(20d, window.FrameAverageMilliseconds, 1e-9d, "Frame average");
        Equal(2L, window.GetCounter(PerformanceCounter.VoicePackets), "Profiler counter");
        Equal(960L, window.GetCounter(PerformanceCounter.DecodedSamples), "Profiler decoded samples");
        True(window.FrameP95Milliseconds >= 30d, "Frame p95 histogram upper bound");
        True(window.GetMetric(PerformanceMetric.VoicePacket).P95Milliseconds >= 20d, "Metric p95 histogram upper bound");
        True(ReferenceEquals(window, profiler.LatestCompleted), "Latest completed window");
        True(ReferenceEquals(window, profiler.GetCompletedWindow(0)), "History newest window");

        profiler.Reset(5000L);
        True(profiler.LatestCompleted == null, "Profiler reset clears completed window");
        True(!profiler.TryCloseWindow(6999L, out _), "Profiler reset starts new duration");

        PerformanceProfiler sampled = new PerformanceProfiler(2, 60, 1000L, 0L);
        for (int i = 0; i < 128; i++)
        {
            long started = sampled.BeginSample(PerformanceMetric.DirectRoleSync, 64);
            sampled.EndSample(PerformanceMetric.DirectRoleSync, started);
        }
        True(sampled.TryCloseWindow(2000L, out PerformanceWindow sampledWindow), "Sampled window closes");
        Equal(128L, sampledWindow.GetMetric(PerformanceMetric.DirectRoleSync).CallCount, "Sampling counts every call");
        Equal(2L, sampledWindow.GetMetric(PerformanceMetric.DirectRoleSync).SampleCount, "Sampling times one in 64");

        PerformanceProfiler cedModSamples = new PerformanceProfiler(2, 60, 1000L, 0L);
        for (int i = 0; i < 128; i++)
        {
            long prefixStarted = cedModSamples.BeginSample(PerformanceMetric.CedModRoleSync, 64);
            cedModSamples.EndSample(PerformanceMetric.CedModRoleSync, prefixStarted);
            long postfixStarted = cedModSamples.BeginSample(PerformanceMetric.CedModPostfix, 64);
            cedModSamples.EndSample(PerformanceMetric.CedModPostfix, postfixStarted);
        }
        True(cedModSamples.TryCloseWindow(2000L, out PerformanceWindow cedModWindow), "CedMod sample window closes");
        Equal(128L, cedModWindow.GetMetric(PerformanceMetric.CedModRoleSync).CallCount, "CedMod prefix counts every call");
        Equal(2L, cedModWindow.GetMetric(PerformanceMetric.CedModRoleSync).SampleCount, "CedMod prefix owns 1/64 stream");
        Equal(128L, cedModWindow.GetMetric(PerformanceMetric.CedModPostfix).CallCount, "CedMod postfix counts every call");
        Equal(2L, cedModWindow.GetMetric(PerformanceMetric.CedModPostfix).SampleCount, "CedMod postfix owns independent 1/64 stream");

        sampled.Reset();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++)
        {
            long started = sampled.BeginSample(PerformanceMetric.DirectRoleSync, 64);
            sampled.EndSample(PerformanceMetric.DirectRoleSync, started);
            sampled.Increment(PerformanceCounter.SyncEntries);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Equal(0L, allocated, "Profiler steady-state allocation");

        PerformanceProfiler aggregateSource = new PerformanceProfiler(2, 60, 1000L, 0L);
        for (int i = 0; i < 100; i++)
            aggregateSource.RecordFrameMilliseconds(10d);
        True(aggregateSource.TryCloseWindow(2000L, out PerformanceWindow denseWindow), "Dense frame window closes");
        aggregateSource.RecordFrameMilliseconds(100d);
        True(aggregateSource.TryCloseWindow(4000L, out PerformanceWindow sparseWindow), "Sparse frame window closes");

        FrameHistogramAccumulator aggregate = new FrameHistogramAccumulator();
        aggregate.Add(denseWindow);
        aggregate.Add(sparseWindow);
        Equal(101L, aggregate.FrameCount, "Summary histogram weights every raw frame");
        Near((100d * 10d + 100d) / 101d, aggregate.AverageMilliseconds, 1e-9d, "Summary frame average");
        Near(10.25d, aggregate.P50Milliseconds, 1e-9d, "Summary p50 from merged bins");
        Near(10.25d, aggregate.P95Milliseconds, 1e-9d, "Summary p95 from merged bins");
        Near(10.25d, aggregate.P99Milliseconds, 1e-9d, "Summary p99 from merged bins");
        Near(100d, aggregate.MaxMilliseconds, 1e-9d, "Summary frame maximum");
        True(
            Math.Abs(aggregate.P95Milliseconds -
                     (denseWindow.FrameP95Milliseconds + sparseWindow.FrameP95Milliseconds) / 2d) > 1d,
            "Merged p95 is not an invalid average of window percentiles");

        PerformanceProfiler tailSource = new PerformanceProfiler(2, 60, 1000L, 0L);
        for (int i = 0; i < 95; i++)
            tailSource.RecordFrameMilliseconds(129d);
        for (int i = 0; i < 5; i++)
            tailSource.RecordFrameMilliseconds(10000d);
        True(tailSource.TryCloseWindow(2000L, out PerformanceWindow tailWindow), "Tail frame window closes");
        True(
            tailWindow.FrameP95Milliseconds >= 129d &&
            tailWindow.FrameP95Milliseconds < 1000d,
            "Tail p95 is not replaced by an unrelated extreme maximum");
        True(
            tailWindow.FrameP99Milliseconds < tailWindow.FrameMaxMilliseconds,
            "Saturated percentile reports a lower bound while maximum stays separate");
        Near(10000d, tailWindow.FrameMaxMilliseconds, 1e-9d, "Tail frame maximum remains exact");

        aggregate.Reset();
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            aggregate.Reset();
            aggregate.Add(denseWindow);
            aggregate.Add(sparseWindow);
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Equal(0L, allocated, "Summary histogram accumulation is allocation-free");
        Throws<ArgumentNullException>(() => aggregate.Add(null), "Summary histogram rejects null window");
    }

    private static void TestMetricCatalogAndSummary()
    {
        bool[] budgeted =
        {
            true, false, false, true, true, false,
            true, true, true, true, false, false,
            true, false, false, false, false, false,
            true, false, false, false, false,
        };
        int[] sampleEvery =
        {
            8, 8, 8, 1, 1, 1,
            64, 64, 64, 1, 1, 1,
            64, 8, 8, 8, 8, 8,
            8, 8, 1, 1, 1,
        };
        PerformanceSubsystem[] subsystem =
        {
            PerformanceSubsystem.Voice,      // VoicePacket
            PerformanceSubsystem.None,       // OpusDecode
            PerformanceSubsystem.None,       // EnergyUpdate
            PerformanceSubsystem.Face,       // FaceBatch
            PerformanceSubsystem.Look,       // LookBatch
            PerformanceSubsystem.None,       // GlanceBatch
            PerformanceSubsystem.Sync,       // DirectRoleSync
            PerformanceSubsystem.Sync,       // CedModRoleSync
            PerformanceSubsystem.Sync,       // CedModPostfix
            PerformanceSubsystem.Scheduler,  // SchedulerOverhead
            PerformanceSubsystem.Monitoring, // Governor
            PerformanceSubsystem.Monitoring, // ProfilerOverhead
            PerformanceSubsystem.Sync,       // DirectVisibilitySync
            PerformanceSubsystem.None,       // DirectVisibilityActive
            PerformanceSubsystem.None,       // DirectRoleSyncActive
            PerformanceSubsystem.None,       // DirectRoleSyncRewrite
            PerformanceSubsystem.None,       // CedModRoleSyncActive
            PerformanceSubsystem.None,       // CedModPostfixRewrite
            PerformanceSubsystem.Scheduler,  // SettingsResponse
            PerformanceSubsystem.None,       // AnimationRpc
            PerformanceSubsystem.None,       // PendingActionDrain
            PerformanceSubsystem.None,       // Lifecycle
            PerformanceSubsystem.None,       // ConfigReload
        };

        for (int index = 0; index < (int)PerformanceMetric.Count; index++)
        {
            PerformanceMetric metric = (PerformanceMetric)index;
            PerformanceMetricDefinition definition = PerformanceMetricCatalog.Get(metric);
            Equal(budgeted[index], definition.CountsTowardTalkyBudget, metric + " budget mapping");
            Equal(sampleEvery[index], definition.DefaultSampleEvery, metric + " sample cadence");
            Equal(subsystem[index], definition.SummarySubsystem, metric + " summary group");
            Equal(
                budgeted[index],
                PerformanceWindow.CountsTowardTalkyBudget(metric),
                metric + " profiler budget mapping");
        }

        Throws<ArgumentOutOfRangeException>(
            () => PerformanceMetricCatalog.Get(PerformanceMetric.Count),
            "Metric catalog rejects sentinel");
        True(
            !PerformanceWindow.CountsTowardTalkyBudget(PerformanceMetric.Count),
            "Profiler budget wrapper preserves invalid-metric behavior");

        PerformanceProfiler profiler = new PerformanceProfiler(2, 60, 1000L, 0L);
        for (int index = 0; index < (int)PerformanceMetric.Count; index++)
            profiler.RecordDurationTicks((PerformanceMetric)index, index + 1L);
        for (int index = 0; index < (int)PerformanceCounter.Count; index++)
            profiler.Increment((PerformanceCounter)index, index + 1L);
        profiler.RecordFrameMilliseconds(10d);
        profiler.RecordFrameMilliseconds(20d);
        True(profiler.TryCloseWindow(2000L, out PerformanceWindow window), "Summary source closes");

        PerformanceBreakdown breakdown = PerformanceBreakdown.FromWindow(window);
        Near(1d, breakdown.VoiceMilliseconds, 0d, "Summary voice excludes nested decode");
        Near(4d, breakdown.FaceMilliseconds, 0d, "Summary face group");
        Near(5d, breakdown.LookMilliseconds, 0d, "Summary look excludes nested glance");
        Near(29d, breakdown.SchedulerMilliseconds, 0d, "Summary scheduler group includes settings");
        Near(37d, breakdown.SyncMilliseconds, 0d, "Summary combines all sync paths");
        Near(23d, breakdown.MonitoringMilliseconds, 0d, "Summary combines monitor overhead");

        PerformanceSummaryAccumulator summary = new PerformanceSummaryAccumulator();
        summary.Reset(100d);
        summary.RecordEligibleFrame(10, 2, 3, 20d, 50, true);
        summary.RecordEligibleFrame(20, 4, 5, 25d, 40, true);
        summary.AddWindow(window, breakdown);

        Near(100d, summary.StartedAt, 0d, "Summary start time");
        Equal(1L, summary.WindowCount, "Summary window count");
        Equal(2L, summary.FrameCount, "Summary frame count");
        Near(76d, summary.TotalTalkyMilliseconds, 0d, "Summary budget total");
        Near(38d, summary.TalkyMillisecondsPerFrame, 0d, "Summary Talky per frame");
        Near(38d, summary.TalkyMillisecondsPerSecond, 0d, "Summary Talky per second");
        Near(15d, summary.AveragePlayers, 0d, "Summary average players");
        Near(3d, summary.AverageRecentSpeakers, 0d, "Summary average speakers");
        Near(4d, summary.AverageActiveLooks, 0d, "Summary average active looks");
        Near(1000d / 22.5d, summary.SmoothTickrate, 1e-9d, "Summary smooth tickrate");
        Near(45d, summary.TargetTickrate, 0d, "Summary target tickrate");
        Near(
            18.5d,
            summary.GetSubsystemMillisecondsPerFrame(PerformanceSubsystem.Sync),
            0d,
            "Summary sync per frame");
        Near(
            100d / 76d,
            summary.GetSubsystemShare(PerformanceSubsystem.Voice),
            1e-9d,
            "Summary subsystem share");
        Equal(1L, summary.GetCounter(PerformanceCounter.VoicePackets), "Summary voice counter");
        Equal(10L, summary.GetCounter(PerformanceCounter.EmotionWrites), "Summary emotion counter");
        summary.ObserveTier(PerformanceTier.Full);
        summary.ObserveTier(PerformanceTier.Economy);
        Equal(PerformanceTier.Full, summary.FirstObservedTier, "Summary first observed tier");
        Equal(PerformanceTier.Economy, summary.LastObservedTier, "Summary last observed tier");
        Equal(1L, summary.PolicyChangeCount, "Summary policy change count");

        string message = summary.CreateLogMessage(
            PerformanceMode.Automatic,
            PerformanceTier.Full,
            PerformanceTier.Economy,
            GovernorReason.SoftTalkyBudget);
        True(message.Contains("effective_tier=Economy"), "Summary message effective tier");
        True(
            message.Contains("observed_tier_span=Full->Economy, policy_changes=1"),
            "Summary message identifies mixed-tier evidence");
        True(
            message.Contains($"voice={0.5d:F3} ({100d / 76d:F1}%)"),
            "Summary message voice field");
        True(
            message.Contains($"frame_monitor_lower_bound={11.5d:F3} ms/frame"),
            "Summary message monitoring lower-bound field");
        True(
            !message.Contains("profiler="),
            "Summary does not imply the frame monitor is total profiler overhead");
        True(message.Contains("sync_entries=7"), "Summary message counter values");
        string probeMessage = summary.CreateLogMessage(
            PerformanceMode.Automatic,
            PerformanceTier.Full,
            PerformanceTier.NoGlancing,
            PerformanceTier.FaceOnly,
            true,
            GovernorReason.Recovery);
        True(probeMessage.Contains("committed_tier=FaceOnly"), "Summary message committed tier");
        True(probeMessage.Contains("recovery_probe=active"), "Summary message recovery probe state");
        summary.ObserveTier(PerformanceTier.Full);
        string roundTripMessage = summary.CreateLogMessage(
            PerformanceMode.Automatic,
            PerformanceTier.Full,
            PerformanceTier.Full,
            GovernorReason.Recovery);
        True(
            roundTripMessage.Contains("observed_tier_span=Full->Full, policy_changes=2"),
            "Summary preserves round-trip policy changes");

        summary.Reset(200d);
        summary.RecordEligibleFrame(1, 0, 0, double.NaN, 0, false);
        Near(0d, summary.SmoothTickrate, 0d, "Summary ignores unavailable smooth tickrate");
        summary.Reset(200d);
        summary.AddWindow(window, breakdown);
        summary.RecordEligibleFrame(10, 2, 3, 20d, 50, true);
        summary.Reset(200d);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1000; iteration++)
        {
            breakdown = PerformanceBreakdown.FromWindow(window);
            summary.AddWindow(window, breakdown);
            summary.RecordEligibleFrame(10, 2, 3, 20d, 50, true);
            summary.ObserveTier((PerformanceTier)(iteration % 2));
            _measurementSink += breakdown.SyncMilliseconds;
            _measurementSink += PerformanceMetricCatalog.DefaultSampleEvery(
                (PerformanceMetric)(iteration % (int)PerformanceMetric.Count));
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Equal(0L, allocated, "Summary accumulation steady-state allocation");

        summary.Reset(300d);
        True(!summary.HasWindows, "Summary reset clears windows");
        Equal(0L, summary.FrameCount, "Summary reset clears frames");
        Near(0d, summary.GetSubsystemMilliseconds(PerformanceSubsystem.Sync), 0d, "Summary reset clears groups");
        Equal(0L, summary.GetCounter(PerformanceCounter.EmotionWrites), "Summary reset clears counters");
        True(!summary.HasTierObservations, "Summary reset clears observed tier span");
        Equal(0L, summary.PolicyChangeCount, "Summary reset clears policy change count");
    }

    private static void TestPerformanceTransitionTracker()
    {
        PerformanceProfiler profiler = new PerformanceProfiler(2, 60, 1000L, 0L);
        PerformanceTransitionTracker tracker = new PerformanceTransitionTracker(30);
        long timestamp = 0L;
        PerformanceWindow window = null;
        PerformanceBreakdown breakdown = default;

        for (int index = 0; index < 5; index++)
        {
            double talky = index == 4 ? 2d : 1d;
            window = CloseImpactWindow(profiler, ref timestamp, talky, 20d, 10, 2, 3);
            breakdown = PerformanceBreakdown.FromWindow(window);
            tracker.RecordWindow(
                window,
                breakdown,
                PerformanceTier.Full,
                true);
        }

        tracker.Start(
            PerformanceTier.Full,
            PerformanceTier.Economy,
            GovernorReason.SoftTalkyBudget,
            10d,
            10);
        True(tracker.IsActive, "Transition tracker starts observation");

        for (int index = 0; index < 4; index++)
        {
            window = CloseImpactWindow(profiler, ref timestamp, 0.4d, 16d, 12, 3, 4);
            breakdown = PerformanceBreakdown.FromWindow(window);
            tracker.RecordWindow(
                window,
                breakdown,
                PerformanceTier.Economy,
                true);
        }

        True(
            !tracker.TryComplete(18d, out _),
            "Transition impact waits for ten complete post-change seconds");

        window = CloseImpactWindow(profiler, ref timestamp, 0.4d, 16d, 12, 3, 4);
        breakdown = PerformanceBreakdown.FromWindow(window);
        tracker.RecordWindow(
            window,
            breakdown,
            PerformanceTier.Economy,
            true);

        True(
            tracker.TryComplete(20d, out PerformanceTransitionImpact impact),
            "Transition impact completes at ten-second boundary");
        Equal(1L, impact.Sequence, "Transition impact sequence");
        Equal(PerformanceTier.Full, impact.FromTier, "Transition impact source tier");
        Equal(PerformanceTier.Economy, impact.ToTier, "Transition impact target tier");
        Equal(GovernorReason.SoftTalkyBudget, impact.TransitionReason, "Transition impact reason");
        Equal(5L, impact.Before.WindowCount, "Transition impact before windows");
        Equal(5L, impact.After.WindowCount, "Transition impact after windows");
        True(impact.HasCompleteBaseline, "Transition impact has complete baseline");
        True(impact.HasCompleteAfter, "Transition impact has complete after evidence");
        Near(1.2d, impact.Before.TalkyMillisecondsPerFrame, 1e-9d, "Transition impact before Talky");
        Near(0.4d, impact.After.TalkyMillisecondsPerFrame, 1e-9d, "Transition impact after Talky");
        True(
            impact.After.FrameP95Milliseconds < impact.Before.FrameP95Milliseconds,
            "Transition impact merges lower post-change server p95");
        Near(10d, impact.Before.AveragePlayers, 1e-9d, "Transition impact before population");
        Near(12d, impact.After.AveragePlayers, 1e-9d, "Transition impact after population");
        Near(10d, impact.Before.VoicePacketsPerSecond, 1e-9d, "Transition impact workload rate");
        True(!tracker.IsActive, "Completed transition clears observation");
        string impactMessage =
            PerformanceTransitionReportFormatter.CreateLogMessage(new Config(), impact);
        True(impactMessage.Contains("status=observed"), "Transition report outcome");
        True(impactMessage.Contains("baseline=complete, after=complete"), "Transition report evidence state");
        True(impactMessage.Contains("face_rate"), "Transition report names effective rate change");
        True(impactMessage.Contains("talky=1.200->0.400"), "Transition report before/after cost");
        True(impactMessage.Contains("observational"), "Transition report qualifies server delta");

        Config reloadBefore = new Config();
        Config reloadAfter = new Config
        {
            EnableReactionOnHurt = false,
            EnableEmoteOnConsumables = false,
        };
        string reloadChanges = PerformanceTransitionReportFormatter.DescribePolicyChanges(
            EffectivePerformancePolicy.Create(reloadBefore, PerformanceTier.Full),
            EffectivePerformancePolicy.Create(reloadAfter, PerformanceTier.Full));
        True(
            reloadChanges.Contains("face_reactions on->off"),
            "Policy diff reports reaction-only reload changes");
        True(
            reloadChanges.Contains("consumable_emotes on->off"),
            "Policy diff reports consumable-only reload changes");

        tracker.Start(
            PerformanceTier.Economy,
            PerformanceTier.NoGlancing,
            GovernorReason.Recovery,
            20d,
            10);
        for (int index = 0; index < 4; index++)
        {
            window = CloseImpactWindow(profiler, ref timestamp, 0.3d, 15d, 12, 2, 2);
            breakdown = PerformanceBreakdown.FromWindow(window);
            tracker.RecordWindow(
                window,
                breakdown,
                PerformanceTier.NoGlancing,
                true);
        }

        window = CloseImpactWindow(profiler, ref timestamp, 0.3d, 15d, 0, 0, 0);
        breakdown = PerformanceBreakdown.FromWindow(window);
        tracker.RecordWindow(
            window,
            breakdown,
            PerformanceTier.NoGlancing,
            false);

        for (int index = 0; index < 4; index++)
        {
            window = CloseImpactWindow(profiler, ref timestamp, 0.3d, 15d, 11, 2, 2);
            breakdown = PerformanceBreakdown.FromWindow(window);
            tracker.RecordWindow(
                window,
                breakdown,
                PerformanceTier.NoGlancing,
                true);
        }

        True(
            !tracker.TryComplete(38d, out _),
            "Ineligible transition window resets post-change evidence");
        window = CloseImpactWindow(profiler, ref timestamp, 0.3d, 15d, 11, 2, 2);
        breakdown = PerformanceBreakdown.FromWindow(window);
        tracker.RecordWindow(
            window,
            breakdown,
            PerformanceTier.NoGlancing,
            true);
        True(
            tracker.TryFinish(
                40d,
                PerformanceImpactOutcome.RecoveryAccepted,
                GovernorReason.RecoveryProbeSucceeded,
                out impact),
            "Transition tracker returns explicitly accepted recovery");
        Equal(5L, impact.After.WindowCount, "Only fresh eligible windows survive reset");
        Equal(PerformanceImpactOutcome.RecoveryAccepted, impact.Outcome, "Recovery impact outcome");
        Equal(
            GovernorReason.RecoveryProbeSucceeded,
            impact.CompletionReason,
            "Recovery impact completion reason");

        PerformanceProfiler reuseProfiler =
            new PerformanceProfiler(1, 2, 1000L, 0L);
        PerformanceTransitionTracker reuseTracker =
            new PerformanceTransitionTracker(4);
        long reuseTimestamp = 0L;
        for (int index = 1; index <= 4; index++)
        {
            PerformanceWindow reuseWindow = CloseImpactWindow(
                reuseProfiler,
                ref reuseTimestamp,
                index,
                10d,
                8,
                1,
                1);
            reuseTracker.RecordWindow(
                reuseWindow,
                PerformanceBreakdown.FromWindow(reuseWindow),
                PerformanceTier.Full,
                true);
        }

        reuseTracker.Start(
            PerformanceTier.Full,
            PerformanceTier.Economy,
            GovernorReason.SoftTalkyBudget,
            8d,
            10);
        PerformanceWindow reuseAfter = CloseImpactWindow(
            reuseProfiler,
            ref reuseTimestamp,
            0.5d,
            9d,
            8,
            1,
            1);
        reuseTracker.RecordWindow(
            reuseAfter,
            PerformanceBreakdown.FromWindow(reuseAfter),
            PerformanceTier.Economy,
            true);
        True(
            reuseTracker.TryFinish(
                10d,
                PerformanceImpactOutcome.Interrupted,
                GovernorReason.HardTalkyBudget,
                out PerformanceTransitionImpact reuseImpact),
            "Transition tracker finishes after profiler ring reuse");
        Equal(2L, reuseImpact.Before.WindowCount, "Tracker excludes invalidated profiler slots");
        Near(3.5d, reuseImpact.Before.TalkyMillisecondsPerFrame, 1e-9d, "Tracker copied valid baseline before reuse");
        double copiedBaseline = reuseImpact.Before.TalkyMillisecondsPerFrame;
        for (int index = 0; index < 4; index++)
        {
            reuseAfter = CloseImpactWindow(
                reuseProfiler,
                ref reuseTimestamp,
                9d,
                30d,
                1,
                0,
                0);
        }
        Near(
            copiedBaseline,
            reuseImpact.Before.TalkyMillisecondsPerFrame,
            0d,
            "Completed impact remains stable after profiler slot reuse");

        tracker.Reset();
        tracker.RecordWindow(
            window,
            breakdown,
            PerformanceTier.NoGlancing,
            true);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 10000; iteration++)
        {
            tracker.RecordWindow(
                window,
                breakdown,
                PerformanceTier.NoGlancing,
                true);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Equal(0L, allocated, "Transition window recording steady-state allocation");

        tracker.Start(
            PerformanceTier.NoGlancing,
            PerformanceTier.FaceOnly,
            GovernorReason.SoftTalkyBudget,
            40d,
            10);
        tracker.RecordWindow(
            window,
            breakdown,
            PerformanceTier.FaceOnly,
            true);
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1000; iteration++)
        {
            tracker.RecordWindow(
                window,
                breakdown,
                PerformanceTier.FaceOnly,
                true);
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Equal(0L, allocated, "Active transition accumulation steady-state allocation");
    }

    private static PerformanceWindow CloseImpactWindow(
        PerformanceProfiler profiler,
        ref long timestamp,
        double talkyMillisecondsPerFrame,
        double frameMilliseconds,
        int players,
        int recentSpeakers,
        int activeLooks)
    {
        const int frameCount = 10;
        long talkyTicks = (long)Math.Round(talkyMillisecondsPerFrame * frameCount);
        profiler.RecordDurationTicks(PerformanceMetric.VoicePacket, talkyTicks);
        profiler.Increment(PerformanceCounter.VoicePackets, 20L);
        profiler.Increment(PerformanceCounter.TrackedPlayers, players * frameCount);
        profiler.Increment(PerformanceCounter.RecentSpeakers, recentSpeakers * frameCount);
        profiler.Increment(PerformanceCounter.ActiveLookTargets, activeLooks * frameCount);
        profiler.Increment(PerformanceCounter.SyncEntries, 100L);
        profiler.Increment(PerformanceCounter.Rewrites, 5L);
        for (int frame = 0; frame < frameCount; frame++)
            profiler.RecordFrameMilliseconds(frameMilliseconds);

        timestamp += 2000L;
        True(profiler.TryCloseWindow(timestamp, out PerformanceWindow window), "Impact source window closes");
        return window;
    }

    private static bool IsRecentSpeech(double lastPacketSeconds, double nowSeconds, double durationMilliseconds)
    {
        return lastPacketSeconds >= 0d &&
               (nowSeconds - lastPacketSeconds) * 1000d <= durationMilliseconds;
    }

    private static int[] BuildRecentIndices(
        GlancePlayer[] players,
        double nowSeconds,
        double durationMilliseconds)
    {
        int count = 0;
        for (int i = 0; i < players.Length; i++)
        {
            if (players[i].HasSnapshot &&
                IsRecentSpeech(players[i].LastPacketSeconds, nowSeconds, durationMilliseconds))
            {
                count++;
            }
        }

        int[] result = new int[count];
        int write = 0;
        for (int i = 0; i < players.Length; i++)
        {
            if (players[i].HasSnapshot &&
                IsRecentSpeech(players[i].LastPacketSeconds, nowSeconds, durationMilliseconds))
            {
                result[write++] = i;
            }
        }

        return result;
    }

    private static GlanceSelection SelectGlanceBruteForce(
        GlancePlayer[] players,
        int viewerIndex,
        double nowSeconds,
        double recentMilliseconds,
        double maxDistance,
        double fovDegrees)
    {
        int bestId = -1;
        double bestScore = double.NegativeInfinity;
        int visits = 0;
        for (int candidate = 0; candidate < players.Length; candidate++)
        {
            if (candidate == viewerIndex)
                continue;

            visits++;
            GlancePlayer value = players[candidate];
            if (!value.HasSnapshot ||
                !IsRecentSpeech(value.LastPacketSeconds, nowSeconds, recentMilliseconds))
            {
                continue;
            }

            double score;
            if (TryScoreGlance(players[viewerIndex], value, maxDistance, fovDegrees, out score) &&
                score > bestScore)
            {
                bestId = value.Id;
                bestScore = score;
            }
        }

        return new GlanceSelection(bestId, bestId < 0 ? 0d : bestScore, visits);
    }

    private static GlanceSelection SelectGlanceIndexed(
        GlancePlayer[] players,
        int[] recentIndices,
        int viewerIndex,
        double nowSeconds,
        double recentMilliseconds,
        double maxDistance,
        double fovDegrees)
    {
        int bestId = -1;
        double bestScore = double.NegativeInfinity;
        int visits = 0;
        for (int i = 0; i < recentIndices.Length; i++)
        {
            int candidate = recentIndices[i];
            if (candidate == viewerIndex)
                continue;

            visits++;
            GlancePlayer value = players[candidate];
            if (!value.HasSnapshot ||
                !IsRecentSpeech(value.LastPacketSeconds, nowSeconds, recentMilliseconds))
            {
                continue;
            }

            double score;
            if (TryScoreGlance(players[viewerIndex], value, maxDistance, fovDegrees, out score) &&
                score > bestScore)
            {
                bestId = value.Id;
                bestScore = score;
            }
        }

        return new GlanceSelection(bestId, bestId < 0 ? 0d : bestScore, visits);
    }

    private static bool TryScoreGlance(
        GlancePlayer viewer,
        GlancePlayer candidate,
        double maxDistance,
        double fovDegrees,
        out double score)
    {
        double positionX = candidate.PositionX - viewer.PositionX;
        double positionY = candidate.PositionY - viewer.PositionY;
        double positionZ = candidate.PositionZ - viewer.PositionZ;
        double distanceSquared =
            positionX * positionX + positionY * positionY + positionZ * positionZ;
        if (distanceSquared > maxDistance * maxDistance)
        {
            score = 0d;
            return false;
        }

        double cameraX = candidate.CameraX - viewer.CameraX;
        double cameraY = candidate.CameraY - viewer.CameraY;
        double cameraZ = candidate.CameraZ - viewer.CameraZ;
        double cameraDistanceSquared =
            cameraX * cameraX + cameraY * cameraY + cameraZ * cameraZ;
        if (cameraDistanceSquared <= 0.000001d)
        {
            score = 0d;
            return false;
        }

        double inverseCameraDistance = 1d / Math.Sqrt(cameraDistanceSquared);
        double frontDot = cameraZ * inverseCameraDistance;
        double minimumFrontDot = Math.Cos(0.5d * fovDegrees * Math.PI / 180d);
        if (frontDot < minimumFrontDot)
        {
            score = 0d;
            return false;
        }

        score = 0.8d * frontDot - 0.2d * (Math.Sqrt(distanceSquared) / maxDistance);
        return true;
    }

    private static PerformanceConfig FastGovernorConfig()
    {
        return new PerformanceConfig
        {
            WarmupSeconds = 0,
            TransitionCooldownSeconds = 0,
            RecoveryCooldownSeconds = 0,
            RecoveryHealthySeconds = 4,
            RecoveryProbeSeconds = 10,
            EvaluationWindowSeconds = 2,
            ConsecutiveBreachWindows = 3,
            EmergencyBreachWindows = 2,
        };
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
        if (Math.Abs(expected - actual) > tolerance)
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

        throw new InvalidOperationException(name + ": expected " + typeof(TException).Name);
    }

    private sealed class TestSpeaker
    {
        public TestSpeaker(int id, double lastPacketSeconds)
        {
            Id = id;
            LastPacketSeconds = lastPacketSeconds;
        }

        public int Id { get; }
        public double LastPacketSeconds { get; set; }
        public int Index { get; set; } = -1;
    }

    private sealed class TestRecentSpeakerSet
    {
        private readonly List<TestSpeaker> _speakers = new List<TestSpeaker>();

        public int Count => _speakers.Count;

        public bool Add(TestSpeaker speaker)
        {
            if (speaker == null || speaker.Index >= 0)
                return false;

            speaker.Index = _speakers.Count;
            _speakers.Add(speaker);
            return true;
        }

        public bool Contains(TestSpeaker speaker)
        {
            int index = speaker?.Index ?? -1;
            return index >= 0 && index < _speakers.Count && ReferenceEquals(_speakers[index], speaker);
        }

        public void Prune(double nowSeconds, double durationMilliseconds)
        {
            for (int i = _speakers.Count - 1; i >= 0; i--)
            {
                if (!IsRecentSpeech(_speakers[i].LastPacketSeconds, nowSeconds, durationMilliseconds))
                    RemoveAt(i);
            }
        }

        public void AssertDenseIndices()
        {
            for (int i = 0; i < _speakers.Count; i++)
                Equal(i, _speakers[i].Index, "Recent speaker dense index");
        }

        private void RemoveAt(int index)
        {
            int lastIndex = _speakers.Count - 1;
            TestSpeaker removed = _speakers[index];
            if (index != lastIndex)
            {
                TestSpeaker moved = _speakers[lastIndex];
                _speakers[index] = moved;
                moved.Index = index;
            }

            _speakers.RemoveAt(lastIndex);
            removed.Index = -1;
        }
    }

    private readonly struct GlancePlayer
    {
        public GlancePlayer(
            int id,
            double positionX,
            double positionY,
            double positionZ,
            double cameraX,
            double cameraY,
            double cameraZ,
            double lastPacketSeconds,
            bool hasSnapshot)
        {
            Id = id;
            PositionX = positionX;
            PositionY = positionY;
            PositionZ = positionZ;
            CameraX = cameraX;
            CameraY = cameraY;
            CameraZ = cameraZ;
            LastPacketSeconds = lastPacketSeconds;
            HasSnapshot = hasSnapshot;
        }

        public int Id { get; }
        public double PositionX { get; }
        public double PositionY { get; }
        public double PositionZ { get; }
        public double CameraX { get; }
        public double CameraY { get; }
        public double CameraZ { get; }
        public double LastPacketSeconds { get; }
        public bool HasSnapshot { get; }
    }

    private readonly struct GlanceSelection
    {
        public GlanceSelection(int playerId, double score, int candidateVisits)
        {
            PlayerId = playerId;
            Score = score;
            CandidateVisits = candidateVisits;
        }

        public int PlayerId { get; }
        public double Score { get; }
        public int CandidateVisits { get; }
    }
}
