using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Talky.Compatibility;

namespace Talky.Bridges;

internal static class ExiledBridge
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ExiledPresent()
    {
        bool loaderPresent = false;
        bool apiPresent = false;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string name = assembly.GetName().Name;
            loaderPresent |= string.Equals(
                name,
                "Exiled.Loader",
                StringComparison.OrdinalIgnoreCase);
            apiPresent |= string.Equals(
                name,
                "Exiled.API",
                StringComparison.OrdinalIgnoreCase);
            if (loaderPresent && apiPresent)
                return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static CedModRuntimeProbe ProbeCedModExiled()
    {
        if (!ExiledPresent())
            return CedModRuntimeProbe.NotPresent("EXILED");

        try
        {
            return ProbeCedModExiledCore();
        }
        catch (Exception exception)
        {
            return new CedModRuntimeProbe(
                CedModPluginState.Unreadable,
                "EXILED",
                null,
                null,
                "discovery failed: " + exception.GetType().Name + ": " + exception.Message);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CedModRuntimeProbe ProbeCedModExiledCore()
    {
        List<object> enabledCedModPlugins = new List<object>();
        foreach (var plugin in Exiled.Loader.Loader.Plugins)
        {
            if (plugin?.GetType().FullName != CedModBridge.PluginTypeName)
                continue;

            if (plugin.Config == null || !plugin.Config.IsEnabled)
                continue;

            enabledCedModPlugins.Add(plugin);
        }

        return CedModBridge.ProbeCandidates(enabledCedModPlugins, "EXILED");
    }

}
