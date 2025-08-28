using System;
using Exiled.API.Features;

namespace Talky.EXILED
{
    public class Plugin : Plugin<Config>
    {
        
        public static Plugin Instance { get; private set; }

        
        public static VoiceChattingHandler voiceChattingHandler;

        public override void OnEnabled()
        {
            Instance = this;
            // Your initialization code here
            voiceChattingHandler =  new VoiceChattingHandler();
            
            voiceChattingHandler.RegisterEvents();

            base.OnEnabled();
        }

        public override void OnDisabled()
        {
            // Your cleanup code here
            if (voiceChattingHandler != null)
            {
                voiceChattingHandler.UnregisterEvents();
                voiceChattingHandler = null;
            }
            base.OnDisabled();
        }
        
        
        public override string Name => "Talky.EXILED";
        public override string Author => "TayTay";
        public override string Prefix => "Talky.EXILED";
        public override Version Version => new Version(0, 1, 1);
    }
}