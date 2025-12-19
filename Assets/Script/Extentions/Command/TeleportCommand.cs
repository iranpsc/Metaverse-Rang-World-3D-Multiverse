using UnityEngine;

namespace Meta.Commands
{
    [AddComponentMenu("Meta/TeleportCommand")]
    [HelpURL("https://github.com/DreamFaver")]
    public class TeleportCommand : BaseCommand
    {
        public override string Name => "tp";
        public override string Help => "tp x y z";

        public override string Execute(CommandContext _Context)
        {
            if (_Context.Args.Length < 3) return "Usage: tp x y z";

            Vector3 Pos = new Vector3(float.Parse(_Context.Args[0]), float.Parse(_Context.Args[1]), float.Parse(_Context.Args[2]));

            _Context.SenderObject.transform.position = Pos;

            Rigidbody rb = _Context.SenderObject.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            return "<color=#00FF00>Teleported.</color>";
        }
    }
}