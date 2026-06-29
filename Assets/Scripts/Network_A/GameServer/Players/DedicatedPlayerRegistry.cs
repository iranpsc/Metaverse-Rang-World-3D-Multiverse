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

        private readonly Dictionary<string, DedicatedPlayerSession> dict_playersByConnectionId =
            new Dictionary<string, DedicatedPlayerSession>();

        private readonly Dictionary<string, string> dict_connectionIdByUserId =
            new Dictionary<string, string>();

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

            DedicatedPlayerSession newSession = new DedicatedPlayerSession
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

            if (!ValidateSession(newSession, out error))
            {
                return false;
            }

            DedicatedPlayerSession removedDuplicate = null;

            lock (syncLock)
            {
                if (dict_connectionIdByUserId.TryGetValue(newSession.userId, out string oldConnectionId))
                {
                    if (dict_playersByConnectionId.TryGetValue(oldConnectionId, out removedDuplicate))
                    {
                        dict_playersByConnectionId.Remove(oldConnectionId);
                    }

                    dict_connectionIdByUserId.Remove(newSession.userId);
                }

                dict_playersByConnectionId[newSession.connectionId] = newSession;
                dict_connectionIdByUserId[newSession.userId] = newSession.connectionId;
            }

            if (removedDuplicate != null)
            {
                PlayerRemoved?.Invoke(removedDuplicate, "duplicate_user_replaced");
                Debug.LogWarning("[DedicatedPlayerRegistry] Duplicate user replaced | userId=" + removedDuplicate.userId);
            }

            session = newSession;

            Debug.Log("[DedicatedPlayerRegistry] Player registered | userId=" +
                      session.userId + " | connectionId=" + session.connectionId +
                      " | count=" + CurrentPlayerCount);

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

                if (!string.IsNullOrWhiteSpace(removed.userId))
                {
                    dict_connectionIdByUserId.Remove(removed.userId);
                }
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
            }

            for (int i = 0; i < removedPlayers.Count; i++)
            {
                PlayerRemoved?.Invoke(removedPlayers[i], reason);
            }

            Debug.Log("[DedicatedPlayerRegistry] Cleared | reason=" + reason);
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
}
