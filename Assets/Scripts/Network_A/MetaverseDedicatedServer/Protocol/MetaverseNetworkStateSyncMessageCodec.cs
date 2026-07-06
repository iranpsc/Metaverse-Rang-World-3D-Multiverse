using System;
using Network_A.GameServer.Protocol;
using Network_A.Realtime.Protocol;
using UnityEngine;

public static class MetaverseNetworkStateSyncMessageCodec
{
    public static string CreateSyncVarEnvelopeJson(MetaverseNetworkSyncVarPayload payload, string roomId = "")
    {
        if (payload == null) return string.Empty;
        payload.type = RealtimeMessageTypes.SyncVar;
        if (!string.IsNullOrWhiteSpace(roomId)) payload.roomId = roomId.Trim();
        if (payload.serverTimeUnixMs <= 0) payload.serverTimeUnixMs = NowUnixMs();
        payload.mirrorRoute = MetaverseDedicatedMessageTypes.MirrorRouteSyncVar;
        payload.NormalizeDefaults();
        string payloadJson = JsonUtility.ToJson(payload);
        return DedicatedRealtimeEnvelopeCodec.WrapGamePayload(RealtimeMessageTypes.SyncVar, payloadJson, payload.roomId);
    }

    public static string CreateNetworkTransformEnvelopeJson(MetaverseNetworkTransformPayload payload, string roomId = "")
    {
        if (payload == null) return string.Empty;
        payload.type = RealtimeMessageTypes.NetworkTransform;
        if (!string.IsNullOrWhiteSpace(roomId)) payload.roomId = roomId.Trim();
        if (payload.serverTimeUnixMs <= 0) payload.serverTimeUnixMs = NowUnixMs();
        payload.mirrorRoute = MetaverseDedicatedMessageTypes.MirrorRouteSyncTransform;
        payload.NormalizeDefaults();
        string payloadJson = JsonUtility.ToJson(payload);
        return DedicatedRealtimeEnvelopeCodec.WrapGamePayload(RealtimeMessageTypes.NetworkTransform, payloadJson, payload.roomId);
    }

    public static string CreateSyncTransformEnvelopeJson(MetaverseNetworkTransformPayload payload, string roomId = "")
    {
        return CreateNetworkTransformEnvelopeJson(payload, roomId);
    }

    public static bool IsSyncVarEnvelope(string rawJson)
    {
        return DedicatedRealtimeEnvelopeCodec.Matches(rawJson, RealtimeChannels.Game, RealtimeMessageTypes.SyncVar);
    }

    public static bool IsNetworkTransformEnvelope(string rawJson)
    {
        return DedicatedRealtimeEnvelopeCodec.Matches(rawJson, RealtimeChannels.Game, RealtimeMessageTypes.NetworkTransform);
    }

    public static bool IsStateSyncEnvelope(string rawJson)
    {
        return IsSyncVarEnvelope(rawJson) || IsNetworkTransformEnvelope(rawJson);
    }

    public static bool TryReadSyncVarPayload(string rawJson, out MetaverseNetworkSyncVarPayload payload)
    {
        payload = null;
        if (!IsSyncVarEnvelope(rawJson)) return false;
        string payloadJson = DedicatedRealtimeEnvelopeCodec.ReadPayloadOrRawJson(rawJson);
        if (string.IsNullOrWhiteSpace(payloadJson)) return false;
        try
        {
            payload = JsonUtility.FromJson<MetaverseNetworkSyncVarPayload>(payloadJson);
            if (payload == null) return false;
            if (string.IsNullOrWhiteSpace(payload.type)) payload.type = RealtimeMessageTypes.SyncVar;
            payload.NormalizeDefaults();
            return payload.HasValidSyncTarget();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[MetaverseNetworkStateSyncMessageCodec] SyncVar parse failed | " + ex.Message);
            return false;
        }
    }

    public static bool TryReadNetworkTransformPayload(string rawJson, out MetaverseNetworkTransformPayload payload)
    {
        payload = null;
        if (!IsNetworkTransformEnvelope(rawJson)) return false;
        string payloadJson = DedicatedRealtimeEnvelopeCodec.ReadPayloadOrRawJson(rawJson);
        if (string.IsNullOrWhiteSpace(payloadJson)) return false;
        try
        {
            payload = JsonUtility.FromJson<MetaverseNetworkTransformPayload>(payloadJson);
            if (payload == null) return false;
            if (string.IsNullOrWhiteSpace(payload.type)) payload.type = RealtimeMessageTypes.NetworkTransform;
            payload.NormalizeDefaults();
            return payload.HasValidTransformTarget();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[MetaverseNetworkStateSyncMessageCodec] NetworkTransform parse failed | " + ex.Message);
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
        return string.Empty;
    }

    public static string ReadMessageFormat(string rawJson)
    {
        return DedicatedRealtimeEnvelopeCodec.ReadMessageFormat(rawJson);
    }

    public static string ReadRouteForLog(string rawJson)
    {
        RealtimeEnvelope envelope = RealtimeEnvelope.FromJson(rawJson);
        if (envelope != null && envelope.IsValidBasic()) return SafeRoutePart(envelope.ch) + "/" + SafeRoutePart(envelope.t) + " | mirrorRoute=" + MetaverseDedicatedMessageTypes.ReadMirrorLikeRoute(envelope.t);
        return "game/state_sync | mirrorRoute=SyncVar/SyncTransform";
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
