// ListClientsCommand.cs
using UnityEngine;
using Mirror; // IMPORTANT: Need the Mirror namespace
using System.Text;
using System.Collections.Generic;

namespace Meta
{
    [CreateAssetMenu(fileName = "List Clients", menuName = "Meta/Console/List Connected Clients")]
    public class ListClientsCommand : ConsoleCommand
    {
        public override string Execute(string[] _Args)
        {
            // 1. Check if the server is active
            if (!NetworkServer.active)
            {
                return "Error: Cannot list clients. The NetworkServer is not currently active (run server_start first).";
            }

            // 2. Get the dictionary of connections
            Dictionary<int, NetworkConnectionToClient> connections = NetworkServer.connections;

            // Check if there are any connections besides the host (connectionId 0)
            if (connections.Count <= 0)
            {
                return "Info: No clients are currently connected.";
            }

            // 3. Build the output log message
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"--- Connected Clients ({connections.Count} total) ---");

            // We iterate through the dictionary of connections
            foreach (KeyValuePair<int, NetworkConnectionToClient> pair in connections)
            {
                int connectionId = pair.Key;
                NetworkConnectionToClient connection = pair.Value;

                // Get the player object associated with this connection
                GameObject playerObject = connection.identity?.gameObject;

                // Determine player information
                string playerName = playerObject != null ? playerObject.name : "N/A (No Player Object)";
                string clientAddress = connection.address;

                // The Host/Server is always connection ID 0
                string connectionType = (connectionId == 0) ? " (Host/Server)" : "";

                sb.AppendLine($"[ID: {connectionId:D4}]{connectionType} | Player: {playerName} | Address: {clientAddress}");
            }

            sb.AppendLine("----------------------------------------");

            // 4. Return the complete list
            return sb.ToString();
        }
    }
}