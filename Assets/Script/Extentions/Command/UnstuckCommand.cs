using UnityEngine;
using Mirror;
using System.Linq;
using System.Collections.Generic;

namespace Meta.Commands
{
    public class UnstuckCommand : BaseCommand
    {
        public override string Name => "unstuck";

        public override string Help => "If You Stuck Use This Command";

        public override string Execute(CommandContext _Context)
        {
            List<Transform> spawnPoints = NetworkManager.startPositions;

            if (spawnPoints == null || spawnPoints.Count == 0)
            {
                return "<color=#FF0000>Error:</color> No Network Start Positions found in the scene to teleport to.";
            }

            Transform spawnPoint = spawnPoints.FirstOrDefault();

            if (spawnPoint == null)
            {
                return "<color=#FF0000>Error:</color> Could not find a valid spawn point transform.";
            }

            Vector3 targetPosition = spawnPoint.position + new Vector3(0f, 0f, 0f);
            _Context.SenderObject.transform.position = targetPosition;

            // Optional: Reset player physics
            Rigidbody rb = _Context.SenderObject.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            string posString = $"({targetPosition.x:F1}, {targetPosition.y:F1}, {targetPosition.z:F1})";
            return $"<color=#00FF00>Success:</color> You have been unstuck and moved to {posString}.";
        }
    }
}