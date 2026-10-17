using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using LabApi.Loader.Features.Paths;
using Talky.Compatibility;
using YamlDotNet.RepresentationModel;

namespace Talky.Bridges;

internal static class ExiledBridge
{
    internal static void AddLegacyCheckDirectories(
        ICollection<KeyValuePair<string, SearchOption>> directories,
        string port)
    {
        string plugins = ExiledPresent() ? GetRuntimeConfiguredPluginDirectory() : null;
        if (string.IsNullOrWhiteSpace(plugins))
        {
            // The loader can enable after Talky, or be absent during this startup.
            // Read its authoritative LabAPI config without resolving EXILED types.
            string loaderConfig = Path.Combine(
                PathManager.Configs.FullName, port, "Exiled Loader", "config.yml");
            string configuredRoot = ReadConfiguredExiledRoot(loaderConfig);
            if (!string.IsNullOrWhiteSpace(configuredRoot))
                plugins = Path.Combine(configuredRoot, "Plugins");
        }
        if (string.IsNullOrWhiteSpace(plugins) && ExiledPresent())
            plugins = GetRuntimePluginDirectory();
        if (string.IsNullOrWhiteSpace(plugins))
            plugins = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "EXILED", "Plugins");

        // Only the effective root and active port participate. A different server's
        // default/shared EXILED directory must not block a configured isolated root.
        AddPluginDirectory(directories, plugins, port);
    }

    private static void AddPluginDirectory(
        ICollection<KeyValuePair<string, SearchOption>> directories,
        string plugins,
        string port)
    {
        if (string.IsNullOrWhiteSpace(plugins))
            return;
        directories.Add(new KeyValuePair<string, SearchOption>(plugins, SearchOption.TopDirectoryOnly));
        directories.Add(new KeyValuePair<string, SearchOption>(
            Path.Combine(plugins, port), SearchOption.TopDirectoryOnly));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string GetRuntimeConfiguredPluginDirectory()
    {
        if (Exiled.Loader.LoaderPlugin.Instance == null)
            return null;
        string configuredRoot = Exiled.Loader.LoaderPlugin.Config?.ExiledDirectoryPath;
        return string.IsNullOrWhiteSpace(configuredRoot)
            ? null
            : Path.Combine(configuredRoot, "Plugins");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string GetRuntimePluginDirectory() => Exiled.API.Features.Paths.Plugins;

    internal static string ReadConfiguredExiledRoot(string loaderConfig)
    {
        if (!File.Exists(loaderConfig))
            return null;
        using StringReader input = new StringReader(File.ReadAllText(loaderConfig));
        YamlStream yaml = new YamlStream();
        yaml.Load(input);
        if (yaml.Documents.Count == 0)
            return null;
        if (!(yaml.Documents[0].RootNode is YamlMappingNode root))
            throw new InvalidDataException("EXILED loader configuration is not a YAML mapping: " + loaderConfig);
        if (!root.Children.TryGetValue(new YamlScalarNode("exiled_directory_path"), out YamlNode value))
            return null;
        if (!(value is YamlScalarNode scalar))
            throw new InvalidDataException("EXILED exiled_directory_path is not a scalar: " + loaderConfig);
        return scalar.Value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ExiledPresent()
    {
        bool loaderPresent = false;
        bool apiPresent = false;
        // Don't use LINQ here because it exposes Exiled types beyond "NoInlining".
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
