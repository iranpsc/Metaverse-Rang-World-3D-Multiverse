using UnityEngine;

namespace Meta.Commands
{
    [AddComponentMenu("Meta/KickCommand")]
    [HelpURL("https://github.com/DreamFaver")]
    public class KickCommand : BaseCommand
    {
        public override string Name => "kick";
        public override string Help => "disconnect player";
        public override string Execute(CommandContext _Context)
        {
            _Context.SenderConnection.Disconnect();
            return $"Player => <color=#FF0000>{_Context.SenderConnection}</color> Has Been Kicked From Server";
        }
    }
}