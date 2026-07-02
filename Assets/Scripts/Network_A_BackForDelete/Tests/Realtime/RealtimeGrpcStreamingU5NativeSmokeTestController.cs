using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.GameServer;
using Network_A.Realtime.Auth;
using Network_A.Realtime.Core;
using Network_A.Realtime.Protocol;
using Network_A.Realtime.Transport;
using UnityEngine;

namespace Network_A.Tests.Realtime
{
    //* کنترلر تست یو‌فایو است و مسیر جی‌آر‌پی‌سی استریمینگ نیتیو را بدون تغییر وب‌سوکت بررسی می‌کند.
    [DefaultExecutionOrder(110)]
    public class RealtimeGrpcStreamingU5NativeSmokeTestController : MonoBehaviour
    {
        [Header("Connection")]
        [SerializeField] private string serverTarget = string.Empty;
        [SerializeField] private bool useServerConfigTarget = true;
        [SerializeField] private bool forceDedicatedServerConfig = true;
        [SerializeField] private RealtimeTransportKind transportKind = RealtimeTransportKind.GrpcStreaming;
        [SerializeField] private bool runOnStart;
#if UNITY_EDITOR
        [SerializeField] private bool allowRunOnStartInEditor;
#endif
        [SerializeField] private bool autoDisconnectAtEnd = true;
        [SerializeField] private int disconnectTimeoutMs = 2000;

        [Header("Auth")]
        [TextArea(2, 6)]
        [SerializeField] private string accessTokenOverride = string.Empty;
        [SerializeField] private bool useStoredTokenWhenOverrideIsEmpty = true;

        [Header("Room")]
        [SerializeField] private bool enableRoomFlow;
        [SerializeField] private bool joinRoomAfterAuth = true;
        [SerializeField] private bool leaveRoomAtEnd = true;
        [SerializeField] private string roomIdPrefix = "unity_u5_grpc_room";

        [Header("Timeout")]
        [SerializeField] private int connectTimeoutMs = 10000;
        [SerializeField] private int sendTimeoutMs = 10000;
        [SerializeField] private int waitTimeoutMs = 10000;

        private RealtimeClient realtimeClient;
        private RealtimeAuthClient realtimeAuthClient;
        private GameServerClient gameServerClient;
        private CancellationTokenSource lifecycleCts;
        private TaskCompletionSource<bool> authWaiter;
        private TaskCompletionSource<bool> pongWaiter;
        private TaskCompletionSource<bool> ackWaiter;
        private string waitingAckPrefix = string.Empty;
        private string activeRoomId = string.Empty;
        private string activeServerTarget = string.Empty;
        private bool isRunning;
        private bool eventsBound;

        #region <Unity Lifecycle>

        //* منبع لغو تست را آماده می‌کند، اما کلاینت جی‌آر‌پی‌سی را در ادیتور زودتر از اجرای تست نمی‌سازد.
        private void Awake()
        {
            lifecycleCts = new CancellationTokenSource();
        }

        //* اگر اجرای خودکار فعال باشد، تست را شروع می‌کند؛ در ادیتور برای جلوگیری از گیر ریلود دامین باید اجازه جداگانه روشن باشد.
        private async void Start()
        {
            if (!runOnStart) return;

#if UNITY_EDITOR
            if (!allowRunOnStartInEditor)
            {
                Log("Run On Start is enabled, but Allow Run On Start In Editor is disabled.");
                return;
            }
#endif

            await RunFullGrpcStreamingU5TestAsync();
        }

        //* هنگام حذف آبجکت فقط تست را لغو می‌کند و از await شبکه‌ای در ریلود دامین جلوگیری می‌کند.
        private void OnDestroy()
        {
            if (lifecycleCts != null)
            {
                lifecycleCts.Cancel();
                lifecycleCts.Dispose();
                lifecycleCts = null;
            }

            ClearWaiters();
            UnbindEvents();

            if (gameServerClient != null) gameServerClient.Dispose();
            if (realtimeAuthClient != null) realtimeAuthClient.Dispose();

            gameServerClient = null;
            realtimeAuthClient = null;
            realtimeClient = null;
            eventsBound = false;
            isRunning = false;
        }

        #endregion

        #region <Inspector Buttons>

        //* از اینسپکتور یا دکمه یو‌آی برای اجرای تست کامل جی‌آر‌پی‌سی استریمینگ صدا زده می‌شود.
        public async void RunFullGrpcStreamingU5TestButton()
        {
            await RunFullGrpcStreamingU5TestAsync();
        }

        //* از اینسپکتور یا دکمه یو‌آی فقط اتصال خام جی‌آر‌پی‌سی استریمینگ را بدون اَث و روم تست می‌کند.
        public async void RunConnectOnlyButton()
        {
            await RunConnectOnlyAsync();
        }

        //* از اینسپکتور یا دکمه یو‌آی برای قطع دستی تست صدا زده می‌شود.
        public async void DisconnectButton()
        {
            await DisconnectAsync("Manual U5 gRPC streaming disconnect");
        }

        #endregion

        #region <Main Test Flow>

        //* مسیر کامل کانکت، اَث، پینگ، جوین، لیو و دیسکانکت را روی ترنسپورت جی‌آر‌پی‌سی استریمینگ تست می‌کند.
        public async Task<bool> RunFullGrpcStreamingU5TestAsync()
        {
            if (isRunning)
            {
                Log("U5 gRPC streaming test is already running.");
                return false;
            }

            if (lifecycleCts == null || lifecycleCts.IsCancellationRequested) lifecycleCts = new CancellationTokenSource();
            if (realtimeClient == null) CreateClients();

            isRunning = true;
            activeRoomId = BuildRunRoomId();
            Log("U5 gRPC streaming test started. target=" + activeServerTarget + " room=" + activeRoomId);

            try
            {
                bool connected = await ConnectAsync();
                if (!connected) return Fail("Connect failed.");

                bool authenticated = await AuthenticateAsync();
                if (!authenticated) return Fail("Auth failed.");

                bool pongReceived = await SendManualPingAndWaitForPongAsync();
                if (!pongReceived) return Fail("Ping/Pong failed.");

                if (enableRoomFlow && joinRoomAfterAuth)
                {
                    bool joined = await JoinRoomAndWaitAckAsync();
                    if (!joined) return Fail("Join room failed.");

                    if (leaveRoomAtEnd)
                    {
                        bool left = await LeaveRoomAndWaitAckAsync();
                        if (!left) return Fail("Leave room failed.");
                    }
                }
                else
                {
                    Log("Room flow is disabled for safe U5 test.");
                }

                Log("U5 gRPC streaming test completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("U5 gRPC streaming test canceled.");
            }
            catch (Exception ex)
            {
                return Fail("U5 gRPC streaming test exception: " + ex.Message);
            }
            finally
            {
                ClearWaiters();

                if (autoDisconnectAtEnd)
                {
                    await DisconnectAsync("U5 gRPC streaming finished");
                }

                isRunning = false;
            }
        }

        //* فقط کانال جی‌آر‌پی‌سی استریمینگ را باز می‌کند تا اتصال پایه جدا از اَث و روم بررسی شود.
        public async Task<bool> RunConnectOnlyAsync()
        {
            if (isRunning)
            {
                Log("U5 gRPC streaming test is already running.");
                return false;
            }

            if (lifecycleCts == null || lifecycleCts.IsCancellationRequested) lifecycleCts = new CancellationTokenSource();
            if (realtimeClient == null) CreateClients();

            isRunning = true;
            Log("U5 gRPC streaming connect-only started. target=" + activeServerTarget);

            try
            {
                bool connected = await ConnectAsync();
                if (!connected) return Fail("Connect-only failed.");

                Log("U5 gRPC streaming connect-only completed successfully.");

                if (autoDisconnectAtEnd) await DisconnectAsync("U5 gRPC streaming connect-only completed");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("U5 gRPC streaming connect-only canceled.");
            }
            catch (Exception ex)
            {
                return Fail("U5 gRPC streaming connect-only exception: " + ex.Message);
            }
            finally
            {
                ClearWaiters();
                isRunning = false;
            }
        }

        //* کُر ریل‌تایم را از طریق ترنسپورت جی‌آر‌پی‌سی استریمینگ وصل می‌کند.
        private async Task<bool> ConnectAsync()
        {
            Log("Connecting through gRPC streaming to " + activeServerTarget);
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

        //* درخواست خروج از روم را می‌فرستد و اَک leave_room را کنترل می‌کند.
        private async Task<bool> LeaveRoomAndWaitAckAsync()
        {
            PrepareAckWaiter("leave_room_");
            bool sent = await gameServerClient.LeaveRoomAsync(activeRoomId, lifecycleCts.Token);
            if (!sent) return false;
            return await WaitWithTimeoutAsync(ackWaiter, "leave_room ack", waitTimeoutMs, lifecycleCts.Token);
        }

        //* اتصال فعال را با تایم‌اوت کوتاه می‌بندد تا ادیتور هنگام استاپ یا ریلود دامین منتظر شبکه نماند.
        private async Task DisconnectAsync(string reason)
        {
            if (realtimeClient == null) return;

            int timeout = Math.Max(500, disconnectTimeoutMs);

            try
            {
                using (CancellationTokenSource timeoutCts = new CancellationTokenSource(timeout))
                {
                    Task disconnectTask = realtimeClient.DisconnectAsync(reason, timeoutCts.Token);
                    Task completedTask = await Task.WhenAny(disconnectTask, Task.Delay(timeout + 250));

                    if (completedTask == disconnectTask)
                    {
                        await disconnectTask;
                        Log("Disconnect completed: " + reason);
                        return;
                    }

                    Log("Disconnect timed out and was not awaited further: " + reason);
                }
            }
            catch (OperationCanceledException)
            {
                Log("Disconnect canceled: " + reason);
            }
            catch (Exception ex)
            {
                Log("Disconnect exception: " + ex.Message);
            }
        }

        #endregion

        #region <Client Setup>

        //* کلاینت‌های تست را با کانفیگ جی‌آر‌پی‌سی استریمینگ می‌سازد و رویدادها را وصل می‌کند.
        private void CreateClients()
        {
            activeServerTarget = ResolveRealtimeServerTarget();

            var config = new RealtimeConfig
            {
                serverUrl = activeServerTarget,
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

            BindEvents();
        }

        //* رویدادهای کُر، اَث و گیم‌سرور را به کنترلر تست وصل می‌کند.
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

            eventsBound = false;
        }

        //* آدرس تارگت جی‌آر‌پی‌سی ریل‌تایم را از سرورکانفیگ مرکزی یا مقدار دستی اینسپکتور می‌گیرد.
        private string ResolveRealtimeServerTarget()
        {
            if (!string.IsNullOrWhiteSpace(serverTarget)) return serverTarget.Trim();

            if (useServerConfigTarget)
            {
                if (forceDedicatedServerConfig) return BuildRealtimeTargetFromEndpoint(ServerConfig.DedicatedRealtimeGrpcStreamingEndpoint);
                return BuildRealtimeTargetFromEndpoint(ServerConfig.RealtimeGrpcStreamingEndpoint);
            }

            return ServerConfig.BuildRealtimeGrpcStreamingTarget();
        }

        //* اندپوینت ریل‌تایم اِستریمینگ را بدون تغییر دادن سرورکانفیگ اصلی به تارگت جی‌آر‌پی‌سی تبدیل می‌کند.
        private static string BuildRealtimeTargetFromEndpoint(Network_A.Core.Endpoint endpoint)
        {
            string host = string.IsNullOrWhiteSpace(endpoint.Host) ? "dev-world-3d.metarang.com" : endpoint.Host.Trim();
            int port = endpoint.Port > 0 ? endpoint.Port : 50052;
            string scheme = endpoint.UseTls ? "grpcs://" : "grpc://";
            return scheme + host + ":" + port;
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

        //* همه کلاینت‌های ساخته‌شده برای تست را آزاد می‌کند؛ این تابع فقط در مسیرهای کنترل‌شده صدا زده شود، نه در آن‌دیستروی.
        private async Task CleanupAsync()
        {
            UnbindEvents();

            if (gameServerClient != null) gameServerClient.Dispose();
            if (realtimeAuthClient != null) realtimeAuthClient.Dispose();
            if (realtimeClient != null) await DisconnectAsync("U5 cleanup");

            gameServerClient = null;
            realtimeAuthClient = null;
            realtimeClient = null;
        }

        #endregion

        #region <Format Helpers>

        //* برای هر اجرای تست یک روم یکتا می‌سازد تا تست‌ها با هم قاطی نشوند.
        private string BuildRunRoomId()
        {
            string prefix = string.IsNullOrWhiteSpace(roomIdPrefix) ? "unity_u5_grpc_room" : roomIdPrefix.Trim();
            return prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        //* خطای ریل‌تایم را به متن کوتاه قابل لاگ تبدیل می‌کند.
        private static string FormatError(RealtimeError error)
        {
            return error == null ? "unknown" : error.code + " | " + error.message;
        }

        //* شکست تست را لاگ می‌کند و false برمی‌گرداند.
        private bool Fail(string message)
        {
            Log("U5 gRPC streaming failed: " + message);
            return false;
        }

        //* لاگ یکدست تست را در کنسول یونیتی چاپ می‌کند.
        private void Log(string message)
        {
            Debug.Log("[RealtimeGrpcStreamingU5] " + message);
        }

        #endregion
    }
}

//* این فایل تست نیتیو جی‌آر‌پی‌سی استریمینگ را برای یونیتی اجرا می‌کند.
//* تست فقط از RealtimeClient و GameServerClient استفاده می‌کند و به وب‌سوکت خام وابسته نیست.
