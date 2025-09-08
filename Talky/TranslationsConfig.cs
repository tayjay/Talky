using System.ComponentModel;

namespace Talky;

public class TranslationsConfig
{
    
    [Description("Header for Talky settings in the server-specific settings menu.")]
    public string SSGroupLabel { get; set; } = "Talky Settings";
    [Description("Label for the Animate - Grab keybind setting.")]
    public string SSGrabLabel { get; set; } = "Animate - Grab";
    [Description("Hint for the Animate - Grab keybind setting.")]
    public string SSGrabHint { get; set; } = "Reach out and grab something.";
    [Description("Label for the Default Emotion dropdown setting.")]
    public string SSDefaultEmotionLabel { get; set; } = "Default Emotion";
    [Description("Hint for the Default Emotion dropdown setting.")]
    public string SSDefaultEmotionHint { get; set; } = "How you look when not talking.";
    
    [Description("Emotion preset options. Changing these will change the options in the Default Emotion dropdown setting.")]
    public string Neutral { get; set; } = "Neutral";
    public string Happy { get; set; } = "Happy";
    public string AwkwardSmile { get; set; } = "Awkward Smile";
    public string Scared { get; set; } = "Scared";
    public string Angry { get; set; } = "Angry";
    public string Chad { get; set; } = "Chad";
    public string Ogre { get; set; } = "Ogre";
    
}