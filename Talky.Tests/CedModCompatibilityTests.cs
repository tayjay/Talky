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
        TestEnumValueStability();
        TestModeResolution();
        TestHarmonyModeResolution();
        TestLookSyncSelectionPolicy();
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

    private static void TestEnumValueStability()
    {
        Equal(0, (int)CedModLookSyncMode.StockSync, "Stock sync enum value");
        Equal(1, (int)CedModLookSyncMode.CedModFakeSync, "CedMod fake sync enum value");
        Equal(2, (int)CedModLookSyncMode.CedModUnsupported, "Unsupported enum value");
        Equal(3, (int)CedModLookSyncMode.StockHarmony, "Stock Harmony enum value");

        Equal(5, (int)CedModCompatibilityReason.PatchUnavailable, "Patch unavailable reason value");
        Equal(
            6,
            (int)CedModCompatibilityReason.HarmonyPreferenceDisabled,
            "Harmony preference reason value");
    }

    private static void TestHarmonyModeResolution()
    {
        CedModSelection absent = new CedModSelection(
            CedModPluginState.NotPresent,
            CedModCompatibilityReason.NotPresent);
        CedModLookSyncPlan plan = CedModCompatibilityPolicy.Resolve(absent, true, true);
        Equal(CedModLookSyncMode.StockHarmony, plan.Mode, "Stock Harmony patch success mode");
        Equal(CedModCompatibilityReason.NotPresent, plan.Reason, "Stock Harmony success reason");
        True(!plan.UseStockEvents, "Stock Harmony success skips stock events");
        True(plan.UseStockPatch, "Stock Harmony success reports stock patch use");
        True(!plan.UseCedModPatch, "Stock Harmony success does not claim CedMod patch use");

        plan = CedModCompatibilityPolicy.Resolve(absent, true, false);
        Equal(CedModLookSyncMode.CedModUnsupported, plan.Mode, "Stock Harmony failure fails closed");
        Equal(CedModCompatibilityReason.PatchUnavailable, plan.Reason, "Stock patch failure reason");
        True(!plan.UseStockEvents, "Stock Harmony failure leaves the legacy event path untouched");
        True(!plan.UseStockPatch, "Stock Harmony failure does not claim patch use");

        CedModSelection stockCedMod = new CedModSelection(
            CedModPluginState.StockSync,
            CedModCompatibilityReason.FakeSyncDisabled);
        plan = CedModCompatibilityPolicy.Resolve(stockCedMod, true, true);
        Equal(
            CedModLookSyncMode.StockHarmony,
            plan.Mode,
            "CedMod with fake syncing disabled uses the verified stock Harmony patch");
        Equal(
            CedModCompatibilityReason.FakeSyncDisabled,
            plan.Reason,
            "CedMod stock Harmony mode preserves the compatibility reason");
        True(plan.UseStockPatch, "CedMod stock Harmony mode reports stock patch use");

        plan = CedModCompatibilityPolicy.Resolve(stockCedMod, true, false);
        Equal(
            CedModLookSyncMode.CedModUnsupported,
            plan.Mode,
            "CedMod with fake syncing disabled fails closed when the structural stock patch is unavailable");
        Equal(
            CedModCompatibilityReason.PatchUnavailable,
            plan.Reason,
            "CedMod stock fallback reports the unavailable patch");
        True(!plan.UseStockEvents, "CedMod stock patch rejection leaves vanilla events untouched");

        plan = CedModCompatibilityPolicy.Resolve(stockCedMod, false, true);
        Equal(CedModLookSyncMode.StockSync, plan.Mode, "Preference-off stock mode");
        Equal(
            CedModCompatibilityReason.FakeSyncDisabled,
            plan.Reason,
            "Preference-off stock preserves selection reason");
        True(plan.UseStockEvents, "Preference-off stock uses events");
        True(!plan.UseStockPatch, "Preference-off stock ignores a patch outcome");

        CedModSelection fakeCedMod = new CedModSelection(
            CedModPluginState.FakeSync,
            CedModCompatibilityReason.FakeSyncEnabled);
        plan = CedModCompatibilityPolicy.Resolve(fakeCedMod, true, true);
        Equal(CedModLookSyncMode.CedModFakeSync, plan.Mode, "CedMod Harmony patch success mode");
        True(!plan.UseStockEvents, "CedMod Harmony success skips stock events");
        True(!plan.UseStockPatch, "CedMod Harmony success does not claim stock patch use");
        True(plan.UseCedModPatch, "CedMod Harmony success reports CedMod patch use");

        plan = CedModCompatibilityPolicy.Resolve(fakeCedMod, true, false);
        Equal(CedModLookSyncMode.CedModUnsupported, plan.Mode, "CedMod Harmony failure fails closed");
        Equal(CedModCompatibilityReason.PatchUnavailable, plan.Reason, "CedMod Harmony failure reason");

        plan = CedModCompatibilityPolicy.Resolve(fakeCedMod, false, true);
        Equal(
            CedModLookSyncMode.CedModUnsupported,
            plan.Mode,
            "Preference-off CedMod fake sync is unsupported");
        Equal(
            CedModCompatibilityReason.HarmonyPreferenceDisabled,
            plan.Reason,
            "Preference-off CedMod fake sync reason");
        True(!plan.UseStockEvents, "Preference-off CedMod never falls back to stock events");
        True(!plan.UseStockPatch, "Preference-off CedMod does not claim stock patch use");
        True(!plan.UseCedModPatch, "Preference-off CedMod does not claim CedMod patch use");
    }

    private static void TestLookSyncSelectionPolicy()
    {
        True(
            LookSyncSelectionPolicy.IsHarmonyModeActive(
                true,
                CedModLookSyncMode.StockHarmony),
            "Stock Harmony mode activates selector policy");
        True(
            LookSyncSelectionPolicy.IsHarmonyModeActive(
                true,
                CedModLookSyncMode.CedModFakeSync),
            "CedMod Harmony mode activates selector policy");
        True(
            !LookSyncSelectionPolicy.IsHarmonyModeActive(
                true,
                CedModLookSyncMode.StockSync),
            "Vanilla mode keeps stale selector callbacks inert");
        True(
            !LookSyncSelectionPolicy.IsHarmonyModeActive(
                false,
                CedModLookSyncMode.StockHarmony),
            "Unregistered handler keeps selector inert");

        True(
            LookSyncSelectionPolicy.IsImmediatePairEligible(
                false, false, true, true, true, false, false),
            "Ordinary visible remote pair is immediately eligible");
        True(
            !LookSyncSelectionPolicy.IsImmediatePairEligible(
                true, false, true, true, true, false, false),
            "Invisible pair passes through");
        True(
            !LookSyncSelectionPolicy.IsImmediatePairEligible(
                false, true, true, true, true, false, false),
            "CedMod near-positioned pair passes through");
        True(
            !LookSyncSelectionPolicy.IsImmediatePairEligible(
                false, false, true, true, true, true, false),
            "Self pair passes through");
        True(
            !LookSyncSelectionPolicy.IsImmediatePairEligible(
                false, false, true, true, true, false, true),
            "Local target passes through");
        True(
            !LookSyncSelectionPolicy.IsImmediatePairEligible(
                false, false, false, true, true, false, false),
            "Missing receiver passes through");
        True(
            !LookSyncSelectionPolicy.IsImmediatePairEligible(
                false, false, true, false, true, false, false),
            "Missing target passes through");
        True(
            !LookSyncSelectionPolicy.IsImmediatePairEligible(
                false, false, true, true, false, false, false),
            "Missing movement module passes through");

        True(
            LookSyncSelectionPolicy.ShouldInstrumentSelector(
                true,
                CedModLookSyncMode.StockHarmony,
                false, false, true, true, true, false, false,
                true, true),
            "Active visible Harmony pair enters measured selector path");
        True(
            !LookSyncSelectionPolicy.ShouldInstrumentSelector(
                true,
                CedModLookSyncMode.StockSync,
                false, false, true, true, true, false, false,
                true, true),
            "Vanilla fallback is excluded from selector instrumentation");
        True(
            !LookSyncSelectionPolicy.ShouldInstrumentSelector(
                true,
                CedModLookSyncMode.StockHarmony,
                true, false, true, true, true, false, false,
                true, true),
            "Immediately ineligible pair is excluded from selector instrumentation");
        True(
            !LookSyncSelectionPolicy.ShouldInstrumentSelector(
                true,
                CedModLookSyncMode.StockHarmony,
                false, false, true, true, true, false, false,
                false, true),
            "Disabled runtime is excluded from selector instrumentation");
        True(
            !LookSyncSelectionPolicy.ShouldInstrumentSelector(
                true,
                CedModLookSyncMode.StockHarmony,
                false, false, true, true, true, false, false,
                true, false),
            "Runtime without active look targets is excluded from selector instrumentation");

        True(
            LookSyncSelectionPolicy.IsRuntimeTargetActive(true, true, true),
            "Enabled active runtime target is eligible");
        True(
            !LookSyncSelectionPolicy.IsRuntimeTargetActive(false, true, true),
            "Inactive runtime passes through");
        True(
            !LookSyncSelectionPolicy.IsRuntimeTargetActive(true, false, true),
            "Runtime without active targets passes through");
        True(
            !LookSyncSelectionPolicy.IsRuntimeTargetActive(true, true, false),
            "Stale inactive target passes through");

        True(
            LookSyncSelectionPolicy.IsViewerAllowed(false, false),
            "Ordinary receiver does not require spectator permission");
        True(
            !LookSyncSelectionPolicy.IsViewerAllowed(true, false),
            "Disallowed spectator passes through");
        True(
            LookSyncSelectionPolicy.IsViewerAllowed(true, true),
            "Allowed spectator receives presentation look");

        True(
            LookSyncSelectionPolicy.IsTargetStateCurrent(true, true, true, true),
            "Current tracker and FPC module are eligible");
        True(
            !LookSyncSelectionPolicy.IsTargetStateCurrent(false, true, true, true),
            "Missing or disposed tracker passes through");
        True(
            !LookSyncSelectionPolicy.IsTargetStateCurrent(true, false, true, true),
            "Non-FPC role passes through");
        True(
            !LookSyncSelectionPolicy.IsTargetStateCurrent(true, true, false, true),
            "Role movement-module change passes through");
        True(
            !LookSyncSelectionPolicy.IsTargetStateCurrent(true, true, true, false),
            "Stale live-look instance passes through");

        True(
            !LookSyncSelectionPolicy.HasPresentationOffset(0f, 0f, 0f, 0f),
            "Both-zero offset state passes through");
        True(
            LookSyncSelectionPolicy.HasPresentationOffset(2f, -1f, 2f, -1f),
            "Unchanged nonzero effective offset still uses presentation look");
        True(
            LookSyncSelectionPolicy.HasPresentationOffset(0f, 0f, 2f, -1f),
            "Return-to-zero state uses presentation look");
        True(
            !LookSyncSelectionPolicy.HasPresentationOffset(float.NaN, 0f, 1f, 0f),
            "Non-finite next offset passes through");
        True(
            !LookSyncSelectionPolicy.HasPresentationOffset(1f, 0f, float.PositiveInfinity, 0f),
            "Non-finite last offset passes through");
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
