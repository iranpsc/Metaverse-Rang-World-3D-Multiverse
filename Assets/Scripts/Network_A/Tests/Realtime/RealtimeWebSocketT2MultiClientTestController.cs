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
    //* کنترلر تست تی‌تو است و دو کلاینت وب‌سوکت را داخل یک روم برای بررسی برادکست تست می‌کند.
    public class RealtimeWebSocketT2MultiClientTestController : MonoBehaviour
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
        [SerializeField] private string roomIdPrefix = "unity_t2_room";
        [SerializeField] private string playerActionType = "unity_t2_action";
        [SerializeField] private bool requireBroadcastSentGreaterThanZero = true;
        [SerializeField] private bool waitForClientBActionEnvelope = true;

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
        private TaskCompletionSource<bool> clientBActionEnvelopeWaiter;

        private string waitingAckPrefixA = string.Empty;
        private string waitingAckPrefixB = string.Empty;
        private string activeRoomId = string.Empty;
        private int lastPlayerActionSentCount = -1;
        private bool isRunning;
        private bool eventsBound;

        #region <Unity Lifecycle>

        //* وابستگی‌های تست دوکلاینتی را می‌سازد تا هر کلاینت اتصال مستقل خودش را داشته باشد.
        private void Awake()
        {
            lifecycleCts = new CancellationTokenSource();
            CreateClients();
        }

        //* اگر از اینسپکتور فعال باشد، تست دوکلاینتی را بعد از شروع صحنه اجرا می‌کند.
        private async void Start()
        {
            if (!runOnStart) return;
            await RunMultiClientWebSocketT2TestAsync();
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

        //* از اینسپکتور یا دکمه یوآی برای اجرای تست کامل دوکلاینتی صدا زده می‌شود.
        public async void RunMultiClientWebSocketT2TestButton()
        {
            await RunMultiClientWebSocketT2TestAsync();
        }

        //* از اینسپکتور یا دکمه یوآی برای قطع هر دو کلاینت تست صدا زده می‌شود.
        public async void DisconnectBothButton()
        {
            await DisconnectBothAsync("Manual T2 disconnect");
        }

        #endregion

        #region <Main Test Flow>

        //* مسیر کامل دو کلاینت، اَث، جوین مشترک، برادکست اکشن و خروج از روم را تست می‌کند.
        public async Task<bool> RunMultiClientWebSocketT2TestAsync()
        {
            if (isRunning)
            {
                Log("T2 test is already running.");
                return false;
            }

            isRunning = true;
            activeRoomId = BuildRunRoomId();
            lastPlayerActionSentCount = -1;
            Log("T2 multi client test started. room=" + activeRoomId);

            try
            {
                bool connected = await ConnectBothAsync();
                if (!connected) return Fail("Connect both clients failed.");

                bool authenticated = await AuthenticateBothAsync();
                if (!authenticated) return Fail("Auth both clients failed.");

                bool joinedA = await JoinRoomAAndWaitAckAsync();
                if (!joinedA) return Fail("Client A join room failed.");

                bool joinedB = await JoinRoomBAndWaitAckAsync();
                if (!joinedB) return Fail("Client B join room failed.");

                bool actionBroadcasted = await SendPlayerActionFromAAndVerifyBroadcastAsync();
                if (!actionBroadcasted) return Fail("Player action broadcast failed.");

                bool leftB = await LeaveRoomBAndWaitAckAsync();
                if (!leftB) return Fail("Client B leave room failed.");

                bool leftA = await LeaveRoomAAndWaitAckAsync();
                if (!leftA) return Fail("Client A leave room failed.");

                if (autoDisconnectAtEnd) await DisconnectBothAsync("T2 completed");

                Log("T2 multi client test completed successfully. sent=" + lastPlayerActionSentCount);
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("T2 test canceled.");
            }
            catch (Exception ex)
            {
                return Fail("T2 test exception: " + ex.Message);
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

        //* کلاینت B را وارد همان روم مشترک می‌کند و اَک آن را کنترل می‌کند.
        private async Task<bool> JoinRoomBAndWaitAckAsync()
        {
            PrepareAckWaiterB("join_room_");
            bool sent = await gameServerClientB.JoinRoomAsync(activeRoomId, lifecycleCts.Token);
            if (!sent) return false;
            return await WaitWithTimeoutAsync(ackWaiterB, "client B join_room ack", waitTimeoutMs, lifecycleCts.Token);
        }

        //* کلاینت A اکشن می‌فرستد و بررسی می‌کند کلاینت B پیام برادکست‌شده را دریافت کند.
        private async Task<bool> SendPlayerActionFromAAndVerifyBroadcastAsync()
        {
            PrepareAckWaiterA("player_action_");
            clientBActionEnvelopeWaiter = CreateBoolWaiter();

            string payloadJson = "{\"source\":\"unity_t2_client_a\",\"roomId\":\"" + EscapeJson(activeRoomId) + "\",\"time\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}";
            bool sent = await gameServerClientA.SendPlayerActionAsync(playerActionType, payloadJson, lifecycleCts.Token);
            if (!sent) return false;

            bool ackOk = await WaitWithTimeoutAsync(ackWaiterA, "client A player_action ack", waitTimeoutMs, lifecycleCts.Token);
            if (!ackOk) return false;

            if (requireBroadcastSentGreaterThanZero && lastPlayerActionSentCount <= 0)
            {
                Log("Broadcast sent count is not greater than zero. sent=" + lastPlayerActionSentCount);
                return false;
            }

            if (!waitForClientBActionEnvelope) return true;
            bool receivedByB = await WaitWithTimeoutAsync(clientBActionEnvelopeWaiter, "client B player_action envelope", waitTimeoutMs, lifecycleCts.Token);
            return receivedByB;
        }

        //* کلاینت B را از روم مشترک خارج می‌کند و اَک آن را کنترل می‌کند.
        private async Task<bool> LeaveRoomBAndWaitAckAsync()
        {
            PrepareAckWaiterB("leave_room_");
            bool sent = await gameServerClientB.LeaveRoomAsync(activeRoomId, lifecycleCts.Token);
            if (!sent) return false;
            return await WaitWithTimeoutAsync(ackWaiterB, "client B leave_room ack", waitTimeoutMs, lifecycleCts.Token);
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

        //* اِنولوپ‌های دریافتی کلاینت A را برای دیباگ مسیر تست بررسی می‌کند.
        private void HandleEnvelopeReceivedA(RealtimeEnvelope envelope)
        {
            if (envelope == null) return;
        }

        //* اِنولوپ‌های دریافتی کلاینت B را بررسی می‌کند تا برادکست اکشن کلاینت A تایید شود.
        private void HandleEnvelopeReceivedB(RealtimeEnvelope envelope)
        {
            if (envelope == null) return;
            if (envelope.ch != RealtimeChannels.Game || envelope.t != RealtimeMessageTypes.PlayerAction) return;
            LogClientB("Player action broadcast received from server. room=" + envelope.room);
            TrySetWaiter(clientBActionEnvelopeWaiter, true);
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

        //* موفقیت اَث کلاینت B را به انتظار تست وصل می‌کند.
        private void HandleAuthenticatedB(string connectionId, string userId)
        {
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

        //* اَک‌های کلاینت A را کنترل می‌کند و sent مربوط به player_action را از جزئیات اَک می‌خواند.
        private void HandleAckReceivedA(GameServerAckResult ack)
        {
            if (ack == null) return;

            if (ack.originalMessageId.StartsWith("player_action_", StringComparison.OrdinalIgnoreCase))
            {
                lastPlayerActionSentCount = ReadInt(ack.detailsJson, "sent", -1);
                LogClientA("Player action ack sent count: " + lastPlayerActionSentCount);
            }

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
            clientBActionEnvelopeWaiter = null;
            waitingAckPrefixA = string.Empty;
            waitingAckPrefixB = string.Empty;
        }

        #endregion

        #region <Cleanup>

        //* همه کلاینت‌های ساخته‌شده برای تست دوکلاینتی را آزاد می‌کند.
        private async Task CleanupAsync()
        {
            UnbindEvents();

            if (gameServerClientA != null) gameServerClientA.Dispose();
            if (gameServerClientB != null) gameServerClientB.Dispose();
            if (realtimeAuthClientA != null) realtimeAuthClientA.Dispose();
            if (realtimeAuthClientB != null) realtimeAuthClientB.Dispose();
            if (realtimeClientB != null) await realtimeClientB.DisconnectAsync("T2 cleanup");
            if (realtimeClientA != null) await realtimeClientA.DisconnectAsync("T2 cleanup");
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
            string prefix = string.IsNullOrWhiteSpace(roomIdPrefix) ? "unity_t2_room" : roomIdPrefix.Trim();
            return prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        //* یک عدد صحیح ساده را از آبجکت جیسون جزئیات اَک می‌خواند.
        private static int ReadInt(string json, string key, int fallback)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key)) return fallback;

            string pattern = "\"" + key + "\"";
            int keyIndex = json.IndexOf(pattern, StringComparison.Ordinal);
            if (keyIndex < 0) return fallback;

            int colonIndex = json.IndexOf(':', keyIndex + pattern.Length);
            if (colonIndex < 0) return fallback;

            int valueStart = colonIndex + 1;
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart])) valueStart++;

            int valueEnd = valueStart;
            while (valueEnd < json.Length && (char.IsDigit(json[valueEnd]) || json[valueEnd] == '-')) valueEnd++;
            if (valueEnd <= valueStart) return fallback;

            string numberText = json.Substring(valueStart, valueEnd - valueStart);
            return int.TryParse(numberText, out int value) ? value : fallback;
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
            Log("T2 failed: " + message);
            return false;
        }

        //* لاگ یکدست تست را در کنسول یونیتی چاپ می‌کند.
        private void Log(string message)
        {
            Debug.Log("[RealtimeT2] " + message);
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

//* این فایل تست دوکلاینتی وب‌سوکت را برای یونیتی اجرا می‌کند.
//* هدف این تست اثبات برادکست واقعی روم و sent بزرگ‌تر از صفر است.
