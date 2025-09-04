using System;
using System.Linq;
using LabApi.Features;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins;


namespace Talky
{
#if EXILED
    public class Plugin : Exiled.API.Features.Plugin<Config>
#else
    public class Plugin : Plugin<Config>
#endif
    
    {
        public VoiceChattingHandler voiceChattingHandler;
        //public static OverlayAnimationHandler overlayAnimationHandler;
        public SSTalkySettings settings;
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
                Logger.Error("Both Talky.EXILED and Talky.LabAPI were detected. Disabling Talky.EXILED, please remove Talky.LabAPI plugin if you'd like to use this one instead.");
                return;
            }
#endif
            settings = new SSTalkySettings();
            settings.Activate();
            voiceChattingHandler =  new VoiceChattingHandler();
            //overlayAnimationHandler = new OverlayAnimationHandler();
            
            voiceChattingHandler.RegisterEvents();
            //overlayAnimationHandler.RegisterEvents();
            
            Instance = this;
        }

#if EXILED
        public override void OnDisabled()
#else
        public override void Disable()
#endif
        {
            if (voiceChattingHandler != null)
            {
                voiceChattingHandler.UnregisterEvents();
                voiceChattingHandler = null;
            }
            /*if (overlayAnimationHandler != null)
            {
                overlayAnimationHandler.UnregisterEvents();
                overlayAnimationHandler = null;
            }*/
            settings.Deactivate();
            Instance = null;
        }

        
        public override string Author { get; } = "TayTay";
        public override Version Version { get; } = new Version(0, 2, 1, 0);
        
#if EXILED
            public override string Name { get; } = "Talky.EXILED";
            public override string Prefix => "Talky";
#else 
        public override string Name { get; } = "Talky.LabAPI";
        public override string Description { get; } = "A plugin for LabApi that adds mouth movements while talking in-game.";
        public override Version RequiredApiVersion { get; } = new Version(LabApiProperties.CompiledVersion);
#endif
        
    }
}