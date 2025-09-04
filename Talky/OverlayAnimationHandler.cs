using System;
using System.Collections.Generic;
using System.Linq;
using InventorySystem.Items.Firearms.Thirdperson;
using LabApi.Events.Arguments.PlayerEvents;
using LabApi.Features.Wrappers;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.Thirdperson;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers.OverlayAnims;
using UnityEngine;
using Logger = LabApi.Features.Console.Logger;

namespace Talky;

public class OverlayAnimationHandler
{
    public void OnSneak(PlayerMovementStateChangedEventArgs ev)
    {
        try
        {
                foreach (Player npc in Player.DummyList)
                {
                    OverlayAnimationsSubcontroller subcontroller;
                    if (!(npc.ReferenceHub.roleManager.CurrentRole is IFpcRole currentRole) ||
                        !(currentRole.FpcModule.CharacterModelInstance is AnimatedCharacterModel
                            characterModelInstance) ||
                        !characterModelInstance.TryGetSubcontroller<OverlayAnimationsSubcontroller>(out subcontroller))
                    {
                        // Non-animated character model speaking
                        Logger.Debug("Failed to get OverlayAnimationsSubcontroller from NPC");

                        return;
                    }
                    
                    if(!characterModelInstance.TryGetSubcontroller<EmotionSubcontroller>(out var emotionSubcontroller))
                    {
                        Logger.Debug("Failed to get EmotionSubcontroller from NPC");
                        return;
                    }

                    int index = -1;
                    if (ev.NewState == PlayerMovementState.Sneaking)
                    {
                        index = 1;
                    } else if (ev.NewState == PlayerMovementState.Sprinting)
                    {
                        index = 0;
                    }

                    if (index >= 0)
                    {
                        subcontroller._overlayAnimations[index].IsPlaying = true;
                        foreach (var renderer in emotionSubcontroller.Renderers)
                        {
                            renderer.SetWeight(EmotionBlendshape.EyeCloseLeft, 1f);
                            renderer.SetWeight(EmotionBlendshape.EyeCloseRight, 1f);
                        }

                        foreach (var clip in characterModelInstance.Animator.runtimeAnimatorController.animationClips)
                        {
                            //Logger.Debug(clip.name + " " + clip.length);
                        }
                        characterModelInstance.Animator.Play("run/forward");
                        //subcontroller._overlayAnimations[index].SendRpc();
                        
                    }
                    
                    

                    
                }
            
        }
        catch (Exception ex)
        {
            Logger.Error($"Error in OnSneak: {ex}");
            return;
        }
        
    }

    public void RegisterEvents()
    {
        LabApi.Events.Handlers.PlayerEvents.MovementStateChanged += OnSneak;
    }
    
    public void UnregisterEvents()
    {
        LabApi.Events.Handlers.PlayerEvents.MovementStateChanged -= OnSneak;
    }
}