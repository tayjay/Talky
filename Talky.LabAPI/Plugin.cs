using System;
using LabApi.Features;
using LabApi.Loader.Features.Plugins;

namespace Talky.LabAPI
{
    public class Plugin : Plugin<Config>
    {
        public static VoiceChattingHandler voiceChattingHandler;
        //public static SSTalkySettings settings;
        public static Plugin Instance { get; private set; }
        
        public override void Enable()
        {
            voiceChattingHandler =  new VoiceChattingHandler();
            
            voiceChattingHandler.RegisterEvents();
            
            Instance = this;
        }

        public override void Disable()
        {
            if (voiceChattingHandler != null)
            {
                voiceChattingHandler.UnregisterEvents();
                voiceChattingHandler = null;
            }
        }

        public override string Name { get; } = "Talky.LabAPI";
        public override string Description { get; } = "A plugin for LabApi that adds mouth movements while talking in-game.";
        public override string Author { get; } = "TayTay";
        public override Version Version { get; } = new Version(0, 1, 0, 0);
        public override Version RequiredApiVersion { get; } = new Version(LabApiProperties.CompiledVersion);
    }
}