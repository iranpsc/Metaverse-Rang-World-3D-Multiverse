using UnityEngine;

namespace Meta.Commands
{
    [AddComponentMenu("Meta/ClearCommand")]
    [HelpURL("https://github.com/DreamFaver")]
    public class ClearCommand : BaseCommand
    {
        public override string Name => "clear";
        public override string Help => "Clear Text Box";
        public override bool RequiresAuthority => false;
        public override string Execute(CommandContext _Context)
        {
            ConsoleUI.Instance.ClearOutput();
            return null;
        }
    }
}