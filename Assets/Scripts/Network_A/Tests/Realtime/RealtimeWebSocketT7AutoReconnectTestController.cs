using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.GameServer;
using Network_A.Realtime.Auth;
using Network_A.Realtime.Core;
using Network_A.Realtime.Protocol;
using Network_A.Realtime.Stability;
using Network_A.Realtime.Transport;
using UnityEngine;

namespace Network_A.Tests.Realtime
{
    //* کنترلر تست تی‌سون است و ریکانکت خودکار، آث دوباره، جوین دوباره و فلش صف با اَک را بررسی می‌کند.
    public class RealtimeWebSocketT7AutoReconnectTestController : MonoBehaviour
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
        [SerializeField] private string roomIdPrefix = "unity_t7_room";
        [SerializeField] private string beforeDisconnectActionType = "unity_t7_before_disconnect";
        [SerializeField] private string queuedActionType = "unity_t7_queued_during_auto_reconnect";
        [SerializeField] private string afterRecoveryActionType = "unity_t7_after_recovery";

        [Header("Timeout")]
        [SerializeField] private int waitTimeoutMs = 10000;
        [SerializeField] private int recoveryTimeoutMs = 60000;
        [SerializeField] private int autoReconnectInitialDelayMs = 800;
        [SerializeField] private int autoReconnectMaxAttempts = 4;

        [Header("Reliable Ack")]
        [SerializeField] private int reliableAckTimeoutMs = 3000;
        [SerializeField] private int reliableMaxSendAttempts = 2;
        [SerializeField] private int reliableRetryDelayMs = 250;

        private RealtimeClient realtimeClient;
        private RealtimeAuthClient realtimeAuthClient;
        private GameServerClient gameServerClient;
        private RealtimeAutoReconnectOrchestrator autoReconnectOrchestrator;
        private CancellationTokenSource lifecycleCts;
        private TaskCompletionSource<bool> authWaiter;
        private TaskCompletionSource<bool> ackWaiter;
        private TaskCompletionSource<bool> recoveryWaiter;
        private string waitingAckPrefix = string.Empty;
        private string activeRoomId = string.Empty;
        private int droppedByPolicyCount;
        private bool isRunning;
        private bool eventsBound;

        #region <Unity Lifecycle>

        //* وابستگی‌های تست را می‌سازد تا تست ریکانکت خودکار از همان کلاینت‌های واقعی استفاده کند.
        private void Awake()
        {
            lifecycleCts = new CancellationTokenSource();
            CreateClients();
        }

        //* اگر از اینسپکتور فعال باشد، تست تی‌سون را بعد از شروع صحنه اجرا می‌کند.
        private async void Start()
        {
            if (!runOnStart) return;
            await RunAutoReconnectT7TestAsync();
        }

        //* هنگام حذف آبجکت، اتصال و رویدادها را پاکسازی می‌کند.
        private async void OnDestroy()
        {
            lifecycleCts?.Cancel();
            await CleanupAsync();
            lifecycleCts?.Dispose();
            lifecycleCts = null;
        }

        #endregion

        #region <Inspector Buttons>

        //* از اینسپکتور یا دکمه یوآی برای اجرای تست ریکانکت خودکار صدا زده می‌شود.
        public async void RunAutoReconnectT7TestButton()
        {
            await RunAutoReconnectT7TestAsync();
        }

        //* از اینسپکتور یا دکمه یوآی برای دیسکانکت دستی تست صدا زده می‌شود.
        public async void DisconnectButton()
        {
            await DisconnectAsync("Manual T7 disconnect");
        }

        #endregion

        #region <Main Test Flow>

        //* مسیر کانکت، آث، جوین، قطعی ناخواسته، ریکانکت خودکار، جوین دوباره و فلش صف را تست می‌کند.
        public async Task<bool> RunAutoReconnectT7TestAsync()
        {
            if (isRunning)
            {
                Log("T7 auto reconnect test is already running.");
                return false;
            }

            isRunning = true;
            activeRoomId = BuildRunRoomId();
            droppedByPolicyCount = 0;
            Log("T7 auto reconnect test started. room=" + activeRoomId);

            try
            {
                bool firstReady = await ConnectAuthenticateJoinReliableAsync("first connection");
                if (!firstReady) return Fail("First connection flow failed.");

                bool beforeAction = await SendPlayerActionReliableAsync(beforeDisconnectActionType, "before_disconnect");
                if (!beforeAction) return Fail("Before disconnect reliable player_action failed.");

                recoveryWaiter = CreateBoolWaiter();
                autoReconnectOrchestrator.Start();

                Log("Simulated unexpected disconnect started.");
                await DisconnectAsync("T7 simulated unexpected network drop");

                bool playerStateDropped = await TrySendPlayerStateDuringDisconnectAsync();
                if (!playerStateDropped) return Fail("Disconnected player_state was not dropped by policy as expected.");

                bool queued = await QueuePlayerActionDuringDisconnectAsync();
                if (!queued) return Fail("Reliable player_action was not queued during auto reconnect gap.");
                if (realtimeClient.QueuedMessageCount <= 0) return Fail("Queue is empty after disconnected reliable send.");
                Log("Queue count before auto recovery: " + realtimeClient.QueuedMessageCount);

                bool recovered = await WaitWithTimeoutAsync(recoveryWaiter, "auto reconnect recovery", recoveryTimeoutMs, lifecycleCts.Token);
                if (!recovered) return Fail("Auto reconnect recovery failed.");

                if (!realtimeClient.IsConnected) return Fail("Realtime client is not connected after auto recovery.");
                if (!realtimeAuthClient.IsAuthenticated) return Fail("Realtime auth is not authenticated after auto recovery.");
                if (!gameServerClient.HasRoom) return Fail("Game server client has no active room after auto recovery.");
                if (realtimeClient.QueuedMessageCount != 0) return Fail("Queue was not flushed after auto recovery. queue=" + realtimeClient.QueuedMessageCount);

                bool afterRecoveryAction = await SendPlayerActionReliableAsync(afterRecoveryActionType, "after_recovery");
                if (!afterRecoveryAction) return Fail("After recovery reliable player_action failed.");

                autoReconnectOrchestrator.Stop();

                bool left = await LeaveRoomAndWaitAckAsync();
                if (!left) return Fail("Leave room after T7 failed.");

                if (autoDisconnectAtEnd) await DisconnectAsync("T7 completed");

                Log("T7 auto reconnect test completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("T7 auto reconnect test canceled.");
            }
            catch (Exception ex)
            {
                return Fail("T7 auto reconnect test exception: " + ex.Message);
            }
            finally
            {
                ClearWaiters();
                isRunning = false;
            }
        }

        //* یک چرخه کامل کانکت، آث و جوین قابل اطمینان را برای اتصال اول اجرا می‌کند.
        private async Task<bool> ConnectAuthenticateJoinReliableAsync(string label)
        {
            Log(label + " connect flow started.");

            bool connected = await ConnectAsync();
            if (!connected) return false;

            bool authenticated = await AuthenticateAsync();
            if (!authenticated) return false;

            RealtimeReliableSendResult joinResult = await gameServerClient.JoinRoomReliableAsync(activeRoomId, BuildReliableOptions(), lifecycleCts.Token);
            Log(label + " reliable join result: " + FormatReliableResult(joinResult));
            if (joinResult == null || !joinResult.isSuccess) return false;

            Log(label + " connect flow completed.");
            return true;
        }

        //* کُر ریل‌تایم را از طریق ترنسپورت انتخاب‌شده وصل می‌کند.
        private async Task<bool> ConnectAsync()
        {
            Log("Connecting to " + serverUrl);
            bool connected = await realtimeClient.ConnectAsync(null, lifecycleCts.Token);
            Log("Connect result: " + connected);
            return connected;
        }

        //* اکسس توکن را از اینسپکتور یا ذخیره‌ساز آث می‌گیرد و پیام system/auth را ارسال می‌کند.
        private async Task<bool> AuthenticateAsync()
        {
            authWaiter = CreateBoolWaiter();
            bool sent = await SendAuthMessageAsync(lifecycleCts.Token);

            if (!sent)
            {
                Log("Auth message was not sent.");
                return false;
            }

            return await WaitWithTimeoutAsync(authWaiter, "auth_ok", waitTimeoutMs, lifecycleCts.Token);
        }

        //* پیام آث را طبق تنظیمات اینسپکتور می‌فرستد تا هم تست و هم ارکستریتور از یک مسیر استفاده کنند.
        private async Task<bool> SendAuthMessageAsync(CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(accessTokenOverride)) return await realtimeAuthClient.AuthenticateWithAccessTokenAsync(accessTokenOverride.Trim(), cancellationToken);
            if (useStoredTokenWhenOverrideIsEmpty) return await realtimeAuthClient.AuthenticateWithStoredTokenAsync(cancellationToken);
            return false;
        }

        //* اکشن تستی پلیر را با مسیر قابل اطمینان می‌فرستد و نتیجه اَک داخلی کُر را بررسی می‌کند.
        private async Task<bool> SendPlayerActionReliableAsync(string actionType, string phase)
        {
            string payloadJson = BuildActionPayloadJson(phase);
            RealtimeReliableSendResult result = await gameServerClient.SendPlayerActionReliableAsync(actionType, payloadJson, BuildReliableOptions(), lifecycleCts.Token);
            Log(phase + " reliable player_action result: " + FormatReliableResult(result));
            return result != null && result.isSuccess && !result.wasQueued;
        }

        //* هنگام قطعی، وضعیت لحظه‌ای پلیر را می‌فرستد تا طبق سیاست ارسال حذف شود و داخل صف نرود.
        private async Task<bool> TrySendPlayerStateDuringDisconnectAsync()
        {
            int beforeQueue = realtimeClient.QueuedMessageCount;
            int beforeDrop = droppedByPolicyCount;
            bool sent = await gameServerClient.SendPlayerStateAsync(new Vector3(7.1f, 7.2f, 7.3f), Quaternion.identity, lifecycleCts.Token);
            bool wasDroppedByPolicy = !sent && droppedByPolicyCount > beforeDrop && realtimeClient.QueuedMessageCount == beforeQueue;
            Log("Disconnected player_state send result: " + sent + " | queue=" + realtimeClient.QueuedMessageCount + " | policyDrops=" + droppedByPolicyCount);
            return wasDroppedByPolicy;
        }

        //* هنگام قطعی، اکشن مهم را با مسیر قابل اطمینان می‌فرستد تا طبق سیاست داخل صف ذخیره شود.
        private async Task<bool> QueuePlayerActionDuringDisconnectAsync()
        {
            string payloadJson = BuildActionPayloadJson("queued_during_auto_reconnect");
            RealtimeReliableSendResult result = await gameServerClient.SendPlayerActionReliableAsync(queuedActionType, payloadJson, BuildReliableOptions(), lifecycleCts.Token);
            Log("Disconnected reliable player_action queued result: " + FormatReliableResult(result));
            return result != null && result.isSuccess && result.wasQueued;
        }

        //* درخواست خروج از روم را می‌فرستد و اَک leave_room را کنترل می‌کند.
        private async Task<bool> LeaveRoomAndWaitAckAsync()
        {
            PrepareAckWaiter("leave_room_");
            bool sent = await gameServerClient.LeaveRoomAsync(activeRoomId, lifecycleCts.Token);
            if (!sent) return false;
            return await WaitWithTimeoutAsync(ackWaiter, "leave_room ack", waitTimeoutMs, lifecycleCts.Token);
        }

        //* اتصال فعال را از سمت کُر ریل‌تایم می‌بندد.
        private async Task DisconnectAsync(string reason)
        {
            if (realtimeClient == null) return;
            await realtimeClient.DisconnectAsync(reason, lifecycleCts.Token);
        }

        #endregion

        #region <Client Setup>

        //* کلاینت‌ها و ارکستریتور تست را با کانفیگ وب‌سوکت می‌سازد و رویدادها را وصل می‌کند.
        private void CreateClients()
        {
            var config = new RealtimeConfig
            {
                serverUrl = serverUrl,
                transportKind = transportKind,
                connectTimeoutMs = waitTimeoutMs,
                sendTimeoutMs = waitTimeoutMs,
                autoAuthenticateAfterConnect = false,
                logIncomingMessages = true,
                logOutgoingMessages = true
            };

            realtimeClient = new RealtimeClient(config);
            realtimeAuthClient = new RealtimeAuthClient(realtimeClient);
            gameServerClient = new GameServerClient(realtimeClient);
            autoReconnectOrchestrator = new RealtimeAutoReconnectOrchestrator(realtimeClient, realtimeAuthClient, gameServerClient);
            ConfigureAutoReconnectOrchestrator();
            BindEvents();
        }

        //* تنظیمات اتوریکانکت را از اینسپکتور روی ارکستریتور اعمال می‌کند.
        private void ConfigureAutoReconnectOrchestrator()
        {
            autoReconnectOrchestrator.maxAttempts = autoReconnectMaxAttempts;
            autoReconnectOrchestrator.initialDelayMs = autoReconnectInitialDelayMs;
            autoReconnectOrchestrator.maxDelayMs = 8000;
            autoReconnectOrchestrator.totalTimeoutMs = recoveryTimeoutMs;
            autoReconnectOrchestrator.delayMultiplier = 2f;
            autoReconnectOrchestrator.authTimeoutMs = waitTimeoutMs;
            autoReconnectOrchestrator.flushQueueAfterRejoin = true;
            autoReconnectOrchestrator.ignoreIntentionalDisconnects = true;
            autoReconnectOrchestrator.reliableOptions = BuildReliableOptions();
            autoReconnectOrchestrator.SetAuthMessageSender(SendAuthMessageAsync);
        }

        //* رویدادهای کُر، آث، صف، گیم‌سرور و ارکستریتور را به کنترلر تست وصل می‌کند.
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

            gameServerClient.Events.LogReceived += Log;
            gameServerClient.Events.AckReceived += HandleAckReceived;
            gameServerClient.Events.ErrorReceived += HandleGameError;

            autoReconnectOrchestrator.AutoReconnectStarted += HandleAutoReconnectStarted;
            autoReconnectOrchestrator.AutoReconnectStepChanged += HandleAutoReconnectStepChanged;
            autoReconnectOrchestrator.AutoReconnectSucceeded += HandleAutoReconnectSucceeded;
            autoReconnectOrchestrator.AutoReconnectFailed += HandleAutoReconnectFailed;
            autoReconnectOrchestrator.AutoReconnectLogReceived += HandleAutoReconnectLogReceived;
        }

        //* رویدادها را جدا می‌کند تا بعد از پاکسازی، سابسکرایب تکراری ایجاد نشود.
        private void UnbindEvents()
        {
            if (!eventsBound) return;
            eventsBound = false;

            if (realtimeClient != null)
            {
                realtimeClient.StateChanged -= HandleStateChanged;
                realtimeClient.TransportErrorReceived -= HandleTransportError;
                realtimeClient.Disconnected -= HandleDisconnected;
                realtimeClient.QueueCountChanged -= HandleQueueCountChanged;
                realtimeClient.QueueLogReceived -= HandleQueueLogReceived;
                realtimeClient.QueuedMessageDropped -= HandleQueuedMessageDropped;
                realtimeClient.EnvelopeDroppedByPolicy -= HandleEnvelopeDroppedByPolicy;
                realtimeClient.ReliableLogReceived -= HandleReliableLogReceived;
                realtimeClient.ReliableAckTimeout -= HandleReliableAckTimeout;
            }

            if (realtimeAuthClient != null)
            {
                realtimeAuthClient.Authenticated -= HandleAuthenticated;
                realtimeAuthClient.AuthenticationFailed -= HandleAuthenticationFailed;
                realtimeAuthClient.AuthLogReceived -= Log;
            }

            if (gameServerClient != null)
            {
                gameServerClient.Events.LogReceived -= Log;
                gameServerClient.Events.AckReceived -= HandleAckReceived;
                gameServerClient.Events.ErrorReceived -= HandleGameError;
            }

            if (autoReconnectOrchestrator != null)
            {
                autoReconnectOrchestrator.AutoReconnectStarted -= HandleAutoReconnectStarted;
                autoReconnectOrchestrator.AutoReconnectStepChanged -= HandleAutoReconnectStepChanged;
                autoReconnectOrchestrator.AutoReconnectSucceeded -= HandleAutoReconnectSucceeded;
                autoReconnectOrchestrator.AutoReconnectFailed -= HandleAutoReconnectFailed;
                autoReconnectOrchestrator.AutoReconnectLogReceived -= HandleAutoReconnectLogReceived;
            }
        }

        #endregion

        #region <Event Handlers>

        //* تغییر وضعیت کُر را در لاگ تست نشان می‌دهد.
        private void HandleStateChanged(RealtimeConnectionState state)
        {
            Log("State: " + state);
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

        //* لاگ داخلی صف را در لاگ تست نشان می‌دهد.
        private void HandleQueueLogReceived(string message)
        {
            Log("Queue: " + message);
        }

        //* حذف پیام صف‌شده را در لاگ تست نشان می‌دهد.
        private void HandleQueuedMessageDropped(RealtimeEnvelope envelope)
        {
            Log("Queued message dropped: " + (envelope == null ? "null" : envelope.id));
        }

        //* حذف کنترل‌شده پیام بر اساس سیاست ارسال را در لاگ تست نشان می‌دهد.
        private void HandleEnvelopeDroppedByPolicy(RealtimeEnvelope envelope, RealtimeDeliveryPolicy deliveryPolicy)
        {
            droppedByPolicyCount++;
            Log("Envelope dropped by policy: " + (envelope == null ? "null" : envelope.id) + " | policy=" + deliveryPolicy);
        }

        //* لاگ مسیر ارسال قابل اطمینان را در تست نشان می‌دهد.
        private void HandleReliableLogReceived(string message)
        {
            Log("Reliable: " + message);
        }

        //* تایم اوت اَک را در تست نشان می‌دهد.
        private void HandleReliableAckTimeout(string messageId)
        {
            Log("Reliable ack timeout: " + messageId);
        }

        //* موفقیت آث را به انتظار تست آث وصل می‌کند.
        private void HandleAuthenticated(string connectionId, string userId)
        {
            Log("Authenticated: " + connectionId + " | " + userId);
            TrySetWaiter(authWaiter, true);
        }

        //* شکست آث را به انتظار تست آث وصل می‌کند.
        private void HandleAuthenticationFailed(RealtimeError error)
        {
            Log("Auth failed: " + FormatError(error));
            TrySetWaiter(authWaiter, false);
        }

        //* اَک‌های گیم‌سرور را بر اساس پیشوند پیام در حال انتظار کنترل می‌کند.
        private void HandleAckReceived(GameServerAckResult ack)
        {
            if (ack == null) return;
            if (string.IsNullOrWhiteSpace(waitingAckPrefix)) return;
            if (!ack.originalMessageId.StartsWith(waitingAckPrefix, StringComparison.OrdinalIgnoreCase)) return;
            TrySetWaiter(ackWaiter, ack.IsProcessed());
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

        //* موفقیت اتوریکانکت را به انتظار تست وصل می‌کند.
        private void HandleAutoReconnectSucceeded(int attempt)
        {
            Log("Auto reconnect succeeded. attempt=" + attempt);
            TrySetWaiter(recoveryWaiter, true);
        }

        //* شکست اتوریکانکت را به انتظار تست وصل می‌کند.
        private void HandleAutoReconnectFailed(string reason)
        {
            Log("Auto reconnect failed: " + reason);
            TrySetWaiter(recoveryWaiter, false);
        }

        //* لاگ داخلی ارکستریتور را در تست نشان می‌دهد.
        private void HandleAutoReconnectLogReceived(string message)
        {
            Log("AutoReconnect: " + message);
        }

        #endregion

        #region <Wait Helpers>

        //* انتظار اَک بعدی را برای یک پیشوند مشخص آماده می‌کند.
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

        //* منتظر رویداد مورد نظر می‌ماند و اگر دیر شود، شکست کنترل‌شده برمی‌گرداند.
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

        //* نتیجه یک انتظار را فقط اگر قبلاً کامل نشده باشد ثبت می‌کند.
        private static void TrySetWaiter(TaskCompletionSource<bool> waiter, bool value)
        {
            if (waiter == null || waiter.Task.IsCompleted) return;
            waiter.TrySetResult(value);
        }

        //* انتظارهای فعلی تست را پاک می‌کند.
        private void ClearWaiters()
        {
            authWaiter = null;
            ackWaiter = null;
            recoveryWaiter = null;
            waitingAckPrefix = string.Empty;
        }

        #endregion

        #region <Cleanup>

        //* همه کلاینت‌های ساخته‌شده برای تست را آزاد می‌کند.
        private async Task CleanupAsync()
        {
            UnbindEvents();

            if (autoReconnectOrchestrator != null) autoReconnectOrchestrator.Dispose();
            if (gameServerClient != null) gameServerClient.Dispose();
            if (realtimeAuthClient != null) realtimeAuthClient.Dispose();
            if (realtimeClient != null) await realtimeClient.DisconnectAsync("T7 cleanup");
            if (realtimeClient != null) realtimeClient.Dispose();

            autoReconnectOrchestrator = null;
            gameServerClient = null;
            realtimeAuthClient = null;
            realtimeClient = null;
        }

        #endregion

        #region <Format Helpers>

        //* برای هر اجرای تست یک روم یکتا می‌سازد تا تست‌ها با هم قاطی نشوند.
        private string BuildRunRoomId()
        {
            string prefix = string.IsNullOrWhiteSpace(roomIdPrefix) ? "unity_t7_room" : roomIdPrefix.Trim();
            return prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        //* پِیلود اکشن تستی را با فاز فعلی و روم فعال می‌سازد.
        private string BuildActionPayloadJson(string phase)
        {
            return "{\"source\":\"unity_t7\",\"phase\":\"" + EscapeJson(phase) + "\",\"roomId\":\"" + EscapeJson(activeRoomId) + "\",\"time\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}";
        }

        //* تنظیمات ارسال قابل اطمینان را از اینسپکتور می‌سازد.
        private RealtimeReliableSendOptions BuildReliableOptions()
        {
            var options = new RealtimeReliableSendOptions();
            options.ackTimeoutMs = reliableAckTimeoutMs;
            options.maxSendAttempts = reliableMaxSendAttempts;
            options.retryDelayMs = reliableRetryDelayMs;
            options.retryOnAckTimeout = true;
            options.retryOnTransportSendFailed = true;
            options.Normalize();
            return options;
        }

        //* نتیجه ارسال قابل اطمینان را به متن کوتاه برای لاگ تبدیل می‌کند.
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

        //* خطای ریل‌تایم را به متن کوتاه قابل لاگ تبدیل می‌کند.
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

        //* شکست تست را لاگ می‌کند و false برمی‌گرداند.
        private bool Fail(string message)
        {
            Log("T7 failed: " + message);
            return false;
        }

        //* لاگ یکدست تست را در کنسول یونیتی چاپ می‌کند.
        private void Log(string message)
        {
            Debug.Log("[RealtimeT7] " + message);
        }

        #endregion
    }
}

//* این فایل تست اتوریکانکت تی‌سون را برای یونیتی اجرا می‌کند.
//* تست ثابت می‌کند قطع ناخواسته می‌تواند خودکار به کانکت، آث دوباره، جوین دوباره و فلش صف با اَک تبدیل شود.
