using System.Runtime.CompilerServices;

namespace Talky.Compatibility;

/// <summary>
/// Allocation-free eligibility rules shared by the runtime selector and deterministic tests.
/// Object identity and game-state reads stay in the adapter; this policy receives their results.
/// </summary>
public static class LookSyncSelectionPolicy
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsHarmonyModeActive(bool registered, CedModLookSyncMode mode)
    {
        return registered &&
               (mode == CedModLookSyncMode.StockHarmony ||
                mode == CedModLookSyncMode.CedModFakeSync);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsImmediatePairEligible(
        bool invisible,
        bool nearPositioning,
        bool hasReceiver,
        bool hasTarget,
        bool hasMovement,
        bool isSelf,
        bool targetIsLocal)
    {
        return !invisible &&
               !nearPositioning &&
               hasReceiver &&
               hasTarget &&
               hasMovement &&
               !isSelf &&
               !targetIsLocal;
    }

    /// <summary>
    /// Identifies selector calls that enter Talky's measured candidate path.
    /// Calls rejected by these global and pair-local guards are intentionally omitted
    /// from selector call/pass-through counters and sync-budget attribution.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ShouldInstrumentSelector(
        bool registered,
        CedModLookSyncMode mode,
        bool invisible,
        bool nearPositioning,
        bool hasReceiver,
        bool hasTarget,
        bool hasMovement,
        bool isSelf,
        bool targetIsLocal,
        bool runtimeEnabled,
        bool hasActiveTargets)
    {
        return IsHarmonyModeActive(registered, mode) &&
               IsImmediatePairEligible(
                   invisible,
                   nearPositioning,
                   hasReceiver,
                   hasTarget,
                   hasMovement,
                   isSelf,
                   targetIsLocal) &&
               runtimeEnabled &&
               hasActiveTargets;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsRuntimeTargetActive(
        bool runtimeEnabled,
        bool hasActiveTargets,
        bool targetActive)
    {
        return runtimeEnabled && hasActiveTargets && targetActive;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsViewerAllowed(
        bool receiverIsSpectatingTarget,
        bool spectatorHeadMovementsEnabled)
    {
        return !receiverIsSpectatingTarget || spectatorHeadMovementsEnabled;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsTargetStateCurrent(
        bool trackerValid,
        bool roleIsFpc,
        bool movementMatches,
        bool liveLookMatches)
    {
        return trackerValid && roleIsFpc && movementMatches && liveLookMatches;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool HasPresentationOffset(
        float nextHorizontal,
        float nextVertical,
        float lastHorizontal,
        float lastVertical)
    {
        return IsFinite(nextHorizontal) &&
               IsFinite(nextVertical) &&
               IsFinite(lastHorizontal) &&
               IsFinite(lastVertical) &&
               (nextHorizontal != 0f || nextVertical != 0f ||
                lastHorizontal != 0f || lastVertical != 0f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
