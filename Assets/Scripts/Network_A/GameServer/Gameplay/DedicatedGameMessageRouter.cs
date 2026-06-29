using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;
using Network_A.GameServer.Players;
using Network_A.GameServer.Protocol;
using Network_A.GameServer.WebSocket;
using UnityEngine;

namespace Network_A.GameServer.Gameplay
{
    public class DedicatedGameMessageRouter : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private DedicatedWebSocketServer webSocketServer;
        [SerializeField] private DedicatedPlayerRegistry playerRegistry;
        [SerializeField] private DedicatedPlayerStateStore playerStateStore;

        [Header("Rules")]
        [SerializeField] private bool sendStateAckToSender = true;
        [SerializeField] private bool broadcastStateToSender = false;
        [SerializeField] private bool broadcastPresenceEvents = true;
        [SerializeField] private bool dedupePlayerStateMessages = true;
        [SerializeField] private bool logDuplicatePlayerStateMessages = false;
        [SerializeField] private bool logPlayerStateMessages = true;

        private FieldInfo connectionsField;
        private bool eventsSubscribed;
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
                      " | playerStateStore=" + BoolText(playerStateStore != null));
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

            DedicatedMessageTypeDto typeDto = ParseMessageType(text);

            if (typeDto == null || string.IsNullOrWhiteSpace(typeDto.type))
            {
                return;
            }

            if (typeDto.type == "auth_ticket")
            {
                return;
            }

            bool isAuthenticated = playerRegistry.IsConnectionAuthenticated(connection.ConnectionId);

            if (!isAuthenticated)
            {
                return;
            }

            if (typeDto.type == "player_state")
            {
                await HandlePlayerStateAsync(connection, text);
                return;
            }
        }

        //* این تابع پیام player_state را پردازش، ذخیره و برای بقیه پلیرهای همان روم پخش می کند.
        private async Task HandlePlayerStateAsync(DedicatedWebSocketConnection connection, string text)
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
            string broadcastJson = JsonUtility.ToJson(broadcast);

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

                await connection.SendTextAsync(JsonUtility.ToJson(accepted));
            }

            if (logPlayerStateMessages)
            {
                Debug.Log("[DedicatedGameMessageRouter] Player state handled | userId=" +
                          session.userId + " | roomId=" + session.roomId +
                          " | sequence=" + record.sequence + " | broadcastCount=" + sentCount);
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

            int sentCount = BroadcastToRoom(session.roomId, JsonUtility.ToJson(evt), session.connectionId);

            Debug.Log("[DedicatedGameMessageRouter] Player joined broadcast | userId=" +
                      session.userId + " | sentCount=" + sentCount);
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

            int sentCount = BroadcastToRoom(session.roomId, JsonUtility.ToJson(evt), session.connectionId);

            Debug.Log("[DedicatedGameMessageRouter] Player left broadcast | userId=" +
                      session.userId + " | reason=" + reason + " | sentCount=" + sentCount);
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

            await connection.SendTextAsync(JsonUtility.ToJson(error));

            Debug.LogWarning("[DedicatedGameMessageRouter] Game error sent | connectionId=" +
                             connection.ConnectionId + " | reason=" + error.reason);
        }

        //* این تابع متن امن برای لاگ می سازد.
        private string SafeForLog(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        //* این تابع تایپ پیام ورودی را از جیسون می خواند.
        private DedicatedMessageTypeDto ParseMessageType(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            try
            {
                return JsonUtility.FromJson<DedicatedMessageTypeDto>(text);
            }
            catch
            {
                return null;
            }
        }

        //* این تابع پیام player_state را از جیسون می خواند.
        private DedicatedPlayerStateMessageDto ParsePlayerStateMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            try
            {
                return JsonUtility.FromJson<DedicatedPlayerStateMessageDto>(text);
            }
            catch (Exception ex)
            {
                Debug.LogError("[DedicatedGameMessageRouter] Player state parse failed | " + ex.Message);
                return null;
            }
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
