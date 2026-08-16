using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Exiled.API.Features;
using Exiled.API.Interfaces;
using Exiled.Loader;
using LabApi.Loader;
using Talky.Core;

namespace Talky.Bridges;

/// <summary>
/// Registers the embedded EXILED shim only when EXILED is present.
/// </summary>
public static class ExiledRegistration
{
    // The .bin suffix prevents host auto-extraction on LabAPI-only servers.
    private const string ShimResource = "Talky.Exiled.Shim.dll.bin";

    /// <summary>Registers the shim when EXILED is loaded.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void TryRegisterNativePlugin()
    {
        // Keep EXILED type resolution behind the assembly-presence check.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            if (asm.GetName().Name == "Exiled.API")
            {
                EnsureRegistered();
                return;
            }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EnsureRegistered()
    {
        // Defer to an already-enabled LabAPI instance.
        if (PluginLoader.EnabledPlugins.Any(p => p.Name == TalkyBridge.Name)) return;
        if (LoaderPlugin.Instance == null) return;

        LoadShimPlugin();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadShimPlugin()
    {
        Assembly self = typeof(ExiledRegistration).Assembly;
        using Stream stream = self.GetManifestResourceStream(ShimResource);
        if (stream == null)
        {
            Log.Error($"Embedded shim resource '{ShimResource}' was not found.");
            return;
        }

        using MemoryStream ms = new MemoryStream();
        stream.CopyTo(ms);
        Assembly shim = Assembly.Load(ms.ToArray());

        // Let EXILED create the plugin so configuration and translations bind normally.
        IPlugin<IConfig> plugin = Loader.CreatePlugin(shim);
        if (plugin == null)
        {
            Log.Error("The embedded EXILED shim did not yield a plugin instance.");
            return;
        }

        // EXILED reloads may clear the plugin list.
        if (!Loader.Plugins.Any<IPlugin<IConfig>>((Func<IPlugin<IConfig>, bool>)(p => p.Name == plugin.Name)))
        {
            Log.Info($"Loaded plugin {plugin.Name}@{plugin.Version}");
            Server.PluginAssemblies.Add(shim, plugin);
            Loader.Plugins.Add(plugin);
        }
    }
}
