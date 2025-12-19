using System.Text;
using UnityEditor.Hardware;
using UnityEngine;

namespace Meta.Commands
{
    [AddComponentMenu("Meta/HelpCommand")]
    [HelpURL("https://github.com/DreamFaver")]
    public class HelpCommand : BaseCommand
    {
        public override string Name => "help";
        public override string Help => "List Of All Commands";

        public override string Execute(CommandContext _Context)
        {
            StringBuilder _Sb = new StringBuilder();
            _Sb.AppendLine("Available Command:");
            _Sb.AppendLine("<color=#00FF00>help</color> : <color=#FFDA00>show all commands</color>");
            _Sb.AppendLine("<color=#00FF00>fps</color> : <color=#FFDA00>show player fps</color>");
            _Sb.AppendLine("<color=#00FF00>tp</color> : <color=#FFDA00>teleport player => /tp x y z</color>");
            _Sb.AppendLine("<color=#00FF00>clear</color> : <color=#FFDA00>clear chatbox</color>");
            _Sb.AppendLine("<color=#00FF00>log</color> : <color=#FFDA00>log an message localy</color>");
            _Sb.AppendLine("<color=#00FF00>mute</color> : <color=#FFDA00>mute game sound</color>");
            _Sb.AppendLine("<color=#00FF00>destroy</color> : <color=#FFDA00>destory vehicle currently looking at</color>");
            _Sb.AppendLine("<color=#00FF00>unstuck</color> : <color=#FFDA00>tp player to first start point</color>");

            return _Sb.ToString();
        }
    }
}