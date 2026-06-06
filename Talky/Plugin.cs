using System;
using System.IO;
using System.Linq;
using LabApi.Events.Handlers;
using LabApi.Features;
using LabApi.Features.Console;
using LabApi.Loader;
using LabApi.Loader.Features.Paths;
using LabApi.Loader.Features.Plugins;
using MEC;


namespace Talky;

public class Plugin : Plugin<Config>
{
    public VoiceChattingHandler VoiceChattingHandler;
    public FakeLookHandler FakeLookHandler;
    public SSTalkySettings Settings;
    public static Plugin Instance { get; private set; }
    
    public override void Enable()
    {
        Instance = this;
        Settings = new SSTalkySettings();
        Settings.Activate();
        VoiceChattingHandler =  new VoiceChattingHandler();
        FakeLookHandler = new FakeLookHandler();
        
        VoiceChattingHandler.RegisterEvents();
        ServerEvents.PluginsEnabled += OnPluginsEnabled;
    }
 
    public override void Disable()
    {
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
        Settings.Deactivate();
        Instance = null;
    }

    public void OnPluginsEnabled()
    {
        // Look for old Talky versions and disable them if found.
        // Look for Talky.LabAPI
        var plugin = PluginLoader.EnabledPlugins.FirstOrDefault(plugin => plugin.Name.Equals("Talky.LabAPI", StringComparison.OrdinalIgnoreCase));
        if (plugin != null)
        {
            Logger.Warn($"{plugin.Name} was found and will be disabled to prevent conflicts. It may still be unstable. Please remove it from your LabApi plugins folder.");
            plugin.UnregisterCommands();
            plugin.Disable();
            PluginLoader.EnabledPlugins.Remove(plugin);
        }
        
        // Look for Talky.EXILED
        if (ExiledBridge.ExiledPresent())
        {
            ExiledBridge.TryDisableLegacyTalky();

            // Retry shortly after LabAPI's PluginsEnabled event just in case Exiled loads later than expected.
            Timing.CallDelayed(0.25f, ()=>ExiledBridge.TryDisableLegacyTalky());
        }
        
        // Attempt to patch CedMod regardless of if it is there or not, or if its config is configured to break our plugin.
        // If it is not there, nothing will happen. If it is there, the patch will only do something if it is called.
        if(HarmonyBridge.HarmonyPresent())
            if (HarmonyBridge.ApplyPatches())
                FakeLookHandler.RequireHarmonyPatch = CheckCedMod();
        FakeLookHandler.RegisterEvents();
    }
        
    public bool CheckCedMod()
    {
        foreach(var cedModAssembly in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name.StartsWith("CedMod")))
        {
            var cedModMainType = cedModAssembly.GetTypes().FirstOrDefault(type => type.FullName == "CedMod.CedModMain");
            if (cedModMainType == null) continue;
        
            // Get the Singleton instance of CedMod
            var singletonField = cedModMainType.GetField("Singleton");
            var cedModInstance = singletonField.GetValue(null);
            // Get Config property
            var configProperty = cedModMainType.GetProperty("Config");
            var config = configProperty.GetValue(cedModInstance);
            // Get CedModConfig property
            var cedModConfigType = config.GetType();
            var cedModConfigProperty = cedModConfigType.GetProperty("CedMod");
            var cedModConfig = cedModConfigProperty.GetValue(config);
            // Get value of DisableFakeSyncing
            var disableFakeSyncingProperty = cedModConfig.GetType().GetProperty("DisableFakeSyncing");
            var disableFakeSyncingValue = (bool)disableFakeSyncingProperty.GetValue(cedModConfig);
            if (!disableFakeSyncingValue)
            {
                Logger.Warn("Applying CedMod workaround patch...");
                return true;
            }
        }
        
        return false;
    }

        
    public override string Author { get; } = "TayTay";
    public override Version Version { get; } = typeof(Plugin).Assembly.GetName().Version;
    public override string Name { get; } = "Talky";
    public override string Description { get; } = "A plugin that adds animations when players talk, react, and perform common actions.";
    public override Version RequiredApiVersion { get; } = typeof(PluginLoader).Assembly.GetName().Version;
    
    public string githubRepo = "tayjay/Talky";
        
}
