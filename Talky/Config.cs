using System.ComponentModel;

namespace Talky
{
#if EXILED
    public class Config : Exiled.API.Interfaces.IConfig
#else
    public class Config 
#endif
    {
        [Description("Low dB threshold for voice activation. If the dB level is below this value, the player will show their mouth closed.")]
        public double LowDbThreshold { get; set; } = -80.0;
        
        [Description("High dB threshold for voice activation. If the dB level is above this value, the player will show their mouth fully open.")]
        public double HighDbThreshold { get; set; } = -30.0;
        
        [Description("Time in milliseconds for the mouth to reset to default after the player stops talking. Default is 500ms.")]
        public int EmotionResetTime { get; set; } = 500;
        
        [Description("How often to update volume. Default is 0.033f (30Hz)")]
        public float SpeechUpdateInterval = 0.033f;
        
        [Description("Allow players to use the Animate - Grab kebind to make their character reach out. Default is true.")]
        public bool EnableGrabAnimation { get; set; } = true;
        
        [Description("Should the player react with a specific emotion when hurt? Default is true.")]
        public bool EnableReactionOnHurt { get; set; } = true;
        [Description("How much damage must be taken in a single hit to trigger a hurt reaction. Default is 5.")]
        public int MiniumDamageForReaction { get; set; } = 5;
        
        [Description("Should the player open their mouth when consuming items (Medkits, SCP207, etc.)? Default is true.")]
        public bool EnableEmoteOnConsumables { get; set; } = true;
        
        [Description("Enable or disable head bobbing while talking. Default is true.")]
        public bool EnableHeadBobbing { get; set; } = true;
        [Description("How much should the player bob their head while talking. Default is 10.")]

        public float HeadBobAmount { get; set; } = 10;
        
        [Description("Enable or disable glancing towards the direction of the voice source while talking. Adds more immersion at cost of performance on large servers. Default is true.")]
        public bool EnableGlancing { get; set; } = true;

        [Description("The maximum range at which glancing will occur. Default is 7 meters.")]
        public float GlaceRange { get; set; } = 7f;
        
        [Description("The field of view angle within which glancing will occur. Default is 140 degrees.")]
        public float GlaceFov { get; set; } = 140f;
        
        [Description("The speed at which the player glances towards the voice source. Default is 1.65.")]
        public float GlaceGain { get; set; } = 1.5f;
        
        [Description("The maximum yaw angle for glancing. Default is 40 degrees.")]
        public float GlanceMaxYaw { get; set; } = 40f;
        
        [Description("The maximum pitch angle for glancing. Default is 35 degrees.")]
        public float GlanceMaxPitch { get; set; } = 35f;
        
        [Description("The maximum duration (in milliseconds) to look at voice source after they stop talking. Default is 1000ms.")]
        public long GlanceMaxDuration { get; set; } = 1000;
        
        //[Description("Should dynamic traits be used for glancing? Makes players move at different speeds to add variety. Default is true.")]
        //public bool GlanceDynamicTraits { get; set; } = true;
        
        
        [Description("Talky plugin translations")]
        public TranslationsConfig Translations { get; set; } = new TranslationsConfig();
#if EXILED
        [Description("Enable or disable the plugin. Default is true.")]
        public bool IsEnabled { get; set; } = true;
        
#endif
        [Description("Enable or disable debug logs. Default is false.")]
        public bool Debug { get; set; } = false;

    }
}