using System;

[Serializable]
public class MetaverseDespawnPayload
{
    public int netId;
    public string reason;
    public string roomId;
    public string mirrorRoute;
    public long sequence;
    public long despawnedAtUnixMs;
    public bool destroy;

    public bool HasValidNetId => netId > 0;
    public bool IsDestroyRoute => destroy || string.Equals(SafeTrim(reason), "network_server_destroy", StringComparison.Ordinal) || string.Equals(SafeTrim(mirrorRoute), "NetworkServer.Destroy", StringComparison.Ordinal);
    public bool IsDespawnRoute => HasValidNetId && !IsDestroyRoute;

    public void NormalizeDefaults(string defaultReason = "network_server_despawn")
    {
        reason = SafeReason(reason, defaultReason);
        roomId = SafeTrim(roomId);
        mirrorRoute = SafeReason(mirrorRoute, IsDestroyRoute ? "NetworkServer.Destroy" : "NetworkServer.Despawn");
        destroy = IsDestroyRoute;
        if (despawnedAtUnixMs <= 0) despawnedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public MetaverseDespawnPayload Clone()
    {
        return new MetaverseDespawnPayload
        {
            netId = netId,
            reason = reason,
            roomId = roomId,
            mirrorRoute = mirrorRoute,
            sequence = sequence,
            despawnedAtUnixMs = despawnedAtUnixMs,
            destroy = destroy
        };
    }

    public string GetDebugSummary()
    {
        return "netId=" + netId +
               " | reason=" + SafeTrim(reason) +
               " | roomId=" + SafeTrim(roomId) +
               " | mirrorRoute=" + SafeTrim(mirrorRoute) +
               " | sequence=" + sequence +
               " | destroy=" + destroy;
    }

    private static string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string SafeReason(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
