using System;

[Serializable]
public class MetaverseNetworkPlayerInputPayload
{
    public string type;
    public int netId;
    public string prefabId;
    public string roomId;
    public string connectionId;
    public string userId;
    public string playerId;
    public string mirrorRoute;
    public string rejectReason;
    public long sequence;
    public float moveX;
    public float moveZ;
    public float deltaTime;
    public long clientTimeUnixMs;
    public long serverTimeUnixMs;

    public void NormalizeDefaults()
    {
        type = string.IsNullOrWhiteSpace(type) ? MetaverseDedicatedMessageTypes.PlayerInput : type.Trim();
        prefabId = SafeTrim(prefabId);
        roomId = SafeTrim(roomId);
        connectionId = SafeTrim(connectionId);
        userId = SafeTrim(userId);
        playerId = SafeTrim(playerId);
        mirrorRoute = string.IsNullOrWhiteSpace(mirrorRoute) ? MetaverseDedicatedMessageTypes.MirrorRouteOwnerInput : mirrorRoute.Trim();
        rejectReason = SafeTrim(rejectReason);
        if (deltaTime < 0f) deltaTime = 0f;
    }

    public bool HasValidInputTarget()
    {
        return netId > 0;
    }

    public bool IsOwnerMatch(string targetConnectionId, string targetUserId, string targetPlayerId)
    {
        if (!string.IsNullOrWhiteSpace(connectionId) && !string.IsNullOrWhiteSpace(targetConnectionId) && string.Equals(connectionId.Trim(), targetConnectionId.Trim(), StringComparison.Ordinal)) return true;
        if (!string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(targetUserId) && string.Equals(userId.Trim(), targetUserId.Trim(), StringComparison.Ordinal)) return true;
        if (!string.IsNullOrWhiteSpace(playerId) && !string.IsNullOrWhiteSpace(targetPlayerId) && string.Equals(playerId.Trim(), targetPlayerId.Trim(), StringComparison.Ordinal)) return true;
        return false;
    }

    public string GetDebugSummary()
    {
        NormalizeDefaults();
        return "type=" + type +
               " | mirrorRoute=" + mirrorRoute +
               " | netId=" + netId +
               " | roomId=" + roomId +
               " | sequence=" + sequence +
               " | moveX=" + moveX.ToString("F3") +
               " | moveZ=" + moveZ.ToString("F3") +
               " | deltaTime=" + deltaTime.ToString("F3") +
               " | connectionId=" + connectionId +
               " | userId=" + userId +
               " | playerId=" + playerId +
               " | rejectReason=" + rejectReason;
    }

    private static string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
