using System;

[Serializable]
public class MetaverseSpawnSnapshotPayload
{
    public string type;
    public string roomId;
    public string mirrorRoute;
    public long sequence;
    public long createdAtUnixMs;
    public MetaverseSpawnPayload[] spawns;

    public int Count => spawns != null ? spawns.Length : 0;
    public bool HasSpawns => Count > 0;

    public void NormalizeDefaults(string defaultType = "spawn_snapshot")
    {
        type = SafeReason(type, defaultType);
        roomId = SafeTrim(roomId);
        mirrorRoute = SafeReason(mirrorRoute, "NetworkServer.SpawnSnapshot");
        if (createdAtUnixMs <= 0) createdAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (spawns == null) spawns = Array.Empty<MetaverseSpawnPayload>();

        for (int i = 0; i < spawns.Length; i++)
        {
            if (spawns[i] == null) continue;
            if (!string.IsNullOrWhiteSpace(roomId) && string.IsNullOrWhiteSpace(spawns[i].roomId)) spawns[i].roomId = roomId;
            if (string.IsNullOrWhiteSpace(spawns[i].mirrorRoute)) spawns[i].mirrorRoute = mirrorRoute;
            spawns[i].NormalizeDefaults("network_server_spawn_snapshot");
        }
    }

    public string GetDebugSummary()
    {
        return "type=" + SafeTrim(type) +
               " | roomId=" + SafeTrim(roomId) +
               " | mirrorRoute=" + SafeTrim(mirrorRoute) +
               " | sequence=" + sequence +
               " | count=" + Count;
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
