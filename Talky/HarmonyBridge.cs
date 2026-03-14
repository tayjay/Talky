using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using LabApi.Features.Console;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.NetworkMessages;

namespace Talky;

internal static class HarmonyBridge
{
    //public static Harmony HarmonyInstance;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ApplyPatches()
    {
        var harmonyAsm = AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "0Harmony");

        var harmony = new HarmonyLib.Harmony("ca.taytay.talky");
        
        // Applying a prefix and postfix to CedMod patch
        var original = AccessTools.Method("CedMod.Addons.Sentinal.Patches.FpcServerPositionDistributorPatch:GetNewSyncData",
            [typeof(ReferenceHub), typeof(ReferenceHub), typeof(FirstPersonMovementModule), typeof(bool),typeof(bool)]);

        harmony.Patch(original, new HarmonyMethod(typeof(HarmonyBridge), nameof(Prefix)),
            new HarmonyMethod(typeof(HarmonyBridge), nameof(Postfix)));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void RemovePatches()
    {
        var harmony = new Harmony("ca.taytay.talky");
        harmony.UnpatchAll();
    }

    static void Prefix(ReferenceHub receiver, ReferenceHub target, FirstPersonMovementModule fpmm, bool isInvisible,
        bool nearPositioning)
    {
        Plugin.Instance.FakeLookHandler.CallPrefix(receiver, target, fpmm, isInvisible, nearPositioning);
    }

    static void Postfix(ReferenceHub receiver, ReferenceHub target, FirstPersonMovementModule fpmm, bool isInvisible,
        bool nearPositioning, ref FpcSyncData __result)
    {
        __result = Plugin.Instance.FakeLookHandler.CallPostfix(receiver, target, fpmm, isInvisible, nearPositioning,
            __result);
    }
}