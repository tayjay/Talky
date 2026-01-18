using System.Collections.Generic;
using InventorySystem.Items.Firearms.Modules;
using InventorySystem.Searching;
using LabApi.Events.Arguments.PlayerEvents;
using Mirror;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.NetworkMessages;
using PlayerRoles.PlayableScps.Scp173;
using PlayerRoles.Spectating;
using UnityEngine;

namespace Talky;

public class FakeLookHandler
{
    private FpcSyncData _lastSyncData;
    private int _index = 0;
    public bool IncompatiblePluginDetected = false;

    // Cache LookOverride components to avoid TryGetComponent calls in hot paths
    public readonly Dictionary<uint, LookOverride> LookOverrideCache = new Dictionary<uint, LookOverride>();
    

    public void OnSpawn(PlayerSpawnedEventArgs ev)
    {
        if (IncompatiblePluginDetected) return;
        var hub = ev.Player.ReferenceHub;
        if (!hub.TryGetComponent(out LookOverride tracker))
        {
            tracker = hub.gameObject.AddComponent<LookOverride>();
        }
        LookOverrideCache[hub.netId] = tracker;
    }
    
    
    // Save the last sync data before it is changed
    public void OnPlayerValidatedVisibility(PlayerValidatedVisibilityEventArgs ev)
    {
        _lastSyncData = FpcServerPositionDistributor.GetPrevSyncData(ev.Player.ReferenceHub, ev.Target.ReferenceHub);
    }
    
    
    // This is fired in FpcServerPositionDistributor just after the position buffer is written by FpcServerPositionDistributor.GetNewSyncData
    public RoleTypeId OnRoleSyncEvent(ReferenceHub target, ReferenceHub receiver, RoleTypeId role, NetworkWriter writer)
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
            // Don't want to move camera for spectators
            return role;
        }

        // Use cached LookOverride instead of TryGetComponent
        if (!LookOverrideCache.TryGetValue(target.netId, out LookOverride tracker) || tracker == null)
        {
            return role;
        }

        if (target.roleManager.CurrentRole is not IFpcRole currentRole)
        {
            return role;
        }
        
        FirstPersonMovementModule fpmm = currentRole.FpcModule;
        // Now we need to modify the sync data
        FpcServerPositionDistributor.PreviouslySent[receiver.netId][target.netId] = _lastSyncData;
        Vector2 lookOffset = tracker.GetLookOffset();
        fpmm.MouseLook.CurrentHorizontal += lookOffset.x;
        fpmm.MouseLook.CurrentVertical += lookOffset.y;
        var newSyncData = FpcServerPositionDistributor.GetNewSyncData(receiver, target, fpmm, false); // Can set isInvisible to false here because it should never reach here if they are invisible.
        fpmm.MouseLook.CurrentVertical -= lookOffset.y;
        fpmm.MouseLook.CurrentHorizontal -= lookOffset.x;
        FpcServerPositionDistributor._bufferSyncData[_index] = newSyncData;
        
        return role;
    }

    

    public void OnShoot(PlayerShotWeaponEventArgs ev)
    {
        if (ev.FirearmItem.Base.TryGetModule<IAdsModule>(out IAdsModule module) && module.AdsTarget) return; // Don't reset if aiming down sights

        if (LookOverrideCache.TryGetValue(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time + 1f;
    }

    public void ToggleAds(PlayerAimedWeaponEventArgs ev)
    {
        if (!LookOverrideCache.TryGetValue(ev.Player.ReferenceHub.netId, out var lookOverride) || lookOverride == null)
            return;

        if (ev.Aiming)
        {
            lookOverride.LastBusyTime = float.MaxValue;
        }
        else
        {
            lookOverride.LastBusyTime = Time.time;
        }
    }

    public void OnStartUsingItem(PlayerUsingItemEventArgs ev)
    {
        if (LookOverrideCache.TryGetValue(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time + ev.UsableItem.UseDuration;
    }

    public void OnFinishUsedItem(PlayerUsedItemEventArgs ev)
    {
        if (LookOverrideCache.TryGetValue(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time;
    }

    public void OnCancelUseItem(PlayerCancelledUsingItemEventArgs ev)
    {
        if (LookOverrideCache.TryGetValue(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time;
    }

    public void OnPickingUp(PlayerSearchingPickupEventArgs ev)
    {
        if (LookOverrideCache.TryGetValue(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time + (ev.Pickup.Base as ISearchable).SearchTimeForPlayer(ev.Player.ReferenceHub);
    }

    public void OnPickedUp(PlayerSearchedPickupEventArgs ev)
    {
        if (LookOverrideCache.TryGetValue(ev.Player.ReferenceHub.netId, out var lookOverride) && lookOverride != null)
            lookOverride.LastBusyTime = Time.time;
    }
    
    public void OnRoundRestart()
    {
        LookOverrideCache.Clear();
    }

    public void OnPlayerLeft(PlayerLeftEventArgs ev)
    {
        LookOverrideCache.Remove(ev.Player.ReferenceHub.netId);
    }

    public void RegisterEvents()
    {
        if (IncompatiblePluginDetected) return;
        LabApi.Events.Handlers.PlayerEvents.ShotWeapon += OnShoot;
        LabApi.Events.Handlers.PlayerEvents.AimedWeapon += ToggleAds;
        LabApi.Events.Handlers.PlayerEvents.UsingItem += OnStartUsingItem;
        LabApi.Events.Handlers.PlayerEvents.UsedItem += OnFinishUsedItem;
        LabApi.Events.Handlers.PlayerEvents.CancelledUsingItem += OnCancelUseItem;
        LabApi.Events.Handlers.PlayerEvents.SearchingPickup += OnPickingUp;
        LabApi.Events.Handlers.PlayerEvents.SearchedPickup += OnPickedUp;
        LabApi.Events.Handlers.PlayerEvents.Left += OnPlayerLeft;
        LabApi.Events.Handlers.ServerEvents.RoundRestarted  += OnRoundRestart;

        LabApi.Events.Handlers.PlayerEvents.Spawned += OnSpawn;
        LabApi.Events.Handlers.PlayerEvents.ValidatedVisibility += OnPlayerValidatedVisibility;
        FpcServerPositionDistributor.RoleSyncEvent += OnRoleSyncEvent;
    }

    public void UnregisterEvents()
    {
        if (IncompatiblePluginDetected) return;
        LabApi.Events.Handlers.PlayerEvents.ShotWeapon -= OnShoot;
        LabApi.Events.Handlers.PlayerEvents.AimedWeapon -= ToggleAds;
        LabApi.Events.Handlers.PlayerEvents.UsingItem -= OnStartUsingItem;
        LabApi.Events.Handlers.PlayerEvents.UsedItem -= OnFinishUsedItem;
        LabApi.Events.Handlers.PlayerEvents.CancelledUsingItem -= OnCancelUseItem;
        LabApi.Events.Handlers.PlayerEvents.SearchingPickup -= OnPickingUp;
        LabApi.Events.Handlers.PlayerEvents.SearchedPickup -= OnPickedUp;
        LabApi.Events.Handlers.PlayerEvents.Left -= OnPlayerLeft;
        LabApi.Events.Handlers.ServerEvents.RoundRestarted -= OnRoundRestart;

        LabApi.Events.Handlers.PlayerEvents.Spawned -= OnSpawn;
        LabApi.Events.Handlers.PlayerEvents.ValidatedVisibility -= OnPlayerValidatedVisibility;
        FpcServerPositionDistributor.RoleSyncEvent -= OnRoleSyncEvent;

        LookOverrideCache.Clear();
    }
}