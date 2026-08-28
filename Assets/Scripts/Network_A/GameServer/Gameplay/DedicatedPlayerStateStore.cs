using System;
using System.Collections.Generic;
using Network_A.GameServer.Players;
using UnityEngine;

namespace Network_A.GameServer.Gameplay
{
    public class DedicatedPlayerStateStore : MonoBehaviour
    {
        private readonly object syncLock = new object();

        private readonly Dictionary<string, DedicatedPlayerStateRecord> dict_stateByConnectionId =
            new Dictionary<string, DedicatedPlayerStateRecord>();

        private readonly Dictionary<string, DedicatedPlayerStateRecord> dict_stateByUserId =
            new Dictionary<string, DedicatedPlayerStateRecord>();

        public int StateCount
        {
            get
            {
                lock (syncLock)
                {
                    return dict_stateByConnectionId.Count;
                }
            }
        }

        public event Action<DedicatedPlayerStateRecord> PlayerStateUpdated;
        public event Action<DedicatedPlayerStateRecord, string> PlayerStateRemoved;

        public string LastStateRejectReason { get; private set; } = string.Empty;
        public long LastAcceptedSequence { get; private set; } = 0;
        public long LastRejectedSequence { get; private set; } = 0;

        //* این تابع آخرین وضعیت حرکتی پلیر احراز شده را ذخیره یا به روز می کند.
        public DedicatedPlayerStateRecord UpdateState(
            DedicatedPlayerSession session,
            DedicatedPlayerStateMessageDto message,
            long serverTimeUnixMs)
        {
            LastStateRejectReason = string.Empty;

            if (session == null)
            {
                LastStateRejectReason = "session_missing";
                return null;
            }

            if (message == null)
            {
                LastStateRejectReason = "message_missing";
                return null;
            }

            lock (syncLock)
            {
                DedicatedPlayerStateRecord oldRecord = null;

                if (!string.IsNullOrWhiteSpace(session.connectionId))
                {
                    dict_stateByConnectionId.TryGetValue(session.connectionId, out oldRecord);
                }

                if (oldRecord == null && !string.IsNullOrWhiteSpace(session.userId))
                {
                    dict_stateByUserId.TryGetValue(BuildRoomUserKey(session.roomId, session.userId), out oldRecord);
                }

                if (oldRecord != null && message.sequence > 0 && oldRecord.sequence > 0 && message.sequence <= oldRecord.sequence)
                {
                    LastRejectedSequence = message.sequence;
                    LastStateRejectReason = "stale_or_duplicate_sequence";
                    return oldRecord;
                }
            }

            DedicatedPlayerStateRecord record = new DedicatedPlayerStateRecord
            {
                connectionId = session.connectionId,
                userId = session.userId,
                playerId = session.playerId,
                userName = session.userName,
                roomId = session.roomId,
                serverId = session.serverId,
                sessionId = session.sessionId,

                sequence = message.sequence,
                clientTimestampUnixMs = message.timestampUnixMs,
                serverTimestampUnixMs = serverTimeUnixMs,

                px = message.px,
                py = message.py,
                pz = message.pz,

                rx = message.rx,
                ry = message.ry,
                rz = message.rz,
                rw = message.rw,

                vx = message.vx,
                vy = message.vy,
                vz = message.vz
            };

            lock (syncLock)
            {
                string safeConnectionId = SafeTrim(record.connectionId);
                string safeUserId = SafeTrim(record.userId);
                string roomUserKey = BuildRoomUserKey(record.roomId, record.userId);

                if (!string.IsNullOrWhiteSpace(safeUserId) &&
                    dict_stateByUserId.TryGetValue(roomUserKey, out DedicatedPlayerStateRecord previousRecord) &&
                    previousRecord != null)
                {
                    string previousConnectionId = SafeTrim(previousRecord.connectionId);

                    if (!string.IsNullOrWhiteSpace(previousConnectionId) &&
                        !string.Equals(previousConnectionId, safeConnectionId, StringComparison.Ordinal))
                    {
                        dict_stateByConnectionId.Remove(previousConnectionId);
                    }
                }

                if (!string.IsNullOrWhiteSpace(safeConnectionId))
                {
                    dict_stateByConnectionId[safeConnectionId] = record;
                }

                if (!string.IsNullOrWhiteSpace(safeUserId))
                {
                    dict_stateByUserId[roomUserKey] = record;
                }
            }

            LastAcceptedSequence = record.sequence;
            PlayerStateUpdated?.Invoke(record);

            return record;
        }

        //* این تابع وضعیت ذخیره شده یک کانکشن را حذف می کند.
        public bool RemoveByConnectionId(string connectionId, string reason)
        {
            string safeConnectionId = SafeTrim(connectionId);
            string safeReason = SafeTrim(reason);
            if (string.IsNullOrWhiteSpace(safeConnectionId)) return false;

            DedicatedPlayerStateRecord removed = null;
            bool skipRemoveEventForReconnect = string.Equals(safeReason, "duplicate_user_replaced", StringComparison.Ordinal);

            lock (syncLock)
            {
                if (!dict_stateByConnectionId.TryGetValue(safeConnectionId, out removed))
                {
                    return false;
                }

                dict_stateByConnectionId.Remove(safeConnectionId);

                string safeUserId = SafeTrim(removed.userId);
                string roomUserKey = BuildRoomUserKey(removed.roomId, removed.userId);
                if (!string.IsNullOrWhiteSpace(safeUserId))
                {
                    if (skipRemoveEventForReconnect)
                    {
                        dict_stateByUserId[roomUserKey] = removed;
                    }
                    else if (dict_stateByUserId.TryGetValue(roomUserKey, out DedicatedPlayerStateRecord mappedRecord) && mappedRecord == removed)
                    {
                        dict_stateByUserId.Remove(roomUserKey);
                    }
                }
            }

            if (skipRemoveEventForReconnect)
            {
                Debug.Log("[DedicatedPlayerStateStore] State remove skipped for reconnect rebind | userId=" +
                          SafeTrim(removed.userId) + " | connectionId=" + safeConnectionId +
                          " | reason=" + safeReason + " | event=NO | count=" + StateCount);

                return true;
            }

            PlayerStateRemoved?.Invoke(removed, safeReason);

            Debug.Log("[DedicatedPlayerStateStore] State removed | connectionId=" +
                      safeConnectionId + " | reason=" + safeReason + " | count=" + StateCount);

            return true;
        }

        //* این تابع وضعیت آخر یک کانکشن را برمی گرداند.
        public DedicatedPlayerStateRecord GetByConnectionId(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId)) return null;

            lock (syncLock)
            {
                return dict_stateByConnectionId.TryGetValue(connectionId, out DedicatedPlayerStateRecord record)
                    ? record
                    : null;
            }
        }

        //* این تابع اسنپ شات وضعیت همه پلیرهای ذخیره شده را می سازد.
        public List<DedicatedPlayerStateRecord> CreateSnapshot()
        {
            lock (syncLock)
            {
                return new List<DedicatedPlayerStateRecord>(dict_stateByConnectionId.Values);
            }
        }


        //* این تابع وضعیت آخر یک یوزر را برمی گرداند.
        public DedicatedPlayerStateRecord GetByUserId(string userId)
        {
            string safeUserId = SafeTrim(userId);
            if (string.IsNullOrWhiteSpace(safeUserId)) return null;

            lock (syncLock)
            {
                foreach (DedicatedPlayerStateRecord record in dict_stateByConnectionId.Values)
                {
                    if (record == null) continue;
                    if (string.Equals(SafeTrim(record.userId), safeUserId, StringComparison.Ordinal)) return record;
                }
            }

            return null;
        }

        //* این تابع وضعیت آخر یک یوزر را داخل روم مشخص برمی گرداند.
        public DedicatedPlayerStateRecord GetByUserIdInRoom(string roomId, string userId)
        {
            string roomUserKey = BuildRoomUserKey(roomId, userId);
            if (string.IsNullOrWhiteSpace(roomUserKey)) return null;

            lock (syncLock)
            {
                return dict_stateByUserId.TryGetValue(roomUserKey, out DedicatedPlayerStateRecord record) ? record : null;
            }
        }

        //* این تابع وضعیت آخر یک پلیر را برمی گرداند.
        public DedicatedPlayerStateRecord GetByPlayerId(string playerId)
        {
            string safePlayerId = SafeTrim(playerId);
            if (string.IsNullOrWhiteSpace(safePlayerId)) return null;

            lock (syncLock)
            {
                foreach (DedicatedPlayerStateRecord record in dict_stateByConnectionId.Values)
                {
                    if (record == null) continue;
                    if (string.Equals(SafeTrim(record.playerId), safePlayerId, StringComparison.Ordinal)) return record;
                }
            }

            return null;
        }

        //* این تابع بررسی می کند وضعیت یک کانکشن وجود دارد یا نه.
        public bool HasStateForConnection(string connectionId)
        {
            if (string.IsNullOrWhiteSpace(connectionId)) return false;
            lock (syncLock)
            {
                return dict_stateByConnectionId.ContainsKey(connectionId);
            }
        }

        //* این تابع اسنپ شات وضعیت پلیرهای یک روم را می سازد.
        public List<DedicatedPlayerStateRecord> CreateRoomSnapshot(string roomId)
        {
            string safeRoomId = SafeTrim(roomId);
            List<DedicatedPlayerStateRecord> result = new List<DedicatedPlayerStateRecord>();
            if (string.IsNullOrWhiteSpace(safeRoomId)) return result;

            lock (syncLock)
            {
                foreach (DedicatedPlayerStateRecord record in dict_stateByConnectionId.Values)
                {
                    if (record == null) continue;
                    if (string.Equals(SafeTrim(record.roomId), safeRoomId, StringComparison.Ordinal)) result.Add(record);
                }
            }

            return result;
        }

        //* این تابع وضعیت یک یوزر را حذف می کند.
        public bool RemoveByUserId(string userId, string reason)
        {
            DedicatedPlayerStateRecord record = GetByUserId(userId);
            if (record == null) return false;
            return RemoveByConnectionId(record.connectionId, reason);
        }

        //* این تابع وضعیت یک پلیر را حذف می کند.
        public bool RemoveByPlayerId(string playerId, string reason)
        {
            DedicatedPlayerStateRecord record = GetByPlayerId(playerId);
            if (record == null) return false;
            return RemoveByConnectionId(record.connectionId, reason);
        }

        //* این تابع خلاصه وضعیت استور را برای تست فاز سی و سه آ برمی گرداند.
        public string GetMirrorLikeStateStoreDebugSummary()
        {
            return "phase=33A" +
                   " | mirrorRoute=PlayerStateStore" +
                   " | states=" + StateCount +
                   " | userStates=" + GetUserStateCount() +
                   " | lastAcceptedSequence=" + LastAcceptedSequence +
                   " | lastRejectedSequence=" + LastRejectedSequence +
                   " | lastRejectReason=" + LastStateRejectReason;
        }


        //* این تابع وضعیت ذخیره شده یک یوزر را به کانکشن جدید ریکانکت وصل می کند.
        public bool RebindConnectionForUser(DedicatedPlayerSession session, string reason = "reconnect_rebind")
        {
            if (session == null) return false;
            return RebindConnectionForUser(session.userId, session.connectionId, session, reason);
        }

        //* این تابع وضعیت ذخیره شده یک یوزر را با یوزر آی دی و کانکشن جدید دوباره مپ می کند.
        public bool RebindConnectionForUser(string userId, string newConnectionId, DedicatedPlayerSession session = null, string reason = "reconnect_rebind")
        {
            string safeUserId = SafeTrim(userId);
            string safeConnectionId = SafeTrim(newConnectionId);
            string safeReason = SafeTrim(reason);

            if (string.IsNullOrWhiteSpace(safeUserId) || string.IsNullOrWhiteSpace(safeConnectionId)) return false;

            DedicatedPlayerStateRecord record = null;
            string oldConnectionId = string.Empty;
            string roomUserKey = session != null ? BuildRoomUserKey(session.roomId, safeUserId) : string.Empty;

            lock (syncLock)
            {
                if (string.IsNullOrWhiteSpace(roomUserKey) || !dict_stateByUserId.TryGetValue(roomUserKey, out record) || record == null)
                {
                    foreach (KeyValuePair<string, DedicatedPlayerStateRecord> pair in dict_stateByUserId)
                    {
                        if (pair.Value == null) continue;
                        if (!string.Equals(SafeTrim(pair.Value.userId), safeUserId, StringComparison.Ordinal)) continue;
                        roomUserKey = pair.Key;
                        record = pair.Value;
                        break;
                    }
                }

                if (record == null) return false;

                oldConnectionId = SafeTrim(record.connectionId);

                if (!string.IsNullOrWhiteSpace(oldConnectionId) &&
                    !string.Equals(oldConnectionId, safeConnectionId, StringComparison.Ordinal))
                {
                    dict_stateByConnectionId.Remove(oldConnectionId);
                }

                ApplySessionToRecord(record, session, safeUserId, safeConnectionId);
                roomUserKey = BuildRoomUserKey(record.roomId, record.userId);

                dict_stateByConnectionId[safeConnectionId] = record;
                dict_stateByUserId[roomUserKey] = record;
            }

            PlayerStateUpdated?.Invoke(record);

            Debug.Log("[DedicatedPlayerStateStore] State rebound for reconnect | userId=" + safeUserId +
                      " | oldConnectionId=" + oldConnectionId +
                      " | newConnectionId=" + safeConnectionId +
                      " | reason=" + safeReason +
                      " | stateRemovedEvent=NO");

            return true;
        }

        //* این تابع اطلاعات سشن جدید را روی رکورد وضعیت قبلی اعمال می کند.
        private void ApplySessionToRecord(DedicatedPlayerStateRecord record, DedicatedPlayerSession session, string userId, string connectionId)
        {
            if (record == null) return;

            record.connectionId = connectionId;
            record.userId = userId;

            if (session == null) return;

            record.playerId = session.playerId;
            record.userName = session.userName;
            record.roomId = session.roomId;
            record.serverId = session.serverId;
            record.sessionId = session.sessionId;
        }

        //* این تابع تعداد وضعیت های ذخیره شده بر اساس یوزر آی دی را برمی گرداند.
        public int GetUserStateCount()
        {
            lock (syncLock)
            {
                return dict_stateByUserId.Count;
            }
        }

        //* این تابع کلید داخلی وضعیت یوزر داخل روم را می سازد.
        private string BuildRoomUserKey(string roomId, string userId)
        {
            return SafeTrim(roomId) + "::" + SafeTrim(userId);
        }

        private string SafeTrim(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        //* این تابع همه وضعیت های ذخیره شده را پاک می کند.
        public void ClearAll(string reason)
        {
            List<DedicatedPlayerStateRecord> removed;

            lock (syncLock)
            {
                removed = new List<DedicatedPlayerStateRecord>(dict_stateByConnectionId.Values);
                dict_stateByConnectionId.Clear();
                dict_stateByUserId.Clear();
            }

            for (int i = 0; i < removed.Count; i++)
            {
                PlayerStateRemoved?.Invoke(removed[i], reason);
            }

            Debug.Log("[DedicatedPlayerStateStore] Cleared | reason=" + reason);
        }

        /*
        توضیح مکتوب فایل:
        این اسکریپت سمت یونیتی ددیکیتد سرور آخرین وضعیت حرکتی پلیرهای احراز شده را نگه می دارد.
        کلیدهای وضعیت، کانکشن آی دی وب سوکت و یوزر آی دی هستند.
        اطلاعات قابل اعتماد پلیر از DedicatedPlayerSession گرفته می شود، نه از بادی کلاینت.
        DedicatedGameMessageRouter بعد از دریافت player_state این استور را به روز می کند.
        */
    }

    [Serializable]
    public class DedicatedPlayerStateRecord
    {
        public string connectionId;
        public string userId;
        public string playerId;
        public string userName;
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
    }
}
