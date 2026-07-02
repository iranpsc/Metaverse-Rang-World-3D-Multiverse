using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.GameServer;
using Network_A.Realtime.Auth;
using Network_A.Realtime.Core;
using Network_A.Realtime.Protocol;
using Network_A.Realtime.Transport;
using TMPro;
using UnityEngine;
using Network_A.Realtime.Stability;
namespace Network_A.Tests.Realtime
{
    //* تست جی‌سیکس برای تست عمومی تیم است و مسیر لاگین ذخیره‌شده، کانکت، اَث، ساخت روم، ورود، چت، حضور و خروج را روی سرور واقعی بررسی می‌کند.
    public class RealtimeWebSocketG6TeamRoomChatTestController : MonoBehaviour
    {
        [Header("Connection")]
        [SerializeField] private string serverUrl = "wss://dev-world-3d.metarang.com/ws";
        [SerializeField] private bool useServerConfigUrl = true;
        [SerializeField] private bool forceDedicatedServerConfig = true;
        [SerializeField] private RealtimeTransportKind transportKind = RealtimeTransportKind.WebSocket;

        [Header("Room")]
        [SerializeField] private string roomIdPrefix = "webgl_g6_team_room";
        [SerializeField] private string chatActionType = "webgl_g6_chat_message";
        [SerializeField] private string clientLabel = "User";

        [Header("Timing")]
        [SerializeField] private int connectTimeoutMs = 10000;
        [SerializeField] private int sendTimeoutMs = 10000;
        [SerializeField] private int waitTimeoutMs = 15000;
        [SerializeField] private int reliableAckTimeoutMs = 5000;

        [Header("UI")]
        [SerializeField] private TMP_InputField roomInput;
        [SerializeField] private TMP_InputField messageInput;
        [SerializeField] private TextMeshProUGUI roomText;
        [SerializeField] private TextMeshProUGUI statusText;
        [SerializeField] private TextMeshProUGUI logText;

        private RealtimeClient realtimeClient;
        private RealtimeAuthClient realtimeAuthClient;
        private GameServerClient gameServerClient;
        private CancellationTokenSource lifecycleCts;

        private TaskCompletionSource<bool> authWaiter;
        private TaskCompletionSource<bool> leaveAckWaiter;
        private string activeServerUrl = string.Empty;
        private string activeRoomId = string.Empty;
        private bool isConnected;
        private bool isAuthenticated;
        private bool isJoined;
        private bool eventsBound;
        private readonly StringBuilder logBuffer = new StringBuilder(4096);

        #region <Unity Lifecycle>

        //* منبع لغو تست را آماده می‌کند و مقدار اولیه روم را از یوآی می‌خواند.
        private void Awake()
        {
            lifecycleCts = new CancellationTokenSource();
            activeServerUrl = ResolveRealtimeServerUrl();
            SyncRoomFromInput();
            SetStatus("Ready");
        }

        //* هنگام حذف آبجکت، روم و اتصال را تمیز می‌بندد.
        private async void OnDestroy()
        {
            try
            {
                lifecycleCts?.Cancel();
                await CleanupAsync("G6 object destroyed");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[G6-TeamRoomChat] Destroy cleanup warning: " + ex.Message);
            }
            finally
            {
                lifecycleCts?.Dispose();
                lifecycleCts = null;
            }
        }

        #endregion

        #region <UI Buttons>

        //* این دکمه بعد از لاگین معمول پروژه، توکن ذخیره‌شده را چک می‌کند و اتصال ریل‌تایم را با همان توکن انجام می‌دهد.
        public async void LoginCheckAndConnectButton()
        {
            await LoginCheckConnectAndAuthAsync();
        }

        //* این دکمه یک شناسه روم یکتا می‌سازد؛ خود روم هنگام جوین روی سرور ایجاد یا فعال می‌شود.
        public void CreateRoomButton()
        {
            activeRoomId = BuildRoomId();
            if (roomInput != null) roomInput.text = activeRoomId;
            UpdateRoomDisplay();
            Log("Room id created: " + activeRoomId);
            SetStatus("Room created");
        }

        //* این دکمه کاربر فعلی را وارد روم نوشته‌شده در فیلد روم می‌کند.
        public async void JoinRoomButton()
        {
            await JoinRoomAsync();
        }

        //* این دکمه پیام چت را به شکل player_action قابل اطمینان می‌فرستد تا کاربر دیگر در همان روم دریافت کند.
        public async void SendMessageButton()
        {
            string text = messageInput == null ? string.Empty : messageInput.text;
            await SendChatMessageAsync(text);
        }

        //* این دکمه کاربر فعلی را از روم خارج می‌کند و اَک خروج را بررسی می‌کند.
        public async void LeaveRoomButton()
        {
            await LeaveRoomAsync();
        }

        //* این دکمه اتصال ریل‌تایم را کامل می‌بندد.
        public async void DisconnectButton()
        {
            await CleanupAsync("Manual G6 disconnect");
        }

        #endregion

        #region <Main Actions>

        //* توکن ذخیره‌شده بعد از لاگین را می‌خواند، وب‌سوکت را وصل می‌کند و پیام اَث ریل‌تایم را می‌فرستد.
        public async Task<bool> LoginCheckConnectAndAuthAsync()
        {
            if (isConnected && isAuthenticated) return true;

            string storedToken = SecureTokenStorage.GetAccessToken();
            if (string.IsNullOrWhiteSpace(storedToken))
            {
                return Fail("Stored access token is empty. First login with the normal Auth UI in this browser/profile.");
            }

            CreateClientObjects();

            bool connected = await ConnectAsync();
            if (!connected) return Fail("Realtime connect failed.");

            bool authenticated = await AuthenticateWithStoredTokenAsync();
            if (!authenticated) return Fail("Realtime auth failed.");

            SetStatus("Connected + Authenticated");
            Log("Login check and realtime auth completed.");
            return true;
        }

        //* اتصال خام وب‌سوکت را به سرور واقعی شروع می‌کند.
        private async Task<bool> ConnectAsync()
        {
            activeServerUrl = ResolveRealtimeServerUrl();
            Log("Connecting to " + activeServerUrl);

            bool connected = await realtimeClient.ConnectAsync(null, lifecycleCts.Token);
            isConnected = connected;

            Log("Connect result: " + connected);
            return connected;
        }

        //* پیام system/auth را با توکن ذخیره‌شده ارسال می‌کند و تا دریافت auth_ok منتظر می‌ماند.
        private async Task<bool> AuthenticateWithStoredTokenAsync()
        {
            authWaiter = CreateBoolWaiter();

            bool sent = await realtimeAuthClient.AuthenticateWithStoredTokenAsync(lifecycleCts.Token);
            if (!sent) return Fail("Realtime auth message was not sent.");

            bool ok = await WaitForBoolAsync(authWaiter, waitTimeoutMs, lifecycleCts.Token);
            isAuthenticated = ok;
            Log("Auth result: " + ok);
            return ok;
        }

        //* کاربر فعلی را با ارسال reliable join_room وارد روم می‌کند.
        public async Task<bool> JoinRoomAsync()
        {
            SyncRoomFromInput();

            if (!await LoginCheckConnectAndAuthAsync()) return false;
            if (string.IsNullOrWhiteSpace(activeRoomId)) return Fail("Room id is empty. Create or paste a room id first.");
            if (isJoined && gameServerClient != null && gameServerClient.HasRoom) return true;

            RealtimeReliableSendResult result = await gameServerClient.JoinRoomReliableAsync(activeRoomId, CreateReliableOptions(), lifecycleCts.Token);
            bool ok = result != null && result.isSuccess;

            isJoined = ok;
            Log("Join room result: " + ok + " | room=" + activeRoomId + " | attempts=" + (result == null ? 0 : result.attempts) + " | error=" + (result == null ? "null" : result.errorMessage));
            SetStatus(ok ? "Joined room" : "Join failed");
            return ok;
        }

        //* پیام چت را به شکل player_action reliable ارسال می‌کند و منتظر اَک می‌ماند.
        public async Task<bool> SendChatMessageAsync(string text)
        {
            if (!EnsureReadyForRoomMessage()) return false;
            if (string.IsNullOrWhiteSpace(text)) return Fail("Message text is empty.");

            string payloadJson = BuildChatPayload(text.Trim());
            RealtimeReliableSendResult result = await gameServerClient.SendPlayerActionReliableAsync(chatActionType, payloadJson, CreateReliableOptions(), lifecycleCts.Token);
            bool ok = result != null && result.isSuccess;

            Log("Me: " + text.Trim());
            Log("Chat send result: " + ok + " | attempts=" + (result == null ? 0 : result.attempts) + " | error=" + (result == null ? "null" : result.errorMessage));
            SetStatus(ok ? "Message sent" : "Message send failed");

            if (ok && messageInput != null) messageInput.text = string.Empty;
            return ok;
        }

        //* کاربر فعلی را از روم خارج می‌کند و اَک leave_room را کنترل می‌کند.
        public async Task<bool> LeaveRoomAsync()
        {
            if (gameServerClient == null || !isJoined)
            {
                Log("Leave skipped. Client is not joined.");
                return true;
            }

            leaveAckWaiter = CreateBoolWaiter();
            bool sent = await gameServerClient.LeaveRoomAsync(activeRoomId, lifecycleCts.Token);
            if (!sent) return Fail("Leave room message was not sent.");

            bool ack = await WaitForBoolAsync(leaveAckWaiter, waitTimeoutMs, lifecycleCts.Token);
            isJoined = !ack;

            Log("Leave room ack result: " + ack);
            SetStatus(ack ? "Left room" : "Leave timeout");
            return ack;
        }

        #endregion

        #region <Client Setup>

        //* آبجکت‌های کلاینت را با کانفیگ فعلی می‌سازد و رویدادها را وصل می‌کند.
        private void CreateClientObjects()
        {
            CleanupClientObjectsOnly();

            var config = new RealtimeConfig
            {
                serverUrl = activeServerUrl,
                transportKind = transportKind,
                connectTimeoutMs = connectTimeoutMs,
                sendTimeoutMs = sendTimeoutMs,
                autoAuthenticateAfterConnect = false,
                logIncomingMessages = false,
                logOutgoingMessages = false
            };

            realtimeClient = new RealtimeClient(config);
            realtimeAuthClient = new RealtimeAuthClient(realtimeClient);
            gameServerClient = new GameServerClient(realtimeClient);

            BindEvents();
        }

        //* رویدادهای کُر، اَث و گیم‌سرورکلاینت را وصل می‌کند.
        private void BindEvents()
        {
            if (eventsBound) return;
            eventsBound = true;

            realtimeClient.StateChanged += HandleStateChanged;
            realtimeClient.EnvelopeReceived += HandleEnvelopeReceived;
            realtimeClient.TransportErrorReceived += HandleTransportError;
            realtimeClient.Disconnected += HandleDisconnected;
            realtimeClient.ReliableLogReceived += HandleReliableLog;
            realtimeClient.ReliableAckTimeout += HandleReliableAckTimeout;

            realtimeAuthClient.Authenticated += HandleAuthenticated;
            realtimeAuthClient.AuthenticationFailed += HandleAuthenticationFailed;
            realtimeAuthClient.AuthLogReceived += HandleAuthLog;

            gameServerClient.Events.LogReceived += HandleGameLog;
            gameServerClient.Events.AckReceived += HandleAckReceived;
            gameServerClient.Events.ErrorReceived += HandleGameError;
            gameServerClient.Events.PlayerJoinedReceived += HandlePlayerJoinedReceived;
            gameServerClient.Events.PlayerLeftReceived += HandlePlayerLeftReceived;
        }

        //* همه رویدادها را جدا می‌کند تا بعد از تغییر صحنه یا تست مجدد نشتی رویداد ایجاد نشود.
        private void UnbindEvents()
        {
            if (!eventsBound) return;
            eventsBound = false;

            if (realtimeClient != null)
            {
                realtimeClient.StateChanged -= HandleStateChanged;
                realtimeClient.EnvelopeReceived -= HandleEnvelopeReceived;
                realtimeClient.TransportErrorReceived -= HandleTransportError;
                realtimeClient.Disconnected -= HandleDisconnected;
                realtimeClient.ReliableLogReceived -= HandleReliableLog;
                realtimeClient.ReliableAckTimeout -= HandleReliableAckTimeout;
            }

            if (realtimeAuthClient != null)
            {
                realtimeAuthClient.Authenticated -= HandleAuthenticated;
                realtimeAuthClient.AuthenticationFailed -= HandleAuthenticationFailed;
                realtimeAuthClient.AuthLogReceived -= HandleAuthLog;
            }

            if (gameServerClient != null)
            {
                gameServerClient.Events.LogReceived -= HandleGameLog;
                gameServerClient.Events.AckReceived -= HandleAckReceived;
                gameServerClient.Events.ErrorReceived -= HandleGameError;
                gameServerClient.Events.PlayerJoinedReceived -= HandlePlayerJoinedReceived;
                gameServerClient.Events.PlayerLeftReceived -= HandlePlayerLeftReceived;
            }
        }

        //* تنظیمات ارسال reliable را برای join و chat می‌سازد.
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

        //* آدرس وب‌سوکت را از سرورکانفیگ مرکزی یا مقدار دستی اینسپکتور می‌سازد.
        private string ResolveRealtimeServerUrl()
        {
            if (useServerConfigUrl)
            {
                if (forceDedicatedServerConfig)
                {
                    ServerConfig.UseDedicatedGrpcWeb();
                }

                ServerConfig.UseRealtimeWebSocketPath("/ws");
                return ServerConfig.RealtimeWebSocketUrl;
            }

            if (!string.IsNullOrWhiteSpace(serverUrl)) return serverUrl.Trim();
            return ServerConfig.RealtimeWebSocketUrl;
        }

        #endregion

        #region <Event Handlers>

        //* تغییر وضعیت اتصال را در لاگ تست ثبت می‌کند.
        private void HandleStateChanged(RealtimeConnectionState state)
        {
            Log("State changed: " + state);
        }

        //* اِنولوپ‌های خام ریل‌تایم را بررسی می‌کند تا پیام چت برادکست‌شده دریافت شود.
        private void HandleEnvelopeReceived(RealtimeEnvelope envelope)
        {
            if (envelope == null) return;

            if (envelope.ch == RealtimeChannels.Game && envelope.t == RealtimeMessageTypes.PlayerAction)
            {
                HandleIncomingPlayerActionEnvelope(envelope);
            }
        }

        //* خطای ترنسپورت را ثبت می‌کند.
        private void HandleTransportError(string error)
        {
            Log("Transport error: " + error);
            SetStatus("Transport error");
        }

        //* قطع شدن اتصال را ثبت می‌کند و وضعیت داخلی را به‌روزرسانی می‌کند.
        private void HandleDisconnected(string reason)
        {
            isConnected = false;
            isAuthenticated = false;
            isJoined = false;
            Log("Disconnected: " + reason);
            SetStatus("Disconnected");
        }

        //* لاگ داخلی سیستم reliable را ثبت می‌کند.
        private void HandleReliableLog(string message)
        {
            Log("Reliable: " + message);
        }

        //* تایم‌اوت اَک reliable را ثبت می‌کند.
        private void HandleReliableAckTimeout(string messageId)
        {
            Log("Reliable ack timeout: " + messageId);
        }

        //* موفقیت اَث ریل‌تایم را به انتظار اَث وصل می‌کند.
        private void HandleAuthenticated(string connectionId, string userId)
        {
            isAuthenticated = true;
            Log("Authenticated. connectionId=" + connectionId + " userId=" + userId);
            CompleteBoolWaiter(authWaiter, true);
        }

        //* شکست اَث ریل‌تایم را ثبت می‌کند.
        private void HandleAuthenticationFailed(RealtimeError error)
        {
            isAuthenticated = false;
            Log("Authentication failed: " + FormatError(error));
            CompleteBoolWaiter(authWaiter, false);
        }

        //* لاگ اَث را ثبت می‌کند.
        private void HandleAuthLog(string message)
        {
            Log("Auth: " + message);
        }

        //* لاگ گیم‌سرورکلاینت را ثبت می‌کند.
        private void HandleGameLog(string message)
        {
            Log("Game: " + message);
        }

        //* اَک‌های گیم‌سرور را برای خروج از روم بررسی می‌کند.
        private void HandleAckReceived(GameServerAckResult ack)
        {
            if (ack == null) return;
            Log("Ack: " + ack.originalMessageId + " | processed=" + ack.IsProcessed());

            if (ack.originalMessageId.StartsWith("leave_room_", StringComparison.OrdinalIgnoreCase))
            {
                CompleteBoolWaiter(leaveAckWaiter, ack.IsProcessed());
            }
        }

        //* خطاهای گیم‌سرور را ثبت می‌کند.
        private void HandleGameError(RealtimeError error)
        {
            Log("Game error: " + FormatError(error));
        }

        //* ورود پلیر دیگر به روم را ثبت می‌کند.
        private void HandlePlayerJoinedReceived(GameServerPresenceEvent presence)
        {
            if (presence == null) return;
            Log("Player joined: " + presence.ResolveNetworkPlayerId());
        }

        //* خروج پلیر دیگر از روم را ثبت می‌کند.
        private void HandlePlayerLeftReceived(GameServerPresenceEvent presence)
        {
            if (presence == null) return;
            Log("Player left: " + presence.ResolveNetworkPlayerId());
        }

        //* پیام player_action را اگر از نوع چت باشد، مثل پیام چت نمایش می‌دهد.
        private void HandleIncomingPlayerActionEnvelope(RealtimeEnvelope envelope)
        {
            string payload = envelope.payloadJson ?? string.Empty;
            if (!payload.Contains("\"kind\":\"chat\"")) return;
            if (!payload.Contains("\"actionType\":\"" + EscapeJson(chatActionType) + "\"")) return;

            string sender = ReadJsonString(payload, "senderLabel", "Remote");
            string text = ReadJsonString(payload, "text", payload);
            Log(sender + ": " + text);
        }

        #endregion

        #region <Cleanup>

        //* اگر داخل روم باشد خارج می‌شود، سپس اتصال را می‌بندد و آبجکت‌ها را Dispose می‌کند.
        private async Task CleanupAsync(string reason)
        {
            try
            {
                if (gameServerClient != null && isJoined)
                {
                    await gameServerClient.LeaveRoomAsync(activeRoomId, lifecycleCts == null ? default(CancellationToken) : lifecycleCts.Token);
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

            CleanupClientObjectsOnly();
            isConnected = false;
            isAuthenticated = false;
            isJoined = false;
            SetStatus("Cleaned up");
        }

        //* فقط آبجکت‌های کلاینت و انتظارها را آزاد می‌کند.
        private void CleanupClientObjectsOnly()
        {
            UnbindEvents();

            gameServerClient?.Dispose();
            realtimeAuthClient?.Dispose();
            realtimeClient?.Dispose();

            gameServerClient = null;
            realtimeAuthClient = null;
            realtimeClient = null;
            authWaiter = null;
            leaveAckWaiter = null;
        }

        #endregion

        #region <Helpers>

        //* مطمئن می‌شود کاربر برای ارسال پیام داخل روم آماده است.
        private bool EnsureReadyForRoomMessage()
        {
            if (!isConnected || !isAuthenticated) return Fail("Client is not connected/authenticated.");
            if (!isJoined || gameServerClient == null || !gameServerClient.HasRoom) return Fail("Client is not joined to a room.");
            return true;
        }

        //* روم را از اینپوت می‌خواند و مقدار داخلی را به‌روزرسانی می‌کند.
        private void SyncRoomFromInput()
        {
            if (roomInput != null && !string.IsNullOrWhiteSpace(roomInput.text))
            {
                activeRoomId = roomInput.text.Trim();
            }

            UpdateRoomDisplay();
        }

        //* متن نمایش روم را به‌روزرسانی می‌کند.
        private void UpdateRoomDisplay()
        {
            if (roomText != null) roomText.text = "Room: " + (string.IsNullOrWhiteSpace(activeRoomId) ? "-" : activeRoomId);
        }

        //* یک روم آی‌دی یکتا برای تست تیم می‌سازد.
        private string BuildRoomId()
        {
            string prefix = string.IsNullOrWhiteSpace(roomIdPrefix) ? "webgl_g6_team_room" : roomIdPrefix.Trim();
            return prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        //* payload پیام چت را به جیسون ساده تبدیل می‌کند.
        private string BuildChatPayload(string text)
        {
            return "{"
                   + "\"kind\":\"chat\","
                   + "\"actionType\":\"" + EscapeJson(chatActionType) + "\","
                   + "\"senderLabel\":\"" + EscapeJson(clientLabel) + "\","
                   + "\"roomId\":\"" + EscapeJson(activeRoomId) + "\","
                   + "\"text\":\"" + EscapeJson(text) + "\","
                   + "\"ts\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                   + "}";
        }

        //* یک انتظار bool امن برای اَث یا اَک می‌سازد.
        private static TaskCompletionSource<bool> CreateBoolWaiter()
        {
            return new TaskCompletionSource<bool>();
        }

        //* منتظر نتیجه bool می‌ماند و اگر تایم‌اوت شود false برمی‌گرداند.
        private async Task<bool> WaitForBoolAsync(TaskCompletionSource<bool> waiter, int timeoutMs, CancellationToken cancellationToken)
        {
            if (waiter == null) return false;

            Task timeoutTask = Task.Delay(Mathf.Max(500, timeoutMs), cancellationToken);
            Task completed = await Task.WhenAny(waiter.Task, timeoutTask);
            if (completed != waiter.Task) return false;
            return waiter.Task.Result;
        }

        //* انتظار bool را اگر هنوز کامل نشده باشد کامل می‌کند.
        private static void CompleteBoolWaiter(TaskCompletionSource<bool> waiter, bool value)
        {
            if (waiter == null || waiter.Task.IsCompleted) return;
            waiter.TrySetResult(value);
        }

        //* مقدار string را از جیسون ساده استخراج می‌کند.
        private static string ReadJsonString(string json, string key, string fallback)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key)) return fallback;

            string pattern = "\"" + key + "\":\"";
            int start = json.IndexOf(pattern, StringComparison.Ordinal);
            if (start < 0) return fallback;

            start += pattern.Length;
            StringBuilder value = new StringBuilder();
            bool escaped = false;

            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (escaped)
                {
                    if (c == 'n') value.Append('\n');
                    else if (c == 'r') value.Append('\r');
                    else if (c == 't') value.Append('\t');
                    else value.Append(c);
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '"') break;
                value.Append(c);
            }

            return value.Length == 0 ? fallback : value.ToString();
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

        //* خطا را به متن کوتاه تبدیل می‌کند.
        private static string FormatError(RealtimeError error)
        {
            return error == null ? "unknown" : error.code + " | " + error.message;
        }

        //* شکست کنترل‌شده را ثبت می‌کند.
        private bool Fail(string message)
        {
            Log("FAILED: " + message);
            SetStatus("Failed");
            return false;
        }

        //* وضعیت کوتاه را در یوآی نشان می‌دهد.
        private void SetStatus(string value)
        {
            if (statusText != null) statusText.text = "Status: " + value;
        }

        //* لاگ را هم در کنسول و هم در یوآی نمایش می‌دهد.
        private void Log(string message)
        {
            string line = "[G6-TeamRoomChat] " + message;
            Debug.Log(line);

            logBuffer.AppendLine(line);
            if (logBuffer.Length > 6000) logBuffer.Remove(0, logBuffer.Length - 6000);
            if (logText != null) logText.text = logBuffer.ToString();
        }

        #endregion
    }
}
