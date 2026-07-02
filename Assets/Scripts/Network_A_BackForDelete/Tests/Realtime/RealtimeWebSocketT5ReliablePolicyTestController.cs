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
    //* کنترلر تست تی‌فایو است و صف شدن پیام مهم هنگام قطعی و فلش شدن بعد از ریکانکت را بررسی می‌کند.
    public class RealtimeWebSocketT5ReliablePolicyTestController : MonoBehaviour
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
        [SerializeField] private string roomIdPrefix = "unity_t5_room";
        [SerializeField] private string beforeDisconnectActionType = "unity_t5_before_disconnect";
        [SerializeField] private string queuedActionType = "unity_t5_queued_during_disconnect";
        [SerializeField] private string afterFlushActionType = "unity_t5_after_flush";
        [SerializeField] private string droppedPlayerStatePhase = "unity_t5_disconnected_player_state";

        [Header("Timeout")]
        [SerializeField] private int waitTimeoutMs = 10000;
        [SerializeField] private int reconnectDelayMs = 700;

        private RealtimeClient realtimeClient;
        private RealtimeAuthClient realtimeAuthClient;
        private GameServerClient gameServerClient;
        private CancellationTokenSource lifecycleCts;
        private TaskCompletionSource<bool> authWaiter;
        private TaskCompletionSource<bool> ackWaiter;
        private string waitingAckPrefix = string.Empty;
        private string activeRoomId = string.Empty;
        private int droppedByPolicyCount;
        private bool isRunning;
        private bool eventsBound;

        #region <Unity Lifecycle>

        //* وابستگی‌های تست را می‌سازد تا تست صف پیام فقط از کُر و گیم‌سرورکلاینت استفاده کند.
        private void Awake()
        {
            lifecycleCts = new CancellationTokenSource();
            CreateClients();
        }

        //* اگر از اینسپکتور فعال باشد، تست تی‌فایو را بعد از شروع صحنه اجرا می‌کند.
        private async void Start()
        {
            if (!runOnStart) return;
            await RunReliablePolicyT5TestAsync();
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

        //* از اینسپکتور یا دکمه یوآی برای اجرای تست صف پیام تی‌فایو صدا زده می‌شود.
        public async void RunReliablePolicyT5TestButton()
        {
            await RunReliablePolicyT5TestAsync();
        }

        //* از اینسپکتور یا دکمه یوآی برای دیسکانکت دستی تست صدا زده می‌شود.
        public async void DisconnectButton()
        {
            await DisconnectAsync("Manual T5 disconnect");
        }

        #endregion

        #region <Main Test Flow>

        //* مسیر کانکت، آث، جوین، دیسکانکت، صف شدن پیام، ریکانکت، جوین دوباره و فلش صف را تست می‌کند.
        public async Task<bool> RunReliablePolicyT5TestAsync()
        {
            if (isRunning)
            {
                Log("T5 reliable policy test is already running.");
                return false;
            }

            isRunning = true;
            activeRoomId = BuildRunRoomId();
            droppedByPolicyCount = 0;
            Log("T5 reliable policy test started. room=" + activeRoomId);

            try
            {
                bool firstReady = await ConnectAuthenticateJoinAsync("first connection");
                if (!firstReady) return Fail("First connection flow failed.");

                bool beforeAction = await SendPlayerActionAndWaitAckAsync(beforeDisconnectActionType, "before_disconnect");
                if (!beforeAction) return Fail("Before disconnect player action failed.");

                Log("Intentional disconnect started.");
                await DisconnectAsync("T5 intentional disconnect");
                ClearWaiters();

                bool playerStateDropped = await TrySendPlayerStateDuringDisconnectAsync();
                if (!playerStateDropped) return Fail("Disconnected player_state was not dropped by policy as expected.");
                if (realtimeClient.QueuedMessageCount != 0) return Fail("Player state should not enter queue. queue=" + realtimeClient.QueuedMessageCount);

                await QueuePlayerActionDuringDisconnectAsync();
                if (realtimeClient.QueuedMessageCount <= 0) return Fail("Queue is empty after reliable disconnected send.");
                Log("Queue count after reliable disconnected send: " + realtimeClient.QueuedMessageCount);

                await DelayReconnectAsync();

                bool secondReady = await ConnectAuthenticateJoinAsync("second connection");
                if (!secondReady) return Fail("Second connection flow failed.");

                bool flushed = await FlushQueueAndWaitAckAsync();
                if (!flushed) return Fail("Queued player action flush failed.");

                bool afterFlushAction = await SendPlayerActionAndWaitAckAsync(afterFlushActionType, "after_flush");
                if (!afterFlushAction) return Fail("After flush player action failed.");

                bool left = await LeaveRoomAndWaitAckAsync();
                if (!left) return Fail("Leave room after T5 failed.");

                if (autoDisconnectAtEnd) await DisconnectAsync("T5 completed");

                Log("T5 reliable policy test completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("T5 reliable policy test canceled.");
            }
            catch (Exception ex)
            {
                return Fail("T5 reliable policy test exception: " + ex.Message);
            }
            finally
            {
                ClearWaiters();
                isRunning = false;
            }
        }

        //* یک چرخه کامل کانکت، آث و جوین را برای اتصال اول یا دوم اجرا می‌کند.
        private async Task<bool> ConnectAuthenticateJoinAsync(string label)
        {
            Log(label + " connect flow started.");

            bool connected = await ConnectAsync();
            if (!connected) return false;

            bool authenticated = await AuthenticateAsync();
            if (!authenticated) return false;

            bool joined = await JoinRoomAndWaitAckAsync();
            if (!joined) return false;

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

            bool sent;
            if (!string.IsNullOrWhiteSpace(accessTokenOverride)) sent = await realtimeAuthClient.AuthenticateWithAccessTokenAsync(accessTokenOverride.Trim(), lifecycleCts.Token);
            else if (useStoredTokenWhenOverrideIsEmpty) sent = await realtimeAuthClient.AuthenticateWithStoredTokenAsync(lifecycleCts.Token);
            else sent = false;

            if (!sent)
            {
                Log("Auth message was not sent.");
                return false;
            }

            return await WaitWithTimeoutAsync(authWaiter, "auth_ok", waitTimeoutMs, lifecycleCts.Token);
        }

        //* درخواست ورود به روم را می‌فرستد و اَک join_room را کنترل می‌کند.
        private async Task<bool> JoinRoomAndWaitAckAsync()
        {
            PrepareAckWaiter("join_room_");
            bool sent = await gameServerClient.JoinRoomAsync(activeRoomId, lifecycleCts.Token);
            if (!sent) return false;
            return await WaitWithTimeoutAsync(ackWaiter, "join_room ack", waitTimeoutMs, lifecycleCts.Token);
        }

        //* اکشن تستی پلیر را می‌فرستد و اَک player_action را کنترل می‌کند.
        private async Task<bool> SendPlayerActionAndWaitAckAsync(string actionType, string phase)
        {
            PrepareAckWaiter("player_action_");
            string payloadJson = BuildActionPayloadJson(phase);
            bool sent = await gameServerClient.SendPlayerActionAsync(actionType, payloadJson, lifecycleCts.Token);
            if (!sent) return false;
            return await WaitWithTimeoutAsync(ackWaiter, phase + " player_action ack", waitTimeoutMs, lifecycleCts.Token);
        }

        //* هنگام قطعی، وضعیت لحظه‌ای پلیر را می‌فرستد تا طبق سیاست ارسال حذف شود و داخل صف نرود.
        private async Task<bool> TrySendPlayerStateDuringDisconnectAsync()
        {
            int beforeQueue = realtimeClient.QueuedMessageCount;
            int beforeDrop = droppedByPolicyCount;
            bool sent = await gameServerClient.SendPlayerStateAsync(new Vector3(1.1f, 2.2f, 3.3f), Quaternion.identity, lifecycleCts.Token);
            bool wasDroppedByPolicy = !sent && droppedByPolicyCount > beforeDrop && realtimeClient.QueuedMessageCount == beforeQueue;
            Log("Disconnected player_state send result: " + sent + " | queue=" + realtimeClient.QueuedMessageCount + " | policyDrops=" + droppedByPolicyCount);
            return wasDroppedByPolicy;
        }

        //* هنگام قطعی، اکشن مهم را ارسال می‌کند تا به جای ارسال مستقیم داخل صف ریل‌تایم ذخیره شود.
        private async Task<bool> QueuePlayerActionDuringDisconnectAsync()
        {
            string payloadJson = BuildActionPayloadJson("queued_during_disconnect");
            bool queued = await gameServerClient.SendPlayerActionAsync(queuedActionType, payloadJson, lifecycleCts.Token);
            Log("Disconnected player_action queued result: " + queued);
            return queued;
        }

        //* صف پیام‌های مهم را بعد از ریکانکت و جوین دوباره فلش می‌کند و اَک پیام صف‌شده را می‌گیرد.
        private async Task<bool> FlushQueueAndWaitAckAsync()
        {
            PrepareAckWaiter("player_action_");
            Log("Flushing realtime queue. count=" + realtimeClient.QueuedMessageCount);
            await realtimeClient.FlushQueuedMessagesAsync(lifecycleCts.Token);
            bool ackReceived = await WaitWithTimeoutAsync(ackWaiter, "queued player_action ack", waitTimeoutMs, lifecycleCts.Token);
            Log("Queue count after flush: " + realtimeClient.QueuedMessageCount);
            return ackReceived && realtimeClient.QueuedMessageCount == 0;
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

        //* بعد از دیسکانکت عمدی کمی صبر می‌کند تا کلین‌آپ سرور فرصت اجرا داشته باشد.
        private async Task DelayReconnectAsync()
        {
            int delay = Math.Max(100, reconnectDelayMs);
            Log("Waiting before reconnect: " + delay + "ms");
            await Task.Delay(delay, lifecycleCts.Token);
        }

        #endregion

        #region <Client Setup>

        //* کلاینت‌های تست را با کانفیگ وب‌سوکت می‌سازد و رویدادها را وصل می‌کند.
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
            BindEvents();
        }

        //* رویدادهای کُر، آث، صف و گیم‌سرور را به کنترلر تست وصل می‌کند.
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

            realtimeAuthClient.Authenticated += HandleAuthenticated;
            realtimeAuthClient.AuthenticationFailed += HandleAuthenticationFailed;
            realtimeAuthClient.AuthLogReceived += Log;

            gameServerClient.Events.LogReceived += Log;
            gameServerClient.Events.AckReceived += HandleAckReceived;
            gameServerClient.Events.ErrorReceived += HandleGameError;
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
            waitingAckPrefix = string.Empty;
        }

        #endregion

        #region <Cleanup>

        //* همه کلاینت‌های ساخته‌شده برای تست را آزاد می‌کند.
        private async Task CleanupAsync()
        {
            UnbindEvents();

            if (gameServerClient != null) gameServerClient.Dispose();
            if (realtimeAuthClient != null) realtimeAuthClient.Dispose();
            if (realtimeClient != null) await realtimeClient.DisconnectAsync("T5 cleanup");
            if (realtimeClient != null) realtimeClient.Dispose();

            gameServerClient = null;
            realtimeAuthClient = null;
            realtimeClient = null;
        }

        #endregion

        #region <Format Helpers>

        //* برای هر اجرای تست یک روم یکتا می‌سازد تا تست‌ها با هم قاطی نشوند.
        private string BuildRunRoomId()
        {
            string prefix = string.IsNullOrWhiteSpace(roomIdPrefix) ? "unity_t5_room" : roomIdPrefix.Trim();
            return prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        //* پِیلود اکشن تستی را با فاز فعلی و روم فعال می‌سازد.
        private string BuildActionPayloadJson(string phase)
        {
            return "{\"source\":\"unity_t5\",\"phase\":\"" + EscapeJson(phase) + "\",\"roomId\":\"" + EscapeJson(activeRoomId) + "\",\"time\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}";
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
            Log("T5 failed: " + message);
            return false;
        }

        //* لاگ یکدست تست را در کنسول یونیتی چاپ می‌کند.
        private void Log(string message)
        {
            Debug.Log("[RealtimeT5] " + message);
        }

        #endregion
    }
}

//* این فایل تست سیاست ارسال پیام تی‌فایو وب‌سوکت را برای یونیتی اجرا می‌کند.
//* تست ثابت می‌کند پیام لحظه‌ای در زمان قطعی صف نمی‌شود و پیام مهم بعد از ریکانکت، آث و جوین دوباره با اَک ارسال می‌شود.
