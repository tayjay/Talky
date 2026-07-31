using System;
using System.Data;
using System.Linq;
using System.Reflection;
using LabApi.Events.Handlers;
using LabApi.Features.Console;
using LabApi.Loader;
using LabApi.Loader.Features.Plugins;
using MEC;
using Talky.Bridges;
using Talky.Core;
using Talky.Handlers;
using Talky.Performance;
using Talky.Runtime;
using Talky.Settings;


namespace Talky;

public class Plugin : Plugin<Config>
{
    public VoiceChattingHandler VoiceChattingHandler;
    public FakeLookHandler FakeLookHandler;
    public SSTalkySettings Settings;
    public TalkyRuntime Runtime { get; private set; }
    public TalkyPerformanceMonitor PerformanceMonitor { get; private set; }
    public static Plugin Instance { get; private set; }

    private CoroutineHandle _postEnableHandle;
    private int _lifecycleGeneration;
    private bool _enabled;
    private bool _fullyEnabled;

    // Set by the EXILED native path (TalkyBridge.Enable) before Enable() runs, so Enable() runs
    // post-load setup directly instead of subscribing to LabAPI's ServerEvents.PluginsEnabled,
    // which has already fired by the time EXILED enables the embedded native plugin.
    internal bool ExiledNativeMode;

    public Plugin()
    {
        Assembly ownAssembly = typeof(Plugin).Assembly;
        if(AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
               assembly != ownAssembly &&
               assembly.GetType(typeof(Plugin).FullName, false) != null))
            throw new DuplicateNameException("Multiple Talky plugin assemblies detected. Please remove legacy versions of Talky to use this.");
        Instance = this;
        WireExiledBridge();
        ExiledRegistration.TryRegisterNativePlugin();
    }

    // Wires identity + lifecycle for the embedded EXILED shim (Talky.Exiled.dll). The shim reads
    // these through TalkyBridge so it needs no compile-time reference back to this assembly.
    // Harmless on LabAPI-only servers, where the shim is never loaded.
    private void WireExiledBridge()
    {
        TalkyBridge.Name = Name;
        TalkyBridge.Prefix = Name.ToLower();
        TalkyBridge.Author = Author;
        TalkyBridge. Version = Version;
        TalkyBridge.Enable = cfg =>
        {
            if (cfg != null) Config = cfg;   // adopt EXILED's loaded config
            ExiledNativeMode = true;
            Enable();
        };
        TalkyBridge.Disable = Disable;
    }
    
    public override void Enable()
    {
        if (_enabled)
            return;

        _enabled = true;
        _fullyEnabled = false;
        int generation = ++_lifecycleGeneration;
        try
        {
            Config ??= new Config();
            ValidatePerformanceConfig();

            Settings = new SSTalkySettings();
            Settings.Activate();
            PerformanceMonitor = new TalkyPerformanceMonitor(Config);
            Runtime = new TalkyRuntime();
            ApplyCurrentPerformancePolicy();
            Runtime.Enable();

            VoiceChattingHandler = new VoiceChattingHandler();
            FakeLookHandler = new FakeLookHandler();

            VoiceChattingHandler.RegisterEvents();
            if (ExiledNativeMode)
                SchedulePostEnable(generation, 0.1f);
            else
            {
                ServerEvents.PluginsEnabled += OnPluginsEnabled;
                // PluginsEnabled is an initial-load signal. A reload after that signal needs a
                // bounded fallback so the plugin cannot remain half-enabled forever.
                SchedulePostEnable(generation, 1f);
            }

            Logger.Warn("Talky was enabled.");
        }
        catch
        {
            try
            {
                Disable();
            }
            catch (Exception cleanupException)
            {
                Logger.Error("Talky failed to roll back a partial enable: " + cleanupException);
            }

            throw;
        }
    }
    
 
    public override void Disable()
    {
        if (!_enabled && Runtime == null && VoiceChattingHandler == null && FakeLookHandler == null)
            return;

        _enabled = false;
        _fullyEnabled = false;
        ++_lifecycleGeneration;
        ServerEvents.PluginsEnabled -= OnPluginsEnabled;
        if (_postEnableHandle.IsValid)
            Timing.KillCoroutines(_postEnableHandle);
        _postEnableHandle = default;

        Runtime?.Disable();

        if (VoiceChattingHandler != null)
        {
            VoiceChattingHandler.UnregisterEvents();
            VoiceChattingHandler = null;
        }
        if (FakeLookHandler != null)
        {
            FakeLookHandler.UnregisterEvents();
            FakeLookHandler = null;
        }
        if(HarmonyBridge.HarmonyPresent())
            HarmonyBridge.RemovePatches();
        Runtime?.Dispose();
        Runtime = null;
        if (Settings != null)
        {
            Settings.Deactivate();
            Settings = null;
        }
        PerformanceMonitor?.Dispose();
        PerformanceMonitor = null;
        Logger.Warn("Talky was disabled.");
    }

    public void OnPluginsEnabled()
    {
        ServerEvents.PluginsEnabled -= OnPluginsEnabled;
        if (!_enabled)
            return;

        SchedulePostEnable(_lifecycleGeneration, 0.1f);
    }

    private void SchedulePostEnable(int generation, float delay)
    {
        if (_postEnableHandle.IsValid)
            Timing.KillCoroutines(_postEnableHandle);

        _postEnableHandle = Timing.CallDelayed(delay, () => CompleteEnable(generation));
    }

    private void CompleteEnable(int generation)
    {
        _postEnableHandle = default;
        if (!_enabled || _fullyEnabled || generation != _lifecycleGeneration ||
            FakeLookHandler == null || Runtime == null)
        {
            return;
        }

        try
        {
            bool requiresCedModPatch = CheckCedMod();
            FakeLookHandler.RequireHarmonyPatch =
                requiresCedModPatch && HarmonyBridge.HarmonyPresent() && HarmonyBridge.ApplyPatches();
            FakeLookHandler.RegisterEvents();
            _fullyEnabled = true;

            Logger.Debug("Talky was fully enabled.", Config.Debug);
        }
        catch (Exception exception)
        {
            Logger.Error("Talky post-enable setup failed: " + exception);
            FakeLookHandler.RequireHarmonyPatch = false;
            FakeLookHandler.RegisterEvents();
            _fullyEnabled = true;
        }
    }

    public override void LoadConfigs()
    {
        base.LoadConfigs();
        Config ??= new Config();
        ValidatePerformanceConfig();
        PerformanceMonitor?.Reconfigure(Config);
        ApplyCurrentPerformancePolicy();
    }

    internal void ApplyCurrentPerformancePolicy()
    {
        if (Runtime == null)
            return;

        EffectivePerformancePolicy policy = PerformanceMonitor?.CurrentPolicy ??
                                            EffectivePerformancePolicy.Create(Config, PerformanceTier.Full);
        Runtime.ApplyPerformancePolicy(policy);
    }

    private void ValidatePerformanceConfig()
    {
        PerformanceConfigValidationResult validation =
            PerformanceConfigValidator.ValidateAndApply(Config);
        if (validation.WasCorrected)
        {
            Logger.Warn(
                $"Talky corrected {validation.CorrectionCount} performance setting(s): " +
                validation.CorrectionSummary);
        }
    }
        
    private bool CheckCedMod()
    {
        var cedMod = PluginLoader.EnabledPlugins.FirstOrDefault(
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
        {
            Logger.Warn("Applying CedMod workaround patch...");
            return true;
        }

        if (ExiledBridge.ExiledPresent())
            return ExiledBridge.CheckCedModExiled();

        return false;

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

        
    public override string Author { get; } = "TayTay";
    public override Version Version { get; } = typeof(Plugin).Assembly.GetName().Version;
    public override string Name { get; } = "Talky";
    public override string Description { get; } = "A plugin that adds animations when players talk, react, and perform common actions.";
    public override Version RequiredApiVersion { get; } = new Version(1, 1, 7, 0);
    
    public string githubRepo = "tayjay/Talky";
        
}
