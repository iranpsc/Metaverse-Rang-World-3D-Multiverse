using Mirror;
using UnityEngine;

[CreateAssetMenu(fileName = "Teleport", menuName = "Meta/Command/Teleport")]
public class TeleportCommand : ConsoleCommandSO
{
    public override void ExecuteLocal(CommandContext _Context) { }

    public override void ExecuteServer(CommandContext context)
    {
        if (context.Args.Length < 3)
        {
            context.SetResult("Usage: /tp x y z", CommandResultType.Error);
            return;
        }

        if (!float.TryParse(context.Args[0], out float x) ||
            !float.TryParse(context.Args[1], out float y) ||
            !float.TryParse(context.Args[2], out float z))
        {
            context.SetResult("Invalid coordinates", CommandResultType.Error);
            return;
        }

        Vector3 targetPos = new Vector3(x, y, z);

        if (context.Sender != null)
        {
            // فقط روی خود مالک Player اعمال شود
            NetworkCommandExecutor.Instance.TargetTeleport(context.Sender.connectionToClient, targetPos);
            context.SetResult($"Teleported to {x}, {y}, {z}", CommandResultType.Success);
        }
        else
        {
            context.SetResult("Error: Sender not found", CommandResultType.Error);
        }
    }
}
