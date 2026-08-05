using System;
using Talky.Compatibility;

namespace Talky.Tests;

internal static class CedModCompatibilityTests
{
    private static int _assertions;

    public static int Run()
    {
        _assertions = 0;
        TestConfigurationInspection();
        TestLoaderSelection();
        TestModeResolution();
        TestPerInvocationRewriteState();
        TestRewriteCommitCoherence();
        return _assertions;
    }

    private static void TestConfigurationInspection()
    {
        Equal(
            CedModPluginState.NotPresent,
            CedModCompatibilityPolicy.Inspect(null),
            "Missing CedMod is not present");
        Equal(
            CedModPluginState.StockSync,
            CedModCompatibilityPolicy.Inspect(new Candidate(true)),
            "DisableFakeSyncing true selects stock sync");
        Equal(
            CedModPluginState.FakeSync,
            CedModCompatibilityPolicy.Inspect(new Candidate(false)),
            "DisableFakeSyncing false selects fake sync");
        Equal(
            CedModPluginState.Unreadable,
            CedModCompatibilityPolicy.Inspect(new NullConfigCandidate()),
            "Null CedMod config fails closed");
        Equal(
            CedModPluginState.Unreadable,
            CedModCompatibilityPolicy.Inspect(new MissingPathCandidate()),
            "Missing CedMod config path fails closed");
        Equal(
            CedModPluginState.Unreadable,
            CedModCompatibilityPolicy.Inspect(new WrongLeafCandidate()),
            "Non-boolean CedMod setting fails closed");
        Equal(
            CedModPluginState.Unreadable,
            CedModCompatibilityPolicy.Inspect(new ThrowingCandidate()),
            "Throwing CedMod getter fails closed");
    }

    private static void TestLoaderSelection()
    {
        CedModSelection selection = CedModCompatibilityPolicy.Select(
            CedModPluginState.NotPresent,
            CedModPluginState.NotPresent);
        Equal(CedModPluginState.NotPresent, selection.State, "No loader selection state");
        Equal(CedModCompatibilityReason.NotPresent, selection.Reason, "No loader selection reason");

        selection = CedModCompatibilityPolicy.Select(
            CedModPluginState.FakeSync,
            CedModPluginState.NotPresent);
        Equal(CedModPluginState.FakeSync, selection.State, "LabAPI fake sync selection");
        Equal(CedModCompatibilityReason.FakeSyncEnabled, selection.Reason, "LabAPI fake sync reason");

        selection = CedModCompatibilityPolicy.Select(
            CedModPluginState.NotPresent,
            CedModPluginState.FakeSync);
        Equal(CedModPluginState.FakeSync, selection.State, "Pure EXILED fake sync is reachable");
        Equal(CedModCompatibilityReason.FakeSyncEnabled, selection.Reason, "Pure EXILED fake sync reason");

        selection = CedModCompatibilityPolicy.Select(
            CedModPluginState.NotPresent,
            CedModPluginState.StockSync);
        Equal(CedModPluginState.StockSync, selection.State, "Pure EXILED stock sync is reachable");
        Equal(CedModCompatibilityReason.FakeSyncDisabled, selection.Reason, "Pure EXILED stock sync reason");

        selection = CedModCompatibilityPolicy.Select(
            CedModPluginState.StockSync,
            CedModPluginState.FakeSync);
        Equal(CedModPluginState.Unreadable, selection.State, "Dual loader installation fails closed");
        Equal(
            CedModCompatibilityReason.MultipleInstallations,
            selection.Reason,
            "Dual loader installation reason");

        selection = CedModCompatibilityPolicy.Select(
            CedModPluginState.Unreadable,
            CedModPluginState.NotPresent);
        Equal(CedModPluginState.Unreadable, selection.State, "Unreadable config selection");
        Equal(
            CedModCompatibilityReason.ConfigurationUnreadable,
            selection.Reason,
            "Unreadable config reason");
    }

    private static void TestModeResolution()
    {
        CedModLookSyncPlan plan = CedModCompatibilityPolicy.Resolve(
            new CedModSelection(
                CedModPluginState.NotPresent,
                CedModCompatibilityReason.NotPresent),
            false);
        Equal(CedModLookSyncMode.StockSync, plan.Mode, "Absent CedMod uses stock hook");
        True(plan.UseStockEvents, "Stock mode enables direct sync events");
        True(!plan.UseCedModPatch, "Stock mode does not claim a CedMod patch");

        plan = CedModCompatibilityPolicy.Resolve(
            new CedModSelection(
                CedModPluginState.StockSync,
                CedModCompatibilityReason.FakeSyncDisabled),
            false);
        Equal(CedModLookSyncMode.StockSync, plan.Mode, "Disabled fake sync uses stock hook");
        True(plan.UseStockEvents, "Disabled fake sync enables direct sync events");
        True(!plan.UseCedModPatch, "Disabled fake sync does not use the helper patch");

        plan = CedModCompatibilityPolicy.Resolve(
            new CedModSelection(
                CedModPluginState.FakeSync,
                CedModCompatibilityReason.FakeSyncEnabled),
            true);
        Equal(CedModLookSyncMode.CedModFakeSync, plan.Mode, "Verified helper patch mode");
        True(!plan.UseStockEvents, "CedMod fake sync skips stock hooks");
        True(plan.UseCedModPatch, "CedMod fake sync reports patch use");

        plan = CedModCompatibilityPolicy.Resolve(
            new CedModSelection(
                CedModPluginState.FakeSync,
                CedModCompatibilityReason.FakeSyncEnabled),
            false);
        Equal(CedModLookSyncMode.CedModUnsupported, plan.Mode, "Patch failure fails closed");
        Equal(CedModCompatibilityReason.PatchUnavailable, plan.Reason, "Patch failure reason");
        True(!plan.UseStockEvents, "Patch failure never falls back to stock hooks");
        True(!plan.UseCedModPatch, "Patch failure does not claim a patch");

        plan = CedModCompatibilityPolicy.Resolve(
            new CedModSelection(
                CedModPluginState.Unreadable,
                CedModCompatibilityReason.MultipleInstallations),
            false);
        Equal(CedModLookSyncMode.CedModUnsupported, plan.Mode, "Ambiguous install mode");
        Equal(
            CedModCompatibilityReason.MultipleInstallations,
            plan.Reason,
            "Ambiguous install reason survives resolution");

        plan = CedModCompatibilityPolicy.Resolve(
            new CedModSelection(
                CedModPluginState.StockSync,
                CedModCompatibilityReason.MultipleInstallations),
            false);
        Equal(CedModLookSyncMode.CedModUnsupported, plan.Mode, "Incoherent selection fails closed");
        Equal(
            CedModCompatibilityReason.ConfigurationUnreadable,
            plan.Reason,
            "Incoherent selection reports unreadable compatibility state");
    }

    private static void TestPerInvocationRewriteState()
    {
        CedModRewriteState<int, int> first = CedModRewriteState<int, int>.Capture(
            true,
            false,
            false,
            true,
            true,
            false,
            91,
            7);
        True(first.IsArmed, "First receiver-target pair is eligible");
        Equal(0, first.Previous, "First receiver-target pair uses default previous sync");
        Equal(7, first.Offset, "First receiver-target pair captures offset");

        CedModRewriteState<int, int> existing = CedModRewriteState<int, int>.Capture(
            true,
            false,
            false,
            true,
            true,
            true,
            91,
            8);
        True(existing.IsArmed, "Existing receiver-target pair is eligible");
        Equal(91, existing.Previous, "Existing receiver-target pair captures previous sync");
        Equal(8, existing.Offset, "Existing receiver-target pair has independent offset");
        Equal(7, first.Offset, "Earlier invocation state remains unchanged");

        NotArmed(false, false, false, true, true, "Inactive target");
        NotArmed(true, true, false, true, true, "Invisible target");
        NotArmed(true, false, true, true, true, "Near-positioned target");
        NotArmed(true, false, false, false, true, "Ineligible target");
        NotArmed(true, false, false, true, false, "Non-finite offset");
    }

    private static void TestRewriteCommitCoherence()
    {
        CedModRewriteCommit<string> passThrough =
            CedModRewriteCommit<string>.PassThrough("original");
        True(!passThrough.UpdateCache, "Pass-through does not update cache");
        Equal("original", passThrough.Outgoing, "Pass-through outgoing value");
        Equal("original", passThrough.CacheValue, "Pass-through cache value");

        CedModRewriteCommit<string> rewrite =
            CedModRewriteCommit<string>.Rewrite("adjusted");
        True(rewrite.UpdateCache, "Rewrite updates cache");
        Equal("adjusted", rewrite.Outgoing, "Rewrite outgoing value");
        Equal("adjusted", rewrite.CacheValue, "Rewrite cache matches outgoing value");
    }

    private static void NotArmed(
        bool targetActive,
        bool isInvisible,
        bool nearPositioning,
        bool targetEligible,
        bool offsetFinite,
        string name)
    {
        CedModRewriteState<int, int> state = CedModRewriteState<int, int>.Capture(
            targetActive,
            isInvisible,
            nearPositioning,
            targetEligible,
            offsetFinite,
            true,
            3,
            4);
        True(!state.IsArmed, name + " is not rewritten");
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

    private sealed class Candidate
    {
        public Candidate(bool disableFakeSyncing)
        {
            Config = new CandidateConfig(disableFakeSyncing);
        }

        public CandidateConfig Config { get; }
    }

    private sealed class CandidateConfig
    {
        public CandidateConfig(bool disableFakeSyncing)
        {
            CedMod = new CandidateCedModConfig(disableFakeSyncing);
        }

        public CandidateCedModConfig CedMod { get; }
    }

    private sealed class CandidateCedModConfig
    {
        public CandidateCedModConfig(bool disableFakeSyncing)
        {
            DisableFakeSyncing = disableFakeSyncing;
        }

        public bool DisableFakeSyncing { get; }
    }

    private sealed class NullConfigCandidate
    {
        public object Config => null;
    }

    private sealed class MissingPathCandidate
    {
        public object SomethingElse => new object();
    }

    private sealed class WrongLeafCandidate
    {
        public WrongLeafConfig Config { get; } = new WrongLeafConfig();
    }

    private sealed class WrongLeafConfig
    {
        public WrongLeafCedModConfig CedMod { get; } = new WrongLeafCedModConfig();
    }

    private sealed class WrongLeafCedModConfig
    {
        public string DisableFakeSyncing => "false";
    }

    private sealed class ThrowingCandidate
    {
        public object Config => throw new InvalidOperationException("test getter failure");
    }
}
