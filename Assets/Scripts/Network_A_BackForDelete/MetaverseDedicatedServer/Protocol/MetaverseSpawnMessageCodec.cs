using System;
using Network_A.Realtime.Protocol;
using UnityEngine;

public static class MetaverseSpawnMessageCodec
{
    public const string MirrorSpawnRoute = "NetworkServer.Spawn";
    public const string MirrorSpawnPrefabRoute = "NetworkServer.SpawnPrefab";
    public const string MirrorDespawnRoute = "NetworkServer.Despawn";
    public const string MirrorDestroyRoute = "NetworkServer.Destroy";
    public const string MirrorSnapshotRoute = "NetworkServer.SpawnSnapshot";

    public static MetaverseSpawnEnvelope CreateSpawn(MetaverseSpawnPayload payload)
    {
        if (payload != null) payload.NormalizeDefaults("network_server_spawn");
        return new MetaverseSpawnEnvelope
        {
            v = 1,
            type = MetaverseDedicatedMessageTypes.Spawn,
            messageId = Guid.NewGuid().ToString("N"),
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            spawn = payload
        };
    }

    public static MetaverseSpawnEnvelope CreateDespawn(int netId, string reason)
    {
        return new MetaverseSpawnEnvelope
        {
            v = 1,
            type = MetaverseDedicatedMessageTypes.Despawn,
            messageId = Guid.NewGuid().ToString("N"),
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            despawn = new MetaverseDespawnPayload { netId = netId, reason = SafeTrim(reason) }
        };
    }

    public static MetaverseSpawnEnvelope CreateDestroy(int netId, string reason)
    {
        return CreateDespawn(netId, SafeReason(reason, "network_server_destroy"));
    }

    public static MetaverseSpawnEnvelope CreateSpawnSnapshot(MetaverseSpawnPayload[] payloads)
    {
        NormalizePayloadArray(payloads, MirrorSnapshotRoute);
        return new MetaverseSpawnEnvelope
        {
            v = 1,
            type = MetaverseDedicatedMessageTypes.LegacySpawnSnapshot,
            messageId = Guid.NewGuid().ToString("N"),
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            spawns = payloads ?? Array.Empty<MetaverseSpawnPayload>()
        };
    }

    public static string CreateSpawnEnvelopeJson(MetaverseSpawnPayload payload, string roomId = "")
    {
        if (payload == null) return string.Empty;
        payload.roomId = string.IsNullOrWhiteSpace(roomId) ? payload.roomId : roomId.Trim();
        payload.NormalizeDefaults("network_server_spawn");
        RealtimeEnvelope envelope = RealtimeEnvelope.Create(
            RealtimeChannels.Game,
            RealtimeMessageTypes.Spawn,
            JsonUtility.ToJson(payload),
            SafeTrim(roomId),
            false);
        return envelope.ToJson();
    }

    public static string CreateSpawnPrefabEnvelopeJson(MetaverseSpawnPayload payload, string roomId = "")
    {
        if (payload == null) return string.Empty;
        payload.mirrorRoute = MirrorSpawnPrefabRoute;
        return CreateSpawnEnvelopeJson(payload, roomId);
    }

    public static string CreateDespawnEnvelopeJson(int netId, string reason, string roomId = "")
    {
        MetaverseDespawnPayload payload = new MetaverseDespawnPayload
        {
            netId = netId,
            reason = SafeReason(reason, "network_server_despawn")
        };

        RealtimeEnvelope envelope = RealtimeEnvelope.Create(
            RealtimeChannels.Game,
            RealtimeMessageTypes.Despawn,
            JsonUtility.ToJson(payload),
            SafeTrim(roomId),
            false);
        return envelope.ToJson();
    }

    public static string CreateDestroyEnvelopeJson(int netId, string reason, string roomId = "")
    {
        return CreateDespawnEnvelopeJson(netId, SafeReason(reason, "network_server_destroy"), roomId);
    }

    public static string CreateSpawnSnapshotEnvelopeJson(MetaverseSpawnPayload[] payloads, string roomId = "")
    {
        NormalizePayloadArray(payloads, MirrorSnapshotRoute);
        MetaverseSpawnSnapshotPayload payload = new MetaverseSpawnSnapshotPayload
        {
            type = MetaverseDedicatedMessageTypes.LegacySpawnSnapshot,
            spawns = payloads ?? Array.Empty<MetaverseSpawnPayload>()
        };

        RealtimeEnvelope envelope = RealtimeEnvelope.Create(
            RealtimeChannels.Game,
            RealtimeMessageTypes.Snapshot,
            JsonUtility.ToJson(payload),
            SafeTrim(roomId),
            false);
        return envelope.ToJson();
    }

    public static string ToJson(MetaverseSpawnEnvelope envelope)
    {
        return envelope == null ? string.Empty : JsonUtility.ToJson(envelope, false);
    }

    public static bool TryFromJson(string rawJson, out MetaverseSpawnEnvelope envelope)
    {
        envelope = null;
        if (string.IsNullOrWhiteSpace(rawJson)) return false;

        try
        {
            envelope = JsonUtility.FromJson<MetaverseSpawnEnvelope>(rawJson);
            return envelope != null && !string.IsNullOrWhiteSpace(envelope.type);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[MetaverseSpawnMessageCodec] Json parse failed | error=" + ex.Message);
            return false;
        }
    }

    public static bool TryReadMessage(
        string rawJson,
        out string messageType,
        out MetaverseSpawnPayload spawnPayload,
        out MetaverseDespawnPayload despawnPayload,
        out MetaverseSpawnPayload[] snapshotPayloads)
    {
        messageType = string.Empty;
        spawnPayload = null;
        despawnPayload = null;
        snapshotPayloads = null;

        if (string.IsNullOrWhiteSpace(rawJson)) return false;

        if (TryReadRealtimeEnvelopeMessage(rawJson, out messageType, out spawnPayload, out despawnPayload, out snapshotPayloads)) return true;
        if (!TryFromJson(rawJson, out MetaverseSpawnEnvelope legacyEnvelope) || legacyEnvelope == null) return false;

        messageType = NormalizeMessageType(legacyEnvelope.type);
        spawnPayload = legacyEnvelope.spawn;
        despawnPayload = legacyEnvelope.despawn;
        snapshotPayloads = legacyEnvelope.spawns;
        if (spawnPayload != null) spawnPayload.NormalizeDefaults(MirrorSpawnRoute);
        NormalizePayloadArray(snapshotPayloads, MirrorSnapshotRoute);
        return !string.IsNullOrWhiteSpace(messageType);
    }

    public static bool TryReadSpawn(string rawJson, out MetaverseSpawnPayload payload)
    {
        payload = null;
        bool ok = TryReadMessage(rawJson, out string messageType, out MetaverseSpawnPayload spawnPayload, out _, out _);
        if (!ok || !IsSpawnMessage(messageType) || spawnPayload == null) return false;
        payload = spawnPayload;
        return payload.IsValidForSpawn();
    }

    public static bool TryReadDespawn(string rawJson, out MetaverseDespawnPayload payload)
    {
        payload = null;
        bool ok = TryReadMessage(rawJson, out string messageType, out _, out MetaverseDespawnPayload despawnPayload, out _);
        if (!ok || !IsDespawnMessage(messageType) || despawnPayload == null || despawnPayload.netId <= 0) return false;
        payload = despawnPayload;
        return true;
    }

    public static bool TryReadSnapshot(string rawJson, out MetaverseSpawnPayload[] payloads)
    {
        payloads = Array.Empty<MetaverseSpawnPayload>();
        bool ok = TryReadMessage(rawJson, out string messageType, out _, out _, out MetaverseSpawnPayload[] snapshotPayloads);
        if (!ok || !IsSnapshotMessage(messageType)) return false;
        payloads = snapshotPayloads ?? Array.Empty<MetaverseSpawnPayload>();
        NormalizePayloadArray(payloads, MirrorSnapshotRoute);
        return true;
    }

    public static bool IsSpawnMessage(string messageType)
    {
        return string.Equals(NormalizeMessageType(messageType), RealtimeMessageTypes.Spawn, StringComparison.Ordinal);
    }

    public static bool IsDespawnMessage(string messageType)
    {
        return string.Equals(NormalizeMessageType(messageType), RealtimeMessageTypes.Despawn, StringComparison.Ordinal);
    }

    public static bool IsSnapshotMessage(string messageType)
    {
        string normalized = NormalizeMessageType(messageType);
        return string.Equals(normalized, RealtimeMessageTypes.Snapshot, StringComparison.Ordinal) || string.Equals(normalized, MetaverseDedicatedMessageTypes.LegacySpawnSnapshot, StringComparison.Ordinal);
    }

    public static bool IsRealtimeSpawnEnvelope(string rawJson)
    {
        return TryReadRealtimeEnvelope(rawJson, out RealtimeEnvelope envelope) && envelope.ch == RealtimeChannels.Game && envelope.t == RealtimeMessageTypes.Spawn;
    }

    public static bool IsRealtimeDespawnEnvelope(string rawJson)
    {
        return TryReadRealtimeEnvelope(rawJson, out RealtimeEnvelope envelope) && envelope.ch == RealtimeChannels.Game && envelope.t == RealtimeMessageTypes.Despawn;
    }

    public static bool IsRealtimeSpawnSnapshotEnvelope(string rawJson)
    {
        return TryReadRealtimeEnvelope(rawJson, out RealtimeEnvelope envelope) && envelope.ch == RealtimeChannels.Game && envelope.t == RealtimeMessageTypes.Snapshot;
    }

    public static string ReadMessageFormat(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return "empty";
        if (TryReadRealtimeEnvelope(rawJson, out RealtimeEnvelope _)) return "envelope";
        if (TryFromJson(rawJson, out MetaverseSpawnEnvelope _)) return "legacy";
        return "invalid";
    }

    public static string ReadRouteForLog(string rawJson)
    {
        if (TryReadRealtimeEnvelope(rawJson, out RealtimeEnvelope envelope)) return SafeRoutePart(envelope.ch) + "/" + SafeRoutePart(envelope.t);
        if (TryFromJson(rawJson, out MetaverseSpawnEnvelope legacyEnvelope) && legacyEnvelope != null) return "legacy/" + SafeRoutePart(legacyEnvelope.type);
        return "invalid/unknown";
    }

    public static string ReadMirrorLikeRouteForLog(string rawJson)
    {
        if (TryReadSpawn(rawJson, out MetaverseSpawnPayload spawnPayload)) return SafeRoutePart(spawnPayload.mirrorRoute);
        if (TryReadDespawn(rawJson, out MetaverseDespawnPayload despawnPayload)) return string.Equals(despawnPayload.reason, "network_server_destroy", StringComparison.Ordinal) ? MirrorDestroyRoute : MirrorDespawnRoute;
        if (TryReadSnapshot(rawJson, out _)) return MirrorSnapshotRoute;
        return "unknown";
    }

    public static string NormalizeMessageType(string messageType)
    {
        if (string.IsNullOrWhiteSpace(messageType)) return string.Empty;
        string value = messageType.Trim();
        if (string.Equals(value, MetaverseDedicatedMessageTypes.Spawn, StringComparison.Ordinal)) return RealtimeMessageTypes.Spawn;
        if (string.Equals(value, MetaverseDedicatedMessageTypes.Despawn, StringComparison.Ordinal)) return RealtimeMessageTypes.Despawn;
        if (string.Equals(value, MetaverseDedicatedMessageTypes.SpawnSnapshot, StringComparison.Ordinal)) return RealtimeMessageTypes.Snapshot;
        return value;
    }

    private static bool TryReadRealtimeEnvelopeMessage(
        string rawJson,
        out string messageType,
        out MetaverseSpawnPayload spawnPayload,
        out MetaverseDespawnPayload despawnPayload,
        out MetaverseSpawnPayload[] snapshotPayloads)
    {
        messageType = string.Empty;
        spawnPayload = null;
        despawnPayload = null;
        snapshotPayloads = null;

        if (!TryReadRealtimeEnvelope(rawJson, out RealtimeEnvelope envelope)) return false;
        if (envelope.ch != RealtimeChannels.Game) return false;

        messageType = NormalizeMessageType(envelope.t);

        try
        {
            if (envelope.t == RealtimeMessageTypes.Spawn)
            {
                spawnPayload = JsonUtility.FromJson<MetaverseSpawnPayload>(envelope.payloadJson);
                if (spawnPayload != null) spawnPayload.NormalizeDefaults(MirrorSpawnRoute);
                return spawnPayload != null;
            }

            if (envelope.t == RealtimeMessageTypes.Despawn)
            {
                despawnPayload = JsonUtility.FromJson<MetaverseDespawnPayload>(envelope.payloadJson);
                return despawnPayload != null;
            }

            if (envelope.t == RealtimeMessageTypes.Snapshot)
            {
                MetaverseSpawnSnapshotPayload snapshot = JsonUtility.FromJson<MetaverseSpawnSnapshotPayload>(envelope.payloadJson);
                snapshotPayloads = snapshot != null ? snapshot.spawns : Array.Empty<MetaverseSpawnPayload>();
                NormalizePayloadArray(snapshotPayloads, MirrorSnapshotRoute);
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[MetaverseSpawnMessageCodec] Realtime payload parse failed | route=" + envelope.ch + "/" + envelope.t + " | error=" + ex.Message);
            return false;
        }

        return false;
    }

    private static bool TryReadRealtimeEnvelope(string rawJson, out RealtimeEnvelope envelope)
    {
        envelope = null;
        if (string.IsNullOrWhiteSpace(rawJson)) return false;

        RealtimeEnvelope parsed = RealtimeEnvelope.FromJson(rawJson);
        if (parsed == null || !parsed.IsValidBasic()) return false;

        envelope = parsed;
        return true;
    }

    private static void NormalizePayloadArray(MetaverseSpawnPayload[] payloads, string mirrorRoute)
    {
        if (payloads == null) return;
        for (int i = 0; i < payloads.Length; i++)
        {
            if (payloads[i] == null) continue;
            if (string.IsNullOrWhiteSpace(payloads[i].mirrorRoute)) payloads[i].mirrorRoute = mirrorRoute;
            payloads[i].NormalizeDefaults("network_server_spawn_snapshot");
        }
    }

    private static string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string SafeReason(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string SafeRoutePart(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
    }
}
