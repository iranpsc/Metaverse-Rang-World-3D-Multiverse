using System;
using System.Collections.Generic;
using Network_A.GameIntegration.Presence;
using Network_A.GameServer;
using UnityEngine;

namespace Network_A.GameIntegration.Movement
{
    //* رجیستری پلیرهای ریموت است و برای هر playerId یک آبجکت نمایشی می‌سازد یا به‌روزرسانی می‌کند.
    public class RealtimeRemotePlayerMovementRegistry : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private RealtimeRemotePlayerMovementReceiver receiver;
        [SerializeField] private RealtimeRemotePlayerPresenceReceiver presenceReceiver;
        [SerializeField] private RealtimeRemotePlayerMovementView remotePlayerPrefab;
        [SerializeField] private Transform remotePlayersRoot;

        [Header("Behavior")]
        [SerializeField] private bool createRemoteOnMovementIfMissing = true;
        [SerializeField] private bool despawnOnPresenceLeft = true;

        [Header("Debug")]
        [SerializeField] private bool logRegistryEvents;

        private readonly Dictionary<string, RealtimeRemotePlayerMovementView> dict_ViewByPlayerId = new Dictionary<string, RealtimeRemotePlayerMovementView>();
        private bool isMovementReceiverBound;
        private bool isPresenceReceiverBound;

        public int RemotePlayerCount => dict_ViewByPlayerId.Count;
        public event Action<string, RealtimeRemotePlayerMovementView> RemotePlayerSpawned;
        public event Action<string> RemotePlayerDespawned;

        #region <Unity Lifecycle>

        //* اگر receiverها از اینسپکتور تنظیم شده باشند، رجیستری را به آن‌ها وصل می‌کند.
        private void Awake()
        {
            if (remotePlayersRoot == null) remotePlayersRoot = transform;
            BindReceivers();
        }

        //* هنگام فعال شدن، اتصال receiverها را دوباره بررسی می‌کند.
        private void OnEnable()
        {
            BindReceivers();
        }

        //* هنگام غیرفعال شدن، رویدادهای receiverها را جدا می‌کند.
        private void OnDisable()
        {
            UnbindReceivers();
        }

        #endregion

        #region <Public API>

        //* receiver حرکت را از کد بیرونی تنظیم می‌کند و subscribe تکراری ایجاد نمی‌کند.
        public void Initialize(RealtimeRemotePlayerMovementReceiver movementReceiver)
        {
            Initialize(movementReceiver, presenceReceiver, remotePlayerPrefab, remotePlayersRoot);
        }

        //* receiverهای حرکت و پرزنس را از کد بیرونی تنظیم می‌کند و ساخت/حذف پلیر ریموت را فعال می‌کند.
        public void Initialize(RealtimeRemotePlayerMovementReceiver movementReceiver, RealtimeRemotePlayerPresenceReceiver remotePresenceReceiver, RealtimeRemotePlayerMovementView prefab, Transform root)
        {
            UnbindReceivers();
            receiver = movementReceiver;
            presenceReceiver = remotePresenceReceiver;
            remotePlayerPrefab = prefab == null ? remotePlayerPrefab : prefab;
            remotePlayersRoot = root == null ? (remotePlayersRoot == null ? transform : remotePlayersRoot) : root;
            BindReceivers();
        }

        //* prefab پلیر ریموت را برای ساخت آبجکت‌های بعدی تنظیم می‌کند.
        public void SetRemotePlayerPrefab(RealtimeRemotePlayerMovementView prefab)
        {
            remotePlayerPrefab = prefab;
        }

        //* نمای پلیر ریموت را با playerId پیدا می‌کند.
        public bool TryGetRemoteView(string playerId, out RealtimeRemotePlayerMovementView view)
        {
            view = null;
            if (string.IsNullOrWhiteSpace(playerId)) return false;
            return dict_ViewByPlayerId.TryGetValue(playerId.Trim(), out view) && view != null;
        }

        //* اگر پلیر ریموت وجود نداشته باشد، آن را می‌سازد و به رجیستری اضافه می‌کند.
        public RealtimeRemotePlayerMovementView SpawnRemotePlayer(string playerId)
        {
            return GetOrCreateView(playerId);
        }

        //* پلیر ریموت مشخص را از رجیستری و صحنه حذف می‌کند.
        public bool DespawnRemotePlayer(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return false;
            string key = playerId.Trim();

            if (!dict_ViewByPlayerId.TryGetValue(key, out RealtimeRemotePlayerMovementView view)) return false;
            dict_ViewByPlayerId.Remove(key);

            if (view != null) Destroy(view.gameObject);
            if (logRegistryEvents) Debug.Log("[RealtimeMovementRegistry] Remote player despawned: " + key);
            RemotePlayerDespawned?.Invoke(key);
            return true;
        }

        //* همه پلیرهای ریموت ساخته‌شده را حذف می‌کند.
        public void ClearRemotePlayers()
        {
            foreach (RealtimeRemotePlayerMovementView view in dict_ViewByPlayerId.Values)
            {
                if (view != null) Destroy(view.gameObject);
            }

            dict_ViewByPlayerId.Clear();
        }

        #endregion

        #region <Receiver Binding>

        //* رجیستری را به رویدادهای حرکت و پرزنس وصل می‌کند.
        private void BindReceivers()
        {
            BindMovementReceiver();
            BindPresenceReceiver();
        }

        //* اتصال رجیستری از همه receiverها را جدا می‌کند.
        private void UnbindReceivers()
        {
            UnbindMovementReceiver();
            UnbindPresenceReceiver();
        }

        //* رجیستری را به رویداد اسنپ‌شات receiver حرکت وصل می‌کند.
        private void BindMovementReceiver()
        {
            if (isMovementReceiverBound || receiver == null) return;
            isMovementReceiverBound = true;
            receiver.SnapshotReceived += HandleSnapshotReceived;
        }

        //* اتصال رجیستری از receiver حرکت را جدا می‌کند.
        private void UnbindMovementReceiver()
        {
            if (!isMovementReceiverBound) return;
            isMovementReceiverBound = false;
            if (receiver != null) receiver.SnapshotReceived -= HandleSnapshotReceived;
        }

        //* رجیستری را به رویدادهای پرزنس وصل می‌کند.
        private void BindPresenceReceiver()
        {
            if (isPresenceReceiverBound || presenceReceiver == null) return;
            isPresenceReceiverBound = true;
            presenceReceiver.RemotePlayerJoined += HandleRemotePlayerJoined;
            presenceReceiver.RemotePlayerLeft += HandleRemotePlayerLeft;
        }

        //* اتصال رجیستری از receiver پرزنس را جدا می‌کند.
        private void UnbindPresenceReceiver()
        {
            if (!isPresenceReceiverBound) return;
            isPresenceReceiverBound = false;

            if (presenceReceiver != null)
            {
                presenceReceiver.RemotePlayerJoined -= HandleRemotePlayerJoined;
                presenceReceiver.RemotePlayerLeft -= HandleRemotePlayerLeft;
            }
        }

        #endregion

        #region <Presence Handling>

        //* ورود پلیر ریموت را به ساخت آبجکت ریموت وصل می‌کند.
        private void HandleRemotePlayerJoined(GameServerPresenceEvent presenceEvent)
        {
            if (presenceEvent == null) return;
            string playerId = presenceEvent.ResolveNetworkPlayerId();
            if (string.IsNullOrWhiteSpace(playerId)) return;
            GetOrCreateView(playerId);
        }

        //* خروج پلیر ریموت را به حذف آبجکت ریموت وصل می‌کند.
        private void HandleRemotePlayerLeft(GameServerPresenceEvent presenceEvent)
        {
            if (!despawnOnPresenceLeft || presenceEvent == null) return;
            string playerId = presenceEvent.ResolveNetworkPlayerId();
            if (string.IsNullOrWhiteSpace(playerId)) return;
            DespawnRemotePlayer(playerId);
        }

        #endregion

        #region <Snapshot Handling>

        //* اسنپ‌شات دریافتی را به نمای پلیر مربوطه اعمال می‌کند.
        private void HandleSnapshotReceived(RealtimeMovementSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.IsValid()) return;

            RealtimeRemotePlayerMovementView view = ResolveViewForSnapshot(snapshot.playerId);
            if (view == null) return;

            view.ApplySnapshot(snapshot);
        }

        //* نمای مناسب اسنپ‌شات را پیدا می‌کند یا در صورت مجاز بودن می‌سازد.
        private RealtimeRemotePlayerMovementView ResolveViewForSnapshot(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return null;
            string key = playerId.Trim();
            if (dict_ViewByPlayerId.TryGetValue(key, out RealtimeRemotePlayerMovementView existing) && existing != null) return existing;
            return createRemoteOnMovementIfMissing ? GetOrCreateView(key) : null;
        }

        //* اگر نمای پلیر ریموت وجود نداشته باشد، آن را از prefab می‌سازد.
        private RealtimeRemotePlayerMovementView GetOrCreateView(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return null;
            string key = playerId.Trim();
            if (dict_ViewByPlayerId.TryGetValue(key, out RealtimeRemotePlayerMovementView existing) && existing != null) return existing;

            if (remotePlayerPrefab == null)
            {
                Debug.LogWarning("[RealtimeMovementRegistry] Remote player prefab is not assigned.");
                return null;
            }

            RealtimeRemotePlayerMovementView created = Instantiate(remotePlayerPrefab, remotePlayersRoot == null ? transform : remotePlayersRoot);
            created.InitializeIdentity(key);
            created.gameObject.SetActive(true);
            dict_ViewByPlayerId[key] = created;

            if (logRegistryEvents) Debug.Log("[RealtimeMovementRegistry] Remote player spawned: " + key);
            RemotePlayerSpawned?.Invoke(key, created);
            return created;
        }

        #endregion
    }
}
