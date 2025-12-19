using UnityEngine;

namespace Meta.Commands
{
    public class LogCommand : BaseCommand
    {
        public override string Name => "log";

        public override string Help => "log a message localy";

        public override string Execute(CommandContext _Context)
        {
            string _LogText = string.Join(" ", _Context.Args);

            return $"[log] {_LogText}";
        }
    }
}