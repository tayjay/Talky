using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using LabApi.Features.Console;
using Talky.Compatibility;

namespace Talky.Bridges;

internal static class ExiledBridge
{
    private const string LegacyPluginName = "Talky.EXILED";

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ExiledPresent()
    {
        return AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
            assembly.GetName().Name.Equals("Exiled.Loader", StringComparison.OrdinalIgnoreCase));
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool TryDisableLegacyTalky()
    {
        if (!ExiledPresent())
            return false;

        try
        {
            return DisableLegacyTalky();
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to disable {LegacyPluginName}: {ex}");
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool DisableLegacyTalky()
    {
        foreach (var plugin in Exiled.Loader.Loader.Plugins)
        {
            if (plugin.Config.IsEnabled &&
                (string.Equals(plugin.Name, LegacyPluginName, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(plugin.Assembly?.GetName().Name, LegacyPluginName, StringComparison.OrdinalIgnoreCase)))
            {
                Logger.Warn($"{LegacyPluginName} was found in {Exiled.Loader.PathExtensions.GetPath(plugin)}. Please remove it to allow Talky {Plugin.Instance.Version} to run.");
                return true;
            }
        }

        return false;
        
    }
}
