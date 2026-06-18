using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.GameIntegration.Movement;
using Network_A.GameIntegration.Room;
using Network_A.GameIntegration.World;
using Network_A.GameServer;
using Network_A.Realtime.Auth;
using Network_A.Realtime.Core;
using Network_A.Realtime.Protocol;
using Network_A.Realtime.Stability;
using Network_A.Realtime.Transport;
using UnityEngine;

namespace Network_A.Tests.Realtime
{
    //* کنترلر تست جی‌فور است و ارسال رویداد قابل اطمینان جهان و اعمال آن روی آبجکت صحنه را تست می‌کند.
    public class RealtimeWebSocketG4WorldEventIntegrationTestController : MonoBehaviour
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
        [SerializeField] private string roomIdPrefix = "unity_g4_room";
        [SerializeField] private string clientAPlayerIdFallback = "unity_g4_client_a";
        [SerializeField] private string clientBPlayerIdFallback = "unity_g4_client_b";
        [SerializeField] private bool useConnectionIdAsNetworkPlayerId = true;

        [Header("World Event")]
        [SerializeField] private string worldObjectId = "g4_door_01";
        [SerializeField] private string worldStateKey = "isOpen";
        [SerializeField] private bool worldBoolValue = true;
        [SerializeField] private long worldEventSequence = 1;

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
        private RealtimeWorldEventTarget worldTargetA;
        private GameObject localPlayerObjectA;
        private GameObject localPlayerObjectB;
        private Transform remoteRootA;
        private Transform remoteRootB;
        private CancellationTokenSource lifecycleCts;
        private TaskCompletionSource<bool> authWaiterA;
        private TaskCompletionSource<bool> authWaiterB;
        private TaskCompletionSource<RealtimeWorldEventData> worldEventAppliedWaiterA;
        private string activeRoomId = string.Empty;
        private string clientAConnectionId = string.Empty;
        private string clientBConnectionId = string.Empty;
        private string clientANetworkPlayerId = string.Empty;
        private string clientBNetworkPlayerId = string.Empty;
        private bool isRunning;
        private bool eventsBound;
        private bool roomEventsBound;

        #region <Unity Lifecycle>

        //* وابستگی‌های تست جی‌فور را می‌سازد تا world event به RoomManager و آبجکت صحنه وصل شود.
        private void Awake()
        {
            lifecycleCts = new CancellationTokenSource();
            CreateClients();
            CreateGameplaySceneObjects();
        }

        //* اگر از اینسپکتور فعال باشد، تست جی‌فور را اجرا می‌کند.
        private async void Start()
        {
            if (!runOnStart) return;
            await RunG4WorldEventIntegrationTestAsync();
        }

        //* هنگام حذف آبجکت تست، اتصال‌ها و آبجکت‌های ساخته‌شده را پاکسازی می‌کند.
        private async void OnDestroy()
        {
            await CleanupAsync();
            lifecycleCts?.Cancel();
            lifecycleCts?.Dispose();
            lifecycleCts = null;
        }

        #endregion

        #region <Inspector Buttons>

        //* اجرای دستی تست جی‌فور از اینسپکتور یا یوآی.
        public async void RunG4WorldEventIntegrationTestButton()
        {
            await RunG4WorldEventIntegrationTestAsync();
        }

        //* قطع دستی هر دو کلاینت تست.
        public async void DisconnectBothButton()
        {
            await DisconnectBothAsync("Manual G4 disconnect");
        }

        #endregion

        #region <Main Flow>

        //* مسیر کامل ارسال world_event قابل اطمینان و اعمال آن روی آبجکت کلاینت دیگر را تست می‌کند.
        public async Task<bool> RunG4WorldEventIntegrationTestAsync()
        {
            if (isRunning)
            {
                Log("G4 world event integration test is already running.");
                return false;
            }

            isRunning = true;
            activeRoomId = BuildRunRoomId();
            Log("G4 world event integration test started. room=" + activeRoomId);

            try
            {
                bool connected = await ConnectBothAsync();
                if (!connected) return Fail("Connect both clients failed.");

                bool authenticated = await AuthenticateBothAsync();
                if (!authenticated) return Fail("Auth both clients failed.");

                ResolveNetworkPlayerIds();
                InitializeRoomManagers();

                bool started = await StartRoomManagersAsync();
                if (!started) return Fail("RoomManagers did not start.");

                bool worldEventApplied = await SendWorldEventFromBAndValidateOnAAsync();
                if (!worldEventApplied) return false;

                await roomManagerB.LeaveAndStopAsync(lifecycleCts.Token);
                await roomManagerA.LeaveAndStopAsync(lifecycleCts.Token);
                if (autoDisconnectAtEnd) await DisconnectBothAsync("G4 completed");

                Log("G4 world event integration test completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("G4 world event integration test canceled.");
            }
            catch (Exception ex)
            {
                return Fail("G4 world event integration test exception: " + ex.Message);
            }
            finally
            {
                ClearWaiters();
                isRunning = false;
            }
        }

        //* هر دو RoomManager را آماده و وارد روم مشترک می‌کند.
        private async Task<bool> StartRoomManagersAsync()
        {
            bool readyA = roomManagerA.PrepareGameplayBinding();
            if (!readyA) return false;

            bool joinedA = await roomManagerA.JoinAndStartAsync(BuildReliableOptions(), lifecycleCts.Token);
            Log("RoomManager A start result: " + joinedA + " | state=" + roomManagerA.State);
            if (!joinedA || !roomManagerA.IsReady) return false;

            bool readyB = roomManagerB.PrepareGameplayBinding();
            if (!readyB) return false;

            bool joinedB = await roomManagerB.JoinAndStartAsync(BuildReliableOptions(), lifecycleCts.Token);
            Log("RoomManager B start result: " + joinedB + " | state=" + roomManagerB.State);
            return joinedB && roomManagerB.IsReady;
        }

        //* از کلاینت B رویداد جهان می‌فرستد و بررسی می‌کند روی تارگت کلاینت A اعمال شود.
        private async Task<bool> SendWorldEventFromBAndValidateOnAAsync()
        {
            worldEventAppliedWaiterA = new TaskCompletionSource<RealtimeWorldEventData>(TaskCreationOptions.RunContinuationsAsynchronously);

            RealtimeReliableSendResult sendResult = await gameServerClientB.SendWorldObjectStateReliableAsync(
                clientBNetworkPlayerId,
                worldObjectId,
                worldStateKey,
                worldBoolValue,
                worldEventSequence,
                "open",
                1f,
                BuildReliableOptions(),
                lifecycleCts.Token
            );

            Log("World object state send result: success=" + (sendResult != null && sendResult.isSuccess) + " | attempts=" + (sendResult == null ? 0 : sendResult.attempts));
            if (sendResult == null || !sendResult.isSuccess) return Fail("World event reliable send failed: " + (sendResult == null ? "null" : sendResult.errorMessage));

            RealtimeWorldEventData eventData = await WaitWorldEventWithTimeoutAsync(worldEventAppliedWaiterA, "client A world event applied", waitTimeoutMs, lifecycleCts.Token);
            worldEventAppliedWaiterA = null;

            if (eventData == null) return Fail("World event was not applied on client A.");
            if (!string.Equals(eventData.objectId, worldObjectId, StringComparison.OrdinalIgnoreCase)) return Fail("World object id mismatch. received=" + eventData.objectId + " expected=" + worldObjectId);
            if (!string.Equals(eventData.stateKey, worldStateKey, StringComparison.OrdinalIgnoreCase)) return Fail("World state key mismatch. received=" + eventData.stateKey + " expected=" + worldStateKey);
            if (eventData.boolValue != worldBoolValue) return Fail("World bool value mismatch. received=" + eventData.boolValue + " expected=" + worldBoolValue);
            if (worldTargetA.LastSequence != worldEventSequence) return Fail("World target sequence mismatch. received=" + worldTargetA.LastSequence + " expected=" + worldEventSequence);
            if (worldTargetA.BoolState != worldBoolValue) return Fail("World target bool state mismatch. received=" + worldTargetA.BoolState + " expected=" + worldBoolValue);

            Log("World event applied on client A. object=" + eventData.objectId + " | key=" + eventData.stateKey + " | bool=" + eventData.boolValue + " | seq=" + eventData.sequence);
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
            authWaiterA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            authWaiterB = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

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
            localPlayerObjectA = new GameObject("G4_LocalPlayer_A");
            localPlayerObjectA.transform.SetParent(transform);

            localPlayerObjectB = new GameObject("G4_LocalPlayer_B");
            localPlayerObjectB.transform.SetParent(transform);

            GameObject rootA = new GameObject("G4_RemoteRoot_A");
            rootA.transform.SetParent(transform);
            remoteRootA = rootA.transform;

            GameObject rootB = new GameObject("G4_RemoteRoot_B");
            rootB.transform.SetParent(transform);
            remoteRootB = rootB.transform;

            remotePlayerPrefabA = CreateRemotePlayerPrefab("G4_RemotePlayerPrefab_A");
            remotePlayerPrefabB = CreateRemotePlayerPrefab("G4_RemotePlayerPrefab_B");

            GameObject targetObjectA = new GameObject("G4_WorldTarget_A_" + worldObjectId);
            targetObjectA.transform.SetParent(transform);
            worldTargetA = targetObjectA.AddComponent<RealtimeWorldEventTarget>();
            worldTargetA.InitializeIdentity(worldObjectId);

            GameObject managerAObject = new GameObject("G4_RoomManager_A");
            managerAObject.transform.SetParent(transform);
            roomManagerA = managerAObject.AddComponent<RealtimeGameplayRoomManager>();

            GameObject managerBObject = new GameObject("G4_RoomManager_B");
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

        //* شناسه شبکه‌ای کلاینت‌ها را برای یکی شدن پرزنس، movement و world event تعیین می‌کند.
        private void ResolveNetworkPlayerIds()
        {
            clientANetworkPlayerId = useConnectionIdAsNetworkPlayerId && !string.IsNullOrWhiteSpace(clientAConnectionId) ? clientAConnectionId : clientAPlayerIdFallback;
            clientBNetworkPlayerId = useConnectionIdAsNetworkPlayerId && !string.IsNullOrWhiteSpace(clientBConnectionId) ? clientBConnectionId : clientBPlayerIdFallback;
            Log("Network player ids resolved. A=" + clientANetworkPlayerId + " | B=" + clientBNetworkPlayerId);
        }

        //* RoomManagerها را با گیم‌سرورکلاینت، آبجکت‌های صحنه و تارگت‌های world event آماده می‌کند.
        private void InitializeRoomManagers()
        {
            roomManagerA.SetSceneReferences(localPlayerObjectA.transform, remotePlayerPrefabA, remoteRootA);
            roomManagerB.SetSceneReferences(localPlayerObjectB.transform, remotePlayerPrefabB, remoteRootB);
            roomManagerA.RegisterWorldTargets(worldTargetA);

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

        //* تنظیمات اَک قابل اطمینان را برای join و world event می‌سازد.
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

        //* رویدادهای RoomManager را برای validate کردن world event bind می‌کند.
        private void BindRoomEvents()
        {
            if (roomEventsBound) return;
            roomEventsBound = true;

            roomManagerA.WorldEventApplied += HandleWorldEventAppliedA;
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
                roomManagerA.WorldEventApplied -= HandleWorldEventAppliedA;
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

        //* اعمال شدن world_event روی RoomManager A را به waiter وصل می‌کند.
        private void HandleWorldEventAppliedA(RealtimeWorldEventData eventData, RealtimeWorldEventTarget target)
        {
            if (eventData == null || target == null) return;
            if (!string.Equals(eventData.objectId, worldObjectId, StringComparison.OrdinalIgnoreCase)) return;
            TrySetWorldEvent(worldEventAppliedWaiterA, eventData);
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

        //* منتظر اعمال شدن رویداد جهان با تایم‌اوت می‌ماند.
        private async Task<RealtimeWorldEventData> WaitWorldEventWithTimeoutAsync(TaskCompletionSource<RealtimeWorldEventData> waiter, string label, int timeoutMs, CancellationToken cancellationToken)
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

        //* مقدار world event را فقط اگر waiter کامل نشده باشد تنظیم می‌کند.
        private void TrySetWorldEvent(TaskCompletionSource<RealtimeWorldEventData> waiter, RealtimeWorldEventData value)
        {
            if (waiter == null || waiter.Task.IsCompleted) return;
            waiter.TrySetResult(value);
        }

        //* waiterهای جاری را پاک می‌کند.
        private void ClearWaiters()
        {
            authWaiterA = null;
            authWaiterB = null;
            worldEventAppliedWaiterA = null;
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
            await DisconnectBothAsync("G4 cleanup");
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
            string prefix = string.IsNullOrWhiteSpace(roomIdPrefix) ? "unity_g4_room" : roomIdPrefix.Trim();
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
            Debug.Log("[RealtimeG4] " + message);
        }

        //* لاگ کلاینت A را چاپ می‌کند.
        private void LogClientA(string message)
        {
            Debug.Log("[RealtimeG4][A] " + message);
        }

        //* لاگ کلاینت B را چاپ می‌کند.
        private void LogClientB(string message)
        {
            Debug.Log("[RealtimeG4][B] " + message);
        }

        //* تست را با لاگ خطا false می‌کند.
        private bool Fail(string message)
        {
            Debug.LogError("[RealtimeG4] " + message);
            return false;
        }

        #endregion
    }
}
