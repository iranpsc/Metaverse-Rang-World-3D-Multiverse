using Network_A.GameServer.Players;
using UnityEngine;

public class MetaverseNetworkRpcSmokeProbe : MetaverseNetworkBehaviour
{
    private const string DefaultCommandName = "CmdPhase33MirrorLikeSmoke";
    private const string LegacyCommandName = "CmdPhase20Smoke";
    private const string DefaultClientRpcName = "RpcPhase33MirrorLikeSmoke";
    private const string LegacyClientRpcName = "RpcPhase20Smoke";
    private const string DefaultTargetRpcName = "TargetRpcPhase33MirrorLikeSmoke";
    private const string LegacyTargetRpcName = "TargetPhase20Smoke";

    [Header("Mirror-Like API")]
    [SerializeField] private string commandName = DefaultCommandName;
    [SerializeField] private string clientRpcName = DefaultClientRpcName;
    [SerializeField] private string targetRpcName = DefaultTargetRpcName;
    [SerializeField] private bool useOwnerCommandWhenLocalOwner = true;
    [SerializeField] private bool acceptLegacyPhase20Command = true;

    [Header("Debug")]
    [SerializeField] private bool logMessages = true;
    [SerializeField] private string probeLabel = "phase33A_mirror_like_rpc_probe";

    private bool commandRequested;
    private int serverCommandCount;
    private int clientRpcCount;
    private int targetRpcCount;
    private string lastCommandSendApi = string.Empty;
    private string lastCommandSendResult = string.Empty;
    private string lastServerCommandName = string.Empty;
    private string lastClientRpcName = string.Empty;
    private string lastTargetRpcName = string.Empty;

    public override void OnNetworkSpawn()
    {
        Log("OnNetworkSpawn");

        if (isClient && !commandRequested)
        {
            commandRequested = true;
            TrySendMirrorLikeCommandFromClient();
        }
    }

    public override void OnCommand(string commandName, string payloadJson, DedicatedPlayerSession senderSession)
    {
        serverCommandCount++;
        lastServerCommandName = SafeText(commandName);

        Log("OnCommand | mirrorRoute=Cmd/Command" +
            " | command=" + SafeText(commandName) +
            " | serverCommandCount=" + serverCommandCount +
            " | senderConnectionId=" + (senderSession != null ? SafeText(senderSession.connectionId) : string.Empty) +
            " | senderUserId=" + (senderSession != null ? SafeText(senderSession.userId) : string.Empty) +
            " | senderPlayerId=" + (senderSession != null ? SafeText(senderSession.playerId) : string.Empty) +
            " | payload=" + SafeText(payloadJson));

        if (!IsAcceptedCommandName(commandName)) return;

        string resolvedClientRpcName = GetSafeName(clientRpcName, DefaultClientRpcName);
        string resolvedTargetRpcName = GetSafeName(targetRpcName, DefaultTargetRpcName);
        string clientRpcPayload = BuildServerRpcPayload("Rpc", resolvedClientRpcName, senderSession);
        string targetRpcPayload = BuildServerRpcPayload("TargetRpc", resolvedTargetRpcName, senderSession);

        bool rpcSent = Rpc(resolvedClientRpcName, clientRpcPayload);
        Log("Rpc sent | mirrorRoute=ClientRpc/Rpc | rpc=" + resolvedClientRpcName + " | sent=" + BoolText(rpcSent));

        if (senderSession != null && !string.IsNullOrWhiteSpace(senderSession.connectionId))
        {
            bool targetSent = TargetRpc(senderSession.connectionId, resolvedTargetRpcName, targetRpcPayload);
            Log("TargetRpc sent | mirrorRoute=TargetRpc | targetConnectionId=" + SafeText(senderSession.connectionId) + " | rpc=" + resolvedTargetRpcName + " | sent=" + BoolText(targetSent));
        }
        else
        {
            Log("TargetRpc skipped | reason=sender_session_missing_or_connection_id_empty");
        }
    }

    public override void OnClientRpc(string rpcName, string payloadJson)
    {
        clientRpcCount++;
        lastClientRpcName = SafeText(rpcName);

        Log("OnClientRpc | mirrorRoute=ClientRpc/Rpc" +
            " | rpc=" + SafeText(rpcName) +
            " | clientRpcCount=" + clientRpcCount +
            " | payload=" + SafeText(payloadJson));
    }

    public override void OnTargetRpc(string rpcName, string payloadJson)
    {
        targetRpcCount++;
        lastTargetRpcName = SafeText(rpcName);

        Log("OnTargetRpc | mirrorRoute=TargetRpc" +
            " | rpc=" + SafeText(rpcName) +
            " | targetRpcCount=" + targetRpcCount +
            " | payload=" + SafeText(payloadJson));
    }

    public override void OnNetworkDespawn()
    {
        Log("OnNetworkDespawn" +
            " | serverCommandCount=" + serverCommandCount +
            " | clientRpcCount=" + clientRpcCount +
            " | targetRpcCount=" + targetRpcCount +
            " | lastCommandApi=" + SafeText(lastCommandSendApi) +
            " | lastCommandResult=" + SafeText(lastCommandSendResult));
    }

    public string GetSmokeDebugSummary()
    {
        return "label=" + SafeText(probeLabel) +
               " | netId=" + netId +
               " | commandRequested=" + BoolText(commandRequested) +
               " | serverCommandCount=" + serverCommandCount +
               " | clientRpcCount=" + clientRpcCount +
               " | targetRpcCount=" + targetRpcCount +
               " | lastCommand=" + SafeText(lastServerCommandName) +
               " | lastRpc=" + SafeText(lastClientRpcName) +
               " | lastTargetRpc=" + SafeText(lastTargetRpcName);
    }

    private void TrySendMirrorLikeCommandFromClient()
    {
        string resolvedCommandName = GetSafeName(commandName, DefaultCommandName);
        string payload = BuildClientPayload();
        bool shouldUseOwnerCommand = useOwnerCommandWhenLocalOwner && isLocalOwner;

        if (shouldUseOwnerCommand)
        {
            lastCommandSendApi = "OwnerCmd";
            bool ownerSent = OwnerCmd(resolvedCommandName, payload);
            lastCommandSendResult = BoolText(ownerSent);
            Log("OwnerCmd requested | mirrorRoute=Command(requiresAuthority=true) | command=" + resolvedCommandName + " | sent=" + BoolText(ownerSent));
            if (ownerSent) return;
        }

        lastCommandSendApi = "Cmd";
        bool sent = Cmd(resolvedCommandName, payload);
        lastCommandSendResult = BoolText(sent);
        Log("Cmd requested | mirrorRoute=Command(requiresAuthority=false/server-owned-compatible) | command=" + resolvedCommandName + " | sent=" + BoolText(sent));
    }

    private bool IsAcceptedCommandName(string value)
    {
        string safeValue = SafeText(value);
        if (string.Equals(safeValue, GetSafeName(commandName, DefaultCommandName), System.StringComparison.Ordinal)) return true;
        if (acceptLegacyPhase20Command && string.Equals(safeValue, LegacyCommandName, System.StringComparison.Ordinal)) return true;
        return false;
    }

    private string BuildClientPayload()
    {
        return "{\"source\":\"client\",\"phase\":\"33A\",\"api\":\"Cmd\",\"label\":\"" + SafeText(probeLabel) +
               "\",\"playerId\":\"" + SafeText(MetaverseNetworkClient.playerId) +
               "\",\"userId\":\"" + SafeText(MetaverseNetworkClient.userId) +
               "\",\"connectionId\":\"" + SafeText(MetaverseNetworkClient.connectionId) + "\"}";
    }

    private string BuildServerRpcPayload(string apiName, string rpcName, DedicatedPlayerSession senderSession)
    {
        return "{\"source\":\"server\",\"phase\":\"33A\",\"api\":\"" + SafeText(apiName) +
               "\",\"rpc\":\"" + SafeText(rpcName) +
               "\",\"serverCommandCount\":" + serverCommandCount +
               ",\"senderConnectionId\":\"" + (senderSession != null ? SafeText(senderSession.connectionId) : string.Empty) +
               "\",\"senderUserId\":\"" + (senderSession != null ? SafeText(senderSession.userId) : string.Empty) +
               "\",\"senderPlayerId\":\"" + (senderSession != null ? SafeText(senderSession.playerId) : string.Empty) + "\"}";
    }

    private string GetSafeName(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private void Log(string message)
    {
        if (!logMessages) return;
        Debug.Log("[MetaverseNetworkRpcSmokeProbe] " + message +
                  " | label=" + SafeText(probeLabel) +
                  " | netId=" + netId +
                  " | prefabId=" + SafeText(prefabId) +
                  " | ownerConnectionId=" + SafeText(ownerConnectionId) +
                  " | ownerUserId=" + SafeText(ownerUserId) +
                  " | ownerPlayerId=" + SafeText(ownerPlayerId) +
                  " | isServer=" + BoolText(isServer) +
                  " | isClient=" + BoolText(isClient) +
                  " | isLocalOwner=" + BoolText(isLocalOwner) +
                  " | hasAuthority=" + BoolText(hasAuthority));
    }

    private string SafeText(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Replace("\\", "/").Replace("\"", "'");
    }

    private string BoolText(bool value)
    {
        return value ? "True" : "False";
    }
}
