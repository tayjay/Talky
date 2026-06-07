using System;
using System.Linq;
using System.Runtime.CompilerServices;
using LabApi.Features.Console;

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