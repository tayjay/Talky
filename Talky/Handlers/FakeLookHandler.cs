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
using Talky.Performance;
using Talky.Runtime;
using UnityEngine;

namespace Talky.Handlers;

public class FakeLookHandler
{
    private FpcSyncData _lastSyncData;
    private PlayerValidatedVisibilityEventArgs _pendingVisibility;
    private readonly HashSet<uint> _seenVisibilityTargets = new HashSet<uint>(128);
    private uint _visibilityReceiverId;
    private uint _visibilityTargetId;
    private int _visibleBufferCount;
    private int _pendingBufferIndex;
    private int _visibilityFrame = -1;
    private bool _hasVisibilitySequence;
    private bool _pendingPreviousCaptured;
    private bool _cedModHasPrevious;
    private bool _registered;
    public bool RequireHarmonyPatch = false;

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
        TalkyRuntime runtime = Runtime;
        if (runtime == null || !runtime.HasActiveLookTargets ||
            ev?.Player?.ReferenceHub == null ||
            ev.Target?.ReferenceHub == null)
        {
            ResetDirectSyncState();
            return;
        }

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

        _lastSyncData = FpcServerPositionDistributor.GetPrevSyncData(ev.Player.ReferenceHub, ev.Target.ReferenceHub);
        _pendingPreviousCaptured = true;
    }
    
    
    // This is fired in FpcServerPositionDistributor just after the position buffer is written by FpcServerPositionDistributor.GetNewSyncData
    private RoleTypeId OnRoleSyncEvent(ReferenceHub target, ReferenceHub receiver, RoleTypeId role, NetworkWriter writer)
    {
        TalkyRuntime runtime = Runtime;
        if (runtime == null || !runtime.HasActiveLookTargets)
            return role;

        TalkyPerformanceMonitor monitor = Plugin.Instance?.PerformanceMonitor;
        monitor?.Count(PerformanceCounter.SyncEntries);
        long started = monitor?.Begin(PerformanceMetric.DirectRoleSync) ??
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
        if (visibility == null ||
            visibility.Player?.ReferenceHub != receiver ||
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

        if (bufferIndex < 0 ||
            bufferIndex >= FpcServerPositionDistributor._bufferPlayerIDs.Length ||
            FpcServerPositionDistributor._bufferPlayerIDs[bufferIndex] != target.PlayerId)
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
        // PreviouslySent isn't populated for this receiver, _lastSyncData is stale,
        // and the buffer indices don't correspond to this (receiver, target) pair.
        if (!FpcServerPositionDistributor.PreviouslySent.TryGetValue(receiver.netId, out var receiverPrev))
        {
            return role;
        }
        
        if(!receiverPrev.TryGetValue(target.netId, out var _))
        {
            // This should never happen because the sync data should have been cached in CallPrefix, but just in case
            return role;
        }

        FirstPersonMovementModule fpmm = currentRole.FpcModule;
        // Now we need to modify the sync data
        receiverPrev[target.netId] = _lastSyncData;
        Vector2 lookOffset = tracker.NextLookOffset;
        float originalHorizontal = fpmm.MouseLook.CurrentHorizontal;
        float originalVertical = fpmm.MouseLook.CurrentVertical;
        FpcSyncData newSyncData;
        try
        {
            fpmm.MouseLook.CurrentHorizontal = originalHorizontal + lookOffset.x;
            fpmm.MouseLook.CurrentVertical = originalVertical + lookOffset.y;
            newSyncData = FpcServerPositionDistributor.GetNewSyncData(receiver, target, fpmm, false);
        }
        finally
        {
            fpmm.MouseLook.CurrentHorizontal = originalHorizontal;
            fpmm.MouseLook.CurrentVertical = originalVertical;
        }

        FpcServerPositionDistributor._bufferSyncData[bufferIndex] = newSyncData;
        monitor?.Count(PerformanceCounter.Rewrites);
        return role;
    }

    public void CallPrefix(ReferenceHub receiver, ReferenceHub target, FirstPersonMovementModule fpmm, bool isInvisible,
        bool nearPositioning)
    {
        _cedModHasPrevious = false;
        TalkyRuntime runtime = Runtime;
        if (runtime == null || !runtime.HasActiveLookTargets)
            return;

        TalkyPerformanceMonitor monitor = Plugin.Instance?.PerformanceMonitor;
        monitor?.Count(PerformanceCounter.SyncEntries);
        long started = monitor?.Begin(PerformanceMetric.CedModRoleSync) ??
                       PerformanceProfiler.NotSampled;
        try
        {
            if (!runtime.IsLookTargetActive(target.netId))
            {
                return;
            }

            monitor?.Count(PerformanceCounter.ActiveTargetHits);
            if (!FpcServerPositionDistributor.PreviouslySent.TryGetValue(
                    receiver.netId,
                    out var lastSent))
            {
                return;
            }

            if (!lastSent.TryGetValue(target.netId, out var last))
                return;

            _lastSyncData = last;
            _cedModHasPrevious = true;
        }
        finally
        {
            // Only Talky's prefix work is charged. The patched CedMod method executes after
            // this returns and must not be attributed to Talky's adaptive budget.
            monitor?.End(PerformanceMetric.CedModRoleSync, started);
        }
    }
    

    public FpcSyncData CallPostfix(ReferenceHub receiver, ReferenceHub target, FirstPersonMovementModule fpmm, bool isInvisible,
        bool nearPositioning,FpcSyncData original)
    {
        TalkyRuntime runtime = Runtime;
        if (runtime == null || !runtime.HasActiveLookTargets)
        {
            _cedModHasPrevious = false;
            return original;
        }

        TalkyPerformanceMonitor monitor = Plugin.Instance?.PerformanceMonitor;
        long started = monitor?.Begin(PerformanceMetric.CedModPostfix) ??
                       PerformanceProfiler.NotSampled;
        try
        {
            if (!runtime.IsLookTargetActive(target.netId))
                return original;
            if (!_cedModHasPrevious)
                return original;

            if (target == receiver) return original; // Don't modify own player data

            if (target.IsSpectatedBy(receiver))
            {
                if(Plugin.Instance?.PerformanceMonitor?.CurrentPolicy?.SpectatorHeadMovementsEnabled != true)
                    return original;
            }
        
            if (target.roleManager.CurrentRole is not IFpcRole) return original;

            if (!runtime.TryGetLookOverride(target.netId, out LookOverride tracker) || tracker == null) return original;
        
            if(tracker.NextLookOffset == Vector2.zero && tracker.LastLookOffset == Vector2.zero) return original;
        
            Vector2 lookOffset = tracker.NextLookOffset;
            float originalHorizontal = fpmm.MouseLook.CurrentHorizontal;
            float originalVertical = fpmm.MouseLook.CurrentVertical;
            try
            {
                fpmm.MouseLook.CurrentHorizontal = originalHorizontal + lookOffset.x;
                fpmm.MouseLook.CurrentVertical = originalVertical + lookOffset.y;
                FpcSyncData rewritten = new FpcSyncData(
                    _lastSyncData,
                    original._state,
                    original._bitCustom,
                    original._position,
                    fpmm.MouseLook);
                monitor?.Count(PerformanceCounter.Rewrites);
                return rewritten;
            }
            finally
            {
                fpmm.MouseLook.CurrentHorizontal = originalHorizontal;
                fpmm.MouseLook.CurrentVertical = originalVertical;
            }
        }
        finally
        {
            // As with the prefix, record only Talky's postfix work.
            monitor?.End(PerformanceMetric.CedModPostfix, started);
            _cedModHasPrevious = false;
        }
    }


    private void OnShoot(PlayerShotWeaponEventArgs ev)
    {
        if (ev.FirearmItem.Base.TryGetModule(out IAdsModule module) && module.AdsTarget) return; // Don't reset if aiming down sights

        if (TryGetLookOverride(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time + 1f;
    }

    private void ToggleAds(PlayerAimedWeaponEventArgs ev)
    {
        if (!TryGetLookOverride(ev.Player.ReferenceHub.netId, out var lookOverride) || lookOverride == null)
            return;

        lookOverride.LastBusyTime = ev.Aiming ? float.MaxValue : Time.time;
    }

    private void OnStartUsingItem(PlayerUsingItemEventArgs ev)
    {
        if (TryGetLookOverride(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time + ev.UsableItem.UseDuration;
    }

    private void OnFinishUsedItem(PlayerUsedItemEventArgs ev)
    {
        if (TryGetLookOverride(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time;
    }

    private void OnCancelUseItem(PlayerCancelledUsingItemEventArgs ev)
    {
        if (TryGetLookOverride(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time;
    }
    
    private void OnPickingUp(PlayerSearchingPickupEventArgs ev)
    {
        if (TryGetLookOverride(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time + (ev.Pickup.Base as ISearchable).SearchTimeForPlayer(ev.Player.ReferenceHub);
    }

    private void OnPickedUp(PlayerSearchedPickupEventArgs ev)
    {
        if (TryGetLookOverride(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time;
    }

    private void OnHurt(PlayerHurtEventArgs ev)
    {
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

    public void RegisterEvents()
    {
        if (_registered)
            return;

        _registered = true;
        
        LabApi.Events.Handlers.PlayerEvents.ShotWeapon += OnShoot;
        LabApi.Events.Handlers.PlayerEvents.AimedWeapon += ToggleAds;
        LabApi.Events.Handlers.PlayerEvents.UsingItem += OnStartUsingItem;
        LabApi.Events.Handlers.PlayerEvents.UsedItem += OnFinishUsedItem;
        LabApi.Events.Handlers.PlayerEvents.CancelledUsingItem += OnCancelUseItem;
        LabApi.Events.Handlers.PlayerEvents.SearchingPickup += OnPickingUp;
        LabApi.Events.Handlers.PlayerEvents.SearchedPickup += OnPickedUp;
        LabApi.Events.Handlers.PlayerEvents.Hurt += OnHurt;
        
        if (RequireHarmonyPatch) return;
        LabApi.Events.Handlers.PlayerEvents.ValidatedVisibility += OnPlayerValidatedVisibility;
        FpcServerPositionDistributor.RoleSyncEvent += OnRoleSyncEvent;
    }

    public void UnregisterEvents()
    {
        if (!_registered)
            return;

        _registered = false;
        
        LabApi.Events.Handlers.PlayerEvents.ShotWeapon -= OnShoot;
        LabApi.Events.Handlers.PlayerEvents.AimedWeapon -= ToggleAds;
        LabApi.Events.Handlers.PlayerEvents.UsingItem -= OnStartUsingItem;
        LabApi.Events.Handlers.PlayerEvents.UsedItem -= OnFinishUsedItem;
        LabApi.Events.Handlers.PlayerEvents.CancelledUsingItem -= OnCancelUseItem;
        LabApi.Events.Handlers.PlayerEvents.SearchingPickup -= OnPickingUp;
        LabApi.Events.Handlers.PlayerEvents.SearchedPickup -= OnPickedUp;
        LabApi.Events.Handlers.PlayerEvents.Hurt -= OnHurt;
        ResetDirectSyncState();
        
        if (RequireHarmonyPatch) return;
        LabApi.Events.Handlers.PlayerEvents.ValidatedVisibility -= OnPlayerValidatedVisibility;
        FpcServerPositionDistributor.RoleSyncEvent -= OnRoleSyncEvent;
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
