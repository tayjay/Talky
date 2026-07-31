using System;
using System.Linq;
using System.Reflection;
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
    public static bool CheckCedModExiled()
    {
        if(!ExiledPresent()) return false;
        
        var cedMod = Exiled.Loader.Loader.Plugins.FirstOrDefault(
            plugin => plugin.GetType().FullName == "CedMod.CedModMain");
        
        if (cedMod == null) return false;

        if (!TryGetPropertyPath(
                cedMod,
                out bool disableFakeSyncing,
                "Config",
                "CedMod",
                "DisableFakeSyncing"))
        {
            return false;
        }

        if (!disableFakeSyncing)
            Logger.Warn("Applying CedMod workaround patch for Exiled...");

        return !disableFakeSyncing;
    }
    
    private static bool TryGetPropertyPath<T>(
        object root,
        out T value,
        params string[] path)
    {
        object current = root;

        foreach (string memberName in path)
        {
            if (current == null)
            {
                value = default;
                return false;
            }

            PropertyInfo property = current.GetType().GetProperty(
                memberName,
                BindingFlags.Instance | BindingFlags.Public);

            if (property == null || property.GetIndexParameters().Length != 0)
            {
                value = default;
                return false;
            }

            current = property.GetValue(current);
        }

        if (current is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
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