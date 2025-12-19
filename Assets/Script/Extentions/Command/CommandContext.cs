using Mirror;
using UnityEngine;

namespace Meta.Commands
{
    [AddComponentMenu("Meta/CommandContext")]
    [HelpURL("https://github.com/DreamFaver")]
    public class CommandContext
    {
        public NetworkConnectionToClient SenderConnection;
        public NetworkIdentity SenderIdentity;
        public GameObject SenderObject;
        public bool IsServer;
        public string[] Args;

        public CommandContext(NetworkConnectionToClient _Conn, string[] _Args)
        {
            SenderConnection = _Conn;
            SenderIdentity = _Conn.identity;
            SenderObject = _Conn.identity.gameObject;
            Args = _Args;
            IsServer = true;
        }
    }
}