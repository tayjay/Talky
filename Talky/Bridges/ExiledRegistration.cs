using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Exiled.API.Features;     // Log
using Exiled.API.Interfaces;   // IPlugin, IConfig
using Exiled.Loader;           // Loader, LoaderPlugin
using LabApi.Loader;           // PluginLoader
using Talky.Core;              // TalkyBridge

namespace Talky.Bridges;

/// <summary>
/// On an EXILED server, loads the embedded Talky.Exiled.Shim assembly and registers its plugin
/// type directly into EXILED's native plugin set (<see cref="Loader.Plugins"/>). No-ops entirely
/// when EXILED isn't present, so a LabAPI-only server never touches an EXILED type or the shim.
/// </summary>
public static class ExiledRegistration
{
    // Manifest-resource name of the embedded shim. The ".bin" suffix keeps LabAPI/EXILED's
    // auto-extractors (which only grab "*.dll"/"*.dll.compressed") from loading it on LabAPI-only
    // servers; we load it ourselves, here, only when EXILED is present.
    private const string ShimResource = "Talky.Exiled.Shim.dll.bin";

    /// <summary>Call from the LabAPI Plugin's constructor. No-ops unless loaded under EXILED.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void TryRegisterNativePlugin()
    {
        // Only reference EXILED types / load the shim when Exiled.API is actually loaded. EXILED's
        // LoadAssembly resolves our embedded Talky.Core before this runs, so the plugin's config
        // types are already available by the time our ctor executes.
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
        // If LabAPI already enabled a "Talky" (e.g. the DLL also sits in the LabAPI folder), defer to it.
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
            Log.Error($"Talky: embedded shim resource '{ShimResource}' was not found.");
            return;
        }

        using MemoryStream ms = new MemoryStream();
        stream.CopyTo(ms);
        Assembly shim = Assembly.Load(ms.ToArray());

        // Let EXILED build the instance so it gets the same config/translation binding as a plugin
        // loaded from disk. Returns null if the shim exposes no usable plugin type.
        IPlugin<IConfig> plugin = Loader.CreatePlugin(shim);
        if (plugin == null)
        {
            Log.Error("Talky: the embedded EXILED shim did not yield a plugin instance.");
            return;
        }

        // Re-add if absent (covers EXILED 'reload', which clears Loader.Plugins).
        if (!Loader.Plugins.Any<IPlugin<IConfig>>((Func<IPlugin<IConfig>, bool>)(p => p.Name == plugin.Name)))
        {
            Log.Info($"Loaded plugin {plugin.Name}@{plugin.Version}");
            Server.PluginAssemblies.Add(shim, plugin);
            Loader.Plugins.Add(plugin);
        }
    }
}
