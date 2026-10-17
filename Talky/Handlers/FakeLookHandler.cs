using System;
using System.Collections.Generic;
using InventorySystem.Items.Firearms.Modules;
using InventorySystem.Searching;
using LabApi.Events.Arguments.PlayerEvents;
using Mirror;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.NetworkMessages;
using PlayerRoles.Spectating;
using PlayerStatsSystem;
using Talky.Behaviours;
using Talky.Compatibility;
using Talky.Performance;
using Talky.Runtime;
using UnityEngine;

namespace Talky.Handlers;

public class FakeLookHandler
{
    private FpcSyncData _directLastSyncData;
    private PlayerValidatedVisibilityEventArgs _pendingVisibility;
    private readonly HashSet<uint> _seenVisibilityTargets = new HashSet<uint>(128);
    private uint _visibilityReceiverId;
    private uint _visibilityTargetId;
    private int _visibleBufferCount;
    private int _pendingBufferIndex;
    private int _visibilityFrame = -1;
    private bool _hasVisibilitySequence;
    private bool _pendingPreviousCaptured;
    private bool _registered;
    private bool _directSyncRegistered;
    private int _serializedLookFailureLogged;
    private int _syncAccessFailureLatched;
    private CedModLookSyncMode _syncMode = CedModLookSyncMode.StockSync;

    private TalkyRuntime Runtime => Plugin.Instance?.Runtime;

    private bool TryGetLookOverride(uint networkId, out LookOverride tracker)
    {
        TalkyRuntime runtime = Runtime;
        if (runtime != null)
            return runtime.TryGetLookOverride(networkId, out tracker);

        tracker = null;
        return false;
    }
    
    
    // Capture stock sync data before applying a look rewrite.
    private void OnPlayerValidatedVisibility(PlayerValidatedVisibilityEventArgs ev)
    {
        // Keep the inactive hot path outside instrumentation.
        TalkyRuntime runtime = Runtime;
        if (_syncMode != CedModLookSyncMode.StockSync ||
            runtime == null || !runtime.HasActiveLookTargets ||
            ev?.Player?.ReferenceHub == null ||
            ev.Target?.ReferenceHub == null)
        {
            ResetDirectSyncState();
            return;
        }

        uint eventTargetId = ev.Target.ReferenceHub.netId;
        if (runtime.IsLookTargetActive(eventTargetId) &&
            runtime.TryGetLookOverride(eventTargetId, out LookOverride activeLook) &&
            activeLook != null &&
            !activeLook.TryGetSerializedLookOffsets(
                Time.frameCount,
                out _,
                out _))
        {
            runtime.RefreshLookActivity(activeLook);
            if (!runtime.HasActiveLookTargets)
            {
                ResetDirectSyncState();
                return;
            }
        }

        TalkyPerformanceMonitor monitor = Plugin.Instance?.PerformanceMonitor;
        long started = monitor?.Begin(
                           PerformanceMetric.DirectVisibilitySync,
                           PerformanceMetricCatalog.SyncSampleEvery) ??
                       PerformanceProfiler.NotSampled;
        try
        {
            uint receiverId = ev.Player.ReferenceHub.netId;
            uint targetId = ev.Target.ReferenceHub.netId;
            int frame = Time.frameCount;
            bool newCycle =
                !_hasVisibilitySequence ||
                receiverId != _visibilityReceiverId ||
                frame != _visibilityFrame;
            if (!newCycle && !_seenVisibilityTargets.Add(targetId))
                newCycle = true;

            if (newCycle)
            {
                _hasVisibilitySequence = true;
                _visibilityReceiverId = receiverId;
                _visibilityFrame = frame;
                _visibleBufferCount = 0;
                _seenVisibilityTargets.Clear();
                _seenVisibilityTargets.Add(targetId);
            }

            _pendingVisibility = ev;
            _visibilityTargetId = targetId;
            _pendingBufferIndex = _visibleBufferCount;
            _pendingPreviousCaptured = false;

            if (!runtime.IsLookTargetActive(_visibilityTargetId))
                return;

            try
            {
                _directLastSyncData = SyncMemberAccess.GetPreviousSyncData(
                    ev.Player.ReferenceHub,
                    ev.Target.ReferenceHub);
                SyncAccessProbe.Succeeded(SyncAccessSite.GetPrevSyncData);
            }
            catch (Exception exception)
            {
                HandleSyncAccessFailure(SyncAccessSite.GetPrevSyncData, exception);
                return;
            }

            _pendingPreviousCaptured = true;
        }
        finally
        {
            monitor?.End(PerformanceMetric.DirectVisibilitySync, started);
        }
    }
    
    
    // Runs after GetNewSyncData writes the current position buffer.
    private RoleTypeId OnRoleSyncEvent(ReferenceHub target, ReferenceHub receiver, RoleTypeId role, NetworkWriter writer)
    {
        TalkyRuntime runtime = Runtime;
        if (_syncMode != CedModLookSyncMode.StockSync ||
            runtime == null || !runtime.HasActiveLookTargets)
        {
            return role;
        }

        // Count only receiver-target pairs examined after the inactive guard.
        TalkyPerformanceMonitor monitor = Plugin.Instance?.PerformanceMonitor;
        monitor?.Count(PerformanceCounter.SyncEntries);
        long started = monitor?.Begin(
                           PerformanceMetric.DirectRoleSync,
                           PerformanceMetricCatalog.SyncSampleEvery) ??
                       PerformanceProfiler.NotSampled;
        try
        {
            return ProcessRoleSyncEvent(target, receiver, role, writer, monitor, runtime);
        }
        finally
        {
            monitor?.End(PerformanceMetric.DirectRoleSync, started);
        }
    }

    private RoleTypeId ProcessRoleSyncEvent(
        ReferenceHub target,
        ReferenceHub receiver,
        RoleTypeId role,
        NetworkWriter writer,
        TalkyPerformanceMonitor monitor,
        TalkyRuntime runtime)
    {
        // Preserve the final visibility event so later subscriber changes are honored,
        // and advance the shared-buffer index only for targets that remain visible.
        PlayerValidatedVisibilityEventArgs visibility = _pendingVisibility;
        int bufferIndex = _pendingBufferIndex;
        bool capturedPrevious = _pendingPreviousCaptured;
        _pendingVisibility = null;
        _pendingPreviousCaptured = false;
        if (visibility == null)
            return role;

        if (visibility.Player?.ReferenceHub != receiver ||
            visibility.Target?.ReferenceHub != target ||
            _visibilityReceiverId != receiver.netId ||
            _visibilityTargetId != target.netId)
        {
            return role;
        }

        if (!visibility.IsVisible)
            return role;

        _visibleBufferCount++;

        if (!capturedPrevious)
            return role;

        if (!runtime.IsLookTargetActive(target.netId))
            return role;

        int[] bufferPlayerIds;
        try
        {
            bufferPlayerIds = SyncMemberAccess.GetBufferPlayerIds();
            SyncAccessProbe.Succeeded(SyncAccessSite.BufferPlayerIds);
        }
        catch (Exception exception)
        {
            HandleSyncAccessFailure(SyncAccessSite.BufferPlayerIds, exception);
            return role;
        }

        if (bufferIndex < 0 ||
            bufferIndex >= bufferPlayerIds.Length ||
            bufferPlayerIds[bufferIndex] != target.PlayerId)
        {
            return role;
        }

        monitor?.Count(PerformanceCounter.ActiveTargetHits);

        if (target == receiver)
        {
            return role;
        }

        if (target.IsSpectatedBy(receiver))
        {
            if(Plugin.Instance?.PerformanceMonitor?.CurrentPolicy?.SpectatorHeadMovementsEnabled != true)
                return role;
        }

        if (!runtime.TryGetLookOverride(target.netId, out LookOverride tracker) || tracker == null)
        {
            return role;
        }

        if (target.roleManager.CurrentRole is not IFpcRole currentRole)
        {
            return role;
        }

        if (!tracker.TryGetSerializedLookOffsets(
                Time.frameCount,
                out Vector2 lookOffset,
                out Vector2 previousOffset))
        {
            runtime.RefreshLookActivity(tracker);
            return role;
        }

        if (!IsFinite(lookOffset) || !IsFinite(previousOffset))
            return role;
        
        // Role-sync calls outside position distribution lack matching cache and buffer state.
        Dictionary<uint, Dictionary<uint, FpcSyncData>> previouslySent;
        try
        {
            previouslySent = SyncMemberAccess.GetPreviouslySent();
            SyncAccessProbe.Succeeded(SyncAccessSite.PreviouslySentStock);
        }
        catch (Exception exception)
        {
            HandleSyncAccessFailure(SyncAccessSite.PreviouslySentStock, exception);
            return role;
        }

        if (!previouslySent.TryGetValue(receiver.netId, out var receiverPrev))
        {
            return role;
        }
        
        if (!receiverPrev.TryGetValue(target.netId, out FpcSyncData committedStockData))
        {
            // The stock distributor must cache the pair before role sync is rewritten.
            return role;
        }

        FirstPersonMovementModule fpmm = currentRole.FpcModule;

        // Validate inputs before mutating shared sync state.
        float originalHorizontal = fpmm.MouseLook.CurrentHorizontal;
        float originalVertical = fpmm.MouseLook.CurrentVertical;
        FpcSyncData newSyncData;
        try
        {
            receiverPrev[target.netId] = _directLastSyncData;
            fpmm.MouseLook.CurrentHorizontal = originalHorizontal + lookOffset.x;
            fpmm.MouseLook.CurrentVertical = originalVertical + lookOffset.y;
            newSyncData = SyncMemberAccess.GetNewSyncData(receiver, target, fpmm, false);
            SyncAccessProbe.Succeeded(SyncAccessSite.GetNewSyncData);
        }
        catch (Exception exception)
        {
            RestoreCommittedStockValue(
                receiverPrev,
                target.netId,
                committedStockData,
                SyncAccessSite.GetNewSyncData,
                exception);
            return role;
        }
        finally
        {
            fpmm.MouseLook.CurrentHorizontal = originalHorizontal;
            fpmm.MouseLook.CurrentVertical = originalVertical;
        }

        try
        {
            // Keep the assignment alone in this guarded region. A later diagnostics
            // failure must not roll the cache back after the outgoing value committed.
            SyncMemberAccess.WriteBufferSyncData(bufferIndex, newSyncData);
        }
        catch (Exception exception)
        {
            RestoreCommittedStockValue(
                receiverPrev,
                target.netId,
                committedStockData,
                SyncAccessSite.BufferSyncDataWrite,
                exception);
            return role;
        }

        SyncAccessProbe.Succeeded(SyncAccessSite.BufferSyncDataWrite);

        monitor?.Count(PerformanceCounter.Rewrites);
        if (lookOffset == Vector2.zero && previousOffset != Vector2.zero)
        {
            tracker.MarkZeroCorrectionObserved();
            // Evidence that Talky's rewritten value reached the stock outbound buffer;
            // this does not prove later WriteAll completion or network delivery.
            monitor?.Count(PerformanceCounter.ZeroCorrectionRewrites);
        }
        return role;
    }

    /// <summary>
    /// Selects the mouse-look instance consumed by outbound sync-data construction.
    /// A cached presentation-only instance carries Talky's offsets; the live movement
    /// module remains authoritative and is never modified here.
    /// </summary>
    internal FpcMouseLook SelectSerializedMouseLook(
        FpcMouseLook liveMouseLook,
        ReferenceHub receiver,
        ReferenceHub target,
        FirstPersonMovementModule movement,
        bool isInvisible,
        bool nearPositioning)
    {
        if (liveMouseLook == null)
            return null;

        TalkyPerformanceMonitor monitor = null;
        PerformanceMetric selectorMetric =
            _syncMode == CedModLookSyncMode.CedModFakeSync
                ? PerformanceMetric.CedModRoleSync
                : PerformanceMetric.DirectRoleSync;
        long selectorStarted = PerformanceProfiler.NotSampled;
        bool selectorInstrumented = false;
        try
        {
            if (!LookSyncSelectionPolicy.IsHarmonyModeActive(_registered, _syncMode))
                return liveMouseLook;

            bool hasReceiver = receiver != null;
            bool hasTarget = target != null;
            bool isSelf = hasReceiver && hasTarget &&
                          (ReferenceEquals(receiver, target) ||
                           receiver.netId == target.netId);
            bool targetIsLocal = hasTarget && target.isLocalPlayer;
            if (!LookSyncSelectionPolicy.IsImmediatePairEligible(
                    isInvisible,
                    nearPositioning,
                    hasReceiver,
                    hasTarget,
                    movement != null,
                    isSelf,
                    targetIsLocal))
            {
                return liveMouseLook;
            }

            Plugin plugin = Plugin.Instance;
            TalkyRuntime runtime = plugin?.Runtime;
            bool runtimeEnabled = runtime?.IsEnabled == true;
            bool hasActiveTargets = runtime?.HasActiveLookTargets == true;
            if (!LookSyncSelectionPolicy.ShouldInstrumentSelector(
                    _registered,
                    _syncMode,
                    isInvisible,
                    nearPositioning,
                    hasReceiver,
                    hasTarget,
                    movement != null,
                    isSelf,
                    targetIsLocal,
                    runtimeEnabled,
                    hasActiveTargets))
            {
                // Rejected calls are intentionally outside both measurement and
                // selector call/pass-through counters. Those counters describe only
                // candidate fanout while Talky has active presentation look state.
                return liveMouseLook;
            }

            monitor = plugin?.PerformanceMonitor;
            selectorStarted = monitor?.Begin(
                                  selectorMetric,
                                  PerformanceMetricCatalog.SyncSampleEvery) ??
                              PerformanceProfiler.NotSampled;
            selectorInstrumented = true;
            monitor?.Count(PerformanceCounter.LookSelectorCalls);
            monitor?.Count(PerformanceCounter.SyncEntries);
            if (!LookSyncSelectionPolicy.IsRuntimeTargetActive(
                    runtimeEnabled,
                    hasActiveTargets,
                    runtime.IsLookTargetActive(target.netId)))
            {
                return RecordSelectorPassThrough(monitor, liveMouseLook);
            }

            monitor?.Count(PerformanceCounter.ActiveTargetHits);
            if (!LookSyncSelectionPolicy.IsViewerAllowed(
                    target.IsSpectatedBy(receiver),
                    monitor?.CurrentPolicy?.SpectatorHeadMovementsEnabled == true))
            {
                return RecordSelectorPassThrough(monitor, liveMouseLook);
            }

            bool trackerFound = runtime.TryGetLookOverride(
                target.netId,
                out LookOverride tracker);
            bool trackerValid = trackerFound &&
                                tracker != null &&
                                !tracker.IsDisposed &&
                                ReferenceEquals(tracker.Hub, target);
            if (!trackerValid)
            {
                return RecordSelectorPassThrough(monitor, liveMouseLook);
            }

            if (!tracker.TryGetSerializedLookOffsets(
                    Time.frameCount,
                    out Vector2 offset,
                    out Vector2 previousOffset))
            {
                runtime.RefreshLookActivity(tracker);
                return RecordSelectorPassThrough(monitor, liveMouseLook);
            }

            IFpcRole currentRole = target.roleManager.CurrentRole as IFpcRole;
            if (!LookSyncSelectionPolicy.IsTargetStateCurrent(
                    trackerValid,
                    currentRole != null,
                    ReferenceEquals(currentRole?.FpcModule, movement),
                    ReferenceEquals(movement.MouseLook, liveMouseLook)))
            {
                return RecordSelectorPassThrough(monitor, liveMouseLook);
            }

            if (!LookSyncSelectionPolicy.HasPresentationOffset(
                    offset.x,
                    offset.y,
                    previousOffset.x,
                    previousOffset.y))
            {
                return RecordSelectorPassThrough(monitor, liveMouseLook);
            }

            FpcMouseLook selected = tracker.GetSerializedMouseLook(
                liveMouseLook,
                movement,
                offset,
                out bool scratchAllocated);
            if (scratchAllocated)
            {
                monitor?.Count(PerformanceCounter.LookSelectorScratchAllocations);
            }

            if (selected == null || ReferenceEquals(selected, liveMouseLook))
                return RecordSelectorPassThrough(monitor, liveMouseLook);

            monitor?.Count(PerformanceCounter.LookSelectorRewrites);
            monitor?.Count(PerformanceCounter.Rewrites);
            if (offset == Vector2.zero && previousOffset != Vector2.zero)
            {
                tracker.MarkZeroCorrectionObserved();
                // Evidence that the selector returned Talky's zero-correction look to
                // GetNewSyncData; this does not prove later construction or delivery.
                monitor?.Count(PerformanceCounter.ZeroCorrectionRewrites);
            }
            return selected;
        }
        catch (Exception exception)
        {
            if (selectorInstrumented)
            {
                try
                {
                    monitor?.Count(PerformanceCounter.LookSelectorFailures);
                    monitor?.Count(PerformanceCounter.LookSelectorPassThroughs);
                }
                catch
                {
                    // Diagnostics must never disrupt position distribution.
                }
            }

            if (System.Threading.Interlocked.Exchange(
                    ref _serializedLookFailureLogged,
                    1) == 0)
            {
                try
                {
                    LabApi.Features.Console.Logger.Error(
                        "Outbound look selection failed; live look will be serialized: " +
                        exception);
                }
                catch
                {
                    // Logging must never disrupt position distribution.
                }
            }

            return liveMouseLook;
        }
        finally
        {
            if (selectorInstrumented)
            {
                try
                {
                    monitor?.End(selectorMetric, selectorStarted);
                }
                catch
                {
                    // Diagnostics must never disrupt position distribution.
                }
            }
        }
    }

    private static FpcMouseLook RecordSelectorPassThrough(
        TalkyPerformanceMonitor monitor,
        FpcMouseLook liveMouseLook)
    {
        monitor?.Count(PerformanceCounter.LookSelectorPassThroughs);
        return liveMouseLook;
    }

    private void RestoreCommittedStockValue(
        IDictionary<uint, FpcSyncData> receiverPrevious,
        uint targetId,
        FpcSyncData committedStockData,
        SyncAccessSite failedSite,
        Exception originalFailure)
    {
        if (!SyncAccessProbe.TryRestoreCommittedValue(
                receiverPrevious,
                targetId,
                committedStockData,
                out Exception restoreFailure))
        {
            SyncAccessProbe.Failed(failedSite, originalFailure);
            FailClosedOutboundLookSync(
                failedSite,
                new InvalidOperationException(
                    "The committed stock sync cache value could not be restored after " +
                    "a rewrite abort.",
                    restoreFailure));
            return;
        }

        HandleSyncAccessFailure(failedSite, originalFailure);
    }

    private void HandleSyncAccessFailure(
        SyncAccessSite site,
        Exception exception)
    {
        SyncAccessProbe.Failed(site, exception);
        if (SyncAccessProbe.IsStructuralFailure(exception))
            FailClosedOutboundLookSync(site, exception);
    }

    private void FailClosedOutboundLookSync(
        SyncAccessSite site,
        Exception exception)
    {
        if (System.Threading.Interlocked.Exchange(
                ref _syncAccessFailureLatched,
                1) != 0)
        {
            return;
        }

        // Make callbacks and stale Harmony selectors inert immediately. Keep registration
        // ownership flags intact so ordinary plugin teardown still removes every subscription.
        _syncMode = CedModLookSyncMode.CedModUnsupported;
        ResetDirectSyncState();

        string detail =
            $"runtime sync access failed at {site}: " +
            $"{exception.GetType().Name}: {exception.Message}";
        try
        {
            Plugin plugin = Plugin.Instance;
            if (plugin != null && ReferenceEquals(plugin.FakeLookHandler, this))
            {
                plugin.ReportLookSyncRuntimeFailure(detail);
            }
            else
            {
                LabApi.Features.Console.Logger.Error(
                    "Outbound head-sync rewriting was disabled for this Talky lifecycle; " +
                    "voice and face features remain enabled. " + detail);
            }
        }
        catch
        {
            // Fail-closed state must not depend on diagnostics being available.
        }
    }

    private static bool IsFinite(Vector2 value) =>
        !float.IsNaN(value.x) &&
        !float.IsInfinity(value.x) &&
        !float.IsNaN(value.y) &&
        !float.IsInfinity(value.y);


    private void OnShoot(PlayerShotWeaponEventArgs ev)
    {
        if (!AnimationEventsEnabled()) return;
        if (ev.FirearmItem.Base.TryGetModule(out IAdsModule module) && module.AdsTarget) return;

        if (TryGetLookOverride(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time + 1f;
    }

    private void ToggleAds(PlayerAimedWeaponEventArgs ev)
    {
        if (!AnimationEventsEnabled()) return;
        if (!TryGetLookOverride(ev.Player.ReferenceHub.netId, out var lookOverride) || lookOverride == null)
            return;

        if (ev.Aiming)
            lookOverride.ClearOffsets();
        else
            lookOverride.LastBusyTime = Math.Max(lookOverride.LastBusyTime, Time.time);
    }

    private void OnStartUsingItem(PlayerUsingItemEventArgs ev)
    {
        if (!AnimationEventsEnabled()) return;
        if (TryGetLookOverride(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time + ev.UsableItem.UseDuration;
    }

    private void OnFinishUsedItem(PlayerUsedItemEventArgs ev)
    {
        if (!AnimationEventsEnabled()) return;
        if (TryGetLookOverride(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time;
    }

    private void OnCancelUseItem(PlayerCancelledUsingItemEventArgs ev)
    {
        if (!AnimationEventsEnabled()) return;
        if (TryGetLookOverride(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time;
    }
    
    private void OnPickingUp(PlayerSearchingPickupEventArgs ev)
    {
        if (!AnimationEventsEnabled()) return;
        if (TryGetLookOverride(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time + (ev.Pickup.Base as ISearchable).SearchTimeForPlayer(ev.Player.ReferenceHub);
    }

    private void OnPickedUp(PlayerSearchedPickupEventArgs ev)
    {
        if (!AnimationEventsEnabled()) return;
        if (TryGetLookOverride(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time;
    }

    private void OnHurt(PlayerHurtEventArgs ev)
    {
        if (!AnimationEventsEnabled()) return;
        if(!Plugin.Instance.Config.EnableReactionOnHurt) return;
        if (Plugin.Instance?.PerformanceMonitor?.CurrentPolicy?.HeadMovementEnabled != true) return;
        if(!TryGetLookOverride(ev.Player.NetworkId, out LookOverride tracker) || tracker==null)
        {
            return;
        }

        int damage = 1000;
        if (ev.DamageHandler is StandardDamageHandler standardDamageHandler)
        {
            damage = (int)Math.Floor(standardDamageHandler.DealtHealthDamage);
        }
        if(damage <=Plugin.Instance.Config.MiniumDamageForReaction ) return;

        tracker.CauseReact(damage);
    }

    private static bool AnimationEventsEnabled()
    {
        return Plugin.Instance?.Runtime?.IsEnabled == true;
    }

    public void ConfigureSyncMode(CedModLookSyncMode mode)
    {
        if (_registered)
            throw new InvalidOperationException("Cannot change Talky's sync mode while events are registered.");

        _syncMode = mode;
    }

    public void RegisterEvents()
    {
        if (_registered)
            return;

        _registered = true;
        System.Threading.Interlocked.Exchange(ref _serializedLookFailureLogged, 0);
        System.Threading.Interlocked.Exchange(ref _syncAccessFailureLatched, 0);
        SyncAccessProbe.Reset();

        LabApi.Events.Handlers.PlayerEvents.ShotWeapon += OnShoot;
        LabApi.Events.Handlers.PlayerEvents.AimedWeapon += ToggleAds;
        LabApi.Events.Handlers.PlayerEvents.UsingItem += OnStartUsingItem;
        LabApi.Events.Handlers.PlayerEvents.UsedItem += OnFinishUsedItem;
        LabApi.Events.Handlers.PlayerEvents.CancelledUsingItem += OnCancelUseItem;
        LabApi.Events.Handlers.PlayerEvents.SearchingPickup += OnPickingUp;
        LabApi.Events.Handlers.PlayerEvents.SearchedPickup += OnPickedUp;
        LabApi.Events.Handlers.PlayerEvents.Hurt += OnHurt;
        
        if (_syncMode != CedModLookSyncMode.StockSync)
            return;

        LabApi.Events.Handlers.PlayerEvents.ValidatedVisibility += OnPlayerValidatedVisibility;
        try
        {
            FpcServerPositionDistributor.RoleSyncEvent += OnRoleSyncEvent;
            _directSyncRegistered = true;
        }
        catch
        {
            LabApi.Events.Handlers.PlayerEvents.ValidatedVisibility -= OnPlayerValidatedVisibility;
            throw;
        }
    }

    public void UnregisterEvents()
    {
        // Make stale patches inert before removing event subscriptions.
        _syncMode = CedModLookSyncMode.CedModUnsupported;

        if (_directSyncRegistered)
        {
            LabApi.Events.Handlers.PlayerEvents.ValidatedVisibility -= OnPlayerValidatedVisibility;
            FpcServerPositionDistributor.RoleSyncEvent -= OnRoleSyncEvent;
            _directSyncRegistered = false;
        }

        if (!_registered)
        {
            ResetDirectSyncState();
            return;
        }
        
        LabApi.Events.Handlers.PlayerEvents.ShotWeapon -= OnShoot;
        LabApi.Events.Handlers.PlayerEvents.AimedWeapon -= ToggleAds;
        LabApi.Events.Handlers.PlayerEvents.UsingItem -= OnStartUsingItem;
        LabApi.Events.Handlers.PlayerEvents.UsedItem -= OnFinishUsedItem;
        LabApi.Events.Handlers.PlayerEvents.CancelledUsingItem -= OnCancelUseItem;
        LabApi.Events.Handlers.PlayerEvents.SearchingPickup -= OnPickingUp;
        LabApi.Events.Handlers.PlayerEvents.SearchedPickup -= OnPickedUp;
        LabApi.Events.Handlers.PlayerEvents.Hurt -= OnHurt;
        ResetDirectSyncState();
        _registered = false;
        SyncAccessProbe.LogSummary();
    }

    private void ResetDirectSyncState()
    {
        _pendingVisibility = null;
        _visibilityReceiverId = 0u;
        _visibilityTargetId = 0u;
        _visibleBufferCount = 0;
        _pendingBufferIndex = 0;
        _visibilityFrame = -1;
        _hasVisibilitySequence = false;
        _pendingPreviousCaptured = false;
        _seenVisibilityTargets.Clear();
    }
}
