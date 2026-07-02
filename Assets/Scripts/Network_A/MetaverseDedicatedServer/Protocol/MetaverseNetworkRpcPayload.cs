using System;

[Serializable]
public class MetaverseNetworkRpcPayload
{
    public string type;
    public int netId;
    public string prefabId;
    public string behaviourName;
    public string methodName;
    public string commandName;
    public string rpcName;
    public string payloadJson;
    public string roomId;

    public string senderConnectionId;
    public string senderUserId;
    public string senderPlayerId;

    public string targetConnectionId;
    public string targetUserId;
    public string targetPlayerId;

    public string mirrorRoute;
    public string rejectReason;
    public long sequence;
    public long clientTimeUnixMs;
    public long serverTimeUnixMs;

    public void NormalizeDefaults()
    {
        type = SafeTrim(type);
        prefabId = SafeTrim(prefabId);
        behaviourName = SafeTrim(behaviourName);
        methodName = SafeTrim(ReadMethodName());
        commandName = SafeTrim(string.IsNullOrWhiteSpace(commandName) ? methodName : commandName);
        rpcName = SafeTrim(string.IsNullOrWhiteSpace(rpcName) ? methodName : rpcName);
        payloadJson = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson.Trim();
        roomId = SafeTrim(roomId);
        senderConnectionId = SafeTrim(senderConnectionId);
        senderUserId = SafeTrim(senderUserId);
        senderPlayerId = SafeTrim(senderPlayerId);
        targetConnectionId = SafeTrim(targetConnectionId);
        targetUserId = SafeTrim(targetUserId);
        targetPlayerId = SafeTrim(targetPlayerId);
        mirrorRoute = string.IsNullOrWhiteSpace(mirrorRoute) ? MetaverseDedicatedMessageTypes.ReadMirrorLikeRoute(type) : mirrorRoute.Trim();
        rejectReason = SafeTrim(rejectReason);
    }

    public string ReadMethodName()
    {
        if (!string.IsNullOrWhiteSpace(methodName)) return methodName.Trim();
        if (!string.IsNullOrWhiteSpace(commandName)) return commandName.Trim();
        if (!string.IsNullOrWhiteSpace(rpcName)) return rpcName.Trim();
        return string.Empty;
    }

    public void SetMethodName(string value)
    {
        string safeValue = SafeTrim(value);
        methodName = safeValue;
        if (type == MetaverseDedicatedMessageTypes.Command) commandName = safeValue;
        if (type == MetaverseDedicatedMessageTypes.ClientRpc || type == MetaverseDedicatedMessageTypes.TargetRpc) rpcName = safeValue;
    }

    public bool IsTargetedToConnection(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(targetConnectionId)) return false;
        if (string.IsNullOrWhiteSpace(connectionId)) return false;
        return string.Equals(targetConnectionId.Trim(), connectionId.Trim(), StringComparison.Ordinal);
    }

    public bool IsTargetedToUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(targetUserId)) return false;
        if (string.IsNullOrWhiteSpace(userId)) return false;
        return string.Equals(targetUserId.Trim(), userId.Trim(), StringComparison.Ordinal);
    }

    public bool IsTargetedToPlayer(string playerId)
    {
        if (string.IsNullOrWhiteSpace(targetPlayerId)) return false;
        if (string.IsNullOrWhiteSpace(playerId)) return false;
        return string.Equals(targetPlayerId.Trim(), playerId.Trim(), StringComparison.Ordinal);
    }

    public bool HasValidNetworkTarget()
    {
        return netId > 0 && !string.IsNullOrWhiteSpace(ReadMethodName());
    }

    public string GetDebugSummary()
    {
        NormalizeDefaults();
        return "type=" + type +
               " | mirrorRoute=" + mirrorRoute +
               " | netId=" + netId +
               " | prefabId=" + prefabId +
               " | method=" + methodName +
               " | roomId=" + roomId +
               " | sequence=" + sequence +
               " | senderConnectionId=" + senderConnectionId +
               " | targetConnectionId=" + targetConnectionId +
               " | rejectReason=" + rejectReason;
    }

    private static string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
