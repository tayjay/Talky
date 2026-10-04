using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using LabApi.Loader;
using Talky.Core;

namespace Talky.Bridges;

/// <summary>
/// Registers the embedded EXILED shim only when EXILED is present.
/// </summary>
internal static class ExiledRegistration
{
    // The .bin suffix prevents host auto-extraction on LabAPI-only servers.
    private const string ShimResource = "Talky.Exiled.Shim.dll.bin";

    /// <summary>Registers the shim when EXILED is loaded.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void TryRegisterNativePlugin()
    {
        // Keep EXILED type resolution behind the assembly-presence check.
        if (ExiledBridge.ExiledPresent())
            EnsureRegistered();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EnsureRegistered()
    {
        // Defer to an already-enabled LabAPI instance.
        if (PluginLoader.EnabledPlugins.Any(p => p.Name == TalkyBridge.Name)) return;
        if (Exiled.Loader.LoaderPlugin.Instance == null) return;

        LoadShimPlugin();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadShimPlugin()
    {
        Assembly self = typeof(ExiledRegistration).Assembly;
        using Stream stream = self.GetManifestResourceStream(ShimResource);
        if (stream == null)
        {
            Exiled.API.Features.Log.Error($"Embedded shim resource '{ShimResource}' was not found.");
            return;
        }

        using MemoryStream ms = new MemoryStream();
        stream.CopyTo(ms);
        Assembly shim = Assembly.Load(ms.ToArray());

        // Let EXILED create the plugin so configuration and translations bind normally.
        Exiled.API.Interfaces.IPlugin<Exiled.API.Interfaces.IConfig> plugin = Exiled.Loader.Loader.CreatePlugin(shim);
        if (plugin == null)
        {
            Exiled.API.Features.Log.Error("The embedded EXILED shim did not yield a plugin instance.");
            return;
        }

        // EXILED reloads may clear the plugin list.
        bool any = false;
        // Cannot use LINQ as it exposes Exiled types beyond "NoInlining".
        foreach (var p in Exiled.Loader.Loader.Plugins)
        {
            if (p.Name == plugin.Name)
            {
                any = true;
                break;
            }
        }

        if (!any)
        {
            Exiled.API.Features.Log.Info($"Loaded plugin {plugin.Name}@{plugin.Version}");
            Exiled.API.Features.Server.PluginAssemblies.Add(shim, plugin);
            Exiled.Loader.Loader.Plugins.Add(plugin);
        }
    }
}
