using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Core;
using Network_A.GameServer;
using Network_A.Realtime.Auth;
using Network_A.Realtime.Core;
using Network_A.Realtime.Protocol;
using Network_A.Realtime.Stability;
using Network_A.Realtime.Transport;
using UnityEngine;

namespace Network_A.Tests.Realtime
{
    //* نقش مرورگر را برای تست جی فایو ده مشخص می کند.
    public enum RealtimeWebSocketG510BrowserRole
    {
        JoinOnly,
        RecoveryObserver,
        RepeatedRecoveringClient
    }

    //* تست جی فایو ده است و چند چرخه قطع، ریکانکت، آث، ریجوین و دریافت پرزنس را در WebGL بررسی می کند.
    public class RealtimeWebSocketG510WebGLRepeatedRecoveryTestController : MonoBehaviour
    {
        [Header("Connection")]
        [SerializeField] private string serverUrl = "ws://127.0.0.1:8080";
        [SerializeField] private RealtimeTransportKind transportKind = RealtimeTransportKind.WebSocket;
        [SerializeField] private string roomId = "webgl_g510_repeated_recovery_room";

        [Header("Run")]
        [SerializeField] private bool runOnStart;
        [SerializeField] private bool readRoleFromUrl = true;
        [SerializeField] private RealtimeWebSocketG510BrowserRole defaultRole = RealtimeWebSocketG510BrowserRole.JoinOnly;
        [SerializeField] private bool leaveRoomAtEnd = true;
        [SerializeField] private bool disconnectAtEnd = true;

        [Header("Repeated Recovery")]
        [SerializeField] private int recoveryCycleCount = 3;
        [SerializeField] private int delayBetweenCyclesMs = 1200;
        [SerializeField] private int recoveringStartDelayMs = 1500;
        [SerializeField] private int afterRecoverySendDelayMs = 500;

        [Header("Timing")]
        [SerializeField] private int connectTimeoutMs = 10000;
        [SerializeField] private int sendTimeoutMs = 10000;
        [SerializeField] private int waitTimeoutMs = 20000;
        [SerializeField] private int recoveryTimeoutMs = 60000;
        [SerializeField] private int reliableAckTimeoutMs = 5000;

        [Header("Auto Reconnect")]
        [SerializeField] private int autoReconnectInitialDelayMs = 800;
        [SerializeField] private int autoReconnectMaxAttempts = 5;
        [SerializeField] private int autoReconnectMaxDelayMs = 8000;

        [Header("Recovery Payload")]
        [SerializeField] private string recoveringPlayerId = "webgl_g510_recovering_player";
        [SerializeField] private string queuedWorldEventType = "webgl_g510_queued_during_recovery";
        [SerializeField] private string afterRecoveryWorldEventType = "webgl_g510_after_recovery";
        [SerializeField] private Vector3 recoveryPosition = new Vector3(5f, 0f, 2f);
        [SerializeField] private Vector3 recoveryVelocity = new Vector3(0.2f, 0f, 0.1f);
        [SerializeField] private float recoveryYaw = 120f;

        private RealtimeClient realtimeClient;
        private RealtimeAuthClient realtimeAuthClient;
        private GameServerClient gameServerClient;
        private RealtimeAutoReconnectOrchestrator autoReconnectOrchestrator;
        private CancellationTokenSource lifecycleCts;

        private bool isRunning;
        private bool isJoined;
        private bool eventsBound;
        private int droppedByPolicyCount;
        private int queuedMessageDroppedCount;
        private int reliableAckTimeoutCount;
        private int duplicateAckCount;
        private int playerJoinedCount;
        private int playerLeftCount;
        private int playerStateCount;
        private int worldEventCount;
        private int maxQueueCountSeen;
        private int maxPendingAckCountSeen;

        private string waitingAckPrefix = string.Empty;
        private string lastAckMessageId = string.Empty;
        private TaskCompletionSource<bool> authWaiter;
        private TaskCompletionSource<bool> recoveryWaiter;
        private TaskCompletionSource<bool> ackWaiter;
        private TaskCompletionSource<bool> playerJoinedCountWaiter;
        private TaskCompletionSource<bool> playerLeftCountWaiter;
        private TaskCompletionSource<bool> playerStateCountWaiter;
        private TaskCompletionSource<bool> worldEventCountWaiter;
        private int expectedPlayerJoinedCount;
        private int expectedPlayerLeftCount;
        private int expectedPlayerStateCount;
        private int expectedWorldEventCount;

        #region <Unity Lifecycle>

        //* منبع لغو تست را هنگام ساخت آبجکت آماده می کند.
        private void Awake()
        {
            lifecycleCts = new CancellationTokenSource();
        }

        //* اگر اجرای خودکار فعال باشد، نقش تست را از یو آر ال یا اینسپکتور می خواند و اجرا می کند.
        private async void Start()
        {
            if (!runOnStart) return;
            await RunByResolvedRoleAsync();
        }

        //* هنگام حذف آبجکت، اتصال و ارکستریتور را تمیز متوقف می کند.
        private async void OnDestroy()
        {
            try
            {
                lifecycleCts?.Cancel();
                await CleanupAsync("G5.10 object destroyed", false);
                lifecycleCts?.Dispose();
                lifecycleCts = null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[G5.10-WebGL-RepeatedRecovery] Destroy cleanup warning: " + ex.Message);
            }
        }

        #endregion

        #region <Inspector Buttons>

        //* این دکمه فقط اتصال، آث با توکن ذخیره شده و ورود ریلایبل به روم را انجام می دهد.
        public async void ConnectAuthJoinButton()
        {
            await ConnectAuthJoinAsync();
        }

        //* این دکمه مرورگر ناظر را اجرا می کند و چند خروج و ورود دوباره را می شمارد.
        public async void RunRecoveryObserverButton()
        {
            await RunRecoveryObserverFlowAsync();
        }

        //* این دکمه مرورگر ریکاورینگ را اجرا می کند و چند بار قطع و وصل پشت سر هم را تست می کند.
        public async void RunRepeatedRecoveringClientButton()
        {
            await RunRepeatedRecoveringClientFlowAsync();
        }

        //* این دکمه فقط روی کلاینت جوین شده، یک چرخه قطع و ریکاوری را اجرا می کند.
        public async void SimulateOneDropRecoverButton()
        {
            ConfigureAutoReconnectOrchestrator();
            if (!autoReconnectOrchestrator.IsStarted) autoReconnectOrchestrator.Start();
            await RunSingleRecoveryCycleAsync(1);
        }

        //* این دکمه خروج ریلایبل از روم را برای پاکسازی تست می فرستد.
        public async void LeaveRoomButton()
        {
            await LeaveRoomAndWaitAckAsync();
        }

        //* این دکمه اتصال را با کلوز استاندارد می بندد.
        public async void DisconnectButton()
        {
            await CleanupAsync("Manual G5.10 disconnect", false);
        }

        #endregion

        #region <Main Role Flow>

        //* نقش تست را از یو آر ال یا مقدار اینسپکتور می خواند و مسیر مناسب را اجرا می کند.
        public async Task<bool> RunByResolvedRoleAsync()
        {
            RealtimeWebSocketG510BrowserRole role = ResolveRole();
            Log("Resolved role: " + role);

            if (role == RealtimeWebSocketG510BrowserRole.RecoveryObserver) return await RunRecoveryObserverFlowAsync();
            if (role == RealtimeWebSocketG510BrowserRole.RepeatedRecoveringClient) return await RunRepeatedRecoveringClientFlowAsync();

            return await ConnectAuthJoinAsync();
        }

        //* مسیر مرورگر ناظر را اجرا می کند و باید قبل از مرورگر ریکاورینگ آماده شود.
        public async Task<bool> RunRecoveryObserverFlowAsync()
        {
            if (isRunning) return Fail("Another G5.10 flow is already running.");
            isRunning = true;

            try
            {
                Log("Repeated recovery observer flow started. cycles=" + GetSafeRecoveryCycleCount());
                ResetCounters();

                bool ready = await ConnectAuthJoinAsync();
                if (!ready) return false;

                Log("Observer is ready. Start RepeatedRecoveringClient in another browser with another logged-in user.");

                bool firstJoin = await WaitForPlayerJoinedCountAsync(1);
                if (!firstJoin) return Fail("Observer did not receive first player_joined before timeout.");
                Log("Observer got first player_joined.");

                for (int cycle = 1; cycle <= GetSafeRecoveryCycleCount(); cycle++)
                {
                    bool left = await WaitForPlayerLeftCountAsync(cycle);
                    if (!left) return Fail("Observer did not receive player_left for cycle " + cycle + ".");
                    Log("Observer got player_left for cycle " + cycle + ".");

                    bool rejoin = await WaitForPlayerJoinedCountAsync(cycle + 1);
                    if (!rejoin) return Fail("Observer did not receive rejoin player_joined for cycle " + cycle + ".");
                    Log("Observer got rejoin player_joined for cycle " + cycle + ".");

                    bool stateAfterRecovery = await WaitForPlayerStateCountAsync(cycle);
                    if (!stateAfterRecovery) return Fail("Observer did not receive player_state after cycle " + cycle + ".");
                    Log("Observer got player_state after cycle " + cycle + ".");

                    bool worldEventAfterRecovery = await WaitForWorldEventCountAsync(cycle);
                    if (!worldEventAfterRecovery) return Fail("Observer did not receive world_event after cycle " + cycle + ".");
                    Log("Observer got world_event after cycle " + cycle + ".");
                }

                if (disconnectAtEnd) await CleanupAsync("G5.10 observer completed", true);

                Log("G5.10 repeated recovery observer flow completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("Repeated recovery observer flow canceled.");
            }
            catch (Exception ex)
            {
                return Fail("Repeated recovery observer flow exception: " + ex.Message);
            }
            finally
            {
                isRunning = false;
            }
        }

        //* مسیر مرورگر ریکاورینگ را اجرا می کند و چند بار پشت سر هم قطع، ریکانکت و ریجوین را بررسی می کند.
        public async Task<bool> RunRepeatedRecoveringClientFlowAsync()
        {
            if (isRunning) return Fail("Another G5.10 flow is already running.");
            isRunning = true;

            try
            {
                Log("Repeated recovering client flow started. cycles=" + GetSafeRecoveryCycleCount());
                ResetCounters();
                bool startDelayCompleted = await WaitTestDelayAsync(Mathf.Max(0, recoveringStartDelayMs), "recovering start delay", lifecycleCts.Token);
                if (!startDelayCompleted) return Fail("Recovering start delay canceled.");

                bool ready = await ConnectAuthJoinAsync();
                if (!ready) return false;

                ConfigureAutoReconnectOrchestrator();
                autoReconnectOrchestrator.Start();

                for (int cycle = 1; cycle <= GetSafeRecoveryCycleCount(); cycle++)
                {
                    bool recoveryOk = await RunSingleRecoveryCycleAsync(cycle);
                    if (!recoveryOk) return false;

                    if (cycle < GetSafeRecoveryCycleCount())
                    {
                        bool cycleDelayCompleted = await WaitTestDelayAsync(Mathf.Max(0, delayBetweenCyclesMs), "delay between recovery cycles", lifecycleCts.Token);
                        if (!cycleDelayCompleted) return Fail("Delay between recovery cycles canceled.");
                    }
                }

                autoReconnectOrchestrator.Stop();

                bool finalStateOk = ValidateCleanState("final repeated recovery state");
                if (!finalStateOk) return false;

                if (leaveRoomAtEnd)
                {
                    bool left = await LeaveRoomAndWaitAckAsync();
                    if (!left) return Fail("Leave room after repeated recovery failed.");
                }

                if (disconnectAtEnd) await CleanupAsync("G5.10 repeated recovering client completed", false);

                Log("G5.10 repeated recovering client flow completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("Repeated recovering client flow canceled.");
            }
            catch (Exception ex)
            {
                return Fail("Repeated recovering client flow exception: " + ex.Message);
            }
            finally
            {
                isRunning = false;
            }
        }

        //* یک چرخه کامل قطع شبیه سازی شده، صف پیام، ریکانکت، آث، ریجوین و ارسال بعد از ریکاوری را اجرا می کند.
        private async Task<bool> RunSingleRecoveryCycleAsync(int cycle)
        {
            if (!EnsureJoinedForSend()) return false;

            int recoveryCountBefore = autoReconnectOrchestrator.SuccessfulRecoveryCount;
            int beforeDrop = droppedByPolicyCount;
            int beforeQueuedDrop = queuedMessageDroppedCount;

            recoveryWaiter = CreateBoolWaiter();
            Log("G5.10 recovery cycle " + cycle + " started.");

            await realtimeClient.DisconnectAsync("G5.10 simulated WebGL network drop cycle " + cycle, lifecycleCts.Token);

            bool playerStateDropped = await TrySendPlayerStateDuringDisconnectAsync(cycle, beforeDrop);
            if (!playerStateDropped) return Fail("Cycle " + cycle + " disconnected player_state was not dropped by policy as expected.");

            bool queuedWorldEvent = await QueueWorldEventDuringDisconnectAsync(cycle);
            if (!queuedWorldEvent) return Fail("Cycle " + cycle + " reliable world_event was not queued during recovery gap.");
            if (realtimeClient.QueuedMessageCount <= 0) return Fail("Cycle " + cycle + " queue is empty after disconnected reliable world_event.");
            Log("Cycle " + cycle + " queue before recovery: " + realtimeClient.QueuedMessageCount);

            bool recovered = await WaitWithTimeoutAsync(recoveryWaiter, "auto reconnect recovery cycle " + cycle, recoveryTimeoutMs, lifecycleCts.Token);
            recoveryWaiter = null;
            if (!recovered) return Fail("Cycle " + cycle + " auto reconnect recovery failed.");

            if (autoReconnectOrchestrator.SuccessfulRecoveryCount <= recoveryCountBefore) return Fail("Cycle " + cycle + " recovery counter did not increase.");
            if (queuedMessageDroppedCount > beforeQueuedDrop) return Fail("Cycle " + cycle + " queued message was dropped during recovery.");

            bool cleanState = ValidateCleanState("after recovery cycle " + cycle);
            if (!cleanState) return false;

            bool afterRecoveryDelayCompleted = await WaitTestDelayAsync(Mathf.Max(0, afterRecoverySendDelayMs), "after recovery send delay", lifecycleCts.Token);
            if (!afterRecoveryDelayCompleted) return Fail("After recovery send delay canceled.");

            bool movementSent = await SendPlayerStateAfterRecoveryAsync(cycle);
            if (!movementSent) return Fail("Cycle " + cycle + " player_state after recovery failed.");

            bool worldEventSent = await SendWorldEventReliableAsync(afterRecoveryWorldEventType, "after_recovery_cycle_" + cycle, cycle);
            if (!worldEventSent) return Fail("Cycle " + cycle + " world_event after recovery failed.");

            Log("G5.10 recovery cycle " + cycle + " completed. queue=" + realtimeClient.QueuedMessageCount + " | pendingAck=" + realtimeClient.PendingAckCount);
            return true;
        }

        #endregion

        #region <Connect Auth Join>

        //* اتصال، آث ریل تایم و ورود ریلایبل به روم را فقط با توکن ذخیره شده انجام می دهد.
        public async Task<bool> ConnectAuthJoinAsync()
        {
            if (isJoined && realtimeClient != null && realtimeClient.IsConnected) return true;

            string storedToken = SecureTokenStorage.GetAccessToken();
            if (string.IsNullOrWhiteSpace(storedToken)) return Fail("Stored access token is empty. Login must complete before running G5.10.");

            CreateClients();

            bool connected = await ConnectAsync();
            if (!connected) return Fail("Realtime connect failed.");

            bool authenticated = await AuthenticateWithStoredTokenAsync();
            if (!authenticated) return Fail("Realtime auth with stored token failed.");

            bool joined = await JoinRoomReliableAsync();
            if (!joined) return Fail("Join room reliable failed.");

            isJoined = true;
            Log("Client is authenticated and joined. roomId=" + roomId);
            return true;
        }

        //* اتصال خام ریل تایم را با ترنسپورت انتخاب شده شروع می کند.
        private async Task<bool> ConnectAsync()
        {
            Log("Connecting to " + serverUrl);
            bool connected = await realtimeClient.ConnectAsync(null, lifecycleCts.Token);
            Log("Connect result: " + connected);
            return connected;
        }

        //* پیام system/auth را با توکن ذخیره شده می فرستد و تا auth_ok منتظر می ماند.
        private async Task<bool> AuthenticateWithStoredTokenAsync()
        {
            authWaiter = CreateBoolWaiter();

            bool sent = await realtimeAuthClient.AuthenticateWithStoredTokenAsync(lifecycleCts.Token);
            if (!sent) return Fail("Realtime auth message was not sent.");

            bool ok = await WaitWithTimeoutAsync(authWaiter, "auth_ok", waitTimeoutMs, lifecycleCts.Token);
            Log("Auth result: " + ok);
            return ok;
        }

        //* درخواست ورود به روم را به صورت ریلایبل می فرستد و دریافت اَک را بررسی می کند.
        private async Task<bool> JoinRoomReliableAsync()
        {
            RealtimeReliableSendResult result = await gameServerClient.JoinRoomReliableAsync(roomId, CreateReliableOptions(), lifecycleCts.Token);
            UpdateDiagnosticPeaks();
            bool ok = result != null && result.isSuccess;
            Log("Join reliable result: " + ok + " | " + FormatReliableResult(result));
            return ok;
        }

        #endregion

        #region <Recovery Send>

        //* هنگام قطعی، یک player_state می فرستد تا ثابت شود پیام لحظه ای صف نمی شود و درست drop می شود.
        private async Task<bool> TrySendPlayerStateDuringDisconnectAsync(int cycle, int beforeDrop)
        {
            int beforeQueue = realtimeClient.QueuedMessageCount;
            Vector3 position = recoveryPosition + new Vector3(cycle, 0f, cycle * 0.25f);
            bool sent = await gameServerClient.SendPlayerStateAsync(recoveringPlayerId, position, Quaternion.Euler(0f, recoveryYaw + cycle, 0f), recoveryVelocity, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), lifecycleCts.Token);
            bool wasDroppedByPolicy = !sent && droppedByPolicyCount > beforeDrop && realtimeClient.QueuedMessageCount == beforeQueue;
            Log("Cycle " + cycle + " disconnected player_state send result: " + sent + " | queue=" + realtimeClient.QueuedMessageCount + " | policyDrops=" + droppedByPolicyCount);
            return wasDroppedByPolicy;
        }

        //* هنگام قطعی، world_event مهم را می فرستد تا داخل صف ریلایبل ذخیره شود و بعد از ریجوین فلش شود.
        private async Task<bool> QueueWorldEventDuringDisconnectAsync(int cycle)
        {
            RealtimeReliableSendResult result = await gameServerClient.SendWorldEventReliableAsync(queuedWorldEventType, BuildWorldPayloadJson("queued_during_recovery_cycle_" + cycle, cycle), CreateReliableOptions(), lifecycleCts.Token);
            UpdateDiagnosticPeaks();
            Log("Cycle " + cycle + " disconnected reliable world_event queued result: " + FormatReliableResult(result));
            return result != null && result.isSuccess && result.wasQueued;
        }

        //* بعد از ریکاوری، وضعیت پلیر را می فرستد تا مرورگر ناظر برگشت عملیاتی کلاینت را ببیند.
        private async Task<bool> SendPlayerStateAfterRecoveryAsync(int cycle)
        {
            Vector3 position = recoveryPosition + new Vector3(cycle, 0f, cycle * 0.5f);
            Quaternion rotation = Quaternion.Euler(0f, recoveryYaw + cycle, 0f);
            long sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool sent = await gameServerClient.SendPlayerStateAsync(recoveringPlayerId, position, rotation, recoveryVelocity, sequence, lifecycleCts.Token);
            Log("Cycle " + cycle + " after recovery player_state send result: " + sent + " | sequence=" + sequence);
            return sent;
        }

        //* یک world_event ریلایبل را می فرستد و اَک آن را بررسی می کند.
        private async Task<bool> SendWorldEventReliableAsync(string eventType, string phase, int cycle)
        {
            if (!EnsureJoinedForSend()) return false;

            RealtimeReliableSendResult result = await gameServerClient.SendWorldEventReliableAsync(eventType, BuildWorldPayloadJson(phase, cycle), CreateReliableOptions(), lifecycleCts.Token);
            UpdateDiagnosticPeaks();
            Log("Cycle " + cycle + " world_event reliable result: " + FormatReliableResult(result));
            return result != null && result.isSuccess && !result.wasQueued;
        }

        //* خروج از روم را می فرستد و اَک مربوط به leave_room را کنترل می کند.
        private async Task<bool> LeaveRoomAndWaitAckAsync()
        {
            if (!EnsureJoinedForSend()) return false;

            PrepareAckWaiter("leave_room_");
            bool sent = await gameServerClient.LeaveRoomAsync(roomId, lifecycleCts.Token);
            Log("Leave room send result: " + sent + " | room=" + roomId);
            if (!sent) return false;

            bool acked = await WaitWithTimeoutAsync(ackWaiter, "leave_room ack", waitTimeoutMs, lifecycleCts.Token);
            if (acked) isJoined = false;
            return acked;
        }

        //* قبل از ارسال پیام گیم پلی مطمئن می شود کلاینت داخل روم است.
        private bool EnsureJoinedForSend()
        {
            if (gameServerClient == null) return Fail("GameServerClient is null.");
            if (!isJoined && !gameServerClient.HasRoom) return Fail("Client is not joined to a room.");
            return true;
        }

        //* تنظیمات اَک و retry پیام های ریلایبل را برای تست می سازد.
        private RealtimeReliableSendOptions CreateReliableOptions()
        {
            var options = new RealtimeReliableSendOptions
            {
                ackTimeoutMs = reliableAckTimeoutMs,
                maxSendAttempts = 3,
                retryDelayMs = 300,
                retryOnAckTimeout = true,
                retryOnTransportSendFailed = true
            };
            options.Normalize();
            return options;
        }

        #endregion

        #region <Validation>

        //* بعد از هر ریکاوری بررسی می کند صف، اَک و وضعیت روم تمیز باشند.
        private bool ValidateCleanState(string label)
        {
            UpdateDiagnosticPeaks();

            if (!realtimeClient.IsConnected) return Fail(label + " failed: realtime client is disconnected.");
            if (!realtimeAuthClient.IsAuthenticated) return Fail(label + " failed: auth client is not authenticated.");
            if (!gameServerClient.HasRoom) return Fail(label + " failed: game server client has no active room.");
            if (realtimeClient.QueuedMessageCount != 0) return Fail(label + " failed: queue leak detected. queue=" + realtimeClient.QueuedMessageCount);
            if (realtimeClient.PendingAckCount != 0) return Fail(label + " failed: pending ack leak detected. pendingAck=" + realtimeClient.PendingAckCount);
            if (duplicateAckCount > 0) return Fail(label + " failed: duplicate ack detected. count=" + duplicateAckCount);
            if (queuedMessageDroppedCount > 0) return Fail(label + " failed: queued message dropped. count=" + queuedMessageDroppedCount);
            if (reliableAckTimeoutCount > 0) return Fail(label + " failed: reliable ack timeout. count=" + reliableAckTimeoutCount);

            Log(label + " clean state ok. queue=0 | pendingAck=0 | recoveries=" + autoReconnectOrchestrator.SuccessfulRecoveryCount + " | maxQueue=" + maxQueueCountSeen + " | maxPendingAck=" + maxPendingAckCountSeen);
            return true;
        }

        #endregion

        #region <Client Setup>

        //* کلاینت های ریل تایم، آث، گیم سرور و ارکستریتور را برای تست می سازد.
        private void CreateClients()
        {
            CleanupClientObjectsOnly();

            var config = new RealtimeConfig
            {
                serverUrl = serverUrl,
                transportKind = transportKind,
                connectTimeoutMs = connectTimeoutMs,
                sendTimeoutMs = sendTimeoutMs,
                autoAuthenticateAfterConnect = false,
                logIncomingMessages = true,
                logOutgoingMessages = true
            };

            realtimeClient = new RealtimeClient(config);
            realtimeAuthClient = new RealtimeAuthClient(realtimeClient);
            gameServerClient = new GameServerClient(realtimeClient);
            autoReconnectOrchestrator = new RealtimeAutoReconnectOrchestrator(realtimeClient, realtimeAuthClient, gameServerClient);

            BindEvents();
        }

        //* تنظیمات اتوریکانکت را روی ارکستریتور اعمال می کند.
        private void ConfigureAutoReconnectOrchestrator()
        {
            autoReconnectOrchestrator.maxAttempts = autoReconnectMaxAttempts;
            autoReconnectOrchestrator.initialDelayMs = autoReconnectInitialDelayMs;
            autoReconnectOrchestrator.maxDelayMs = autoReconnectMaxDelayMs;
            autoReconnectOrchestrator.totalTimeoutMs = recoveryTimeoutMs;
            autoReconnectOrchestrator.delayMultiplier = 2f;
            autoReconnectOrchestrator.authTimeoutMs = waitTimeoutMs;
            autoReconnectOrchestrator.flushQueueAfterRejoin = true;
            autoReconnectOrchestrator.ignoreIntentionalDisconnects = true;
            autoReconnectOrchestrator.logAutoReconnect = true;
            autoReconnectOrchestrator.reliableOptions = CreateReliableOptions();
            autoReconnectOrchestrator.SetAuthMessageSender(realtimeAuthClient.AuthenticateWithStoredTokenAsync);
        }

        //* رویدادهای کُر، آث، گیم سرور و اتوریکانکت را به تست وصل می کند.
        private void BindEvents()
        {
            if (eventsBound) return;
            eventsBound = true;

            realtimeClient.StateChanged += HandleStateChanged;
            realtimeClient.TransportErrorReceived += HandleTransportError;
            realtimeClient.Disconnected += HandleDisconnected;
            realtimeClient.QueueCountChanged += HandleQueueCountChanged;
            realtimeClient.QueueLogReceived += HandleQueueLogReceived;
            realtimeClient.QueuedMessageDropped += HandleQueuedMessageDropped;
            realtimeClient.EnvelopeDroppedByPolicy += HandleEnvelopeDroppedByPolicy;
            realtimeClient.ReliableLogReceived += HandleReliableLogReceived;
            realtimeClient.ReliableAckTimeout += HandleReliableAckTimeout;

            realtimeAuthClient.Authenticated += HandleAuthenticated;
            realtimeAuthClient.AuthenticationFailed += HandleAuthenticationFailed;
            realtimeAuthClient.AuthLogReceived += Log;

            gameServerClient.Events.LogReceived += message => Log("Game: " + message);
            gameServerClient.Events.AckReceived += HandleAckReceived;
            gameServerClient.Events.PlayerJoinedReceived += HandlePlayerJoinedReceived;
            gameServerClient.Events.PlayerLeftReceived += HandlePlayerLeftReceived;
            gameServerClient.Events.PlayerStateReceived += HandlePlayerStateReceived;
            gameServerClient.Events.WorldEventReceived += HandleWorldEventReceived;
            gameServerClient.Events.ErrorReceived += HandleGameError;

            autoReconnectOrchestrator.AutoReconnectStarted += HandleAutoReconnectStarted;
            autoReconnectOrchestrator.AutoReconnectStepChanged += HandleAutoReconnectStepChanged;
            autoReconnectOrchestrator.AutoReconnectSucceeded += HandleAutoReconnectSucceeded;
            autoReconnectOrchestrator.AutoReconnectFailed += HandleAutoReconnectFailed;
            autoReconnectOrchestrator.AutoReconnectLogReceived += HandleAutoReconnectLogReceived;
        }

        #endregion

        #region <Event Handlers>

        //* تغییر وضعیت کُر را در لاگ تست نشان می دهد.
        private void HandleStateChanged(RealtimeConnectionState state)
        {
            UpdateDiagnosticPeaks();
            Log("State changed: " + state);
        }

        //* خطای خام ترنسپورت را در لاگ تست نشان می دهد.
        private void HandleTransportError(string error)
        {
            Log("Transport error: " + error);
        }

        //* قطع اتصال را در لاگ تست نشان می دهد.
        private void HandleDisconnected(string reason)
        {
            Log("Disconnected: " + reason);
        }

        //* تغییر تعداد صف را در لاگ تست نشان می دهد.
        private void HandleQueueCountChanged(int count)
        {
            if (count > maxQueueCountSeen) maxQueueCountSeen = count;
            Log("Queue count changed: " + count);
        }

        //* لاگ داخلی صف را در تست نشان می دهد.
        private void HandleQueueLogReceived(string message)
        {
            Log("Queue: " + message);
        }

        //* حذف پیام صف شده را در تست نشان می دهد.
        private void HandleQueuedMessageDropped(RealtimeEnvelope envelope)
        {
            queuedMessageDroppedCount++;
            Log("Queued message dropped: " + (envelope == null ? "null" : envelope.id));
        }

        //* حذف کنترل شده پیام بر اساس سیاست را برای تست ثبت می کند.
        private void HandleEnvelopeDroppedByPolicy(RealtimeEnvelope envelope, RealtimeDeliveryPolicy deliveryPolicy)
        {
            droppedByPolicyCount++;
            Log("Envelope dropped by policy: " + (envelope == null ? "null" : envelope.id) + " | policy=" + deliveryPolicy);
        }

        //* لاگ مسیر ریلایبل را در تست نشان می دهد.
        private void HandleReliableLogReceived(string message)
        {
            UpdateDiagnosticPeaks();
            Log("Reliable: " + message);
        }

        //* تایم اوت اَک را در تست نشان می دهد.
        private void HandleReliableAckTimeout(string messageId)
        {
            reliableAckTimeoutCount++;
            Log("Reliable ack timeout: " + messageId);
        }

        //* موفقیت آث را به انتظار auth_ok وصل می کند.
        private void HandleAuthenticated(string connectionId, string userId)
        {
            Log("Authenticated. connectionId=" + connectionId + " userId=" + userId);
            TrySetWaiter(authWaiter, true);
        }

        //* شکست آث را به انتظار auth_ok وصل می کند.
        private void HandleAuthenticationFailed(RealtimeError error)
        {
            Log("Authentication failed: " + FormatError(error));
            TrySetWaiter(authWaiter, false);
        }

        //* اَک های گیم سرور را برای leave_room، لاگ و تشخیص اَک تکراری پردازش می کند.
        private void HandleAckReceived(GameServerAckResult ack)
        {
            if (ack == null) return;
            Log("Game ack received: " + ack.originalMessageId + " | " + ack.status);

            if (string.Equals(lastAckMessageId, ack.originalMessageId, StringComparison.OrdinalIgnoreCase))
            {
                duplicateAckCount++;
                Log("Duplicate ack detected: " + ack.originalMessageId);
            }

            lastAckMessageId = ack.originalMessageId;

            if (!string.IsNullOrWhiteSpace(waitingAckPrefix) && ack.originalMessageId.StartsWith(waitingAckPrefix, StringComparison.OrdinalIgnoreCase))
            {
                TrySetWaiter(ackWaiter, ack.IsProcessed());
            }
        }

        //* دریافت player_joined را می شمارد تا ناظر ورود اول و ریجوین های تکراری را تشخیص دهد.
        private void HandlePlayerJoinedReceived(GameServerPresenceEvent presence)
        {
            if (presence == null) return;
            playerJoinedCount++;
            Log("Player joined received. count=" + playerJoinedCount + " playerId=" + presence.ResolveNetworkPlayerId());
            if (playerJoinedCount >= expectedPlayerJoinedCount) TrySetWaiter(playerJoinedCountWaiter, true);
        }

        //* دریافت player_left را می شمارد تا ناظر خروج ناشی از قطعی را تشخیص دهد.
        private void HandlePlayerLeftReceived(GameServerPresenceEvent presence)
        {
            if (presence == null) return;
            playerLeftCount++;
            Log("Player left received. count=" + playerLeftCount + " playerId=" + presence.ResolveNetworkPlayerId());
            if (playerLeftCount >= expectedPlayerLeftCount) TrySetWaiter(playerLeftCountWaiter, true);
        }

        //* دریافت player_state را می شمارد تا ناظر پیام بعد از هر ریکاوری را تایید کند.
        private void HandlePlayerStateReceived(RealtimeEnvelope envelope)
        {
            if (envelope == null) return;
            playerStateCount++;
            Log("Player state received. count=" + playerStateCount);
            if (playerStateCount >= expectedPlayerStateCount) TrySetWaiter(playerStateCountWaiter, true);
        }

        //* دریافت world_event را می شمارد تا ناظر پیام ریلایبل بعد از هر ریکاوری را تایید کند.
        private void HandleWorldEventReceived(RealtimeEnvelope envelope)
        {
            if (envelope == null) return;
            worldEventCount++;
            Log("World event received. count=" + worldEventCount + " type=" + ReadString(envelope.payloadJson, "eventType", string.Empty));
            if (worldEventCount >= expectedWorldEventCount) TrySetWaiter(worldEventCountWaiter, true);
        }

        //* خطاهای سطح گیم سرور را در لاگ تست نشان می دهد.
        private void HandleGameError(RealtimeError error)
        {
            Log("Game error: " + FormatError(error));
        }

        //* شروع اتوریکانکت را در لاگ تست نشان می دهد.
        private void HandleAutoReconnectStarted(string reason)
        {
            Log("Auto reconnect started: " + reason);
        }

        //* مرحله فعلی اتوریکانکت را در لاگ تست نشان می دهد.
        private void HandleAutoReconnectStepChanged(string step)
        {
            Log("Auto reconnect step: " + step);
        }

        //* موفقیت اتوریکانکت را به انتظار ریکاوری وصل می کند.
        private void HandleAutoReconnectSucceeded(int attempt)
        {
            Log("Auto reconnect succeeded. attempt=" + attempt);
            TrySetWaiter(recoveryWaiter, true);
        }

        //* شکست اتوریکانکت را به انتظار ریکاوری وصل می کند.
        private void HandleAutoReconnectFailed(string reason)
        {
            Log("Auto reconnect failed: " + reason);
            TrySetWaiter(recoveryWaiter, false);
        }

        //* لاگ داخلی ارکستریتور را چاپ می کند.
        private void HandleAutoReconnectLogReceived(string message)
        {
            Log("AutoReconnect: " + message);
        }

        #endregion

        #region <Wait Helpers>

        //* منتظر تعداد مشخصی از player_joined می ماند.
        private async Task<bool> WaitForPlayerJoinedCountAsync(int expectedCount)
        {
            expectedPlayerJoinedCount = expectedCount;
            if (playerJoinedCount >= expectedCount) return true;
            playerJoinedCountWaiter = CreateBoolWaiter();
            return await WaitWithTimeoutAsync(playerJoinedCountWaiter, "player_joined count " + expectedCount, waitTimeoutMs, lifecycleCts.Token);
        }

        //* منتظر تعداد مشخصی از player_left می ماند.
        private async Task<bool> WaitForPlayerLeftCountAsync(int expectedCount)
        {
            expectedPlayerLeftCount = expectedCount;
            if (playerLeftCount >= expectedCount) return true;
            playerLeftCountWaiter = CreateBoolWaiter();
            return await WaitWithTimeoutAsync(playerLeftCountWaiter, "player_left count " + expectedCount, waitTimeoutMs, lifecycleCts.Token);
        }

        //* منتظر تعداد مشخصی از player_state می ماند.
        private async Task<bool> WaitForPlayerStateCountAsync(int expectedCount)
        {
            expectedPlayerStateCount = expectedCount;
            if (playerStateCount >= expectedCount) return true;
            playerStateCountWaiter = CreateBoolWaiter();
            return await WaitWithTimeoutAsync(playerStateCountWaiter, "player_state count " + expectedCount, waitTimeoutMs, lifecycleCts.Token);
        }

        //* منتظر تعداد مشخصی از world_event می ماند.
        private async Task<bool> WaitForWorldEventCountAsync(int expectedCount)
        {
            expectedWorldEventCount = expectedCount;
            if (worldEventCount >= expectedCount) return true;
            worldEventCountWaiter = CreateBoolWaiter();
            return await WaitWithTimeoutAsync(worldEventCountWaiter, "world_event count " + expectedCount, waitTimeoutMs, lifecycleCts.Token);
        }

        //* انتظار اَک بعدی را برای یک پیشوند مشخص آماده می کند.
        private void PrepareAckWaiter(string messageIdPrefix)
        {
            waitingAckPrefix = messageIdPrefix ?? string.Empty;
            ackWaiter = CreateBoolWaiter();
        }

        //* یک انتظار بولین امن برای رویدادهای اَسینک می سازد.
        private static TaskCompletionSource<bool> CreateBoolWaiter()
        {
            return new TaskCompletionSource<bool>();
        }

        //* منتظر رویداد مورد نظر می ماند و در صورت تایم اوت false برمی گرداند.
        private async Task<bool> WaitWithTimeoutAsync(TaskCompletionSource<bool> waiter, string label, int timeoutMs, CancellationToken cancellationToken)
        {
            if (waiter == null) return false;

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task<bool> timeoutTask = WaitTestDelayAsync(Math.Max(500, timeoutMs), "timeout:" + label, timeoutCts.Token);
                Task completedTask = await Task.WhenAny(waiter.Task, timeoutTask);

                if (completedTask != waiter.Task)
                {
                    Log("Timeout waiting for " + label);
                    return false;
                }

                timeoutCts.Cancel();
                bool result = await waiter.Task;
                Log(label + " result: " + result);
                return result;
            }
        }

        //* تاخیرهای تست را در وب جی ال با کوروتین یونیتی جلو می برد تا مسیر تست روی تَسک دیلی مرورگر گیر نکند.
        private async Task<bool> WaitTestDelayAsync(int delayMs, string label, CancellationToken cancellationToken)
        {
            int safeDelayMs = Mathf.Max(0, delayMs);
            if (safeDelayMs <= 0) return !cancellationToken.IsCancellationRequested;

#if UNITY_WEBGL && !UNITY_EDITOR
            return await WaitTestDelayWithUnityCoroutineAsync(safeDelayMs, label, cancellationToken);
#else
            try
            {
                await Task.Delay(safeDelayMs, cancellationToken);
                return !cancellationToken.IsCancellationRequested;
            }
            catch (TaskCanceledException)
            {
                Log("Test delay canceled: " + label);
                return false;
            }
#endif
        }

        //* تاخیر تست را در وب جی ال روی مین ترد یونیتی اجرا می کند و در پایان نتیجه را به تَسک برمی گرداند.
        private async Task<bool> WaitTestDelayWithUnityCoroutineAsync(int delayMs, string label, CancellationToken cancellationToken)
        {
            var waiter = new TaskCompletionSource<bool>();
            Coroutine delayCoroutine = null;
            CancellationTokenRegistration registration = default;

            try
            {
                Log("Test unity delay started: " + label + " | delayMs=" + delayMs);
                delayCoroutine = CoroutineRunner_A.Run(CompleteTestDelayWithUnityCoroutine(delayMs, cancellationToken, waiter));

                if (cancellationToken.CanBeCanceled)
                {
                    registration = cancellationToken.Register(() =>
                    {
                        CoroutineRunner_A.Stop(delayCoroutine);
                        Log("Test unity delay canceled: " + label);
                        waiter.TrySetResult(false);
                    });
                }

                bool completed = await waiter.Task;
                if (completed) Log("Test unity delay completed: " + label);
                return completed && !cancellationToken.IsCancellationRequested;
            }
            finally
            {
                registration.Dispose();
                CoroutineRunner_A.Stop(delayCoroutine);
            }
        }

        //* کوروتین تاخیر تست را فریم به فریم جلو می برد و لغو شدن تست را هم کنترل می کند.
        private IEnumerator CompleteTestDelayWithUnityCoroutine(int delayMs, CancellationToken cancellationToken, TaskCompletionSource<bool> waiter)
        {
            float endTime = Time.realtimeSinceStartup + (Mathf.Max(0, delayMs) / 1000f);

            while (Time.realtimeSinceStartup < endTime)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    waiter.TrySetResult(false);
                    yield break;
                }

                yield return null;
            }

            waiter.TrySetResult(!cancellationToken.IsCancellationRequested);
        }

        //* نتیجه یک انتظار را اگر هنوز کامل نشده باشد ثبت می کند.
        private static void TrySetWaiter(TaskCompletionSource<bool> waiter, bool value)
        {
            if (waiter == null || waiter.Task.IsCompleted) return;
            waiter.TrySetResult(value);
        }

        //* شمارنده های رویدادهای ناظر و متریک های ریکاورینگ را صفر می کند.
        private void ResetCounters()
        {
            droppedByPolicyCount = 0;
            queuedMessageDroppedCount = 0;
            reliableAckTimeoutCount = 0;
            duplicateAckCount = 0;
            playerJoinedCount = 0;
            playerLeftCount = 0;
            playerStateCount = 0;
            worldEventCount = 0;
            maxQueueCountSeen = 0;
            maxPendingAckCountSeen = 0;
            expectedPlayerJoinedCount = 0;
            expectedPlayerLeftCount = 0;
            expectedPlayerStateCount = 0;
            expectedWorldEventCount = 0;
            lastAckMessageId = string.Empty;
        }

        //* آمار بیشینه صف و اَک های در انتظار را به روز می کند.
        private void UpdateDiagnosticPeaks()
        {
            if (realtimeClient == null) return;
            if (realtimeClient.QueuedMessageCount > maxQueueCountSeen) maxQueueCountSeen = realtimeClient.QueuedMessageCount;
            if (realtimeClient.PendingAckCount > maxPendingAckCountSeen) maxPendingAckCountSeen = realtimeClient.PendingAckCount;
        }

        //* تعداد چرخه های ریکاوری را در محدوده امن تست نگه می دارد.
        private int GetSafeRecoveryCycleCount()
        {
            int queryCycleCount = ReadIntQueryValue("cycles", recoveryCycleCount);
            return Mathf.Clamp(queryCycleCount, 1, 10);
        }

        #endregion

        #region <Role Helpers>

        //* نقش تست را از کوئری استرینگ مرورگر یا مقدار اینسپکتور انتخاب می کند.
        private RealtimeWebSocketG510BrowserRole ResolveRole()
        {
            if (!readRoleFromUrl) return defaultRole;

            string role = ReadQueryValue("role");
            if (string.IsNullOrWhiteSpace(role)) return defaultRole;

            if (string.Equals(role, "observer", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG510BrowserRole.RecoveryObserver;
            if (string.Equals(role, "recovery_observer", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG510BrowserRole.RecoveryObserver;
            if (string.Equals(role, "recover", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG510BrowserRole.RepeatedRecoveringClient;
            if (string.Equals(role, "recovering", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG510BrowserRole.RepeatedRecoveringClient;
            if (string.Equals(role, "repeated", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG510BrowserRole.RepeatedRecoveringClient;
            if (string.Equals(role, "repeated_recovering", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG510BrowserRole.RepeatedRecoveringClient;
            if (string.Equals(role, "join", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG510BrowserRole.JoinOnly;

            return defaultRole;
        }

        //* مقدار ساده یک کوئری استرینگ را از آدرس WebGL می خواند.
        private string ReadQueryValue(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return string.Empty;
            string url = Application.absoluteURL ?? string.Empty;
            int queryIndex = url.IndexOf('?');
            if (queryIndex < 0 || queryIndex >= url.Length - 1) return string.Empty;

            string query = url.Substring(queryIndex + 1);
            string[] pairs = query.Split('&');

            for (int i = 0; i < pairs.Length; i++)
            {
                string[] parts = pairs[i].Split('=');
                if (parts.Length < 2) continue;
                if (!string.Equals(parts[0], key, StringComparison.OrdinalIgnoreCase)) continue;
                return Uri.UnescapeDataString(parts[1]);
            }

            return string.Empty;
        }

        //* مقدار عددی کوئری استرینگ را می خواند و اگر معتبر نبود مقدار پیش فرض را برمی گرداند.
        private int ReadIntQueryValue(string key, int fallback)
        {
            string value = ReadQueryValue(key);
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            int parsed;
            return int.TryParse(value, out parsed) ? parsed : fallback;
        }

        #endregion

        #region <Cleanup>

        //* اتصال، روم و آبجکت های تست را تمیز پاکسازی می کند.
        private async Task CleanupAsync(string reason, bool leaveBeforeDisconnect)
        {
            try
            {
                autoReconnectOrchestrator?.Stop();

                if (leaveBeforeDisconnect && gameServerClient != null && gameServerClient.HasRoom)
                {
                    await gameServerClient.LeaveRoomAsync(null, lifecycleCts == null ? default(CancellationToken) : lifecycleCts.Token);
                }
            }
            catch (Exception ex)
            {
                Log("Leave cleanup warning: " + ex.Message);
            }

            try
            {
                if (realtimeClient != null)
                {
                    await realtimeClient.DisconnectAsync(reason, lifecycleCts == null ? default(CancellationToken) : lifecycleCts.Token);
                }
            }
            catch (Exception ex)
            {
                Log("Disconnect cleanup warning: " + ex.Message);
            }

            isJoined = false;
            CleanupClientObjectsOnly();
        }

        //* فقط آبجکت های کلاینت و انتظارهای داخلی را Dispose می کند.
        private void CleanupClientObjectsOnly()
        {
            eventsBound = false;

            autoReconnectOrchestrator?.Dispose();
            autoReconnectOrchestrator = null;

            gameServerClient?.Dispose();
            gameServerClient = null;

            realtimeAuthClient?.Dispose();
            realtimeAuthClient = null;

            realtimeClient?.Dispose();
            realtimeClient = null;

            authWaiter = null;
            recoveryWaiter = null;
            ackWaiter = null;
            playerJoinedCountWaiter = null;
            playerLeftCountWaiter = null;
            playerStateCountWaiter = null;
            worldEventCountWaiter = null;
            waitingAckPrefix = string.Empty;
            isJoined = false;
        }

        #endregion

        #region <Format Helpers>

        //* پِیلود world_event را با فاز، شماره چرخه و زمان تست می سازد.
        private string BuildWorldPayloadJson(string phase, int cycle)
        {
            return "{\"source\":\"g510_webgl_repeated_recovery\",\"phase\":\"" + EscapeJson(phase) + "\",\"cycle\":" + cycle + ",\"roomId\":\"" + EscapeJson(roomId) + "\",\"time\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}";
        }

        //* نتیجه ارسال قابل اطمینان را به متن کوتاه تبدیل می کند.
        private static string FormatReliableResult(RealtimeReliableSendResult result)
        {
            if (result == null) return "null";
            return "success=" + result.isSuccess
                + " | queued=" + result.wasQueued
                + " | dropped=" + result.wasDropped
                + " | timeout=" + result.ackTimedOut
                + " | attempts=" + result.attempts
                + " | messageId=" + result.messageId
                + " | status=" + result.ackStatus
                + " | error=" + result.errorMessage;
        }

        //* خطای ریل تایم را به متن کوتاه تبدیل می کند.
        private static string FormatError(RealtimeError error)
        {
            return error == null ? "unknown" : error.code + " | " + error.message;
        }

        //* متن را برای قرار گرفتن داخل جیسون escape می کند.
        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }

        //* یک فیلد متنی ساده را از جیسون می خواند.
        private static string ReadString(string json, string key, string fallback)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key)) return fallback;

            string pattern = "\"" + key + "\"";
            int keyIndex = json.IndexOf(pattern, StringComparison.Ordinal);
            if (keyIndex < 0) return fallback;

            int colonIndex = json.IndexOf(':', keyIndex + pattern.Length);
            if (colonIndex < 0) return fallback;

            int valueStart = colonIndex + 1;
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart])) valueStart++;
            if (valueStart >= json.Length || json[valueStart] != '\"') return fallback;

            int textStart = valueStart + 1;
            var result = new System.Text.StringBuilder();

            for (int i = textStart; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    i++;
                    result.Append(json[i]);
                    continue;
                }

                if (c == '\"') return result.ToString();
                result.Append(c);
            }

            return fallback;
        }

        //* پیام تست را با prefix ثابت چاپ می کند.
        private void Log(string message)
        {
            Debug.Log("[G5.10-WebGL-RepeatedRecovery] " + message);
        }

        //* شکست تست را ثبت می کند و false برمی گرداند.
        private bool Fail(string message)
        {
            Debug.LogError("[G5.10-WebGL-RepeatedRecovery] " + message);
            return false;
        }

        #endregion
    }
}

//* این فایل تست ریکاوری تکراری WebGL چندکاربره را برای فاز G5.10 اجرا می کند.
//* این تست هیچ توکن دستی نمی گیرد و فقط از SecureTokenStorage بعد از Login موفق استفاده می کند.
