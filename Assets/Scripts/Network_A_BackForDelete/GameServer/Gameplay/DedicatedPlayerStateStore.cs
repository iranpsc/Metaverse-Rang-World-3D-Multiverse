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
                if (dict_stateByConnectionId.TryGetValue(session.connectionId, out DedicatedPlayerStateRecord oldRecord))
                {
                    if (message.sequence > 0 && oldRecord.sequence > 0 && message.sequence <= oldRecord.sequence)
                    {
                        LastRejectedSequence = message.sequence;
                        LastStateRejectReason = "stale_or_duplicate_sequence";
                        return oldRecord;
                    }
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
                dict_stateByConnectionId[session.connectionId] = record;
            }

            LastAcceptedSequence = record.sequence;
            PlayerStateUpdated?.Invoke(record);

            return record;
        }

        //* این تابع وضعیت ذخیره شده یک کانکشن را حذف می کند.
        public bool RemoveByConnectionId(string connectionId, string reason)
        {
            if (string.IsNullOrWhiteSpace(connectionId)) return false;

            DedicatedPlayerStateRecord removed = null;

            lock (syncLock)
            {
                if (!dict_stateByConnectionId.TryGetValue(connectionId, out removed))
                {
                    return false;
                }

                dict_stateByConnectionId.Remove(connectionId);
            }

            PlayerStateRemoved?.Invoke(removed, reason);

            Debug.Log("[DedicatedPlayerStateStore] State removed | connectionId=" +
                      connectionId + " | reason=" + reason + " | count=" + StateCount);

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
                   " | lastAcceptedSequence=" + LastAcceptedSequence +
                   " | lastRejectedSequence=" + LastRejectedSequence +
                   " | lastRejectReason=" + LastStateRejectReason;
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
        کلید اصلی وضعیت، کانکشن آی دی وب سوکت است.
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
