using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.GameServer;
using Network_A.Realtime.Auth;
using Network_A.Realtime.Core;

namespace Network_A.Realtime.Stability
{
    //* ارکستریتور اتوریکانکت ریل تایم است و بعد از قطعی ناخواسته، کانکت، آث، جوین دوباره و فلش صف را پشت سر هم انجام می دهد.
    public class RealtimeAutoReconnectOrchestrator : IDisposable
    {
        private readonly RealtimeClient realtimeClient;
        private readonly RealtimeAuthClient authClient;
        private readonly GameServerClient gameServerClient;
        private readonly RealtimeReconnect reconnect = new RealtimeReconnect();
        private CancellationTokenSource recoveryCts;
        private Func<CancellationToken, Task<bool>> authMessageSender;
        private bool isStarted;
        private bool isDisposed;
        private bool isRecovering;

        public event Action<string> AutoReconnectStarted;
        public event Action<string> AutoReconnectStepChanged;
        public event Action<int> AutoReconnectSucceeded;
        public event Action<string> AutoReconnectFailed;
        public event Action<string> AutoReconnectLogReceived;

        public int maxAttempts = 6;
        public int initialDelayMs = 800;
        public int maxDelayMs = 8000;
        public int totalTimeoutMs = 60000;
        public float delayMultiplier = 2f;
        public int authTimeoutMs = 10000;
        public bool flushQueueAfterRejoin = true;
        public bool ignoreIntentionalDisconnects = true;
        public bool logAutoReconnect;
        public RealtimeReliableSendOptions reliableOptions = RealtimeReliableSendOptions.Default();

        public bool IsStarted => isStarted;
        public bool IsRecovering => isRecovering;
        public int SuccessfulRecoveryCount { get; private set; }
        public int FailedRecoveryCount { get; private set; }
        public string LastDisconnectReason { get; private set; } = string.Empty;

        //* ارکستریتور را با کُر، آث و گیم سرور کلاینت می سازد.
        public RealtimeAutoReconnectOrchestrator(RealtimeClient realtimeClient, RealtimeAuthClient authClient, GameServerClient gameServerClient)
        {
            this.realtimeClient = realtimeClient ?? throw new ArgumentNullException(nameof(realtimeClient));
            this.authClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            this.gameServerClient = gameServerClient ?? throw new ArgumentNullException(nameof(gameServerClient));
            BindReconnectEvents();
        }

        //* تابع ارسال پیام آث را قابل جایگزینی می کند تا تست بتواند از توکن override یا توکن ذخیره شده استفاده کند.
        public void SetAuthMessageSender(Func<CancellationToken, Task<bool>> sender)
        {
            authMessageSender = sender;
        }

        //* گوش دادن به دیسکانکت ریل تایم را شروع می کند.
        public void Start()
        {
            if (isDisposed || isStarted) return;
            isStarted = true;
            realtimeClient.Disconnected += HandleRealtimeDisconnected;
            WriteLog("Auto reconnect orchestrator started.");
        }

        //* اتوریکانکت را متوقف می کند و تلاش فعال را لغو می کند.
        public void Stop()
        {
            if (!isStarted) return;
            isStarted = false;
            isRecovering = false;
            realtimeClient.Disconnected -= HandleRealtimeDisconnected;
            reconnect.Stop();
            recoveryCts?.Cancel();
            recoveryCts?.Dispose();
            recoveryCts = null;
            WriteLog("Auto reconnect orchestrator stopped.");
        }

        //* در صورت قطعی ناخواسته، چرخه ریکاوری را روشن می کند.
        private void HandleRealtimeDisconnected(string reason)
        {
            if (!isStarted || isDisposed || isRecovering) return;

            LastDisconnectReason = reason ?? string.Empty;
            if (ShouldIgnoreDisconnectReason(LastDisconnectReason))
            {
                WriteLog("Auto reconnect ignored disconnect: " + LastDisconnectReason);
                return;
            }

            BeginRecovery(LastDisconnectReason);
        }

        //* چرخه ریکاوری را با RealtimeReconnect شروع می کند.
        private void BeginRecovery(string reason)
        {
            isRecovering = true;
            ApplyReconnectSettings();

            recoveryCts?.Cancel();
            recoveryCts?.Dispose();
            recoveryCts = new CancellationTokenSource();

            AutoReconnectStarted?.Invoke(reason ?? string.Empty);
            WriteLog("Auto reconnect recovery started. reason=" + reason);
            reconnect.Start(RunRecoveryAttemptAsync);
        }

        //* یک تلاش کامل ریکاوری را اجرا می کند: کانکت، آث، جوین دوباره و فلش صف.
        private async Task<bool> RunRecoveryAttemptAsync(CancellationToken cancellationToken)
        {
            try
            {
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, recoveryCts.Token))
                {
                    CancellationToken token = linkedCts.Token;

                    SetStep("connect");
                    bool connected = await realtimeClient.ConnectAsync(null, token);
                    if (!connected) return FailAttempt("connect failed");

                    SetStep("auth");
                    bool authenticated = await AuthenticateAndWaitAsync(token);
                    if (!authenticated) return FailAttempt("auth failed");

                    string roomId = gameServerClient.LastKnownRoomId;
                    if (!string.IsNullOrWhiteSpace(roomId))
                    {
                        SetStep("rejoin:" + roomId);
                        RealtimeReliableSendResult joinResult = await gameServerClient.JoinRoomReliableAsync(roomId, reliableOptions, token);
                        if (joinResult == null || !joinResult.isSuccess) return FailAttempt("rejoin failed: " + (joinResult == null ? "null" : joinResult.errorMessage));
                    }
                    else
                    {
                        WriteLog("Auto reconnect skipped rejoin because lastKnownRoomId is empty.");
                    }

                    if (flushQueueAfterRejoin)
                    {
                        SetStep("flush_queue:" + realtimeClient.QueuedMessageCount);
                        bool flushed = await realtimeClient.FlushQueuedMessagesWithAckAsync(reliableOptions, token);
                        if (!flushed) return FailAttempt("queue flush failed");
                    }

                    SetStep("ready");
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                return FailAttempt("recovery attempt canceled");
            }
            catch (Exception ex)
            {
                return FailAttempt("recovery attempt exception: " + ex.Message);
            }
        }

        //* پیام آث را ارسال می کند و تا دریافت auth_ok یا auth_failed منتظر می ماند.
        private async Task<bool> AuthenticateAndWaitAsync(CancellationToken cancellationToken)
        {
            var authWaiter = new TaskCompletionSource<bool>();

            void HandleAuthenticated(string connectionId, string userId) => TrySetWaiter(authWaiter, true);
            void HandleAuthFailed(Network_A.Realtime.Protocol.RealtimeError error) => TrySetWaiter(authWaiter, false);

            authClient.Authenticated += HandleAuthenticated;
            authClient.AuthenticationFailed += HandleAuthFailed;

            try
            {
                authClient.ResetAuthState();
                Func<CancellationToken, Task<bool>> sender = authMessageSender;
                if (sender == null) sender = authClient.AuthenticateWithStoredTokenAsync;
                bool sent = await sender(cancellationToken);
                if (!sent) return false;

                Task completedTask = await Task.WhenAny(authWaiter.Task, Task.Delay(Math.Max(500, authTimeoutMs), cancellationToken));
                if (completedTask != authWaiter.Task)
                {
                    WriteLog("Auto reconnect auth timeout.");
                    return false;
                }

                return await authWaiter.Task;
            }
            finally
            {
                authClient.Authenticated -= HandleAuthenticated;
                authClient.AuthenticationFailed -= HandleAuthFailed;
            }
        }

        //* تنظیمات RealtimeReconnect داخلی را از تنظیمات ارکستریتور اعمال می کند.
        private void ApplyReconnectSettings()
        {
            reconnect.maxAttempts = maxAttempts;
            reconnect.initialDelayMs = initialDelayMs;
            reconnect.maxDelayMs = maxDelayMs;
            reconnect.totalTimeoutMs = totalTimeoutMs;
            reconnect.delayMultiplier = delayMultiplier;
            reconnect.logReconnect = logAutoReconnect;
            reliableOptions = reliableOptions ?? RealtimeReliableSendOptions.Default();
            reliableOptions.Normalize();
        }

        //* رویدادهای RealtimeReconnect را به رویدادهای سطح ارکستریتور وصل می کند.
        private void BindReconnectEvents()
        {
            reconnect.ReconnectAttemptStarted += HandleReconnectAttemptStarted;
            reconnect.ReconnectSucceeded += HandleReconnectSucceeded;
            reconnect.ReconnectFailed += HandleReconnectFailed;
            reconnect.ReconnectLogReceived += WriteLog;
        }

        //* رویدادهای RealtimeReconnect را جدا می کند.
        private void UnbindReconnectEvents()
        {
            reconnect.ReconnectAttemptStarted -= HandleReconnectAttemptStarted;
            reconnect.ReconnectSucceeded -= HandleReconnectSucceeded;
            reconnect.ReconnectFailed -= HandleReconnectFailed;
            reconnect.ReconnectLogReceived -= WriteLog;
        }

        //* شروع هر تلاش را لاگ می کند.
        private void HandleReconnectAttemptStarted(int attempt, int delayMs)
        {
            SetStep("attempt:" + attempt + ":delay=" + delayMs);
        }

        //* موفقیت ریکاوری کامل را اعلام می کند.
        private void HandleReconnectSucceeded(int attempt)
        {
            isRecovering = false;
            SuccessfulRecoveryCount++;
            AutoReconnectSucceeded?.Invoke(attempt);
            WriteLog("Auto reconnect recovery succeeded. attempt=" + attempt);
        }

        //* شکست نهایی ریکاوری را اعلام می کند.
        private void HandleReconnectFailed(string reason)
        {
            isRecovering = false;
            FailedRecoveryCount++;
            AutoReconnectFailed?.Invoke(reason ?? string.Empty);
            WriteLog("Auto reconnect recovery failed: " + reason);
        }

        //* شکست یک تلاش را لاگ می کند و false برمی گرداند تا تلاش بعدی انجام شود.
        private bool FailAttempt(string reason)
        {
            WriteLog("Auto reconnect attempt failed: " + reason);
            return false;
        }

        //* مرحله فعلی ریکاوری را به بیرون اعلام می کند.
        private void SetStep(string step)
        {
            string safeStep = step ?? string.Empty;
            AutoReconnectStepChanged?.Invoke(safeStep);
            WriteLog("Auto reconnect step: " + safeStep);
        }

        //* مشخص می کند دلیل دیسکانکت عمدی است و نباید اتوریکانکت شروع شود.
        private bool ShouldIgnoreDisconnectReason(string reason)
        {
            if (!ignoreIntentionalDisconnects) return false;
            if (string.IsNullOrWhiteSpace(reason)) return false;

            string lower = reason.ToLowerInvariant();
            return lower.Contains("intentional")
                || lower.Contains("manual")
                || lower.Contains("completed")
                || lower.Contains("cleanup")
                || lower.Contains("disposed")
                || lower.Contains("logout")
                || lower.Contains("leave")
                || lower.Contains("client disconnect");
        }

        //* نتیجه انتظار را اگر هنوز کامل نشده باشد ثبت می کند.
        private static void TrySetWaiter(TaskCompletionSource<bool> waiter, bool value)
        {
            if (waiter == null || waiter.Task.IsCompleted) return;
            waiter.TrySetResult(value);
        }

        //* لاگ ارکستریتور را چاپ یا به بیرون ارسال می کند.
        private void WriteLog(string message)
        {
            string safeMessage = message ?? string.Empty;
            if (logAutoReconnect) UnityEngine.Debug.Log("[RealtimeAutoReconnect] " + safeMessage);
            AutoReconnectLogReceived?.Invoke(safeMessage);
        }

        //* منابع ارکستریتور را آزاد می کند.
        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;
            Stop();
            UnbindReconnectEvents();
            reconnect.Dispose();
        }
    }
}

//* این فایل ریکانکت خودکار ریل تایم را ارکستریت می کند.
//* این فایل ترنسپورت را نمی شناسد و فقط کُر، آث، روم و صف قابل اطمینان را هماهنگ می کند.
