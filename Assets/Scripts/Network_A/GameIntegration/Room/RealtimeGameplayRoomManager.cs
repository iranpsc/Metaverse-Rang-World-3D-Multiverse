using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.GameIntegration.Movement;
using Network_A.GameIntegration.Presence;
using Network_A.GameIntegration.World;
using Network_A.GameServer;
using Network_A.Realtime.Stability;
using UnityEngine;

namespace Network_A.GameIntegration.Room
{
    //* منیجر اتصال گیم‌پلی به روم است و local player، remote players، پرزنس و movement را یکجا bind می‌کند.
    public class RealtimeGameplayRoomManager : MonoBehaviour
    {
        [Header("Scene References")]
        [SerializeField] private Transform localPlayerTransform;
        [SerializeField] private RealtimePlayerMovementSender localMovementSender;
        [SerializeField] private RealtimeRemotePlayerPresenceReceiver presenceReceiver;
        [SerializeField] private RealtimeRemotePlayerMovementReceiver movementReceiver;
        [SerializeField] private RealtimeRemotePlayerMovementRegistry remoteRegistry;
        [SerializeField] private RealtimeWorldEventReceiver worldEventReceiver;
        [SerializeField] private RealtimeWorldEventRegistry worldEventRegistry;
        [SerializeField] private RealtimeRemotePlayerMovementView remotePlayerPrefab;
        [SerializeField] private Transform remotePlayersRoot;

        [Header("Behavior")]
        [SerializeField] private bool createMissingComponents = true;
        [SerializeField] private bool disableMovementUntilReady = true;
        [SerializeField] private bool clearRemotePlayersOnStop = true;

        [Header("Debug")]
        [SerializeField] private bool logLifecycle;

        private GameServerClient gameServerClient;
        private string localNetworkPlayerId = string.Empty;
        private string roomId = string.Empty;
        private bool isBound;
        private bool isStopping;

        public RealtimeGameplayRoomState State { get; private set; } = RealtimeGameplayRoomState.Idle;
        public string LocalNetworkPlayerId => localNetworkPlayerId;
        public string RoomId => roomId;
        public bool IsReady => State == RealtimeGameplayRoomState.Ready;
        public int RemotePlayerCount => remoteRegistry == null ? 0 : remoteRegistry.RemotePlayerCount;
        public RealtimePlayerMovementSender LocalMovementSender => localMovementSender;
        public RealtimeRemotePlayerMovementRegistry RemoteRegistry => remoteRegistry;
        public RealtimeRemotePlayerPresenceReceiver PresenceReceiver => presenceReceiver;
        public RealtimeRemotePlayerMovementReceiver MovementReceiver => movementReceiver;
        public RealtimeWorldEventReceiver WorldEventReceiver => worldEventReceiver;
        public RealtimeWorldEventRegistry WorldEventRegistry => worldEventRegistry;

        public event Action<RealtimeGameplayRoomState> StateChanged;
        public event Action<RealtimeGameplayRoomManager> BindingReady;
        public event Action<RealtimeGameplayRoomManager> RoomReady;
        public event Action<RealtimeGameplayRoomManager> RoomStopped;
        public event Action<string, RealtimeRemotePlayerMovementView> RemotePlayerSpawned;
        public event Action<string> RemotePlayerDespawned;
        public event Action<RealtimeMovementSnapshot> RemoteMovementSnapshotReceived;
        public event Action<RealtimeWorldEventData> WorldEventReceived;
        public event Action<RealtimeWorldEventData, RealtimeWorldEventTarget> WorldEventApplied;

        #region <Unity Lifecycle>

        //* هنگام ساخت آبجکت، مرجع‌های صحنه را اگر لازم باشد کامل می‌کند.
        private void Awake()
        {
            EnsureSceneComponents();
            SetMovementSenderEnabled(!disableMovementUntilReady);
        }

        //* هنگام غیرفعال شدن، ارسال movement را متوقف می‌کند تا قبل از آماده بودن روم پیام ارسال نشود.
        private void OnDisable()
        {
            SetMovementSenderEnabled(false);
        }

        //* هنگام حذف آبجکت، رویدادهای داخلی را جدا می‌کند.
        private void OnDestroy()
        {
            UnbindGameplayEvents();
        }

        #endregion

        #region <Public Setup>

        //* منیجر را با گیم‌سرورکلاینت، آیدی پلیر لوکال و روم هدف آماده می‌کند.
        public void Initialize(GameServerClient client, string playerId, string targetRoomId)
        {
            gameServerClient = client;
            localNetworkPlayerId = string.IsNullOrWhiteSpace(playerId) ? string.Empty : playerId.Trim();
            roomId = string.IsNullOrWhiteSpace(targetRoomId) ? string.Empty : targetRoomId.Trim();
            PrepareGameplayBinding();
        }

        //* مرجع‌های صحنه را از کد بیرونی تنظیم می‌کند تا همین منیجر در صحنه واقعی هم قابل استفاده باشد.
        public void SetSceneReferences(Transform localPlayer, RealtimeRemotePlayerMovementView remotePrefab, Transform remoteRoot)
        {
            localPlayerTransform = localPlayer == null ? localPlayerTransform : localPlayer;
            remotePlayerPrefab = remotePrefab == null ? remotePlayerPrefab : remotePrefab;
            remotePlayersRoot = remoteRoot == null ? remotePlayersRoot : remoteRoot;
            EnsureSceneComponents();
        }

        //* تارگت‌های رویداد جهان را از بیرون صحنه ثبت می‌کند تا world_event روی آبجکت درست اعمال شود.
        public void RegisterWorldTargets(params RealtimeWorldEventTarget[] targets)
        {
            EnsureSceneComponents();
            if (targets == null || worldEventRegistry == null) return;

            for (int i = 0; i < targets.Length; i++) worldEventRegistry.RegisterTarget(targets[i]);
        }

        //* کامپوننت‌های receiver و registry را آماده و به گیم‌سرورکلاینت متصل می‌کند.
        public bool PrepareGameplayBinding()
        {
            SetState(RealtimeGameplayRoomState.Binding);
            EnsureSceneComponents();

            if (gameServerClient == null) return FailBinding("GameServerClient is null.");
            if (string.IsNullOrWhiteSpace(localNetworkPlayerId)) return FailBinding("Local network player id is empty.");
            if (string.IsNullOrWhiteSpace(roomId)) return FailBinding("Room id is empty.");
            if (remotePlayerPrefab == null) return FailBinding("Remote player prefab is not assigned.");

            presenceReceiver.Initialize(gameServerClient, localNetworkPlayerId);
            movementReceiver.Initialize(gameServerClient, localNetworkPlayerId);
            remoteRegistry.Initialize(movementReceiver, presenceReceiver, remotePlayerPrefab, remotePlayersRoot);
            worldEventReceiver.Initialize(gameServerClient, localNetworkPlayerId);
            worldEventRegistry.Initialize(worldEventReceiver);

            if (localMovementSender != null) localMovementSender.Initialize(gameServerClient, localNetworkPlayerId);
            BindGameplayEvents();
            SetMovementSenderEnabled(false);
            isBound = true;

            if (logLifecycle) Debug.Log("[RealtimeGameplayRoomManager] Binding ready. room=" + roomId + " | local=" + localNetworkPlayerId);
            BindingReady?.Invoke(this);
            SetState(RealtimeGameplayRoomState.Idle);
            return true;
        }

        #endregion

        #region <Room Lifecycle>

        //* با اَک قابل اطمینان وارد روم می‌شود و بعد از آماده شدن، movement پلیر لوکال را فعال می‌کند.
        public async Task<bool> JoinAndStartAsync(RealtimeReliableSendOptions options = null, CancellationToken cancellationToken = default)
        {
            if (!isBound && !PrepareGameplayBinding()) return false;

            SetState(RealtimeGameplayRoomState.Joining);
            RealtimeReliableSendResult joinResult = await gameServerClient.JoinRoomReliableAsync(roomId, options, cancellationToken);
            if (joinResult == null || !joinResult.isSuccess)
            {
                SetState(RealtimeGameplayRoomState.Failed);
                if (logLifecycle) Debug.LogWarning("[RealtimeGameplayRoomManager] Join failed. room=" + roomId + " | error=" + (joinResult == null ? "null" : joinResult.errorMessage));
                return false;
            }

            SetMovementSenderEnabled(true);
            SetState(RealtimeGameplayRoomState.Ready);
            if (logLifecycle) Debug.Log("[RealtimeGameplayRoomManager] Room ready. room=" + roomId + " | local=" + localNetworkPlayerId);
            RoomReady?.Invoke(this);
            return true;
        }

        //* خروج از روم را انجام می‌دهد، movement را خاموش می‌کند و آبجکت‌های ریموت را پاکسازی می‌کند.
        public async Task<bool> LeaveAndStopAsync(CancellationToken cancellationToken = default)
        {
            if (isStopping) return true;
            isStopping = true;
            SetState(RealtimeGameplayRoomState.Leaving);
            SetMovementSenderEnabled(false);

            bool leaveSent = true;
            if (gameServerClient != null && !string.IsNullOrWhiteSpace(roomId)) leaveSent = await gameServerClient.LeaveRoomAsync(roomId, cancellationToken);
            if (clearRemotePlayersOnStop) remoteRegistry?.ClearRemotePlayers();

            SetState(RealtimeGameplayRoomState.Stopped);
            if (logLifecycle) Debug.Log("[RealtimeGameplayRoomManager] Room stopped. room=" + roomId + " | leaveSent=" + leaveSent);
            RoomStopped?.Invoke(this);
            isStopping = false;
            return leaveSent;
        }

        //* بدون ارسال Leave، فقط binding گیم‌پلی و آبجکت‌های ریموت را پاک می‌کند.
        public void ResetGameplayBinding()
        {
            SetMovementSenderEnabled(false);
            if (clearRemotePlayersOnStop) remoteRegistry?.ClearRemotePlayers();
            UnbindGameplayEvents();
            isBound = false;
            SetState(RealtimeGameplayRoomState.Idle);
        }

        #endregion

        #region <Validation Helpers>

        //* بررسی می‌کند آبجکت ریموت مشخص در رجیستری وجود داشته باشد.
        public bool TryGetRemotePlayer(string playerId, out RealtimeRemotePlayerMovementView view)
        {
            view = null;
            if (remoteRegistry == null) return false;
            return remoteRegistry.TryGetRemoteView(playerId, out view);
        }

        #endregion

        #region <Internal Binding>

        //* کامپوننت‌های لازم صحنه را پیدا می‌کند یا در صورت فعال بودن گزینه ساخت، ایجاد می‌کند.
        private void EnsureSceneComponents()
        {
            if (remotePlayersRoot == null)
            {
                Transform existingRoot = transform.Find("RemotePlayersRoot");
                remotePlayersRoot = existingRoot != null ? existingRoot : new GameObject("RemotePlayersRoot").transform;
                if (remotePlayersRoot.parent == null) remotePlayersRoot.SetParent(transform);
            }

            if (localPlayerTransform != null && localMovementSender == null)
            {
                localMovementSender = localPlayerTransform.GetComponent<RealtimePlayerMovementSender>();
                if (localMovementSender == null && createMissingComponents) localMovementSender = localPlayerTransform.gameObject.AddComponent<RealtimePlayerMovementSender>();
            }

            if (presenceReceiver == null)
            {
                presenceReceiver = GetComponent<RealtimeRemotePlayerPresenceReceiver>();
                if (presenceReceiver == null && createMissingComponents) presenceReceiver = gameObject.AddComponent<RealtimeRemotePlayerPresenceReceiver>();
            }

            if (movementReceiver == null)
            {
                movementReceiver = GetComponent<RealtimeRemotePlayerMovementReceiver>();
                if (movementReceiver == null && createMissingComponents) movementReceiver = gameObject.AddComponent<RealtimeRemotePlayerMovementReceiver>();
            }

            if (remoteRegistry == null)
            {
                remoteRegistry = GetComponent<RealtimeRemotePlayerMovementRegistry>();
                if (remoteRegistry == null && createMissingComponents) remoteRegistry = gameObject.AddComponent<RealtimeRemotePlayerMovementRegistry>();
            }

            if (worldEventReceiver == null)
            {
                worldEventReceiver = GetComponent<RealtimeWorldEventReceiver>();
                if (worldEventReceiver == null && createMissingComponents) worldEventReceiver = gameObject.AddComponent<RealtimeWorldEventReceiver>();
            }

            if (worldEventRegistry == null)
            {
                worldEventRegistry = GetComponent<RealtimeWorldEventRegistry>();
                if (worldEventRegistry == null && createMissingComponents) worldEventRegistry = gameObject.AddComponent<RealtimeWorldEventRegistry>();
            }
        }

        //* رویدادهای registry و receiver را به خروجی‌های سطح RoomManager وصل می‌کند.
        private void BindGameplayEvents()
        {
            UnbindGameplayEvents();

            if (remoteRegistry != null)
            {
                remoteRegistry.RemotePlayerSpawned += HandleRemotePlayerSpawned;
                remoteRegistry.RemotePlayerDespawned += HandleRemotePlayerDespawned;
            }

            if (movementReceiver != null) movementReceiver.SnapshotReceived += HandleRemoteMovementSnapshotReceived;
            if (worldEventReceiver != null) worldEventReceiver.WorldEventReceived += HandleWorldEventReceived;
            if (worldEventRegistry != null) worldEventRegistry.WorldEventApplied += HandleWorldEventApplied;
        }

        //* اتصال رویدادهای داخلی را جدا می‌کند تا subscribe تکراری ایجاد نشود.
        private void UnbindGameplayEvents()
        {
            if (remoteRegistry != null)
            {
                remoteRegistry.RemotePlayerSpawned -= HandleRemotePlayerSpawned;
                remoteRegistry.RemotePlayerDespawned -= HandleRemotePlayerDespawned;
            }

            if (movementReceiver != null) movementReceiver.SnapshotReceived -= HandleRemoteMovementSnapshotReceived;
            if (worldEventReceiver != null) worldEventReceiver.WorldEventReceived -= HandleWorldEventReceived;
            if (worldEventRegistry != null) worldEventRegistry.WorldEventApplied -= HandleWorldEventApplied;
        }

        //* فعال یا غیرفعال بودن ارسال movement لوکال را کنترل می‌کند.
        private void SetMovementSenderEnabled(bool isEnabled)
        {
            if (localMovementSender == null) return;
            localMovementSender.enabled = isEnabled;
        }

        #endregion

        #region <Event Forwarding>

        //* ساخته شدن پلیر ریموت را از registry به مصرف‌کننده‌های RoomManager اعلام می‌کند.
        private void HandleRemotePlayerSpawned(string playerId, RealtimeRemotePlayerMovementView view)
        {
            if (logLifecycle) Debug.Log("[RealtimeGameplayRoomManager] Remote spawned: " + playerId);
            RemotePlayerSpawned?.Invoke(playerId, view);
        }

        //* حذف شدن پلیر ریموت را از registry به مصرف‌کننده‌های RoomManager اعلام می‌کند.
        private void HandleRemotePlayerDespawned(string playerId)
        {
            if (logLifecycle) Debug.Log("[RealtimeGameplayRoomManager] Remote despawned: " + playerId);
            RemotePlayerDespawned?.Invoke(playerId);
        }

        //* اسنپ‌شات movement ریموت را به بیرون از RoomManager اعلام می‌کند.
        private void HandleRemoteMovementSnapshotReceived(RealtimeMovementSnapshot snapshot)
        {
            RemoteMovementSnapshotReceived?.Invoke(snapshot);
        }

        //* رویداد جهان دریافتی را به مصرف‌کننده‌های RoomManager اعلام می‌کند.
        private void HandleWorldEventReceived(RealtimeWorldEventData eventData)
        {
            WorldEventReceived?.Invoke(eventData);
        }

        //* اعمال شدن world_event روی آبجکت صحنه را به مصرف‌کننده‌های RoomManager اعلام می‌کند.
        private void HandleWorldEventApplied(RealtimeWorldEventData eventData, RealtimeWorldEventTarget target)
        {
            WorldEventApplied?.Invoke(eventData, target);
        }

        #endregion

        #region <State And Errors>

        //* وضعیت داخلی را تغییر می‌دهد و event مربوطه را ارسال می‌کند.
        private void SetState(RealtimeGameplayRoomState nextState)
        {
            if (State == nextState) return;
            State = nextState;
            StateChanged?.Invoke(State);
        }

        //* خطای binding را ثبت می‌کند و وضعیت را failed می‌کند.
        private bool FailBinding(string message)
        {
            SetState(RealtimeGameplayRoomState.Failed);
            Debug.LogWarning("[RealtimeGameplayRoomManager] Binding failed: " + message);
            return false;
        }

        #endregion
    }
}
