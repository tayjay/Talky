using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using LabApi.Features.Console;
using Mirror;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.NetworkMessages;
using Talky.Handlers;

namespace Talky.Bridges;

internal static class HarmonyBridge
{
    internal const string OwnerId = "ca.taytay.talky.cedmod-sync";
    private static int _runtimeOwnershipPossible;

    /// <summary>
    /// This facade deliberately has no HarmonyLib-typed fields, parameters, return values, or
    /// locals. LabAPI installations are allowed to omit Harmony entirely, so the CLR must be able
    /// to load and execute these lifecycle guards without resolving the optional assembly.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool HarmonyPresent()
    {
        return AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
            string.Equals(
                assembly.GetName().Name,
                "0Harmony",
                StringComparison.OrdinalIgnoreCase));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool HasOwnedPatchState()
    {
        if (!RuntimeOwnershipPossible())
            return false;

        if (!HarmonyBridge.HarmonyPresent())
            return true;

        try
        {
            bool owned = HasOwnedPatchStateWithHarmony();
            if (!owned)
                ClearRuntimeOwnershipPossible();
            return owned;
        }
        catch
        {
            // Once Talky has entered the patch implementation, an incompatible/unreadable
            // Harmony runtime cannot prove ownership was released. Fail closed for re-enable.
            return true;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ApplyPatches(Assembly cedModAssembly, out string detail)
    {
        if (!HarmonyPresent())
        {
            detail = "0Harmony is not loaded";
            return false;
        }

        try
        {
            return ApplyPatchesWithHarmony(cedModAssembly, out detail);
        }
        catch (Exception exception)
        {
            detail = "Harmony runtime is incompatible: " + exception.GetType().Name;
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void RemovePatches()
    {
        // If Harmony has never been loaded, Talky cannot have installed a Harmony patch in this
        // AppDomain. Avoid touching the implementation type so optional-dependency resolution
        // remains genuinely lazy.
        if (!RuntimeOwnershipPossible())
            return;

        if (!HarmonyPresent())
        {
            throw new InvalidOperationException(
                "Talky retains possible Harmony patch ownership but 0Harmony is unavailable.");
        }

        try
        {
            RemovePatchesWithHarmony();
            ClearRuntimeOwnershipPossible();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Talky could not verify removal of its Harmony patch ownership.",
                exception);
        }
    }

    internal static void MarkRuntimeOwnershipPossible()
    {
        Interlocked.Exchange(ref _runtimeOwnershipPossible, 1);
    }

    internal static void ClearRuntimeOwnershipPossible()
    {
        Interlocked.Exchange(ref _runtimeOwnershipPossible, 0);
    }

    private static bool RuntimeOwnershipPossible()
    {
        return Volatile.Read(ref _runtimeOwnershipPossible) != 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool HasOwnedPatchStateWithHarmony() =>
        HarmonyRuntimeBridge.HasOwnedPatchState();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ApplyPatchesWithHarmony(
        Assembly cedModAssembly,
        out string detail) =>
        HarmonyRuntimeBridge.ApplyPatches(cedModAssembly, out detail);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RemovePatchesWithHarmony() =>
        HarmonyRuntimeBridge.RemovePatches();
}

/// <summary>
/// Harmony-dependent implementation. No method on this type is reached until the dependency-free
/// facade has proved that 0Harmony is already loaded by the host.
/// </summary>
internal static class HarmonyRuntimeBridge
{
    private const string OwnerId = HarmonyBridge.OwnerId;

    private const string CedModPatchTypeName =
        "CedMod.Addons.Sentinal.Patches.FpcServerPositionDistributorPatch";
    private const string CedModHelperName = "GetNewSyncData";

    private static readonly Type[] TargetParameterTypes =
    {
        typeof(ReferenceHub),
        typeof(ReferenceHub),
        typeof(FirstPersonMovementModule),
        typeof(bool),
        typeof(bool),
    };

    private static readonly MethodInfo PrefixMethod = typeof(HarmonyRuntimeBridge).GetMethod(
        nameof(Prefix),
        BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly MethodInfo PostfixMethod = typeof(HarmonyRuntimeBridge).GetMethod(
        nameof(Postfix),
        BindingFlags.Static | BindingFlags.NonPublic);

    private static Harmony _harmony;
    private static MethodInfo _target;
    private static bool _patched;
    private static int _callbackFailureLogged;

    /// <summary>
    /// Reports both locally retained patch state and owner-scoped Harmony patches left by an
    /// interrupted reload. This is a cold lifecycle check; callback paths never enumerate
    /// Harmony's patch registry.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool HasOwnedPatchState()
    {
        if (_patched || _target != null || _harmony != null)
            return true;

        if (!HarmonyBridge.HarmonyPresent())
            return false;

        try
        {
            return HasHarmonyOwnerPatches();
        }
        catch
        {
            // An unreadable Harmony registry cannot prove that no Talky-owned patch remains.
            return true;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ApplyPatches(Assembly cedModAssembly, out string detail)
    {
        HarmonyBridge.MarkRuntimeOwnershipPossible();

        if (cedModAssembly == null)
        {
            RemovePatches();
            detail = "CedMod assembly was not available";
            return false;
        }

        if (!HarmonyBridge.HarmonyPresent())
        {
            detail = "0Harmony is not loaded";
            return false;
        }

        MethodInfo target = null;
        try
        {
            Type patchType = cedModAssembly.GetType(
                CedModPatchTypeName,
                false,
                false);
            if (patchType == null)
            {
                RemovePatchesInternal();
                detail = "CedMod patch type was not found in " +
                         cedModAssembly.GetName().Name;
                return false;
            }

            target = patchType.GetMethod(
                CedModHelperName,
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly,
                null,
                TargetParameterTypes,
                null);
            if (target == null || target.ReturnType != typeof(FpcSyncData))
            {
                RemovePatchesInternal();
                detail = "CedMod helper signature is incompatible: expected " +
                         ExpectedSignature();
                return false;
            }

            MethodInfo sentinalPrefix = patchType.GetMethod(
                "Prefix",
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly,
                null,
                new[] { typeof(ReferenceHub), typeof(NetworkWriter) },
                null);
            MethodInfo writeAll = typeof(FpcServerPositionDistributor).GetMethod(
                nameof(FpcServerPositionDistributor.WriteAll),
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(ReferenceHub), typeof(NetworkWriter) },
                null);
            if (sentinalPrefix == null || sentinalPrefix.ReturnType != typeof(bool) ||
                writeAll == null || writeAll.ReturnType != typeof(void))
            {
                RemovePatchesInternal();
                detail = "CedMod Sentinal WriteAll prefix signature is incompatible";
                return false;
            }

            if (PrefixMethod == null || PostfixMethod == null)
            {
                RemovePatchesInternal();
                detail = "Talky Harmony callback methods could not be resolved";
                return false;
            }

            Patches writeAllPatches = Harmony.GetPatchInfo(writeAll);
            Patch sentinalPatch = writeAllPatches?.Prefixes.FirstOrDefault(patch =>
                Equals(patch.PatchMethod, sentinalPrefix));
            if (sentinalPatch == null)
            {
                RemovePatchesInternal();
                detail = "CedMod's exact Sentinal WriteAll prefix is not installed";
                return false;
            }

            string sentinalOwner = sentinalPatch.owner;

            if (_patched && Equals(_target, target) && VerifyPatch(target))
            {
                detail = DescribePatch(target, sentinalOwner, true);
                return true;
            }

            RemovePatchesInternal();

            // Re-arm after the sweep. RemovePatchesInternal ends in ClearPatchState, which
            // clears the ownership flag, and everything below this line can leave a live
            // Talky-owned patch behind. Without this the flag is 0 while _patched is true, and
            // RemovePatches() returns at its RuntimeOwnershipPossible guard without unpatching.
            HarmonyBridge.MarkRuntimeOwnershipPossible();
            _harmony = new Harmony(OwnerId);
            _target = target;

            // Remove a stale Talky-owned patch left by an interrupted plugin reload before
            // installing the currently loaded methods.
            _harmony.Unpatch(target, HarmonyPatchType.All, OwnerId);

            HarmonyMethod prefix = new HarmonyMethod(PrefixMethod)
            {
                priority = Priority.Last,
            };
            HarmonyMethod postfix = new HarmonyMethod(PostfixMethod)
            {
                priority = Priority.Last,
            };
            _harmony.Patch(target, prefix, postfix);

            if (!VerifyPatch(target))
            {
                _harmony.Unpatch(target, HarmonyPatchType.All, OwnerId);
                ClearPatchState();
                detail = DescribePatch(target, sentinalOwner, false);
                return false;
            }

            _patched = true;
            Interlocked.Exchange(ref _callbackFailureLogged, 0);
            detail = DescribePatch(target, sentinalOwner, true);
            return true;
        }
        catch (Exception exception)
        {
            MethodInfo ownedTarget = _target;
            bool ownedRemoved = TryUnpatch(ownedTarget);
            bool targetRemoved = Equals(target, ownedTarget) || TryUnpatch(target);
            MethodInfo retainedTarget = !ownedRemoved
                ? ownedTarget
                : !targetRemoved
                    ? target
                    : null;
            if (retainedTarget == null)
            {
                ClearPatchState();
            }
            else
            {
                // Retain the handle so Disable or a later apply can retry owner-scoped
                // cleanup. Runtime mode guards keep a possibly stale patch inert.
                _target = retainedTarget;
                _patched = true;
            }

            detail = "CedMod helper patch failed: " + exception.GetType().Name +
                     ": " + exception.Message;
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void RemovePatches()
    {
        if (!HarmonyBridge.HarmonyPresent())
        {
            if (_patched || _target != null || _harmony != null)
            {
                throw new InvalidOperationException(
                    "Talky retains CedMod patch ownership but Harmony is unavailable.");
            }

            return;
        }

        try
        {
            RemovePatchesInternal();
        }
        catch (Exception exception)
        {
            Logger.Error("Talky failed to remove its CedMod sync patch: " + exception);
            // Keep the target and owner handle so a later Disable can retry cleanup.
            throw;
        }
    }

    private static void RemovePatchesInternal()
    {
        Harmony harmony = _harmony ?? new Harmony(OwnerId);
        if (_target != null)
        {
            harmony.Unpatch(
                _target,
                HarmonyPatchType.All,
                OwnerId);
        }

        // A previous plugin instance may have lost its MethodInfo handle while Harmony retained
        // the owner-scoped patch. Sweep only this plugin's owner id and verify it is gone.
        foreach (MethodBase method in Harmony.GetAllPatchedMethods().ToArray())
        {
            Patches patches = Harmony.GetPatchInfo(method);
            if (patches?.Owners?.Contains(OwnerId) == true)
                harmony.Unpatch(method, HarmonyPatchType.All, OwnerId);
        }

        if (HasHarmonyOwnerPatches())
        {
            _harmony = harmony;
            _patched = true;
            throw new InvalidOperationException(
                "Harmony still reports Talky-owned CedMod patches after cleanup.");
        }

        ClearPatchState();
    }

    private static bool HasHarmonyOwnerPatches()
    {
        foreach (MethodBase method in Harmony.GetAllPatchedMethods())
        {
            Patches patches = Harmony.GetPatchInfo(method);
            if (patches?.Owners?.Contains(OwnerId) == true)
                return true;
        }

        return false;
    }

    private static void ClearPatchState()
    {
        _harmony = null;
        _target = null;
        _patched = false;
        Interlocked.Exchange(ref _callbackFailureLogged, 0);
        HarmonyBridge.ClearRuntimeOwnershipPossible();
    }

    private static bool TryUnpatch(MethodInfo target)
    {
        if (target == null)
            return true;

        try
        {
            (_harmony ?? new Harmony(OwnerId)).Unpatch(
                target,
                HarmonyPatchType.All,
                OwnerId);
            return true;
        }
        catch
        {
            // Preserve the original patching failure for the caller's diagnostic.
            return false;
        }
    }

    private static bool VerifyPatch(MethodInfo target)
    {
        Patches patches = Harmony.GetPatchInfo(target);
        return patches != null &&
               patches.Prefixes.Any(patch =>
                   patch.owner == OwnerId && Equals(patch.PatchMethod, PrefixMethod)) &&
               patches.Postfixes.Any(patch =>
                   patch.owner == OwnerId && Equals(patch.PatchMethod, PostfixMethod));
    }

    private static string ExpectedSignature() =>
        "FpcSyncData GetNewSyncData(ReferenceHub, ReferenceHub, " +
        "FirstPersonMovementModule, Boolean, Boolean)";

    private static string DescribePatch(
        MethodInfo target,
        string sentinalOwner,
        bool verified)
    {
        Version harmonyVersion = typeof(Harmony).Assembly.GetName().Version;
        return $"owner={OwnerId}, harmony={harmonyVersion}, " +
               $"sentinal_owner={sentinalOwner}, target=" +
               $"{target.DeclaringType?.FullName}.{target}, verified={verified}";
    }

    private static void Prefix(
        ReferenceHub __0,
        ReferenceHub __1,
        FirstPersonMovementModule __2,
        bool __3,
        bool __4,
        out CedModSyncPatchState __state)
    {
        __state = default;
        try
        {
            FakeLookHandler handler = Plugin.Instance?.FakeLookHandler;
            if (handler != null)
                __state = handler.CaptureCedModSync(__0, __1, __2, __3, __4);
        }
        catch (Exception exception)
        {
            LogCallbackFailure("prefix", exception);
        }
    }

    private static void Postfix(
        ReferenceHub __0,
        ReferenceHub __1,
        FirstPersonMovementModule __2,
        bool __3,
        bool __4,
        bool __runOriginal,
        CedModSyncPatchState __state,
        ref FpcSyncData __result)
    {
        try
        {
            FakeLookHandler handler = Plugin.Instance?.FakeLookHandler;
            handler?.RewriteCedModSync(
                __0,
                __1,
                __2,
                __3,
                __4,
                __runOriginal,
                __state,
                ref __result);
        }
        catch (Exception exception)
        {
            LogCallbackFailure("postfix", exception);
        }
    }

    private static void LogCallbackFailure(string phase, Exception exception)
    {
        if (Interlocked.Exchange(ref _callbackFailureLogged, 1) != 0)
            return;

        try
        {
            Logger.Error(
                $"Talky's CedMod sync {phase} failed; CedMod processing will continue: " +
                exception);
        }
        catch
        {
            // Never let diagnostic logging break CedMod's synchronization loop.
        }
    }
}
