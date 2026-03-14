using System;
using System.IO;
using System.Linq;
using LabApi.Features;
using LabApi.Features.Console;
using LabApi.Loader;
using LabApi.Loader.Features.Paths;
using LabApi.Loader.Features.Plugins;
using MEC;


namespace Talky
{
#if EXILED
    public class Plugin : Exiled.API.Features.Plugin<Config>
#else
    public class Plugin : Plugin<Config>
#endif
    {
        public VoiceChattingHandler VoiceChattingHandler;
        public FakeLookHandler FakeLookHandler;
        public PlayerSnapshotManager PlayerSnapshotManager;
        //public static OverlayAnimationHandler overlayAnimationHandler;
        public SSTalkySettings Settings;
        public static Plugin Instance { get; private set; }
        
        
        
#if EXILED
        public override void OnEnabled()
#else
        public override void Enable()
#endif
        {
#if EXILED
            if (LabApi.Loader.PluginLoader.EnabledPlugins.Any(plugin => plugin.Name == "Talky.LabAPI"))
            {
                Exiled.API.Features.Log.Error("Both Talky.EXILED and Talky.LabAPI were detected. Disabling Talky.EXILED, please remove Talky.LabAPI plugin if you'd like to use this one instead.");
                return;
            }
#endif
            Instance = this;
            Settings = new SSTalkySettings();
            Settings.Activate();
            VoiceChattingHandler =  new VoiceChattingHandler();
            FakeLookHandler = new FakeLookHandler();
            PlayerSnapshotManager = new PlayerSnapshotManager();
            
            FakeLookHandler.RequireHarmonyPatch = CheckCedMod();

            VoiceChattingHandler.RegisterEvents();
            FakeLookHandler.RegisterEvents();
            PlayerSnapshotManager.RegisterEvents();
            
#if EXILED
            base.OnEnabled();
#endif
        }

        

#if EXILED
        public override void OnDisabled()
#else
        public override void Disable()
#endif
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
            if (PlayerSnapshotManager != null)
            {
                PlayerSnapshotManager.UnregisterEvents();
                PlayerSnapshotManager = null;
            }
            if(FakeLookHandler.RequireHarmonyPatch)
                HarmonyBridge.RemovePatches();
            Settings.Deactivate();
            Instance = null;
#if EXILED
            base.OnDisabled();
#endif
        }
        
        public bool CheckCedMod()
        {
#if EXILED
            foreach(var plugin in Exiled.Loader.Loader.Plugins)
#else
            foreach(var plugin in LabApi.Loader.PluginLoader.EnabledPlugins) // CedMod has a High Priority load, will be enabled before this
#endif  
            {
                if (plugin.Name == "CedMod")
                {
                    // Get the Singleton instance of CedMod
                    var cedModType = plugin.GetType();
                    var singletonField = cedModType.GetField("Singleton");
                    var cedModInstance = singletonField.GetValue(null);
                    // Get Config property
                    var configProperty = cedModType.GetProperty("Config");
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
                        HarmonyBridge.ApplyPatches();
                        return true;
                    }
                }
            }
            return false;
        }

        
        public override string Author { get; } = "TayTay";
        public override Version Version { get; } = typeof(Plugin).Assembly.GetName().Version;
        
#if EXILED
        public override string Name { get; } = "Talky.EXILED";
            public override string Prefix => "Talky";
            public override Version RequiredExiledVersion { get; } = new Version(9, 13, 1);
#else 
        public override string Name { get; } = "Talky.LabAPI";
        public override string Description { get; } = "A plugin for LabApi that adds mouth movements while talking in-game.";
        public override Version RequiredApiVersion { get; } = new Version(LabApiProperties.CompiledVersion);
#endif
        
        public string githubRepo = "tayjay/Talky";
        
    }
}