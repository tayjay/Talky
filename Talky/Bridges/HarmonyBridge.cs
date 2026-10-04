using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
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
        if (!TryPrepareHarmonyRuntime(out string compatibilityDetail))
        {
            if (RuntimeOwnershipPossible())
            {
                throw new InvalidOperationException(
                    "Talky retains possible patch ownership but the bound Harmony runtime " +
                    "cannot be used for safe cleanup: " + compatibilityDetail);
            }

            return;
        }

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
        try
        {
            return HarmonyRuntimeBridge.TrySelectHarmonyRuntime(
                resolvedHarmonyType?.Assembly,
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
            return Type.GetType("HarmonyLib.Harmony, 0Harmony", false, false);
        }
        catch
        {
            // TryPrepareHarmonyRuntime provides the actionable compatibility diagnostic.
            return null;
        }
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

    private sealed class HarmonyApi
    {
        internal Assembly Assembly;
        internal Type CodeInstructionType;
        internal Type TypedInstructionEnumerableType;
        internal ConstructorInfo HarmonyConstructor;
        internal ConstructorInfo HarmonyMethodConstructor;
        internal ConstructorInfo CodeInstructionConstructor;
        internal MethodInfo PatchMethod;
        internal MethodInfo UnpatchMethod;
        internal MethodInfo GetPatchInfoMethod;
        internal MethodInfo GetAllPatchedMethodsMethod;
        internal ValueAccessor HarmonyMethodPriority;
        internal ValueAccessor CodeInstructionOpcode;
        internal ValueAccessor CodeInstructionOperand;
        internal ValueAccessor CodeInstructionLabels;
        internal ValueAccessor CodeInstructionBlocks;
        internal ValueAccessor Prefixes;
        internal ValueAccessor Transpilers;
        internal ValueAccessor Owners;
        internal ValueAccessor PatchOwner;
        internal ValueAccessor PatchMethodMember;
        internal object PatchTypeAll;
        internal int PriorityLast;
    }

    private sealed class ValueAccessor
    {
        internal FieldInfo Field;
        internal PropertyInfo Property;
        internal Type ValueType => Field?.FieldType ?? Property?.PropertyType;

        internal object GetValue(object instance) =>
            Field != null ? Field.GetValue(instance) : Property.GetValue(instance, null);

        internal void SetValue(object instance, object value)
        {
            if (Field != null)
                Field.SetValue(instance, value);
            else
                Property.SetValue(instance, value, null);
        }
    }

    private static readonly object HarmonySelectionLock = new object();
    private static readonly List<HarmonyApi> CompatibleHarmonyApis = new List<HarmonyApi>();
    private static HarmonyApi _api;
    private static HarmonyApi _resolverApi;
    private static object _harmony;
    private static MethodInfo _target;
    private static MethodInfo _transpilerMethod;
    private static Type _codeInstructionType;
    private static Type _typedInstructionEnumerableType;
    private static ConstructorInfo _codeInstructionConstructor;
    private static ValueAccessor _codeInstructionOpcode;
    private static ValueAccessor _codeInstructionOperand;
    private static ValueAccessor _codeInstructionLabels;
    private static ValueAccessor _codeInstructionBlocks;
    private static PatchTargetKind _targetKind;
    private static bool _patched;
    private static int _callbackFailureLogged;
    private static int _dynamicTranspilerGeneration;

    /// <summary>
    /// Selects the Harmony implementation chosen by the host resolver when it exposes every API
    /// capability Talky uses. Incompatible same-name assemblies are ignored. If the resolver did
    /// not choose one, a single capability-compatible loaded implementation is accepted. Versions
    /// are deliberately not gated.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool TrySelectHarmonyRuntime(
        Assembly resolverSelectedAssembly,
        out string detail)
    {
        lock (HarmonySelectionLock)
        {
            List<string> observed = new List<string>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!IsHarmonyAssemblyName(assembly))
                    continue;

                HarmonyApi candidate = CompatibleHarmonyApis.FirstOrDefault(api =>
                    ReferenceEquals(api.Assembly, assembly));
                string reason = "compatible";
                bool created = false;
                if (candidate == null)
                {
                    try
                    {
                        created = TryCreateHarmonyApi(
                            assembly,
                            out candidate,
                            out reason);
                    }
                    catch (Exception exception)
                    {
                        reason = "inspection failed: " + exception.GetType().Name;
                    }
                }
                if (created)
                    CompatibleHarmonyApis.Add(candidate);

                if (candidate != null)
                {
                    if (ReferenceEquals(assembly, resolverSelectedAssembly))
                        _resolverApi = candidate;
                    observed.Add(DescribeAssembly(assembly) + ",capabilities=compatible");
                }
                else
                {
                    observed.Add(DescribeAssembly(assembly) + ",capabilities=" + reason);
                }
            }

            List<HarmonyApi> logicalApis = GetLogicalHarmonyApis();
            if (logicalApis.Count == 0)
            {
                detail = observed.Count == 0
                    ? "0Harmony could not be resolved or is not loaded"
                    : "no loaded 0Harmony assembly exposes Talky's required API capabilities; " +
                      "observed=[" + string.Join("; ", observed) + "]";
                return false;
            }

            bool selectionIsOwned = _patched || _target != null || _harmony != null;
            HarmonyApi selected = selectionIsOwned
                ? _api
                : logicalApis.FirstOrDefault(api => ReferenceEquals(api, _resolverApi)) ??
                  logicalApis.FirstOrDefault(api => ReferenceEquals(api, _api)) ?? logicalApis
                    .OrderBy(api => api.Assembly.IsDynamic ? 1 : 0)
                    .ThenBy(
                        api => SafeAssemblyLocation(api.Assembly),
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(api => api.Assembly.FullName, StringComparer.Ordinal)
                    .First();
            SelectHarmonyApi(selected);
            detail = "selected Harmony runtime is compatible (" +
                     DescribeAssembly(_api.Assembly) + "); compatible_loaded=" +
                     CompatibleHarmonyApis.Count + ",logical_runtimes=" + logicalApis.Count;
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
            _harmony = _api.HarmonyConstructor.Invoke(new object[] { OwnerId });
            _target = target;
            _targetKind = kind;
            _transpilerMethod = CreateHarmonyTranspiler();

            object transpiler = _api.HarmonyMethodConstructor.Invoke(
                new object[] { _transpilerMethod });
            _api.HarmonyMethodPriority.SetValue(transpiler, _api.PriorityLast);
            _api.PatchMethod.Invoke(
                _harmony,
                new[] { (object)target, null, null, transpiler, null });

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
                _harmony ??= _api.HarmonyConstructor.Invoke(new object[] { OwnerId });
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

        List<Tuple<HarmonyApi, string>> matches = new List<Tuple<HarmonyApi, string>>();
        foreach (HarmonyApi api in GetLogicalHarmonyApis())
        {
            try
            {
                if (TryGetInstalledPrefixOwner(
                        api,
                        writeAll,
                        sentinalPrefix,
                        out string owner))
                {
                    matches.Add(Tuple.Create(api, owner));
                }
            }
            catch
            {
                // A different compatible copy may own CedMod's installed patch metadata.
            }
        }

        if (matches.Count == 0)
        {
            detail = "CedMod's exact Sentinal WriteAll prefix is not installed in any " +
                     "capability-compatible Harmony runtime";
            return false;
        }

        Tuple<HarmonyApi, string> selected = matches.FirstOrDefault(match =>
            ReferenceEquals(match.Item1, _resolverApi)) ??
            matches.FirstOrDefault(match => ReferenceEquals(match.Item1, _api)) ??
            matches.OrderBy(match => match.Item1.Assembly.IsDynamic ? 1 : 0)
                .ThenBy(
                    match => SafeAssemblyLocation(match.Item1.Assembly),
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(match => match.Item1.Assembly.FullName, StringComparer.Ordinal)
                .First();
        SelectHarmonyApi(selected.Item1);
        sentinalOwner = selected.Item2;
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
            _typedInstructionEnumerableType != null &&
            _codeInstructionConstructor != null && _codeInstructionOpcode != null &&
            _codeInstructionOperand != null && _codeInstructionLabels != null &&
            _codeInstructionBlocks != null)
        {
            return _transpilerMethod;
        }

        _codeInstructionType = _api.CodeInstructionType;
        _typedInstructionEnumerableType = _api.TypedInstructionEnumerableType;
        _codeInstructionConstructor = _api.CodeInstructionConstructor;
        _codeInstructionOpcode = _api.CodeInstructionOpcode;
        _codeInstructionOperand = _api.CodeInstructionOperand;
        _codeInstructionLabels = _api.CodeInstructionLabels;
        _codeInstructionBlocks = _api.CodeInstructionBlocks;
        MethodInfo transformer = typeof(HarmonyInstructionTransformer).GetMethod(
            nameof(HarmonyInstructionTransformer.Transform),
            BindingFlags.Static | BindingFlags.Public);

        if (_codeInstructionConstructor == null || _codeInstructionOpcode == null ||
            _codeInstructionOperand == null || _codeInstructionLabels == null ||
            _codeInstructionBlocks == null || transformer == null)
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

    internal static IEnumerable TransformInstructions(IEnumerable instructions, MethodBase original)
    {
        if (instructions == null)
            throw new ArgumentNullException(nameof(instructions));
        if (original == null || !Equals(original, _target) || _targetKind == PatchTargetKind.None)
            throw new InvalidOperationException("Talky's transpiler target state is unavailable.");
        if (_codeInstructionType == null || _typedInstructionEnumerableType == null ||
            _codeInstructionConstructor == null || _codeInstructionOpcode == null ||
            _codeInstructionOperand == null || _codeInstructionLabels == null ||
            _codeInstructionBlocks == null)
        {
            throw new InvalidOperationException("Talky's Harmony instruction bridge is unavailable.");
        }

        List<object> source = new List<object>();
        foreach (object instruction in instructions)
        {
            if (instruction == null || !_codeInstructionType.IsInstanceOfType(instruction))
            {
                throw new InvalidOperationException(
                    "Harmony supplied an incompatible instruction stream.");
            }

            source.Add(instruction);
        }

        foreach (object instruction in source)
        {
            if (HasItems(_codeInstructionBlocks.GetValue(instruction)))
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

            if (HasItems(_codeInstructionLabels.GetValue(source[index + 1])))
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

        IList rewritten = (IList)Activator.CreateInstance(
            typeof(List<>).MakeGenericType(_codeInstructionType),
            source.Count + anchors * 6);
        for (int index = 0; index < source.Count; index++)
        {
            object instruction = source[index];
            rewritten.Add(instruction);
            if (index + 1 >= source.Count ||
                !IsMouseLookLoad(instruction) ||
                !IsFpcSyncDataConstruction(source[index + 1]))
            {
                continue;
            }

            // The live FpcMouseLook is already on the stack. These arguments select either that
            // object or a Talky-owned presentation proxy before the original constructor runs.
            rewritten.Add(NewInstruction(OpCodes.Ldarg_0));
            rewritten.Add(NewInstruction(OpCodes.Ldarg_1));
            rewritten.Add(NewInstruction(OpCodes.Ldarg_2));
            rewritten.Add(NewInstruction(OpCodes.Ldarg_3));
            rewritten.Add(_targetKind == PatchTargetKind.CedMod
                ? NewInstruction(OpCodes.Ldarg_S, (byte)4)
                : NewInstruction(OpCodes.Ldc_I4_0));
            rewritten.Add(NewInstruction(OpCodes.Call, SelectorMethod));
        }

        return (IEnumerable)rewritten;
    }

    private static object NewInstruction(OpCode opcode, object operand = null)
    {
        return _codeInstructionConstructor.Invoke(new[] { (object)opcode, operand });
    }

    private static bool HasItems(object value)
    {
        return value is ICollection collection && collection.Count != 0;
    }

    private static bool IsMouseLookLoad(object instruction)
    {
        return instruction != null &&
               (ReadOpcode(instruction) == OpCodes.Call ||
                ReadOpcode(instruction) == OpCodes.Callvirt) &&
               Equals(_codeInstructionOperand.GetValue(instruction), MouseLookGetter);
    }

    private static bool IsFpcSyncDataConstruction(object instruction)
    {
        return instruction != null &&
               (ReadOpcode(instruction) == OpCodes.Newobj ||
                ReadOpcode(instruction) == OpCodes.Call) &&
               Equals(_codeInstructionOperand.GetValue(instruction), FpcSyncDataConstructor);
    }

    private static OpCode ReadOpcode(object instruction)
    {
        return (OpCode)_codeInstructionOpcode.GetValue(instruction);
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

    private static void RemovePatchesInternal()
    {
        List<Exception> failures = new List<Exception>();
        foreach (HarmonyApi api in GetLogicalHarmonyApis())
        {
            try
            {
                object harmony = ReferenceEquals(api, _api) && _harmony != null
                    ? _harmony
                    : api.HarmonyConstructor.Invoke(new object[] { OwnerId });
                if (_target != null)
                {
                    object targetPatches = GetPatchInfo(api, _target);
                    if (HasOwner(api, targetPatches, OwnerId))
                        Unpatch(api, harmony, _target);
                }

                foreach (MethodBase method in GetAllPatchedMethods(api).ToArray())
                {
                    object patches = GetPatchInfo(api, method);
                    if (HasOwner(api, patches, OwnerId))
                        Unpatch(api, harmony, method);
                }
            }
            catch (Exception exception)
            {
                failures.Add(new InvalidOperationException(
                    "Harmony cleanup failed for " + DescribeAssembly(api.Assembly),
                    exception));
            }
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
                "Talky could not verify owner-scoped cleanup across all compatible Harmony " +
                "runtimes.",
                failures);
        }

        ClearPatchState();
    }

    private static bool HasHarmonyOwnerPatches()
    {
        foreach (HarmonyApi api in GetLogicalHarmonyApis())
        {
            foreach (MethodBase method in GetAllPatchedMethods(api))
            {
                object patches = GetPatchInfo(api, method);
                if (HasOwner(api, patches, OwnerId))
                    return true;
            }
        }

        return false;
    }

    private static bool VerifyPatch(MethodInfo target)
    {
        object patches = GetPatchInfo(_api, target);
        IEnumerable transpilers = patches == null
            ? null
            : _api.Transpilers.GetValue(patches) as IEnumerable;
        if (transpilers == null)
            return false;

        foreach (object patch in transpilers)
        {
            if (string.Equals(
                    _api.PatchOwner.GetValue(patch) as string,
                    OwnerId,
                    StringComparison.Ordinal) &&
                Equals(_api.PatchMethodMember.GetValue(patch), _transpilerMethod))
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
        Version harmonyVersion = _api.Assembly.GetName().Version;
        string sentinal = kind == PatchTargetKind.CedMod
            ? $", sentinal_owner={sentinalOwner}"
            : string.Empty;
        return $"owner={OwnerId}, harmony={harmonyVersion}, " +
               $"harmony_runtime=({DescribeAssembly(_api.Assembly)}), " +
               $"mode={kind}{sentinal}, " +
               $"target={target.DeclaringType?.FullName}.{target.Name}, {profile}, " +
               $"verified={verified}";
    }

    private static bool TryCreateHarmonyApi(
        Assembly assembly,
        out HarmonyApi api,
        out string missingCapability)
    {
        api = null;
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
            missingCapability = "one or more required HarmonyLib runtime types are missing";
            return false;
        }

        ConstructorInfo harmonyConstructor = harmony.GetConstructor(new[] { typeof(string) });
        if (harmonyConstructor == null)
        {
            missingCapability = "Harmony(string) is missing";
            return false;
        }

        MethodInfo patchMethod = FindMethod(
            harmony,
            "Patch",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            typeof(MethodBase),
            harmonyMethod,
            harmonyMethod,
            harmonyMethod,
            harmonyMethod);
        if (patchMethod == null)
        {
            missingCapability = "Harmony.Patch(MethodBase, HarmonyMethod x4) is missing";
            return false;
        }

        MethodInfo unpatchMethod = FindMethod(
            harmony,
            "Unpatch",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            typeof(MethodBase),
            patchType,
            typeof(string));
        if (unpatchMethod == null)
        {
            missingCapability = "Harmony.Unpatch(MethodBase, HarmonyPatchType, string) is missing";
            return false;
        }

        MethodInfo getPatchInfo = FindMethod(
            harmony,
            "GetPatchInfo",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            typeof(MethodBase));
        if (getPatchInfo == null || !patches.IsAssignableFrom(getPatchInfo.ReturnType))
        {
            missingCapability = "Harmony.GetPatchInfo(MethodBase) is missing";
            return false;
        }

        MethodInfo getAllPatchedMethods = FindMethod(
            harmony,
            "GetAllPatchedMethods",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (getAllPatchedMethods == null ||
            !typeof(IEnumerable).IsAssignableFrom(getAllPatchedMethods.ReturnType))
        {
            missingCapability = "Harmony.GetAllPatchedMethods() is missing";
            return false;
        }

        ConstructorInfo harmonyMethodConstructor =
            harmonyMethod.GetConstructor(new[] { typeof(MethodInfo) });
        ValueAccessor harmonyMethodPriority = GetAccessor(harmonyMethod, "priority", true);
        if (harmonyMethodConstructor == null ||
            harmonyMethodPriority?.ValueType != typeof(int))
        {
            missingCapability = "HarmonyMethod(MethodInfo) or its priority field is missing";
            return false;
        }

        const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        ConstructorInfo codeInstructionConstructor = codeInstruction.GetConstructor(
            InstanceMembers,
            null,
            new[] { typeof(OpCode), typeof(object) },
            null);
        ValueAccessor opcode = GetAccessor(codeInstruction, "opcode", false);
        ValueAccessor operand = GetAccessor(codeInstruction, "operand", false);
        ValueAccessor labels = GetAccessor(codeInstruction, "labels", false);
        ValueAccessor blocks = GetAccessor(codeInstruction, "blocks", false);
        if (codeInstructionConstructor == null || opcode?.ValueType != typeof(OpCode) ||
            operand?.ValueType != typeof(object) || labels == null || blocks == null)
        {
            missingCapability = "CodeInstruction's constructor or instruction metadata fields are missing";
            return false;
        }

        ValueAccessor prefixes = GetAccessor(patches, "Prefixes", false);
        ValueAccessor transpilers = GetAccessor(patches, "Transpilers", false);
        ValueAccessor owners = GetAccessor(patches, "Owners", false);
        if (prefixes == null || transpilers == null || owners == null ||
            !typeof(IEnumerable).IsAssignableFrom(prefixes.ValueType) ||
            !typeof(IEnumerable).IsAssignableFrom(transpilers.ValueType) ||
            !typeof(IEnumerable).IsAssignableFrom(owners.ValueType))
        {
            missingCapability = "Harmony patch metadata collections are missing";
            return false;
        }

        ValueAccessor patchOwner = GetAccessor(patch, "owner", false);
        ValueAccessor patchMethodMember = GetAccessor(patch, "PatchMethod", false);
        if (patchOwner?.ValueType != typeof(string) ||
            patchMethodMember?.ValueType != typeof(MethodInfo))
        {
            missingCapability = "Harmony patch owner or PatchMethod metadata is missing";
            return false;
        }

        FieldInfo priorityLast = priority.GetField(
            "Last",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (!patchType.IsEnum || !Enum.IsDefined(patchType, "All") ||
            priorityLast?.FieldType != typeof(int))
        {
            missingCapability = "HarmonyPatchType.All or Priority.Last is missing";
            return false;
        }

        api = new HarmonyApi
        {
            Assembly = assembly,
            CodeInstructionType = codeInstruction,
            TypedInstructionEnumerableType =
                typeof(IEnumerable<>).MakeGenericType(codeInstruction),
            HarmonyConstructor = harmonyConstructor,
            HarmonyMethodConstructor = harmonyMethodConstructor,
            CodeInstructionConstructor = codeInstructionConstructor,
            PatchMethod = patchMethod,
            UnpatchMethod = unpatchMethod,
            GetPatchInfoMethod = getPatchInfo,
            GetAllPatchedMethodsMethod = getAllPatchedMethods,
            HarmonyMethodPriority = harmonyMethodPriority,
            CodeInstructionOpcode = opcode,
            CodeInstructionOperand = operand,
            CodeInstructionLabels = labels,
            CodeInstructionBlocks = blocks,
            Prefixes = prefixes,
            Transpilers = transpilers,
            Owners = owners,
            PatchOwner = patchOwner,
            PatchMethodMember = patchMethodMember,
            PatchTypeAll = Enum.Parse(patchType, "All"),
            PriorityLast = (int)priorityLast.GetValue(null),
        };
        missingCapability = null;
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

    private static ValueAccessor GetAccessor(Type type, string name, bool writable)
    {
        const BindingFlags Members =
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic;
        FieldInfo field = type.GetField(name, Members);
        if (field != null && (!writable || !field.IsInitOnly))
            return new ValueAccessor { Field = field };

        PropertyInfo property = type.GetProperty(name, Members);
        if (property?.GetGetMethod(true) != null &&
            (!writable || property.GetSetMethod(true) != null))
        {
            return new ValueAccessor { Property = property };
        }

        field = type.GetFields(Members).FirstOrDefault(candidate => string.Equals(
            candidate.Name,
            name,
            StringComparison.OrdinalIgnoreCase));
        if (field != null && (!writable || !field.IsInitOnly))
            return new ValueAccessor { Field = field };

        property = type.GetProperties(Members).FirstOrDefault(candidate => string.Equals(
            candidate.Name,
            name,
            StringComparison.OrdinalIgnoreCase));
        if (property?.GetGetMethod(true) != null &&
            (!writable || property.GetSetMethod(true) != null))
        {
            return new ValueAccessor { Property = property };
        }

        return null;
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

    private static void SelectHarmonyApi(HarmonyApi selected)
    {
        if (selected == null)
            throw new ArgumentNullException(nameof(selected));
        if (ReferenceEquals(selected, _api))
            return;
        if (_patched || _target != null || _harmony != null)
        {
            throw new InvalidOperationException(
                "Talky cannot change Harmony runtimes while patch ownership is active.");
        }

        _api = selected;
        _transpilerMethod = null;
        _codeInstructionType = null;
        _typedInstructionEnumerableType = null;
        _codeInstructionConstructor = null;
        _codeInstructionOpcode = null;
        _codeInstructionOperand = null;
        _codeInstructionLabels = null;
        _codeInstructionBlocks = null;
    }

    private static List<HarmonyApi> GetLogicalHarmonyApis()
    {
        return CompatibleHarmonyApis
            .GroupBy(GetHarmonyBinaryIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                HarmonyApi current = group.FirstOrDefault(api => ReferenceEquals(api, _api));
                if (current != null)
                    return current;

                HarmonyApi resolver = group.FirstOrDefault(api =>
                    ReferenceEquals(api, _resolverApi));
                if (resolver != null)
                    return resolver;

                return group
                    .OrderBy(api => api.Assembly.IsDynamic ? 1 : 0)
                    .ThenBy(
                        api => SafeAssemblyLocation(api.Assembly),
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(api => api.Assembly.FullName, StringComparer.Ordinal)
                    .First();
            })
            .ToList();
    }

    private static string GetHarmonyBinaryIdentity(HarmonyApi api)
    {
        try
        {
            AssemblyName name = api.Assembly.GetName();
            Guid mvid = api.Assembly.ManifestModule.ModuleVersionId;
            return name.Name + "|" + mvid.ToString("D");
        }
        catch
        {
            // If an implementation does not expose stable module identity, do not collapse it
            // with another runtime whose independent patch state cannot be ruled out.
            return (api.Assembly?.FullName ?? "<unknown>") + "|instance=" +
                   RuntimeHelpers.GetHashCode(api.Assembly).ToString();
        }
    }

    private static bool TrySelectRuntimeForTarget(
        PatchTargetKind kind,
        MethodBase target,
        out string detail)
    {
        List<HarmonyApi> active = new List<HarmonyApi>();
        try
        {
            foreach (HarmonyApi api in GetLogicalHarmonyApis())
            {
                object patches = GetPatchInfo(api, target);
                if (HasAnyOwner(api, patches))
                    active.Add(api);
            }
        }
        catch (Exception exception)
        {
            detail = "Harmony patch metadata could not be inspected safely: " +
                     exception.GetType().Name + ": " + exception.Message;
            return false;
        }

        if (active.Count > 1)
        {
            detail = "Harmony patch metadata for Talky's exact target is split across " +
                     "multiple compatible runtimes: " +
                     string.Join(
                         "; ",
                         active.Select(api => DescribeAssembly(api.Assembly)));
            return false;
        }

        if (active.Count == 1)
        {
            if (kind == PatchTargetKind.Stock)
                SelectHarmonyApi(active[0]);
            else if (!ReferenceEquals(active[0], _api))
            {
                detail = "CedMod's selected Harmony runtime differs from the runtime holding " +
                         "the process's active patch metadata";
                return false;
            }
        }

        detail = null;
        return true;
    }

    private static bool TryGetInstalledPrefixOwner(
        HarmonyApi api,
        MethodBase target,
        MethodInfo expectedPrefix,
        out string owner)
    {
        owner = null;
        object patches = GetPatchInfo(api, target);
        IEnumerable prefixes = patches == null
            ? null
            : api.Prefixes.GetValue(patches) as IEnumerable;
        if (prefixes == null)
            return false;

        foreach (object patch in prefixes)
        {
            if (!Equals(api.PatchMethodMember.GetValue(patch), expectedPrefix))
                continue;

            owner = api.PatchOwner.GetValue(patch) as string;
            return true;
        }

        return false;
    }

    private static object GetPatchInfo(HarmonyApi api, MethodBase target)
    {
        return api.GetPatchInfoMethod.Invoke(null, new object[] { target });
    }

    private static IEnumerable<MethodBase> GetAllPatchedMethods(HarmonyApi api)
    {
        object value = api.GetAllPatchedMethodsMethod.Invoke(null, null);
        if (!(value is IEnumerable methods))
            throw new InvalidOperationException("Harmony returned an invalid patched-method set.");

        foreach (object method in methods)
        {
            if (!(method is MethodBase methodBase))
            {
                throw new InvalidOperationException(
                    "Harmony returned a non-method patch metadata entry.");
            }

            yield return methodBase;
        }
    }

    private static bool HasOwner(HarmonyApi api, object patches, string owner)
    {
        if (patches == null)
            return false;

        if (!(api.Owners.GetValue(patches) is IEnumerable owners))
            return false;
        foreach (object candidate in owners)
        {
            if (string.Equals(candidate as string, owner, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool HasAnyOwner(HarmonyApi api, object patches)
    {
        if (patches == null || !(api.Owners.GetValue(patches) is IEnumerable owners))
            return false;

        foreach (object candidate in owners)
        {
            if (candidate is string)
                return true;
        }

        return false;
    }

    private static void Unpatch(HarmonyApi api, object harmony, MethodBase target)
    {
        api.UnpatchMethod.Invoke(
            harmony,
            new[] { (object)target, api.PatchTypeAll, OwnerId });
    }

    private static string SafeAssemblyLocation(Assembly assembly)
    {
        try
        {
            return assembly == null || assembly.IsDynamic ||
                   string.IsNullOrWhiteSpace(assembly.Location)
                ? "~"
                : assembly.Location;
        }
        catch
        {
            return "~";
        }
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
