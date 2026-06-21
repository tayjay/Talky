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
/// On an EXILED server, loads the embedded Talky.Exiled shim assembly and registers its plugin
/// type directly into EXILED's native plugin set (<see cref="Loader.Plugins"/>). No-ops entirely
/// when EXILED isn't present, so a LabAPI-only server never touches an EXILED type or the shim.
/// </summary>
public static class ExiledRegistration
{
    // Manifest-resource name of the embedded shim. The ".bin" suffix keeps LabAPI/EXILED's
    // auto-extractors (which only grab "*.dll"/"*.dll.compressed") from loading it on LabAPI-only
    // servers; we load it ourselves, here, only when EXILED is present.
    private const string ShimResource = "Talky.Exiled.dll.bin";

    private static Type _pluginType;

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

        if (_pluginType == null)
            _pluginType = LoadShimPluginType();
        if (_pluginType == null) return;

        // Re-add if absent (covers EXILED 'reload', which clears Loader.Plugins).
        // foreach, NOT .Any(lambda): a lambda would bake IPlugin<> into a closure field.
        foreach (var existing in Loader.Plugins)
            if (existing.Name == TalkyBridge.Name)
                return;

        var instance = (IPlugin<IConfig>)Activator.CreateInstance(_pluginType);
        Loader.Plugins.Add(instance);
        Log.Info($"Replacing Talky plugin with {_pluginType.FullName}.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Type LoadShimPluginType()
    {
        Assembly self = typeof(ExiledRegistration).Assembly;
        using Stream stream = self.GetManifestResourceStream(ShimResource);
        if (stream == null)
        {
            Log.Error($"Talky: embedded shim resource '{ShimResource}' was not found.");
            return null;
        }

        using MemoryStream ms = new MemoryStream();
        stream.CopyTo(ms);
        Assembly shim = Assembly.Load(ms.ToArray());
        return shim.GetType("Talky.Exiled.TalkyExiledPlugin", throwOnError: true);
    }
}
