using System;
using System.Collections.Generic;
using Network_A.GameServer.Auth;
using Network_A.GameServer.Protocol;
using Network_A.GameServer.WebSocket;
using UnityEngine;

namespace Network_A.GameServer.Players
{
    public class DedicatedPlayerRegistry : MonoBehaviour
    {
        private readonly object syncLock = new object();
        [Header("WebSocket Cleanup")]
        [SerializeField] private DedicatedWebSocketServer webSocketServer;

        private readonly Dictionary<string, DedicatedPlayerSession> dict_playersByConnectionId =
            new Dictionary<string, DedicatedPlayerSession>();

        private readonly Dictionary<string, string> dict_connectionIdByUserId =
            new Dictionary<string, string>();

        private readonly Dictionary<string, DedicatedRoomContext> dict_roomContextByRoomId =
            new Dictionary<string, DedicatedRoomContext>();

        private string activeRoomId = string.Empty;

        public int CurrentPlayerCount
        {
            get
            {
                lock (syncLock)
                {
                    return dict_playersByConnectionId.Count;
                }
            }
        }

        public event Action<DedicatedPlayerSession> PlayerRegistered;
        public event Action<DedicatedPlayerSession, string> PlayerRemoved;

        public int UniqueUserCount
        {
            get
            {
                lock (syncLock)
                {
                    return dict_connectionIdByUserId.Count;
                }
            }
        }

        public bool HasAnyAuthenticatedPlayer => CurrentPlayerCount > 0;

        //* این تابع رفرنس وب سوکت سرور را در شروع آبجکت پیدا می کند.
        private void Awake()
        {
            EnsureWebSocketServerReference();
        }
        //* این تابع پلیر تأیید شده را داخل رجیستری ددیکیتد سرور ثبت می کند.
        public bool TryRegisterVerifiedPlayer(
            DedicatedWebSocketConnection connection,
            DedicatedVerifyTicketResult verifyResult,
            out DedicatedPlayerSession session,
            out string error)
        {
            session = null;

            if (connection == null)
            {
                error = "Connection is missing.";
                return false;
            }

            if (verifyResult == null || !verifyResult.IsSuccess || verifyResult.Request == null)
            {
                error = "Verify result is invalid.";
                return false;
            }

            DedicatedVerifyTicketRequestDto request = verifyResult.Request;
            long now = NowUnixMs();

            DedicatedPlayerSession verifiedSession = new DedicatedPlayerSession
            {
                connectionId = connection.ConnectionId,
                remoteEndPoint = connection.RemoteEndPoint,
                userId = SafeTrim(request.userId),
                playerId = SafeValue(request.playerId, request.userId),
                userName = SafeValue(request.userName, request.userId),
                roomId = SafeTrim(request.roomId),
                serverId = SafeTrim(request.serverId),
                sessionId = SafeTrim(request.sessionId),
                joinedAtUnixMs = now,
                lastSeenAtUnixMs = now,
                isReady = true,
                isAuthenticated = true
            };

            if (!ValidateSession(verifiedSession, out error)) return false;

            DedicatedPlayerSession duplicateSocketSession = null;
            bool reconnectRebound = false;
            string previousConnectionId = string.Empty;

            lock (syncLock)
            {
                string requestedRoomId = SafeTrim(verifiedSession.roomId);
                string roomUserKey = BuildRoomUserKey(requestedRoomId, verifiedSession.userId);

                if (string.IsNullOrWhiteSpace(activeRoomId))
                {
                    activeRoomId = requestedRoomId;
                    Debug.Log("[DedicatedPlayerRegistry] Warm server bound to room | roomId=" + activeRoomId);
                }

                if (dict_connectionIdByUserId.TryGetValue(roomUserKey, out string mappedConnectionId))
                {
                    previousConnectionId = SafeTrim(mappedConnectionId);

                    if (dict_playersByConnectionId.TryGetValue(
                            mappedConnectionId,
                            out DedicatedPlayerSession existingSession) &&
                        existingSession != null)
                    {
                        if (string.IsNullOrWhiteSpace(previousConnectionId))
                        {
                            previousConnectionId = SafeTrim(existingSession.connectionId);
                        }

                        if (!string.IsNullOrWhiteSpace(previousConnectionId))
                        {
                            dict_playersByConnectionId.Remove(previousConnectionId);
                            RemoveConnectionFromRoomContextUnsafe(existingSession.roomId, previousConnectionId);
                        }

                        if (!string.IsNullOrWhiteSpace(previousConnectionId) &&
                            !string.Equals(
                                previousConnectionId,
                                SafeTrim(verifiedSession.connectionId),
                                StringComparison.Ordinal))
                        {
                            duplicateSocketSession = new DedicatedPlayerSession
                            {
                                connectionId = previousConnectionId,
                                userId = existingSession.userId,
                                playerId = existingSession.playerId,
                                roomId = existingSession.roomId
                            };
                        }

                        existingSession.RebindVerifiedConnection(verifiedSession, now);
                        session = existingSession;
                        reconnectRebound = true;
                    }
                    else
                    {
                        RemoveConnectionFromRoomContextUnsafe(requestedRoomId, previousConnectionId);
                    }

                    dict_connectionIdByUserId.Remove(roomUserKey);
                }

                if (session == null) session = verifiedSession;

                // Preserve the distinction between a fresh join and a websocket reconnect.
                // Subscribers still receive PlayerRegistered so authority/object/state rebind paths keep working,
                // but presence broadcasters can suppress a false player_joined event.
                session.wasReconnectRebound = reconnectRebound;

                dict_playersByConnectionId[session.connectionId] = session;
                dict_connectionIdByUserId[roomUserKey] = session.connectionId;
                AddSessionToRoomContextUnsafe(session);
            }

            if (duplicateSocketSession != null)
            {
                CloseRemovedDuplicateConnection(duplicateSocketSession, "duplicate_user_replaced");

                Debug.LogWarning(
                    "[DedicatedPlayerRegistry] Existing session rebound | userId=" +
                    session.userId +
                    " | oldConnectionId=" + previousConnectionId +
                    " | newConnectionId=" + session.connectionId +
                    " | sameSessionReference=YES | playerRemovedEvent=NO"
                );
            }

            Debug.Log(
                "[DedicatedPlayerRegistry] Player registered | userId=" +
                session.userId +
                " | connectionId=" + session.connectionId +
                " | roomId=" + session.roomId +
                " | activeRoomId=" + GetPrimaryRoomId() +
                " | reconnectRebound=" + reconnectRebound +
                " | count=" + CurrentPlayerCount
            );

            PlayerRegistered?.Invoke(session);

            error = string.Empty;
            return true;
        }

        //* این تابع پلیر را با کانکشن آی دی از رجیستری حذف می کند.
        public bool RemoveByConnectionId(string connectionId, string reason)
        {
            if (string.IsNullOrWhiteSpace(connectionId)) return false;

            DedicatedPlayerSession removed = null;

            lock (syncLock)
            {
                if (!dict_playersByConnectionId.TryGetValue(connectionId, out removed))
                {
                    return false;
                }

                dict_playersByConnectionId.Remove(connectionId);
                RemoveSessionIndexesUnsafe(removed);
                RefreshPrimaryRoomIdUnsafe();
            }

            Debug.Log("[DedicatedPlayerRegistry] Player removed | userId=" +
                      removed.userId + " | connectionId=" + removed.connectionId +
                      " | reason=" + reason + " | count=" + CurrentPlayerCount);

            PlayerRemoved?.Invoke(removed, reason);

            return true;
        }

        //* این تابع بررسی می کند که یک کانکشن قبلاً احراز شده است یا نه.
        public bool IsConnectionAuthenticated(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId)) return false;

            lock (syncLock)
            {
                return dict_playersByConnectionId.ContainsKey(connectionId);
            }
        }

        //* این تابع پلیر را با کانکشن آی دی برمی گرداند.
        public DedicatedPlayerSession GetByConnectionId(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId)) return null;

            lock (syncLock)
            {
                return dict_playersByConnectionId.TryGetValue(connectionId, out DedicatedPlayerSession session)
                    ? session
                    : null;
            }
        }

        //* این تابع تعداد پلیرهای فعلی را برای هارت بیت برمی گرداند.
        public int GetCurrentPlayerCount()
        {
            return CurrentPlayerCount;
        }


        public string GetPrimaryRoomId()
        {
            lock (syncLock)
            {
                if (!string.IsNullOrWhiteSpace(activeRoomId)) return activeRoomId.Trim();

                foreach (DedicatedPlayerSession session in dict_playersByConnectionId.Values)
                {
                    if (session == null) continue;
                    if (!string.IsNullOrWhiteSpace(session.roomId)) return session.roomId.Trim();
                }
            }

            return string.Empty;
        }

        public string GetPrimaryServerId()
        {
            lock (syncLock)
            {
                foreach (DedicatedPlayerSession session in dict_playersByConnectionId.Values)
                {
                    if (session == null) continue;
                    if (!string.IsNullOrWhiteSpace(session.serverId)) return session.serverId.Trim();
                }
            }

            return string.Empty;
        }

        public bool HasAnyPlayerInAnotherRoom(string roomId)
        {
            string safeRoomId = SafeTrim(roomId);
            if (string.IsNullOrWhiteSpace(safeRoomId)) return false;

            lock (syncLock)
            {
                string currentActiveRoomId = SafeTrim(activeRoomId);

                if (!string.IsNullOrWhiteSpace(currentActiveRoomId) &&
                    !string.Equals(currentActiveRoomId, safeRoomId, StringComparison.Ordinal))
                {
                    return true;
                }

                foreach (DedicatedPlayerSession session in dict_playersByConnectionId.Values)
                {
                    if (session == null) continue;
                    if (string.IsNullOrWhiteSpace(session.roomId)) continue;
                    if (!string.Equals(session.roomId.Trim(), safeRoomId, StringComparison.Ordinal)) return true;
                }
            }

            return false;
        }

        //* این تابع زمان آخرین فعالیت یک کانکشن احراز شده را به روز می کند.
        public void TouchConnection(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId)) return;

            lock (syncLock)
            {
                if (dict_playersByConnectionId.TryGetValue(connectionId, out DedicatedPlayerSession session))
                {
                    session.Touch(NowUnixMs());
                }
            }
        }

        //* این تابع یک اسنپ شات از همه پلیرهای ثبت شده می سازد.
        public List<DedicatedPlayerSession> CreateSnapshot()
        {
            lock (syncLock)
            {
                return new List<DedicatedPlayerSession>(dict_playersByConnectionId.Values);
            }
        }

        //* این تابع کل رجیستری را پاک می کند.
        public void ClearAll(string reason)
        {
            List<DedicatedPlayerSession> removedPlayers;

            lock (syncLock)
            {
                removedPlayers = new List<DedicatedPlayerSession>(dict_playersByConnectionId.Values);
                dict_playersByConnectionId.Clear();
                dict_connectionIdByUserId.Clear();
                dict_roomContextByRoomId.Clear();
                activeRoomId = string.Empty;
            }

            for (int i = 0; i < removedPlayers.Count; i++)
            {
                PlayerRemoved?.Invoke(removedPlayers[i], reason);
            }

            Debug.Log("[DedicatedPlayerRegistry] Cleared | reason=" + reason);
        }

        //* این تابع کانکشن قدیمی یوزر تکراری را از وب سوکت سرور می بندد.
        private void CloseRemovedDuplicateConnection(DedicatedPlayerSession removedDuplicate, string reason)
        {
            if (removedDuplicate == null) return;

            string oldConnectionId = SafeTrim(removedDuplicate.connectionId);
            if (string.IsNullOrWhiteSpace(oldConnectionId)) return;

            EnsureWebSocketServerReference();

            if (webSocketServer == null)
            {
                Debug.LogWarning("[DedicatedPlayerRegistry] Duplicate socket close skipped | reason=web_socket_server_missing | oldConnectionId=" +
                                 oldConnectionId);
                return;
            }

            bool closed = webSocketServer.CloseConnectionById(oldConnectionId, reason);

            Debug.Log("[DedicatedPlayerRegistry] Duplicate socket close requested | oldConnectionId=" +
                      oldConnectionId + " | closed=" + closed + " | reason=" + reason);
        }

        //* این تابع رفرنس وب سوکت سرور را از صحنه پیدا می کند.
        private void EnsureWebSocketServerReference()
        {
            if (webSocketServer != null) return;

            webSocketServer = GetComponent<DedicatedWebSocketServer>();
            if (webSocketServer != null) return;

#if UNITY_2023_1_OR_NEWER
            webSocketServer = UnityEngine.Object.FindFirstObjectByType<DedicatedWebSocketServer>();
#else
            webSocketServer = UnityEngine.Object.FindObjectOfType<DedicatedWebSocketServer>();
#endif
        }
        //* این تابع مدل پلیر را قبل از ثبت اعتبارسنجی می کند.
        private bool ValidateSession(DedicatedPlayerSession session, out string error)
        {
            if (session == null)
            {
                error = "Session is missing.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(session.connectionId))
            {
                error = "Connection id is missing.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(session.userId))
            {
                error = "User id is missing.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(session.playerId))
            {
                error = "Player id is missing.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(session.roomId))
            {
                error = "Room id is missing.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(session.serverId))
            {
                error = "Server id is missing.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(session.sessionId))
            {
                error = "Session id is missing.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        //* این تابع زمان فعلی را به میلی ثانیه یونیکس تبدیل می کند.
        private long NowUnixMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        //* این تابع اگر مقدار اول خالی باشد مقدار جایگزین را برمی گرداند.
        private string SafeValue(string value, string fallback)
        {
            string safe = SafeTrim(value);
            if (!string.IsNullOrWhiteSpace(safe)) return safe;

            return SafeTrim(fallback);
        }

        //* این تابع مقدار رشته را بدون نال و فاصله اضافه برمی گرداند.
        private string SafeTrim(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }


        //* این تابع تلاش می کند سشن را با کانکشن آی دی برگرداند.
        public bool TryGetByConnectionId(string connectionId, out DedicatedPlayerSession session)
        {
            session = GetByConnectionId(connectionId);
            return session != null;
        }

        //* این تابع سشن را با یوزر آی دی برمی گرداند.
        public DedicatedPlayerSession GetByUserId(string userId)
        {
            string safeUserId = SafeTrim(userId);
            if (string.IsNullOrWhiteSpace(safeUserId)) return null;

            lock (syncLock)
            {
                foreach (DedicatedPlayerSession session in dict_playersByConnectionId.Values)
                {
                    if (session == null) continue;
                    if (string.Equals(SafeTrim(session.userId), safeUserId, StringComparison.Ordinal)) return session;
                }
            }

            return null;
        }

        //* این تابع سشن را با روم آی دی و یوزر آی دی برمی گرداند.
        public DedicatedPlayerSession GetByUserIdInRoom(string roomId, string userId)
        {
            string safeRoomId = SafeTrim(roomId);
            string safeUserId = SafeTrim(userId);
            if (string.IsNullOrWhiteSpace(safeRoomId) || string.IsNullOrWhiteSpace(safeUserId)) return null;

            lock (syncLock)
            {
                string roomUserKey = BuildRoomUserKey(safeRoomId, safeUserId);
                if (!dict_connectionIdByUserId.TryGetValue(roomUserKey, out string connectionId)) return null;
                return dict_playersByConnectionId.TryGetValue(connectionId, out DedicatedPlayerSession session) ? session : null;
            }
        }

        //* این تابع تلاش می کند سشن را با یوزر آی دی برگرداند.
        public bool TryGetByUserId(string userId, out DedicatedPlayerSession session)
        {
            session = GetByUserId(userId);
            return session != null;
        }

        //* این تابع سشن را با پلیر آی دی پیدا می کند.
        public DedicatedPlayerSession GetByPlayerId(string playerId)
        {
            string safePlayerId = SafeTrim(playerId);
            if (string.IsNullOrWhiteSpace(safePlayerId)) return null;

            lock (syncLock)
            {
                foreach (DedicatedPlayerSession session in dict_playersByConnectionId.Values)
                {
                    if (session == null) continue;
                    if (string.Equals(SafeTrim(session.playerId), safePlayerId, StringComparison.Ordinal)) return session;
                }
            }

            return null;
        }

        //* این تابع تلاش می کند سشن را با پلیر آی دی برگرداند.
        public bool TryGetByPlayerId(string playerId, out DedicatedPlayerSession session)
        {
            session = GetByPlayerId(playerId);
            return session != null;
        }

        //* این تابع سشن را با سشن آی دی پیدا می کند.
        public DedicatedPlayerSession GetBySessionId(string sessionId)
        {
            string safeSessionId = SafeTrim(sessionId);
            if (string.IsNullOrWhiteSpace(safeSessionId)) return null;

            lock (syncLock)
            {
                foreach (DedicatedPlayerSession session in dict_playersByConnectionId.Values)
                {
                    if (session == null) continue;
                    if (string.Equals(SafeTrim(session.sessionId), safeSessionId, StringComparison.Ordinal)) return session;
                }
            }

            return null;
        }

        //* این تابع سشن های یک روم را برای مسیرهای اسپاون و سینک می سازد.
        public List<DedicatedPlayerSession> CreateRoomSnapshot(string roomId)
        {
            string safeRoomId = SafeTrim(roomId);
            List<DedicatedPlayerSession> result = new List<DedicatedPlayerSession>();
            if (string.IsNullOrWhiteSpace(safeRoomId)) return result;

            lock (syncLock)
            {
                foreach (DedicatedPlayerSession session in dict_playersByConnectionId.Values)
                {
                    if (session == null) continue;
                    if (string.Equals(SafeTrim(session.roomId), safeRoomId, StringComparison.Ordinal)) result.Add(session);
                }
            }

            return result;
        }

        //* این تابع فقط سشن های آماده و آث شده را برای مسیر شبیه میرور برمی گرداند.
        public List<DedicatedPlayerSession> CreateMirrorLikeGameplaySnapshot(string roomId = "")
        {
            string safeRoomId = SafeTrim(roomId);
            List<DedicatedPlayerSession> result = new List<DedicatedPlayerSession>();

            lock (syncLock)
            {
                foreach (DedicatedPlayerSession session in dict_playersByConnectionId.Values)
                {
                    if (session == null) continue;
                    if (!session.IsMirrorLikeReady) continue;
                    if (!string.IsNullOrWhiteSpace(safeRoomId) && !string.Equals(SafeTrim(session.roomId), safeRoomId, StringComparison.Ordinal)) continue;
                    result.Add(session);
                }
            }

            return result;
        }

        //* این تابع تعداد پلیرهای آث شده داخل یک روم را برمی گرداند.
        public int GetAuthenticatedPlayerCountInRoom(string roomId)
        {
            return CreateMirrorLikeGameplaySnapshot(roomId).Count;
        }

        //* این تابع کانکشن آی دی را با یوزر آی دی پیدا می کند.
        public bool TryGetConnectionIdByUserId(string userId, out string connectionId)
        {
            connectionId = string.Empty;
            DedicatedPlayerSession session = GetByUserId(userId);
            if (session == null) return false;
            connectionId = SafeTrim(session.connectionId);
            return !string.IsNullOrWhiteSpace(connectionId);
        }

        //* این تابع کانکشن آی دی را با روم آی دی و یوزر آی دی پیدا می کند.
        public bool TryGetConnectionIdByUserIdInRoom(string roomId, string userId, out string connectionId)
        {
            connectionId = string.Empty;
            DedicatedPlayerSession session = GetByUserIdInRoom(roomId, userId);
            if (session == null) return false;
            connectionId = SafeTrim(session.connectionId);
            return !string.IsNullOrWhiteSpace(connectionId);
        }

        //* این تابع بررسی می کند که کانکشن داخل روم مورد نظر است یا نه.
        public bool IsConnectionInRoom(string connectionId, string roomId)
        {
            DedicatedPlayerSession session = GetByConnectionId(connectionId);
            return session != null && session.IsRoom(roomId);
        }

        //* این تابع بررسی می کند که دو کانکشن داخل یک روم هستند یا نه.
        public bool AreConnectionsInSameRoom(string firstConnectionId, string secondConnectionId)
        {
            DedicatedPlayerSession first = GetByConnectionId(firstConnectionId);
            DedicatedPlayerSession second = GetByConnectionId(secondConnectionId);
            if (first == null || second == null) return false;
            return first.IsRoom(second.roomId);
        }

        //* این تابع پلیر را با یوزر آی دی حذف می کند.
        public bool RemoveByUserId(string userId, string reason)
        {
            if (!TryGetConnectionIdByUserId(userId, out string connectionId)) return false;
            return RemoveByConnectionId(connectionId, reason);
        }

        //* این تابع پلیر را با روم آی دی و یوزر آی دی حذف می کند.
        public bool RemoveByUserIdInRoom(string roomId, string userId, string reason)
        {
            if (!TryGetConnectionIdByUserIdInRoom(roomId, userId, out string connectionId)) return false;
            return RemoveByConnectionId(connectionId, reason);
        }

        //* این تابع پلیر را با پلیر آی دی حذف می کند.
        public bool RemoveByPlayerId(string playerId, string reason)
        {
            DedicatedPlayerSession session = GetByPlayerId(playerId);
            if (session == null) return false;
            return RemoveByConnectionId(session.connectionId, reason);
        }

        //* این تابع خلاصه وضعیت رجیستری را برای تست فاز سی و سه آ برمی گرداند.
        public string GetMirrorLikeRegistryDebugSummary()
        {
            long now = NowUnixMs();
            List<DedicatedPlayerSession> snapshot = CreateSnapshot();
            int readyCount = 0;
            int authenticatedCount = 0;

            for (int i = 0; i < snapshot.Count; i++)
            {
                DedicatedPlayerSession session = snapshot[i];
                if (session == null) continue;
                if (session.isReady) readyCount++;
                if (session.isAuthenticated) authenticatedCount++;
            }

            return "phase=33A" +
                   " | mirrorRoute=PlayerRegistry" +
                   " | players=" + snapshot.Count +
                   " | ready=" + readyCount +
                   " | authenticated=" + authenticatedCount +
                   " | uniqueUsers=" + UniqueUserCount +
                   " | primaryRoomId=" + GetPrimaryRoomId() +
                   " | primaryServerId=" + GetPrimaryServerId() +
                   " | now=" + now;
        }


        //* این تابع تعداد روم های فعال داخل ددیکیتد سرور را برمی گرداند.
        public int GetActiveRoomCount()
        {
            lock (syncLock)
            {
                return dict_roomContextByRoomId.Count;
            }
        }

        //* این تابع شناسه روم های فعال را برای هارت بیت مولتی روم برمی گرداند.
        public List<string> CreateActiveRoomIdSnapshot()
        {
            List<string> result = new List<string>();

            lock (syncLock)
            {
                foreach (string roomId in dict_roomContextByRoomId.Keys)
                {
                    string safeRoomId = SafeTrim(roomId);
                    if (!string.IsNullOrWhiteSpace(safeRoomId)) result.Add(safeRoomId);
                }
            }

            return result;
        }

        //* این تابع تعداد پلیرهای فعلی یک روم را برای گزارش هارت بیت برمی گرداند.
        public int GetCurrentPlayerCountInRoom(string roomId)
        {
            string safeRoomId = SafeTrim(roomId);
            if (string.IsNullOrWhiteSpace(safeRoomId)) return 0;

            int count = 0;

            lock (syncLock)
            {
                foreach (DedicatedPlayerSession session in dict_playersByConnectionId.Values)
                {
                    if (session == null) continue;
                    if (string.Equals(SafeTrim(session.roomId), safeRoomId, StringComparison.Ordinal)) count++;
                }
            }

            return count;
        }

        //* این تابع کلید داخلی یوزر داخل روم را می سازد.
        private string BuildRoomUserKey(string roomId, string userId)
        {
            return SafeTrim(roomId) + "::" + SafeTrim(userId);
        }

        //* این تابع ایندکس های داخلی یک سشن را پاک می کند.
        private void RemoveSessionIndexesUnsafe(DedicatedPlayerSession session)
        {
            if (session == null) return;

            string roomUserKey = BuildRoomUserKey(session.roomId, session.userId);
            if (!string.IsNullOrWhiteSpace(roomUserKey)) dict_connectionIdByUserId.Remove(roomUserKey);

            string safeRoomId = SafeTrim(session.roomId);
            if (!dict_roomContextByRoomId.TryGetValue(safeRoomId, out DedicatedRoomContext roomContext) || roomContext == null) return;

            roomContext.connectionIds.Remove(SafeTrim(session.connectionId));
            roomContext.userIds.Remove(SafeTrim(session.userId));
            roomContext.playerIds.Remove(SafeTrim(session.playerId));
            roomContext.lastUpdatedUnixMs = NowUnixMs();

            if (roomContext.connectionIds.Count <= 0)
            {
                dict_roomContextByRoomId.Remove(safeRoomId);
            }
        }
        //* این تابع فقط کانکشن قدیمی را هنگام ریبایند از کانتکست روم حذف می کند.
        private void RemoveConnectionFromRoomContextUnsafe(string roomId, string connectionId)
        {
            string safeRoomId = SafeTrim(roomId);
            string safeConnectionId = SafeTrim(connectionId);

            if (string.IsNullOrWhiteSpace(safeRoomId) ||
                string.IsNullOrWhiteSpace(safeConnectionId))
            {
                return;
            }

            if (!dict_roomContextByRoomId.TryGetValue(
                    safeRoomId,
                    out DedicatedRoomContext roomContext) ||
                roomContext == null)
            {
                return;
            }

            roomContext.connectionIds.Remove(safeConnectionId);
            roomContext.lastUpdatedUnixMs = NowUnixMs();
        }
        //* این تابع سشن را به کانتکست روم خودش اضافه می کند.
        private void AddSessionToRoomContextUnsafe(DedicatedPlayerSession session)
        {
            if (session == null) return;

            string safeRoomId = SafeTrim(session.roomId);
            if (string.IsNullOrWhiteSpace(safeRoomId)) return;

            if (!dict_roomContextByRoomId.TryGetValue(safeRoomId, out DedicatedRoomContext roomContext) || roomContext == null)
            {
                long now = NowUnixMs();
                roomContext = new DedicatedRoomContext
                {
                    roomId = safeRoomId,
                    createdAtUnixMs = now,
                    lastUpdatedUnixMs = now
                };
                dict_roomContextByRoomId[safeRoomId] = roomContext;
            }

            roomContext.connectionIds.Add(SafeTrim(session.connectionId));
            roomContext.userIds.Add(SafeTrim(session.userId));
            roomContext.playerIds.Add(SafeTrim(session.playerId));
            roomContext.lastUpdatedUnixMs = NowUnixMs();
        }

        //* این تابع روم اصلی را بعد از حذف پلیرها دوباره انتخاب می کند.
        private void RefreshPrimaryRoomIdUnsafe()
        {
            if (!string.IsNullOrWhiteSpace(activeRoomId) && dict_roomContextByRoomId.ContainsKey(activeRoomId)) return;

            activeRoomId = string.Empty;
            foreach (string roomId in dict_roomContextByRoomId.Keys)
            {
                activeRoomId = SafeTrim(roomId);
                return;
            }
        }

        //* این تابع هنگام حذف آبجکت، رجیستری پلیرها را پاک می کند.
        private void OnDestroy()
        {
            ClearAll("registry_destroyed");
        }

        /*
        توضیح مکتوب فایل:
        این اسکریپت رجیستری داخلی پلیرهای تأیید شده داخل یونیتی ددیکیتد سرور است.
        بعد از auth_ok، پلیر در این رجیستری ذخیره می شود.
        اگر کانکشن قطع شود، پلیر از همین رجیستری حذف می شود.
        DedicatedHeartbeatLoop از این رجیستری تعداد واقعی پلیرها را برای نود جی اس می فرستد.
        در فازهای بعدی سیستم اسپاون و سینک حرکت از همین رجیستری استفاده خواهد کرد.
        */
    }

    [Serializable]
    public class DedicatedRoomContext
    {
        public string roomId;
        public long createdAtUnixMs;
        public long lastUpdatedUnixMs;
        public readonly HashSet<string> connectionIds = new HashSet<string>();
        public readonly HashSet<string> userIds = new HashSet<string>();
        public readonly HashSet<string> playerIds = new HashSet<string>();
    }

}
