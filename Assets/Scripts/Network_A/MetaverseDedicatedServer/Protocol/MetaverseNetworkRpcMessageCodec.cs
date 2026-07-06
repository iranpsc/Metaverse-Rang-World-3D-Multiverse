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

    public static string CreateCmdEnvelopeJson(MetaverseNetworkRpcPayload payload, string roomId = "")
    {
        return CreateCommandEnvelopeJson(payload, roomId);
    }

    public static string CreateClientRpcEnvelopeJson(MetaverseNetworkRpcPayload payload, string roomId = "")
    {
        return CreateEnvelopeJson(RealtimeMessageTypes.ClientRpc, payload, roomId);
    }

    public static string CreateRpcEnvelopeJson(MetaverseNetworkRpcPayload payload, string roomId = "")
    {
        return CreateClientRpcEnvelopeJson(payload, roomId);
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
        payload.SetMethodName(payload.ReadMethodName());
        if (!string.IsNullOrWhiteSpace(roomId)) payload.roomId = roomId.Trim();
        if (payload.clientTimeUnixMs <= 0 && safeType == RealtimeMessageTypes.Command) payload.clientTimeUnixMs = NowUnixMs();
        if (payload.serverTimeUnixMs <= 0 && safeType != RealtimeMessageTypes.Command) payload.serverTimeUnixMs = NowUnixMs();
        payload.mirrorRoute = MetaverseDedicatedMessageTypes.ReadMirrorLikeRoute(safeType);
        payload.NormalizeDefaults();

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

    public static bool TryReadRpcPayload(string rawJson, out MetaverseNetworkRpcPayload payload)
    {
        return TryReadClientRpcPayload(rawJson, out payload);
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
        if (string.IsNullOrWhiteSpace(messageType)) messageType = ReadLegacyType(rawJson);

        if (!string.IsNullOrWhiteSpace(expectedType) && !string.Equals(messageType, expectedType, StringComparison.Ordinal)) return false;

        string payloadJson = DedicatedRealtimeEnvelopeCodec.ReadPayloadOrRawJson(rawJson);
        if (string.IsNullOrWhiteSpace(payloadJson)) return false;

        try
        {
            MetaverseNetworkRpcPayload parsed = JsonUtility.FromJson<MetaverseNetworkRpcPayload>(payloadJson);
            if (parsed == null) return false;
            if (string.IsNullOrWhiteSpace(parsed.type)) parsed.type = messageType;
            parsed.SetMethodName(parsed.ReadMethodName());
            parsed.mirrorRoute = MetaverseDedicatedMessageTypes.ReadMirrorLikeRoute(parsed.type);
            parsed.NormalizeDefaults();
            payload = parsed;
            return payload.HasValidNetworkTarget();
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
        string route = DedicatedRealtimeEnvelopeCodec.ReadRouteForLog(rawJson);
        string type = DedicatedRealtimeEnvelopeCodec.ReadMessageType(rawJson);
        if (string.IsNullOrWhiteSpace(type)) type = ReadLegacyType(rawJson);
        return route + " | mirrorRoute=" + MetaverseDedicatedMessageTypes.ReadMirrorLikeRoute(type);
    }

    public static string ReadMirrorLikeRouteForLog(string rawJson)
    {
        string type = DedicatedRealtimeEnvelopeCodec.ReadMessageType(rawJson);
        if (string.IsNullOrWhiteSpace(type)) type = ReadLegacyType(rawJson);
        return MetaverseDedicatedMessageTypes.ReadMirrorLikeRoute(type);
    }

    private static string ReadLegacyType(string rawJson)
    {
        try
        {
            MetaverseNetworkRpcPayload payload = JsonUtility.FromJson<MetaverseNetworkRpcPayload>(rawJson);
            return payload == null ? string.Empty : SafeTrim(payload.type);
        }
        catch
        {
            return string.Empty;
        }
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
