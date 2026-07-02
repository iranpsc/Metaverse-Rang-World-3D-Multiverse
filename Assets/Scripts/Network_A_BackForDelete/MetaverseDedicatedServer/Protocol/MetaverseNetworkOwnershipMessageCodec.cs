using System;
using Network_A.GameServer.Protocol;
using Network_A.Realtime.Protocol;
using UnityEngine;

public static class MetaverseNetworkOwnershipMessageCodec
{
    public static string CreateOwnershipEnvelopeJson(MetaverseNetworkOwnershipPayload payload, string roomId = "")
    {
        if (payload == null) return string.Empty;
        payload.type = RealtimeMessageTypes.Ownership;
        if (!string.IsNullOrWhiteSpace(roomId)) payload.roomId = roomId.Trim();
        if (payload.serverTimeUnixMs <= 0) payload.serverTimeUnixMs = NowUnixMs();
        payload.mirrorRoute = payload.ReadMirrorRoute();
        payload.NormalizeDefaults();
        string payloadJson = JsonUtility.ToJson(payload);
        return DedicatedRealtimeEnvelopeCodec.WrapGamePayload(RealtimeMessageTypes.Ownership, payloadJson, payload.roomId);
    }

    public static string EnsureGameEnvelopeRoom(string rawJson, string roomId)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return string.Empty;
        RealtimeEnvelope envelope = RealtimeEnvelope.FromJson(rawJson);
        if (envelope != null && envelope.IsValidBasic())
        {
            envelope.room = string.IsNullOrWhiteSpace(roomId) ? envelope.room : roomId.Trim();
            envelope.EnsureDefaults();
            return envelope.ToJson();
        }

        if (!TryReadOwnershipPayload(rawJson, out MetaverseNetworkOwnershipPayload payload) || payload == null) return string.Empty;
        return CreateOwnershipEnvelopeJson(payload, roomId);
    }

    public static bool IsOwnershipEnvelope(string rawJson)
    {
        return DedicatedRealtimeEnvelopeCodec.Matches(rawJson, RealtimeChannels.Game, RealtimeMessageTypes.Ownership);
    }

    public static bool TryReadOwnershipPayload(string rawJson, out MetaverseNetworkOwnershipPayload payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(rawJson)) return false;

        string payloadJson = DedicatedRealtimeEnvelopeCodec.ReadPayloadOrRawJson(rawJson);
        if (string.IsNullOrWhiteSpace(payloadJson)) return false;

        try
        {
            payload = JsonUtility.FromJson<MetaverseNetworkOwnershipPayload>(payloadJson);
            if (payload == null) return false;
            if (string.IsNullOrWhiteSpace(payload.type)) payload.type = RealtimeMessageTypes.Ownership;
            payload.NormalizeDefaults();
            return payload.netId > 0;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[MetaverseNetworkOwnershipMessageCodec] Ownership payload parse failed | error=" + ex.Message);
            return false;
        }
    }

    public static string ReadMessageFormat(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return "empty";
        if (RealtimeEnvelope.FromJson(rawJson) != null) return "envelope";
        return "raw";
    }

    public static string ReadRouteForLog(string rawJson)
    {
        RealtimeEnvelope envelope = RealtimeEnvelope.FromJson(rawJson);
        if (envelope != null && envelope.IsValidBasic()) return SafeRoutePart(envelope.ch) + "/" + SafeRoutePart(envelope.t) + " | mirrorRoute=" + MetaverseDedicatedMessageTypes.ReadMirrorLikeRoute(envelope.t);
        return "game/ownership | mirrorRoute=" + MetaverseDedicatedMessageTypes.MirrorRouteAssignAuthority;
    }

    private static long NowUnixMs()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static string SafeRoutePart(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
    }
}
