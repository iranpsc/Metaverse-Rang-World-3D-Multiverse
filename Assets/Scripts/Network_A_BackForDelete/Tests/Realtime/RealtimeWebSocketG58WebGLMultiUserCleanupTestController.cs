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
    //* نقش این کلاینت را در تست خروج و پاکسازی چند مرورگره مشخص می‌کند.
    public enum RealtimeWebSocketG58BrowserRole
    {
        JoinOnly,
        CleanupReceiver,
        CleanupLeaver
    }

    //* تست جی‌فایو هشت است و خروج از روم، دریافت player_left و قطع اتصال تمیز را در WebGL چندکاربره بررسی می‌کند.
    public class RealtimeWebSocketG58WebGLMultiUserCleanupTestController : MonoBehaviour
    {
        [Header("Connection")]
        [SerializeField] private string serverUrl = "ws://127.0.0.1:8080";
        [SerializeField] private RealtimeTransportKind transportKind = RealtimeTransportKind.WebSocket;
        [SerializeField] private string roomId = "webgl_g58_cleanup_room";

        [Header("Run")]
        [SerializeField] private bool runOnStart;
        [SerializeField] private bool readRoleFromUrl = true;
        [SerializeField] private RealtimeWebSocketG58BrowserRole defaultRole = RealtimeWebSocketG58BrowserRole.JoinOnly;
        [SerializeField] private bool disconnectAtEnd = true;

        [Header("Timing")]
        [SerializeField] private int connectTimeoutMs = 10000;
        [SerializeField] private int sendTimeoutMs = 10000;
        [SerializeField] private int waitTimeoutMs = 15000;
        [SerializeField] private int leaverStartDelayMs = 1500;
        [SerializeField] private int leaveAfterJoinDelayMs = 2500;
        [SerializeField] private int reliableAckTimeoutMs = 5000;

        [Header("Optional Movement Before Leave")]
        [SerializeField] private bool sendMovementBeforeLeave = true;
        [SerializeField] private string leaverPlayerId = "webgl_g58_leaver_player";
        [SerializeField] private Vector3 leaverPosition = new Vector3(3f, 0f, 1f);
        [SerializeField] private Vector3 leaverVelocity = new Vector3(0.15f, 0f, 0.2f);
        [SerializeField] private float leaverYaw = 90f;

        private RealtimeClient realtimeClient;
        private RealtimeAuthClient realtimeAuthClient;
        private GameServerClient gameServerClient;
        private CancellationTokenSource lifecycleCts;
        private bool isRunning;
        private bool isJoined;
        private TaskCompletionSource<bool> authWaiter;
        private TaskCompletionSource<GameServerPresenceEvent> playerJoinedWaiter;
        private TaskCompletionSource<GameServerPresenceEvent> playerLeftWaiter;
        private TaskCompletionSource<GameServerAckResult> leaveAckWaiter;

        #region <Unity Lifecycle>

        //* منبع لغو تست را هنگام ساخت آبجکت آماده می‌کند.
        private void Awake()
        {
            lifecycleCts = new CancellationTokenSource();
        }

        //* اگر اجرای خودکار فعال باشد، نقش تست را می‌خواند و همان مسیر را اجرا می‌کند.
        private async void Start()
        {
            if (!runOnStart) return;
            await RunByResolvedRoleAsync();
        }

        //* هنگام حذف آبجکت، اتصال باز را تمیز می‌بندد و منابع تست را آزاد می‌کند.
        private async void OnDestroy()
        {
            try
            {
                lifecycleCts?.Cancel();
                await CleanupAsync("G5.8 object destroyed");
                lifecycleCts?.Dispose();
                lifecycleCts = null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[G5.8-WebGL-Cleanup] Destroy cleanup warning: " + ex.Message);
            }
        }

        #endregion

        #region <Inspector Buttons>

        //* این دکمه فقط اتصال، احراز هویت و ورود به روم را با توکن ذخیره‌شده انجام می‌دهد.
        public async void ConnectAuthJoinButton()
        {
            await ConnectAuthJoinAsync();
        }

        //* این دکمه نقش گیرنده پاکسازی را اجرا می‌کند و منتظر player_joined و سپس player_left می‌ماند.
        public async void RunCleanupReceiverButton()
        {
            await RunCleanupReceiverFlowAsync();
        }

        //* این دکمه نقش خارج‌شونده را اجرا می‌کند و بعد از ورود به روم، leave و disconnect تمیز انجام می‌دهد.
        public async void RunCleanupLeaverButton()
        {
            await RunCleanupLeaverFlowAsync();
        }

        //* این دکمه فقط یک پیام Movement از همین مرورگر می‌فرستد.
        public async void SendMovementButton()
        {
            await SendMovementSnapshotAsync();
        }

        //* این دکمه فقط خروج از روم را با انتظار ACK تست می‌کند.
        public async void LeaveRoomButton()
        {
            await LeaveRoomAndWaitAckAsync();
        }

        //* این دکمه اتصال تست را با close استاندارد می‌بندد.
        public async void DisconnectButton()
        {
            await CleanupAsync("Manual G5.8 disconnect");
        }

        #endregion

        #region <Main Role Flow>

        //* نقش تست را از URL یا Inspector می‌خواند و همان مسیر را اجرا می‌کند.
        public async Task<bool> RunByResolvedRoleAsync()
        {
            RealtimeWebSocketG58BrowserRole role = ResolveRole();
            Log("Resolved role: " + role);

            if (role == RealtimeWebSocketG58BrowserRole.CleanupLeaver) return await RunCleanupLeaverFlowAsync();
            if (role == RealtimeWebSocketG58BrowserRole.CleanupReceiver) return await RunCleanupReceiverFlowAsync();

            return await ConnectAuthJoinAsync();
        }

        //* مسیر گیرنده پاکسازی را اجرا می‌کند و باید قبل از مرورگر خارج‌شونده آماده شود.
        public async Task<bool> RunCleanupReceiverFlowAsync()
        {
            if (isRunning) return Fail("Another G5.8 flow is already running.");
            isRunning = true;

            try
            {
                Log("Cleanup receiver flow started.");
                bool ready = await ConnectAuthJoinAsync();
                if (!ready) return false;

                Log("Cleanup receiver is ready. Start CleanupLeaver in another browser with another logged-in user.");

                GameServerPresenceEvent joined = await WaitForPlayerJoinedAsync();
                if (joined == null) return Fail("Receiver did not receive player_joined before timeout.");
                Log("Receiver got player_joined. remotePlayerId=" + joined.ResolveNetworkPlayerId());

                GameServerPresenceEvent left = await WaitForPlayerLeftAsync();
                if (left == null) return Fail("Receiver did not receive player_left before timeout.");
                Log("Receiver got player_left. remotePlayerId=" + left.ResolveNetworkPlayerId());

                if (disconnectAtEnd) await CleanupAsync("G5.8 receiver completed");

                Log("G5.8 cleanup receiver flow completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("Cleanup receiver flow canceled.");
            }
            catch (Exception ex)
            {
                return Fail("Cleanup receiver flow exception: " + ex.Message);
            }
            finally
            {
                isRunning = false;
            }
        }

        //* مسیر خارج‌شونده را اجرا می‌کند و بعد از ورود به روم، player_state اختیاری، leave و disconnect تمیز انجام می‌دهد.
        public async Task<bool> RunCleanupLeaverFlowAsync()
        {
            if (isRunning) return Fail("Another G5.8 flow is already running.");
            isRunning = true;

            try
            {
                Log("Cleanup leaver flow started.");
                await Task.Delay(Mathf.Max(0, leaverStartDelayMs), lifecycleCts.Token);

                bool ready = await ConnectAuthJoinAsync();
                if (!ready) return false;

                if (sendMovementBeforeLeave)
                {
                    bool movementSent = await SendMovementSnapshotAsync();
                    if (!movementSent) return Fail("Leaver movement send failed before leave.");
                }

                await Task.Delay(Mathf.Max(0, leaveAfterJoinDelayMs), lifecycleCts.Token);

                bool left = await LeaveRoomAndWaitAckAsync();
                if (!left) return Fail("Leaver leave_room ack failed.");

                if (disconnectAtEnd) await CleanupAsync("G5.8 leaver completed");

                Log("G5.8 cleanup leaver flow completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("Cleanup leaver flow canceled.");
            }
            catch (Exception ex)
            {
                return Fail("Cleanup leaver flow exception: " + ex.Message);
            }
            finally
            {
                isRunning = false;
            }
        }

        #endregion

        #region <Connect Auth Join>

        //* اتصال، احراز هویت ریل‌تایم و ورود reliable به روم را با توکن ذخیره‌شده انجام می‌دهد.
        public async Task<bool> ConnectAuthJoinAsync()
        {
            if (isJoined && realtimeClient != null && realtimeClient.IsConnected) return true;

            string storedToken = SecureTokenStorage.GetAccessToken();
            if (string.IsNullOrWhiteSpace(storedToken))
            {
                return Fail("Stored access token is empty. Login must complete before running G5.8.");
            }

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
            authWaiter = new TaskCompletionSource<bool>();

            bool sent = await realtimeAuthClient.AuthenticateWithStoredTokenAsync(lifecycleCts.Token);
            if (!sent) return Fail("Realtime auth message was not sent.");

            bool ok = await WaitForBoolAsync(authWaiter, waitTimeoutMs, lifecycleCts.Token);
            Log("Auth result: " + ok);
            return ok;
        }

        //* درخواست ورود به روم را به صورت reliable می‌فرستد و دریافت ACK را بررسی می‌کند.
        private async Task<bool> JoinRoomReliableAsync()
        {
            RealtimeReliableSendResult result = await gameServerClient.JoinRoomReliableAsync(roomId, CreateReliableOptions(), lifecycleCts.Token);
            bool ok = result != null && result.isSuccess;
            Log("Join reliable result: " + ok + " | attempts=" + (result == null ? 0 : result.attempts) + " | error=" + (result == null ? "null" : result.errorMessage));
            return ok;
        }

        #endregion

        #region <Gameplay And Cleanup Send>

        //* یک snapshot حرکتی latest-only برای تست اینکه پلیر قبل از خروج در روم دیده شده ارسال می‌کند.
        public async Task<bool> SendMovementSnapshotAsync()
        {
            if (!EnsureJoinedForSend()) return false;

            Quaternion rotation = Quaternion.Euler(0f, leaverYaw, 0f);
            long sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool sent = await gameServerClient.SendPlayerStateAsync(leaverPlayerId, leaverPosition, rotation, leaverVelocity, sequence, lifecycleCts.Token);
            Log("Movement send result: " + sent + " | sequence=" + sequence);
            return sent;
        }

        //* خروج از روم را می‌فرستد و تا دریافت ACK مربوط به leave_room منتظر می‌ماند.
        public async Task<bool> LeaveRoomAndWaitAckAsync()
        {
            if (!EnsureJoinedForSend()) return false;

            leaveAckWaiter = new TaskCompletionSource<GameServerAckResult>();
            bool sent = await gameServerClient.LeaveRoomAsync(roomId, lifecycleCts.Token);
            Log("Leave room send result: " + sent + " | room=" + roomId);
            if (!sent) return false;

            GameServerAckResult ack = await WaitForResultAsync(leaveAckWaiter, waitTimeoutMs, lifecycleCts.Token);
            bool ok = ack != null && ack.IsProcessed();
            Log("Leave room ack result: " + ok + " | original=" + (ack == null ? "null" : ack.originalMessageId) + " | status=" + (ack == null ? "null" : ack.status));

            if (ok) isJoined = false;
            return ok;
        }

        //* تنظیمات ACK و retry پیام‌های reliable را برای تست می‌سازد.
        private RealtimeReliableSendOptions CreateReliableOptions()
        {
            return new RealtimeReliableSendOptions
            {
                ackTimeoutMs = reliableAckTimeoutMs,
                maxSendAttempts = 3,
                retryDelayMs = 300,
                retryOnAckTimeout = true,
                retryOnTransportSendFailed = true
            };
        }

        //* قبل از ارسال gameplay یا leave مطمئن می‌شود کلاینت داخل روم است.
        private bool EnsureJoinedForSend()
        {
            if (gameServerClient == null) return Fail("GameServerClient is null.");
            if (!isJoined && !gameServerClient.HasRoom) return Fail("Client is not joined to a room.");
            return true;
        }

        #endregion

        #region <Client Setup>

        //* کلاینت‌های ریل‌تایم، احراز هویت و گیم‌سرور را روی ساختار فعلی پروژه می‌سازد.
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

            BindRealtimeEvents();
            BindAuthEvents();
            BindGameEvents();
        }

        //* رویدادهای کُر ریل‌تایم را برای لاگ تست وصل می‌کند.
        private void BindRealtimeEvents()
        {
            realtimeClient.StateChanged += state => Log("State changed: " + state);
            realtimeClient.TransportErrorReceived += error => Log("Transport error: " + error);
            realtimeClient.Disconnected += reason => Log("Disconnected: " + reason);
            realtimeClient.ReliableLogReceived += message => Log("Reliable: " + message);
            realtimeClient.ReliableAckTimeout += messageId => Log("Reliable ack timeout: " + messageId);
        }

        //* رویدادهای احراز هویت ریل‌تایم را برای تکمیل انتظار auth_ok وصل می‌کند.
        private void BindAuthEvents()
        {
            realtimeAuthClient.Authenticated += (connectionId, userId) =>
            {
                Log("Authenticated. connectionId=" + connectionId + " userId=" + userId);
                CompleteBoolWaiter(authWaiter, true);
            };

            realtimeAuthClient.AuthenticationFailed += error =>
            {
                Log("Authentication failed: " + (error == null ? "unknown" : error.code + " | " + error.message));
                CompleteBoolWaiter(authWaiter, false);
            };

            realtimeAuthClient.AuthLogReceived += Log;
        }

        //* رویدادهای گیم‌سرور را برای دریافت player_left و ACK خروج از روم وصل می‌کند.
        private void BindGameEvents()
        {
            gameServerClient.Events.LogReceived += message => Log("Game: " + message);

            gameServerClient.Events.AckReceived += ack =>
            {
                if (ack == null) return;
                Log("Game ack received: " + ack.originalMessageId + " | " + ack.status);

                if (!string.IsNullOrWhiteSpace(ack.originalMessageId) && ack.originalMessageId.StartsWith("leave_room", StringComparison.OrdinalIgnoreCase))
                {
                    CompleteWaiter(leaveAckWaiter, ack);
                }
            };

            gameServerClient.Events.PlayerJoinedReceived += presence =>
            {
                if (presence == null) return;
                Log("Player joined received. playerId=" + presence.ResolveNetworkPlayerId());
                CompleteWaiter(playerJoinedWaiter, presence);
            };

            gameServerClient.Events.PlayerLeftReceived += presence =>
            {
                if (presence == null) return;
                Log("Player left received. playerId=" + presence.ResolveNetworkPlayerId());
                CompleteWaiter(playerLeftWaiter, presence);
            };

            gameServerClient.Events.ErrorReceived += error => Log("Game error: " + (error == null ? "unknown" : error.code + " | " + error.message));
        }

        #endregion

        #region <Wait Helpers>

        //* تا دریافت player_joined از مرورگر دیگر منتظر می‌ماند.
        private async Task<GameServerPresenceEvent> WaitForPlayerJoinedAsync()
        {
            playerJoinedWaiter = new TaskCompletionSource<GameServerPresenceEvent>();
            return await WaitForResultAsync(playerJoinedWaiter, waitTimeoutMs, lifecycleCts.Token);
        }

        //* تا دریافت player_left از مرورگر دیگر منتظر می‌ماند.
        private async Task<GameServerPresenceEvent> WaitForPlayerLeftAsync()
        {
            playerLeftWaiter = new TaskCompletionSource<GameServerPresenceEvent>();
            return await WaitForResultAsync(playerLeftWaiter, waitTimeoutMs, lifecycleCts.Token);
        }

        //* منتظر نتیجه bool می‌ماند و در صورت تایم اوت false برمی‌گرداند.
        private async Task<bool> WaitForBoolAsync(TaskCompletionSource<bool> waiter, int timeoutMs, CancellationToken cancellationToken)
        {
            if (waiter == null) return false;

            Task completed = await Task.WhenAny(waiter.Task, Task.Delay(Mathf.Max(1, timeoutMs), cancellationToken));
            if (completed != waiter.Task) return false;
            return waiter.Task.Result;
        }

        //* منتظر نتیجه typed می‌ماند و در صورت تایم اوت مقدار پیش‌فرض برمی‌گرداند.
        private async Task<T> WaitForResultAsync<T>(TaskCompletionSource<T> waiter, int timeoutMs, CancellationToken cancellationToken)
        {
            if (waiter == null) return default(T);

            Task completed = await Task.WhenAny(waiter.Task, Task.Delay(Mathf.Max(1, timeoutMs), cancellationToken));
            if (completed != waiter.Task) return default(T);
            return waiter.Task.Result;
        }

        //* انتظار bool را اگر هنوز کامل نشده باشد کامل می‌کند.
        private void CompleteBoolWaiter(TaskCompletionSource<bool> waiter, bool value)
        {
            if (waiter == null || waiter.Task.IsCompleted) return;
            waiter.TrySetResult(value);
        }

        //* انتظار typed را اگر هنوز کامل نشده باشد کامل می‌کند.
        private void CompleteWaiter<T>(TaskCompletionSource<T> waiter, T value)
        {
            if (waiter == null || waiter.Task.IsCompleted) return;
            waiter.TrySetResult(value);
        }

        #endregion

        #region <Role Helpers>

        //* نقش تست را از query string مرورگر یا مقدار Inspector انتخاب می‌کند.
        private RealtimeWebSocketG58BrowserRole ResolveRole()
        {
            if (!readRoleFromUrl) return defaultRole;

            string role = ReadQueryValue("role");
            if (string.IsNullOrWhiteSpace(role)) return defaultRole;

            if (string.Equals(role, "leaver", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG58BrowserRole.CleanupLeaver;
            if (string.Equals(role, "cleanup_leaver", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG58BrowserRole.CleanupLeaver;
            if (string.Equals(role, "receiver", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG58BrowserRole.CleanupReceiver;
            if (string.Equals(role, "cleanup_receiver", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG58BrowserRole.CleanupReceiver;
            if (string.Equals(role, "join", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG58BrowserRole.JoinOnly;

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

        //* اتصال و آبجکت‌های تست را بدون حذف هیچ منطق اصلی پروژه پاک‌سازی می‌کند.
        private async Task CleanupAsync(string reason)
        {
            try
            {
                if (gameServerClient != null && gameServerClient.HasRoom)
                {
                    await gameServerClient.LeaveRoomAsync(null, lifecycleCts == null ? default(CancellationToken) : lifecycleCts.Token);
                }
            }
            catch (Exception ex)
            {
                Log("Leave room cleanup warning: " + ex.Message);
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

        //* فقط آبجکت‌های کلاینت و event bindingهای داخلی را Dispose می‌کند.
        private void CleanupClientObjectsOnly()
        {
            gameServerClient?.Dispose();
            gameServerClient = null;

            realtimeAuthClient?.Dispose();
            realtimeAuthClient = null;

            realtimeClient?.Dispose();
            realtimeClient = null;

            authWaiter = null;
            playerJoinedWaiter = null;
            playerLeftWaiter = null;
            leaveAckWaiter = null;
            isJoined = false;
        }

        #endregion

        #region <Logging>

        //* پیام تست را با prefix ثابت در Console چاپ می‌کند.
        private void Log(string message)
        {
            Debug.Log("[G5.8-WebGL-Cleanup] " + message);
        }

        //* شکست تست را ثبت می‌کند و مقدار false برمی‌گرداند.
        private bool Fail(string message)
        {
            Debug.LogError("[G5.8-WebGL-Cleanup] " + message);
            return false;
        }

        #endregion
    }
}

//* این فایل تست خروج و پاکسازی چند کاربره WebGL را برای فاز G5.8 اجرا می‌کند.
//* این تست هیچ توکن دستی نمی‌گیرد و فقط از SecureTokenStorage بعد از Login موفق استفاده می‌کند.
