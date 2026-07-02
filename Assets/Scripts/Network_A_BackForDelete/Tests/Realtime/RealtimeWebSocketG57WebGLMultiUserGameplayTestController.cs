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
    //* نقش این کلاینت را در تست چند مرورگره مشخص می‌کند.
    public enum RealtimeWebSocketG57BrowserRole
    {
        JoinOnly,
        Receiver,
        Sender
    }

    //* تست جی‌فایو هفت است و مسیر واقعی دو مرورگر، دو کاربر، Auth، Join Room، Movement و World Event را بررسی می‌کند.
    public class RealtimeWebSocketG57WebGLMultiUserGameplayTestController : MonoBehaviour
    {
        [Header("Connection")]
        [SerializeField] private string serverUrl = "ws://127.0.0.1:8080";
        [SerializeField] private RealtimeTransportKind transportKind = RealtimeTransportKind.WebSocket;
        [SerializeField] private string roomId = "webgl_g57_room";

        [Header("Run")]
        [SerializeField] private bool runOnStart;
        [SerializeField] private bool readRoleFromUrl = true;
        [SerializeField] private RealtimeWebSocketG57BrowserRole defaultRole = RealtimeWebSocketG57BrowserRole.JoinOnly;
        [SerializeField] private bool disconnectAtEnd;

        [Header("Timing")]
        [SerializeField] private int connectTimeoutMs = 10000;
        [SerializeField] private int sendTimeoutMs = 10000;
        [SerializeField] private int waitTimeoutMs = 15000;
        [SerializeField] private int senderStartDelayMs = 1500;
        [SerializeField] private int reliableAckTimeoutMs = 5000;

        [Header("Sender Payload")]
        [SerializeField] private string senderPlayerId = "webgl_sender_player";
        [SerializeField] private Vector3 senderPosition = new Vector3(1.5f, 0f, 2.5f);
        [SerializeField] private Vector3 senderVelocity = new Vector3(0.25f, 0f, 0.5f);
        [SerializeField] private float senderYaw = 45f;
        [SerializeField] private string actionType = "webgl_test_fire";
        [SerializeField] private string worldEventType = "webgl_test_object_state";

        private RealtimeClient realtimeClient;
        private RealtimeAuthClient realtimeAuthClient;
        private GameServerClient gameServerClient;
        private CancellationTokenSource lifecycleCts;
        private bool isRunning;
        private bool isJoined;
        private TaskCompletionSource<bool> authWaiter;
        private TaskCompletionSource<GameServerPresenceEvent> playerJoinedWaiter;
        private TaskCompletionSource<RealtimeEnvelope> playerStateWaiter;
        private TaskCompletionSource<RealtimeEnvelope> worldEventWaiter;

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
                await CleanupAsync("G5.7 object destroyed");
                lifecycleCts?.Dispose();
                lifecycleCts = null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[G5.7-WebGL-MultiUser] Destroy cleanup warning: " + ex.Message);
            }
        }

        #endregion

        #region <Inspector Buttons>

        //* این دکمه فقط اتصال، احراز هویت و ورود به روم را با توکن ذخیره‌شده انجام می‌دهد.
        public async void ConnectAuthJoinButton()
        {
            await ConnectAuthJoinAsync();
        }

        //* این دکمه نقش گیرنده را اجرا می‌کند و منتظر پیام‌های کاربر دیگر می‌ماند.
        public async void RunReceiverButton()
        {
            await RunReceiverFlowAsync();
        }

        //* این دکمه نقش فرستنده را اجرا می‌کند و پیام‌های تستی را برای مرورگر دیگر می‌فرستد.
        public async void RunSenderButton()
        {
            await RunSenderFlowAsync();
        }

        //* این دکمه فقط یک پیام Movement از همین مرورگر می‌فرستد.
        public async void SendMovementButton()
        {
            await SendMovementSnapshotAsync();
        }

        //* این دکمه فقط یک اکشن قابل اطمینان از همین مرورگر می‌فرستد.
        public async void SendPlayerActionButton()
        {
            await SendPlayerActionReliableAsync();
        }

        //* این دکمه فقط یک رویداد جهان قابل اطمینان از همین مرورگر می‌فرستد.
        public async void SendWorldEventButton()
        {
            await SendWorldEventReliableAsync();
        }

        //* این دکمه اتصال تست را با close استاندارد می‌بندد.
        public async void DisconnectButton()
        {
            await CleanupAsync("Manual G5.7 disconnect");
        }

        #endregion

        #region <Main Role Flow>

        //* نقش تست را از URL یا Inspector می‌خواند و همان مسیر را اجرا می‌کند.
        public async Task<bool> RunByResolvedRoleAsync()
        {
            RealtimeWebSocketG57BrowserRole role = ResolveRole();
            Log("Resolved role: " + role);

            if (role == RealtimeWebSocketG57BrowserRole.Sender) return await RunSenderFlowAsync();
            if (role == RealtimeWebSocketG57BrowserRole.Receiver) return await RunReceiverFlowAsync();

            return await ConnectAuthJoinAsync();
        }

        //* مسیر گیرنده را اجرا می‌کند و باید قبل از Sender در مرورگر دیگر آماده شود.
        public async Task<bool> RunReceiverFlowAsync()
        {
            if (isRunning) return Fail("Another G5.7 flow is already running.");
            isRunning = true;

            try
            {
                Log("Receiver flow started.");
                bool ready = await ConnectAuthJoinAsync();
                if (!ready) return false;

                Log("Receiver is ready. Start Sender in another browser with another logged-in user.");

                GameServerPresenceEvent joined = await WaitForPlayerJoinedAsync();
                if (joined == null) return Fail("Receiver did not receive player_joined before timeout.");
                Log("Receiver got player_joined. remotePlayerId=" + joined.ResolveNetworkPlayerId());

                RealtimeEnvelope state = await WaitForPlayerStateAsync();
                if (state == null) return Fail("Receiver did not receive player_state before timeout.");
                Log("Receiver got player_state. payload=" + state.payloadJson);

                RealtimeEnvelope world = await WaitForWorldEventAsync();
                if (world == null) return Fail("Receiver did not receive world_event before timeout.");
                Log("Receiver got world_event. payload=" + world.payloadJson);

                if (disconnectAtEnd) await CleanupAsync("G5.7 receiver completed");

                Log("G5.7 receiver flow completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("Receiver flow canceled.");
            }
            catch (Exception ex)
            {
                return Fail("Receiver flow exception: " + ex.Message);
            }
            finally
            {
                isRunning = false;
            }
        }

        //* مسیر فرستنده را اجرا می‌کند و بعد از ورود به روم پیام‌های gameplay را ارسال می‌کند.
        public async Task<bool> RunSenderFlowAsync()
        {
            if (isRunning) return Fail("Another G5.7 flow is already running.");
            isRunning = true;

            try
            {
                Log("Sender flow started.");
                bool ready = await ConnectAuthJoinAsync();
                if (!ready) return false;

                await Task.Delay(Mathf.Max(0, senderStartDelayMs), lifecycleCts.Token);

                bool movementSent = await SendMovementSnapshotAsync();
                if (!movementSent) return Fail("Sender movement send failed.");

                bool actionSent = await SendPlayerActionReliableAsync();
                if (!actionSent) return Fail("Sender player_action reliable failed.");

                bool worldSent = await SendWorldEventReliableAsync();
                if (!worldSent) return Fail("Sender world_event reliable failed.");

                if (disconnectAtEnd) await CleanupAsync("G5.7 sender completed");

                Log("G5.7 sender flow completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("Sender flow canceled.");
            }
            catch (Exception ex)
            {
                return Fail("Sender flow exception: " + ex.Message);
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
                return Fail("Stored access token is empty. Login must complete before running G5.7.");
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
            var options = new RealtimeReliableSendOptions
            {
                ackTimeoutMs = reliableAckTimeoutMs,
                maxSendAttempts = 3,
                retryDelayMs = 300,
                retryOnAckTimeout = true,
                retryOnTransportSendFailed = true
            };

            RealtimeReliableSendResult result = await gameServerClient.JoinRoomReliableAsync(roomId, options, lifecycleCts.Token);
            bool ok = result != null && result.isSuccess;
            Log("Join reliable result: " + ok + " | attempts=" + (result == null ? 0 : result.attempts) + " | error=" + (result == null ? "null" : result.errorMessage));
            return ok;
        }

        #endregion

        #region <Gameplay Send>

        //* یک snapshot حرکتی latest-only برای تست مسیر player_state ارسال می‌کند.
        public async Task<bool> SendMovementSnapshotAsync()
        {
            if (!EnsureJoinedForSend()) return false;

            Quaternion rotation = Quaternion.Euler(0f, senderYaw, 0f);
            long sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool sent = await gameServerClient.SendPlayerStateAsync(senderPlayerId, senderPosition, rotation, senderVelocity, sequence, lifecycleCts.Token);
            Log("Movement send result: " + sent + " | sequence=" + sequence);
            return sent;
        }

        //* یک player_action قابل اطمینان ارسال می‌کند و تا ACK منتظر می‌ماند.
        public async Task<bool> SendPlayerActionReliableAsync()
        {
            if (!EnsureJoinedForSend()) return false;

            string payloadJson = "{\"weaponId\":\"test_blaster\",\"fire\":true,\"ts\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}";
            RealtimeReliableSendResult result = await gameServerClient.SendPlayerActionReliableAsync(actionType, payloadJson, CreateReliableOptions(), lifecycleCts.Token);
            bool ok = result != null && result.isSuccess;
            Log("Player action reliable result: " + ok + " | attempts=" + (result == null ? 0 : result.attempts) + " | error=" + (result == null ? "null" : result.errorMessage));
            return ok;
        }

        //* یک world_event قابل اطمینان ارسال می‌کند و تا ACK منتظر می‌ماند.
        public async Task<bool> SendWorldEventReliableAsync()
        {
            if (!EnsureJoinedForSend()) return false;

            string payloadJson = "{\"objectId\":\"webgl_g57_door\",\"stateKey\":\"open\",\"boolValue\":true,\"ts\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}";
            RealtimeReliableSendResult result = await gameServerClient.SendWorldEventReliableAsync(worldEventType, payloadJson, CreateReliableOptions(), lifecycleCts.Token);
            bool ok = result != null && result.isSuccess;
            Log("World event reliable result: " + ok + " | attempts=" + (result == null ? 0 : result.attempts) + " | error=" + (result == null ? "null" : result.errorMessage));
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

        //* قبل از ارسال gameplay مطمئن می‌شود کلاینت داخل روم است.
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

        //* رویدادهای گیم‌سرور را برای دریافت پیام‌های کاربر دیگر وصل می‌کند.
        private void BindGameEvents()
        {
            gameServerClient.Events.LogReceived += message => Log("Game: " + message);

            gameServerClient.Events.PlayerJoinedReceived += presence =>
            {
                if (presence == null) return;
                Log("Player joined received. playerId=" + presence.ResolveNetworkPlayerId());
                CompleteWaiter(playerJoinedWaiter, presence);
            };

            gameServerClient.Events.PlayerStateReceived += envelope =>
            {
                if (envelope == null) return;
                Log("Player state received.");
                CompleteWaiter(playerStateWaiter, envelope);
            };

            gameServerClient.Events.WorldEventReceived += envelope =>
            {
                if (envelope == null) return;
                Log("World event received.");
                CompleteWaiter(worldEventWaiter, envelope);
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

        //* تا دریافت player_state از مرورگر دیگر منتظر می‌ماند.
        private async Task<RealtimeEnvelope> WaitForPlayerStateAsync()
        {
            playerStateWaiter = new TaskCompletionSource<RealtimeEnvelope>();
            return await WaitForResultAsync(playerStateWaiter, waitTimeoutMs, lifecycleCts.Token);
        }

        //* تا دریافت world_event از مرورگر دیگر منتظر می‌ماند.
        private async Task<RealtimeEnvelope> WaitForWorldEventAsync()
        {
            worldEventWaiter = new TaskCompletionSource<RealtimeEnvelope>();
            return await WaitForResultAsync(worldEventWaiter, waitTimeoutMs, lifecycleCts.Token);
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
        private RealtimeWebSocketG57BrowserRole ResolveRole()
        {
            if (!readRoleFromUrl) return defaultRole;

            string role = ReadQueryValue("role");
            if (string.IsNullOrWhiteSpace(role)) return defaultRole;

            if (string.Equals(role, "sender", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG57BrowserRole.Sender;
            if (string.Equals(role, "receiver", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG57BrowserRole.Receiver;
            if (string.Equals(role, "join", StringComparison.OrdinalIgnoreCase)) return RealtimeWebSocketG57BrowserRole.JoinOnly;

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
            playerStateWaiter = null;
            worldEventWaiter = null;
            isJoined = false;
        }

        #endregion

        #region <Logging>

        //* پیام تست را با prefix ثابت در Console چاپ می‌کند.
        private void Log(string message)
        {
            Debug.Log("[G5.7-WebGL-MultiUser] " + message);
        }

        //* شکست تست را ثبت می‌کند و مقدار false برمی‌گرداند.
        private bool Fail(string message)
        {
            Debug.LogError("[G5.7-WebGL-MultiUser] " + message);
            return false;
        }

        #endregion
    }
}

//* این فایل تست چند کاربره WebGL را برای فاز G5.7 اجرا می‌کند.
//* این تست هیچ توکن دستی نمی‌گیرد و فقط از SecureTokenStorage بعد از Login موفق استفاده می‌کند.
