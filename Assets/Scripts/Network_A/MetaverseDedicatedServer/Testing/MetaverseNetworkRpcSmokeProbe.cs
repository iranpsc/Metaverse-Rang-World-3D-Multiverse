using Network_A.GameServer.Players;
using UnityEngine;

public class MetaverseNetworkRpcSmokeProbe : MetaverseNetworkBehaviour
{
    [Header("Debug")]
    [SerializeField] private bool logMessages = true;
    [SerializeField] private string probeLabel = "phase20_rpc_probe";

    private bool commandRequested;
    private int serverCommandCount;
    private int clientRpcCount;
    private int targetRpcCount;

    public override void OnNetworkSpawn()
    {
        Log("OnNetworkSpawn");

        if (isClient && !commandRequested)
        {
            commandRequested = true;
            SendCommand("CmdPhase20Smoke", BuildClientPayload());
        }
    }

    public override void OnCommand(string commandName, string payloadJson, DedicatedPlayerSession senderSession)
    {
        serverCommandCount++;
        Log("OnCommand | command=" + SafeText(commandName) +
            " | serverCommandCount=" + serverCommandCount +
            " | senderUserId=" + (senderSession != null ? SafeText(senderSession.userId) : string.Empty) +
            " | payload=" + SafeText(payloadJson));

        if (!string.Equals(commandName, "CmdPhase20Smoke", System.StringComparison.Ordinal)) return;

        string clientRpcPayload = "{\"source\":\"server\",\"rpc\":\"RpcPhase20Smoke\",\"serverCommandCount\":" + serverCommandCount + "}";
        string targetRpcPayload = "{\"source\":\"server\",\"rpc\":\"TargetPhase20Smoke\",\"serverCommandCount\":" + serverCommandCount + "}";

        SendClientRpc("RpcPhase20Smoke", clientRpcPayload);

        if (senderSession != null && !string.IsNullOrWhiteSpace(senderSession.connectionId))
        {
            SendTargetRpc(senderSession.connectionId, "TargetPhase20Smoke", targetRpcPayload);
        }
    }

    public override void OnClientRpc(string rpcName, string payloadJson)
    {
        clientRpcCount++;
        Log("OnClientRpc | rpc=" + SafeText(rpcName) +
            " | clientRpcCount=" + clientRpcCount +
            " | payload=" + SafeText(payloadJson));
    }

    public override void OnTargetRpc(string rpcName, string payloadJson)
    {
        targetRpcCount++;
        Log("OnTargetRpc | rpc=" + SafeText(rpcName) +
            " | targetRpcCount=" + targetRpcCount +
            " | payload=" + SafeText(payloadJson));
    }

    public override void OnNetworkDespawn()
    {
        Log("OnNetworkDespawn");
    }

    private string BuildClientPayload()
    {
        return "{\"source\":\"client\",\"label\":\"" + probeLabel + "\",\"playerId\":\"" + SafeText(MetaverseNetworkClient.playerId) + "\"}";
    }

    private void Log(string message)
    {
        if (!logMessages) return;
        Debug.Log("[MetaverseNetworkRpcSmokeProbe] " + message +
                  " | label=" + probeLabel +
                  " | netId=" + netId +
                  " | isServer=" + BoolText(isServer) +
                  " | isClient=" + BoolText(isClient) +
                  " | hasAuthority=" + BoolText(hasAuthority));
    }

    private string SafeText(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Replace("\"", "'");
    }

    private string BoolText(bool value)
    {
        return value ? "True" : "False";
    }
}
