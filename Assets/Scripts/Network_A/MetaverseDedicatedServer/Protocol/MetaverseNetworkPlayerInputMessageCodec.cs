using System;
using Network_A.GameServer.Protocol;
using Network_A.Realtime.Protocol;
using UnityEngine;

public static class MetaverseNetworkPlayerInputMessageCodec
{
    public static string CreatePlayerInputEnvelopeJson(MetaverseNetworkPlayerInputPayload payload, string roomId = "")
    {
        if (payload == null) return string.Empty;
        payload.type = RealtimeMessageTypes.PlayerInput;
        if (!string.IsNullOrWhiteSpace(roomId)) payload.roomId = roomId.Trim();
        if (payload.clientTimeUnixMs <= 0) payload.clientTimeUnixMs = NowUnixMs();
        payload.mirrorRoute = MetaverseDedicatedMessageTypes.MirrorRouteOwnerInput;
        payload.NormalizeDefaults();
        string payloadJson = JsonUtility.ToJson(payload);
        return DedicatedRealtimeEnvelopeCodec.WrapGamePayload(RealtimeMessageTypes.PlayerInput, payloadJson, payload.roomId);
    }

    public static string CreateOwnerInputEnvelopeJson(MetaverseNetworkPlayerInputPayload payload, string roomId = "")
    {
        return CreatePlayerInputEnvelopeJson(payload, roomId);
    }

    public static bool IsPlayerInputEnvelope(string rawJson)
    {
        return DedicatedRealtimeEnvelopeCodec.Matches(rawJson, RealtimeChannels.Game, RealtimeMessageTypes.PlayerInput);
    }

    public static bool TryReadPlayerInputPayload(string rawJson, out MetaverseNetworkPlayerInputPayload payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(rawJson)) return false;

        string payloadJson = DedicatedRealtimeEnvelopeCodec.ReadPayloadOrRawJson(rawJson);
        if (string.IsNullOrWhiteSpace(payloadJson)) return false;

        try
        {
            payload = JsonUtility.FromJson<MetaverseNetworkPlayerInputPayload>(payloadJson);
            if (payload == null) return false;
            if (string.IsNullOrWhiteSpace(payload.type)) payload.type = RealtimeMessageTypes.PlayerInput;
            payload.NormalizeDefaults();
            return payload.HasValidInputTarget();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[MetaverseNetworkPlayerInputMessageCodec] Player input payload parse failed | error=" + ex.Message);
            return false;
        }
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

        if (!TryReadPlayerInputPayload(rawJson, out MetaverseNetworkPlayerInputPayload payload)) return string.Empty;
        return CreatePlayerInputEnvelopeJson(payload, roomId);
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
        if (envelope != null && envelope.IsValidBasic()) return SafeRoutePart(envelope.ch) + "/" + SafeRoutePart(envelope.t) + " | mirrorRoute=" + MetaverseDedicatedMessageTypes.MirrorRouteOwnerInput;
        return "game/player_input | mirrorRoute=" + MetaverseDedicatedMessageTypes.MirrorRouteOwnerInput;
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
