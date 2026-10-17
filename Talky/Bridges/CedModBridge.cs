using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Talky.Compatibility;

namespace Talky.Bridges;

internal sealed class CedModRuntimeProbe
{
    public CedModRuntimeProbe(
        CedModPluginState state,
        string source,
        object plugin,
        Assembly assembly,
        string detail)
    {
        State = state;
        Source = source;
        Plugin = plugin;
        Assembly = assembly;
        Detail = detail;
    }

    public CedModPluginState State { get; }
    public string Source { get; }
    public object Plugin { get; }
    public Assembly Assembly { get; }
    public string Detail { get; }

    public static CedModRuntimeProbe NotPresent(string source) =>
        new CedModRuntimeProbe(
            CedModPluginState.NotPresent,
            source,
            null,
            null,
            "no enabled CedMod.CedModMain plugin");
}

internal static class CedModBridge
{
    internal const string PluginTypeName = "CedMod.CedModMain";
    private static readonly Dictionary<Type, Func<object, bool>> ConfigurationReaders =
        new Dictionary<Type, Func<object, bool>>();

    public static CedModRuntimeProbe ProbeCandidates(
        IEnumerable<object> candidates,
        string source)
    {
        try
        {
            object match = null;
            int matchCount = 0;
            if (candidates != null)
            {
                foreach (object candidate in candidates)
                {
                    if (candidate?.GetType().FullName != PluginTypeName)
                        continue;

                    match ??= candidate;
                    matchCount++;
                }
            }

            if (matchCount == 0)
                return CedModRuntimeProbe.NotPresent(source);

            if (matchCount != 1)
            {
                return new CedModRuntimeProbe(
                    CedModPluginState.Unreadable,
                    source,
                    null,
                    null,
                    $"found {matchCount} enabled CedMod.CedModMain plugins");
            }

            Type pluginType = match.GetType();
            Assembly assembly = pluginType.Assembly;
            Func<object, bool> reader = GetConfigurationReader(pluginType);
            CedModPluginState state = reader == null
                ? CedModPluginState.Unreadable
                : CedModCompatibilityPolicy.Inspect(() => reader(match));
            Version version = assembly.GetName().Version;
            return new CedModRuntimeProbe(
                state,
                source,
                match,
                assembly,
                $"assembly={assembly.GetName().Name}, version={version}, config={state}");
        }
        catch (Exception exception)
        {
            return new CedModRuntimeProbe(
                CedModPluginState.Unreadable,
                source,
                null,
                null,
                "discovery failed: " + exception.GetType().Name + ": " + exception.Message);
        }
    }

    // CedMod is optional and has no shared typed configuration interface. Bind its public
    // contract once using the metadata needed for Harmony integration; runtime reads are
    // ordinary getter calls in the emitted delegate, never PropertyInfo.GetValue/Invoke.
    internal static Func<object, bool> GetConfigurationReader(Type pluginType)
    {
        lock (ConfigurationReaders)
        {
            if (ConfigurationReaders.TryGetValue(pluginType, out Func<object, bool> cached))
                return cached;

            Func<object, bool> reader = null;
            try
            {
                Type current = pluginType;
                string[] path = { "Config", "CedMod", "DisableFakeSyncing" };
                MethodInfo[] getters = new MethodInfo[path.Length];
                for (int index = 0; index < path.Length; index++)
                {
                    MethodInfo getter = current.GetProperty(
                            path[index], BindingFlags.Instance | BindingFlags.Public)
                        ?.GetGetMethod();
                    if (getter == null || getter.IsStatic || getter.GetParameters().Length != 0 ||
                        getter.ReturnType.IsByRef || getter.ReturnType.IsPointer ||
                        (index < path.Length - 1 && getter.ReturnType.IsValueType) ||
                        (index == path.Length - 1 && getter.ReturnType != typeof(bool)))
                    {
                        return ConfigurationReaders[pluginType] = null;
                    }

                    getters[index] = getter;
                    current = getter.ReturnType;
                }

                DynamicMethod method = new DynamicMethod(
                    "ReadCedModDisableFakeSyncing", typeof(bool), new[] { typeof(object) },
                    typeof(CedModBridge).Module, false);
                ILGenerator il = method.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, pluginType);
                foreach (MethodInfo getter in getters)
                    il.Emit(OpCodes.Callvirt, getter);
                il.Emit(OpCodes.Ret);
                reader = (Func<object, bool>)method.CreateDelegate(typeof(Func<object, bool>));
            }
            catch
            {
                // Unknown CedMod contracts disable rewriting rather than guessing offsets.
            }

            ConfigurationReaders[pluginType] = reader;
            return reader;
        }
    }
}
