using System;
using UnityEngine;

[Serializable]
public class MetaverseNetworkTransformPayload
{
    public string type;
    public int netId;
    public string prefabId;
    public string roomId;
    public string mirrorRoute;
    public string rejectReason;
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 scale;
    public long sequence;
    public long serverTimeUnixMs;

    public void NormalizeDefaults()
    {
        type = string.IsNullOrWhiteSpace(type) ? MetaverseDedicatedMessageTypes.NetworkTransform : type.Trim();
        prefabId = SafeTrim(prefabId);
        roomId = SafeTrim(roomId);
        mirrorRoute = string.IsNullOrWhiteSpace(mirrorRoute) ? MetaverseDedicatedMessageTypes.MirrorRouteSyncTransform : mirrorRoute.Trim();
        rejectReason = SafeTrim(rejectReason);
        if (scale == Vector3.zero) scale = Vector3.one;
    }

    public bool HasValidTransformTarget()
    {
        return netId > 0;
    }

    public string GetDebugSummary()
    {
        NormalizeDefaults();
        return "type=" + type +
               " | mirrorRoute=" + mirrorRoute +
               " | netId=" + netId +
               " | prefabId=" + prefabId +
               " | roomId=" + roomId +
               " | sequence=" + sequence +
               " | position=" + position.ToString("F3") +
               " | rejectReason=" + rejectReason;
    }

    private static string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
