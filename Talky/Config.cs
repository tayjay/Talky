using System.ComponentModel;

namespace Talky
{
#if EXILED
    public class Config : Exiled.API.Interfaces.IConfig
#else
    public class Config 
#endif
    
    {
        
        [Description("Low volume threshold for voice activation. If the volume is below this value, the player will show their mouth closed.")]
        public float LowVolumeThreshold { get; set; } = 0.005f;
        
        [Description("High volume threshold for voice activation. If the volume is above this value, the player will show their mouth fully open.")]
        public float HighVolumeThreshold { get; set; } = 0.02f;
        
        [Description("Time in milliseconds for the mouth to reset to default after the player stops talking. Default is 500ms.")]
        public int EmotionResetTime { get; set; } = 500;
        
        [Description("Default emotion to use when the player is not talking. Options are: Angry, AwkwardSmile, Chad, Happy, Neutral, Ogre, Scared.")]
        public string DefaultEmotion { get; set; } = "Neutral";
        
        [Description("How to calculate the voice volume. Options are Average and Peak. You will need to tweak your thresholds if you use Peak.")]
        public string CalculationType { get; set; } = "Average";
#if EXILED
        public bool IsEnabled { get; set; } = true;
        public bool Debug { get; set; } = false;
#endif
    }
}