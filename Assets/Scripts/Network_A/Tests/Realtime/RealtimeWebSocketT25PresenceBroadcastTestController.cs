using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.GameServer;
using Network_A.Realtime.Auth;
using Network_A.Realtime.Core;
using Network_A.Realtime.Protocol;
using Network_A.Realtime.Transport;
using UnityEngine;

namespace Network_A.Tests.Realtime
{
    //* کنترلر تست تی‌تو‌ونیم است و برادکست ورود و خروج پرزنس را بین دو کلاینت بررسی می‌کند.
    public class RealtimeWebSocketT25PresenceBroadcastTestController : MonoBehaviour
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
        [SerializeField] private string roomIdPrefix = "unity_t25_room";
        [SerializeField] private bool requirePlayerJoinedBroadcast = true;
        [SerializeField] private bool requirePlayerLeftBroadcast = true;

        [Header("Timeout")]
        [SerializeField] private int waitTimeoutMs = 10000;

        private RealtimeClient realtimeClientA;
        private RealtimeClient realtimeClientB;
        private RealtimeAuthClient realtimeAuthClientA;
        private RealtimeAuthClient realtimeAuthClientB;
        private GameServerClient gameServerClientA;
        private GameServerClient gameServerClientB;
        private CancellationTokenSource lifecycleCts;

        private TaskCompletionSource<bool> authWaiterA;
        private TaskCompletionSource<bool> authWaiterB;
        private TaskCompletionSource<bool> ackWaiterA;
        private TaskCompletionSource<bool> ackWaiterB;
        private TaskCompletionSource<bool> clientAPlayerJoinedWaiter;
        private TaskCompletionSource<bool> clientAPlayerLeftWaiter;

        private string waitingAckPrefixA = string.Empty;
        private string waitingAckPrefixB = string.Empty;
        private string activeRoomId = string.Empty;
        private string clientBConnectionId = string.Empty;
        private bool isRunning;
        private bool eventsBound;

        #region <Unity Lifecycle>

        //* وابستگی‌های تست پرزنس را می‌سازد تا هر کلاینت اتصال مستقل خودش را داشته باشد.
        private void Awake()
        {
            lifecycleCts = new CancellationTokenSource();
            CreateClients();
        }

        //* اگر از اینسپکتور فعال باشد، تست پرزنس را بعد از شروع صحنه اجرا می‌کند.
        private async void Start()
        {
            if (!runOnStart) return;
            await RunPresenceBroadcastT25TestAsync();
        }

        //* هنگام حذف آبجکت، هر دو اتصال و رویدادهای تست را پاکسازی می‌کند.
        private async void OnDestroy()
        {
            lifecycleCts?.Cancel();
            await CleanupAsync();
            lifecycleCts?.Dispose();
            lifecycleCts = null;
        }

        #endregion

        #region <Inspector Buttons>

        //* از اینسپکتور یا دکمه یوآی برای اجرای تست کامل پرزنس صدا زده می‌شود.
        public async void RunPresenceBroadcastT25TestButton()
        {
            await RunPresenceBroadcastT25TestAsync();
        }

        //* از اینسپکتور یا دکمه یوآی برای قطع هر دو کلاینت تست صدا زده می‌شود.
        public async void DisconnectBothButton()
        {
            await DisconnectBothAsync("Manual T2.5 disconnect");
        }

        #endregion

        #region <Main Test Flow>

        //* مسیر دو کلاینت، اَث، جوین مشترک، دریافت player_joined و دریافت player_left را تست می‌کند.
        public async Task<bool> RunPresenceBroadcastT25TestAsync()
        {
            if (isRunning)
            {
                Log("T2.5 test is already running.");
                return false;
            }

            isRunning = true;
            activeRoomId = BuildRunRoomId();
            clientBConnectionId = string.Empty;
            Log("T2.5 presence broadcast test started. room=" + activeRoomId);

            try
            {
                bool connected = await ConnectBothAsync();
                if (!connected) return Fail("Connect both clients failed.");

                bool authenticated = await AuthenticateBothAsync();
                if (!authenticated) return Fail("Auth both clients failed.");

                bool joinedA = await JoinRoomAAndWaitAckAsync();
                if (!joinedA) return Fail("Client A join room failed.");

                bool joinedBAndPresenceReceived = await JoinRoomBAndVerifyPlayerJoinedAsync();
                if (!joinedBAndPresenceReceived) return Fail("Client B join or player_joined broadcast failed.");

                bool leftBAndPresenceReceived = await LeaveRoomBAndVerifyPlayerLeftAsync();
                if (!leftBAndPresenceReceived) return Fail("Client B leave or player_left broadcast failed.");

                bool leftA = await LeaveRoomAAndWaitAckAsync();
                if (!leftA) return Fail("Client A leave room failed.");

                if (autoDisconnectAtEnd) await DisconnectBothAsync("T2.5 completed");

                Log("T2.5 presence broadcast test completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("T2.5 test canceled.");
            }
            catch (Exception ex)
            {
                return Fail("T2.5 test exception: " + ex.Message);
            }
            finally
            {
                ClearWaiters();
                isRunning = false;
            }
        }

        //* هر دو کُر ریل‌تایم را از طریق ترنسپورت انتخاب‌شده به سرور وصل می‌کند.
        private async Task<bool> ConnectBothAsync()
        {
            Log("Client A connecting to " + serverUrl);
            bool connectedA = await realtimeClientA.ConnectAsync(null, lifecycleCts.Token);
            Log("Client A connect result: " + connectedA);
            if (!connectedA) return false;

            Log("Client B connecting to " + serverUrl);
            bool connectedB = await realtimeClientB.ConnectAsync(null, lifecycleCts.Token);
            Log("Client B connect result: " + connectedB);
            return connectedB;
        }

        //* هر دو کلاینت را با اکسس توکن دستی یا توکن ذخیره‌شده بعد از لاگین اینیت احراز می‌کند.
        private async Task<bool> AuthenticateBothAsync()
        {
            authWaiterA = CreateBoolWaiter();
            authWaiterB = CreateBoolWaiter();

            bool sentA = await SendAuthForClientAsync(realtimeAuthClientA, "A");
            if (!sentA) return false;

            bool sentB = await SendAuthForClientAsync(realtimeAuthClientB, "B");
            if (!sentB) return false;

            bool authA = await WaitWithTimeoutAsync(authWaiterA, "client A auth_ok", waitTimeoutMs, lifecycleCts.Token);
            bool authB = await WaitWithTimeoutAsync(authWaiterB, "client B auth_ok", waitTimeoutMs, lifecycleCts.Token);
            return authA && authB;
        }

        //* پیام اَث یک کلاینت را با توکن دستی یا توکن ذخیره‌شده ارسال می‌کند.
        private async Task<bool> SendAuthForClientAsync(RealtimeAuthClient authClient, string clientLabel)
        {
            if (!string.IsNullOrWhiteSpace(accessTokenOverride)) return await authClient.AuthenticateWithAccessTokenAsync(accessTokenOverride.Trim(), lifecycleCts.Token);
            if (useStoredTokenWhenOverrideIsEmpty) return await authClient.AuthenticateWithStoredTokenAsync(lifecycleCts.Token);

            Log("Client " + clientLabel + " auth token is empty.");
            return false;
        }

        //* کلاینت A را وارد روم مشترک می‌کند و اَک آن را کنترل می‌کند.
        private async Task<bool> JoinRoomAAndWaitAckAsync()
        {
            PrepareAckWaiterA("join_room_");
            bool sent = await gameServerClientA.JoinRoomAsync(activeRoomId, lifecycleCts.Token);
            if (!sent) return false;
            return await WaitWithTimeoutAsync(ackWaiterA, "client A join_room ack", waitTimeoutMs, lifecycleCts.Token);
        }

        //* کلاینت B را وارد روم می‌کند و بررسی می‌کند Client A پیام player_joined را بگیرد.
        private async Task<bool> JoinRoomBAndVerifyPlayerJoinedAsync()
        {
            PrepareAckWaiterB("join_room_");
            clientAPlayerJoinedWaiter = CreateBoolWaiter();

            bool sent = await gameServerClientB.JoinRoomAsync(activeRoomId, lifecycleCts.Token);
            if (!sent) return false;

            bool ackOk = await WaitWithTimeoutAsync(ackWaiterB, "client B join_room ack", waitTimeoutMs, lifecycleCts.Token);
            if (!ackOk) return false;
            if (!requirePlayerJoinedBroadcast) return true;

            return await WaitWithTimeoutAsync(clientAPlayerJoinedWaiter, "client A player_joined broadcast", waitTimeoutMs, lifecycleCts.Token);
        }

        //* کلاینت B را از روم خارج می‌کند و بررسی می‌کند Client A پیام player_left را بگیرد.
        private async Task<bool> LeaveRoomBAndVerifyPlayerLeftAsync()
        {
            PrepareAckWaiterB("leave_room_");
            clientAPlayerLeftWaiter = CreateBoolWaiter();

            bool sent = await gameServerClientB.LeaveRoomAsync(activeRoomId, lifecycleCts.Token);
            if (!sent) return false;

            bool ackOk = await WaitWithTimeoutAsync(ackWaiterB, "client B leave_room ack", waitTimeoutMs, lifecycleCts.Token);
            if (!ackOk) return false;
            if (!requirePlayerLeftBroadcast) return true;

            return await WaitWithTimeoutAsync(clientAPlayerLeftWaiter, "client A player_left broadcast", waitTimeoutMs, lifecycleCts.Token);
        }

        //* کلاینت A را از روم مشترک خارج می‌کند و اَک آن را کنترل می‌کند.
        private async Task<bool> LeaveRoomAAndWaitAckAsync()
        {
            PrepareAckWaiterA("leave_room_");
            bool sent = await gameServerClientA.LeaveRoomAsync(activeRoomId, lifecycleCts.Token);
            if (!sent) return false;
            return await WaitWithTimeoutAsync(ackWaiterA, "client A leave_room ack", waitTimeoutMs, lifecycleCts.Token);
        }

        //* هر دو اتصال فعال را با دلیل مشخص از سمت کُر ریل‌تایم می‌بندد.
        private async Task DisconnectBothAsync(string reason)
        {
            if (realtimeClientB != null) await realtimeClientB.DisconnectAsync(reason, lifecycleCts.Token);
            if (realtimeClientA != null) await realtimeClientA.DisconnectAsync(reason, lifecycleCts.Token);
        }

        #endregion

        #region <Client Setup>

        //* هر دو کلاینت تست را با کانفیگ یکسان می‌سازد و رویدادها را وصل می‌کند.
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

        //* کانفیگ مشترک تست را برای هر کلاینت می‌سازد.
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

        //* رویدادهای دو کُر، دو اَث و دو گیم‌سرورکلاینت را به کنترلر تست وصل می‌کند.
        private void BindEvents()
        {
            if (eventsBound) return;
            eventsBound = true;

            realtimeClientA.StateChanged += HandleStateChangedA;
            realtimeClientB.StateChanged += HandleStateChangedB;
            realtimeClientA.EnvelopeReceived += HandleEnvelopeReceivedA;
            realtimeClientB.EnvelopeReceived += HandleEnvelopeReceivedB;
            realtimeClientA.TransportErrorReceived += HandleTransportErrorA;
            realtimeClientB.TransportErrorReceived += HandleTransportErrorB;
            realtimeClientA.Disconnected += HandleDisconnectedA;
            realtimeClientB.Disconnected += HandleDisconnectedB;

            realtimeAuthClientA.Authenticated += HandleAuthenticatedA;
            realtimeAuthClientB.Authenticated += HandleAuthenticatedB;
            realtimeAuthClientA.AuthenticationFailed += HandleAuthenticationFailedA;
            realtimeAuthClientB.AuthenticationFailed += HandleAuthenticationFailedB;
            realtimeAuthClientA.AuthLogReceived += LogClientA;
            realtimeAuthClientB.AuthLogReceived += LogClientB;

            gameServerClientA.Events.LogReceived += LogClientA;
            gameServerClientB.Events.LogReceived += LogClientB;
            gameServerClientA.Events.AckReceived += HandleAckReceivedA;
            gameServerClientB.Events.AckReceived += HandleAckReceivedB;
            gameServerClientA.Events.ErrorReceived += HandleGameErrorA;
            gameServerClientB.Events.ErrorReceived += HandleGameErrorB;
        }

        //* همه رویدادهای دو کلاینت را جدا می‌کند تا تست بعدی دوباره subscribe نشود.
        private void UnbindEvents()
        {
            if (!eventsBound) return;
            eventsBound = false;

            if (realtimeClientA != null)
            {
                realtimeClientA.StateChanged -= HandleStateChangedA;
                realtimeClientA.EnvelopeReceived -= HandleEnvelopeReceivedA;
                realtimeClientA.TransportErrorReceived -= HandleTransportErrorA;
                realtimeClientA.Disconnected -= HandleDisconnectedA;
            }

            if (realtimeClientB != null)
            {
                realtimeClientB.StateChanged -= HandleStateChangedB;
                realtimeClientB.EnvelopeReceived -= HandleEnvelopeReceivedB;
                realtimeClientB.TransportErrorReceived -= HandleTransportErrorB;
                realtimeClientB.Disconnected -= HandleDisconnectedB;
            }

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
                gameServerClientA.Events.AckReceived -= HandleAckReceivedA;
                gameServerClientA.Events.ErrorReceived -= HandleGameErrorA;
            }

            if (gameServerClientB != null)
            {
                gameServerClientB.Events.LogReceived -= LogClientB;
                gameServerClientB.Events.AckReceived -= HandleAckReceivedB;
                gameServerClientB.Events.ErrorReceived -= HandleGameErrorB;
            }
        }

        #endregion

        #region <Event Handlers>

        //* تغییر وضعیت کلاینت A را در لاگ تست نشان می‌دهد.
        private void HandleStateChangedA(RealtimeConnectionState state)
        {
            LogClientA("State: " + state);
        }

        //* تغییر وضعیت کلاینت B را در لاگ تست نشان می‌دهد.
        private void HandleStateChangedB(RealtimeConnectionState state)
        {
            LogClientB("State: " + state);
        }

        //* اِنولوپ‌های دریافتی کلاینت A را بررسی می‌کند تا ورود و خروج پرزنس Client B تایید شود.
        private void HandleEnvelopeReceivedA(RealtimeEnvelope envelope)
        {
            if (envelope == null) return;
            if (envelope.ch != RealtimeChannels.Presence) return;
            if (!string.Equals(envelope.room, activeRoomId, StringComparison.OrdinalIgnoreCase)) return;

            if (envelope.t == RealtimeMessageTypes.PlayerJoined && IsEnvelopeFromClientB(envelope))
            {
                LogClientA("Player joined broadcast received from server. room=" + envelope.room);
                TrySetWaiter(clientAPlayerJoinedWaiter, true);
                return;
            }

            if (envelope.t == RealtimeMessageTypes.PlayerLeft && IsEnvelopeFromClientB(envelope))
            {
                LogClientA("Player left broadcast received from server. room=" + envelope.room);
                TrySetWaiter(clientAPlayerLeftWaiter, true);
            }
        }

        //* اِنولوپ‌های دریافتی کلاینت B را فعلاً فقط برای دیباگ نگه می‌دارد.
        private void HandleEnvelopeReceivedB(RealtimeEnvelope envelope)
        {
            if (envelope == null) return;
        }

        //* خطای ترنسپورت کلاینت A را در لاگ تست نشان می‌دهد.
        private void HandleTransportErrorA(string error)
        {
            LogClientA("Transport error: " + error);
        }

        //* خطای ترنسپورت کلاینت B را در لاگ تست نشان می‌دهد.
        private void HandleTransportErrorB(string error)
        {
            LogClientB("Transport error: " + error);
        }

        //* قطع اتصال کلاینت A را در لاگ تست نشان می‌دهد.
        private void HandleDisconnectedA(string reason)
        {
            LogClientA("Disconnected: " + reason);
        }

        //* قطع اتصال کلاینت B را در لاگ تست نشان می‌دهد.
        private void HandleDisconnectedB(string reason)
        {
            LogClientB("Disconnected: " + reason);
        }

        //* موفقیت اَث کلاینت A را به انتظار تست وصل می‌کند.
        private void HandleAuthenticatedA(string connectionId, string userId)
        {
            LogClientA("Authenticated: " + connectionId + " | " + userId);
            TrySetWaiter(authWaiterA, true);
        }

        //* موفقیت اَث کلاینت B را ذخیره می‌کند تا پرزنس‌های مربوط به خودش تشخیص داده شوند.
        private void HandleAuthenticatedB(string connectionId, string userId)
        {
            clientBConnectionId = connectionId ?? string.Empty;
            LogClientB("Authenticated: " + connectionId + " | " + userId);
            TrySetWaiter(authWaiterB, true);
        }

        //* شکست اَث کلاینت A را به انتظار تست وصل می‌کند.
        private void HandleAuthenticationFailedA(RealtimeError error)
        {
            LogClientA("Auth failed: " + FormatError(error));
            TrySetWaiter(authWaiterA, false);
        }

        //* شکست اَث کلاینت B را به انتظار تست وصل می‌کند.
        private void HandleAuthenticationFailedB(RealtimeError error)
        {
            LogClientB("Auth failed: " + FormatError(error));
            TrySetWaiter(authWaiterB, false);
        }

        //* اَک‌های کلاینت A را بر اساس پیشوند پیام در حال انتظار کنترل می‌کند.
        private void HandleAckReceivedA(GameServerAckResult ack)
        {
            if (ack == null) return;
            if (string.IsNullOrWhiteSpace(waitingAckPrefixA)) return;
            if (!ack.originalMessageId.StartsWith(waitingAckPrefixA, StringComparison.OrdinalIgnoreCase)) return;
            TrySetWaiter(ackWaiterA, ack.IsProcessed());
        }

        //* اَک‌های کلاینت B را بر اساس پیشوند پیام در حال انتظار کنترل می‌کند.
        private void HandleAckReceivedB(GameServerAckResult ack)
        {
            if (ack == null) return;
            if (string.IsNullOrWhiteSpace(waitingAckPrefixB)) return;
            if (!ack.originalMessageId.StartsWith(waitingAckPrefixB, StringComparison.OrdinalIgnoreCase)) return;
            TrySetWaiter(ackWaiterB, ack.IsProcessed());
        }

        //* خطاهای سطح گیم‌سرور کلاینت A را در لاگ تست نشان می‌دهد.
        private void HandleGameErrorA(RealtimeError error)
        {
            LogClientA("Game error: " + FormatError(error));
        }

        //* خطاهای سطح گیم‌سرور کلاینت B را در لاگ تست نشان می‌دهد.
        private void HandleGameErrorB(RealtimeError error)
        {
            LogClientB("Game error: " + FormatError(error));
        }

        #endregion

        #region <Wait Helpers>

        //* انتظار اَک بعدی کلاینت A را برای یک پیشوند مشخص آماده می‌کند.
        private void PrepareAckWaiterA(string messageIdPrefix)
        {
            waitingAckPrefixA = messageIdPrefix ?? string.Empty;
            ackWaiterA = CreateBoolWaiter();
        }

        //* انتظار اَک بعدی کلاینت B را برای یک پیشوند مشخص آماده می‌کند.
        private void PrepareAckWaiterB(string messageIdPrefix)
        {
            waitingAckPrefixB = messageIdPrefix ?? string.Empty;
            ackWaiterB = CreateBoolWaiter();
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
            authWaiterA = null;
            authWaiterB = null;
            ackWaiterA = null;
            ackWaiterB = null;
            clientAPlayerJoinedWaiter = null;
            clientAPlayerLeftWaiter = null;
            waitingAckPrefixA = string.Empty;
            waitingAckPrefixB = string.Empty;
        }

        #endregion

        #region <Cleanup>

        //* همه کلاینت‌های ساخته‌شده برای تست پرزنس را آزاد می‌کند.
        private async Task CleanupAsync()
        {
            UnbindEvents();

            if (gameServerClientA != null) gameServerClientA.Dispose();
            if (gameServerClientB != null) gameServerClientB.Dispose();
            if (realtimeAuthClientA != null) realtimeAuthClientA.Dispose();
            if (realtimeAuthClientB != null) realtimeAuthClientB.Dispose();
            if (realtimeClientB != null) await realtimeClientB.DisconnectAsync("T2.5 cleanup");
            if (realtimeClientA != null) await realtimeClientA.DisconnectAsync("T2.5 cleanup");
            if (realtimeClientA != null) realtimeClientA.Dispose();
            if (realtimeClientB != null) realtimeClientB.Dispose();

            gameServerClientA = null;
            gameServerClientB = null;
            realtimeAuthClientA = null;
            realtimeAuthClientB = null;
            realtimeClientA = null;
            realtimeClientB = null;
        }

        #endregion

        #region <Format Helpers>

        //* برای هر اجرای تست یک روم مشترک یکتا می‌سازد تا تست‌ها با هم قاطی نشوند.
        private string BuildRunRoomId()
        {
            string prefix = string.IsNullOrWhiteSpace(roomIdPrefix) ? "unity_t25_room" : roomIdPrefix.Trim();
            return prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        //* بررسی می‌کند پیام پرزنس دریافت‌شده مربوط به کانکشن کلاینت B باشد.
        private bool IsEnvelopeFromClientB(RealtimeEnvelope envelope)
        {
            if (string.IsNullOrWhiteSpace(clientBConnectionId)) return true;
            string incomingConnectionId = ReadString(envelope.payloadJson, "connectionId", string.Empty);
            return string.Equals(incomingConnectionId, clientBConnectionId, StringComparison.OrdinalIgnoreCase);
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

        //* خطای ریل‌تایم را به متن کوتاه قابل لاگ تبدیل می‌کند.
        private static string FormatError(RealtimeError error)
        {
            return error == null ? "unknown" : error.code + " | " + error.message;
        }

        //* شکست تست را لاگ می‌کند و false برمی‌گرداند.
        private bool Fail(string message)
        {
            Log("T2.5 failed: " + message);
            return false;
        }

        //* لاگ یکدست تست را در کنسول یونیتی چاپ می‌کند.
        private void Log(string message)
        {
            Debug.Log("[RealtimeT2.5] " + message);
        }

        //* لاگ کلاینت A را با پیشوند مشخص در کنسول یونیتی چاپ می‌کند.
        private void LogClientA(string message)
        {
            Log("[A] " + message);
        }

        //* لاگ کلاینت B را با پیشوند مشخص در کنسول یونیتی چاپ می‌کند.
        private void LogClientB(string message)
        {
            Log("[B] " + message);
        }

        #endregion
    }
}

//* این فایل تست دوکلاینتی پرزنس را برای یونیتی اجرا می‌کند.
//* هدف این تست اثبات دریافت player_joined و player_left توسط کلاینت دیگر روم است.
