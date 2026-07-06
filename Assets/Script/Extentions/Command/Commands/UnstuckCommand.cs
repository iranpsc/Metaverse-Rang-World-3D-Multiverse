using UnityEngine;
using Mirror;
using System.Collections.Generic;

[CreateAssetMenu(fileName = "Unstuck", menuName = "Meta/Command/Unstuck")]
public class UnstuckCommand : ConsoleCommandSO
{
    public override void ExecuteLocal(CommandContext _Context) { }

    public override void ExecuteServer(CommandContext context)
    {
        if (context.Sender == null || context.Sender.connectionToClient == null)
        {
            context.SetResult("Invalid player", CommandResultType.Error);
            return;
        }

        List<Transform> startPoints = NetworkManager.startPositions;
        if (startPoints == null || startPoints.Count == 0)
        {
            context.SetResult("No start positions found", CommandResultType.Error);
            return;
        }

        Vector3 playerPos = context.Sender.transform.position;

        Transform closest = null;
        float minDist = float.MaxValue;

        foreach (var sp in startPoints)
        {
            if (sp == null) continue;

            float dist = Vector3.SqrMagnitude(sp.position - playerPos);
            if (dist < minDist)
            {
                minDist = dist;
                closest = sp;
            }
        }

        if (closest == null)
        {
            context.SetResult("No valid start point found", CommandResultType.Error);
            return;
        }

        Vector3 targetPos = closest.position + Vector3.up;

        // 🔥 این خط تفاوت کلیدی است
        NetworkCommandExecutor.Instance.TargetTeleport(
            context.Sender.connectionToClient,
            targetPos
        );

        context.SetResult(
            $"Unstuck → {targetPos.x:F1}, {targetPos.y:F1}, {targetPos.z:F1}",
            CommandResultType.Success
        );
    }
}
