using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Exiled.Loader;
using LabApi.Features.Console;

namespace Talky;

internal static class ExiledBridge
{
    private const string LegacyPluginName = "Talky.EXILED";

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ExiledPresent()
    {
        return AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
            assembly.GetName().Name.Equals("Exiled.Loader", StringComparison.OrdinalIgnoreCase));
    }

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
        var plugin = Loader.Plugins.FirstOrDefault(plugin =>
            plugin.Config.IsEnabled &&
            (string.Equals(plugin.Name, LegacyPluginName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(plugin.Assembly?.GetName().Name, LegacyPluginName, StringComparison.OrdinalIgnoreCase)));

        if (plugin == null)
            return false;

        Logger.Warn($"{LegacyPluginName} was found and will be disabled to prevent conflicts. It may still be unstable. Please remove it from your EXILED plugins folder.");

        plugin.Config.IsEnabled = false;
        plugin.OnUnregisteringCommands();
        plugin.OnDisabled();
        Loader.Plugins.Remove(plugin);
        Loader.Locations.Remove(plugin.Assembly);
        Exiled.API.Features.Server.PluginAssemblies.Remove(plugin.Assembly);

        return true;
    }
}