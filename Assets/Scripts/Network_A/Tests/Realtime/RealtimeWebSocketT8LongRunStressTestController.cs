using System;
using System.Collections.Generic;
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
    //* کنترلر تست تی‌اِیت است و چند چرخه ریکانکت، صف، اَک و کلین‌آپ را پشت سر هم بررسی می‌کند.
    public class RealtimeWebSocketT8LongRunStressTestController : MonoBehaviour
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
        [SerializeField] private string roomIdPrefix = "unity_t8_room";
        [SerializeField] private string beforeDisconnectActionType = "unity_t8_before_disconnect";
        [SerializeField] private string queuedActionType = "unity_t8_queued_during_recovery";
        [SerializeField] private string afterRecoveryActionType = "unity_t8_after_recovery";
        [SerializeField] private string finalActionType = "unity_t8_final_action";

        [Header("Long Run")]
        [SerializeField] private int recoveryCycleCount = 3;
        [SerializeField] private int reliableActionsBeforeEachDrop = 1;
        [SerializeField] private int reliableActionsQueuedEachDrop = 1;
        [SerializeField] private int unreliableStateMessagesEachDrop = 2;

        [Header("Timeout")]
        [SerializeField] private int waitTimeoutMs = 10000;
        [SerializeField] private int recoveryTimeoutMs = 60000;
        [SerializeField] private int autoReconnectInitialDelayMs = 900;
        [SerializeField] private int autoReconnectMaxAttempts = 5;

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
        private readonly Dictionary<string, int> dict_AckCountByMessageId = new Dictionary<string, int>();
        private string waitingAckPrefix = string.Empty;
        private string activeRoomId = string.Empty;
        private int droppedByPolicyCount;
        private int queuedMessageDroppedCount;
        private int reliableAckTimeoutCount;
        private int duplicateAckCount;
        private int stateChangedCount;
        private int disconnectedEventCount;
        private int queueCountChangedCount;
        private int maxQueueCountSeen;
        private int maxPendingAckCountSeen;
        private bool isRunning;
        private bool eventsBound;

        #region <Unity Lifecycle>

        //* وابستگی‌های تست را می‌سازد تا تست فشار از همان مسیر واقعی ریل‌تایم استفاده کند.
        private void Awake()
        {
            lifecycleCts = new CancellationTokenSource();
            CreateClients();
        }

        //* اگر از اینسپکتور فعال باشد، تست تی‌اِیت را بعد از شروع صحنه اجرا می‌کند.
        private async void Start()
        {
            if (!runOnStart) return;
            await RunLongRunT8TestAsync();
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

        //* از اینسپکتور یا دکمه یوآی برای اجرای تست طولانی تی‌اِیت صدا زده می‌شود.
        public async void RunLongRunT8TestButton()
        {
            await RunLongRunT8TestAsync();
        }

        //* از اینسپکتور یا دکمه یوآی برای دیسکانکت دستی تست صدا زده می‌شود.
        public async void DisconnectButton()
        {
            await DisconnectAsync("Manual T8 disconnect");
        }

        #endregion

        #region <Main Test Flow>

        //* چند چرخه کامل قطع ناخواسته، ریکانکت خودکار، جوین دوباره و فلش صف را تست می‌کند.
        public async Task<bool> RunLongRunT8TestAsync()
        {
            if (isRunning)
            {
                Log("T8 long run test is already running.");
                return false;
            }

            isRunning = true;
            activeRoomId = BuildRunRoomId();
            ResetMetrics();
            Log("T8 long run stress test started. room=" + activeRoomId + " | cycles=" + GetSafeRecoveryCycleCount());

            try
            {
                bool firstReady = await ConnectAuthenticateJoinReliableAsync("initial connection");
                if (!firstReady) return Fail("Initial connection flow failed.");

                autoReconnectOrchestrator.Start();

                for (int cycle = 1; cycle <= GetSafeRecoveryCycleCount(); cycle++)
                {
                    bool cycleResult = await RunRecoveryCycleAsync(cycle);
                    if (!cycleResult) return false;
                }

                bool finalAction = await SendPlayerActionReliableAsync(finalActionType, "final_action");
                if (!finalAction) return Fail("Final reliable player_action failed.");

                autoReconnectOrchestrator.Stop();

                bool cleanState = ValidateCleanState("before leave");
                if (!cleanState) return false;

                bool left = await LeaveRoomAndWaitAckAsync();
                if (!left) return Fail("Leave room after T8 failed.");

                if (autoDisconnectAtEnd) await DisconnectAsync("T8 completed");

                bool summaryOk = ValidateFinalSummary();
                if (!summaryOk) return false;

                Log("T8 long run stress test completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("T8 long run test canceled.");
            }
            catch (Exception ex)
            {
                return Fail("T8 long run test exception: " + ex.Message);
            }
            finally
            {
                ClearWaiters();
                isRunning = false;
            }
        }

        //* یک چرخه کامل ارسال قبل از قطعی، قطع ناخواسته، صف، ریکانکت و بررسی پاکی وضعیت را اجرا می‌کند.
        private async Task<bool> RunRecoveryCycleAsync(int cycle)
        {
            Log("T8 cycle " + cycle + " started.");

            bool beforeBurst = await SendReliableBurstBeforeDisconnectAsync(cycle);
            if (!beforeBurst) return Fail("Cycle " + cycle + " reliable burst before disconnect failed.");

            recoveryWaiter = CreateBoolWaiter();
            await DisconnectAsync("T8 simulated unexpected network drop cycle " + cycle);

            bool stateDropped = await SendUnreliableStatesDuringDisconnectAsync(cycle);
            if (!stateDropped) return Fail("Cycle " + cycle + " unreliable player_state messages were not dropped as expected.");

            bool queued = await QueueReliableActionsDuringDisconnectAsync(cycle);
            if (!queued) return Fail("Cycle " + cycle + " reliable queued actions failed.");

            if (realtimeClient.QueuedMessageCount <= 0) return Fail("Cycle " + cycle + " queue is empty before recovery.");
            Log("T8 cycle " + cycle + " queue before recovery: " + realtimeClient.QueuedMessageCount);

            bool recovered = await WaitWithTimeoutAsync(recoveryWaiter, "auto reconnect recovery cycle " + cycle, recoveryTimeoutMs, lifecycleCts.Token);
            recoveryWaiter = null;
            if (!recovered) return Fail("Cycle " + cycle + " auto reconnect recovery failed.");

            bool cleanAfterRecovery = ValidateCleanState("after recovery cycle " + cycle);
            if (!cleanAfterRecovery) return false;

            bool afterRecovery = await SendPlayerActionReliableAsync(afterRecoveryActionType, "after_recovery_cycle_" + cycle);
            if (!afterRecovery) return Fail("Cycle " + cycle + " after recovery reliable action failed.");

            Log("T8 cycle " + cycle + " completed. queue=" + realtimeClient.QueuedMessageCount + " | pendingAck=" + realtimeClient.PendingAckCount);
            return true;
        }

        //* یک چرخه کامل کانکت، آث و جوین قابل اطمینان را برای شروع تست اجرا می‌کند.
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

        //* چند اکشن قابل اطمینان را قبل از هر قطعی می‌فرستد تا مسیر عادی قبل از ریکاوری هم بررسی شود.
        private async Task<bool> SendReliableBurstBeforeDisconnectAsync(int cycle)
        {
            int count = Mathf.Max(1, reliableActionsBeforeEachDrop);
            for (int i = 1; i <= count; i++)
            {
                bool sent = await SendPlayerActionReliableAsync(beforeDisconnectActionType, "before_disconnect_cycle_" + cycle + "_action_" + i);
                if (!sent) return false;
            }

            return true;
        }

        //* اکشن تستی پلیر را با مسیر قابل اطمینان می‌فرستد و نتیجه اَک داخلی کُر را بررسی می‌کند.
        private async Task<bool> SendPlayerActionReliableAsync(string actionType, string phase)
        {
            string payloadJson = BuildActionPayloadJson(phase);
            RealtimeReliableSendResult result = await gameServerClient.SendPlayerActionReliableAsync(actionType, payloadJson, BuildReliableOptions(), lifecycleCts.Token);
            UpdateDiagnosticPeaks();
            Log(phase + " reliable player_action result: " + FormatReliableResult(result));
            return result != null && result.isSuccess && !result.wasQueued;
        }

        //* هنگام قطعی چند پیام وضعیت لحظه‌ای را می‌فرستد تا طبق سیاست ارسال حذف شوند و داخل صف نروند.
        private async Task<bool> SendUnreliableStatesDuringDisconnectAsync(int cycle)
        {
            int count = Mathf.Max(1, unreliableStateMessagesEachDrop);
            int beforeQueue = realtimeClient.QueuedMessageCount;
            int beforeDrop = droppedByPolicyCount;

            for (int i = 1; i <= count; i++)
            {
                bool sent = await gameServerClient.SendPlayerStateAsync(new Vector3(cycle, i, cycle + i), Quaternion.identity, lifecycleCts.Token);
                Log("Cycle " + cycle + " disconnected player_state " + i + " send result: " + sent + " | queue=" + realtimeClient.QueuedMessageCount + " | policyDrops=" + droppedByPolicyCount);
            }

            bool droppedAll = droppedByPolicyCount >= beforeDrop + count;
            bool queueUnchanged = realtimeClient.QueuedMessageCount == beforeQueue;
            return droppedAll && queueUnchanged;
        }

        //* هنگام قطعی چند اکشن مهم را با مسیر قابل اطمینان می‌فرستد تا طبق سیاست داخل صف ذخیره شوند.
        private async Task<bool> QueueReliableActionsDuringDisconnectAsync(int cycle)
        {
            int count = Mathf.Max(1, reliableActionsQueuedEachDrop);

            for (int i = 1; i <= count; i++)
            {
                string phase = "queued_during_recovery_cycle_" + cycle + "_action_" + i;
                string payloadJson = BuildActionPayloadJson(phase);
                RealtimeReliableSendResult result = await gameServerClient.SendPlayerActionReliableAsync(queuedActionType, payloadJson, BuildReliableOptions(), lifecycleCts.Token);
                UpdateDiagnosticPeaks();
                Log("Cycle " + cycle + " disconnected reliable player_action queued result: " + FormatReliableResult(result));
                if (result == null || !result.isSuccess || !result.wasQueued) return false;
            }

            return true;
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

        #region <Validation>

        //* بعد از هر ریکاوری بررسی می‌کند صف، اَک و وضعیت روم تمیز باشند.
        private bool ValidateCleanState(string label)
        {
            UpdateDiagnosticPeaks();

            if (!realtimeClient.IsConnected) return Fail(label + " failed: realtime client is disconnected.");
            if (!realtimeAuthClient.IsAuthenticated) return Fail(label + " failed: auth client is not authenticated.");
            if (!gameServerClient.HasRoom) return Fail(label + " failed: game server client has no active room.");
            if (realtimeClient.QueuedMessageCount != 0) return Fail(label + " failed: queue leak detected. queue=" + realtimeClient.QueuedMessageCount);
            if (realtimeClient.PendingAckCount != 0) return Fail(label + " failed: pending ack leak detected. pendingAck=" + realtimeClient.PendingAckCount);
            if (duplicateAckCount > 0) return Fail(label + " failed: duplicate ack detected. count=" + duplicateAckCount);
            if (queuedMessageDroppedCount > 0) return Fail(label + " failed: queued message dropped. count=" + queuedMessageDroppedCount);
            if (reliableAckTimeoutCount > 0) return Fail(label + " failed: reliable ack timeout. count=" + reliableAckTimeoutCount);

            Log(label + " clean state ok. queue=0 | pendingAck=0 | recoveries=" + autoReconnectOrchestrator.SuccessfulRecoveryCount);
            return true;
        }

        //* خلاصه نهایی تست فشار را بررسی و لاگ می‌کند.
        private bool ValidateFinalSummary()
        {
            int expectedRecoveries = GetSafeRecoveryCycleCount();
            bool recoveryCountOk = autoReconnectOrchestrator.SuccessfulRecoveryCount >= expectedRecoveries;
            bool noFailedRecovery = autoReconnectOrchestrator.FailedRecoveryCount == 0;
            bool noQueueLeak = realtimeClient.QueuedMessageCount == 0;
            bool noPendingAckLeak = realtimeClient.PendingAckCount == 0;

            Log("T8 summary: recoveries=" + autoReconnectOrchestrator.SuccessfulRecoveryCount + "/" + expectedRecoveries
                + " | failedRecoveries=" + autoReconnectOrchestrator.FailedRecoveryCount
                + " | duplicateAck=" + duplicateAckCount
                + " | policyDrops=" + droppedByPolicyCount
                + " | queuedDrops=" + queuedMessageDroppedCount
                + " | ackTimeouts=" + reliableAckTimeoutCount
                + " | maxQueue=" + maxQueueCountSeen
                + " | maxPendingAck=" + maxPendingAckCountSeen
                + " | stateEvents=" + stateChangedCount
                + " | disconnectEvents=" + disconnectedEventCount
                + " | queueEvents=" + queueCountChangedCount);

            if (!recoveryCountOk) return Fail("T8 failed: successful recovery count is lower than expected.");
            if (!noFailedRecovery) return Fail("T8 failed: auto reconnect has failed recovery count.");
            if (!noQueueLeak) return Fail("T8 failed: final queue leak detected.");
            if (!noPendingAckLeak) return Fail("T8 failed: final pending ack leak detected.");
            if (duplicateAckCount > 0) return Fail("T8 failed: duplicate ack count is not zero.");
            if (queuedMessageDroppedCount > 0) return Fail("T8 failed: queued message drop count is not zero.");
            if (reliableAckTimeoutCount > 0) return Fail("T8 failed: reliable ack timeout count is not zero.");

            return true;
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
            autoReconnectOrchestrator.initialDelayMs = Mathf.Max(300, autoReconnectInitialDelayMs);
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
            stateChangedCount++;
            UpdateDiagnosticPeaks();
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
            disconnectedEventCount++;
            Log("Disconnected: " + reason);
        }

        //* تغییر تعداد صف را در لاگ تست نشان می‌دهد.
        private void HandleQueueCountChanged(int count)
        {
            queueCountChangedCount++;
            if (count > maxQueueCountSeen) maxQueueCountSeen = count;
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
            queuedMessageDroppedCount++;
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
            UpdateDiagnosticPeaks();
            Log("Reliable: " + message);
        }

        //* تایم اوت اَک را در تست نشان می‌دهد.
        private void HandleReliableAckTimeout(string messageId)
        {
            reliableAckTimeoutCount++;
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

        //* اَک‌های گیم‌سرور را می‌شمارد تا سابسکرایب تکراری یا اَک تکراری شناسایی شود.
        private void HandleAckReceived(GameServerAckResult ack)
        {
            if (ack == null) return;
            RegisterAckForDuplicateCheck(ack.originalMessageId);

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

        #region <Metrics Helpers>

        //* شمارنده‌های تست فشار را برای اجرای جدید صفر می‌کند.
        private void ResetMetrics()
        {
            droppedByPolicyCount = 0;
            queuedMessageDroppedCount = 0;
            reliableAckTimeoutCount = 0;
            duplicateAckCount = 0;
            stateChangedCount = 0;
            disconnectedEventCount = 0;
            queueCountChangedCount = 0;
            maxQueueCountSeen = 0;
            maxPendingAckCountSeen = 0;
            dict_AckCountByMessageId.Clear();
        }

        //* آمار بیشینه صف و اَک‌های در انتظار را به‌روز می‌کند.
        private void UpdateDiagnosticPeaks()
        {
            if (realtimeClient == null) return;
            if (realtimeClient.QueuedMessageCount > maxQueueCountSeen) maxQueueCountSeen = realtimeClient.QueuedMessageCount;
            if (realtimeClient.PendingAckCount > maxPendingAckCountSeen) maxPendingAckCountSeen = realtimeClient.PendingAckCount;
        }

        //* اَک هر پیام را می‌شمارد تا تکرار ناخواسته رویدادها مشخص شود.
        private void RegisterAckForDuplicateCheck(string messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId)) return;

            int count;
            dict_AckCountByMessageId.TryGetValue(messageId, out count);
            count++;
            dict_AckCountByMessageId[messageId] = count;

            if (count > 1)
            {
                duplicateAckCount++;
                Log("Duplicate ack detected: " + messageId + " | count=" + count);
            }
        }

        //* تعداد چرخه‌های ریکاوری را در محدوده امن تست نگه می‌دارد.
        private int GetSafeRecoveryCycleCount()
        {
            return Mathf.Clamp(recoveryCycleCount, 1, 10);
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
            if (realtimeClient != null) await realtimeClient.DisconnectAsync("T8 cleanup");
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
            string prefix = string.IsNullOrWhiteSpace(roomIdPrefix) ? "unity_t8_room" : roomIdPrefix.Trim();
            return prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        //* پِیلود اکشن تستی را با فاز فعلی و روم فعال می‌سازد.
        private string BuildActionPayloadJson(string phase)
        {
            return "{\"source\":\"unity_t8\",\"phase\":\"" + EscapeJson(phase) + "\",\"roomId\":\"" + EscapeJson(activeRoomId) + "\",\"time\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}";
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
            Log("T8 failed: " + message);
            return false;
        }

        //* لاگ یکدست تست را در کنسول یونیتی چاپ می‌کند.
        private void Log(string message)
        {
            Debug.Log("[RealtimeT8] " + message);
        }

        #endregion
    }
}

//* این فایل تست طولانی تی‌اِیت را برای یونیتی اجرا می‌کند.
//* تست ثابت می‌کند چند چرخه ریکانکت خودکار، آث دوباره، جوین دوباره، فلش صف با اَک و کلین‌آپ بدون نشتی انجام می‌شود.
