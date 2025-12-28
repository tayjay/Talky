using LabApi.Events.Arguments.PlayerEvents;
using Mirror;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.NetworkMessages;
using PlayerRoles.Spectating;

namespace Talky;

public class HeadBobHandler
{
    
    private FpcSyncData _lastSyncData;

    // Save the last sync data before it is changed
    public void OnPlayerValidatedVisibility(PlayerValidatedVisibilityEventArgs ev)
    {
        _lastSyncData = FpcServerPositionDistributor.GetPrevSyncData(ev.Player.ReferenceHub, ev.Target.ReferenceHub);
    }
    
    
    // This is fired in FpcServerPositionDistributor just after the position buffer is written by FpcServerPositionDistributor.GetNewSyncData
    public RoleTypeId OnRoleSyncEvent(ReferenceHub target, ReferenceHub receiver, RoleTypeId role, NetworkWriter writer)
        {
            
            if (target == receiver)
            {
                return role; // Don't modify own player data
            }

            if (target.IsSpectatedBy(receiver))
            {
                // Don't want to move camera for spectators
                return role;
            }

            if (!target.TryGetComponent(out SpeechTracker tracker))
            {
                return role;
            }

            if (!tracker.ShouldHeadBob)
            {
                return role;
            }

            if (tracker.CurrentVolumeRatio < 0)
            {
                return role;
            }

            if (target.roleManager.CurrentRole is not IFpcRole currentRole)
            {
                return role;
            }
            
            FirstPersonMovementModule fpmm = currentRole.FpcModule;
            for (int i = 0; i < FpcServerPositionDistributor._bufferPlayerIDs.Length; i++)
            {
                if(FpcServerPositionDistributor._bufferPlayerIDs[i]==0||FpcServerPositionDistributor._bufferPlayerIDs[i] != target.PlayerId) continue;
                // We found the correct player index
                // Now we need to modify the sync data
                FpcServerPositionDistributor.PreviouslySent[receiver.netId][target.netId] = _lastSyncData;
                fpmm.MouseLook.CurrentVertical += (tracker.CurrentVolumeRatio -0.25f) * Plugin.Instance.Config.HeadBobAmount; 
                var newSyncData = FpcServerPositionDistributor.GetNewSyncData(receiver, target, fpmm, false); // Can set isInvisible to false here because it should never reach here if they are invisible.
                fpmm.MouseLook.CurrentVertical -= (tracker.CurrentVolumeRatio -0.25f) *Plugin.Instance.Config.HeadBobAmount;
                FpcServerPositionDistributor._bufferSyncData[i] = newSyncData;
                break;
            }


            if (tracker.CurrentVolumeRatio == 0)
            {
                tracker.CurrentVolumeRatio = -1f; // Mark as processed
            }
            return role;
        }
    

    public void RegisterEvents()
    {
        LabApi.Events.Handlers.PlayerEvents.ValidatedVisibility += OnPlayerValidatedVisibility;
        FpcServerPositionDistributor.RoleSyncEvent += OnRoleSyncEvent;
    }
    
    public void UnregisterEvents()
    {
        LabApi.Events.Handlers.PlayerEvents.ValidatedVisibility -= OnPlayerValidatedVisibility;
        FpcServerPositionDistributor.RoleSyncEvent -= OnRoleSyncEvent;
    }
}