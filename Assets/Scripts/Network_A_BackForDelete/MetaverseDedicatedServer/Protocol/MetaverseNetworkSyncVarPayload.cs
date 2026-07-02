using System;

[Serializable]
public class MetaverseNetworkSyncVarPayload
{
    public string type;
    public int netId;
    public string prefabId;
    public string syncKey;
    public string oldValueJson;
    public string valueJson;
    public string roomId;
    public string mirrorRoute;
    public string rejectReason;
    public long version;
    public long serverTimeUnixMs;

    public void NormalizeDefaults()
    {
        type = string.IsNullOrWhiteSpace(type) ? MetaverseDedicatedMessageTypes.SyncVar : type.Trim();
        prefabId = SafeTrim(prefabId);
        syncKey = SafeTrim(syncKey);
        oldValueJson = string.IsNullOrWhiteSpace(oldValueJson) ? "{}" : oldValueJson.Trim();
        valueJson = string.IsNullOrWhiteSpace(valueJson) ? "{}" : valueJson.Trim();
        roomId = SafeTrim(roomId);
        mirrorRoute = string.IsNullOrWhiteSpace(mirrorRoute) ? MetaverseDedicatedMessageTypes.MirrorRouteSyncVar : mirrorRoute.Trim();
        rejectReason = SafeTrim(rejectReason);
    }

    public bool HasValidSyncTarget()
    {
        return netId > 0 && !string.IsNullOrWhiteSpace(syncKey);
    }

    public string GetDebugSummary()
    {
        NormalizeDefaults();
        return "type=" + type +
               " | mirrorRoute=" + mirrorRoute +
               " | netId=" + netId +
               " | prefabId=" + prefabId +
               " | syncKey=" + syncKey +
               " | version=" + version +
               " | roomId=" + roomId +
               " | rejectReason=" + rejectReason;
    }

    private static string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
