using Mirror;
using UnityEngine;

namespace Meta.Commands
{
    [AddComponentMenu("Meta/TeleportCommand")]
    [HelpURL("https://github.com/DreamFaver")]
    public class TeleportCommand : BaseCommand
    {
        public override string Name => "tp";
        public override string Help => "tp x y z";
        public override bool RequiresAuthority => false;

        public override string Execute(CommandContext _Context)
        {
            if (!NetworkServer.active)
                return "Teleport must be executed on server.";

            if (_Context.Args.Length < 3)
                return "Usage: tp x y z";

            if (!float.TryParse(_Context.Args[0], out float _X) ||
                !float.TryParse(_Context.Args[1], out float _Y) ||
                !float.TryParse(_Context.Args[2], out float _Z))
            {
                return "Invalid coordinates.";
            }

            GameObject _Player = _Context.SenderObject;
            if (_Player == null)
                return "Player object not found.";

            Vector3 _TargetPos = new Vector3(_X, _Y, _Z);

            // 🔒 TELEPORT SAFELY (SERVER AUTHORITATIVE)
            Rigidbody _Rb = _Player.GetComponent<Rigidbody>();
            if (_Rb != null)
            {
                _Rb.isKinematic = true;
                _Rb.position = _TargetPos;
                _Rb.linearVelocity = Vector3.zero;
                _Rb.angularVelocity = Vector3.zero;
                _Rb.isKinematic = false;
            }
            else
            {
                _Player.transform.position = _TargetPos;
            }

            // 🔁 Force NetworkTransform sync
            NetworkTransformReliable _NetTransform = _Player.GetComponent<NetworkTransformReliable>();
            if (_NetTransform != null)
            {
                _NetTransform.enabled = false;
                _NetTransform.enabled = true;
            }
            Debug.Log($"[TP] Server teleporting {_Context.SenderConnection.connectionId}");

            return "<color=#00FF00>Teleported.</color>";
        }
    }
}
