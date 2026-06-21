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
using Talky.Settings;


namespace Talky;

public class Plugin : Plugin<Config>
{
    public VoiceChattingHandler VoiceChattingHandler;
    public FakeLookHandler FakeLookHandler;
    public SSTalkySettings Settings;
    public static Plugin Instance { get; private set; }

    // Set by the EXILED native path (TalkyBridge.Enable) before Enable() runs, so Enable() runs
    // post-load setup directly instead of subscribing to LabAPI's ServerEvents.PluginsEnabled,
    // which has already fired by the time EXILED enables the embedded native plugin.
    internal bool ExiledNativeMode;

    public Plugin()
    {
        if(AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name.StartsWith("Talky.LabAPI") || assembly.GetName().Name.StartsWith("Talky.EXILED")))
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
        Settings = new SSTalkySettings();
        Settings.Activate();
        VoiceChattingHandler =  new VoiceChattingHandler();
        FakeLookHandler = new FakeLookHandler();
        
        VoiceChattingHandler.RegisterEvents();
        if (ExiledNativeMode)
            OnPluginsEnabled();                       // native path: LabAPI's PluginsEnabled already fired
        else
            ServerEvents.PluginsEnabled += OnPluginsEnabled;
        Logger.Warn("Talky was enabled.");
    }
    
 
    public override void Disable()
    {
        if(HarmonyBridge.HarmonyPresent())
            HarmonyBridge.RemovePatches();
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
        if (Settings != null)
        {
            Settings.Deactivate();
            Settings = null;
        }
        Logger.Warn("Talky was disabled.");
    }

    public void OnPluginsEnabled()
    {
        Timing.CallDelayed(Timing.WaitForSeconds(0.1f), () =>
        {
            // Attempt to patch CedMod regardless of if it is there or not, or if its config is configured to break our plugin.
            // If it is not there, nothing will happen. If it is there, the patch will only do something if it is called.
            if (HarmonyBridge.HarmonyPresent())
                if (HarmonyBridge.ApplyPatches())
                    FakeLookHandler.RequireHarmonyPatch = CheckCedMod();
            FakeLookHandler.RegisterEvents();

            Logger.Debug("Talky was fully enabled.", Config.Debug);
        });
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
    public override Version RequiredApiVersion { get; } = new Version(1, 1, 7, 0);
    
    public string githubRepo = "tayjay/Talky";
        
}
