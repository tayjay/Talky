using System;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.NetworkMessages;

namespace Talky.Bridges;

internal static class HarmonyBridge
{
    //public static Harmony HarmonyInstance;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool HarmonyPresent()
    {
        return AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "0Harmony");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ApplyPatches()
    {
        if (!HarmonyPresent()) return false;

        var harmony = new Harmony("ca.taytay.talky");
        
        // Applying a prefix and postfix to CedMod patch
        var original = AccessTools.Method("CedMod.Addons.Sentinal.Patches.FpcServerPositionDistributorPatch:GetNewSyncData",
            [typeof(ReferenceHub), typeof(ReferenceHub), typeof(FirstPersonMovementModule), typeof(bool),typeof(bool)]);

        if (original == null) return false;
        
        harmony.Patch(original, new HarmonyMethod(typeof(HarmonyBridge), nameof(Prefix)),
            new HarmonyMethod(typeof(HarmonyBridge), nameof(Postfix)));

        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void RemovePatches()
    {
        if(!HarmonyPresent()) return;
        
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