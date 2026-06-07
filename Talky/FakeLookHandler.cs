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
using UnityEngine;

namespace Talky;

public class FakeLookHandler
{
    private FpcSyncData _lastSyncData;
    private int _index;
    public bool RequireHarmonyPatch = false;

    // Cache LookOverride components to avoid TryGetComponent calls in hot paths
    private readonly Dictionary<uint, LookOverride> _lookOverrideCache = new Dictionary<uint, LookOverride>();

    private void OnSpawn(PlayerSpawnedEventArgs ev)
    {
        //if (IncompatiblePluginDetected) return;
        var hub = ev.Player.ReferenceHub;
        if (!hub.TryGetComponent(out LookOverride tracker))
        {
            tracker = hub.gameObject.AddComponent<LookOverride>();
        }
        _lookOverrideCache[hub.netId] = tracker;
    }
    
    
    // Save the last sync data before it is changed
    private void OnPlayerValidatedVisibility(PlayerValidatedVisibilityEventArgs ev)
    {
        _lastSyncData = FpcServerPositionDistributor.GetPrevSyncData(ev.Player.ReferenceHub, ev.Target.ReferenceHub);
    }
    
    
    // This is fired in FpcServerPositionDistributor just after the position buffer is written by FpcServerPositionDistributor.GetNewSyncData
    private RoleTypeId OnRoleSyncEvent(ReferenceHub target, ReferenceHub receiver, RoleTypeId role, NetworkWriter writer)
    {
        
        // Use an O(1) method to find the index of the target in the buffer
        if(_index+1 >= FpcServerPositionDistributor._bufferPlayerIDs.Length)
        {
            // Reached end of buffer, this should never happen as the buffer is always 10 larger than the number of players
            return role;
        }
        if (FpcServerPositionDistributor._bufferPlayerIDs[_index+1] == target.PlayerId)
        {
            // Player was added to the buffer so is not invisible.
            _index++;
        } else if (FpcServerPositionDistributor._bufferPlayerIDs[0] == target.PlayerId)
        {
            // We've started a new buffer cycle
            _index = 0;
        } else if (FpcServerPositionDistributor._bufferPlayerIDs[0] == 0)
        {
            // No one in the buffer yet
            return role;
        }
        else
        {
            // No new players have been added to the buffer since last time
            return role;
        }
        
        if (target == receiver)
        {
            return role; // Don't modify own player data
        }

        if (target.IsSpectatedBy(receiver))
        {
            if(!Plugin.Instance.Config.EnabledSpectatorHeadMovements)
                return role;
        }

        // Use cached LookOverride instead of TryGetComponent
        if (!_lookOverrideCache.TryGetValue(target.netId, out LookOverride tracker) || tracker == null)
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
        fpmm.MouseLook.CurrentHorizontal += lookOffset.x;
        fpmm.MouseLook.CurrentVertical += lookOffset.y;
        var newSyncData = FpcServerPositionDistributor.GetNewSyncData(receiver, target, fpmm, false); // Can set isInvisible to false here because it should never reach here if they are invisible.
        fpmm.MouseLook.CurrentVertical -= lookOffset.y;
        fpmm.MouseLook.CurrentHorizontal -= lookOffset.x;
        FpcServerPositionDistributor._bufferSyncData[_index] = newSyncData;
        return role;
    }

    public void CallPrefix(ReferenceHub receiver, ReferenceHub target, FirstPersonMovementModule fpmm, bool isInvisible,
        bool nearPositioning)
    {
        if (!FpcServerPositionDistributor.PreviouslySent.TryGetValue(receiver.netId, out var lastSent)) return;
        if (!lastSent.TryGetValue(target.netId, out var last)) return;
        _lastSyncData = last;
    }
    

    public FpcSyncData CallPostfix(ReferenceHub receiver, ReferenceHub target, FirstPersonMovementModule fpmm, bool isInvisible,
        bool nearPositioning,FpcSyncData original)
    {
        if (target == receiver) return original; // Don't modify own player data

        if (target.IsSpectatedBy(receiver))
        {
            if(!Plugin.Instance.Config.EnabledSpectatorHeadMovements)
                return original;
        }
        
        if (target.roleManager.CurrentRole is not IFpcRole) return original;

        // Use cached LookOverride instead of TryGetComponent
        if (!_lookOverrideCache.TryGetValue(target.netId, out LookOverride tracker) || tracker == null) return original;
        
        if(tracker.NextLookOffset == Vector2.zero && tracker.LastLookOffset == Vector2.zero) return original;
        
        Vector2 lookOffset = tracker.NextLookOffset;
        fpmm.MouseLook.CurrentHorizontal += lookOffset.x;
        fpmm.MouseLook.CurrentVertical += lookOffset.y;
        var newSyncData = new FpcSyncData(_lastSyncData, original._state, original._bitCustom, original._position,
            fpmm.MouseLook);
        fpmm.MouseLook.CurrentVertical -= lookOffset.y;
        fpmm.MouseLook.CurrentHorizontal -= lookOffset.x;
        return newSyncData;
    }


    private void OnShoot(PlayerShotWeaponEventArgs ev)
    {
        if (ev.FirearmItem.Base.TryGetModule(out IAdsModule module) && module.AdsTarget) return; // Don't reset if aiming down sights

        if (_lookOverrideCache.TryGetValue(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time + 1f;
    }

    private void ToggleAds(PlayerAimedWeaponEventArgs ev)
    {
        if (!_lookOverrideCache.TryGetValue(ev.Player.ReferenceHub.netId, out var lookOverride) || lookOverride == null)
            return;

        lookOverride.LastBusyTime = ev.Aiming ? float.MaxValue : Time.time;
    }

    private void OnStartUsingItem(PlayerUsingItemEventArgs ev)
    {
        if (_lookOverrideCache.TryGetValue(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time + ev.UsableItem.UseDuration;
    }

    private void OnFinishUsedItem(PlayerUsedItemEventArgs ev)
    {
        if (_lookOverrideCache.TryGetValue(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time;
    }

    private void OnCancelUseItem(PlayerCancelledUsingItemEventArgs ev)
    {
        if (_lookOverrideCache.TryGetValue(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time;
    }
    
    private void OnPickingUp(PlayerSearchingPickupEventArgs ev)
    {
        if (_lookOverrideCache.TryGetValue(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time + (ev.Pickup.Base as ISearchable).SearchTimeForPlayer(ev.Player.ReferenceHub);
    }

    private void OnPickedUp(PlayerSearchedPickupEventArgs ev)
    {
        if (_lookOverrideCache.TryGetValue(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time;
    }

    private void OnRoundRestart()
    {
        _lookOverrideCache.Clear();
    }

    private void OnHurt(PlayerHurtEventArgs ev)
    {
        if(!Plugin.Instance.Config.EnableReactionOnHurt) return;
        if(!_lookOverrideCache.TryGetValue(ev.Player.NetworkId, out LookOverride tracker) || tracker==null)
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

    private void OnPlayerLeft(PlayerLeftEventArgs ev)
    {
        _lookOverrideCache.Remove(ev.Player.ReferenceHub.netId);
    }

    public void RegisterEvents()
    {
        
        LabApi.Events.Handlers.PlayerEvents.ShotWeapon += OnShoot;
        LabApi.Events.Handlers.PlayerEvents.AimedWeapon += ToggleAds;
        LabApi.Events.Handlers.PlayerEvents.UsingItem += OnStartUsingItem;
        LabApi.Events.Handlers.PlayerEvents.UsedItem += OnFinishUsedItem;
        LabApi.Events.Handlers.PlayerEvents.CancelledUsingItem += OnCancelUseItem;
        LabApi.Events.Handlers.PlayerEvents.SearchingPickup += OnPickingUp;
        LabApi.Events.Handlers.PlayerEvents.SearchedPickup += OnPickedUp;
        LabApi.Events.Handlers.PlayerEvents.Left += OnPlayerLeft;
        LabApi.Events.Handlers.ServerEvents.RoundRestarted  += OnRoundRestart;
        LabApi.Events.Handlers.PlayerEvents.Hurt += OnHurt;

        LabApi.Events.Handlers.PlayerEvents.Spawned += OnSpawn;
        
        if (RequireHarmonyPatch) return;
        LabApi.Events.Handlers.PlayerEvents.ValidatedVisibility += OnPlayerValidatedVisibility;
        FpcServerPositionDistributor.RoleSyncEvent += OnRoleSyncEvent;
    }

    public void UnregisterEvents()
    {
        
        LabApi.Events.Handlers.PlayerEvents.ShotWeapon -= OnShoot;
        LabApi.Events.Handlers.PlayerEvents.AimedWeapon -= ToggleAds;
        LabApi.Events.Handlers.PlayerEvents.UsingItem -= OnStartUsingItem;
        LabApi.Events.Handlers.PlayerEvents.UsedItem -= OnFinishUsedItem;
        LabApi.Events.Handlers.PlayerEvents.CancelledUsingItem -= OnCancelUseItem;
        LabApi.Events.Handlers.PlayerEvents.SearchingPickup -= OnPickingUp;
        LabApi.Events.Handlers.PlayerEvents.SearchedPickup -= OnPickedUp;
        LabApi.Events.Handlers.PlayerEvents.Left -= OnPlayerLeft;
        LabApi.Events.Handlers.ServerEvents.RoundRestarted -= OnRoundRestart;
        LabApi.Events.Handlers.PlayerEvents.Hurt -= OnHurt;

        LabApi.Events.Handlers.PlayerEvents.Spawned -= OnSpawn;
        _lookOverrideCache.Clear();
        
        if (RequireHarmonyPatch) return;
        LabApi.Events.Handlers.PlayerEvents.ValidatedVisibility -= OnPlayerValidatedVisibility;
        FpcServerPositionDistributor.RoleSyncEvent -= OnRoleSyncEvent;
    }
}