using System.ComponentModel;
using Exiled.API.Interfaces;

namespace Talky
{
    public class Config : Exiled.API.Interfaces.IConfig
    {
        [Description("Low dB threshold for voice activation. If the dB level is below this value, the player will show their mouth closed.")]
        public double LowDbThreshold { get; set; } = -80.0;
        
        [Description("High dB threshold for voice activation. If the dB level is above this value, the player will show their mouth fully open.")]
        public double HighDbThreshold { get; set; } = -30.0;
        
        [Description("Time in milliseconds for the mouth to reset to default after the player stops talking. Default is 500ms.")]
        public int EmotionResetTime { get; set; } = 500;
        
        [Description("Allow players to use the Animate - Grab kebind to make their character reach out. Default is true.")]
        public bool EnableGrabAnimation { get; set; } = true;
        
        [Description("Should the player react with a specific emotion when hurt? Default is true.")]
        public bool EnableReactionOnHurt { get; set; } = true;
        [Description("How much damage must be taken in a single hit to trigger a hurt reaction. Default is 5.")]
        public int MiniumDamageForReaction { get; set; } = 5;
        
        [Description("Should the player open their mouth when consuming items (Medkits, SCP207, etc.)? Default is true.")]
        public bool EnableEmoteOnConsumables { get; set; } = true;
        
        [Description("Talky plugin translations")]
        public TranslationsConfig Translations { get; set; } = new TranslationsConfig();
        [Description("[EXILED] Enable or disable the plugin. Default is true.")]
        public bool IsEnabled { get; set; } = true;
        [Description("[EXILED] Enable or disable debug logs. Default is false.")]
        public bool Debug { get; set; } = false;

    }
}