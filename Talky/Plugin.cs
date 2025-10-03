using System;
using System.Linq;
using Exiled.API.Enums;
using Exiled.Events.EventArgs.Player;
using LabApi.Features;
using LabApi.Features.Console;
using LabApi.Loader.Features.Plugins.Enums;


namespace Talky
{
    public class Plugin
    {
        // Shared constants
        public static string Name { get; } = "Talky";
        public static string Author { get; } = "TayTay";
        public static Version Version { get; } = new Version(0, 4, 0, 0);
        
        // These will be set depending on which plugin system is being used.
        public static LabAPIPlugin LabAPI { get; private set; } = null;
        public static ExiledPlugin Exiled { get; private set; } = null;
        
        /**
         * Dynamically gets the config from either LabAPI or Exiled, depending on which one is being used.
         */
        public static Config Config => LabAPI != null ? LabAPI.Config : Exiled?.Config;
        public static VoiceChattingHandler VoiceChattingHandler => LabAPI != null ? LabAPI.voiceChattingHandler : Exiled?.voiceChattingHandler;
        public static SSTalkySettings Settings => LabAPI != null ? LabAPI.settings : Exiled?.settings;
        
        /**
         * This class will be used if placed in the LabAPI plugin directory.
         */
        public class LabAPIPlugin : LabApi.Loader.Features.Plugins.Plugin<Config>
        {
            
            public VoiceChattingHandler voiceChattingHandler;
            public SSTalkySettings settings;
            public static LabAPIPlugin Instance { get; private set; }
            
            
            public override void Enable()
            {
                Instance = this;
                LabAPI = this;
                settings = new SSTalkySettings();
                settings.Activate();
                voiceChattingHandler =  new VoiceChattingHandler();
                voiceChattingHandler.RegisterEvents();
            }

            public override void Disable()
            {
                if (voiceChattingHandler != null)
                {
                    voiceChattingHandler.UnregisterEvents();
                    voiceChattingHandler = null;
                }
                if (settings != null)
                {
                    settings.Deactivate();
                    settings = null;
                }
                Instance = null;
                LabAPI = null;
            }

            public override string Name { get; } = $"{Plugin.Name}.LabAPI";
            public override string Description { get; } = "A plugin for LabApi that adds mouth movements while talking in-game.";
            public override Version RequiredApiVersion { get; } = new Version(LabApiProperties.CompiledVersion);
            public override string Author { get; } = Plugin.Author;
            public override Version Version { get; } = Plugin.Version;
        }
        
        /**
         * This class will be used if placed in the Exiled plugin directory.
         */
        public class ExiledPlugin : Exiled.API.Features.Plugin<Config>
        {
            
            public VoiceChattingHandler voiceChattingHandler;
            public SSTalkySettings settings;
            
            public static ExiledPlugin Instance { get; private set; }
            
            public override void OnEnabled()
            {
                Instance = this;
                Exiled = this;
                settings = new SSTalkySettings();
                settings.Activate();
                voiceChattingHandler =  new VoiceChattingHandler();
                voiceChattingHandler.RegisterEvents();
            }
            
            public override void OnDisabled()
            {
                if (voiceChattingHandler != null)
                {
                    voiceChattingHandler.UnregisterEvents();
                    voiceChattingHandler = null;
                }
                if (settings != null)
                {
                    settings.Deactivate();
                    settings = null;
                }
                Instance = null;
                Exiled = null;
            }

            public override string Author { get; } = Plugin.Author;
            public override Version Version { get; } = Plugin.Version;
            public override string Name { get; } =  $"{Plugin.Name}.EXILED";
            public override string Prefix { get; } = "talky_exiled";
        }
        
        
    }
}