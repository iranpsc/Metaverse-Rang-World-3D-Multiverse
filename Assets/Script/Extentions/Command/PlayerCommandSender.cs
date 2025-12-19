using Meta.Player.Core;
using Mirror;
using UnityEngine;

namespace Meta.Commands
{
    [AddComponentMenu("Meta/PlayerCommandSender")]
    [HelpURL("https://github.com/DreamFaver")]
    public class PlayerCommandSender : NetworkBehaviour
    {
        
        public override void OnStartLocalPlayer()
        {
            ConsoleUI.Instance.BindPlayer(this);
        }
        [Command]
        public void CmdSendCommand(string _Raw)
        {
            string _Result = ServerCommandManager.Instance.Execute(_Raw, connectionToClient);
            TargetReceive(connectionToClient, _Result);
        }
        [TargetRpc]
        private void TargetReceive(NetworkConnection _Target, string _Message)
        {
            ConsoleUI.Instance.AddLog(_Message);
        }
    }
}