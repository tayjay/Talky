using System;
using System.Linq;
using LabApi.Features;
using LabApi.Loader.Features.Plugins;


namespace Talky
{
#if EXILED
    public class Plugin : Exiled.API.Features.Plugin<Config>
#else
    public class Plugin : Plugin<Config>
#endif
    
    {
        public VoiceChattingHandler VoiceChattingHandler;
        public HeadBobHandler HeadBobHandler;
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
            HeadBobHandler = new HeadBobHandler();
            
            VoiceChattingHandler.RegisterEvents();
            HeadBobHandler.RegisterEvents();
            
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
            if (HeadBobHandler != null)
            {
                HeadBobHandler.UnregisterEvents();
                HeadBobHandler = null;
            }
            Settings.Deactivate();
            Instance = null;
#if EXILED
            base.OnDisabled();
#endif
        }

        
        public override string Author { get; } = "TayTay";
        public override Version Version { get; } = typeof(Plugin).Assembly.GetName().Version;
        
#if EXILED
        public override string Name { get; } = "Talky.EXILED";
            public override string Prefix => "Talky";
            public override Version RequiredExiledVersion { get; } = new Version(9, 12, 1);
#else 
        public override string Name { get; } = "Talky.LabAPI";
        public override string Description { get; } = "A plugin for LabApi that adds mouth movements while talking in-game.";
        public override Version RequiredApiVersion { get; } = new Version(LabApiProperties.CompiledVersion);
#endif
        
        public string githubRepo = "tayjay/Talky";
        
    }
}