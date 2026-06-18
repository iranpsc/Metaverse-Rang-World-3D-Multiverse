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
    //* کنترلر تست تی‌وان است و مسیر کامل وب‌سوکت ریل‌تایم را از داخل یونیتی بررسی می‌کند.
    public class RealtimeWebSocketT1TestController : MonoBehaviour
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
        [SerializeField] private string roomIdPrefix = "unity_t1_room";
        [SerializeField] private string playerActionType = "unity_t1_action";
        [SerializeField] private bool sendPlayerState = true;

        [Header("Timeout")]
        [SerializeField] private int waitTimeoutMs = 10000;
        [SerializeField] private int heartbeatPingIntervalMs = 5000;
        [SerializeField] private int heartbeatPongTimeoutMs = 3000;

        private RealtimeClient realtimeClient;
        private RealtimeAuthClient realtimeAuthClient;
        private GameServerClient gameServerClient;
        private RealtimeHeartbeat realtimeHeartbeat;
        private CancellationTokenSource lifecycleCts;
        private TaskCompletionSource<bool> authWaiter;
        private TaskCompletionSource<bool> pongWaiter;
        private TaskCompletionSource<bool> ackWaiter;
        private string waitingAckPrefix = string.Empty;
        private string activeRoomId = string.Empty;
        private bool isRunning;
        private bool eventsBound;

        private Action heartbeatPongHandler;
        private Action<int> heartbeatPongMissedHandler;
        private Action heartbeatConnectionTimeoutHandler;
        private Action<string> heartbeatLogHandler;

        #region <Unity Lifecycle>

        //* وابستگی‌های تست را می‌سازد تا تست فقط از کُر و گیم‌سرورکلاینت استفاده کند.
        private void Awake()
        {
            lifecycleCts = new CancellationTokenSource();
            CreateClients();
        }

        //* اگر از اینسپکتور فعال باشد، تست کامل را بعد از شروع صحنه اجرا می‌کند.
        private async void Start()
        {
            if (!runOnStart) return;
            await RunFullWebSocketT1TestAsync();
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

        //* از اینسپکتور یا دکمه یوآی برای اجرای تست کامل وب‌سوکت صدا زده می‌شود.
        public async void RunFullWebSocketT1TestButton()
        {
            await RunFullWebSocketT1TestAsync();
        }

        //* از اینسپکتور یا دکمه یوآی برای قطع دستی تست صدا زده می‌شود.
        public async void DisconnectButton()
        {
            await DisconnectAsync("Manual T1 disconnect");
        }

        #endregion

        #region <Main Test Flow>

        //* مسیر کامل کانکت، اَث، پینگ، جوین، اکشن، استیت، لیو و دیسکانکت را تست می‌کند.
        public async Task<bool> RunFullWebSocketT1TestAsync()
        {
            if (isRunning)
            {
                Log("T1 test is already running.");
                return false;
            }

            isRunning = true;
            activeRoomId = BuildRunRoomId();
            Log("T1 test started. room=" + activeRoomId);

            try
            {
                bool connected = await ConnectAsync();
                if (!connected) return Fail("Connect failed.");

                bool authenticated = await AuthenticateAsync();
                if (!authenticated) return Fail("Auth failed.");

                bool pongReceived = await SendManualPingAndWaitForPongAsync();
                if (!pongReceived) return Fail("Ping/Pong failed.");

                StartHeartbeatForRuntimeCheck();

                bool joined = await JoinRoomAndWaitAckAsync();
                if (!joined) return Fail("Join room failed.");

                bool actionSent = await SendPlayerActionAndWaitAckAsync();
                if (!actionSent) return Fail("Player action failed.");

                if (sendPlayerState)
                {
                    bool stateSent = await SendPlayerStateAsync();
                    if (!stateSent) return Fail("Player state send failed.");
                }

                bool left = await LeaveRoomAndWaitAckAsync();
                if (!left) return Fail("Leave room failed.");

                if (autoDisconnectAtEnd) await DisconnectAsync("T1 completed");

                Log("T1 test completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("T1 test canceled.");
            }
            catch (Exception ex)
            {
                return Fail("T1 test exception: " + ex.Message);
            }
            finally
            {
                StopHeartbeat();
                ClearWaiters();
                isRunning = false;
            }
        }

        //* کُر ریل‌تایم را از طریق ترنسپورت وب‌سوکت وصل می‌کند.
        private async Task<bool> ConnectAsync()
        {
            Log("Connecting to " + serverUrl);
            bool connected = await realtimeClient.ConnectAsync(null, lifecycleCts.Token);
            Log("Connect result: " + connected);
            return connected;
        }

        //* اکسس توکن را از اینسپکتور یا ذخیره‌ساز اَث می‌گیرد و پیام system/auth را ارسال می‌کند.
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

        //* یک پینگ دستی می‌فرستد و منتظر پونگ مستقیم سرور می‌ماند.
        private async Task<bool> SendManualPingAndWaitForPongAsync()
        {
            pongWaiter = CreateBoolWaiter();
            bool sent = await realtimeClient.SendPingAsync(lifecycleCts.Token);
            if (!sent) return false;
            return await WaitWithTimeoutAsync(pongWaiter, "system/pong", waitTimeoutMs, lifecycleCts.Token);
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
        private async Task<bool> SendPlayerActionAndWaitAckAsync()
        {
            PrepareAckWaiter("player_action_");
            string payloadJson = "{\"source\":\"unity_t1\",\"roomId\":\"" + EscapeJson(activeRoomId) + "\",\"time\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}";
            bool sent = await gameServerClient.SendPlayerActionAsync(playerActionType, payloadJson, lifecycleCts.Token);
            if (!sent) return false;
            return await WaitWithTimeoutAsync(ackWaiter, "player_action ack", waitTimeoutMs, lifecycleCts.Token);
        }

        //* وضعیت موقعیت و چرخش آبجکت تست را برای مسیر presence/player_state می‌فرستد.
        private async Task<bool> SendPlayerStateAsync()
        {
            bool sent = await gameServerClient.SendPlayerStateAsync(transform.position, transform.rotation, lifecycleCts.Token);
            Log("Player state sent: " + sent);
            return sent;
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
            StopHeartbeat();
            if (realtimeClient == null) return;
            await realtimeClient.DisconnectAsync(reason, lifecycleCts.Token);
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
            realtimeHeartbeat = new RealtimeHeartbeat(realtimeClient)
            {
                pingIntervalMs = heartbeatPingIntervalMs,
                pongTimeoutMs = heartbeatPongTimeoutMs,
                maxMissedPongs = 3,
                logHeartbeat = true
            };

            BindEvents();
        }

        //* رویدادهای کُر، اَث، هارت‌بیت و گیم‌سرور را به کنترلر تست وصل می‌کند.
        private void BindEvents()
        {
            if (eventsBound) return;

            realtimeClient.StateChanged += HandleStateChanged;
            realtimeClient.EnvelopeReceived += HandleEnvelopeReceived;
            realtimeClient.TransportErrorReceived += HandleTransportError;
            realtimeClient.Disconnected += HandleDisconnected;

            realtimeAuthClient.Authenticated += HandleAuthenticated;
            realtimeAuthClient.AuthenticationFailed += HandleAuthenticationFailed;
            realtimeAuthClient.AuthLogReceived += Log;

            gameServerClient.Events.LogReceived += Log;
            gameServerClient.Events.AckReceived += HandleAckReceived;
            gameServerClient.Events.ErrorReceived += HandleGameError;

            heartbeatPongHandler = HandleHeartbeatPong;
            heartbeatPongMissedHandler = HandleHeartbeatPongMissed;
            heartbeatConnectionTimeoutHandler = HandleHeartbeatConnectionTimeout;
            heartbeatLogHandler = HandleHeartbeatLog;

            realtimeHeartbeat.PongReceived += heartbeatPongHandler;
            realtimeHeartbeat.PongMissed += heartbeatPongMissedHandler;
            realtimeHeartbeat.ConnectionTimeout += heartbeatConnectionTimeoutHandler;
            realtimeHeartbeat.HeartbeatLogReceived += heartbeatLogHandler;

            eventsBound = true;
        }

        //* رویدادها را جدا می‌کند تا بعد از پاکسازی، نشتی رویداد ایجاد نشود.
        private void UnbindEvents()
        {
            if (!eventsBound) return;

            if (realtimeClient != null)
            {
                realtimeClient.StateChanged -= HandleStateChanged;
                realtimeClient.EnvelopeReceived -= HandleEnvelopeReceived;
                realtimeClient.TransportErrorReceived -= HandleTransportError;
                realtimeClient.Disconnected -= HandleDisconnected;
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

            if (realtimeHeartbeat != null)
            {
                if (heartbeatPongHandler != null) realtimeHeartbeat.PongReceived -= heartbeatPongHandler;
                if (heartbeatPongMissedHandler != null) realtimeHeartbeat.PongMissed -= heartbeatPongMissedHandler;
                if (heartbeatConnectionTimeoutHandler != null) realtimeHeartbeat.ConnectionTimeout -= heartbeatConnectionTimeoutHandler;
                if (heartbeatLogHandler != null) realtimeHeartbeat.HeartbeatLogReceived -= heartbeatLogHandler;
            }

            heartbeatPongHandler = null;
            heartbeatPongMissedHandler = null;
            heartbeatConnectionTimeoutHandler = null;
            heartbeatLogHandler = null;
            eventsBound = false;
        }

        #endregion

        #region <Event Handlers>

        //* تغییر وضعیت کُر را در لاگ تست نشان می‌دهد.
        private void HandleStateChanged(RealtimeConnectionState state)
        {
            Log("State: " + state);
        }

        //* اِنولوپ‌های عمومی را بررسی می‌کند تا پونگ دستی تشخیص داده شود.
        private void HandleEnvelopeReceived(RealtimeEnvelope envelope)
        {
            if (envelope == null) return;
            if (envelope.ch == RealtimeChannels.System && envelope.t == RealtimeMessageTypes.Pong) TrySetWaiter(pongWaiter, true);
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

        //* موفقیت اَث را به انتظار تست اَث وصل می‌کند.
        private void HandleAuthenticated(string connectionId, string userId)
        {
            Log("Authenticated: " + connectionId + " | " + userId);
            TrySetWaiter(authWaiter, true);
        }

        //* شکست اَث را به انتظار تست اَث وصل می‌کند.
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

        //* دریافت پونگ هارت‌بیت را در لاگ تست نشان می‌دهد.
        private void HandleHeartbeatPong()
        {
            Log("Heartbeat pong received.");
        }

        //* از دست رفتن پونگ هارت‌بیت را در لاگ تست نشان می‌دهد.
        private void HandleHeartbeatPongMissed(int count)
        {
            Log("Heartbeat pong missed: " + count);
        }

        //* تایم‌اوت هارت‌بیت را در لاگ تست نشان می‌دهد.
        private void HandleHeartbeatConnectionTimeout()
        {
            Log("Heartbeat connection timeout.");
        }

        //* لاگ داخلی هارت‌بیت را به لاگ تست منتقل می‌کند.
        private void HandleHeartbeatLog(string message)
        {
            Log("Heartbeat: " + message);
        }

        #endregion

        #region <Heartbeat>

        //* هارت‌بیت کوتاه تستی را بعد از اَث موفق روشن می‌کند.
        private void StartHeartbeatForRuntimeCheck()
        {
            if (realtimeHeartbeat == null) return;
            realtimeHeartbeat.Reset();
            realtimeHeartbeat.Start();
        }

        //* هارت‌بیت را خاموش می‌کند تا بعد از تست پینگ اضافه ارسال نشود.
        private void StopHeartbeat()
        {
            realtimeHeartbeat?.Stop();
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
            pongWaiter = null;
            ackWaiter = null;
            waitingAckPrefix = string.Empty;
        }

        #endregion

        #region <Cleanup>

        //* همه کلاینت‌های ساخته‌شده برای تست را آزاد می‌کند.
        private async Task CleanupAsync()
        {
            StopHeartbeat();
            UnbindEvents();

            if (gameServerClient != null) gameServerClient.Dispose();
            if (realtimeAuthClient != null) realtimeAuthClient.Dispose();
            if (realtimeClient != null) await realtimeClient.DisconnectAsync("T1 cleanup");
            if (realtimeClient != null) realtimeClient.Dispose();
            if (realtimeHeartbeat != null) realtimeHeartbeat.Dispose();

            gameServerClient = null;
            realtimeAuthClient = null;
            realtimeClient = null;
            realtimeHeartbeat = null;
        }

        #endregion

        #region <Format Helpers>

        //* برای هر اجرای تست یک روم یکتا می‌سازد تا تست‌ها با هم قاطی نشوند.
        private string BuildRunRoomId()
        {
            string prefix = string.IsNullOrWhiteSpace(roomIdPrefix) ? "unity_t1_room" : roomIdPrefix.Trim();
            return prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
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
            Log("T1 failed: " + message);
            return false;
        }

        //* لاگ یکدست تست را در کنسول یونیتی چاپ می‌کند.
        private void Log(string message)
        {
            Debug.Log("[RealtimeT1] " + message);
        }

        #endregion
    }
}

//* این فایل تست کامل تی‌وان وب‌سوکت را برای یونیتی اجرا می‌کند.
//* تست فقط از RealtimeClient و GameServerClient استفاده می‌کند و به وب‌سوکت خام یا جی‌آرپی‌سی خام وابسته نیست.
