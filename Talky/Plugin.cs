using System;
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
        public static VoiceChattingHandler voiceChattingHandler;
        //public static SSTalkySettings settings;
        public static Plugin Instance { get; private set; }
        
#if EXILED
        public override void OnEnabled()
#else
        public override void Enable()
#endif
        {
            voiceChattingHandler =  new VoiceChattingHandler();
            
            voiceChattingHandler.RegisterEvents();
            
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
        }

        public override string Name { get; } = "Talky";
        public override string Author { get; } = "TayTay";
        public override Version Version { get; } = new Version(0, 1, 2, 0);
        
#if EXILED
            public override string Prefix => "Talky";
#else 
            public override string Description { get; } = "A plugin for LabApi that adds mouth movements while talking in-game.";
            public override Version RequiredApiVersion { get; } = new Version(LabApiProperties.CompiledVersion);
#endif
        
    }
}