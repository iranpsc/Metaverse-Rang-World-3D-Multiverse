using System;

[Serializable]
public class MetaverseSpawnEnvelope
{
    public int v = 1;
    public string type;
    public string messageId;
    public long ts;
    public string room;
    public string mirrorRoute;
    public bool requiresAck;
    public string replyTo;
    public MetaverseSpawnPayload spawn;
    public MetaverseDespawnPayload despawn;
    public MetaverseSpawnPayload[] spawns;

    public bool HasSpawn => spawn != null;
    public bool HasDespawn => despawn != null;
    public bool HasSnapshot => spawns != null && spawns.Length > 0;

    public void NormalizeDefaults(string fallbackType = "")
    {
        if (v <= 0) v = 1;
        type = SafeReason(type, fallbackType);
        messageId = SafeReason(messageId, Guid.NewGuid().ToString("N"));
        if (ts <= 0) ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        room = SafeTrim(room);
        mirrorRoute = SafeTrim(mirrorRoute);

        if (spawn != null)
        {
            if (!string.IsNullOrWhiteSpace(room) && string.IsNullOrWhiteSpace(spawn.roomId)) spawn.roomId = room;
            if (!string.IsNullOrWhiteSpace(mirrorRoute) && string.IsNullOrWhiteSpace(spawn.mirrorRoute)) spawn.mirrorRoute = mirrorRoute;
            spawn.NormalizeDefaults("network_server_spawn");
        }

        if (despawn != null)
        {
            if (!string.IsNullOrWhiteSpace(room) && string.IsNullOrWhiteSpace(despawn.roomId)) despawn.roomId = room;
            if (!string.IsNullOrWhiteSpace(mirrorRoute) && string.IsNullOrWhiteSpace(despawn.mirrorRoute)) despawn.mirrorRoute = mirrorRoute;
            despawn.NormalizeDefaults("network_server_despawn");
        }

        if (spawns == null) return;
        for (int i = 0; i < spawns.Length; i++)
        {
            if (spawns[i] == null) continue;
            if (!string.IsNullOrWhiteSpace(room) && string.IsNullOrWhiteSpace(spawns[i].roomId)) spawns[i].roomId = room;
            if (!string.IsNullOrWhiteSpace(mirrorRoute) && string.IsNullOrWhiteSpace(spawns[i].mirrorRoute)) spawns[i].mirrorRoute = mirrorRoute;
            spawns[i].NormalizeDefaults("network_server_spawn_snapshot");
        }
    }

    public string GetDebugSummary()
    {
        return "type=" + SafeTrim(type) +
               " | messageId=" + SafeTrim(messageId) +
               " | room=" + SafeTrim(room) +
               " | mirrorRoute=" + SafeTrim(mirrorRoute) +
               " | hasSpawn=" + HasSpawn +
               " | hasDespawn=" + HasDespawn +
               " | spawnCount=" + (spawns != null ? spawns.Length : 0);
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
