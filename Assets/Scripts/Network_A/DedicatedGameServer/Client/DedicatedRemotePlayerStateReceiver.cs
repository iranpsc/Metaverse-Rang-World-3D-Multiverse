using System;
using System.Collections.Generic;
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

        //* این تابع رفرنس کلاینت ددیکیتد را هنگام شروع آبجکت پیدا می کند.
        private void Awake()
        {
            EnsureReferences();
        }

        //* این تابع هنگام فعال شدن آبجکت، پیام های خام کلاینت ددیکیتد را گوش می دهد.
        private void OnEnable()
        {
            BindWsEvents();
        }

        //* این تابع هنگام غیرفعال شدن آبجکت، رویدادها را پاک می کند.
        private void OnDisable()
        {
            UnbindWsEvents();
        }

        //* این تابع رفرنس کلاینت ددیکیتد را از همین آبجکت یا سینگلتون پیدا می کند.
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

        //* این تابع یک اسنپ شات از وضعیت ریموت پلیرها برمی گرداند.
        public List<DedicatedRemotePlayerState> CreateSnapshot()
        {
            return new List<DedicatedRemotePlayerState>(dict_remoteStatesByPlayerId.Values);
        }

        //* این تابع وضعیت یک ریموت پلیر را با پلیر آی دی برمی گرداند.
        public DedicatedRemotePlayerState GetRemoteState(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return null;

            return dict_remoteStatesByPlayerId.TryGetValue(playerId.Trim(), out DedicatedRemotePlayerState state)
                ? state
                : null;
        }

        //* این تابع پیام خام دریافتی از ددیکیتد سرور را دسته بندی می کند.
        private void HandleRawMessageReceived(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;

            DedicatedRemoteMessageTypeDto typeDto = ParseType(raw);
            if (typeDto == null || string.IsNullOrWhiteSpace(typeDto.type)) return;

            if (typeDto.type == "player_state")
            {
                HandlePlayerState(raw);
                return;
            }

            if (typeDto.type == "player_joined")
            {
                HandlePlayerJoined(raw);
                return;
            }

            if (typeDto.type == "player_left")
            {
                HandlePlayerLeft(raw);
                return;
            }

            if (typeDto.type == "player_state_accepted")
            {
                HandlePlayerStateAccepted(raw);
            }
        }

        //* این تابع پیام player_state پلیر دیگر را دریافت و ذخیره می کند.
        private void HandlePlayerState(string raw)
        {
            DedicatedRemotePlayerState state = null;

            try
            {
                state = JsonUtility.FromJson<DedicatedRemotePlayerState>(raw);
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
                          " | remoteCount=" + RemotePlayerCount);
            }
        }

        //* این تابع پیام ورود پلیر دیگر را دریافت می کند.
        private void HandlePlayerJoined(string raw)
        {
            DedicatedRemotePresenceEvent evt = null;

            try
            {
                evt = JsonUtility.FromJson<DedicatedRemotePresenceEvent>(raw);
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
                          evt.ResolvePlayerId() + " | userId=" + evt.userId + " | roomId=" + evt.roomId);
            }
        }

        //* این تابع پیام خروج پلیر دیگر را دریافت می کند و وضعیت ذخیره شده آن را پاک می کند.
        private void HandlePlayerLeft(string raw)
        {
            DedicatedRemotePresenceEvent evt = null;

            try
            {
                evt = JsonUtility.FromJson<DedicatedRemotePresenceEvent>(raw);
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
                          playerId + " | reason=" + evt.reason + " | remoteCount=" + RemotePlayerCount);
            }
        }

        //* این تابع پاسخ player_state_accepted را برای تست تک کلاینت دریافت می کند.
        private void HandlePlayerStateAccepted(string raw)
        {
            DedicatedPlayerStateAcceptedEvent evt = null;

            try
            {
                evt = JsonUtility.FromJson<DedicatedPlayerStateAcceptedEvent>(raw);
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
                          evt.sequence + " | broadcastCount=" + evt.broadcastCount);
            }
        }

        //* این تابع بعد از قطع اتصال، وضعیت ریموت ها را پاک می کند.
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

        //* این تابع تایپ پیام را از جیسون خام می خواند.
        private DedicatedRemoteMessageTypeDto ParseType(string raw)
        {
            try
            {
                return JsonUtility.FromJson<DedicatedRemoteMessageTypeDto>(raw);
            }
            catch
            {
                return null;
            }
        }

        /*
        توضیح مکتوب فایل:
        این اسکریپت سمت کلاینت ددیکیتد پیام های دریافتی از یونیتی ددیکیتد سرور را گوش می دهد.
        برای تست DS-8B، پیام های player_state پلیرهای دیگر را دریافت و ذخیره می کند.
        همچنین player_joined، player_left و player_state_accepted را تشخیص می دهد.
        این فایل هنوز Remote Player را در صحنه نمی سازد؛ فقط داده را دریافت، ذخیره و لاگ می کند.
        فاز بعدی از همین داده برای ساخت و حرکت دادن Remote Player View استفاده می کند.
        */

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

        //* این تابع شناسه پلیر را به شکل امن برمی گرداند.
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

        //* این تابع شناسه پلیر را به شکل امن برمی گرداند.
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
