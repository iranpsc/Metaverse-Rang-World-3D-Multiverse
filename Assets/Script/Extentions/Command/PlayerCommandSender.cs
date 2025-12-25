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
        [Command(requiresAuthority = false)]
        public void CmdSendCommand(string _Raw)
        {
            if (ServerCommandManager.Instance == null)
            {
                Debug.LogError("ServerCommandManager.Instance is NULL on server");
                return;
            }

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