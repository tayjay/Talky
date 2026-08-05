using System;
using System.Reflection;

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
}

public enum CedModCompatibilityReason
{
    NotPresent = 0,
    FakeSyncDisabled = 1,
    FakeSyncEnabled = 2,
    ConfigurationUnreadable = 3,
    MultipleInstallations = 4,
    PatchUnavailable = 5,
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
    public bool UseCedModPatch => Mode == CedModLookSyncMode.CedModFakeSync;
}

public static class CedModCompatibilityPolicy
{
    private static readonly string[] DisableFakeSyncingPath =
    {
        "Config",
        "CedMod",
        "DisableFakeSyncing",
    };

    public static CedModPluginState Inspect(object candidate)
    {
        if (candidate == null)
            return CedModPluginState.NotPresent;

        try
        {
            object current = candidate;
            for (int i = 0; i < DisableFakeSyncingPath.Length; i++)
            {
                if (current == null)
                    return CedModPluginState.Unreadable;

                PropertyInfo property = current.GetType().GetProperty(
                    DisableFakeSyncingPath[i],
                    BindingFlags.Instance | BindingFlags.Public);
                if (property == null || property.GetIndexParameters().Length != 0)
                    return CedModPluginState.Unreadable;

                current = property.GetValue(current);
            }

            if (!(current is bool disableFakeSyncing))
                return CedModPluginState.Unreadable;

            return disableFakeSyncing
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
        if (!IsCoherent(selection))
        {
            return new CedModLookSyncPlan(
                CedModLookSyncMode.CedModUnsupported,
                CedModCompatibilityReason.ConfigurationUnreadable);
        }

        switch (selection.State)
        {
            case CedModPluginState.NotPresent:
            case CedModPluginState.StockSync:
                return new CedModLookSyncPlan(
                    CedModLookSyncMode.StockSync,
                    selection.Reason);
            case CedModPluginState.FakeSync:
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
