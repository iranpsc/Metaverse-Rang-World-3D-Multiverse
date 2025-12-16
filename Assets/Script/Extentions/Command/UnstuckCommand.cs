// UnstuckCommand.cs (FIXED)
using UnityEngine;
using Mirror;
using System.Linq;
using System.Collections.Generic;

namespace Meta
{
    [CreateAssetMenu(fileName = "Unstuck Player", menuName = "Meta/Console/Unstuck Player")]
    public class UnstuckCommand : ConsoleCommand
    {
        public override string Execute(string[] _Args)
        {
            if (!NetworkServer.active)
            {
                return "<color=#FF0000>Error:</color> Unstuck command must be executed on the server. Please ensure you are connected as Host or Client.";
            }

            // FIX 1: Accesses the required static helper method from ConsoleClientBridge
            NetworkIdentity senderIdentity = ConsoleClientBridge.GetCurrentCommandSenderIdentity();
            string _IdentityName = senderIdentity != null ? senderIdentity.gameObject.name : "NULL";
            Debug.Log($"[COMMAND DEBUG] Read senderIdentity from static context: {_IdentityName}");

            if (senderIdentity == null)
            {
                return "<color=#FF0000>Error:</color> Could not identify the player who issued the command on the server. Network identity missing.";
            }

            // FIX 2: Access startPositions statically from the NetworkManager class
            List<Transform> spawnPoints = NetworkManager.startPositions;

            if (spawnPoints == null || spawnPoints.Count == 0)
            {
                return "<color=#FF0000>Error:</color> No Network Start Positions found in the scene to teleport to.";
            }

            // Choose the first spawn point
            Transform spawnPoint = spawnPoints.FirstOrDefault();

            if (spawnPoint == null)
            {
                return "<color=#FF0000>Error:</color> Could not find a valid spawn point transform.";
            }

            // Teleport the player object
            GameObject playerObject = senderIdentity.gameObject;
            Vector3 targetPosition = spawnPoint.position + new Vector3(0, 1f, 0);
            playerObject.transform.position = targetPosition;

            // Optional: Reset player physics
            Rigidbody rb = playerObject.GetComponent<Rigidbody>();
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