using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.GameIntegration.Movement;
using Network_A.GameServer;
using Network_A.Realtime.Auth;
using Network_A.Realtime.Core;
using Network_A.Realtime.Protocol;
using Network_A.Realtime.Stability;
using Network_A.Realtime.Transport;
using UnityEngine;

namespace Network_A.Tests.Realtime
{
    //* کنترلر تست جی‌وان است و ارسال حرکت پلیر A و دریافت آن توسط پلیر B را تست می‌کند.
    public class RealtimeWebSocketG1MovementIntegrationTestController : MonoBehaviour
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
        [SerializeField] private string roomIdPrefix = "unity_g1_room";
        [SerializeField] private string clientAPlayerId = "unity_g1_client_a";
        [SerializeField] private string clientBPlayerId = "unity_g1_client_b";

        [Header("Movement")]
        [SerializeField] private Vector3 firstPosition = new Vector3(1f, 0f, 1f);
        [SerializeField] private Vector3 secondPosition = new Vector3(2f, 0f, 1.5f);
        [SerializeField] private float positionTolerance = 0.2f;

        [Header("Timeout")]
        [SerializeField] private int waitTimeoutMs = 10000;
        [SerializeField] private int reliableAckTimeoutMs = 3000;

        private RealtimeClient realtimeClientA;
        private RealtimeClient realtimeClientB;
        private RealtimeAuthClient realtimeAuthClientA;
        private RealtimeAuthClient realtimeAuthClientB;
        private GameServerClient gameServerClientA;
        private GameServerClient gameServerClientB;
        private RealtimeRemotePlayerMovementReceiver movementReceiverB;
        private RealtimePlayerMovementSender movementSenderA;
        private CancellationTokenSource lifecycleCts;
        private TaskCompletionSource<bool> authWaiterA;
        private TaskCompletionSource<bool> authWaiterB;
        private TaskCompletionSource<RealtimeMovementSnapshot> snapshotWaiterB;
        private GameObject localPlayerObjectA;
        private string activeRoomId = string.Empty;
        private bool isRunning;
        private bool eventsBound;

        #region <Unity Lifecycle>

        //* وابستگی‌های تست جی‌وان را می‌سازد تا ارسال حرکت از مسیر واقعی گیم‌سرور انجام شود.
        private void Awake()
        {
            lifecycleCts = new CancellationTokenSource();
            CreateClients();
            CreateMovementObjects();
        }

        //* اگر از اینسپکتور فعال باشد، تست جی‌وان را بعد از شروع صحنه اجرا می‌کند.
        private async void Start()
        {
            if (!runOnStart) return;
            await RunG1MovementIntegrationTestAsync();
        }

        //* هنگام حذف آبجکت تست، اتصال و رویدادها را پاکسازی می‌کند.
        private async void OnDestroy()
        {
            await CleanupAsync();
            lifecycleCts?.Cancel();
            lifecycleCts?.Dispose();
            lifecycleCts = null;
        }

        #endregion

        #region <Inspector Buttons>

        //* اجرای دستی تست جی‌وان از اینسپکتور یا یوآی.
        public async void RunG1MovementIntegrationTestButton()
        {
            await RunG1MovementIntegrationTestAsync();
        }

        //* قطع دستی هر دو کلاینت تست.
        public async void DisconnectBothButton()
        {
            await DisconnectBothAsync("Manual G1 disconnect");
        }

        #endregion

        #region <Main Flow>

        //* مسیر کامل اتصال دو کلاینت، جوین مشترک، ارسال حرکت و دریافت حرکت را تست می‌کند.
        public async Task<bool> RunG1MovementIntegrationTestAsync()
        {
            if (isRunning)
            {
                Log("G1 movement integration test is already running.");
                return false;
            }

            isRunning = true;
            activeRoomId = BuildRunRoomId();
            Log("G1 movement integration test started. room=" + activeRoomId);

            try
            {
                bool connected = await ConnectBothAsync();
                if (!connected) return Fail("Connect both clients failed.");

                bool authenticated = await AuthenticateBothAsync();
                if (!authenticated) return Fail("Auth both clients failed.");

                bool joined = await JoinBothReliableAsync();
                if (!joined) return Fail("Join both clients failed.");

                movementReceiverB.Initialize(gameServerClientB, clientBPlayerId);
                movementSenderA.Initialize(gameServerClientA, clientAPlayerId);

                bool firstOk = await SendAndValidateSnapshotAsync(firstPosition, Quaternion.Euler(0f, 45f, 0f), "first movement snapshot");
                if (!firstOk) return false;

                bool secondOk = await SendAndValidateSnapshotAsync(secondPosition, Quaternion.Euler(0f, 90f, 0f), "second movement snapshot");
                if (!secondOk) return false;

                if (autoDisconnectAtEnd) await DisconnectBothAsync("G1 completed");

                Log("G1 movement integration test completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("G1 movement integration test canceled.");
            }
            catch (Exception ex)
            {
                return Fail("G1 movement integration test exception: " + ex.Message);
            }
            finally
            {
                snapshotWaiterB = null;
                isRunning = false;
            }
        }

        //* یک position جدید را از پلیر A ارسال می‌کند و دریافت آن را در کلاینت B بررسی می‌کند.
        private async Task<bool> SendAndValidateSnapshotAsync(Vector3 position, Quaternion rotation, string label)
        {
            snapshotWaiterB = new TaskCompletionSource<RealtimeMovementSnapshot>();
            localPlayerObjectA.transform.position = position;
            localPlayerObjectA.transform.rotation = rotation;

            bool sent = await movementSenderA.ForceSendSnapshotAsync(lifecycleCts.Token);
            if (!sent) return Fail(label + " was not sent.");

            RealtimeMovementSnapshot snapshot = await WaitSnapshotWithTimeoutAsync(snapshotWaiterB, label, waitTimeoutMs, lifecycleCts.Token);
            snapshotWaiterB = null;
            if (snapshot == null) return Fail(label + " was not received by client B.");
            if (!string.Equals(snapshot.playerId, clientAPlayerId, StringComparison.OrdinalIgnoreCase)) return Fail(label + " playerId mismatch: " + snapshot.playerId);
            if ((snapshot.position - position).sqrMagnitude > positionTolerance * positionTolerance) return Fail(label + " position mismatch. received=" + snapshot.position + " expected=" + position);

            Log(label + " received by client B. playerId=" + snapshot.playerId + " | seq=" + snapshot.sequence + " | pos=" + snapshot.position);
            return true;
        }

        #endregion

        #region <Connection Flow>

        //* هر دو کلاینت را به سرور ریل‌تایم وصل می‌کند.
        private async Task<bool> ConnectBothAsync()
        {
            bool connectedA = await realtimeClientA.ConnectAsync(null, lifecycleCts.Token);
            Log("Client A connect result: " + connectedA);
            if (!connectedA) return false;

            bool connectedB = await realtimeClientB.ConnectAsync(null, lifecycleCts.Token);
            Log("Client B connect result: " + connectedB);
            return connectedB;
        }

        //* هر دو کلاینت را با توکن ذخیره‌شده یا override احراز می‌کند.
        private async Task<bool> AuthenticateBothAsync()
        {
            authWaiterA = new TaskCompletionSource<bool>();
            authWaiterB = new TaskCompletionSource<bool>();

            bool sentA = await SendAuthAsync(realtimeAuthClientA);
            bool sentB = await SendAuthAsync(realtimeAuthClientB);
            if (!sentA || !sentB) return false;

            bool authA = await WaitBoolWithTimeoutAsync(authWaiterA, "client A auth_ok", waitTimeoutMs, lifecycleCts.Token);
            bool authB = await WaitBoolWithTimeoutAsync(authWaiterB, "client B auth_ok", waitTimeoutMs, lifecycleCts.Token);
            return authA && authB;
        }

        //* پیام اَث یک کلاینت را ارسال می‌کند.
        private async Task<bool> SendAuthAsync(RealtimeAuthClient authClient)
        {
            if (!string.IsNullOrWhiteSpace(accessTokenOverride)) return await authClient.AuthenticateWithAccessTokenAsync(accessTokenOverride.Trim(), lifecycleCts.Token);
            if (useStoredTokenWhenOverrideIsEmpty) return await authClient.AuthenticateWithStoredTokenAsync(lifecycleCts.Token);
            return false;
        }

        //* هر دو کلاینت را با انتظار اَک داخلی وارد روم مشترک می‌کند.
        private async Task<bool> JoinBothReliableAsync()
        {
            RealtimeReliableSendOptions options = BuildReliableOptions();
            RealtimeReliableSendResult joinA = await gameServerClientA.JoinRoomReliableAsync(activeRoomId, options, lifecycleCts.Token);
            Log("Client A join result: " + FormatReliableResult(joinA));
            if (joinA == null || !joinA.isSuccess) return false;

            RealtimeReliableSendResult joinB = await gameServerClientB.JoinRoomReliableAsync(activeRoomId, options, lifecycleCts.Token);
            Log("Client B join result: " + FormatReliableResult(joinB));
            return joinB != null && joinB.isSuccess;
        }

        //* هر دو کلاینت را با دلیل داده‌شده قطع می‌کند.
        private async Task DisconnectBothAsync(string reason)
        {
            if (realtimeClientB != null && realtimeClientB.IsConnected) await realtimeClientB.DisconnectAsync(reason, CancellationToken.None);
            await Task.Delay(150);
            if (realtimeClientA != null && realtimeClientA.IsConnected) await realtimeClientA.DisconnectAsync(reason, CancellationToken.None);
            await Task.Delay(150);
        }

        #endregion

        #region <Setup>

        //* کلاینت‌های ریل‌تایم، اَث و گیم‌سرور را می‌سازد.
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

        //* آبجکت‌های ساده تست حرکت را می‌سازد.
        private void CreateMovementObjects()
        {
            localPlayerObjectA = new GameObject("G1_LocalPlayer_A");
            localPlayerObjectA.transform.SetParent(transform);
            movementSenderA = localPlayerObjectA.AddComponent<RealtimePlayerMovementSender>();
            movementReceiverB = gameObject.AddComponent<RealtimeRemotePlayerMovementReceiver>();
            movementReceiverB.SnapshotReceived += HandleSnapshotReceivedB;
        }

        //* کانفیگ مشترک هر کلاینت را می‌سازد.
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

        //* تنظیمات اَک قابل اطمینان را برای جوین می‌سازد.
        private RealtimeReliableSendOptions BuildReliableOptions()
        {
            return new RealtimeReliableSendOptions
            {
                ackTimeoutMs = reliableAckTimeoutMs,
                maxSendAttempts = 2,
                retryDelayMs = 250,
                retryOnAckTimeout = true,
                retryOnTransportSendFailed = true
            };
        }

        #endregion

        #region <Event Binding>

        //* رویدادهای لازم تست را subscribe می‌کند.
        private void BindEvents()
        {
            if (eventsBound) return;
            eventsBound = true;

            realtimeAuthClientA.Authenticated += HandleAuthenticatedA;
            realtimeAuthClientB.Authenticated += HandleAuthenticatedB;
            realtimeAuthClientA.AuthenticationFailed += HandleAuthenticationFailedA;
            realtimeAuthClientB.AuthenticationFailed += HandleAuthenticationFailedB;
            realtimeAuthClientA.AuthLogReceived += LogClientA;
            realtimeAuthClientB.AuthLogReceived += LogClientB;
            gameServerClientA.Events.LogReceived += LogClientA;
            gameServerClientB.Events.LogReceived += LogClientB;
            gameServerClientA.Events.ErrorReceived += HandleGameErrorA;
            gameServerClientB.Events.ErrorReceived += HandleGameErrorB;
            realtimeClientA.TransportErrorReceived += HandleTransportErrorA;
            realtimeClientB.TransportErrorReceived += HandleTransportErrorB;
        }

        //* رویدادهای تست را جدا می‌کند تا subscribe تکراری نماند.
        private void UnbindEvents()
        {
            if (!eventsBound) return;
            eventsBound = false;

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
                gameServerClientA.Events.ErrorReceived -= HandleGameErrorA;
            }

            if (gameServerClientB != null)
            {
                gameServerClientB.Events.LogReceived -= LogClientB;
                gameServerClientB.Events.ErrorReceived -= HandleGameErrorB;
            }

            if (realtimeClientA != null) realtimeClientA.TransportErrorReceived -= HandleTransportErrorA;
            if (realtimeClientB != null) realtimeClientB.TransportErrorReceived -= HandleTransportErrorB;
        }

        #endregion

        #region <Event Handlers>

        //* موفقیت اَث کلاینت A را به waiter وصل می‌کند.
        private void HandleAuthenticatedA(string connectionId, string userId)
        {
            LogClientA("Authenticated: " + connectionId + " | " + userId);
            TrySetBool(authWaiterA, true);
        }

        //* موفقیت اَث کلاینت B را به waiter وصل می‌کند.
        private void HandleAuthenticatedB(string connectionId, string userId)
        {
            LogClientB("Authenticated: " + connectionId + " | " + userId);
            TrySetBool(authWaiterB, true);
        }

        //* شکست اَث کلاینت A را به waiter وصل می‌کند.
        private void HandleAuthenticationFailedA(RealtimeError error)
        {
            LogClientA("Auth failed: " + FormatError(error));
            TrySetBool(authWaiterA, false);
        }

        //* شکست اَث کلاینت B را به waiter وصل می‌کند.
        private void HandleAuthenticationFailedB(RealtimeError error)
        {
            LogClientB("Auth failed: " + FormatError(error));
            TrySetBool(authWaiterB, false);
        }

        //* خطای گیم کلاینت A را لاگ می‌کند.
        private void HandleGameErrorA(RealtimeError error)
        {
            LogClientA("Game error: " + FormatError(error));
        }

        //* خطای گیم کلاینت B را لاگ می‌کند.
        private void HandleGameErrorB(RealtimeError error)
        {
            LogClientB("Game error: " + FormatError(error));
        }

        //* خطای ترنسپورت کلاینت A را لاگ می‌کند.
        private void HandleTransportErrorA(string error)
        {
            LogClientA("Transport error: " + error);
        }

        //* خطای ترنسپورت کلاینت B را لاگ می‌کند.
        private void HandleTransportErrorB(string error)
        {
            LogClientB("Transport error: " + error);
        }

        //* اسنپ‌شات دریافتی کلاینت B را به انتظار تست وصل می‌کند.
        private void HandleSnapshotReceivedB(RealtimeMovementSnapshot snapshot)
        {
            if (snapshot == null) return;
            if (!string.Equals(snapshot.playerId, clientAPlayerId, StringComparison.OrdinalIgnoreCase)) return;
            TrySetSnapshot(snapshotWaiterB, snapshot);
        }

        #endregion

        #region <Wait Helpers>

        //* منتظر نتیجه bool با تایم‌اوت می‌ماند.
        private async Task<bool> WaitBoolWithTimeoutAsync(TaskCompletionSource<bool> waiter, string label, int timeoutMs, CancellationToken cancellationToken)
        {
            Task completed = await Task.WhenAny(waiter.Task, Task.Delay(timeoutMs, cancellationToken));
            if (completed != waiter.Task)
            {
                Log("Timeout waiting for " + label);
                return false;
            }

            return waiter.Task.Result;
        }

        //* منتظر اسنپ‌شات حرکت با تایم‌اوت می‌ماند.
        private async Task<RealtimeMovementSnapshot> WaitSnapshotWithTimeoutAsync(TaskCompletionSource<RealtimeMovementSnapshot> waiter, string label, int timeoutMs, CancellationToken cancellationToken)
        {
            Task completed = await Task.WhenAny(waiter.Task, Task.Delay(timeoutMs, cancellationToken));
            if (completed != waiter.Task)
            {
                Log("Timeout waiting for " + label);
                return null;
            }

            return waiter.Task.Result;
        }

        //* مقدار bool را فقط اگر waiter هنوز کامل نشده باشد تنظیم می‌کند.
        private void TrySetBool(TaskCompletionSource<bool> waiter, bool value)
        {
            if (waiter == null || waiter.Task.IsCompleted) return;
            waiter.TrySetResult(value);
        }

        //* مقدار اسنپ‌شات را فقط اگر waiter هنوز کامل نشده باشد تنظیم می‌کند.
        private void TrySetSnapshot(TaskCompletionSource<RealtimeMovementSnapshot> waiter, RealtimeMovementSnapshot snapshot)
        {
            if (waiter == null || waiter.Task.IsCompleted) return;
            waiter.TrySetResult(snapshot);
        }

        #endregion

        #region <Cleanup>

        //* همه منابع تست را پاکسازی می‌کند.
        private async Task CleanupAsync()
        {
            if (movementReceiverB != null) movementReceiverB.SnapshotReceived -= HandleSnapshotReceivedB;
            UnbindEvents();
            await DisconnectBothAsync("G1 cleanup");
            gameServerClientA?.Dispose();
            gameServerClientB?.Dispose();
            realtimeAuthClientA?.Dispose();
            realtimeAuthClientB?.Dispose();
            realtimeClientA?.Dispose();
            realtimeClientB?.Dispose();
        }

        #endregion

        #region <Formatting>

        //* آیدی روم تست را با پیشوند و شناسه کوتاه می‌سازد.
        private string BuildRunRoomId()
        {
            return roomIdPrefix.Trim() + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        //* نتیجه reliable را برای لاگ کوتاه می‌کند.
        private string FormatReliableResult(RealtimeReliableSendResult result)
        {
            if (result == null) return "null";
            return "success=" + result.isSuccess + " | attempts=" + result.attempts + " | queued=" + result.wasQueued + " | timeout=" + result.ackTimedOut + " | error=" + result.errorMessage;
        }

        //* خطای ریل‌تایم را برای لاگ کوتاه می‌کند.
        private string FormatError(RealtimeError error)
        {
            if (error == null) return "unknown";
            return error.code + " | " + error.message;
        }

        //* لاگ عمومی تست را چاپ می‌کند.
        private void Log(string message)
        {
            Debug.Log("[RealtimeG1] " + message);
        }

        //* لاگ کلاینت A را چاپ می‌کند.
        private void LogClientA(string message)
        {
            Debug.Log("[RealtimeG1][A] " + message);
        }

        //* لاگ کلاینت B را چاپ می‌کند.
        private void LogClientB(string message)
        {
            Debug.Log("[RealtimeG1][B] " + message);
        }

        //* تست را با لاگ خطا false می‌کند.
        private bool Fail(string message)
        {
            Debug.LogError("[RealtimeG1] " + message);
            return false;
        }

        #endregion
    }
}
