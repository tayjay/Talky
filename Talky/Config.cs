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
        
        [Description("Default emotion to use when the player is not talking. Options are: Angry, AwkwardSmile, Chad, Happy, Neutral, Ogre, Scared.")]
        public string DefaultEmotion { get; set; } = "Neutral";
        
#if EXILED
        public bool IsEnabled { get; set; } = true;
        public bool Debug { get; set; } = false;
#endif
    }
}