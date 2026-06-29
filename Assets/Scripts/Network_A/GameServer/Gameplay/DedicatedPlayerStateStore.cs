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

        //* این تابع آخرین وضعیت حرکتی پلیر احراز شده را ذخیره یا به روز می کند.
        public DedicatedPlayerStateRecord UpdateState(
            DedicatedPlayerSession session,
            DedicatedPlayerStateMessageDto message,
            long serverTimeUnixMs)
        {
            if (session == null || message == null) return null;

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
