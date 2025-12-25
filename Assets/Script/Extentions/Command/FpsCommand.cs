using UnityEngine;

namespace Meta.Commands
{
    [AddComponentMenu("Meta/FpsCommand")]
    [HelpURL("https://github.com/DreamFaver")]
    public class FpsCommand : BaseCommand
    {
        public override string Name => "fps";
        public override string Help => "Show Palyer FPS";
        public override bool RequiresAuthority => false;
        public override string Execute(CommandContext _Context)
        {
            return $"<color=#00FF00>FPS:</color> {(int)(1f / Time.deltaTime)}";
        }
    }
}