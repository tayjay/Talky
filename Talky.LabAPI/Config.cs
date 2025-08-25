using System.ComponentModel;

namespace Talky.LabAPI
{
    public class Config
    {
        
        [Description("Low volume threshold for voice activation. If the volume is below this value, the player will show their mouth closed.")]
        public float LowVolumeThreshold { get; set; } = 0.005f;
        
        [Description("High volume threshold for voice activation. If the volume is above this value, the player will show their mouth fully open.")]
        public float HighVolumeThreshold { get; set; } = 0.02f;
        
        [Description("Time in milliseconds for the mouth to reset to default after the player stops talking. Default is 500ms.")]
        public int EmotionResetTime { get; set; } = 500;
    }
}