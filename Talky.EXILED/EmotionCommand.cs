using System;
using CommandSystem;
using Exiled.API.Features;
using PlayerRoles.FirstPersonControl.Thirdperson.Subcontrollers;
using RemoteAdmin;

namespace Talky.EXILED
{
    [CommandHandler(typeof(ClientCommandHandler))]
    public class EmotionCommand : ParentCommand
    {

        protected override bool ExecuteParent(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (sender is not PlayerCommandSender playerSender)
            {
                response = "This command can only be used by players.";
                return false;
            }

            if (arguments.Count != 1)
            {
                response = $"Invalid emotion. Valid emotions are: Angry, AwkwardSmile, Chad, Happy, Neutral, Ogre, Scared";
                return false;
            }

            string emotion = arguments.At(0);
            Player player = Player.Get(sender);
            if (Enum.TryParse<EmotionPresetType>(emotion, out EmotionPresetType preset))
            {
                player.ReferenceHub.ServerSetEmotionPreset(preset);
                if (player.ReferenceHub.TryGetComponent(out SpeechTracker tracker))
                {
                    tracker.DefaultPreset = preset;
                }
                response = $"Your emotion has been set to {preset}.";
                return true;
            }
            else
            {
                response = $"Invalid emotion. Valid emotions are: Angry, AwkwardSmile, Chad, Happy, Neutral, Ogre, Scared";
                return false;
            }
            response = $"Your emotion has been set to {emotion}.";
            return true;
        }

        public override string Command { get; } = "emotion";
        public override string[] Aliases { get; } = [];
        public override string Description { get; } = "Sets your character's facial expression. Valid emotions are: Angry, AwkwardSmile, Chad, Happy, Neutral, Ogre, Scared";

        public override void LoadGeneratedCommands()
        {
            
        }
    }
}