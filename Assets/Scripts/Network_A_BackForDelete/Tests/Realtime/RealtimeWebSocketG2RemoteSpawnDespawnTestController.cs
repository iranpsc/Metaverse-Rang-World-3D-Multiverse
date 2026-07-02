using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.GameIntegration.Movement;
using Network_A.GameIntegration.Presence;
using Network_A.GameServer;
using Network_A.Realtime.Auth;
using Network_A.Realtime.Core;
using Network_A.Realtime.Protocol;
using Network_A.Realtime.Stability;
using Network_A.Realtime.Transport;
using UnityEngine;

namespace Network_A.Tests.Realtime
{
    //* کنترلر تست جی‌تو است و ساخت، آپدیت و حذف آبجکت ریموت را با پرزنس و حرکت واقعی تست می‌کند.
    public class RealtimeWebSocketG2RemoteSpawnDespawnTestController : MonoBehaviour
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
        [SerializeField] private string roomIdPrefix = "unity_g2_room";
        [SerializeField] private string clientAPlayerIdFallback = "unity_g2_client_a";
        [SerializeField] private string clientBPlayerIdFallback = "unity_g2_client_b";
        [SerializeField] private bool useConnectionIdAsNetworkPlayerId = true;

        [Header("Movement")]
        [SerializeField] private Vector3 remoteMovementPosition = new Vector3(3f, 0f, 2f);
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
        private RealtimeRemotePlayerPresenceReceiver presenceReceiverA;
        private RealtimeRemotePlayerMovementReceiver movementReceiverA;
        private RealtimeRemotePlayerMovementRegistry remoteRegistryA;
        private RealtimePlayerMovementSender movementSenderB;
        private RealtimeRemotePlayerMovementView remotePlayerPrefab;
        private CancellationTokenSource lifecycleCts;
        private TaskCompletionSource<bool> authWaiterA;
        private TaskCompletionSource<bool> authWaiterB;
        private TaskCompletionSource<string> spawnWaiterA;
        private TaskCompletionSource<string> despawnWaiterA;
        private TaskCompletionSource<RealtimeMovementSnapshot> snapshotWaiterA;
        private GameObject localPlayerObjectB;
        private Transform remoteRootA;
        private string activeRoomId = string.Empty;
        private string clientAConnectionId = string.Empty;
        private string clientBConnectionId = string.Empty;
        private string clientANetworkPlayerId = string.Empty;
        private string clientBNetworkPlayerId = string.Empty;
        private bool isRunning;
        private bool eventsBound;

        #region <Unity Lifecycle>

        //* وابستگی‌های تست جی‌تو را می‌سازد تا پرزنس به ساخت و حذف آبجکت ریموت وصل شود.
        private void Awake()
        {
            lifecycleCts = new CancellationTokenSource();
            CreateClients();
            CreateGameplayObjects();
        }

        //* اگر از اینسپکتور فعال باشد، تست جی‌تو را بعد از شروع صحنه اجرا می‌کند.
        private async void Start()
        {
            if (!runOnStart) return;
            await RunG2RemoteSpawnDespawnTestAsync();
        }

        //* هنگام حذف آبجکت تست، اتصال و رویدادها را پاکسازی می‌کند.
        private async void OnDestroy()
        {
            await CleanupAsync();
            lifecycleCts?.Cancel();
            lifecycleCts?.Dispose();
            lifecycleCts = null;
        }

        #endregion

        #region <Inspector Buttons>

        //* اجرای دستی تست جی‌تو از اینسپکتور یا یوآی.
        public async void RunG2RemoteSpawnDespawnTestButton()
        {
            await RunG2RemoteSpawnDespawnTestAsync();
        }

        //* قطع دستی هر دو کلاینت تست.
        public async void DisconnectBothButton()
        {
            await DisconnectBothAsync("Manual G2 disconnect");
        }

        #endregion

        #region <Main Flow>

        //* مسیر کامل دو کلاینت، spawn با player_joined، آپدیت با player_state و despawn با player_left را تست می‌کند.
        public async Task<bool> RunG2RemoteSpawnDespawnTestAsync()
        {
            if (isRunning)
            {
                Log("G2 remote spawn/despawn test is already running.");
                return false;
            }

            isRunning = true;
            activeRoomId = BuildRunRoomId();
            Log("G2 remote spawn/despawn test started. room=" + activeRoomId);

            try
            {
                bool connected = await ConnectBothAsync();
                if (!connected) return Fail("Connect both clients failed.");

                bool authenticated = await AuthenticateBothAsync();
                if (!authenticated) return Fail("Auth both clients failed.");

                ResolveNetworkPlayerIds();
                InitializeGameplayReceivers();

                bool joinedA = await JoinClientAReliableAsync();
                if (!joinedA) return Fail("Client A join failed.");

                bool joinedBAndSpawned = await JoinClientBAndWaitSpawnAsync();
                if (!joinedBAndSpawned) return Fail("Client B join or remote spawn failed.");

                bool movementUpdated = await SendMovementFromBAndValidateViewAsync();
                if (!movementUpdated) return false;

                bool leftBAndDespawned = await LeaveClientBAndWaitDespawnAsync();
                if (!leftBAndDespawned) return Fail("Client B leave or remote despawn failed.");

                await gameServerClientA.LeaveRoomAsync(activeRoomId, lifecycleCts.Token);
                if (autoDisconnectAtEnd) await DisconnectBothAsync("G2 completed");

                Log("G2 remote spawn/despawn test completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("G2 remote spawn/despawn test canceled.");
            }
            catch (Exception ex)
            {
                return Fail("G2 remote spawn/despawn test exception: " + ex.Message);
            }
            finally
            {
                ClearWaiters();
                isRunning = false;
            }
        }

        //* کلاینت B را وارد روم می‌کند و منتظر ساخته شدن آبجکت ریموت در کلاینت A می‌ماند.
        private async Task<bool> JoinClientBAndWaitSpawnAsync()
        {
            spawnWaiterA = CreateStringWaiter();
            RealtimeReliableSendResult joinB = await gameServerClientB.JoinRoomReliableAsync(activeRoomId, BuildReliableOptions(), lifecycleCts.Token);
            Log("Client B join result: " + FormatReliableResult(joinB));
            if (joinB == null || !joinB.isSuccess) return false;

            string spawnedPlayerId = await WaitStringWithTimeoutAsync(spawnWaiterA, "client A remote spawn", waitTimeoutMs, lifecycleCts.Token);
            spawnWaiterA = null;

            if (!string.Equals(spawnedPlayerId, clientBNetworkPlayerId, StringComparison.OrdinalIgnoreCase)) return Fail("Spawned playerId mismatch. received=" + spawnedPlayerId + " expected=" + clientBNetworkPlayerId);
            if (!remoteRegistryA.TryGetRemoteView(clientBNetworkPlayerId, out RealtimeRemotePlayerMovementView view) || view == null) return Fail("Remote view was not registered after spawn.");
            if (remoteRegistryA.RemotePlayerCount != 1) return Fail("Remote player count after spawn is invalid: " + remoteRegistryA.RemotePlayerCount);

            Log("Remote player spawned on client A: " + spawnedPlayerId);
            return true;
        }

        //* حرکت کلاینت B را ارسال می‌کند و بررسی می‌کند همان آبجکت ساخته‌شده آپدیت شود.
        private async Task<bool> SendMovementFromBAndValidateViewAsync()
        {
            snapshotWaiterA = CreateSnapshotWaiter();
            localPlayerObjectB.transform.position = remoteMovementPosition;
            localPlayerObjectB.transform.rotation = Quaternion.Euler(0f, 135f, 0f);

            bool sent = await movementSenderB.ForceSendSnapshotAsync(lifecycleCts.Token);
            if (!sent) return Fail("Client B movement snapshot was not sent.");

            RealtimeMovementSnapshot snapshot = await WaitSnapshotWithTimeoutAsync(snapshotWaiterA, "client A movement snapshot", waitTimeoutMs, lifecycleCts.Token);
            snapshotWaiterA = null;
            if (snapshot == null) return Fail("Movement snapshot was not received by client A.");
            if (!string.Equals(snapshot.playerId, clientBNetworkPlayerId, StringComparison.OrdinalIgnoreCase)) return Fail("Movement playerId mismatch: " + snapshot.playerId);

            await Task.Yield();

            if (!remoteRegistryA.TryGetRemoteView(clientBNetworkPlayerId, out RealtimeRemotePlayerMovementView view) || view == null) return Fail("Remote view missing after movement snapshot.");
            if (view.LastSequence <= 0) return Fail("Remote view was not updated by movement snapshot.");
            if ((view.transform.position - remoteMovementPosition).sqrMagnitude > positionTolerance * positionTolerance) return Fail("Remote view position mismatch. received=" + view.transform.position + " expected=" + remoteMovementPosition);

            Log("Remote player movement applied on spawned view. playerId=" + snapshot.playerId + " | seq=" + snapshot.sequence);
            return true;
        }

        //* کلاینت B را از روم خارج می‌کند و منتظر حذف آبجکت ریموت در کلاینت A می‌ماند.
        private async Task<bool> LeaveClientBAndWaitDespawnAsync()
        {
            despawnWaiterA = CreateStringWaiter();
            bool sent = await gameServerClientB.LeaveRoomAsync(activeRoomId, lifecycleCts.Token);
            if (!sent) return false;

            string despawnedPlayerId = await WaitStringWithTimeoutAsync(despawnWaiterA, "client A remote despawn", waitTimeoutMs, lifecycleCts.Token);
            despawnWaiterA = null;

            if (!string.Equals(despawnedPlayerId, clientBNetworkPlayerId, StringComparison.OrdinalIgnoreCase)) return Fail("Despawned playerId mismatch. received=" + despawnedPlayerId + " expected=" + clientBNetworkPlayerId);
            if (remoteRegistryA.RemotePlayerCount != 0) return Fail("Remote player count after despawn is invalid: " + remoteRegistryA.RemotePlayerCount);
            if (remoteRegistryA.TryGetRemoteView(clientBNetworkPlayerId, out RealtimeRemotePlayerMovementView view) && view != null) return Fail("Remote view still exists after despawn.");

            Log("Remote player despawned on client A: " + despawnedPlayerId);
            return true;
        }

        #endregion

        #region <Connection Flow>

        //* هر دو کلاینت را به سرور ریل‌تایم وصل می‌کند.
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
            authWaiterA = CreateBoolWaiter();
            authWaiterB = CreateBoolWaiter();

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

        //* کلاینت A را با انتظار اَک داخلی وارد روم می‌کند.
        private async Task<bool> JoinClientAReliableAsync()
        {
            RealtimeReliableSendResult joinA = await gameServerClientA.JoinRoomReliableAsync(activeRoomId, BuildReliableOptions(), lifecycleCts.Token);
            Log("Client A join result: " + FormatReliableResult(joinA));
            return joinA != null && joinA.isSuccess;
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

        //* آبجکت‌های گیم‌پلی تست را برای sender، receiver، registry و prefab می‌سازد.
        private void CreateGameplayObjects()
        {
            remoteRootA = new GameObject("G2_RemoteRoot_A").transform;
            remoteRootA.SetParent(transform);

            GameObject prefabObject = new GameObject("G2_RemotePlayerPrefab");
            prefabObject.SetActive(false);
            prefabObject.transform.SetParent(transform);
            remotePlayerPrefab = prefabObject.AddComponent<RealtimeRemotePlayerMovementView>();

            localPlayerObjectB = new GameObject("G2_LocalPlayer_B");
            localPlayerObjectB.transform.SetParent(transform);
            movementSenderB = localPlayerObjectB.AddComponent<RealtimePlayerMovementSender>();

            presenceReceiverA = gameObject.AddComponent<RealtimeRemotePlayerPresenceReceiver>();
            movementReceiverA = gameObject.AddComponent<RealtimeRemotePlayerMovementReceiver>();
            remoteRegistryA = gameObject.AddComponent<RealtimeRemotePlayerMovementRegistry>();

            movementReceiverA.SnapshotReceived += HandleSnapshotReceivedA;
            remoteRegistryA.RemotePlayerSpawned += HandleRemotePlayerSpawnedA;
            remoteRegistryA.RemotePlayerDespawned += HandleRemotePlayerDespawnedA;
        }

        //* شناسه شبکه‌ای کلاینت‌ها را برای یکی شدن پرزنس و movement تعیین می‌کند.
        private void ResolveNetworkPlayerIds()
        {
            clientANetworkPlayerId = useConnectionIdAsNetworkPlayerId && !string.IsNullOrWhiteSpace(clientAConnectionId) ? clientAConnectionId : clientAPlayerIdFallback;
            clientBNetworkPlayerId = useConnectionIdAsNetworkPlayerId && !string.IsNullOrWhiteSpace(clientBConnectionId) ? clientBConnectionId : clientBPlayerIdFallback;
            Log("Network player ids resolved. A=" + clientANetworkPlayerId + " | B=" + clientBNetworkPlayerId);
        }

        //* receiverها و registry گیم‌پلی را بعد از احراز هویت و مشخص شدن connectionId آماده می‌کند.
        private void InitializeGameplayReceivers()
        {
            presenceReceiverA.Initialize(gameServerClientA, clientANetworkPlayerId);
            movementReceiverA.Initialize(gameServerClientA, clientANetworkPlayerId);
            remoteRegistryA.Initialize(movementReceiverA, presenceReceiverA, remotePlayerPrefab, remoteRootA);
            movementSenderB.Initialize(gameServerClientB, clientBNetworkPlayerId);
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

        //* رویدادهای لازم تست را subscribe می‌کند.
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

        //* رویدادهای تست را جدا می‌کند تا subscribe تکراری نماند.
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

        #endregion

        #region <Event Handlers>

        //* موفقیت اَث کلاینت A را به waiter وصل می‌کند و connectionId آن را نگه می‌دارد.
        private void HandleAuthenticatedA(string connectionId, string userId)
        {
            clientAConnectionId = connectionId ?? string.Empty;
            LogClientA("Authenticated: " + connectionId + " | " + userId);
            TrySetBool(authWaiterA, true);
        }

        //* موفقیت اَث کلاینت B را به waiter وصل می‌کند و connectionId آن را برای شناسه شبکه‌ای نگه می‌دارد.
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

        //* ساخته شدن آبجکت ریموت در رجیستری کلاینت A را به waiter تست وصل می‌کند.
        private void HandleRemotePlayerSpawnedA(string playerId, RealtimeRemotePlayerMovementView view)
        {
            if (!string.Equals(playerId, clientBNetworkPlayerId, StringComparison.OrdinalIgnoreCase)) return;
            TrySetString(spawnWaiterA, playerId);
        }

        //* حذف شدن آبجکت ریموت در رجیستری کلاینت A را به waiter تست وصل می‌کند.
        private void HandleRemotePlayerDespawnedA(string playerId)
        {
            if (!string.Equals(playerId, clientBNetworkPlayerId, StringComparison.OrdinalIgnoreCase)) return;
            TrySetString(despawnWaiterA, playerId);
        }

        //* اسنپ‌شات حرکت دریافتی کلاینت A را به انتظار تست وصل می‌کند.
        private void HandleSnapshotReceivedA(RealtimeMovementSnapshot snapshot)
        {
            if (snapshot == null) return;
            if (!string.Equals(snapshot.playerId, clientBNetworkPlayerId, StringComparison.OrdinalIgnoreCase)) return;
            TrySetSnapshot(snapshotWaiterA, snapshot);
        }

        #endregion

        #region <Wait Helpers>

        //* وِیتر bool را طوری می‌سازد که continuation داخل همان event handler اجرا نشود.
        private TaskCompletionSource<bool> CreateBoolWaiter()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        //* وِیتر string را طوری می‌سازد که continuation قبل از تمام شدن رویدادهای registry اجرا نشود.
        private TaskCompletionSource<string> CreateStringWaiter()
        {
            return new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        //* وِیتر اسنپ‌شات را طوری می‌سازد که اول همه listenerها مثل registry اجرا شوند، بعد تست ادامه پیدا کند.
        private TaskCompletionSource<RealtimeMovementSnapshot> CreateSnapshotWaiter()
        {
            return new TaskCompletionSource<RealtimeMovementSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

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

        //* منتظر اسنپ‌شات حرکت با تایم‌اوت می‌ماند.
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

        //* مقدار bool را فقط اگر waiter هنوز کامل نشده باشد تنظیم می‌کند.
        private void TrySetBool(TaskCompletionSource<bool> waiter, bool value)
        {
            if (waiter == null || waiter.Task.IsCompleted) return;
            waiter.TrySetResult(value);
        }

        //* مقدار string را فقط اگر waiter هنوز کامل نشده باشد تنظیم می‌کند.
        private void TrySetString(TaskCompletionSource<string> waiter, string value)
        {
            if (waiter == null || waiter.Task.IsCompleted) return;
            waiter.TrySetResult(value ?? string.Empty);
        }

        //* مقدار اسنپ‌شات را فقط اگر waiter هنوز کامل نشده باشد تنظیم می‌کند.
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
            if (movementReceiverA != null) movementReceiverA.SnapshotReceived -= HandleSnapshotReceivedA;
            if (remoteRegistryA != null)
            {
                remoteRegistryA.RemotePlayerSpawned -= HandleRemotePlayerSpawnedA;
                remoteRegistryA.RemotePlayerDespawned -= HandleRemotePlayerDespawnedA;
                remoteRegistryA.ClearRemotePlayers();
            }

            UnbindEvents();
            await DisconnectBothAsync("G2 cleanup");
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
            string prefix = string.IsNullOrWhiteSpace(roomIdPrefix) ? "unity_g2_room" : roomIdPrefix.Trim();
            return prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        //* نتیجه reliable را برای لاگ کوتاه می‌کند.
        private string FormatReliableResult(RealtimeReliableSendResult result)
        {
            if (result == null) return "null";
            return "success=" + result.isSuccess + " | attempts=" + result.attempts + " | queued=" + result.wasQueued + " | timeout=" + result.ackTimedOut + " | error=" + result.errorMessage;
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
            Debug.Log("[RealtimeG2] " + message);
        }

        //* لاگ کلاینت A را چاپ می‌کند.
        private void LogClientA(string message)
        {
            Debug.Log("[RealtimeG2][A] " + message);
        }

        //* لاگ کلاینت B را چاپ می‌کند.
        private void LogClientB(string message)
        {
            Debug.Log("[RealtimeG2][B] " + message);
        }

        //* تست را با لاگ خطا false می‌کند.
        private bool Fail(string message)
        {
            Debug.LogError("[RealtimeG2] " + message);
            return false;
        }

        #endregion
    }
}
