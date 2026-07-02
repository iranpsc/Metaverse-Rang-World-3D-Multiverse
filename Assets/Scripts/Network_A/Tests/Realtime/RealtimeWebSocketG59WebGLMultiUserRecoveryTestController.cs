using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.GameServer;
using Network_A.Realtime.Auth;
using Network_A.Realtime.Core;
using Network_A.Realtime.Protocol;
using Network_A.Realtime.Stability;
using Network_A.Realtime.Transport;
using UnityEngine;

namespace Network_A.Tests.Realtime
{
    //* نقش این کلاینت را در تست ریکاوری و بازگشت WebGL چندمرورگره مشخص می‌کند.
    public enum RealtimeWebSocketG59BrowserRole
    {
        JoinOnly,
        RecoveryObserver,
        RecoveringClient
    }

    //* تست جی‌فایو نه است و قطع ناگهانی، player_left، اتصال مجدد، rejoin و دریافت دوباره پیام‌ها را در WebGL چندکاربره بررسی می‌کند.
    public class RealtimeWebSocketG59WebGLMultiUserRecoveryTestController : MonoBehaviour
    {
        [Header("Connection")]
        [SerializeField] private string serverUrl = "ws://127.0.0.1:8080";
        [SerializeField] private RealtimeTransportKind transportKind = RealtimeTransportKind.WebSocket;
        [SerializeField] private string roomId = "webgl_g59_recovery_room";

        [Header("Run")]
        [SerializeField] private bool runOnStart;
        [SerializeField] private bool readRoleFromUrl = true;
        [SerializeField] private RealtimeWebSocketG59BrowserRole defaultRole = RealtimeWebSocketG59BrowserRole.JoinOnly;
        [SerializeField] private bool leaveRoomAtEnd = true;
        [SerializeField] private bool disconnectAtEnd = true;

        [Header("Timing")]
        [SerializeField] private int connectTimeoutMs = 10000;
        [SerializeField] private int sendTimeoutMs = 10000;
        [SerializeField] private int waitTimeoutMs = 20000;
        [SerializeField] private int recoveryTimeoutMs = 60000;
        [SerializeField] private int recoveringStartDelayMs = 1500;
        [SerializeField] private int afterRecoverySendDelayMs = 1000;
        [SerializeField] private int reliableAckTimeoutMs = 5000;

        [Header("Auto Reconnect")]
        [SerializeField] private int autoReconnectInitialDelayMs = 800;
        [SerializeField] private int autoReconnectMaxAttempts = 5;
        [SerializeField] private int autoReconnectMaxDelayMs = 8000;

        [Header("Recovery Payload")]
        [SerializeField] private string recoveringPlayerId = "webgl_g59_recovering_player";
        [SerializeField] private string queuedWorldEventType = "webgl_g59_queued_during_recovery";
        [SerializeField] private string afterRecoveryWorldEventType = "webgl_g59_after_recovery";
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
        private int playerJoinedCount;
        private int playerLeftCount;
        private int playerStateCount;
        private int worldEventCount;

        private string waitingAckPrefix = string.Empty;
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

        //* منبع لغو تست را هنگام ساخت آبجکت آماده می‌کند.
        private void Awake()
        {
            lifecycleCts = new CancellationTokenSource();
        }

        //* اگر اجرای خودکار فعال باشد، نقش تست را از URL یا Inspector می‌خواند و اجرا می‌کند.
        private async void Start()
        {
            if (!runOnStart) return;
            await RunByResolvedRoleAsync();
        }

        //* هنگام حذف آبجکت، اتصال و ارکستریتور را تمیز متوقف می‌کند.
        private async void OnDestroy()
        {
            try
            {
                lifecycleCts?.Cancel();
                await CleanupAsync("G5.9 object destroyed", false);
                lifecycleCts?.Dispose();
                lifecycleCts = null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[G5.9-WebGL-Recovery] Destroy cleanup warning: " + ex.Message);
            }
        }

        #endregion

        #region <Inspector Buttons>

        //* این دکمه فقط اتصال، احراز هویت با توکن ذخیره‌شده و ورود reliable به روم را انجام می‌دهد.
        public async void ConnectAuthJoinButton()
        {
            await ConnectAuthJoinAsync();
        }

        //* این دکمه مرورگر ناظر را اجرا می‌کند و منتظر player_joined، player_left، rejoin و پیام بعد از ریکاوری می‌ماند.
        public async void RunRecoveryObserverButton()
        {
            await RunRecoveryObserverFlowAsync();
        }

        //* این دکمه مرورگر در حال ریکاوری را اجرا می‌کند و قطع شبیه‌سازی‌شده، اتوریکانکت، rejoin و ارسال بعد از ریکاوری را تست می‌کند.
        public async void RunRecoveringClientButton()
        {
            await RunRecoveringClientFlowAsync();
        }

        //* این دکمه فقط روی کلاینتی که از قبل داخل روم است، قطع شبیه‌سازی‌شده و ریکاوری را اجرا می‌کند.
        public async void SimulateDropRecoverButton()
        {
            await SimulateDropRecoverAndSendAfterRecoveryAsync();
        }

        //* این دکمه فقط یک world_event بعد از اتصال فعلی می‌فرستد.
        public async void SendWorldEventButton()
        {
            await SendWorldEventReliableAsync(afterRecoveryWorldEventType, "manual_world_event");
        }

        //* این دکمه خروج reliable از روم را برای پاکسازی تست می‌فرستد.
        public async void LeaveRoomButton()
        {
            await LeaveRoomAndWaitAckAsync();
        }

        //* این دکمه اتصال را با close استاندارد می‌بندد.
        public async void DisconnectButton()
        {
            await CleanupAsync("Manual G5.9 disconnect", false);
        }

        #endregion

        #region <Main Role Flow>

        //* نقش تست را از URL یا مقدار Inspector می‌خواند و مسیر مناسب را اجرا می‌کند.
        public async Task<bool> RunByResolvedRoleAsync()
        {
            RealtimeWebSocketG59BrowserRole role = ResolveRole();
            Log("Resolved role: " + role);

            if (role == RealtimeWebSocketG59BrowserRole.RecoveryObserver) return await RunRecoveryObserverFlowAsync();
            if (role == RealtimeWebSocketG59BrowserRole.RecoveringClient) return await RunRecoveringClientFlowAsync();

            return await ConnectAuthJoinAsync();
        }

        //* مسیر مرورگر ناظر را اجرا می‌کند و باید قبل از مرورگر RecoveringClient آماده شود.
        public async Task<bool> RunRecoveryObserverFlowAsync()
        {
            if (isRunning) return Fail("Another G5.9 flow is already running.");
            isRunning = true;

            try
            {
                Log("Recovery observer flow started.");
                ResetCounters();

                bool ready = await ConnectAuthJoinAsync();
                if (!ready) return false;

                Log("Observer is ready. Start RecoveringClient in another browser with another logged-in user.");

                bool firstJoin = await WaitForPlayerJoinedCountAsync(1);
                if (!firstJoin) return Fail("Observer did not receive first player_joined before timeout.");
                Log("Observer got first player_joined.");

                bool left = await WaitForPlayerLeftCountAsync(1);
                if (!left) return Fail("Observer did not receive player_left during simulated drop before timeout.");
                Log("Observer got player_left during recovery.");

                bool secondJoin = await WaitForPlayerJoinedCountAsync(2);
                if (!secondJoin) return Fail("Observer did not receive rejoin player_joined before timeout.");
                Log("Observer got rejoin player_joined.");

                bool stateAfterRecovery = await WaitForPlayerStateCountAsync(1);
                if (!stateAfterRecovery) return Fail("Observer did not receive player_state after recovery before timeout.");
                Log("Observer got player_state after recovery.");

                bool worldEventAfterRecovery = await WaitForWorldEventCountAsync(1);
                if (!worldEventAfterRecovery) return Fail("Observer did not receive world_event after recovery before timeout.");
                Log("Observer got world_event after recovery.");

                if (disconnectAtEnd) await CleanupAsync("G5.9 observer completed", true);

                Log("G5.9 recovery observer flow completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("Recovery observer flow canceled.");
            }
            catch (Exception ex)
            {
                return Fail("Recovery observer flow exception: " + ex.Message);
            }
            finally
            {
                isRunning = false;
            }
        }

        //* مسیر مرورگر RecoveringClient را کامل اجرا می‌کند: ورود، قطع شبیه‌سازی‌شده، صف پیام مهم، rejoin، flush و ارسال پیام بعد از ریکاوری.
        public async Task<bool> RunRecoveringClientFlowAsync()
        {
            if (isRunning) return Fail("Another G5.9 flow is already running.");
            isRunning = true;

            try
            {
                Log("Recovering client flow started.");
                await Task.Delay(Mathf.Max(0, recoveringStartDelayMs), lifecycleCts.Token);

                bool ready = await ConnectAuthJoinAsync();
                if (!ready) return false;

                bool recoveryOk = await SimulateDropRecoverAndSendAfterRecoveryAsync();
                if (!recoveryOk) return false;

                if (leaveRoomAtEnd)
                {
                    bool left = await LeaveRoomAndWaitAckAsync();
                    if (!left) return Fail("Leave room after recovery failed.");
                }

                if (disconnectAtEnd) await CleanupAsync("G5.9 recovering client completed", false);

                Log("G5.9 recovering client flow completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("Recovering client flow canceled.");
            }
            catch (Exception ex)
            {
                return Fail("Recovering client flow exception: " + ex.Message);
            }
            finally
            {
                isRunning = false;
            }
        }

        //* قطع شبیه‌سازی‌شده را اجرا می‌کند و باید بعد از اتصال، آث و جوین موفق صدا زده شود.
        public async Task<bool> SimulateDropRecoverAndSendAfterRecoveryAsync()
        {
            if (!EnsureJoinedForSend()) return false;

            ConfigureAutoReconnectOrchestrator();
            recoveryWaiter = CreateBoolWaiter();
            droppedByPolicyCount = 0;
            autoReconnectOrchestrator.Start();

            Log("Simulated WebGL network drop started.");
            await realtimeClient.DisconnectAsync("G5.9 simulated WebGL network drop", lifecycleCts.Token);

            bool playerStateDropped = await TrySendPlayerStateDuringDisconnectAsync();
            if (!playerStateDropped) return Fail("Disconnected player_state was not dropped by policy as expected.");

            bool queuedWorldEvent = await QueueWorldEventDuringDisconnectAsync();
            if (!queuedWorldEvent) return Fail("Reliable world_event was not queued during recovery gap.");
            if (realtimeClient.QueuedMessageCount <= 0) return Fail("Queue is empty after disconnected reliable world_event.");
            Log("Queue count before recovery: " + realtimeClient.QueuedMessageCount);

            bool recovered = await WaitWithTimeoutAsync(recoveryWaiter, "auto reconnect recovery", recoveryTimeoutMs, lifecycleCts.Token);
            if (!recovered) return Fail("Auto reconnect recovery failed.");

            if (!realtimeClient.IsConnected) return Fail("Realtime client is not connected after recovery.");
            if (!realtimeAuthClient.IsAuthenticated) return Fail("Realtime auth is not authenticated after recovery.");
            if (!gameServerClient.HasRoom) return Fail("GameServerClient has no active room after recovery.");
            if (realtimeClient.QueuedMessageCount != 0) return Fail("Queue was not flushed after recovery. queue=" + realtimeClient.QueuedMessageCount);

            await Task.Delay(Mathf.Max(0, afterRecoverySendDelayMs), lifecycleCts.Token);

            bool movementSent = await SendPlayerStateAfterRecoveryAsync();
            if (!movementSent) return Fail("Player state after recovery failed.");

            bool worldEventSent = await SendWorldEventReliableAsync(afterRecoveryWorldEventType, "after_recovery");
            if (!worldEventSent) return Fail("World event after recovery failed.");

            autoReconnectOrchestrator.Stop();
            Log("G5.9 simulated drop and recovery completed successfully.");
            return true;
        }

        #endregion

        #region <Connect Auth Join>

        //* اتصال، احراز هویت ریل‌تایم و ورود reliable به روم را فقط با توکن ذخیره‌شده انجام می‌دهد.
        public async Task<bool> ConnectAuthJoinAsync()
        {
            if (isJoined && realtimeClient != null && realtimeClient.IsConnected) return true;

            string storedToken = SecureTokenStorage.GetAccessToken();
            if (string.IsNullOrWhiteSpace(storedToken)) return Fail("Stored access token is empty. Login must complete before running G5.9.");

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

        //* اتصال خام ریل‌تایم را با ترنسپورت انتخاب‌شده شروع می‌کند.
        private async Task<bool> ConnectAsync()
        {
            Log("Connecting to " + serverUrl);
            bool connected = await realtimeClient.ConnectAsync(null, lifecycleCts.Token);
            Log("Connect result: " + connected);
            return connected;
        }

        //* پیام system/auth را با توکن ذخیره‌شده می‌فرستد و تا auth_ok منتظر می‌ماند.
        private async Task<bool> AuthenticateWithStoredTokenAsync()
        {
            authWaiter = CreateBoolWaiter();

            bool sent = await realtimeAuthClient.AuthenticateWithStoredTokenAsync(lifecycleCts.Token);
            if (!sent) return Fail("Realtime auth message was not sent.");

            bool ok = await WaitWithTimeoutAsync(authWaiter, "auth_ok", waitTimeoutMs, lifecycleCts.Token);
            Log("Auth result: " + ok);
            return ok;
        }

        //* درخواست ورود به روم را به صورت reliable می‌فرستد و دریافت ACK را بررسی می‌کند.
        private async Task<bool> JoinRoomReliableAsync()
        {
            RealtimeReliableSendResult result = await gameServerClient.JoinRoomReliableAsync(roomId, CreateReliableOptions(), lifecycleCts.Token);
            bool ok = result != null && result.isSuccess;
            Log("Join reliable result: " + ok + " | " + FormatReliableResult(result));
            return ok;
        }

        #endregion

        #region <Recovery Send>

        //* هنگام قطعی، یک player_state می‌فرستد تا ثابت شود latest-only قدیمی صف نمی‌شود و درست drop می‌شود.
        private async Task<bool> TrySendPlayerStateDuringDisconnectAsync()
        {
            int beforeQueue = realtimeClient.QueuedMessageCount;
            int beforeDrop = droppedByPolicyCount;
            bool sent = await gameServerClient.SendPlayerStateAsync(recoveringPlayerId, recoveryPosition, Quaternion.Euler(0f, recoveryYaw, 0f), recoveryVelocity, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), lifecycleCts.Token);
            bool wasDroppedByPolicy = !sent && droppedByPolicyCount > beforeDrop && realtimeClient.QueuedMessageCount == beforeQueue;
            Log("Disconnected player_state send result: " + sent + " | queue=" + realtimeClient.QueuedMessageCount + " | policyDrops=" + droppedByPolicyCount);
            return wasDroppedByPolicy;
        }

        //* هنگام قطعی، world_event مهم را می‌فرستد تا داخل صف reliable ذخیره شود و بعد از rejoin فلش شود.
        private async Task<bool> QueueWorldEventDuringDisconnectAsync()
        {
            RealtimeReliableSendResult result = await gameServerClient.SendWorldEventReliableAsync(queuedWorldEventType, BuildWorldPayloadJson("queued_during_recovery"), CreateReliableOptions(), lifecycleCts.Token);
            Log("Disconnected reliable world_event queued result: " + FormatReliableResult(result));
            return result != null && result.isSuccess && result.wasQueued;
        }

        //* بعد از ریکاوری، وضعیت پلیر را می‌فرستد تا مرورگر ناظر برگشت عملیاتی کلاینت را ببیند.
        private async Task<bool> SendPlayerStateAfterRecoveryAsync()
        {
            Quaternion rotation = Quaternion.Euler(0f, recoveryYaw, 0f);
            long sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool sent = await gameServerClient.SendPlayerStateAsync(recoveringPlayerId, recoveryPosition, rotation, recoveryVelocity, sequence, lifecycleCts.Token);
            Log("After recovery player_state send result: " + sent + " | sequence=" + sequence);
            return sent;
        }

        //* یک world_event reliable را می‌فرستد و ACK آن را بررسی می‌کند.
        private async Task<bool> SendWorldEventReliableAsync(string eventType, string phase)
        {
            if (!EnsureJoinedForSend()) return false;

            RealtimeReliableSendResult result = await gameServerClient.SendWorldEventReliableAsync(eventType, BuildWorldPayloadJson(phase), CreateReliableOptions(), lifecycleCts.Token);
            Log("World event reliable result: " + FormatReliableResult(result));
            return result != null && result.isSuccess && !result.wasQueued;
        }

        //* خروج از روم را می‌فرستد و ACK مربوط به leave_room را کنترل می‌کند.
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

        //* قبل از ارسال پیام gameplay مطمئن می‌شود کلاینت داخل روم است.
        private bool EnsureJoinedForSend()
        {
            if (gameServerClient == null) return Fail("GameServerClient is null.");
            if (!isJoined && !gameServerClient.HasRoom) return Fail("Client is not joined to a room.");
            return true;
        }

        //* تنظیمات ACK و retry پیام‌های reliable را برای تست می‌سازد.
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

        #region <Client Setup>

        //* کلاینت‌های ریل‌تایم، آث، گیم‌سرور و ارکستریتور را برای تست می‌سازد.
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

        //* تنظیمات اتوریکانکت را روی ارکستریتور اعمال می‌کند.
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

        //* رویدادهای کُر، آث، گیم‌سرور و اتوریکانکت را به تست وصل می‌کند.
        private void BindEvents()
        {
            if (eventsBound) return;
            eventsBound = true;

            realtimeClient.StateChanged += HandleStateChanged;
            realtimeClient.TransportErrorReceived += HandleTransportError;
            realtimeClient.Disconnected += HandleDisconnected;
            realtimeClient.QueueCountChanged += HandleQueueCountChanged;
            realtimeClient.QueueLogReceived += HandleQueueLogReceived;
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

        //* تغییر وضعیت کُر را در لاگ تست نشان می‌دهد.
        private void HandleStateChanged(RealtimeConnectionState state)
        {
            Log("State changed: " + state);
        }

        //* خطای خام ترنسپورت را در لاگ تست نشان می‌دهد.
        private void HandleTransportError(string error)
        {
            Log("Transport error: " + error);
        }

        //* قطع اتصال را در لاگ تست نشان می‌دهد.
        private void HandleDisconnected(string reason)
        {
            Log("Disconnected: " + reason);
        }

        //* تغییر تعداد صف را در لاگ تست نشان می‌دهد.
        private void HandleQueueCountChanged(int count)
        {
            Log("Queue count changed: " + count);
        }

        //* لاگ داخلی صف را در تست نشان می‌دهد.
        private void HandleQueueLogReceived(string message)
        {
            Log("Queue: " + message);
        }

        //* حذف کنترل‌شده پیام بر اساس policy را برای تست ثبت می‌کند.
        private void HandleEnvelopeDroppedByPolicy(RealtimeEnvelope envelope, RealtimeDeliveryPolicy deliveryPolicy)
        {
            droppedByPolicyCount++;
            Log("Envelope dropped by policy: " + (envelope == null ? "null" : envelope.id) + " | policy=" + deliveryPolicy);
        }

        //* لاگ مسیر reliable را در تست نشان می‌دهد.
        private void HandleReliableLogReceived(string message)
        {
            Log("Reliable: " + message);
        }

        //* تایم‌اوت ACK را در تست نشان می‌دهد.
        private void HandleReliableAckTimeout(string messageId)
        {
            Log("Reliable ack timeout: " + messageId);
        }

        //* موفقیت آث را به انتظار auth_ok وصل می‌کند.
        private void HandleAuthenticated(string connectionId, string userId)
        {
            Log("Authenticated. connectionId=" + connectionId + " userId=" + userId);
            TrySetWaiter(authWaiter, true);
        }

        //* شکست آث را به انتظار auth_ok وصل می‌کند.
        private void HandleAuthenticationFailed(RealtimeError error)
        {
            Log("Authentication failed: " + FormatError(error));
            TrySetWaiter(authWaiter, false);
        }

        //* ACKهای گیم‌سرور را برای leave_room و لاگ تست پردازش می‌کند.
        private void HandleAckReceived(GameServerAckResult ack)
        {
            if (ack == null) return;
            Log("Game ack received: " + ack.originalMessageId + " | " + ack.status);

            if (!string.IsNullOrWhiteSpace(waitingAckPrefix) && ack.originalMessageId.StartsWith(waitingAckPrefix, StringComparison.OrdinalIgnoreCase))
            {
                TrySetWaiter(ackWaiter, ack.IsProcessed());
            }
        }

        //* دریافت player_joined را می‌شمارد تا ناظر ورود اول و rejoin را تشخیص دهد.
        private void HandlePlayerJoinedReceived(GameServerPresenceEvent presence)
        {
            if (presence == null) return;
            playerJoinedCount++;
            Log("Player joined received. count=" + playerJoinedCount + " playerId=" + presence.ResolveNetworkPlayerId());
            if (playerJoinedCount >= expectedPlayerJoinedCount) TrySetWaiter(playerJoinedCountWaiter, true);
        }

        //* دریافت player_left را می‌شمارد تا ناظر خروج ناشی از قطعی را تشخیص دهد.
        private void HandlePlayerLeftReceived(GameServerPresenceEvent presence)
        {
            if (presence == null) return;
            playerLeftCount++;
            Log("Player left received. count=" + playerLeftCount + " playerId=" + presence.ResolveNetworkPlayerId());
            if (playerLeftCount >= expectedPlayerLeftCount) TrySetWaiter(playerLeftCountWaiter, true);
        }

        //* دریافت player_state را می‌شمارد تا ناظر پیام بعد از ریکاوری را تأیید کند.
        private void HandlePlayerStateReceived(RealtimeEnvelope envelope)
        {
            if (envelope == null) return;
            playerStateCount++;
            Log("Player state received. count=" + playerStateCount);
            if (playerStateCount >= expectedPlayerStateCount) TrySetWaiter(playerStateCountWaiter, true);
        }

        //* دریافت world_event را می‌شمارد تا ناظر پیام reliable بعد از ریکاوری را تأیید کند.
        private void HandleWorldEventReceived(RealtimeEnvelope envelope)
        {
            if (envelope == null) return;
            worldEventCount++;
            Log("World event received. count=" + worldEventCount + " type=" + ReadString(envelope.payloadJson, "eventType", string.Empty));
            if (worldEventCount >= expectedWorldEventCount) TrySetWaiter(worldEventCountWaiter, true);
        }

        //* خطاهای سطح گیم‌سرور را در لاگ تست نشان می‌دهد.
        private void HandleGameError(RealtimeError error)
        {
            Log("Game error: " + FormatError(error));
        }

        //* شروع اتوریکانکت را در لاگ تست نشان می‌دهد.
        private void HandleAutoReconnectStarted(string reason)
        {
            Log("Auto reconnect started: " + reason);
        }

        //* مرحله فعلی اتوریکانکت را در لاگ تست نشان می‌دهد.
        private void HandleAutoReconnectStepChanged(string step)
        {
            Log("Auto reconnect step: " + step);
        }

        //* موفقیت اتوریکانکت را به انتظار ریکاوری وصل می‌کند.
        private void HandleAutoReconnectSucceeded(int attempt)
        {
            Log("Auto reconnect succeeded. attempt=" + attempt);
            TrySetWaiter(recoveryWaiter, true);
        }

        //* شکست اتوریکانکت را به انتظار ریکاوری وصل می‌کند.
        private void HandleAutoReconnectFailed(string reason)
        {
            Log("Auto reconnect failed: " + reason);
            TrySetWaiter(recoveryWaiter, false);
        }

        //* لاگ داخلی ارکستریتور را چاپ می‌کند.
        private void HandleAutoReconnectLogReceived(string message)
        {
            Log("AutoReconnect: " + message);
        }

        #endregion

        #region <Wait Helpers>

        //* منتظر تعداد مشخصی از player_joined می‌ماند.
        private async Task<bool> WaitForPlayerJoinedCountAsync(int expectedCount)
        {
            expectedPlayerJoinedCount = expectedCount;
            if (playerJoinedCount >= expectedCount) return true;
            playerJoinedCountWaiter = CreateBoolWaiter();
            return await WaitWithTimeoutAsync(playerJoinedCountWaiter, "player_joined count " + expectedCount, waitTimeoutMs, lifecycleCts.Token);
        }

        //* منتظر تعداد مشخصی از player_left می‌ماند.
        private async Task<bool> WaitForPlayerLeftCountAsync(int expectedCount)
        {
            expectedPlayerLeftCount = expectedCount;
            if (playerLeftCount >= expectedCount) return true;
            playerLeftCountWaiter = CreateBoolWaiter();
            return await WaitWithTimeoutAsync(playerLeftCountWaiter, "player_left count " + expectedCount, waitTimeoutMs, lifecycleCts.Token);
        }

        //* منتظر تعداد مشخصی از player_state می‌ماند.
        private async Task<bool> WaitForPlayerStateCountAsync(int expectedCount)
        {
            expectedPlayerStateCount = expectedCount;
            if (playerStateCount >= expectedCount) return true;
            playerStateCountWaiter = CreateBoolWaiter();
            return await WaitWithTimeoutAsync(playerStateCountWaiter, "player_state count " + expectedCount, waitTimeoutMs, lifecycleCts.Token);
        }

        //* منتظر تعداد مشخصی از world_event می‌ماند.
        private async Task<bool> WaitForWorldEventCountAsync(int expectedCount)
        {
            expectedWorldEventCount = expectedCount;
            if (worldEventCount >= expectedCount) return true;
            worldEventCountWaiter = CreateBoolWaiter();
            return await WaitWithTimeoutAsync(worldEventCountWaiter, "world_event count " + expectedCount, waitTimeoutMs, lifecycleCts.Token);
        }

        //* انتظار ACK بعدی را برای یک پیشوند مشخص آماده می‌کند.
        private void PrepareAckWaiter(string messageIdPrefix)
        {
            waitingAckPrefix = messageIdPrefix ?? string.Empty;
            ackWaiter = CreateBoolWaiter();
        }

        //* یک انتظار بولین امن برای رویدادهای async می‌سازد.
        private static TaskCompletionSource<bool> CreateBoolWaiter()
        {
            return new TaskCompletionSource<bool>();
        }

        //* منتظر رویداد مورد نظر می‌ماند و در صورت تایم‌اوت false برمی‌گرداند.
        private async Task<bool> WaitWithTimeoutAsync(TaskCompletionSource<bool> waiter, string label, int timeoutMs, CancellationToken cancellationToken)
        {
            if (waiter == null) return false;

            Task completedTask = await Task.WhenAny(waiter.Task, Task.Delay(Math.Max(500, timeoutMs), cancellationToken));
            if (completedTask != waiter.Task)
            {
                Log("Timeout waiting for " + label);
                return false;
            }

            bool result = await waiter.Task;
            Log(label + " result: " + result);
            return result;
        }

        //* نتیجه یک انتظار را اگر هنوز کامل نشده باشد ثبت می‌کند.
        private static void TrySetWaiter(TaskCompletionSource<bool> waiter, bool value)
        {
            if (waiter == null || waiter.Task.IsCompleted) return;
            waiter.TrySetResult(value);
        }

        //* شمارنده‌های رویدادهای ناظر را صفر می‌کند.
        private void ResetCounters()
        {
            playerJoinedCount = 0;
            playerLeftCount = 0;
            playerStateCount = 0;
            worldEventCount = 0;
            expectedPlayerJoinedCount = 0;
            expectedPlayerLeftCount = 0;
            expectedPlayerStateCount = 0;
            expectedWorldEventCount = 0;
        }

        #endregion

        #region <Role Helpers>

        //* نقش تست را از query string مرورگر یا مقدار Inspector انتخاب می‌کند.
        private RealtimeWebSocketG59BrowserRole ResolveRole()
        {
            if (!readRoleFromUrl) return defaultRole;

            string role = ReadQueryValue("role");
            if (string.IsNullOrWhiteSpace(role)) return defaultRole;

            if (string.Equals(role, "observer", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG59BrowserRole.RecoveryObserver;
            if (string.Equals(role, "recovery_observer", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG59BrowserRole.RecoveryObserver;
            if (string.Equals(role, "recover", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG59BrowserRole.RecoveringClient;
            if (string.Equals(role, "recovering", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG59BrowserRole.RecoveringClient;
            if (string.Equals(role, "recovering_client", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG59BrowserRole.RecoveringClient;
            if (string.Equals(role, "join", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG59BrowserRole.JoinOnly;

            return defaultRole;
        }

        //* مقدار ساده یک query string را از آدرس WebGL می‌خواند.
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

        #endregion

        #region <Cleanup>

        //* اتصال، روم و آبجکت‌های تست را تمیز پاکسازی می‌کند.
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

        //* فقط آبجکت‌های کلاینت و انتظارهای داخلی را Dispose می‌کند.
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

        //* پِیلود world_event را با فاز فعلی و زمان تست می‌سازد.
        private string BuildWorldPayloadJson(string phase)
        {
            return "{\"source\":\"g59_webgl_recovery\",\"phase\":\"" + EscapeJson(phase) + "\",\"roomId\":\"" + EscapeJson(roomId) + "\",\"time\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}";
        }

        //* نتیجه ارسال قابل اطمینان را به متن کوتاه تبدیل می‌کند.
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

        //* خطای ریل‌تایم را به متن کوتاه تبدیل می‌کند.
        private static string FormatError(RealtimeError error)
        {
            return error == null ? "unknown" : error.code + " | " + error.message;
        }

        //* متن را برای قرار گرفتن داخل جیسون escape می‌کند.
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

        //* یک فیلد متنی ساده را از جیسون می‌خواند.
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

        //* پیام تست را با prefix ثابت چاپ می‌کند.
        private void Log(string message)
        {
            Debug.Log("[G5.9-WebGL-Recovery] " + message);
        }

        //* شکست تست را ثبت می‌کند و false برمی‌گرداند.
        private bool Fail(string message)
        {
            Debug.LogError("[G5.9-WebGL-Recovery] " + message);
            return false;
        }

        #endregion
    }
}

//* این فایل تست ریکاوری WebGL چندکاربره را برای فاز G5.9 اجرا می‌کند.
//* این تست هیچ توکن دستی نمی‌گیرد و فقط از SecureTokenStorage بعد از Login موفق استفاده می‌کند.
