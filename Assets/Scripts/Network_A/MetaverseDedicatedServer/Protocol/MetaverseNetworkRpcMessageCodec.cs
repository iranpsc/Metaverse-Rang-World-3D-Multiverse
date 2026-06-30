using System;
using Network_A.GameServer.Protocol;
using Network_A.Realtime.Protocol;
using UnityEngine;

public static class MetaverseNetworkRpcMessageCodec
{
    public static string CreateCommandEnvelopeJson(MetaverseNetworkRpcPayload payload, string roomId = "")
    {
        return CreateEnvelopeJson(RealtimeMessageTypes.Command, payload, roomId);
    }

    public static string CreateClientRpcEnvelopeJson(MetaverseNetworkRpcPayload payload, string roomId = "")
    {
        return CreateEnvelopeJson(RealtimeMessageTypes.ClientRpc, payload, roomId);
    }

    public static string CreateTargetRpcEnvelopeJson(MetaverseNetworkRpcPayload payload, string roomId = "")
    {
        return CreateEnvelopeJson(RealtimeMessageTypes.TargetRpc, payload, roomId);
    }

    public static string CreateEnvelopeJson(string messageType, MetaverseNetworkRpcPayload payload, string roomId = "")
    {
        if (payload == null) return string.Empty;

        string safeType = SafeTrim(messageType);
        if (string.IsNullOrWhiteSpace(safeType)) safeType = SafeTrim(payload.type);
        if (string.IsNullOrWhiteSpace(safeType)) return string.Empty;

        payload.type = safeType;
        if (!string.IsNullOrWhiteSpace(roomId)) payload.roomId = roomId.Trim();
        if (payload.clientTimeUnixMs <= 0) payload.clientTimeUnixMs = NowUnixMs();

        string payloadJson = JsonUtility.ToJson(payload);
        return DedicatedRealtimeEnvelopeCodec.WrapGamePayload(safeType, payloadJson, payload.roomId);
    }

    public static bool IsNetworkRpcEnvelope(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return false;
        return IsCommandEnvelope(rawJson) || IsClientRpcEnvelope(rawJson) || IsTargetRpcEnvelope(rawJson);
    }

    public static bool IsCommandEnvelope(string rawJson)
    {
        return DedicatedRealtimeEnvelopeCodec.Matches(rawJson, RealtimeChannels.Game, RealtimeMessageTypes.Command);
    }

    public static bool IsClientRpcEnvelope(string rawJson)
    {
        return DedicatedRealtimeEnvelopeCodec.Matches(rawJson, RealtimeChannels.Game, RealtimeMessageTypes.ClientRpc);
    }

    public static bool IsTargetRpcEnvelope(string rawJson)
    {
        return DedicatedRealtimeEnvelopeCodec.Matches(rawJson, RealtimeChannels.Game, RealtimeMessageTypes.TargetRpc);
    }

    public static bool TryReadCommandPayload(string rawJson, out MetaverseNetworkRpcPayload payload)
    {
        return TryReadPayload(rawJson, RealtimeMessageTypes.Command, out payload);
    }

    public static bool TryReadClientRpcPayload(string rawJson, out MetaverseNetworkRpcPayload payload)
    {
        return TryReadPayload(rawJson, RealtimeMessageTypes.ClientRpc, out payload);
    }

    public static bool TryReadTargetRpcPayload(string rawJson, out MetaverseNetworkRpcPayload payload)
    {
        return TryReadPayload(rawJson, RealtimeMessageTypes.TargetRpc, out payload);
    }

    public static bool TryReadPayload(string rawJson, string expectedType, out MetaverseNetworkRpcPayload payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(rawJson)) return false;

        string messageType = DedicatedRealtimeEnvelopeCodec.ReadMessageType(rawJson);
        if (!string.IsNullOrWhiteSpace(expectedType) &&
            !string.Equals(messageType, expectedType, StringComparison.Ordinal))
        {
            return false;
        }

        string payloadJson = DedicatedRealtimeEnvelopeCodec.ReadPayloadOrRawJson(rawJson);
        if (string.IsNullOrWhiteSpace(payloadJson)) return false;

        try
        {
            MetaverseNetworkRpcPayload parsed = JsonUtility.FromJson<MetaverseNetworkRpcPayload>(payloadJson);
            if (parsed == null) return false;
            if (string.IsNullOrWhiteSpace(parsed.type)) parsed.type = messageType;
            payload = parsed;
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[MetaverseNetworkRpcMessageCodec] Payload parse failed | " + ex.Message);
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

        if (!TryReadPayload(rawJson, string.Empty, out MetaverseNetworkRpcPayload payload)) return string.Empty;
        string messageType = string.IsNullOrWhiteSpace(payload.type) ? DedicatedRealtimeEnvelopeCodec.ReadMessageType(rawJson) : payload.type;
        return CreateEnvelopeJson(messageType, payload, roomId);
    }

    public static string ReadMessageFormat(string rawJson)
    {
        return DedicatedRealtimeEnvelopeCodec.ReadMessageFormat(rawJson);
    }

    public static string ReadRouteForLog(string rawJson)
    {
        return DedicatedRealtimeEnvelopeCodec.ReadRouteForLog(rawJson);
    }

    private static long NowUnixMs()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
