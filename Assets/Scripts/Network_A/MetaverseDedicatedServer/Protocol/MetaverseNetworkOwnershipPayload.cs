using System;

[Serializable]
public class MetaverseNetworkOwnershipPayload
{
    public string type;
    public int netId;
    public string prefabId;
    public string roomId;
    public string ownerConnectionId;
    public string ownerUserId;
    public string ownerPlayerId;
    public string previousOwnerConnectionId;
    public string previousOwnerUserId;
    public string previousOwnerPlayerId;
    public bool serverOwned;
    public string reason;
    public string mirrorRoute;
    public string rejectReason;
    public long version;
    public long serverTimeUnixMs;

    public void NormalizeDefaults()
    {
        type = string.IsNullOrWhiteSpace(type) ? MetaverseDedicatedMessageTypes.Ownership : type.Trim();
        prefabId = SafeTrim(prefabId);
        roomId = SafeTrim(roomId);
        ownerConnectionId = SafeTrim(ownerConnectionId);
        ownerUserId = SafeTrim(ownerUserId);
        ownerPlayerId = SafeTrim(ownerPlayerId);
        previousOwnerConnectionId = SafeTrim(previousOwnerConnectionId);
        previousOwnerUserId = SafeTrim(previousOwnerUserId);
        previousOwnerPlayerId = SafeTrim(previousOwnerPlayerId);
        reason = SafeTrim(reason);
        rejectReason = SafeTrim(rejectReason);
        mirrorRoute = string.IsNullOrWhiteSpace(mirrorRoute) ? ReadMirrorRoute() : mirrorRoute.Trim();
    }

    public bool HasOwner()
    {
        return !string.IsNullOrWhiteSpace(ownerConnectionId) || !string.IsNullOrWhiteSpace(ownerUserId) || !string.IsNullOrWhiteSpace(ownerPlayerId);
    }

    public bool IsOwnedByAny(string connectionId, string userId, string playerId)
    {
        if (!string.IsNullOrWhiteSpace(ownerConnectionId) && !string.IsNullOrWhiteSpace(connectionId) && string.Equals(ownerConnectionId.Trim(), connectionId.Trim(), StringComparison.Ordinal)) return true;
        if (!string.IsNullOrWhiteSpace(ownerUserId) && !string.IsNullOrWhiteSpace(userId) && string.Equals(ownerUserId.Trim(), userId.Trim(), StringComparison.Ordinal)) return true;
        if (!string.IsNullOrWhiteSpace(ownerPlayerId) && !string.IsNullOrWhiteSpace(playerId) && string.Equals(ownerPlayerId.Trim(), playerId.Trim(), StringComparison.Ordinal)) return true;
        return false;
    }

    public string ReadMirrorRoute()
    {
        if (serverOwned || !HasOwner()) return MetaverseDedicatedMessageTypes.MirrorRouteRemoveAuthority;
        return MetaverseDedicatedMessageTypes.MirrorRouteAssignAuthority;
    }

    public string GetDebugSummary()
    {
        NormalizeDefaults();
        return "type=" + type +
               " | mirrorRoute=" + mirrorRoute +
               " | netId=" + netId +
               " | prefabId=" + prefabId +
               " | ownerConnectionId=" + ownerConnectionId +
               " | ownerUserId=" + ownerUserId +
               " | ownerPlayerId=" + ownerPlayerId +
               " | serverOwned=" + serverOwned +
               " | version=" + version +
               " | reason=" + reason +
               " | rejectReason=" + rejectReason;
    }

    private static string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
