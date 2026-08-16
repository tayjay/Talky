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
using RelativePositioning;
using Talky.Behaviours;
using Talky.Compatibility;
using Talky.Performance;
using Talky.Runtime;
using UnityEngine;

namespace Talky.Handlers;

internal readonly struct CedModSyncPatchState
{
    public CedModSyncPatchState(
        CedModRewriteState<FpcSyncData, Vector2> rewrite,
        ReferenceHub receiver,
        ReferenceHub target,
        FirstPersonMovementModule movement)
    {
        Rewrite = rewrite;
        Receiver = receiver;
        Target = target;
        Movement = movement;
    }

    public CedModRewriteState<FpcSyncData, Vector2> Rewrite { get; }
    public ReferenceHub Receiver { get; }
    public ReferenceHub Target { get; }
    public FirstPersonMovementModule Movement { get; }
}

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
    
    
    // Save the last sync data before it is changed
    private void OnPlayerValidatedVisibility(PlayerValidatedVisibilityEventArgs ev)
    {
        // Guard first. WriteAll calls this per receiver per target per tick, so the inactive
        // case must cost three field reads and nothing else.
        TalkyRuntime runtime = Runtime;
        if (_syncMode != CedModLookSyncMode.StockSync ||
            runtime == null || !runtime.HasActiveLookTargets ||
            ev?.Player?.ReferenceHub == null ||
            ev.Target?.ReferenceHub == null)
        {
            ResetDirectSyncState();
            return;
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
                _directLastSyncData = FpcServerPositionDistributor.GetPrevSyncData(
                    ev.Player.ReferenceHub,
                    ev.Target.ReferenceHub);
                SyncAccessProbe.Succeeded(SyncAccessSite.GetPrevSyncData);
            }
            catch (Exception exception)
            {
                SyncAccessProbe.Failed(SyncAccessSite.GetPrevSyncData, exception);
                return;
            }

            _pendingPreviousCaptured = true;
        }
        finally
        {
            monitor?.End(PerformanceMetric.DirectVisibilitySync, started);
        }
    }
    
    
    // This is fired in FpcServerPositionDistributor just after the position buffer is written by FpcServerPositionDistributor.GetNewSyncData
    private RoleTypeId OnRoleSyncEvent(ReferenceHub target, ReferenceHub receiver, RoleTypeId role, NetworkWriter writer)
    {
        TalkyRuntime runtime = Runtime;
        if (_syncMode != CedModLookSyncMode.StockSync ||
            runtime == null || !runtime.HasActiveLookTargets)
        {
            return role;
        }

        // Counted below the guard, so sync_entries reports pairs Talky examined rather than
        // every pair the server distributes.
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
        // ValidatedVisibility is raised immediately before the stock method optionally appends
        // this target to the shared buffer. Keep the exact event object so any later visibility
        // subscriber's change is visible here, and advance the index only for the final result.
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
            bufferPlayerIds = FpcServerPositionDistributor._bufferPlayerIDs;
            SyncAccessProbe.Succeeded(SyncAccessSite.BufferPlayerIds);
        }
        catch (Exception exception)
        {
            SyncAccessProbe.Failed(SyncAccessSite.BufferPlayerIds, exception);
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
            return role; // Don't modify own player data
        }

        if (target.IsSpectatedBy(receiver))
        {
            if(Plugin.Instance?.PerformanceMonitor?.CurrentPolicy?.SpectatorHeadMovementsEnabled != true)
                return role;
        }

        // Use cached LookOverride instead of TryGetComponent
        if (!runtime.TryGetLookOverride(target.netId, out LookOverride tracker) || tracker == null)
        {
            return role;
        }

        if (target.roleManager.CurrentRole is not IFpcRole currentRole)
        {
            return role;
        }
        
        if(tracker.NextLookOffset == Vector2.zero && tracker.LastLookOffset == Vector2.zero) return role;
        
        // RoleSyncEvent also fires from PlayerRoleManager.SendNewRoleInfo during role
        // changes, outside the WriteAll position-distribution cycle. In that path,
        // PreviouslySent isn't populated for this receiver, _directLastSyncData is stale,
        // and the buffer indices don't correspond to this (receiver, target) pair.
        Dictionary<uint, Dictionary<uint, FpcSyncData>> previouslySent;
        try
        {
            previouslySent = FpcServerPositionDistributor.PreviouslySent;
            SyncAccessProbe.Succeeded(SyncAccessSite.PreviouslySentStock);
        }
        catch (Exception exception)
        {
            SyncAccessProbe.Failed(SyncAccessSite.PreviouslySentStock, exception);
            return role;
        }

        if (!previouslySent.TryGetValue(receiver.netId, out var receiverPrev))
        {
            return role;
        }
        
        if(!receiverPrev.TryGetValue(target.netId, out var _))
        {
            // The stock distributor should already have cached this pair before RoleSyncEvent.
            return role;
        }

        FirstPersonMovementModule fpmm = currentRole.FpcModule;
        Vector2 lookOffset = tracker.NextLookOffset;
        if (!IsFinite(lookOffset))
        {
            return role;
        }

        // Validate every input before mutating the stock cache or movement module.
        float originalHorizontal = fpmm.MouseLook.CurrentHorizontal;
        float originalVertical = fpmm.MouseLook.CurrentVertical;
        FpcSyncData newSyncData;
        try
        {
            receiverPrev[target.netId] = _directLastSyncData;
            fpmm.MouseLook.CurrentHorizontal = originalHorizontal + lookOffset.x;
            fpmm.MouseLook.CurrentVertical = originalVertical + lookOffset.y;
            newSyncData = FpcServerPositionDistributor.GetNewSyncData(receiver, target, fpmm, false);
            SyncAccessProbe.Succeeded(SyncAccessSite.GetNewSyncData);
        }
        catch (Exception exception)
        {
            SyncAccessProbe.Failed(SyncAccessSite.GetNewSyncData, exception);
            return role;
        }
        finally
        {
            fpmm.MouseLook.CurrentHorizontal = originalHorizontal;
            fpmm.MouseLook.CurrentVertical = originalVertical;
        }

        try
        {
            FpcServerPositionDistributor._bufferSyncData[bufferIndex] = newSyncData;
            SyncAccessProbe.Succeeded(SyncAccessSite.BufferSyncDataWrite);
        }
        catch (Exception exception)
        {
            SyncAccessProbe.Failed(SyncAccessSite.BufferSyncDataWrite, exception);
            return role;
        }

        monitor?.Count(PerformanceCounter.Rewrites);
        return role;
    }

    internal CedModSyncPatchState CaptureCedModSync(
        ReferenceHub receiver,
        ReferenceHub target,
        FirstPersonMovementModule fpmm,
        bool isInvisible,
        bool nearPositioning)
    {
        TalkyRuntime runtime = Runtime;
        if (_syncMode != CedModLookSyncMode.CedModFakeSync ||
            runtime == null || !runtime.HasActiveLookTargets ||
            receiver == null || target == null || fpmm == null)
        {
            return default;
        }

        TalkyPerformanceMonitor monitor = Plugin.Instance?.PerformanceMonitor;
        monitor?.Count(PerformanceCounter.SyncEntries);
        long started = monitor?.Begin(
                           PerformanceMetric.CedModRoleSync,
                           PerformanceMetricCatalog.SyncSampleEvery) ??
                       PerformanceProfiler.NotSampled;
        try
        {
            bool targetActive = runtime.IsLookTargetActive(target.netId);
            if (targetActive)
                monitor?.Count(PerformanceCounter.ActiveTargetHits);

            bool targetEligible =
                targetActive &&
                !isInvisible &&
                !nearPositioning &&
                target != receiver;
            if (targetEligible && target.IsSpectatedBy(receiver) &&
                monitor?.CurrentPolicy?.SpectatorHeadMovementsEnabled != true)
            {
                targetEligible = false;
            }

            if (targetEligible &&
                (target.roleManager.CurrentRole is not IFpcRole currentRole ||
                 !ReferenceEquals(currentRole.FpcModule, fpmm)))
            {
                targetEligible = false;
            }

            Vector2 lookOffset = default;
            if (targetEligible)
            {
                if (!runtime.TryGetLookOverride(target.netId, out LookOverride tracker) ||
                    tracker == null ||
                    (tracker.NextLookOffset == Vector2.zero &&
                     tracker.LastLookOffset == Vector2.zero))
                {
                    targetEligible = false;
                }
                else
                {
                    lookOffset = tracker.NextLookOffset;
                }
            }

            bool offsetFinite = IsFinite(lookOffset);

            bool hasPrevious = false;
            FpcSyncData previous = default;
            if (targetEligible && offsetFinite)
            {
                try
                {
                    if (FpcServerPositionDistributor.PreviouslySent.TryGetValue(
                            receiver.netId,
                            out var receiverPrevious))
                    {
                        hasPrevious = receiverPrevious.TryGetValue(target.netId, out previous);
                    }

                    SyncAccessProbe.Succeeded(SyncAccessSite.PreviouslySentCedModPrefix);
                }
                catch (Exception exception)
                {
                    SyncAccessProbe.Failed(SyncAccessSite.PreviouslySentCedModPrefix, exception);
                    hasPrevious = false;
                }
            }

            CedModRewriteState<FpcSyncData, Vector2> rewrite =
                CedModRewriteState<FpcSyncData, Vector2>.Capture(
                    targetActive,
                    isInvisible,
                    nearPositioning,
                    targetEligible,
                    offsetFinite,
                    hasPrevious,
                    previous,
                    lookOffset);
            return rewrite.IsArmed
                ? new CedModSyncPatchState(rewrite, receiver, target, fpmm)
                : default;
        }
        finally
        {
            // Only Talky's prefix work is charged. CedMod's helper runs after this
            // returns and must not be attributed to Talky's adaptive budget.
            monitor?.End(PerformanceMetric.CedModRoleSync, started);
        }
    }

    internal void RewriteCedModSync(
        ReferenceHub receiver,
        ReferenceHub target,
        FirstPersonMovementModule fpmm,
        bool isInvisible,
        bool nearPositioning,
        bool runOriginal,
        CedModSyncPatchState state,
        ref FpcSyncData original)
    {
        // Guard before instrumentation: CedMod calls this per receiver per target per tick.
        bool stateReferencesMatch =
            receiver != null &&
            target != null &&
            fpmm != null &&
            ReferenceEquals(receiver, state.Receiver) &&
            ReferenceEquals(target, state.Target) &&
            ReferenceEquals(fpmm, state.Movement);
        if (_syncMode != CedModLookSyncMode.CedModFakeSync ||
            !runOriginal || !state.Rewrite.IsArmed ||
            !stateReferencesMatch ||
            isInvisible || nearPositioning)
        {
            return;
        }

        TalkyRuntime runtime = Runtime;
        if (runtime == null || !runtime.HasActiveLookTargets)
            return;

        TalkyPerformanceMonitor monitor = Plugin.Instance?.PerformanceMonitor;
        long started = monitor?.Begin(
                           PerformanceMetric.CedModPostfix,
                           PerformanceMetricCatalog.SyncSampleEvery) ??
                       PerformanceProfiler.NotSampled;
        try
        {
            if (!runtime.IsLookTargetActive(target.netId) || target == receiver)
                return;

            if (target.IsSpectatedBy(receiver) &&
                monitor?.CurrentPolicy?.SpectatorHeadMovementsEnabled != true)
            {
                return;
            }

            if (target.roleManager.CurrentRole is not IFpcRole currentRole ||
                !ReferenceEquals(currentRole.FpcModule, fpmm))
            {
                return;
            }

            if (!IsFinite(state.Rewrite.Offset))
                return;

            Dictionary<uint, FpcSyncData> receiverPrevious;
            try
            {
                bool found = FpcServerPositionDistributor.PreviouslySent.TryGetValue(
                    receiver.netId,
                    out receiverPrevious);
                SyncAccessProbe.Succeeded(SyncAccessSite.PreviouslySentCedModPostfix);
                if (!found)
                    return;
            }
            catch (Exception exception)
            {
                SyncAccessProbe.Failed(SyncAccessSite.PreviouslySentCedModPostfix, exception);
                return;
            }

            if (!receiverPrevious.ContainsKey(target.netId))
            {
                // The prefix armed only after finding this pair and CedMod's original helper is
                // expected to cache it again before the postfix. Its disappearance means the
                // captured state no longer describes the cache being committed.
                return;
            }

            Vector2 lookOffset = state.Rewrite.Offset;
            float originalHorizontal = fpmm.MouseLook.CurrentHorizontal;
            float originalVertical = fpmm.MouseLook.CurrentVertical;

            PlayerMovementState movementState;
            bool bitCustom;
            RelativePosition position;
            try
            {
                movementState = original._state;
                bitCustom = original._bitCustom;
                position = original._position;
                SyncAccessProbe.Succeeded(SyncAccessSite.FpcSyncDataCarriedFields);
            }
            catch (Exception exception)
            {
                SyncAccessProbe.Failed(SyncAccessSite.FpcSyncDataCarriedFields, exception);
                return;
            }

            FpcSyncData rewritten;
            try
            {
                fpmm.MouseLook.CurrentHorizontal = originalHorizontal + lookOffset.x;
                fpmm.MouseLook.CurrentVertical = originalVertical + lookOffset.y;
                rewritten = new FpcSyncData(
                    state.Rewrite.Previous,
                    movementState,
                    bitCustom,
                    position,
                    fpmm.MouseLook);
            }
            finally
            {
                fpmm.MouseLook.CurrentHorizontal = originalHorizontal;
                fpmm.MouseLook.CurrentVertical = originalVertical;
            }

            CedModRewriteCommit<FpcSyncData> commit =
                CedModRewriteCommit<FpcSyncData>.Rewrite(rewritten);

            // CedMod caches its original record before returning. Update that cache first,
            // then return the exact same record so the next delta is based on what this
            // receiver actually observed.
            receiverPrevious[target.netId] = commit.CacheValue;
            original = commit.Outgoing;
            monitor?.Count(PerformanceCounter.Rewrites);
        }
        finally
        {
            monitor?.End(PerformanceMetric.CedModPostfix, started);
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
        if (ev.FirearmItem.Base.TryGetModule(out IAdsModule module) && module.AdsTarget) return; // Don't reset if aiming down sights

        if (TryGetLookOverride(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time + 1f;
    }

    private void ToggleAds(PlayerAimedWeaponEventArgs ev)
    {
        if (!AnimationEventsEnabled()) return;
        if (!TryGetLookOverride(ev.Player.ReferenceHub.netId, out var lookOverride) || lookOverride == null)
            return;

        lookOverride.LastBusyTime = ev.Aiming ? float.MaxValue : Time.time;
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
        // Make a stale owner patch inert before touching event subscriptions. This remains
        // fail-closed even if an external event accessor throws during teardown.
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
