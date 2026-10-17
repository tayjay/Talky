using System;

namespace Talky.Compatibility;

public enum CedModPluginState
{
    NotPresent = 0,
    StockSync = 1,
    FakeSync = 2,
    Unreadable = 3,
}

public enum CedModLookSyncMode
{
    StockSync = 0,
    CedModFakeSync = 1,
    CedModUnsupported = 2,
    StockHarmony = 3,
}

public enum CedModCompatibilityReason
{
    NotPresent = 0,
    FakeSyncDisabled = 1,
    FakeSyncEnabled = 2,
    ConfigurationUnreadable = 3,
    MultipleInstallations = 4,
    PatchUnavailable = 5,
    HarmonyPreferenceDisabled = 6,
}

public readonly struct CedModSelection
{
    public CedModSelection(CedModPluginState state, CedModCompatibilityReason reason)
    {
        State = state;
        Reason = reason;
    }

    public CedModPluginState State { get; }
    public CedModCompatibilityReason Reason { get; }
}

public readonly struct CedModLookSyncPlan
{
    public CedModLookSyncPlan(CedModLookSyncMode mode, CedModCompatibilityReason reason)
    {
        Mode = mode;
        Reason = reason;
    }

    public CedModLookSyncMode Mode { get; }
    public CedModCompatibilityReason Reason { get; }
    public bool UseStockEvents => Mode == CedModLookSyncMode.StockSync;
    public bool UseStockPatch => Mode == CedModLookSyncMode.StockHarmony;
    public bool UseCedModPatch => Mode == CedModLookSyncMode.CedModFakeSync;
}

public static class CedModCompatibilityPolicy
{
    public static CedModPluginState Inspect(Func<bool> readDisableFakeSyncing)
    {
        if (readDisableFakeSyncing == null)
            return CedModPluginState.NotPresent;

        try
        {
            return readDisableFakeSyncing()
                ? CedModPluginState.StockSync
                : CedModPluginState.FakeSync;
        }
        catch
        {
            return CedModPluginState.Unreadable;
        }
    }

    public static CedModSelection Select(
        CedModPluginState labApi,
        CedModPluginState exiled)
    {
        if (!IsKnown(labApi) || !IsKnown(exiled))
        {
            return new CedModSelection(
                CedModPluginState.Unreadable,
                CedModCompatibilityReason.ConfigurationUnreadable);
        }

        bool labApiPresent = labApi != CedModPluginState.NotPresent;
        bool exiledPresent = exiled != CedModPluginState.NotPresent;
        if (labApiPresent && exiledPresent)
        {
            return new CedModSelection(
                CedModPluginState.Unreadable,
                CedModCompatibilityReason.MultipleInstallations);
        }

        CedModPluginState selected = labApiPresent ? labApi : exiled;
        switch (selected)
        {
            case CedModPluginState.NotPresent:
                return new CedModSelection(
                    selected,
                    CedModCompatibilityReason.NotPresent);
            case CedModPluginState.StockSync:
                return new CedModSelection(
                    selected,
                    CedModCompatibilityReason.FakeSyncDisabled);
            case CedModPluginState.FakeSync:
                return new CedModSelection(
                    selected,
                    CedModCompatibilityReason.FakeSyncEnabled);
            default:
                return new CedModSelection(
                    CedModPluginState.Unreadable,
                    CedModCompatibilityReason.ConfigurationUnreadable);
        }
    }

    public static CedModLookSyncPlan Resolve(
        CedModSelection selection,
        bool patchApplied)
    {
        // Preserve the original contract: stock selections use the event path,
        // while CedMod fake sync consumes the supplied patch result.
        bool preferHarmonyLookSync = selection.State == CedModPluginState.FakeSync;
        return Resolve(selection, preferHarmonyLookSync, patchApplied);
    }

    public static CedModLookSyncPlan Resolve(
        CedModSelection selection,
        bool preferHarmonyLookSync,
        bool patchApplied)
    {
        // Callers without an explicit absence/ownership observation retain the
        // conservative patch-failure behavior.
        return Resolve(selection, preferHarmonyLookSync, patchApplied, true, false);
    }

    public static CedModLookSyncPlan Resolve(
        CedModSelection selection,
        bool preferHarmonyLookSync,
        bool patchApplied,
        bool harmonyPresent,
        bool patchOwnershipPossible)
    {
        if (!IsCoherent(selection))
        {
            return new CedModLookSyncPlan(
                CedModLookSyncMode.CedModUnsupported,
                CedModCompatibilityReason.ConfigurationUnreadable);
        }

        if ((patchOwnershipPossible && (!preferHarmonyLookSync || !patchApplied)) ||
            (patchApplied && !harmonyPresent))
        {
            return new CedModLookSyncPlan(
                CedModLookSyncMode.CedModUnsupported,
                CedModCompatibilityReason.PatchUnavailable);
        }

        switch (selection.State)
        {
            case CedModPluginState.NotPresent:
            case CedModPluginState.StockSync:
                if (!preferHarmonyLookSync)
                {
                    return new CedModLookSyncPlan(
                        CedModLookSyncMode.StockSync,
                        selection.Reason);
                }

                if (!patchApplied && !harmonyPresent && !patchOwnershipPossible)
                {
                    // The stock event path is safe when no Harmony implementation
                    // can hold Talky patches. An installed but incompatible runtime
                    // still fails closed below.
                    return new CedModLookSyncPlan(
                        CedModLookSyncMode.StockSync,
                        CedModCompatibilityReason.PatchUnavailable);
                }

                return patchApplied
                    ? new CedModLookSyncPlan(
                        CedModLookSyncMode.StockHarmony,
                        selection.Reason)
                    : new CedModLookSyncPlan(
                        CedModLookSyncMode.CedModUnsupported,
                        CedModCompatibilityReason.PatchUnavailable);
            case CedModPluginState.FakeSync:
                if (!preferHarmonyLookSync)
                {
                    return new CedModLookSyncPlan(
                        CedModLookSyncMode.CedModUnsupported,
                        CedModCompatibilityReason.HarmonyPreferenceDisabled);
                }

                return patchApplied
                    ? new CedModLookSyncPlan(
                        CedModLookSyncMode.CedModFakeSync,
                        CedModCompatibilityReason.FakeSyncEnabled)
                    : new CedModLookSyncPlan(
                        CedModLookSyncMode.CedModUnsupported,
                        CedModCompatibilityReason.PatchUnavailable);
            default:
                return new CedModLookSyncPlan(
                    CedModLookSyncMode.CedModUnsupported,
                    selection.Reason == CedModCompatibilityReason.MultipleInstallations
                        ? CedModCompatibilityReason.MultipleInstallations
                        : CedModCompatibilityReason.ConfigurationUnreadable);
        }
    }

    private static bool IsKnown(CedModPluginState state) =>
        state >= CedModPluginState.NotPresent &&
        state <= CedModPluginState.Unreadable;

    private static bool IsCoherent(CedModSelection selection)
    {
        switch (selection.State)
        {
            case CedModPluginState.NotPresent:
                return selection.Reason == CedModCompatibilityReason.NotPresent;
            case CedModPluginState.StockSync:
                return selection.Reason == CedModCompatibilityReason.FakeSyncDisabled;
            case CedModPluginState.FakeSync:
                return selection.Reason == CedModCompatibilityReason.FakeSyncEnabled;
            case CedModPluginState.Unreadable:
                return selection.Reason == CedModCompatibilityReason.ConfigurationUnreadable ||
                       selection.Reason == CedModCompatibilityReason.MultipleInstallations;
            default:
                return false;
        }
    }
}
