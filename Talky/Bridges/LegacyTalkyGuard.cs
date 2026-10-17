using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using LabApi.Loader;
using LabApi.Loader.Features.Paths;

namespace Talky.Bridges;

/// <summary>Refuses obsolete Talky assemblies before either framework enables this plugin.</summary>
internal static class LegacyTalkyGuard
{
    internal static void ThrowIfLegacyPresent()
    {
        string port = LabApi.Features.Wrappers.Server.Port.ToString(CultureInfo.InvariantCulture);
        List<KeyValuePair<string, SearchOption>> directories = new List<KeyValuePair<string, SearchOption>>();
        IEnumerable<string> configuredPaths = PluginLoader.Config?.PluginPaths ??
                                              new List<string> { "global", "$port" };
        foreach (string path in configuredPaths)
        {
            if (path == null)
                continue;
            directories.Add(new KeyValuePair<string, SearchOption>(
                Path.Combine(PathManager.Plugins.FullName, path.Replace("$port", port)),
                SearchOption.AllDirectories));
        }

        ExiledBridge.AddLegacyCheckDirectories(directories, port);
        List<AssemblyName> loaded = new List<AssemblyName>();
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            loaded.Add(assembly.GetName());

        string conflict = FindConflict(loaded, directories);
        if (conflict != null)
        {
            string message = "Legacy Talky DLL detected: " + conflict +
                ". Remove legacy Talky.LabAPI/Talky.EXILED DLLs from the plugin directories " +
                "and restart before enabling Talky. Disabling their configuration is insufficient.";
            LabApi.Features.Console.Logger.Error(message);
            throw new InvalidOperationException(message);
        }
    }

    internal static bool IsLegacyIdentity(AssemblyName identity)
    {
        string name = identity?.Name;
        return string.Equals(name, "Talky.LabAPI", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Talky.LabAPI.Debug", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Talky.EXILED", StringComparison.OrdinalIgnoreCase);
    }

    // Explicit inputs keep the metadata/file policy independent of framework startup order.
    internal static string FindConflict(
        IEnumerable<AssemblyName> loadedIdentities,
        IEnumerable<KeyValuePair<string, SearchOption>> directories)
    {
        foreach (AssemblyName identity in loadedIdentities)
        {
            if (IsLegacyIdentity(identity))
                return identity.FullName + " (already loaded)";
        }

        HashSet<string> inspected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, SearchOption> directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory.Key) || !Directory.Exists(directory.Key))
                continue;

            foreach (string path in Directory.EnumerateFiles(directory.Key, "*.dll", directory.Value))
            {
                if (!inspected.Add(Path.GetFullPath(path)))
                    continue;

                AssemblyName identity;
                try
                {
                    // Reads the manifest without loading the assembly or running plugin code.
                    identity = AssemblyName.GetAssemblyName(path);
                }
                catch (BadImageFormatException)
                {
                    // Native or malformed DLLs cannot contain a loadable legacy managed plugin.
                    continue;
                }

                if (IsLegacyIdentity(identity))
                    return identity.FullName + " at '" + Path.GetFullPath(path) + "'";
            }
        }

        return null;
    }
}
