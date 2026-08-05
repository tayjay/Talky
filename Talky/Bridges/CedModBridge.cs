using System;
using System.Collections.Generic;
using System.Reflection;
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
            CedModPluginState state = CedModCompatibilityPolicy.Inspect(match);
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
}
