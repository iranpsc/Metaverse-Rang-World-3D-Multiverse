using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;
using Network_A.GameServer.Players;
using Network_A.GameServer.Protocol;
using Network_A.GameServer.WebSocket;
using Network_A.Realtime.Protocol;
using UnityEngine;

namespace Network_A.GameServer.Gameplay
{
    public class DedicatedGameMessageRouter : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private DedicatedWebSocketServer webSocketServer;
        [SerializeField] private DedicatedPlayerRegistry playerRegistry;
        [SerializeField] private DedicatedPlayerStateStore playerStateStore;
        [SerializeField] private MetaverseSpawnNetworkBridge spawnNetworkBridge;
        [SerializeField] private MetaverseNetworkRpcBridge rpcNetworkBridge;

        [Header("Rules")]
        [SerializeField] private bool sendStateAckToSender = true;
        [SerializeField] private bool broadcastStateToSender = false;
        [SerializeField] private bool broadcastPresenceEvents = true;
        [SerializeField] private bool dedupePlayerStateMessages = true;
        [SerializeField] private bool logDuplicatePlayerStateMessages = false;
        [SerializeField] private bool logPlayerStateMessages = true;

        [Header("Debug")]
        [SerializeField] private bool logMessageFormat = true;

        private FieldInfo connectionsField;
        private bool eventsSubscribed;
        private MetaverseSpawnNetworkBridge subscribedSpawnNetworkBridge;
        private MetaverseNetworkRpcBridge subscribedRpcNetworkBridge;
        private bool attemptedSpawnBridgeAutoInstall;
        private bool loggedSpawnBridgeSubscription;
        private readonly ConcurrentDictionary<string, long> dict_lastStateSequenceByConnectionId =
            new ConcurrentDictionary<string, long>();

        //* این تابع رفرنس های لازم را هنگام شروع آبجکت پیدا می کند.
        private void Awake()
        {
            EnsureReferences();
        }

        //* این تابع هنگام فعال شدن آبجکت، پیام های وب سوکت و رویدادهای پلیر را گوش می دهد.
        private void OnEnable()
        {
            EnsureReferences();
            Subscribe();
        }

        //* این تابع اگر بریج اسپاون بعداً ساخته شد، اتصال رویداد آن را کامل می کند.
        private void Update()
        {
            if (!eventsSubscribed) return;
            if (spawnNetworkBridge == null || subscribedSpawnNetworkBridge != spawnNetworkBridge ||
                rpcNetworkBridge == null || subscribedRpcNetworkBridge != rpcNetworkBridge)
            {
                EnsureReferences();
                EnsureSpawnBridgeSubscription();
                EnsureRpcBridgeSubscription();
            }
        }

        //* این تابع هنگام غیرفعال شدن آبجکت، رویدادها را پاک می کند.
        private void OnDisable()
        {
            Unsubscribe();
        }

        //* این تابع از اینسپکتور برای اتصال دستی رفرنس ها و رویدادها استفاده می شود.
        [ContextMenu("Rebind Gameplay Router")]
        public void Rebind()
        {
            Unsubscribe();
            EnsureReferences();
            Subscribe();

            Debug.Log("[DedicatedGameMessageRouter] Rebound | webSocketServer=" + BoolText(webSocketServer != null) +
                      " | playerRegistry=" + BoolText(playerRegistry != null) +
                      " | playerStateStore=" + BoolText(playerStateStore != null) +
                      " | spawnNetworkBridge=" + SpawnBridgeStatusText() +
                      " | rpcNetworkBridge=" + RpcBridgeStatusText());
        }

        //* این تابع رفرنس های وب سوکت سرور، رجیستری و استور وضعیت را پیدا می کند.
        private void EnsureReferences()
        {
            if (webSocketServer == null)
            {
                webSocketServer = GetComponent<DedicatedWebSocketServer>();
                if (webSocketServer == null) webSocketServer = GetComponentInChildren<DedicatedWebSocketServer>(true);
            }

            if (playerRegistry == null)
            {
                playerRegistry = GetComponent<DedicatedPlayerRegistry>();
                if (playerRegistry == null) playerRegistry = GetComponentInChildren<DedicatedPlayerRegistry>(true);
            }

            if (playerStateStore == null)
            {
                playerStateStore = GetComponent<DedicatedPlayerStateStore>();
                if (playerStateStore == null) playerStateStore = GetComponentInChildren<DedicatedPlayerStateStore>(true);
            }

            FindSpawnNetworkBridge();
            FindRpcNetworkBridge();

            if (spawnNetworkBridge == null || rpcNetworkBridge == null)
            {
                TryAutoInstallSpawnBridgeFromRuntimeConfig();
                FindSpawnNetworkBridge();
                FindRpcNetworkBridge();
            }

            if (connectionsField == null)
            {
                connectionsField = typeof(DedicatedWebSocketServer).GetField(
                    "connections",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }
        }

        //* این تابع رویدادهای لازم را گوش می دهد.
        private void Subscribe()
        {
            if (eventsSubscribed) return;

            if (webSocketServer != null)
            {
                webSocketServer.TextMessageReceived -= HandleTextMessageReceived;
                webSocketServer.ClientDisconnected -= HandleClientDisconnected;
                webSocketServer.TextMessageReceived += HandleTextMessageReceived;
                webSocketServer.ClientDisconnected += HandleClientDisconnected;
            }

            if (playerRegistry != null)
            {
                playerRegistry.PlayerRegistered -= HandlePlayerRegistered;
                playerRegistry.PlayerRemoved -= HandlePlayerRemoved;
                playerRegistry.PlayerRegistered += HandlePlayerRegistered;
                playerRegistry.PlayerRemoved += HandlePlayerRemoved;
            }

            eventsSubscribed = true;
            EnsureSpawnBridgeSubscription();
            EnsureRpcBridgeSubscription();
        }


        //* این تابع رویداد بریج اسپاون را فقط یک بار به رُتر وصل می کند.
        private void EnsureSpawnBridgeSubscription()
        {
            if (!eventsSubscribed || spawnNetworkBridge == null) return;
            if (subscribedSpawnNetworkBridge == spawnNetworkBridge) return;

            if (subscribedSpawnNetworkBridge != null)
            {
                subscribedSpawnNetworkBridge.OutboundMessageReady -= HandleSpawnBridgeOutboundMessageReady;
            }

            spawnNetworkBridge.OutboundMessageReady -= HandleSpawnBridgeOutboundMessageReady;
            spawnNetworkBridge.OutboundMessageReady += HandleSpawnBridgeOutboundMessageReady;
            subscribedSpawnNetworkBridge = spawnNetworkBridge;

            if (!loggedSpawnBridgeSubscription)
            {
                loggedSpawnBridgeSubscription = true;
                Debug.Log("[DedicatedGameMessageRouter] Spawn bridge subscription ready | spawnNetworkBridge=OK");
            }
        }

        //* این تابع رویداد بریج کامند و آر پی سی را فقط یک بار به رُتر وصل می کند.
        private void EnsureRpcBridgeSubscription()
        {
            if (!eventsSubscribed || rpcNetworkBridge == null) return;
            if (subscribedRpcNetworkBridge == rpcNetworkBridge) return;

            if (subscribedRpcNetworkBridge != null)
            {
                subscribedRpcNetworkBridge.OutboundMessageReady -= HandleRpcBridgeOutboundMessageReady;
            }

            rpcNetworkBridge.OutboundMessageReady -= HandleRpcBridgeOutboundMessageReady;
            rpcNetworkBridge.OutboundMessageReady += HandleRpcBridgeOutboundMessageReady;
            subscribedRpcNetworkBridge = rpcNetworkBridge;

            Debug.Log("[DedicatedGameMessageRouter] RPC bridge subscription ready | rpcNetworkBridge=OK");
        }

        //* این تابع بریج اسپاون را در صحنه پیدا می کند.
        private void FindSpawnNetworkBridge()
        {
            if (spawnNetworkBridge != null) return;

            spawnNetworkBridge = GetComponent<MetaverseSpawnNetworkBridge>();
            if (spawnNetworkBridge == null) spawnNetworkBridge = GetComponentInChildren<MetaverseSpawnNetworkBridge>(true);
#if UNITY_2023_1_OR_NEWER
            if (spawnNetworkBridge == null) spawnNetworkBridge = FindFirstObjectByType<MetaverseSpawnNetworkBridge>();
#else
            if (spawnNetworkBridge == null) spawnNetworkBridge = FindObjectOfType<MetaverseSpawnNetworkBridge>();
#endif
        }

        //* این تابع بریج کامند و آر پی سی را در صحنه پیدا می کند.
        private void FindRpcNetworkBridge()
        {
            if (rpcNetworkBridge != null) return;

            rpcNetworkBridge = GetComponent<MetaverseNetworkRpcBridge>();
            if (rpcNetworkBridge == null) rpcNetworkBridge = GetComponentInChildren<MetaverseNetworkRpcBridge>(true);
#if UNITY_2023_1_OR_NEWER
            if (rpcNetworkBridge == null) rpcNetworkBridge = FindFirstObjectByType<MetaverseNetworkRpcBridge>();
#else
            if (rpcNetworkBridge == null) rpcNetworkBridge = FindObjectOfType<MetaverseNetworkRpcBridge>();
#endif
        }

        //* این تابع اگر بریج اسپاون هنوز ساخته نشده باشد، نصب اسپاون سیستم را زودتر اجرا می کند.
        private void TryAutoInstallSpawnBridgeFromRuntimeConfig()
        {
            if (attemptedSpawnBridgeAutoInstall) return;
            attemptedSpawnBridgeAutoInstall = true;

            MetaverseDedicatedServerRuntimeConfig config = MetaverseDedicatedServerRuntimeConfig.LoadDefault();
            if (config == null || !config.AutoInstallSpawnSystem || !config.AutoInstallSpawnNetworkBridge) return;

            MetaverseSpawnSystemInstaller.Install(config);
        }

        //* این تابع وضعیت بریج اسپاون را برای لاگ تمیز نشان می دهد.
        private string SpawnBridgeStatusText()
        {
            if (spawnNetworkBridge != null) return "OK";
            return attemptedSpawnBridgeAutoInstall ? "PENDING" : "NOT_READY";
        }

        //* این تابع وضعیت بریج کامند و آر پی سی را برای لاگ تمیز نشان می دهد.
        private string RpcBridgeStatusText()
        {
            if (rpcNetworkBridge != null) return "OK";
            return attemptedSpawnBridgeAutoInstall ? "PENDING" : "NOT_READY";
        }

        //* این تابع رویدادهای قبلی را جدا می کند.
        private void Unsubscribe()
        {
            if (webSocketServer != null)
            {
                webSocketServer.TextMessageReceived -= HandleTextMessageReceived;
                webSocketServer.ClientDisconnected -= HandleClientDisconnected;
            }

            if (playerRegistry != null)
            {
                playerRegistry.PlayerRegistered -= HandlePlayerRegistered;
                playerRegistry.PlayerRemoved -= HandlePlayerRemoved;
            }

            if (subscribedSpawnNetworkBridge != null)
            {
                subscribedSpawnNetworkBridge.OutboundMessageReady -= HandleSpawnBridgeOutboundMessageReady;
                subscribedSpawnNetworkBridge = null;
            }

            if (subscribedRpcNetworkBridge != null)
            {
                subscribedRpcNetworkBridge.OutboundMessageReady -= HandleRpcBridgeOutboundMessageReady;
                subscribedRpcNetworkBridge = null;
            }

            eventsSubscribed = false;
        }

        //* این تابع پیام های متنی بعد از احراز را مسیریابی می کند.
        private async void HandleTextMessageReceived(DedicatedWebSocketConnection connection, string text)
        {
            if (connection == null) return;

            EnsureReferences();

            if (playerRegistry == null || playerStateStore == null)
            {
                await SendErrorAsync(connection, "game_router_not_ready", "Dedicated game router references are missing.");
                return;
            }

            string messageType = DedicatedRealtimeEnvelopeCodec.ReadMessageType(text);
            string messageFormat = DedicatedRealtimeEnvelopeCodec.ReadMessageFormat(text);
            string messageRoute = DedicatedRealtimeEnvelopeCodec.ReadRouteForLog(text);

            if (string.IsNullOrWhiteSpace(messageType))
            {
                return;
            }

            if (messageType == RealtimeMessageTypes.AuthTicket || messageType == "auth_ticket")
            {
                return;
            }

            bool isAuthenticated = playerRegistry.IsConnectionAuthenticated(connection.ConnectionId);

            if (!isAuthenticated)
            {
                return;
            }

            if (IsPlayerStateMessage(text, messageType))
            {
                await HandlePlayerStateAsync(connection, text, messageFormat, messageRoute);
                return;
            }

            if (IsNetworkCommandRouteMessage(text, messageType))
            {
                await HandleNetworkCommandAsync(connection, text, messageFormat, messageRoute);
                return;
            }

            if (IsNetworkRpcRouteMessage(text, messageType))
            {
                await SendErrorAsync(connection, "rpc_server_authoritative", "ClientRpc and TargetRpc are server-authoritative.");
                return;
            }

            if (IsSpawnRouteMessage(text, messageType))
            {
                await SendErrorAsync(connection, "spawn_server_authoritative", "Spawn and despawn are server-authoritative in this phase.");
                return;
            }
        }

        //* این تابع کامند کلاینت را بعد از احراز به آبجکت شبکه ای روی سرور تحویل می دهد.
        private async Task HandleNetworkCommandAsync(DedicatedWebSocketConnection connection, string text, string messageFormat, string messageRoute)
        {
            DedicatedPlayerSession session = playerRegistry.GetByConnectionId(connection.ConnectionId);
            if (session == null)
            {
                await SendErrorAsync(connection, "session_missing", "Authenticated session was not found.");
                return;
            }

            if (rpcNetworkBridge == null)
            {
                await SendErrorAsync(connection, "rpc_bridge_not_ready", "Network RPC bridge is not ready.");
                return;
            }

            if (!MetaverseNetworkRpcMessageCodec.TryReadCommandPayload(text, out MetaverseNetworkRpcPayload payload) || payload == null)
            {
                await SendErrorAsync(connection, "command_parse_failed", "Network command payload could not be parsed.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(payload.roomId) &&
                !string.Equals(payload.roomId.Trim(), session.roomId, StringComparison.Ordinal))
            {
                await SendErrorAsync(connection, "room_mismatch", "Network command room does not match authenticated session.");
                return;
            }

            bool handled = rpcNetworkBridge.HandleServerCommand(session, payload);
            if (!handled)
            {
                await SendErrorAsync(connection, "command_not_handled", "Network command could not be handled on server.");
                return;
            }

            Debug.Log("[DedicatedGameMessageRouter] Network command handled | userId=" + session.userId +
                      " | roomId=" + session.roomId +
                      " | netId=" + payload.netId +
                      " | command=" + SafeForLog(payload.methodName) +
                      " | messageFormat=" + messageFormat +
                      " | route=" + messageRoute);
        }

        //* این تابع پیام player_state را پردازش، ذخیره و برای بقیه پلیرهای همان روم پخش می کند.
        private async Task HandlePlayerStateAsync(DedicatedWebSocketConnection connection, string text, string messageFormat, string messageRoute)
        {
            DedicatedPlayerSession session = playerRegistry.GetByConnectionId(connection.ConnectionId);

            if (session == null)
            {
                await SendErrorAsync(connection, "session_missing", "Authenticated session was not found.");
                return;
            }

            DedicatedPlayerStateMessageDto message = ParsePlayerStateMessage(text);

            if (message == null)
            {
                await SendErrorAsync(connection, "player_state_parse_failed", "Player state message could not be parsed.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(message.roomId) &&
                !string.Equals(message.roomId.Trim(), session.roomId, StringComparison.Ordinal))
            {
                await SendErrorAsync(connection, "room_mismatch", "Player state room does not match authenticated session.");
                return;
            }

            if (ShouldIgnoreDuplicatePlayerState(session, message))
            {
                return;
            }

            long serverTimeUnixMs = NowUnixMs();

            DedicatedPlayerStateRecord record = playerStateStore.UpdateState(session, message, serverTimeUnixMs);
            playerRegistry.TouchConnection(connection.ConnectionId);

            if (record == null)
            {
                await SendErrorAsync(connection, "state_store_failed", "Player state could not be stored.");
                return;
            }

            DedicatedPlayerStateBroadcastDto broadcast = DedicatedPlayerStateBroadcastDto.FromRecord(record);
            string broadcastJson = WrapPresenceEnvelope(RealtimeMessageTypes.PlayerState, JsonUtility.ToJson(broadcast), session.roomId);

            int sentCount = BroadcastToRoom(
                session.roomId,
                broadcastJson,
                broadcastStateToSender ? string.Empty : connection.ConnectionId);

            if (sendStateAckToSender)
            {
                DedicatedPlayerStateAcceptedDto accepted = new DedicatedPlayerStateAcceptedDto
                {
                    type = "player_state_accepted",
                    ok = true,
                    sequence = record.sequence,
                    roomId = record.roomId,
                    playerId = record.playerId,
                    serverTimeUnixMs = serverTimeUnixMs,
                    broadcastCount = sentCount
                };

                await connection.SendTextAsync(WrapPresenceEnvelope(RealtimeMessageTypes.PlayerStateAccepted, JsonUtility.ToJson(accepted), record.roomId));
            }

            if (logPlayerStateMessages)
            {
                Debug.Log("[DedicatedGameMessageRouter] Player state handled | userId=" +
                          session.userId + " | roomId=" + session.roomId +
                          " | sequence=" + record.sequence + " | broadcastCount=" + sentCount +
                          " | messageFormat=" + messageFormat + " | route=" + messageRoute +
                          " | outgoingMessageFormat=envelope | outgoingRoute=" +
                          RealtimeChannels.Presence + "/" + RealtimeMessageTypes.PlayerState);
            }
        }

        //* این تابع ورود پلیر را برای بقیه پلیرهای همان روم پخش می کند.
        private void HandlePlayerRegistered(DedicatedPlayerSession session)
        {
            if (session != null && !string.IsNullOrWhiteSpace(session.connectionId))
            {
                dict_lastStateSequenceByConnectionId.TryRemove(session.connectionId, out long _);
            }

            if (!broadcastPresenceEvents || session == null) return;

            DedicatedPresenceEventDto evt = new DedicatedPresenceEventDto
            {
                type = "player_joined",
                userId = session.userId,
                playerId = session.playerId,
                userName = session.userName,
                connectionId = session.connectionId,
                roomId = session.roomId,
                serverId = session.serverId,
                sessionId = session.sessionId,
                reason = "player_registered",
                serverTimeUnixMs = NowUnixMs()
            };

            int sentCount = BroadcastToRoom(session.roomId, WrapPresenceEnvelope(RealtimeMessageTypes.PlayerJoined, JsonUtility.ToJson(evt), session.roomId), session.connectionId);

            SendSpawnSnapshotToSession(session);

            Debug.Log("[DedicatedGameMessageRouter] Player joined broadcast | userId=" +
                      session.userId + " | sentCount=" + sentCount +
                      " | outgoingMessageFormat=envelope | outgoingRoute=" +
                      RealtimeChannels.Presence + "/" + RealtimeMessageTypes.PlayerJoined);
        }

        //* این تابع خروج پلیر را برای بقیه پلیرهای همان روم پخش می کند.
        private void HandlePlayerRemoved(DedicatedPlayerSession session, string reason)
        {
            if (session == null) return;

            if (!string.IsNullOrWhiteSpace(session.connectionId))
            {
                dict_lastStateSequenceByConnectionId.TryRemove(session.connectionId, out long _);
            }

            if (playerStateStore != null)
            {
                playerStateStore.RemoveByConnectionId(session.connectionId, reason);
            }

            if (!broadcastPresenceEvents) return;

            DedicatedPresenceEventDto evt = new DedicatedPresenceEventDto
            {
                type = "player_left",
                userId = session.userId,
                playerId = session.playerId,
                userName = session.userName,
                connectionId = session.connectionId,
                roomId = session.roomId,
                serverId = session.serverId,
                sessionId = session.sessionId,
                reason = reason,
                serverTimeUnixMs = NowUnixMs()
            };

            int sentCount = BroadcastToRoom(session.roomId, WrapPresenceEnvelope(RealtimeMessageTypes.PlayerLeft, JsonUtility.ToJson(evt), session.roomId), session.connectionId);

            Debug.Log("[DedicatedGameMessageRouter] Player left broadcast | userId=" +
                      session.userId + " | reason=" + reason + " | sentCount=" + sentCount +
                      " | outgoingMessageFormat=envelope | outgoingRoute=" +
                      RealtimeChannels.Presence + "/" + RealtimeMessageTypes.PlayerLeft);
        }


        //* این تابع پیام آماده اسپاون را از بریج می گیرد و برای کلاینت های همان روم پخش می کند.
        private void HandleSpawnBridgeOutboundMessageReady(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson)) return;

            string roomId = ResolveBroadcastRoomId(rawJson);
            if (string.IsNullOrWhiteSpace(roomId))
            {
                Debug.LogWarning("[DedicatedGameMessageRouter] Spawn message ignored. Room is empty.");
                return;
            }

            string envelopeJson = EnsureGameEnvelopeRoom(rawJson, roomId);
            if (string.IsNullOrWhiteSpace(envelopeJson)) return;

            int sentCount = BroadcastToRoom(roomId, envelopeJson, string.Empty);

            Debug.Log("[DedicatedGameMessageRouter] Spawn route broadcast | sentCount=" + sentCount +
                      " | roomId=" + roomId +
                      " | outgoingMessageFormat=" + MetaverseSpawnMessageCodec.ReadMessageFormat(envelopeJson) +
                      " | outgoingRoute=" + MetaverseSpawnMessageCodec.ReadRouteForLog(envelopeJson));
        }

        //* این تابع پیام آماده آر پی سی را از بریج می گیرد و برای کلاینت های هدف ارسال می کند.
        private void HandleRpcBridgeOutboundMessageReady(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson)) return;

            string roomId = ResolveBroadcastRoomId(rawJson);
            if (string.IsNullOrWhiteSpace(roomId))
            {
                Debug.LogWarning("[DedicatedGameMessageRouter] RPC message ignored. Room is empty.");
                return;
            }

            string envelopeJson = MetaverseNetworkRpcMessageCodec.EnsureGameEnvelopeRoom(rawJson, roomId);
            if (string.IsNullOrWhiteSpace(envelopeJson)) return;

            if (MetaverseNetworkRpcMessageCodec.TryReadTargetRpcPayload(envelopeJson, out MetaverseNetworkRpcPayload targetPayload))
            {
                int targetSent = SendTargetRpcToConnection(roomId, envelopeJson, targetPayload);
                Debug.Log("[DedicatedGameMessageRouter] TargetRpc route sent | sentCount=" + targetSent +
                          " | roomId=" + roomId +
                          " | targetConnectionId=" + SafeForLog(targetPayload != null ? targetPayload.targetConnectionId : string.Empty) +
                          " | outgoingMessageFormat=" + MetaverseNetworkRpcMessageCodec.ReadMessageFormat(envelopeJson) +
                          " | outgoingRoute=" + MetaverseNetworkRpcMessageCodec.ReadRouteForLog(envelopeJson));
                return;
            }

            int sentCount = BroadcastToRoom(roomId, envelopeJson, string.Empty);
            Debug.Log("[DedicatedGameMessageRouter] ClientRpc route broadcast | sentCount=" + sentCount +
                      " | roomId=" + roomId +
                      " | outgoingMessageFormat=" + MetaverseNetworkRpcMessageCodec.ReadMessageFormat(envelopeJson) +
                      " | outgoingRoute=" + MetaverseNetworkRpcMessageCodec.ReadRouteForLog(envelopeJson));
        }

        //* این تابع تارگت آر پی سی را فقط به کانکشن هدف همان روم می فرستد.
        private int SendTargetRpcToConnection(string roomId, string envelopeJson, MetaverseNetworkRpcPayload payload)
        {
            if (payload == null || string.IsNullOrWhiteSpace(envelopeJson)) return 0;

            string targetConnectionId = ResolveTargetConnectionId(payload);
            if (string.IsNullOrWhiteSpace(targetConnectionId)) return 0;

            ConcurrentDictionary<string, DedicatedWebSocketConnection> connections = GetConnections();
            if (connections == null || !connections.TryGetValue(targetConnectionId, out DedicatedWebSocketConnection connection)) return 0;
            if (connection == null || !connection.IsOpen) return 0;

            DedicatedPlayerSession targetSession = playerRegistry != null ? playerRegistry.GetByConnectionId(targetConnectionId) : null;
            if (targetSession == null) return 0;
            if (!string.Equals(targetSession.roomId, roomId, StringComparison.Ordinal)) return 0;

            _ = connection.SendTextAsync(envelopeJson);
            return 1;
        }

        //* این تابع کانکشن هدف آر پی سی را از خود پِیلود یا رجیستری پیدا می کند.
        private string ResolveTargetConnectionId(MetaverseNetworkRpcPayload payload)
        {
            if (payload == null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(payload.targetConnectionId)) return payload.targetConnectionId.Trim();

            if (playerRegistry == null) return string.Empty;
            System.Collections.Generic.List<DedicatedPlayerSession> sessions = playerRegistry.CreateSnapshot();
            if (sessions == null) return string.Empty;

            for (int i = 0; i < sessions.Count; i++)
            {
                DedicatedPlayerSession session = sessions[i];
                if (session == null) continue;

                if (!string.IsNullOrWhiteSpace(payload.targetUserId) &&
                    string.Equals(session.userId, payload.targetUserId.Trim(), StringComparison.Ordinal))
                {
                    return session.connectionId;
                }

                if (!string.IsNullOrWhiteSpace(payload.targetPlayerId) &&
                    string.Equals(session.playerId, payload.targetPlayerId.Trim(), StringComparison.Ordinal))
                {
                    return session.connectionId;
                }
            }

            return string.Empty;
        }

        //* این تابع بعد از ورود پلیر، اسنپ شات آبجکت های اسپاون شده را فقط برای همان کلاینت می فرستد.
        private void SendSpawnSnapshotToSession(DedicatedPlayerSession session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.connectionId)) return;
            if (spawnNetworkBridge == null || MetaverseSpawnManager.Instance == null) return;

            ConcurrentDictionary<string, DedicatedWebSocketConnection> connections = GetConnections();
            if (connections == null) return;

            if (!connections.TryGetValue(session.connectionId, out DedicatedWebSocketConnection connection)) return;
            if (connection == null || !connection.IsOpen) return;

            MetaverseSpawnPayload[] payloads = MetaverseSpawnManager.Instance.BuildSnapshotPayloads();
            string json = MetaverseSpawnMessageCodec.CreateSpawnSnapshotEnvelopeJson(payloads, session.roomId);
            if (string.IsNullOrWhiteSpace(json)) return;

            _ = connection.SendTextAsync(json);

            Debug.Log("[DedicatedGameMessageRouter] Spawn snapshot sent | connectionId=" + session.connectionId +
                      " | roomId=" + session.roomId +
                      " | count=" + (payloads != null ? payloads.Length : 0) +
                      " | outgoingMessageFormat=envelope | outgoingRoute=" +
                      RealtimeChannels.Game + "/" + RealtimeMessageTypes.Snapshot);
        }

        //* این تابع روم مناسب برای پخش پیام اسپاون را از اِنولوپ یا رجیستری پیدا می کند.
        private string ResolveBroadcastRoomId(string rawJson)
        {
            if (!string.IsNullOrWhiteSpace(rawJson))
            {
                RealtimeEnvelope envelope = RealtimeEnvelope.FromJson(rawJson);
                if (envelope != null && envelope.IsValidBasic() && !string.IsNullOrWhiteSpace(envelope.room))
                {
                    return envelope.room.Trim();
                }
            }

            return playerRegistry != null ? playerRegistry.GetPrimaryRoomId() : string.Empty;
        }

        //* این تابع پیام اسپاون را مطمئن می کند که داخل اِنولوپ گیم و روم درست قرار گرفته باشد.
        private string EnsureGameEnvelopeRoom(string rawJson, string roomId)
        {
            if (string.IsNullOrWhiteSpace(rawJson)) return string.Empty;

            RealtimeEnvelope envelope = RealtimeEnvelope.FromJson(rawJson);
            if (envelope != null && envelope.IsValidBasic())
            {
                envelope.room = string.IsNullOrWhiteSpace(roomId) ? envelope.room : roomId.Trim();
                envelope.EnsureDefaults();
                return envelope.ToJson();
            }

            if (!MetaverseSpawnMessageCodec.TryReadMessage(
                    rawJson,
                    out string messageType,
                    out MetaverseSpawnPayload spawnPayload,
                    out MetaverseDespawnPayload despawnPayload,
                    out MetaverseSpawnPayload[] snapshotPayloads))
            {
                return string.Empty;
            }

            if (messageType == RealtimeMessageTypes.Spawn && spawnPayload != null)
            {
                return MetaverseSpawnMessageCodec.CreateSpawnEnvelopeJson(spawnPayload, roomId);
            }

            if (messageType == RealtimeMessageTypes.Despawn && despawnPayload != null)
            {
                return MetaverseSpawnMessageCodec.CreateDespawnEnvelopeJson(despawnPayload.netId, despawnPayload.reason, roomId);
            }

            if (messageType == RealtimeMessageTypes.Snapshot || messageType == MetaverseDedicatedMessageTypes.LegacySpawnSnapshot)
            {
                return MetaverseSpawnMessageCodec.CreateSpawnSnapshotEnvelopeJson(snapshotPayloads, roomId);
            }

            return string.Empty;
        }

        //* این تابع هنگام قطع کانکشن، وضعیت ذخیره شده آن را پاک می کند.
        private void HandleClientDisconnected(DedicatedWebSocketConnection connection, string reason)
        {
            if (connection == null) return;

            dict_lastStateSequenceByConnectionId.TryRemove(connection.ConnectionId, out long _);

            if (playerStateStore == null) return;

            playerStateStore.RemoveByConnectionId(connection.ConnectionId, reason);
        }


        //* این تابع جلوی پردازش دوباره یک player_state با همان sequence را می گیرد.
        private bool ShouldIgnoreDuplicatePlayerState(DedicatedPlayerSession session, DedicatedPlayerStateMessageDto message)
        {
            if (!dedupePlayerStateMessages) return false;
            if (session == null || message == null) return false;
            if (string.IsNullOrWhiteSpace(session.connectionId)) return false;
            if (message.sequence <= 0) return false;

            string key = session.connectionId.Trim();

            if (dict_lastStateSequenceByConnectionId.TryGetValue(key, out long lastSequence) &&
                message.sequence <= lastSequence)
            {
                if (logDuplicatePlayerStateMessages)
                {
                    Debug.Log("[DedicatedGameMessageRouter] Duplicate player_state ignored | connectionId=" +
                              key + " | playerId=" + SafeForLog(session.playerId) +
                              " | sequence=" + message.sequence + " | lastSequence=" + lastSequence);
                }

                return true;
            }

            dict_lastStateSequenceByConnectionId[key] = message.sequence;
            return false;
        }

        //* این تابع پیام را به همه کانکشن های احراز شده همان روم ارسال می کند.
        private int BroadcastToRoom(string roomId, string json, string excludeConnectionId)
        {
            if (string.IsNullOrWhiteSpace(roomId)) return 0;

            ConcurrentDictionary<string, DedicatedWebSocketConnection> connections = GetConnections();

            if (connections == null || playerRegistry == null) return 0;

            int sentCount = 0;

            foreach (DedicatedWebSocketConnection targetConnection in connections.Values)
            {
                if (targetConnection == null || !targetConnection.IsOpen) continue;

                if (!string.IsNullOrWhiteSpace(excludeConnectionId) &&
                    targetConnection.ConnectionId == excludeConnectionId)
                {
                    continue;
                }

                DedicatedPlayerSession targetSession = playerRegistry.GetByConnectionId(targetConnection.ConnectionId);

                if (targetSession == null) continue;

                if (!string.Equals(targetSession.roomId, roomId, StringComparison.Ordinal))
                {
                    continue;
                }

                _ = targetConnection.SendTextAsync(json);
                sentCount++;
            }

            return sentCount;
        }

        //* این تابع دیکشنری کانکشن های وب سوکت سرور را با رفلکشن می خواند.
        private ConcurrentDictionary<string, DedicatedWebSocketConnection> GetConnections()
        {
            if (webSocketServer == null || connectionsField == null) return null;

            try
            {
                return connectionsField.GetValue(webSocketServer) as ConcurrentDictionary<string, DedicatedWebSocketConnection>;
            }
            catch (Exception ex)
            {
                Debug.LogError("[DedicatedGameMessageRouter] Could not read websocket connections | " + ex.Message);
                return null;
            }
        }

        //* این تابع پیام خطای گیم را برای همان کانکشن ارسال می کند.
        private async Task SendErrorAsync(DedicatedWebSocketConnection connection, string reason, string messageText)
        {
            if (connection == null) return;

            DedicatedGameErrorDto error = new DedicatedGameErrorDto
            {
                type = "game_error",
                ok = false,
                reason = string.IsNullOrWhiteSpace(reason) ? "game_error" : reason,
                message = string.IsNullOrWhiteSpace(messageText) ? "Game message failed." : messageText
            };

            await connection.SendTextAsync(DedicatedRealtimeEnvelopeCodec.WrapSystemPayload(RealtimeMessageTypes.Error, JsonUtility.ToJson(error)));

            Debug.LogWarning("[DedicatedGameMessageRouter] Game error sent | connectionId=" +
                             connection.ConnectionId + " | reason=" + error.reason +
                             " | outgoingMessageFormat=envelope | outgoingRoute=" +
                             RealtimeChannels.System + "/" + RealtimeMessageTypes.Error);
        }

        //* این تابع متن امن برای لاگ می سازد.
        private string SafeForLog(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        //* این تابع تایپ پیام ورودی را از اِنولوپ یا پیام قدیمی خام می خواند.
        private DedicatedMessageTypeDto ParseMessageType(string text)
        {
            string messageType = DedicatedRealtimeEnvelopeCodec.ReadMessageType(text);
            return string.IsNullOrWhiteSpace(messageType) ? null : new DedicatedMessageTypeDto { type = messageType };
        }

        //* این تابع بررسی می کند پیام ورودی، وضعیت پلیر از مسیر استاندارد یا قدیمی است یا نه.
        private bool IsPlayerStateMessage(string text, string messageType)
        {
            if (string.Equals(messageType, RealtimeMessageTypes.PlayerState, StringComparison.Ordinal)) return true;
            if (string.Equals(messageType, "player_state", StringComparison.Ordinal)) return true;
            return DedicatedRealtimeEnvelopeCodec.Matches(text, RealtimeChannels.Presence, RealtimeMessageTypes.PlayerState);
        }

        //* این تابع بررسی می کند پیام ورودی یک کامند شبکه ای است یا نه.
        private bool IsNetworkCommandRouteMessage(string text, string messageType)
        {
            if (string.Equals(messageType, RealtimeMessageTypes.Command, StringComparison.Ordinal)) return true;
            return MetaverseNetworkRpcMessageCodec.IsCommandEnvelope(text);
        }

        //* این تابع بررسی می کند پیام ورودی از نوع آر پی سی سرور به کلاینت است یا نه.
        private bool IsNetworkRpcRouteMessage(string text, string messageType)
        {
            if (string.Equals(messageType, RealtimeMessageTypes.ClientRpc, StringComparison.Ordinal)) return true;
            if (string.Equals(messageType, RealtimeMessageTypes.TargetRpc, StringComparison.Ordinal)) return true;
            return MetaverseNetworkRpcMessageCodec.IsClientRpcEnvelope(text) ||
                   MetaverseNetworkRpcMessageCodec.IsTargetRpcEnvelope(text);
        }

        //* این تابع بررسی می کند پیام ورودی مربوط به اسپاون یا دیسپاون است یا نه.
        private bool IsSpawnRouteMessage(string text, string messageType)
        {
            if (string.Equals(messageType, RealtimeMessageTypes.Spawn, StringComparison.Ordinal)) return true;
            if (string.Equals(messageType, RealtimeMessageTypes.Despawn, StringComparison.Ordinal)) return true;
            if (string.Equals(messageType, RealtimeMessageTypes.Snapshot, StringComparison.Ordinal)) return true;
            if (string.Equals(messageType, MetaverseDedicatedMessageTypes.LegacySpawnSnapshot, StringComparison.Ordinal)) return true;
            if (MetaverseSpawnMessageCodec.IsRealtimeSpawnEnvelope(text)) return true;
            if (MetaverseSpawnMessageCodec.IsRealtimeDespawnEnvelope(text)) return true;
            if (MetaverseSpawnMessageCodec.IsRealtimeSpawnSnapshotEnvelope(text)) return true;
            return false;
        }

        //* این تابع پیام player_state را از پِیلود اِنولوپ یا پیام قدیمی خام می خواند.
        private DedicatedPlayerStateMessageDto ParsePlayerStateMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            try
            {
                string payloadJson = DedicatedRealtimeEnvelopeCodec.ReadPayloadOrRawJson(text);
                return JsonUtility.FromJson<DedicatedPlayerStateMessageDto>(payloadJson);
            }
            catch (Exception ex)
            {
                Debug.LogError("[DedicatedGameMessageRouter] Player state parse failed | " + ex.Message);
                return null;
            }
        }

        //* این تابع پیام پرزنس خروجی را داخل اِنولوپ استاندارد قرار می دهد.
        private string WrapPresenceEnvelope(string messageType, string payloadJson, string roomId)
        {
            return DedicatedRealtimeEnvelopeCodec.WrapPresencePayload(messageType, payloadJson, roomId);
        }

        //* این تابع زمان فعلی یونیکس میلی ثانیه را برمی گرداند.
        private long NowUnixMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        //* این تابع مقدار بول را به متن خوانا تبدیل می کند.
        private string BoolText(bool value)
        {
            return value ? "OK" : "MISSING";
        }

        /*
        توضیح مکتوب فایل:
        این اسکریپت مسیر پیام های گیم بعد از auth_ok را داخل یونیتی ددیکیتد سرور مدیریت می کند.
        فعلاً پیام player_state را می خواند، با session احراز شده تطبیق می دهد و آخرین وضعیت را ذخیره می کند.
        سپس player_state را برای بقیه کلاینت های احراز شده همان روم پخش می کند.
        برای تست تک کلاینت، پیام player_state_accepted به همان فرستنده برمی گردد.
        این فایل با GameServerClient قدیمی هیچ تداخلی ندارد.
        */
    }

    [Serializable]
    public class DedicatedPlayerStateMessageDto
    {
        public string type;
        public string userId;
        public string playerId;
        public string roomId;
        public string serverId;
        public string sessionId;
        public long sequence;
        public long timestampUnixMs;

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
    }

    [Serializable]
    public class DedicatedPlayerStateBroadcastDto
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

        //* این تابع پیام قابل پخش player_state را از رکورد ذخیره شده می سازد.
        public static DedicatedPlayerStateBroadcastDto FromRecord(DedicatedPlayerStateRecord record)
        {
            return new DedicatedPlayerStateBroadcastDto
            {
                type = "player_state",
                userId = record.userId,
                playerId = record.playerId,
                userName = record.userName,
                connectionId = record.connectionId,
                roomId = record.roomId,
                serverId = record.serverId,
                sessionId = record.sessionId,

                sequence = record.sequence,
                clientTimestampUnixMs = record.clientTimestampUnixMs,
                serverTimestampUnixMs = record.serverTimestampUnixMs,

                px = record.px,
                py = record.py,
                pz = record.pz,

                rx = record.rx,
                ry = record.ry,
                rz = record.rz,
                rw = record.rw,

                vx = record.vx,
                vy = record.vy,
                vz = record.vz
            };
        }
    }

    [Serializable]
    public class DedicatedPlayerStateAcceptedDto
    {
        public string type;
        public bool ok;
        public long sequence;
        public string roomId;
        public string playerId;
        public long serverTimeUnixMs;
        public int broadcastCount;
    }

    [Serializable]
    public class DedicatedPresenceEventDto
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
    }

    [Serializable]
    public class DedicatedGameErrorDto
    {
        public string type;
        public bool ok;
        public string reason;
        public string message;
    }
}
