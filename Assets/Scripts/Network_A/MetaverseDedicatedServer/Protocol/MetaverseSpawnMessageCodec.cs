using System;
using Network_A.Realtime.Protocol;
using UnityEngine;

public static class MetaverseSpawnMessageCodec
{
    public static MetaverseSpawnEnvelope CreateSpawn(MetaverseSpawnPayload payload)
    {
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

    public static MetaverseSpawnEnvelope CreateSpawnSnapshot(MetaverseSpawnPayload[] payloads)
    {
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
        RealtimeEnvelope envelope = RealtimeEnvelope.Create(
            RealtimeChannels.Game,
            RealtimeMessageTypes.Spawn,
            JsonUtility.ToJson(payload),
            SafeTrim(roomId),
            false);
        return envelope.ToJson();
    }

    public static string CreateDespawnEnvelopeJson(int netId, string reason, string roomId = "")
    {
        MetaverseDespawnPayload payload = new MetaverseDespawnPayload
        {
            netId = netId,
            reason = SafeTrim(reason)
        };

        RealtimeEnvelope envelope = RealtimeEnvelope.Create(
            RealtimeChannels.Game,
            RealtimeMessageTypes.Despawn,
            JsonUtility.ToJson(payload),
            SafeTrim(roomId),
            false);
        return envelope.ToJson();
    }

    public static string CreateSpawnSnapshotEnvelopeJson(MetaverseSpawnPayload[] payloads, string roomId = "")
    {
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

        if (TryReadRealtimeEnvelopeMessage(rawJson, out messageType, out spawnPayload, out despawnPayload, out snapshotPayloads))
        {
            return true;
        }

        if (!TryFromJson(rawJson, out MetaverseSpawnEnvelope legacyEnvelope) || legacyEnvelope == null)
        {
            return false;
        }

        messageType = legacyEnvelope.type;
        spawnPayload = legacyEnvelope.spawn;
        despawnPayload = legacyEnvelope.despawn;
        snapshotPayloads = legacyEnvelope.spawns;
        return !string.IsNullOrWhiteSpace(messageType);
    }

    public static bool IsRealtimeSpawnEnvelope(string rawJson)
    {
        return TryReadRealtimeEnvelope(rawJson, out RealtimeEnvelope envelope) &&
               envelope.ch == RealtimeChannels.Game &&
               envelope.t == RealtimeMessageTypes.Spawn;
    }

    public static bool IsRealtimeDespawnEnvelope(string rawJson)
    {
        return TryReadRealtimeEnvelope(rawJson, out RealtimeEnvelope envelope) &&
               envelope.ch == RealtimeChannels.Game &&
               envelope.t == RealtimeMessageTypes.Despawn;
    }

    public static bool IsRealtimeSpawnSnapshotEnvelope(string rawJson)
    {
        return TryReadRealtimeEnvelope(rawJson, out RealtimeEnvelope envelope) &&
               envelope.ch == RealtimeChannels.Game &&
               envelope.t == RealtimeMessageTypes.Snapshot;
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
        if (TryReadRealtimeEnvelope(rawJson, out RealtimeEnvelope envelope))
        {
            return SafeRoutePart(envelope.ch) + "/" + SafeRoutePart(envelope.t);
        }

        if (TryFromJson(rawJson, out MetaverseSpawnEnvelope legacyEnvelope) && legacyEnvelope != null)
        {
            return "legacy/" + SafeRoutePart(legacyEnvelope.type);
        }

        return "invalid/unknown";
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

        messageType = envelope.t;

        try
        {
            if (envelope.t == RealtimeMessageTypes.Spawn)
            {
                spawnPayload = JsonUtility.FromJson<MetaverseSpawnPayload>(envelope.payloadJson);
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
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[MetaverseSpawnMessageCodec] Realtime payload parse failed | route=" +
                             envelope.ch + "/" + envelope.t + " | error=" + ex.Message);
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

    private static string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string SafeRoutePart(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
    }
}
