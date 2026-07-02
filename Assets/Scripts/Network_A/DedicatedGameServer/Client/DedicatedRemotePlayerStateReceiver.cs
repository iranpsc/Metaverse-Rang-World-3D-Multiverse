using System;
using System.Collections.Generic;
using Network_A.Realtime.Protocol;
using UnityEngine;

namespace Network_A.DedicatedGameServer.Client
{
    public class DedicatedRemotePlayerStateReceiver : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private DedicatedGameServerWsClient wsClient;

        [Header("Rules")]
        [SerializeField] private bool ignoreOwnPlayerState = true;
        [SerializeField] private bool logPlayerJoined = true;
        [SerializeField] private bool logPlayerLeft = true;
        [SerializeField] private bool logRemotePlayerState = true;
        [SerializeField] private bool logStateAccepted = false;
        [SerializeField] private bool logMessageFormat = true;

        private readonly Dictionary<string, DedicatedRemotePlayerState> dict_remoteStatesByPlayerId =
            new Dictionary<string, DedicatedRemotePlayerState>();

        private readonly HashSet<string> set_leftNotifiedPlayerIds = new HashSet<string>();
        private readonly Dictionary<string, long> dict_lastSequenceByPlayerId = new Dictionary<string, long>();

        private bool wsEventsBound;
        private DedicatedGameServerWsClient boundWsClient;

        public int RemotePlayerCount
        {
            get { return dict_remoteStatesByPlayerId.Count; }
        }

        public event Action<DedicatedRemotePlayerState> RemotePlayerStateReceived;
        public event Action<DedicatedRemotePresenceEvent> RemotePlayerJoined;
        public event Action<DedicatedRemotePresenceEvent> RemotePlayerLeft;
        public event Action<DedicatedPlayerStateAcceptedEvent> PlayerStateAccepted;

        private void Awake()
        {
            EnsureReferences();
        }

        private void OnEnable()
        {
            BindWsEvents();
        }

        private void OnDisable()
        {
            UnbindWsEvents();
        }

        private void EnsureReferences()
        {
            if (wsClient != null) return;

            wsClient = GetComponent<DedicatedGameServerWsClient>();
            if (wsClient != null) return;

            wsClient = DedicatedGameServerWsClient.Instance;
        }

        private void BindWsEvents()
        {
            EnsureReferences();

            if (wsEventsBound && boundWsClient == wsClient)
            {
                return;
            }

            UnbindWsEvents();

            if (wsClient == null)
            {
                return;
            }

            wsClient.RawMessageReceived -= HandleRawMessageReceived;
            wsClient.Disconnected -= HandleDisconnected;

            wsClient.RawMessageReceived += HandleRawMessageReceived;
            wsClient.Disconnected += HandleDisconnected;

            boundWsClient = wsClient;
            wsEventsBound = true;
        }

        private void UnbindWsEvents()
        {
            if (boundWsClient == null)
            {
                wsEventsBound = false;
                return;
            }

            boundWsClient.RawMessageReceived -= HandleRawMessageReceived;
            boundWsClient.Disconnected -= HandleDisconnected;

            boundWsClient = null;
            wsEventsBound = false;
        }

        public List<DedicatedRemotePlayerState> CreateSnapshot()
        {
            return new List<DedicatedRemotePlayerState>(dict_remoteStatesByPlayerId.Values);
        }

        public DedicatedRemotePlayerState GetRemoteState(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return null;

            return dict_remoteStatesByPlayerId.TryGetValue(playerId.Trim(), out DedicatedRemotePlayerState state)
                ? state
                : null;
        }

        private void HandleRawMessageReceived(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;

            string messageType = ReadMessageType(raw);
            if (string.IsNullOrWhiteSpace(messageType)) return;

            if (messageType == RealtimeMessageTypes.PlayerState || messageType == "player_state")
            {
                HandlePlayerState(raw);
                return;
            }

            if (messageType == RealtimeMessageTypes.PlayerJoined || messageType == "player_joined")
            {
                HandlePlayerJoined(raw);
                return;
            }

            if (messageType == RealtimeMessageTypes.PlayerLeft || messageType == "player_left")
            {
                HandlePlayerLeft(raw);
                return;
            }

            if (messageType == RealtimeMessageTypes.PlayerStateAccepted || messageType == "player_state_accepted")
            {
                HandlePlayerStateAccepted(raw);
            }
        }

        private void HandlePlayerState(string raw)
        {
            DedicatedRemotePlayerState state = null;
            string payloadJson = ReadPayloadOrRawJson(raw);

            try
            {
                state = JsonUtility.FromJson<DedicatedRemotePlayerState>(payloadJson);
            }
            catch (Exception ex)
            {
                Debug.LogError("[DedicatedRemotePlayerStateReceiver] player_state parse failed | " + ex.Message);
                return;
            }

            if (state == null) return;

            state.rawJson = raw;

            string playerId = state.ResolvePlayerId();

            if (string.IsNullOrWhiteSpace(playerId))
            {
                Debug.LogWarning("[DedicatedRemotePlayerStateReceiver] player_state ignored | playerId empty");
                return;
            }

            playerId = playerId.Trim();

            if (ignoreOwnPlayerState && wsClient != null && playerId == wsClient.PlayerId)
            {
                return;
            }

            if (ShouldDropDuplicateOrOldPlayerState(playerId, state.sequence))
            {
                return;
            }

            set_leftNotifiedPlayerIds.Remove(playerId);
            dict_remoteStatesByPlayerId[playerId] = state;
            RemotePlayerStateReceived?.Invoke(state);

            if (logRemotePlayerState)
            {
                Debug.Log("[DedicatedRemotePlayerStateReceiver] Remote player_state received | playerId=" +
                          playerId + " | sequence=" + state.sequence +
                          " | pos=" + state.Position +
                          " | remoteCount=" + RemotePlayerCount +
                          BuildFormatLog(raw));
            }
        }

        private void HandlePlayerJoined(string raw)
        {
            DedicatedRemotePresenceEvent evt = null;
            string payloadJson = ReadPayloadOrRawJson(raw);

            try
            {
                evt = JsonUtility.FromJson<DedicatedRemotePresenceEvent>(payloadJson);
            }
            catch (Exception ex)
            {
                Debug.LogError("[DedicatedRemotePlayerStateReceiver] player_joined parse failed | " + ex.Message);
                return;
            }

            if (evt == null) return;

            evt.rawJson = raw;

            string joinedPlayerId = evt.ResolvePlayerId();
            if (!string.IsNullOrWhiteSpace(joinedPlayerId))
            {
                joinedPlayerId = joinedPlayerId.Trim();
                set_leftNotifiedPlayerIds.Remove(joinedPlayerId);
                dict_lastSequenceByPlayerId.Remove(joinedPlayerId);
            }

            RemotePlayerJoined?.Invoke(evt);

            if (logPlayerJoined)
            {
                Debug.Log("[DedicatedRemotePlayerStateReceiver] Remote player joined | playerId=" +
                          evt.ResolvePlayerId() + " | userId=" + evt.userId + " | roomId=" + evt.roomId +
                          BuildFormatLog(raw));
            }
        }

        private void HandlePlayerLeft(string raw)
        {
            DedicatedRemotePresenceEvent evt = null;
            string payloadJson = ReadPayloadOrRawJson(raw);

            try
            {
                evt = JsonUtility.FromJson<DedicatedRemotePresenceEvent>(payloadJson);
            }
            catch (Exception ex)
            {
                Debug.LogError("[DedicatedRemotePlayerStateReceiver] player_left parse failed | " + ex.Message);
                return;
            }

            if (evt == null) return;

            evt.rawJson = raw;

            string playerId = evt.ResolvePlayerId();

            if (!string.IsNullOrWhiteSpace(playerId))
            {
                playerId = playerId.Trim();

                if (set_leftNotifiedPlayerIds.Contains(playerId))
                {
                    return;
                }

                set_leftNotifiedPlayerIds.Add(playerId);
                dict_remoteStatesByPlayerId.Remove(playerId);
                dict_lastSequenceByPlayerId.Remove(playerId);
            }

            RemotePlayerLeft?.Invoke(evt);

            if (logPlayerLeft)
            {
                Debug.Log("[DedicatedRemotePlayerStateReceiver] Remote player left | playerId=" +
                          playerId + " | reason=" + evt.reason + " | remoteCount=" + RemotePlayerCount +
                          BuildFormatLog(raw));
            }
        }

        private void HandlePlayerStateAccepted(string raw)
        {
            DedicatedPlayerStateAcceptedEvent evt = null;
            string payloadJson = ReadPayloadOrRawJson(raw);

            try
            {
                evt = JsonUtility.FromJson<DedicatedPlayerStateAcceptedEvent>(payloadJson);
            }
            catch (Exception ex)
            {
                Debug.LogError("[DedicatedRemotePlayerStateReceiver] player_state_accepted parse failed | " + ex.Message);
                return;
            }

            if (evt == null) return;

            evt.rawJson = raw;
            PlayerStateAccepted?.Invoke(evt);

            if (logStateAccepted)
            {
                Debug.Log("[DedicatedRemotePlayerStateReceiver] player_state_accepted | sequence=" +
                          evt.sequence + " | broadcastCount=" + evt.broadcastCount +
                          BuildFormatLog(raw));
            }
        }

        private void HandleDisconnected(string reason)
        {
            int previousCount = dict_remoteStatesByPlayerId.Count;

            dict_remoteStatesByPlayerId.Clear();
            set_leftNotifiedPlayerIds.Clear();
            dict_lastSequenceByPlayerId.Clear();

            if (previousCount > 0)
            {
                Debug.Log("[DedicatedRemotePlayerStateReceiver] Cleared remote states | disconnectReason=" + reason);
            }
        }

        private bool ShouldDropDuplicateOrOldPlayerState(string playerId, long sequence)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return true;
            if (sequence <= 0) return false;

            string safePlayerId = playerId.Trim();

            if (dict_lastSequenceByPlayerId.TryGetValue(safePlayerId, out long lastSequence) &&
                sequence <= lastSequence)
            {
                return true;
            }

            dict_lastSequenceByPlayerId[safePlayerId] = sequence;
            return false;
        }

        private string BuildFormatLog(string raw)
        {
            if (!logMessageFormat) return string.Empty;
            return " | messageFormat=" + ReadMessageFormat(raw) + " | route=" + ReadRouteForLog(raw);
        }

        private string ReadMessageFormat(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "empty";
            if (TryParseEnvelope(raw, out RealtimeEnvelope _)) return "envelope";

            DedicatedRemoteMessageTypeDto typeDto = TryParseLegacyType(raw);
            if (typeDto != null && !string.IsNullOrWhiteSpace(typeDto.type)) return "legacy";

            return "invalid";
        }

        private string ReadRouteForLog(string raw)
        {
            if (TryParseEnvelope(raw, out RealtimeEnvelope envelope))
            {
                string channel = string.IsNullOrWhiteSpace(envelope.ch) ? "unknown" : envelope.ch.Trim();
                string type = string.IsNullOrWhiteSpace(envelope.t) ? "unknown" : envelope.t.Trim();
                return channel + "/" + type;
            }

            DedicatedRemoteMessageTypeDto typeDto = TryParseLegacyType(raw);
            string legacyType = typeDto == null || string.IsNullOrWhiteSpace(typeDto.type) ? "unknown" : typeDto.type.Trim();
            return "legacy/" + legacyType;
        }

        private DedicatedRemoteMessageTypeDto TryParseLegacyType(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            try
            {
                return JsonUtility.FromJson<DedicatedRemoteMessageTypeDto>(raw);
            }
            catch
            {
                return null;
            }
        }

        private DedicatedRemoteMessageTypeDto ParseType(string raw)
        {
            string messageType = ReadMessageType(raw);
            return string.IsNullOrWhiteSpace(messageType) ? null : new DedicatedRemoteMessageTypeDto { type = messageType };
        }

        private string ReadMessageType(string raw)
        {
            if (TryParseEnvelope(raw, out RealtimeEnvelope envelope)) return envelope.t;

            DedicatedRemoteMessageTypeDto typeDto = TryParseLegacyType(raw);
            return typeDto == null ? string.Empty : typeDto.type;
        }

        private string ReadPayloadOrRawJson(string raw)
        {
            if (TryParseEnvelope(raw, out RealtimeEnvelope envelope)) return envelope.payloadJson;
            return string.IsNullOrWhiteSpace(raw) ? "{}" : raw;
        }

        private bool TryParseEnvelope(string raw, out RealtimeEnvelope envelope)
        {
            envelope = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            RealtimeEnvelope parsed = RealtimeEnvelope.FromJson(raw);
            if (parsed == null || !parsed.IsValidBasic()) return false;

            envelope = parsed;
            return true;
        }

        [Serializable]
        private class DedicatedRemoteMessageTypeDto
        {
            public string type;
        }
    }

    [Serializable]
    public class DedicatedRemotePlayerState
    {
        public string type;
        public string userId;
        public string playerId;
        public string userName;
        public string connectionId;
        public string roomId;
        public string serverId;
        public string sessionId;

        public long sequence;
        public long clientTimestampUnixMs;
        public long serverTimestampUnixMs;

        public float px;
        public float py;
        public float pz;

        public float rx;
        public float ry;
        public float rz;
        public float rw;

        public float vx;
        public float vy;
        public float vz;

        public string rawJson;

        public Vector3 Position
        {
            get { return new Vector3(px, py, pz); }
        }

        public Quaternion Rotation
        {
            get { return new Quaternion(rx, ry, rz, rw == 0f ? 1f : rw); }
        }

        public Vector3 Velocity
        {
            get { return new Vector3(vx, vy, vz); }
        }

        public string ResolvePlayerId()
        {
            if (!string.IsNullOrWhiteSpace(playerId)) return playerId.Trim();
            if (!string.IsNullOrWhiteSpace(userId)) return userId.Trim();
            if (!string.IsNullOrWhiteSpace(connectionId)) return connectionId.Trim();
            return string.Empty;
        }
    }

    [Serializable]
    public class DedicatedRemotePresenceEvent
    {
        public string type;
        public string userId;
        public string playerId;
        public string userName;
        public string connectionId;
        public string roomId;
        public string serverId;
        public string sessionId;
        public string reason;
        public long serverTimeUnixMs;
        public string rawJson;

        public string ResolvePlayerId()
        {
            if (!string.IsNullOrWhiteSpace(playerId)) return playerId.Trim();
            if (!string.IsNullOrWhiteSpace(userId)) return userId.Trim();
            if (!string.IsNullOrWhiteSpace(connectionId)) return connectionId.Trim();
            return string.Empty;
        }
    }

    [Serializable]
    public class DedicatedPlayerStateAcceptedEvent
    {
        public string type;
        public bool ok;
        public long sequence;
        public string roomId;
        public string playerId;
        public long serverTimeUnixMs;
        public int broadcastCount;
        public string rawJson;
    }
}
