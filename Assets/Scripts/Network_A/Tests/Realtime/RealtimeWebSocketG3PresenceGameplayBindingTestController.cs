using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.GameIntegration.Movement;
using Network_A.GameIntegration.Room;
using Network_A.GameServer;
using Network_A.Realtime.Auth;
using Network_A.Realtime.Core;
using Network_A.Realtime.Protocol;
using Network_A.Realtime.Stability;
using Network_A.Realtime.Transport;
using UnityEngine;

namespace Network_A.Tests.Realtime
{
    //* کنترلر تست جی‌تری است و binding واقعی پرزنس، movement، local player و remote player prefab را با RoomManager تست می‌کند.
    public class RealtimeWebSocketG3PresenceGameplayBindingTestController : MonoBehaviour
    {
        [Header("Connection")]
        [SerializeField] private string serverUrl = "ws://127.0.0.1:8080";
        [SerializeField] private RealtimeTransportKind transportKind = RealtimeTransportKind.WebSocket;
        [SerializeField] private bool runOnStart;
        [SerializeField] private bool autoDisconnectAtEnd = true;

        [Header("Auth")]
        [TextArea(2, 6)]
        [SerializeField] private string accessTokenOverride = string.Empty;
        [SerializeField] private bool useStoredTokenWhenOverrideIsEmpty = true;

        [Header("Room")]
        [SerializeField] private string roomIdPrefix = "unity_g3_room";
        [SerializeField] private string clientAPlayerIdFallback = "unity_g3_client_a";
        [SerializeField] private string clientBPlayerIdFallback = "unity_g3_client_b";
        [SerializeField] private bool useConnectionIdAsNetworkPlayerId = true;

        [Header("Movement")]
        [SerializeField] private Vector3 clientBMovementPosition = new Vector3(4f, 0f, 2.5f);
        [SerializeField] private float positionTolerance = 0.25f;

        [Header("Timeout")]
        [SerializeField] private int waitTimeoutMs = 10000;
        [SerializeField] private int reliableAckTimeoutMs = 3000;

        private RealtimeClient realtimeClientA;
        private RealtimeClient realtimeClientB;
        private RealtimeAuthClient realtimeAuthClientA;
        private RealtimeAuthClient realtimeAuthClientB;
        private GameServerClient gameServerClientA;
        private GameServerClient gameServerClientB;
        private RealtimeGameplayRoomManager roomManagerA;
        private RealtimeGameplayRoomManager roomManagerB;
        private RealtimeRemotePlayerMovementView remotePlayerPrefabA;
        private RealtimeRemotePlayerMovementView remotePlayerPrefabB;
        private GameObject localPlayerObjectA;
        private GameObject localPlayerObjectB;
        private Transform remoteRootA;
        private Transform remoteRootB;
        private CancellationTokenSource lifecycleCts;
        private TaskCompletionSource<bool> authWaiterA;
        private TaskCompletionSource<bool> authWaiterB;
        private TaskCompletionSource<string> spawnWaiterA;
        private TaskCompletionSource<string> despawnWaiterA;
        private TaskCompletionSource<RealtimeMovementSnapshot> snapshotWaiterA;
        private string activeRoomId = string.Empty;
        private string clientAConnectionId = string.Empty;
        private string clientBConnectionId = string.Empty;
        private string clientANetworkPlayerId = string.Empty;
        private string clientBNetworkPlayerId = string.Empty;
        private bool isRunning;
        private bool eventsBound;
        private bool roomEventsBound;

        #region <Unity Lifecycle>

        //* وابستگی‌های تست جی‌تری را می‌سازد تا RoomManagerها به صحنه واقعی تست وصل شوند.
        private void Awake()
        {
            lifecycleCts = new CancellationTokenSource();
            CreateClients();
            CreateGameplaySceneObjects();
        }

        //* اگر از اینسپکتور فعال باشد، تست جی‌تری را اجرا می‌کند.
        private async void Start()
        {
            if (!runOnStart) return;
            await RunG3PresenceGameplayBindingTestAsync();
        }

        //* هنگام حذف آبجکت تست، اتصال‌ها و آبجکت‌های ریموت را پاکسازی می‌کند.
        private async void OnDestroy()
        {
            await CleanupAsync();
            lifecycleCts?.Cancel();
            lifecycleCts?.Dispose();
            lifecycleCts = null;
        }

        #endregion

        #region <Inspector Buttons>

        //* اجرای دستی تست جی‌تری از اینسپکتور یا یوآی.
        public async void RunG3PresenceGameplayBindingTestButton()
        {
            await RunG3PresenceGameplayBindingTestAsync();
        }

        //* قطع دستی هر دو کلاینت تست.
        public async void DisconnectBothButton()
        {
            await DisconnectBothAsync("Manual G3 disconnect");
        }

        #endregion

        #region <Main Flow>

        //* مسیر کامل RoomManager، ورود روم، spawn/despawn و movement روی prefab واقعی صحنه را تست می‌کند.
        public async Task<bool> RunG3PresenceGameplayBindingTestAsync()
        {
            if (isRunning)
            {
                Log("G3 presence gameplay binding test is already running.");
                return false;
            }

            isRunning = true;
            activeRoomId = BuildRunRoomId();
            Log("G3 presence gameplay binding test started. room=" + activeRoomId);

            try
            {
                bool connected = await ConnectBothAsync();
                if (!connected) return Fail("Connect both clients failed.");

                bool authenticated = await AuthenticateBothAsync();
                if (!authenticated) return Fail("Auth both clients failed.");

                ResolveNetworkPlayerIds();
                InitializeRoomManagers();

                bool startedA = await StartRoomManagerAAsync();
                if (!startedA) return Fail("RoomManager A did not start.");

                bool startedBAndSpawned = await StartRoomManagerBAndWaitSpawnAsync();
                if (!startedBAndSpawned) return Fail("RoomManager B start or A remote spawn failed.");

                bool movementApplied = await MoveClientBAndValidateRemoteViewAsync();
                if (!movementApplied) return false;

                bool stoppedBAndDespawned = await StopRoomManagerBAndWaitDespawnAsync();
                if (!stoppedBAndDespawned) return Fail("RoomManager B stop or A remote despawn failed.");

                await roomManagerA.LeaveAndStopAsync(lifecycleCts.Token);
                if (autoDisconnectAtEnd) await DisconnectBothAsync("G3 completed");

                Log("G3 presence gameplay binding test completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("G3 presence gameplay binding test canceled.");
            }
            catch (Exception ex)
            {
                return Fail("G3 presence gameplay binding test exception: " + ex.Message);
            }
            finally
            {
                ClearWaiters();
                isRunning = false;
            }
        }

        //* منیجر روم کلاینت A را آماده و وارد روم می‌کند.
        private async Task<bool> StartRoomManagerAAsync()
        {
            bool ready = roomManagerA.PrepareGameplayBinding();
            if (!ready) return false;

            bool joined = await roomManagerA.JoinAndStartAsync(BuildReliableOptions(), lifecycleCts.Token);
            Log("RoomManager A start result: " + joined + " | state=" + roomManagerA.State);
            return joined && roomManagerA.IsReady;
        }

        //* منیجر روم کلاینت B را وارد روم می‌کند و منتظر spawn شدن آن در کلاینت A می‌ماند.
        private async Task<bool> StartRoomManagerBAndWaitSpawnAsync()
        {
            spawnWaiterA = new TaskCompletionSource<string>();
            bool joined = await roomManagerB.JoinAndStartAsync(BuildReliableOptions(), lifecycleCts.Token);
            Log("RoomManager B start result: " + joined + " | state=" + roomManagerB.State);
            if (!joined || !roomManagerB.IsReady) return false;

            string spawnedPlayerId = await WaitStringWithTimeoutAsync(spawnWaiterA, "client A remote spawn by RoomManager", waitTimeoutMs, lifecycleCts.Token);
            spawnWaiterA = null;

            if (!string.Equals(spawnedPlayerId, clientBNetworkPlayerId, StringComparison.OrdinalIgnoreCase)) return Fail("Spawned playerId mismatch. received=" + spawnedPlayerId + " expected=" + clientBNetworkPlayerId);
            if (!roomManagerA.TryGetRemotePlayer(clientBNetworkPlayerId, out RealtimeRemotePlayerMovementView view) || view == null) return Fail("Remote player view was not created by RoomManager A.");
            if (roomManagerA.RemotePlayerCount != 1) return Fail("RoomManager A remote count is invalid after spawn: " + roomManagerA.RemotePlayerCount);

            Log("RoomManager A spawned remote player: " + spawnedPlayerId);
            return true;
        }

        //* ترنسفورم کلاینت B را حرکت می‌دهد و بررسی می‌کند view ساخته‌شده در کلاینت A آپدیت شود.
        private async Task<bool> MoveClientBAndValidateRemoteViewAsync()
        {
            snapshotWaiterA = new TaskCompletionSource<RealtimeMovementSnapshot>();
            localPlayerObjectB.transform.position = clientBMovementPosition;
            localPlayerObjectB.transform.rotation = Quaternion.Euler(0f, 160f, 0f);

            bool sent = await roomManagerB.LocalMovementSender.ForceSendSnapshotAsync(lifecycleCts.Token);
            if (!sent) return Fail("RoomManager B local movement sender did not send snapshot.");

            RealtimeMovementSnapshot snapshot = await WaitSnapshotWithTimeoutAsync(snapshotWaiterA, "client A remote movement through RoomManager", waitTimeoutMs, lifecycleCts.Token);
            snapshotWaiterA = null;
            if (snapshot == null) return Fail("Remote movement snapshot was not received through RoomManager A.");
            if (!string.Equals(snapshot.playerId, clientBNetworkPlayerId, StringComparison.OrdinalIgnoreCase)) return Fail("Movement playerId mismatch: " + snapshot.playerId);

            if (!roomManagerA.TryGetRemotePlayer(clientBNetworkPlayerId, out RealtimeRemotePlayerMovementView view) || view == null) return Fail("Remote view missing after movement snapshot.");
            if (view.LastSequence <= 0) return Fail("Remote view was not updated by RoomManager movement binding.");
            if ((view.transform.position - clientBMovementPosition).sqrMagnitude > positionTolerance * positionTolerance) return Fail("Remote view position mismatch. received=" + view.transform.position + " expected=" + clientBMovementPosition);

            Log("RoomManager A applied movement on remote view. playerId=" + snapshot.playerId + " | seq=" + snapshot.sequence);
            return true;
        }

        //* منیجر کلاینت B را از روم خارج می‌کند و منتظر despawn شدن آبجکت ریموت در کلاینت A می‌ماند.
        private async Task<bool> StopRoomManagerBAndWaitDespawnAsync()
        {
            despawnWaiterA = new TaskCompletionSource<string>();
            bool stopped = await roomManagerB.LeaveAndStopAsync(lifecycleCts.Token);
            Log("RoomManager B stop result: " + stopped + " | state=" + roomManagerB.State);
            if (!stopped) return false;

            string despawnedPlayerId = await WaitStringWithTimeoutAsync(despawnWaiterA, "client A remote despawn by RoomManager", waitTimeoutMs, lifecycleCts.Token);
            despawnWaiterA = null;

            if (!string.Equals(despawnedPlayerId, clientBNetworkPlayerId, StringComparison.OrdinalIgnoreCase)) return Fail("Despawned playerId mismatch. received=" + despawnedPlayerId + " expected=" + clientBNetworkPlayerId);
            if (roomManagerA.TryGetRemotePlayer(clientBNetworkPlayerId, out RealtimeRemotePlayerMovementView view) && view != null) return Fail("Remote view still exists after despawn.");
            if (roomManagerA.RemotePlayerCount != 0) return Fail("RoomManager A remote count is invalid after despawn: " + roomManagerA.RemotePlayerCount);

            Log("RoomManager A despawned remote player: " + despawnedPlayerId);
            return true;
        }

        #endregion

        #region <Connection And Auth>

        //* هر دو کلاینت را به سرور وصل می‌کند.
        private async Task<bool> ConnectBothAsync()
        {
            bool connectedA = await realtimeClientA.ConnectAsync(null, lifecycleCts.Token);
            Log("Client A connect result: " + connectedA);
            if (!connectedA) return false;

            bool connectedB = await realtimeClientB.ConnectAsync(null, lifecycleCts.Token);
            Log("Client B connect result: " + connectedB);
            return connectedB;
        }

        //* هر دو کلاینت را با توکن ذخیره‌شده یا override احراز می‌کند.
        private async Task<bool> AuthenticateBothAsync()
        {
            authWaiterA = new TaskCompletionSource<bool>();
            authWaiterB = new TaskCompletionSource<bool>();

            bool sentA = await SendAuthAsync(realtimeAuthClientA);
            bool sentB = await SendAuthAsync(realtimeAuthClientB);
            if (!sentA || !sentB) return false;

            bool authA = await WaitBoolWithTimeoutAsync(authWaiterA, "client A auth_ok", waitTimeoutMs, lifecycleCts.Token);
            bool authB = await WaitBoolWithTimeoutAsync(authWaiterB, "client B auth_ok", waitTimeoutMs, lifecycleCts.Token);
            return authA && authB;
        }

        //* پیام اَث یک کلاینت را ارسال می‌کند.
        private async Task<bool> SendAuthAsync(RealtimeAuthClient authClient)
        {
            if (!string.IsNullOrWhiteSpace(accessTokenOverride)) return await authClient.AuthenticateWithAccessTokenAsync(accessTokenOverride.Trim(), lifecycleCts.Token);
            if (useStoredTokenWhenOverrideIsEmpty) return await authClient.AuthenticateWithStoredTokenAsync(lifecycleCts.Token);
            return false;
        }

        //* هر دو کلاینت را با دلیل داده‌شده قطع می‌کند.
        private async Task DisconnectBothAsync(string reason)
        {
            if (realtimeClientB != null && realtimeClientB.IsConnected) await realtimeClientB.DisconnectAsync(reason, CancellationToken.None);
            await Task.Delay(150);
            if (realtimeClientA != null && realtimeClientA.IsConnected) await realtimeClientA.DisconnectAsync(reason, CancellationToken.None);
            await Task.Delay(150);
        }

        #endregion

        #region <Setup>

        //* کلاینت‌های ریل‌تایم، اَث و گیم‌سرور را می‌سازد.
        private void CreateClients()
        {
            realtimeClientA = new RealtimeClient(CreateConfig());
            realtimeClientB = new RealtimeClient(CreateConfig());
            realtimeAuthClientA = new RealtimeAuthClient(realtimeClientA);
            realtimeAuthClientB = new RealtimeAuthClient(realtimeClientB);
            gameServerClientA = new GameServerClient(realtimeClientA);
            gameServerClientB = new GameServerClient(realtimeClientB);
            BindEvents();
        }

        //* آبجکت‌های صحنه تست را مثل استفاده واقعی گیم‌پلی می‌سازد.
        private void CreateGameplaySceneObjects()
        {
            GameObject localA = new GameObject("G3_LocalPlayer_A");
            localA.transform.SetParent(transform);
            localPlayerObjectA = localA;

            GameObject localB = new GameObject("G3_LocalPlayer_B");
            localB.transform.SetParent(transform);
            localPlayerObjectB = localB;

            GameObject rootA = new GameObject("G3_RemoteRoot_A");
            rootA.transform.SetParent(transform);
            remoteRootA = rootA.transform;

            GameObject rootB = new GameObject("G3_RemoteRoot_B");
            rootB.transform.SetParent(transform);
            remoteRootB = rootB.transform;

            remotePlayerPrefabA = CreateRemotePlayerPrefab("G3_RemotePlayerPrefab_A");
            remotePlayerPrefabB = CreateRemotePlayerPrefab("G3_RemotePlayerPrefab_B");

            GameObject managerAObject = new GameObject("G3_RoomManager_A");
            managerAObject.transform.SetParent(transform);
            roomManagerA = managerAObject.AddComponent<RealtimeGameplayRoomManager>();

            GameObject managerBObject = new GameObject("G3_RoomManager_B");
            managerBObject.transform.SetParent(transform);
            roomManagerB = managerBObject.AddComponent<RealtimeGameplayRoomManager>();
        }

        //* prefab ساده پلیر ریموت را برای تست می‌سازد.
        private RealtimeRemotePlayerMovementView CreateRemotePlayerPrefab(string objectName)
        {
            GameObject prefabObject = new GameObject(objectName);
            prefabObject.SetActive(false);
            prefabObject.transform.SetParent(transform);
            return prefabObject.AddComponent<RealtimeRemotePlayerMovementView>();
        }

        //* شناسه شبکه‌ای کلاینت‌ها را برای یکی شدن پرزنس و movement تعیین می‌کند.
        private void ResolveNetworkPlayerIds()
        {
            clientANetworkPlayerId = useConnectionIdAsNetworkPlayerId && !string.IsNullOrWhiteSpace(clientAConnectionId) ? clientAConnectionId : clientAPlayerIdFallback;
            clientBNetworkPlayerId = useConnectionIdAsNetworkPlayerId && !string.IsNullOrWhiteSpace(clientBConnectionId) ? clientBConnectionId : clientBPlayerIdFallback;
            Log("Network player ids resolved. A=" + clientANetworkPlayerId + " | B=" + clientBNetworkPlayerId);
        }

        //* RoomManagerها را با گیم‌سرورکلاینت و آبجکت‌های صحنه آماده می‌کند.
        private void InitializeRoomManagers()
        {
            roomManagerA.SetSceneReferences(localPlayerObjectA.transform, remotePlayerPrefabA, remoteRootA);
            roomManagerB.SetSceneReferences(localPlayerObjectB.transform, remotePlayerPrefabB, remoteRootB);

            roomManagerA.Initialize(gameServerClientA, clientANetworkPlayerId, activeRoomId);
            roomManagerB.Initialize(gameServerClientB, clientBNetworkPlayerId, activeRoomId);
            BindRoomEvents();
        }

        //* کانفیگ مشترک هر کلاینت را می‌سازد.
        private RealtimeConfig CreateConfig()
        {
            return new RealtimeConfig
            {
                serverUrl = serverUrl,
                transportKind = transportKind,
                connectTimeoutMs = waitTimeoutMs,
                sendTimeoutMs = waitTimeoutMs,
                autoAuthenticateAfterConnect = false,
                logIncomingMessages = true,
                logOutgoingMessages = true
            };
        }

        //* تنظیمات اَک قابل اطمینان را برای جوین می‌سازد.
        private RealtimeReliableSendOptions BuildReliableOptions()
        {
            return new RealtimeReliableSendOptions
            {
                ackTimeoutMs = reliableAckTimeoutMs,
                maxSendAttempts = 2,
                retryDelayMs = 250,
                retryOnAckTimeout = true,
                retryOnTransportSendFailed = true
            };
        }

        #endregion

        #region <Event Binding>

        //* رویدادهای شبکه‌ای تست را subscribe می‌کند.
        private void BindEvents()
        {
            if (eventsBound) return;
            eventsBound = true;

            realtimeAuthClientA.Authenticated += HandleAuthenticatedA;
            realtimeAuthClientB.Authenticated += HandleAuthenticatedB;
            realtimeAuthClientA.AuthenticationFailed += HandleAuthenticationFailedA;
            realtimeAuthClientB.AuthenticationFailed += HandleAuthenticationFailedB;
            realtimeAuthClientA.AuthLogReceived += LogClientA;
            realtimeAuthClientB.AuthLogReceived += LogClientB;
            gameServerClientA.Events.LogReceived += LogClientA;
            gameServerClientB.Events.LogReceived += LogClientB;
            gameServerClientA.Events.ErrorReceived += HandleGameErrorA;
            gameServerClientB.Events.ErrorReceived += HandleGameErrorB;
            realtimeClientA.TransportErrorReceived += HandleTransportErrorA;
            realtimeClientB.TransportErrorReceived += HandleTransportErrorB;
        }

        //* رویدادهای شبکه‌ای تست را جدا می‌کند.
        private void UnbindEvents()
        {
            if (!eventsBound) return;
            eventsBound = false;

            if (realtimeAuthClientA != null)
            {
                realtimeAuthClientA.Authenticated -= HandleAuthenticatedA;
                realtimeAuthClientA.AuthenticationFailed -= HandleAuthenticationFailedA;
                realtimeAuthClientA.AuthLogReceived -= LogClientA;
            }

            if (realtimeAuthClientB != null)
            {
                realtimeAuthClientB.Authenticated -= HandleAuthenticatedB;
                realtimeAuthClientB.AuthenticationFailed -= HandleAuthenticationFailedB;
                realtimeAuthClientB.AuthLogReceived -= LogClientB;
            }

            if (gameServerClientA != null)
            {
                gameServerClientA.Events.LogReceived -= LogClientA;
                gameServerClientA.Events.ErrorReceived -= HandleGameErrorA;
            }

            if (gameServerClientB != null)
            {
                gameServerClientB.Events.LogReceived -= LogClientB;
                gameServerClientB.Events.ErrorReceived -= HandleGameErrorB;
            }

            if (realtimeClientA != null) realtimeClientA.TransportErrorReceived -= HandleTransportErrorA;
            if (realtimeClientB != null) realtimeClientB.TransportErrorReceived -= HandleTransportErrorB;
        }

        //* رویدادهای RoomManager را برای validate کردن گیم‌پلی bind می‌کند.
        private void BindRoomEvents()
        {
            if (roomEventsBound) return;
            roomEventsBound = true;

            roomManagerA.RemotePlayerSpawned += HandleRemotePlayerSpawnedA;
            roomManagerA.RemotePlayerDespawned += HandleRemotePlayerDespawnedA;
            roomManagerA.RemoteMovementSnapshotReceived += HandleRemoteMovementSnapshotReceivedA;
            roomManagerA.StateChanged += HandleRoomManagerAStateChanged;
            roomManagerB.StateChanged += HandleRoomManagerBStateChanged;
        }

        //* رویدادهای RoomManager را جدا می‌کند.
        private void UnbindRoomEvents()
        {
            if (!roomEventsBound) return;
            roomEventsBound = false;

            if (roomManagerA != null)
            {
                roomManagerA.RemotePlayerSpawned -= HandleRemotePlayerSpawnedA;
                roomManagerA.RemotePlayerDespawned -= HandleRemotePlayerDespawnedA;
                roomManagerA.RemoteMovementSnapshotReceived -= HandleRemoteMovementSnapshotReceivedA;
                roomManagerA.StateChanged -= HandleRoomManagerAStateChanged;
            }

            if (roomManagerB != null) roomManagerB.StateChanged -= HandleRoomManagerBStateChanged;
        }

        #endregion

        #region <Event Handlers>

        //* موفقیت اَث کلاینت A را ثبت می‌کند و connectionId را نگه می‌دارد.
        private void HandleAuthenticatedA(string connectionId, string userId)
        {
            clientAConnectionId = connectionId ?? string.Empty;
            LogClientA("Authenticated: " + connectionId + " | " + userId);
            TrySetBool(authWaiterA, true);
        }

        //* موفقیت اَث کلاینت B را ثبت می‌کند و connectionId را نگه می‌دارد.
        private void HandleAuthenticatedB(string connectionId, string userId)
        {
            clientBConnectionId = connectionId ?? string.Empty;
            LogClientB("Authenticated: " + connectionId + " | " + userId);
            TrySetBool(authWaiterB, true);
        }

        //* شکست اَث کلاینت A را به waiter وصل می‌کند.
        private void HandleAuthenticationFailedA(RealtimeError error)
        {
            LogClientA("Auth failed: " + FormatError(error));
            TrySetBool(authWaiterA, false);
        }

        //* شکست اَث کلاینت B را به waiter وصل می‌کند.
        private void HandleAuthenticationFailedB(RealtimeError error)
        {
            LogClientB("Auth failed: " + FormatError(error));
            TrySetBool(authWaiterB, false);
        }

        //* خطای گیم کلاینت A را لاگ می‌کند.
        private void HandleGameErrorA(RealtimeError error)
        {
            LogClientA("Game error: " + FormatError(error));
        }

        //* خطای گیم کلاینت B را لاگ می‌کند.
        private void HandleGameErrorB(RealtimeError error)
        {
            LogClientB("Game error: " + FormatError(error));
        }

        //* خطای ترنسپورت کلاینت A را لاگ می‌کند.
        private void HandleTransportErrorA(string error)
        {
            LogClientA("Transport error: " + error);
        }

        //* خطای ترنسپورت کلاینت B را لاگ می‌کند.
        private void HandleTransportErrorB(string error)
        {
            LogClientB("Transport error: " + error);
        }

        //* ساخته شدن پلیر ریموت در RoomManager A را به waiter وصل می‌کند.
        private void HandleRemotePlayerSpawnedA(string playerId, RealtimeRemotePlayerMovementView view)
        {
            if (!string.Equals(playerId, clientBNetworkPlayerId, StringComparison.OrdinalIgnoreCase)) return;
            TrySetString(spawnWaiterA, playerId);
        }

        //* حذف شدن پلیر ریموت در RoomManager A را به waiter وصل می‌کند.
        private void HandleRemotePlayerDespawnedA(string playerId)
        {
            if (!string.Equals(playerId, clientBNetworkPlayerId, StringComparison.OrdinalIgnoreCase)) return;
            TrySetString(despawnWaiterA, playerId);
        }

        //* اسنپ‌شات movement ریموت در RoomManager A را به waiter وصل می‌کند.
        private void HandleRemoteMovementSnapshotReceivedA(RealtimeMovementSnapshot snapshot)
        {
            if (snapshot == null) return;
            if (!string.Equals(snapshot.playerId, clientBNetworkPlayerId, StringComparison.OrdinalIgnoreCase)) return;
            TrySetSnapshot(snapshotWaiterA, snapshot);
        }

        //* تغییر وضعیت RoomManager A را لاگ می‌کند.
        private void HandleRoomManagerAStateChanged(RealtimeGameplayRoomState state)
        {
            LogClientA("RoomManager state: " + state);
        }

        //* تغییر وضعیت RoomManager B را لاگ می‌کند.
        private void HandleRoomManagerBStateChanged(RealtimeGameplayRoomState state)
        {
            LogClientB("RoomManager state: " + state);
        }

        #endregion

        #region <Wait Helpers>

        //* منتظر نتیجه bool با تایم‌اوت می‌ماند.
        private async Task<bool> WaitBoolWithTimeoutAsync(TaskCompletionSource<bool> waiter, string label, int timeoutMs, CancellationToken cancellationToken)
        {
            Task completed = await Task.WhenAny(waiter.Task, Task.Delay(timeoutMs, cancellationToken));
            if (completed != waiter.Task)
            {
                Log("Timeout waiting for " + label);
                return false;
            }

            return waiter.Task.Result;
        }

        //* منتظر string با تایم‌اوت می‌ماند.
        private async Task<string> WaitStringWithTimeoutAsync(TaskCompletionSource<string> waiter, string label, int timeoutMs, CancellationToken cancellationToken)
        {
            Task completed = await Task.WhenAny(waiter.Task, Task.Delay(timeoutMs, cancellationToken));
            if (completed != waiter.Task)
            {
                Log("Timeout waiting for " + label);
                return string.Empty;
            }

            return waiter.Task.Result;
        }

        //* منتظر اسنپ‌شات movement با تایم‌اوت می‌ماند.
        private async Task<RealtimeMovementSnapshot> WaitSnapshotWithTimeoutAsync(TaskCompletionSource<RealtimeMovementSnapshot> waiter, string label, int timeoutMs, CancellationToken cancellationToken)
        {
            Task completed = await Task.WhenAny(waiter.Task, Task.Delay(timeoutMs, cancellationToken));
            if (completed != waiter.Task)
            {
                Log("Timeout waiting for " + label);
                return null;
            }

            return waiter.Task.Result;
        }

        //* مقدار bool را فقط اگر waiter کامل نشده باشد تنظیم می‌کند.
        private void TrySetBool(TaskCompletionSource<bool> waiter, bool value)
        {
            if (waiter == null || waiter.Task.IsCompleted) return;
            waiter.TrySetResult(value);
        }

        //* مقدار string را فقط اگر waiter کامل نشده باشد تنظیم می‌کند.
        private void TrySetString(TaskCompletionSource<string> waiter, string value)
        {
            if (waiter == null || waiter.Task.IsCompleted) return;
            waiter.TrySetResult(value ?? string.Empty);
        }

        //* مقدار اسنپ‌شات را فقط اگر waiter کامل نشده باشد تنظیم می‌کند.
        private void TrySetSnapshot(TaskCompletionSource<RealtimeMovementSnapshot> waiter, RealtimeMovementSnapshot snapshot)
        {
            if (waiter == null || waiter.Task.IsCompleted) return;
            waiter.TrySetResult(snapshot);
        }

        //* waiterهای جاری را پاک می‌کند.
        private void ClearWaiters()
        {
            authWaiterA = null;
            authWaiterB = null;
            spawnWaiterA = null;
            despawnWaiterA = null;
            snapshotWaiterA = null;
        }

        #endregion

        #region <Cleanup>

        //* همه منابع تست را پاکسازی می‌کند.
        private async Task CleanupAsync()
        {
            UnbindRoomEvents();
            if (roomManagerA != null) roomManagerA.ResetGameplayBinding();
            if (roomManagerB != null) roomManagerB.ResetGameplayBinding();

            UnbindEvents();
            await DisconnectBothAsync("G3 cleanup");
            gameServerClientA?.Dispose();
            gameServerClientB?.Dispose();
            realtimeAuthClientA?.Dispose();
            realtimeAuthClientB?.Dispose();
            realtimeClientA?.Dispose();
            realtimeClientB?.Dispose();
        }

        #endregion

        #region <Formatting>

        //* آیدی روم تست را با پیشوند و شناسه کوتاه می‌سازد.
        private string BuildRunRoomId()
        {
            string prefix = string.IsNullOrWhiteSpace(roomIdPrefix) ? "unity_g3_room" : roomIdPrefix.Trim();
            return prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        //* خطای ریل‌تایم را برای لاگ کوتاه می‌کند.
        private string FormatError(RealtimeError error)
        {
            if (error == null) return "unknown";
            return error.code + " | " + error.message;
        }

        //* لاگ عمومی تست را چاپ می‌کند.
        private void Log(string message)
        {
            Debug.Log("[RealtimeG3] " + message);
        }

        //* لاگ کلاینت A را چاپ می‌کند.
        private void LogClientA(string message)
        {
            Debug.Log("[RealtimeG3][A] " + message);
        }

        //* لاگ کلاینت B را چاپ می‌کند.
        private void LogClientB(string message)
        {
            Debug.Log("[RealtimeG3][B] " + message);
        }

        //* تست را با لاگ خطا false می‌کند.
        private bool Fail(string message)
        {
            Debug.LogError("[RealtimeG3] " + message);
            return false;
        }

        #endregion
    }
}
