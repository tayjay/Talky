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
        if(FakeLookHandler.RequireHarmonyPatch)
            HarmonyBridge.RemovePatches();
        Settings.Deactivate();
        Instance = null;
    }

    public void OnPluginsEnabled()
    {
        FakeLookHandler.RequireHarmonyPatch = CheckCedMod();
        if(FakeLookHandler.RequireHarmonyPatch)
            HarmonyBridge.ApplyPatches();
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
    public override string Description { get; } = "A plugin for LabApi that adds mouth movements while talking in-game.";
    public override Version RequiredApiVersion { get; } = typeof(PluginLoader).Assembly.GetName().Version;
    
    public string githubRepo = "tayjay/Talky";
        
}
