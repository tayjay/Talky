using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using LabApi.Features.Console;
using Mirror;
using PlayerRoles;
using PlayerRoles.FirstPersonControl;
using PlayerRoles.FirstPersonControl.NetworkMessages;
using RelativePositioning;
using Talky.Handlers;

namespace Talky.Bridges;

internal static class HarmonyBridge
{
    // Preserve the deployed owner so upgrades can sweep the former CedMod prefix/postfix callbacks.
    internal const string OwnerId = "ca.taytay.talky.cedmod-sync";
    private static int _runtimeOwnershipPossible;

    /// <summary>
    /// Detects Harmony without introducing an eager dependency.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool HarmonyPresent()
    {
        // Asking for the type lets LabAPI/EXILED's dependency resolver load Harmony from its
        // configured dependency directory. Merely enumerating AppDomain assemblies incorrectly
        // reports "missing" when the dependency is installed but no plugin has touched it yet.
        TryRequestHarmonyResolution();
        return AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
            string.Equals(
                assembly.GetName().Name,
                "0Harmony",
                StringComparison.OrdinalIgnoreCase));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool HasOwnedPatchState()
    {
        if (!TryPrepareHarmonyRuntime(out _))
            return RuntimeOwnershipPossible();

        try
        {
            bool owned = HarmonyRuntimeBridge.HasOwnedPatchState();
            if (!owned)
                ClearRuntimeOwnershipPossible();
            return owned;
        }
        catch
        {
            // An unreadable foreign Harmony runtime must not prevent the clean stock fallback.
            // Once Talky has begun a patch transaction, the ownership marker makes the same
            // uncertainty blocking until owner-scoped cleanup can be verified.
            return RuntimeOwnershipPossible();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ApplyStockPatches(out string detail)
    {
        if (!TryPrepareHarmonyRuntime(out detail))
            return false;

        try
        {
            return HarmonyRuntimeBridge.ApplyStockPatches(out detail);
        }
        catch (Exception exception)
        {
            detail = "Harmony runtime is incompatible: " + exception.GetType().Name;
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ApplyCedModPatches(Assembly cedModAssembly, out string detail)
    {
        if (!TryPrepareHarmonyRuntime(out detail))
            return false;

        try
        {
            return HarmonyRuntimeBridge.ApplyCedModPatches(cedModAssembly, out detail);
        }
        catch (Exception exception)
        {
            detail = "Harmony runtime is incompatible: " + exception.GetType().Name;
            return false;
        }
    }

    /// <summary>
    /// Retained for binary/source compatibility with the former CedMod-only bridge entry point.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ApplyPatches(Assembly cedModAssembly, out string detail) =>
        ApplyCedModPatches(cedModAssembly, out detail);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void RemovePatches()
    {
        // Ownership is recorded only after admission bound the direct API. A later
        // foreign Harmony load must not prevent cleanup through that same runtime.
        // New apply transactions still run the strict global admission checks.
        if (!RuntimeOwnershipPossible() && !TryPrepareHarmonyRuntime(out _))
            return;

        try
        {
            // Sweep the owner even when this plugin instance has no local state. This removes
            // callbacks retained by an older Talky assembly after a loader-level reload.
            HarmonyRuntimeBridge.RemovePatches();
            ClearRuntimeOwnershipPossible();
        }
        catch (Exception exception)
        {
            MarkRuntimeOwnershipPossible();
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

    private static bool TryPrepareHarmonyRuntime(out string detail)
    {
        Type resolvedHarmonyType = TryRequestHarmonyResolution();
        if (resolvedHarmonyType == null)
        {
            // Mono caches a failed AssemblyRef on the requesting image. Entering the
            // typed method before EXILED has loaded Harmony would make a later retry
            // fail even after that exact dependency becomes available.
            detail = "0Harmony is absent or its HarmonyLib.Harmony type could not be resolved";
            return false;
        }

        try
        {
            return HarmonyRuntimeBridge.TrySelectHarmonyRuntime(
                resolvedHarmonyType.Assembly,
                out detail);
        }
        catch (Exception exception)
        {
            detail = "0Harmony could not be resolved as a compatible runtime: " +
                     exception.GetType().Name + ": " + exception.Message;
            return false;
        }
    }

    private static Type TryRequestHarmonyResolution()
    {
        try
        {
            // Keep the type name string-based so Talky.dll remains enumerable when Harmony is
            // genuinely absent. The host's normal assembly resolver chooses the runtime binding.
            Type resolved = Type.GetType("HarmonyLib.Harmony, 0Harmony", false, false);
            if (resolved != null)
                return resolved;
        }
        catch
        {
            // A host resolver failure must not hide an already byte-loaded runtime.
        }

        // EXILED byte-loads dependencies. Inspect already-loaded metadata when a
        // host's name-based resolver does not expose that load context.
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (!string.Equals(assembly.GetName().Name, "0Harmony", StringComparison.OrdinalIgnoreCase))
                    continue;

                Type candidate = assembly.GetType("HarmonyLib.Harmony", false, false);
                if (candidate != null)
                    return candidate;
            }
            catch
            {
                // An unrelated same-name assembly cannot prevent later candidates.
            }
        }

        return null;
    }
}

/// <summary>
/// Public BCL-only entry point used by the in-memory transpiler assembly. Its signature remains
/// loadable when Harmony is absent; the runtime bridge supplies the verified instruction state.
/// </summary>
public static class HarmonyInstructionTransformer
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IEnumerable Transform(IEnumerable instructions, MethodBase original)
    {
        return HarmonyRuntimeBridge.TransformInstructions(instructions, original);
    }
}

/// <summary>
/// Harmony-dependent implementation loaded only after the host dependency is present.
/// Direct Harmony types are intentionally confined to method bodies and local variables.
/// </summary>
internal static class HarmonyRuntimeBridge
{
    private enum PatchTargetKind
    {
        None = 0,
        Stock = 1,
        CedMod = 2,
    }

    private const string OwnerId = HarmonyBridge.OwnerId;
    private const string CedModPatchTypeName =
        "CedMod.Addons.Sentinal.Patches.FpcServerPositionDistributorPatch";
    private const string HelperName = "GetNewSyncData";

    private static readonly Type[] StockTargetParameterTypes =
    {
        typeof(ReferenceHub),
        typeof(ReferenceHub),
        typeof(FirstPersonMovementModule),
        typeof(bool),
    };

    private static readonly Type[] CedModTargetParameterTypes =
    {
        typeof(ReferenceHub),
        typeof(ReferenceHub),
        typeof(FirstPersonMovementModule),
        typeof(bool),
        typeof(bool),
    };

    private static readonly Type[] WriteAllParameterTypes =
    {
        typeof(ReferenceHub),
        typeof(NetworkWriter),
    };

    private static readonly MethodInfo MouseLookGetter =
        typeof(FirstPersonMovementModule).GetProperty(
                nameof(FirstPersonMovementModule.MouseLook),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetGetMethod(true);

    private static readonly ConstructorInfo FpcSyncDataConstructor =
        typeof(FpcSyncData).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[]
            {
                typeof(FpcSyncData),
                typeof(PlayerMovementState),
                typeof(bool),
                typeof(RelativePosition),
                typeof(FpcMouseLook),
            },
            null);

    private static readonly MethodInfo SelectorMethod =
        typeof(HarmonyRuntimeBridge).GetMethod(
            nameof(SelectSerializedMouseLook),
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly object HarmonySelectionLock = new object();
    private static Assembly _harmonyAssembly;
    private static object _harmony;
    private static MethodInfo _target;
    private static MethodInfo _transpilerMethod;
    private static Type _codeInstructionType;
    private static Type _typedInstructionEnumerableType;
    private static PatchTargetKind _targetKind;
    private static bool _patched;
    private static int _callbackFailureLogged;
    private static int _dynamicTranspilerGeneration;

    /// <summary>
    /// Uses the CLR-bound Harmony contract. Metadata inspection admits compatible API shapes
    /// without imposing a version gate; all runtime operations use direct typed calls.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool TrySelectHarmonyRuntime(
        Assembly resolverSelectedAssembly,
        out string detail)
    {
        lock (HarmonySelectionLock)
        {
            Assembly boundAssembly = typeof(Harmony).Assembly;
            if (!TryInspectHarmonyContract(boundAssembly, out detail))
                return false;

            if (resolverSelectedAssembly != null &&
                !ReferenceEquals(resolverSelectedAssembly, boundAssembly) &&
                !HasSameBinaryIdentity(resolverSelectedAssembly, boundAssembly))
            {
                detail = "The host resolver and CLR selected different Harmony runtimes";
                return false;
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!IsHarmonyAssemblyName(assembly) ||
                    ReferenceEquals(assembly, boundAssembly) ||
                    HasSameBinaryIdentity(assembly, boundAssembly))
                    continue;

                if (TryInspectHarmonyContract(assembly, out _))
                {
                    detail = "Multiple distinct compatible Harmony binaries are loaded; " +
                             "independent patch ownership cannot be verified through the " +
                             "CLR-bound API: " + DescribeAssembly(assembly);
                    return false;
                }
            }

            _harmonyAssembly = boundAssembly;
            detail = "selected Harmony runtime is compatible (" +
                     DescribeAssembly(boundAssembly) + "); CLR-bound direct API";
            return true;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool HasOwnedPatchState()
    {
        if (_patched || _target != null || _harmony != null)
            return true;

        return HasHarmonyOwnerPatches();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ApplyStockPatches(out string detail)
    {
        MethodInfo target = typeof(FpcServerPositionDistributor).GetMethod(
            HelperName,
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly,
            null,
            StockTargetParameterTypes,
            null);

        return ApplyTarget(PatchTargetKind.Stock, target, null, out detail);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ApplyCedModPatches(Assembly cedModAssembly, out string detail)
    {
        if (cedModAssembly == null)
        {
            detail = "CedMod assembly was not available";
            return false;
        }

        Type patchType = cedModAssembly.GetType(CedModPatchTypeName, false, false);
        if (patchType == null)
        {
            detail = "CedMod patch type was not found in " + cedModAssembly.GetName().Name;
            return false;
        }

        MethodInfo target = patchType.GetMethod(
            HelperName,
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly,
            null,
            CedModTargetParameterTypes,
            null);

        string sentinalOwner;
        if (!TryValidateCedModWriteAllPrefix(patchType, out sentinalOwner, out detail))
            return false;

        return ApplyTarget(PatchTargetKind.CedMod, target, sentinalOwner, out detail);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void RemovePatches()
    {
        try
        {
            RemovePatchesInternal();
        }
        catch (Exception exception)
        {
            Logger.Error("Failed to remove Talky's look-sync Harmony patches: " + exception);
            HarmonyBridge.MarkRuntimeOwnershipPossible();
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ApplyTarget(
        PatchTargetKind kind,
        MethodInfo target,
        string sentinalOwner,
        out string detail)
    {
        if (!TryValidateTarget(kind, target, out string profile, out detail))
            return false;

        if (!TrySelectRuntimeForTarget(kind, target, out detail))
            return false;

        if (_patched && _targetKind == kind && Equals(_target, target) && VerifyPatch(target))
        {
            detail = DescribePatch(kind, target, profile, sentinalOwner, true);
            return true;
        }

        try
        {
            // Remove all patches under the deployed owner, including former prefix/postfix methods.
            HarmonyBridge.MarkRuntimeOwnershipPossible();
            RemovePatchesInternal();

            HarmonyBridge.MarkRuntimeOwnershipPossible();
            _harmony = new Harmony(OwnerId);
            _target = target;
            _targetKind = kind;
            _transpilerMethod = CreateHarmonyTranspiler();

            HarmonyMethod transpiler = new HarmonyMethod(_transpilerMethod)
            {
                priority = Priority.Last,
            };
            ((Harmony)_harmony).Patch(target, transpiler: transpiler);

            if (!VerifyPatch(target))
                throw new InvalidOperationException("Harmony did not report Talky's transpiler.");

            _patched = true;
            Interlocked.Exchange(ref _callbackFailureLogged, 0);
            detail = DescribePatch(kind, target, profile, sentinalOwner, true);
            return true;
        }
        catch (Exception exception)
        {
            bool cleanupSucceeded = false;
            Exception cleanupFailure = null;
            try
            {
                RemovePatchesInternal();
                cleanupSucceeded = true;
            }
            catch (Exception cleanupException)
            {
                cleanupFailure = cleanupException;
                _harmony ??= new Harmony(OwnerId);
                _target ??= target;
                _targetKind = kind;
                _patched = true;
                HarmonyBridge.MarkRuntimeOwnershipPossible();
            }

            if (cleanupSucceeded)
                HarmonyBridge.ClearRuntimeOwnershipPossible();

            detail = $"{kind} look-sync patch failed: " + DescribeExceptionChain(exception);
            if (cleanupFailure != null)
            {
                detail += "; owner cleanup could not be verified: " +
                          DescribeExceptionChain(cleanupFailure);
            }
            return false;
        }
    }

    private static string DescribeExceptionChain(Exception exception)
    {
        List<string> failures = new List<string>();
        Exception current = exception;
        while (current != null)
        {
            string target = current.TargetSite == null
                ? string.Empty
                : " at " + current.TargetSite.DeclaringType?.FullName + "." +
                  current.TargetSite.Name;
            failures.Add(current.GetType().Name + ": " + current.Message + target);
            current = current.InnerException;
        }

        return string.Join(" -> ", failures);
    }

    private static bool TryValidateTarget(
        PatchTargetKind kind,
        MethodInfo target,
        out string profile,
        out string detail)
    {
        profile = null;
        if (target == null || target.ReturnType != typeof(FpcSyncData))
        {
            detail = $"{kind} helper signature is incompatible: expected " +
                     ExpectedSignature(kind);
            return false;
        }

        if (SelectorMethod == null || MouseLookGetter == null || FpcSyncDataConstructor == null)
        {
            detail = "Talky transpiler members or game look-sync members could not be resolved";
            return false;
        }

        MethodBody body = target.GetMethodBody();
        if (body == null || body.ExceptionHandlingClauses.Count != 0)
        {
            detail = $"{kind} helper has a missing or unsupported exception-handling body";
            return false;
        }

        byte[] il = body.GetILAsByteArray();
        if (il == null || il.Length == 0)
        {
            detail = $"{kind} helper has no inspectable IL body";
            return false;
        }

        // MVID and IL digest are observations for diagnosing game/plugin updates, not admission
        // gates. The transpiler below validates the exact constructor anchors and metadata shape
        // transactionally against Harmony's decoded instruction stream.
        profile = $"mvid={TryGetModuleVersionId(target.Module)}," +
                  $"il={ComputeSha256(il)},il_bytes={il.Length}";
        detail = null;
        return true;
    }

    private static bool TryValidateCedModWriteAllPrefix(
        Type patchType,
        out string sentinalOwner,
        out string detail)
    {
        sentinalOwner = null;
        MethodInfo sentinalPrefix = patchType.GetMethod(
            "Prefix",
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly,
            null,
            WriteAllParameterTypes,
            null);
        MethodInfo writeAll = typeof(FpcServerPositionDistributor).GetMethod(
            nameof(FpcServerPositionDistributor.WriteAll),
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            WriteAllParameterTypes,
            null);
        if (sentinalPrefix == null || sentinalPrefix.ReturnType != typeof(bool) ||
            writeAll == null || writeAll.ReturnType != typeof(void))
        {
            detail = "CedMod Sentinal WriteAll prefix signature is incompatible";
            return false;
        }

        if (!TryGetInstalledPrefixOwner(writeAll, sentinalPrefix, out sentinalOwner))
        {
            detail = "CedMod's exact Sentinal WriteAll prefix is not installed in the " +
                     "CLR-bound Harmony runtime";
            return false;
        }

        detail = null;
        return true;
    }

    /// <summary>
    /// Builds an exact Harmony-typed transpiler only after compatible Harmony has loaded. Keeping
    /// that signature out of Talky.dll allows LabAPI to enumerate all Talky types without
    /// resolving 0Harmony. The emitted wrapper delegates to the BCL-only transformer below.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static MethodInfo CreateHarmonyTranspiler()
    {
        if (_transpilerMethod != null && _codeInstructionType != null &&
            _typedInstructionEnumerableType != null)
        {
            return _transpilerMethod;
        }

        _codeInstructionType = typeof(CodeInstruction);
        _typedInstructionEnumerableType = typeof(IEnumerable<CodeInstruction>);
        MethodInfo transformer = typeof(HarmonyInstructionTransformer).GetMethod(
            nameof(HarmonyInstructionTransformer.Transform),
            BindingFlags.Static | BindingFlags.Public);

        if (transformer == null)
        {
            throw new MissingMemberException(
                "Harmony's CodeInstruction shape is incompatible with Talky's transpiler.");
        }

        int generation = Interlocked.Increment(ref _dynamicTranspilerGeneration);
        string assemblyName = "Talky.RuntimeHarmony." + generation;
        AssemblyBuilder assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
            new AssemblyName(assemblyName),
            AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule(assemblyName);
        TypeBuilder type = module.DefineType(
            "Talky.Bridges.Runtime.LookSyncTranspiler" + generation,
            TypeAttributes.Public |
            TypeAttributes.Abstract |
            TypeAttributes.Sealed |
            TypeAttributes.BeforeFieldInit);
        MethodBuilder wrapper = type.DefineMethod(
            "Transpiler",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            _typedInstructionEnumerableType,
            new[] { _typedInstructionEnumerableType, typeof(MethodBase) });
        ILGenerator generator = wrapper.GetILGenerator();
        generator.Emit(OpCodes.Ldarg_0);
        generator.Emit(OpCodes.Ldarg_1);
        generator.Emit(OpCodes.Call, transformer);
        generator.Emit(OpCodes.Castclass, _typedInstructionEnumerableType);
        generator.Emit(OpCodes.Ret);
        Type transpilerType = type.CreateType();
        return transpilerType.GetMethod(
            "Transpiler",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static IEnumerable TransformInstructions(IEnumerable instructions, MethodBase original)
    {
        if (instructions == null)
            throw new ArgumentNullException(nameof(instructions));
        if (original == null || !Equals(original, _target) || _targetKind == PatchTargetKind.None)
            throw new InvalidOperationException("Talky's transpiler target state is unavailable.");
        if (_codeInstructionType == null || _typedInstructionEnumerableType == null)
        {
            throw new InvalidOperationException("Talky's Harmony instruction bridge is unavailable.");
        }

        List<CodeInstruction> source = new List<CodeInstruction>();
        foreach (object instruction in instructions)
        {
            if (!(instruction is CodeInstruction typedInstruction))
            {
                throw new InvalidOperationException(
                    "Harmony supplied an incompatible instruction stream.");
            }

            source.Add(typedInstruction);
        }

        foreach (CodeInstruction instruction in source)
        {
            if (instruction.blocks.Count != 0)
            {
                throw new InvalidOperationException(
                    "The look-sync helper contains an unexpected exception region.");
            }
        }

        int expectedAnchors = _targetKind == PatchTargetKind.Stock ? 1 : 2;
        int anchors = 0;
        for (int index = 0; index + 1 < source.Count; index++)
        {
            if (!IsMouseLookLoad(source[index]) || !IsFpcSyncDataConstruction(source[index + 1]))
                continue;

            if (source[index + 1].labels.Count != 0)
            {
                throw new InvalidOperationException(
                    "A look-sync constructor anchor is also a branch target.");
            }

            anchors++;
        }

        if (anchors != expectedAnchors)
        {
            throw new InvalidOperationException(
                $"Expected {expectedAnchors} look-sync constructor anchor(s), found {anchors}.");
        }

        List<CodeInstruction> rewritten = new List<CodeInstruction>(source.Count + anchors * 6);
        for (int index = 0; index < source.Count; index++)
        {
            CodeInstruction instruction = source[index];
            rewritten.Add(instruction);
            if (index + 1 >= source.Count ||
                !IsMouseLookLoad(instruction) ||
                !IsFpcSyncDataConstruction(source[index + 1]))
            {
                continue;
            }

            // The live FpcMouseLook is already on the stack. These arguments select either that
            // object or a Talky-owned presentation proxy before the original constructor runs.
            rewritten.Add(new CodeInstruction(OpCodes.Ldarg_0));
            rewritten.Add(new CodeInstruction(OpCodes.Ldarg_1));
            rewritten.Add(new CodeInstruction(OpCodes.Ldarg_2));
            rewritten.Add(new CodeInstruction(OpCodes.Ldarg_3));
            rewritten.Add(_targetKind == PatchTargetKind.CedMod
                ? new CodeInstruction(OpCodes.Ldarg_S, (byte)4)
                : new CodeInstruction(OpCodes.Ldc_I4_0));
            rewritten.Add(new CodeInstruction(OpCodes.Call, SelectorMethod));
        }

        return (IEnumerable)rewritten;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsMouseLookLoad(object instruction)
    {
        return instruction is CodeInstruction typedInstruction &&
               (typedInstruction.opcode == OpCodes.Call ||
                typedInstruction.opcode == OpCodes.Callvirt) &&
               Equals(typedInstruction.operand, MouseLookGetter);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsFpcSyncDataConstruction(object instruction)
    {
        return instruction is CodeInstruction typedInstruction &&
               (typedInstruction.opcode == OpCodes.Newobj ||
                typedInstruction.opcode == OpCodes.Call) &&
               Equals(typedInstruction.operand, FpcSyncDataConstructor);
    }

    internal static FpcMouseLook SelectSerializedMouseLook(
        FpcMouseLook liveMouseLook,
        ReferenceHub receiver,
        ReferenceHub target,
        FirstPersonMovementModule movement,
        bool isInvisible,
        bool nearPositioning)
    {
        if (liveMouseLook == null)
            return null;

        try
        {
            FakeLookHandler handler = Plugin.Instance?.FakeLookHandler;
            FpcMouseLook selected = handler?.SelectSerializedMouseLook(
                liveMouseLook,
                receiver,
                target,
                movement,
                isInvisible,
                nearPositioning);
            return selected ?? liveMouseLook;
        }
        catch (Exception exception)
        {
            LogCallbackFailure(exception);
            return liveMouseLook;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RemovePatchesInternal()
    {
        List<Exception> failures = new List<Exception>();
        try
        {
            Harmony harmony = _harmony as Harmony ?? new Harmony(OwnerId);
            if (_target != null && HasOwner(Harmony.GetPatchInfo(_target), OwnerId))
                harmony.Unpatch(_target, HarmonyPatchType.All, OwnerId);

            foreach (MethodBase method in Harmony.GetAllPatchedMethods().ToArray())
            {
                if (HasOwner(Harmony.GetPatchInfo(method), OwnerId))
                    harmony.Unpatch(method, HarmonyPatchType.All, OwnerId);
            }
        }
        catch (Exception exception)
        {
            failures.Add(new InvalidOperationException(
                "Harmony cleanup failed for " + DescribeAssembly(_harmonyAssembly),
                exception));
        }

        bool ownerRetained;
        try
        {
            ownerRetained = HasHarmonyOwnerPatches();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            ownerRetained = true;
        }

        if (ownerRetained || failures.Count != 0)
        {
            _patched = true;
            HarmonyBridge.MarkRuntimeOwnershipPossible();
            if (failures.Count == 0)
            {
                throw new InvalidOperationException(
                    "Harmony still reports Talky-owned look-sync patches after cleanup.");
            }

            throw new AggregateException(
                "Talky could not verify owner-scoped cleanup in the CLR-bound Harmony runtime.",
                failures);
        }

        ClearPatchState();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool HasHarmonyOwnerPatches()
    {
        foreach (MethodBase method in Harmony.GetAllPatchedMethods())
        {
            if (HasOwner(Harmony.GetPatchInfo(method), OwnerId))
                return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool VerifyPatch(MethodInfo target)
    {
        Patches patches = Harmony.GetPatchInfo(target);
        if (patches == null)
            return false;

        foreach (Patch patch in patches.Transpilers)
        {
            if (string.Equals(
                    patch.owner,
                    OwnerId,
                    StringComparison.Ordinal) &&
                Equals(patch.PatchMethod, _transpilerMethod))
            {
                return true;
            }
        }

        return false;
    }

    private static void ClearPatchState()
    {
        _harmony = null;
        _target = null;
        _targetKind = PatchTargetKind.None;
        _patched = false;
        Interlocked.Exchange(ref _callbackFailureLogged, 0);
        HarmonyBridge.ClearRuntimeOwnershipPossible();
    }

    private static string ExpectedSignature(PatchTargetKind kind)
    {
        string suffix = kind == PatchTargetKind.CedMod ? ", Boolean" : string.Empty;
        return "FpcSyncData GetNewSyncData(ReferenceHub, ReferenceHub, " +
               "FirstPersonMovementModule, Boolean" + suffix + ")";
    }

    private static string DescribePatch(
        PatchTargetKind kind,
        MethodInfo target,
        string profile,
        string sentinalOwner,
        bool verified)
    {
        Version harmonyVersion = _harmonyAssembly?.GetName().Version;
        string sentinal = kind == PatchTargetKind.CedMod
            ? $", sentinal_owner={sentinalOwner}"
            : string.Empty;
        return $"owner={OwnerId}, harmony={harmonyVersion}, " +
               $"harmony_runtime=({DescribeAssembly(_harmonyAssembly)}), " +
               $"mode={kind}{sentinal}, " +
               $"target={target.DeclaringType?.FullName}.{target.Name}, {profile}, " +
               $"verified={verified}";
    }

    // These queries inspect the contract used by the direct calls below. No discovered
    // constructor, field, property, or method is invoked through reflection.
    private static bool TryInspectHarmonyContract(Assembly assembly, out string detail)
    {
        Type harmony = assembly.GetType("HarmonyLib.Harmony", false, false);
        Type harmonyMethod = assembly.GetType("HarmonyLib.HarmonyMethod", false, false);
        Type codeInstruction = assembly.GetType("HarmonyLib.CodeInstruction", false, false);
        Type patches = assembly.GetType("HarmonyLib.Patches", false, false);
        Type patch = assembly.GetType("HarmonyLib.Patch", false, false);
        Type patchType = assembly.GetType("HarmonyLib.HarmonyPatchType", false, false);
        Type priority = assembly.GetType("HarmonyLib.Priority", false, false);
        if (harmony == null || harmonyMethod == null || codeInstruction == null ||
            patches == null || patch == null || patchType == null || priority == null)
        {
            detail = "One or more required HarmonyLib runtime types are missing";
            return false;
        }

        const BindingFlags PublicInstance = BindingFlags.Instance | BindingFlags.Public;
        const BindingFlags PublicStatic = BindingFlags.Static | BindingFlags.Public;
        MethodInfo patchMethod = FindMethod(
            harmony, "Patch", PublicInstance, typeof(MethodBase), harmonyMethod,
            harmonyMethod, harmonyMethod, harmonyMethod);
        MethodInfo getPatchInfo = FindMethod(
            harmony, "GetPatchInfo", PublicStatic, typeof(MethodBase));
        MethodInfo getAllPatchedMethods = FindMethod(
            harmony, "GetAllPatchedMethods", PublicStatic);
        if (harmony.GetConstructor(new[] { typeof(string) }) == null ||
            patchMethod == null ||
            FindMethod(harmony, "Unpatch", PublicInstance,
                typeof(MethodBase), patchType, typeof(string)) == null ||
            getPatchInfo?.ReturnType != patches ||
            getAllPatchedMethods == null ||
            !typeof(IEnumerable<MethodBase>).IsAssignableFrom(getAllPatchedMethods.ReturnType))
        {
            detail = "Harmony's public constructor, patch, cleanup, or inspection contract is incompatible";
            return false;
        }

        FieldInfo methodPriority = harmonyMethod.GetField("priority", PublicInstance);
        if (harmonyMethod.GetConstructor(new[] { typeof(MethodInfo) }) == null ||
            methodPriority?.FieldType != typeof(int) || methodPriority.IsInitOnly ||
            codeInstruction.GetConstructor(new[] { typeof(OpCode), typeof(object) }) == null ||
            codeInstruction.GetField("opcode", PublicInstance)?.FieldType != typeof(OpCode) ||
            codeInstruction.GetField("operand", PublicInstance)?.FieldType != typeof(object) ||
            codeInstruction.GetField("labels", PublicInstance)?.FieldType != typeof(List<Label>) ||
            codeInstruction.GetField("blocks", PublicInstance) == null)
        {
            detail = "Harmony's direct instruction or transpiler member contract is incompatible";
            return false;
        }

        FieldInfo prefixes = patches.GetField("Prefixes", PublicInstance);
        FieldInfo transpilers = patches.GetField("Transpilers", PublicInstance);
        PropertyInfo owners = patches.GetProperty("Owners", PublicInstance);
        PropertyInfo patchMethodMember = patch.GetProperty("PatchMethod", PublicInstance);
        FieldInfo priorityLast = priority.GetField("Last", PublicStatic);
        if (prefixes == null || transpilers == null ||
            !typeof(IEnumerable).IsAssignableFrom(prefixes.FieldType) ||
            !typeof(IEnumerable).IsAssignableFrom(transpilers.FieldType) ||
            owners?.GetGetMethod() == null ||
            !typeof(IEnumerable<string>).IsAssignableFrom(owners.PropertyType) ||
            patch.GetField("owner", PublicInstance)?.FieldType != typeof(string) ||
            patchMethodMember?.PropertyType != typeof(MethodInfo) ||
            patchMethodMember.GetGetMethod() == null ||
            !patchType.IsEnum || !Enum.IsDefined(patchType, "All") ||
            priorityLast?.FieldType != typeof(int) || !priorityLast.IsLiteral)
        {
            detail = "Harmony's direct patch metadata contract is incompatible";
            return false;
        }

        detail = null;
        return true;
    }

    private static MethodInfo FindMethod(
        Type type,
        string name,
        BindingFlags flags,
        params Type[] parameterTypes)
    {
        return type.GetMethod(name, flags, null, parameterTypes, null);
    }

    private static bool IsHarmonyAssemblyName(Assembly assembly)
    {
        try
        {
            return string.Equals(
                assembly?.GetName().Name,
                "0Harmony",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasSameBinaryIdentity(Assembly first, Assembly second)
    {
        try
        {
            return string.Equals(
                       first.GetName().Name,
                       second.GetName().Name,
                       StringComparison.OrdinalIgnoreCase) &&
                   first.ManifestModule.ModuleVersionId == second.ManifestModule.ModuleVersionId;
        }
        catch
        {
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TrySelectRuntimeForTarget(
        PatchTargetKind kind,
        MethodBase target,
        out string detail)
    {
        try
        {
            // Force metadata access through the same direct API that will apply and remove
            // this patch. An incompatible bound runtime must fail before ownership changes.
            Patches patches = Harmony.GetPatchInfo(target);
            if (patches != null && patches.Owners == null)
                throw new InvalidOperationException("Harmony returned an invalid owner collection.");
            detail = null;
            return true;
        }
        catch (Exception exception)
        {
            detail = "Harmony patch metadata could not be inspected safely: " +
                     exception.GetType().Name + ": " + exception.Message;
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryGetInstalledPrefixOwner(
        MethodBase target,
        MethodInfo expectedPrefix,
        out string owner)
    {
        owner = null;
        Patches patches = Harmony.GetPatchInfo(target);
        if (patches == null)
            return false;

        foreach (Patch patch in patches.Prefixes)
        {
            if (!Equals(patch.PatchMethod, expectedPrefix))
                continue;

            owner = patch.owner;
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool HasOwner(object patches, string owner)
    {
        if (!(patches is Patches typedPatches))
            return false;

        foreach (string candidate in typedPatches.Owners)
        {
            if (string.Equals(candidate, owner, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string DescribeAssembly(Assembly assembly)
    {
        if (assembly == null)
            return "assembly=<null>";

        AssemblyName name = assembly.GetName();
        string location;
        try
        {
            location = assembly.IsDynamic ? "<dynamic>" : assembly.Location;
            if (string.IsNullOrWhiteSpace(location))
                location = "<unavailable>";
        }
        catch
        {
            location = "<unavailable>";
        }

        int sameNameLoaded = AppDomain.CurrentDomain.GetAssemblies().Count(candidate =>
        {
            try
            {
                return string.Equals(
                    candidate.GetName().Name,
                    name.Name,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        });

        return $"name={name.Name},version={name.Version},location={location}," +
               $"same_name_loaded={sameNameLoaded}";
    }

    private static string TryGetModuleVersionId(Module module)
    {
        try
        {
            return module?.ModuleVersionId.ToString("D") ?? "<unavailable>";
        }
        catch
        {
            return "<unavailable>";
        }
    }

    private static string ComputeSha256(byte[] value)
    {
        if (value == null)
            return "<unavailable>";

        try
        {
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(value);
            return BitConverter.ToString(hash).Replace("-", string.Empty);
        }
        catch
        {
            // This digest is diagnostic only and must never become a compatibility gate.
            return "<unavailable>";
        }
    }

    private static void LogCallbackFailure(Exception exception)
    {
        if (Interlocked.Exchange(ref _callbackFailureLogged, 1) != 0)
            return;

        try
        {
            Logger.Error(
                "Talky's look-sync selector failed; the live look will be serialized unchanged: " +
                exception);
        }
        catch
        {
            // Diagnostic logging must not disrupt position synchronization.
        }
    }
}
