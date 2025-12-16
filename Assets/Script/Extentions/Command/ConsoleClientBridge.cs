using Mirror;
using UnityEngine;
using System.Linq; // Needed for string array manipulation

namespace Meta
{
    // MUST inherit from NetworkBehaviour to use [Command] and [TargetRpc]
    public class ConsoleClientBridge : NetworkBehaviour
    {
        // 1. EVENT: Used by DeveloperConsoleBehaviour to receive the server's response.
        public static event System.Action<string> OnReceiveCommandResult;

        // 2. STATIC CONTEXT FIELD: Stores the NetworkIdentity of the player who sent the command.
        // This is safe because Mirror processes Commands sequentially on the server.
        private static NetworkIdentity s_currentCommandSenderIdentity = null;

        // 3. STATIC HELPER: Allows server-side commands (like UnstuckCommand) to retrieve the sender.
        public static NetworkIdentity GetCurrentCommandSenderIdentity()
        {
            return s_currentCommandSenderIdentity;
        }

        // --- Client -> Server Command Initiator ---

        // This is called by the client's DeveloperConsoleBehaviour to start the process.
        public void SendConsoleCommandToServer(string fullInput)
        {
            // Only the local player's bridge should attempt to send commands.
            if (isLocalPlayer)
            {
                CmdExecuteConsoleCommand(fullInput);
            }
        }

        // --- Server Execution ---

        // [Command] tells Mirror to send this method call from the client to the server
        [Command]
        private void CmdExecuteConsoleCommand(string fullInput)
        {
            // DEFENSIVE CHECK: Ensure the sender has a player object on the server.
            if (connectionToClient.identity == null)
            {
                // Send an error back to the client if they are not fully spawned in.
                TargetRpcReceiveCommandResult(connectionToClient,
                    "<color=#FF0000>Error:</color> Command failed. You do not have a Player Object spawned on the server yet.");
                return;
            }

            // 1. SET CONTEXT: Store the sender's identity *before* execution.
            s_currentCommandSenderIdentity = connectionToClient.identity;

            // Find the server's local console instance (must be in the scene)
            DeveloperConsoleBehaviour console = FindFirstObjectByType<DeveloperConsoleBehaviour>();

            if (console == null)
            {
                s_currentCommandSenderIdentity = null; // Clean up
                TargetRpcReceiveCommandResult(connectionToClient,
                    "<color=#FF0000>Error:</color> Server console system is not available.");
                return;
            }

            // Execute the command using the server's local console handler (unified logic)
            string result = console.DeveloperConsole.ExecuteCommand(fullInput);

            // 2. CLEAN UP CONTEXT: Clear the static sender identity *after* execution.
            s_currentCommandSenderIdentity = null;

            // Send the result string back to the specific client that initiated the command.
            TargetRpcReceiveCommandResult(connectionToClient, result);
        }

        // --- Client Response ---

        // [TargetRpc] tells Mirror to send this method call from the server back 
        // to a specific client (the 'target' connection).
        [TargetRpc]
        private void TargetRpcReceiveCommandResult(NetworkConnection target, string result)
        {
            // When the client receives the result, invoke the event locally 
            // to update the console UI (handled by DeveloperConsoleBehaviour).
            OnReceiveCommandResult?.Invoke(result);
        }
    }
}