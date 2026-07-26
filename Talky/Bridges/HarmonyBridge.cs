using System;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.NetworkMessages;

namespace Talky.Bridges;

internal static class HarmonyBridge
{
    private const string HarmonyId = "talky";
    private static Harmony _harmony;
    private static bool _patched;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool HarmonyPresent()
    {
        return AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "0Harmony");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ApplyPatches()
    {
        if (!HarmonyPresent()) return false;
        if (_patched) return true;

        _harmony = new Harmony(HarmonyId);
        
        // Applying a prefix and postfix to CedMod patch
        var original = AccessTools.Method("CedMod.Addons.Sentinal.Patches.FpcServerPositionDistributorPatch:GetNewSyncData",
            [typeof(ReferenceHub), typeof(ReferenceHub), typeof(FirstPersonMovementModule), typeof(bool),typeof(bool)]);

        if (original == null) return false;
        
        try
        {
            _harmony.Patch(original, new HarmonyMethod(typeof(HarmonyBridge), nameof(Prefix)),
                new HarmonyMethod(typeof(HarmonyBridge), nameof(Postfix)));

            _patched = true;
            return true;
        }
        catch
        {
            _harmony.UnpatchAll(HarmonyId);
            _harmony = null;
            _patched = false;
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void RemovePatches()
    {
        if(!HarmonyPresent()) return;
        
        _harmony?.UnpatchAll(HarmonyId);
        _harmony = null;
        _patched = false;
    }

    static void Prefix(ReferenceHub receiver, ReferenceHub target, FirstPersonMovementModule fpmm, bool isInvisible,
        bool nearPositioning)
    {
        Plugin.Instance?.FakeLookHandler?.CallPrefix(receiver, target, fpmm, isInvisible, nearPositioning);
    }

    static void Postfix(ReferenceHub receiver, ReferenceHub target, FirstPersonMovementModule fpmm, bool isInvisible,
        bool nearPositioning, ref FpcSyncData __result)
    {
        var handler = Plugin.Instance?.FakeLookHandler;
        if (handler != null)
            __result = handler.CallPostfix(receiver, target, fpmm, isInvisible, nearPositioning, __result);
    }
}
