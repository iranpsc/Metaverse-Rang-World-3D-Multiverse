using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Network_A.GameServer;
using UnityEngine;
using UnityEngine.Networking;

namespace Network_A.Voice.Dedicated
{
    [DisallowMultipleComponent]
    public sealed class VoiceDedicatedSessionDeltaSender : MonoBehaviour
    {
        public const string RelativeEndpointPath =
            "/game-server-control/dedicated/voice-session-delta";

        private const int MaximumPendingEventCount = 1024;
        private const int MaximumBatchEventCount = 1024;
        private const int RequestTimeoutSeconds = 15;
        private const float InitialRetryDelaySeconds = 1.0f;
        private const float MaximumRetryDelaySeconds = 4.0f;
        private const float RetryDelayMultiplier = 1.75f;
        private const int PermanentConflictStatusCode = 409;

        private readonly Queue<VoiceDedicatedSessionDelta> pendingEvents =
            new Queue<VoiceDedicatedSessionDelta>();

        private DedicatedServerRuntime runtime;
        private GameServerControlDedicatedClient controlClient;
        private CancellationTokenSource lifecycleCts;
        private bool configured;
        private bool transportEnabled;
        private bool sendLoopRunning;
        private bool queueFaulted;
        private float nextRetryAtRealtime;
        private float currentRetryDelaySeconds = InitialRetryDelaySeconds;

        public int PendingEventCount { get { return pendingEvents.Count; } }
        public bool IsConfigured { get { return configured; } }
        public bool IsTransportEnabled { get { return transportEnabled; } }
        public bool IsQueueFaulted { get { return queueFaulted; } }
        public long TotalAcceptedEvents { get; private set; }
        public int TotalSuccessfulBatches { get; private set; }
        public int TotalFailedBatches { get; private set; }
        public string LastFailure { get; private set; }

        public event Action<int> BatchAccepted;
        public event Action<string> BatchFailed;
        public event Action<string> QueueFaulted;

        //* این تابع فرستنده رویداد را به رانتایم و کلاینت کنترل سرور اختصاصی متصل می‌کند.
        public void Configure(
            DedicatedServerRuntime dedicatedRuntime,
            GameServerControlDedicatedClient dedicatedControlClient,
            bool enableTransport)
        {
            if (dedicatedRuntime == null)
            {
                throw new ArgumentNullException("dedicatedRuntime");
            }

            if (dedicatedControlClient == null)
            {
                throw new ArgumentNullException("dedicatedControlClient");
            }

            runtime = dedicatedRuntime;
            controlClient = dedicatedControlClient;
            transportEnabled = enableTransport;
            configured = true;
            queueFaulted = false;
            LastFailure = string.Empty;
            currentRetryDelaySeconds = InitialRetryDelaySeconds;
            nextRetryAtRealtime = 0.0f;

            if (lifecycleCts != null)
            {
                lifecycleCts.Cancel();
                lifecycleCts.Dispose();
            }

            lifecycleCts = new CancellationTokenSource();

            Debug.Log(
                "[VoiceDedicatedSessionDeltaSender] Configured" +
                " | transportEnabled=" + transportEnabled +
                " | endpoint=" + RelativeEndpointPath);
        }

        //* این تابع یک رویداد معتبر را با حفظ ترتیب قطعی داخل صف انتقال قرار می‌دهد.
        public bool Enqueue(VoiceDedicatedSessionDelta delta, out string error)
        {
            error = string.Empty;

            if (!configured)
            {
                error = "Voice dedicated delta sender is not configured.";
                return false;
            }

            if (queueFaulted)
            {
                error = "Voice dedicated delta sender queue is faulted.";
                return false;
            }

            if (delta == null)
            {
                error = "Voice dedicated delta is missing.";
                return false;
            }

            try
            {
                delta.ValidateOrThrow();
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }

            if (pendingEvents.Count >= MaximumPendingEventCount)
            {
                queueFaulted = true;
                LastFailure =
                    "Voice dedicated delta queue reached the protected limit of " +
                    MaximumPendingEventCount +
                    " events. No event was dropped silently.";

                Debug.LogError(
                    "[VoiceDedicatedSessionDeltaSender] Queue faulted" +
                    " | pending=" + pendingEvents.Count +
                    " | error=" + LastFailure);

                QueueFaulted?.Invoke(LastFailure);
                error = LastFailure;
                return false;
            }

            pendingEvents.Enqueue(delta);
            return true;
        }

        //* این تابع در هر فریم فقط در صورت وجود صف و پایان فاصله تلاش دوباره، ارسال بعدی را شروع می‌کند.
        private void Update()
        {
            if (!configured ||
                !transportEnabled ||
                queueFaulted ||
                sendLoopRunning ||
                pendingEvents.Count == 0 ||
                lifecycleCts == null ||
                lifecycleCts.IsCancellationRequested ||
                Time.realtimeSinceStartup < nextRetryAtRealtime)
            {
                return;
            }

            _ = SendNextBatchAsync(lifecycleCts.Token);
        }

        //* این تابع اولین بخش مرتب صف را به شکل یک درخواست جیسون امن به نود ارسال می‌کند.
        private async Task SendNextBatchAsync(CancellationToken cancellationToken)
        {
            if (sendLoopRunning) return;
            sendLoopRunning = true;

            try
            {
                VoiceDedicatedSessionDelta[] batch = CreatePendingBatch();
                if (batch.Length == 0) return;

                DedicatedServerConfigData config = runtime.GetCurrentConfig();
                if (config == null)
                {
                    RegisterFailure("Dedicated runtime config is missing.");
                    return;
                }

                string serverId = string.IsNullOrWhiteSpace(config.serverId)
                    ? string.Empty
                    : config.serverId.Trim();

                if (string.IsNullOrWhiteSpace(serverId))
                {
                    RegisterFailure("Dedicated runtime serverId is missing.");
                    return;
                }

                for (int index = 0; index < batch.Length; index += 1)
                {
                    if (!string.Equals(batch[index].serverId, serverId, StringComparison.Ordinal))
                    {
                        RegisterPermanentQueueFault(
                            "A queued Voice delta belongs to another dedicated serverId.");
                        return;
                    }
                }

                string serviceToken = await controlClient.GetFreshServiceTokenAsync(
                    cancellationToken,
                    false);

                if (string.IsNullOrWhiteSpace(serviceToken))
                {
                    RegisterFailure("Dedicated service token is missing.");
                    return;
                }

                VoiceDedicatedSessionDeltaBatchRequest requestBody =
                    new VoiceDedicatedSessionDeltaBatchRequest
                    {
                        serviceToken = serviceToken.Trim(),
                        serverId = serverId,
                        authorityEpochId = batch[0].authorityEpochId,
                        events = batch
                    };

                string baseUrl = string.IsNullOrWhiteSpace(config.controlBaseUrl)
                    ? string.Empty
                    : config.controlBaseUrl.Trim().TrimEnd('/');

                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    RegisterFailure("Dedicated controlBaseUrl is missing.");
                    return;
                }

                string url = baseUrl + RelativeEndpointPath;
                string json = JsonUtility.ToJson(requestBody);
                VoiceDedicatedHttpSendResult result = await SendJsonAsync(
                    url,
                    json,
                    cancellationToken);

                if (!result.Success)
                {
                    if (result.StatusCode == 401 || result.StatusCode == 403)
                    {
                        await controlClient.GetFreshServiceTokenAsync(
                            cancellationToken,
                            true);
                    }

                    string rejectedBody = string.IsNullOrWhiteSpace(result.RawBody)
                        ? string.Empty
                        : " | body=" + CompactForLog(result.RawBody);

                    string rejectedError =
                        "status=" + result.StatusCode +
                        " | error=" + result.Error +
                        rejectedBody;

                    if (IsVoiceConnectionReadinessConflict(
                            result.StatusCode,
                            result.RawBody))
                    {
                        RegisterFailure(
                            "VOICE_G4_PAIR_CONNECTION_NOT_READY_RETRY" +
                            " | " + rejectedError);
                        return;
                    }

                    if (result.StatusCode == PermanentConflictStatusCode)
                    {
                        RegisterPermanentQueueFault(rejectedError);
                        return;
                    }

                    RegisterFailure(rejectedError);
                    return;
                }

                VoiceDedicatedSessionDeltaBatchResponse response = null;

                try
                {
                    response = JsonUtility.FromJson<VoiceDedicatedSessionDeltaBatchResponse>(
                        result.RawBody);
                }
                catch (Exception exception)
                {
                    RegisterFailure(
                        "Voice delta response parse failed: " + exception.Message);
                    return;
                }

                if (response == null || !response.success)
                {
                    string responseReason = response == null
                        ? "voice_delta_response_missing"
                        : response.reason;

                    string responseMessage = response == null
                        ? result.RawBody
                        : response.message;

                    RegisterFailure(
                        responseReason + " | " + responseMessage);
                    return;
                }

                RemoveAcceptedBatch(batch.Length);
                TotalAcceptedEvents += batch.Length;
                TotalSuccessfulBatches += 1;
                LastFailure = string.Empty;
                currentRetryDelaySeconds = InitialRetryDelaySeconds;
                nextRetryAtRealtime = 0.0f;

                Debug.Log(
                    "[VoiceDedicatedSessionDeltaSender] Batch accepted" +
                    " | accepted=" + batch.Length +
                    " | pending=" + pendingEvents.Count +
                    " | serverId=" + serverId);

                BatchAccepted?.Invoke(batch.Length);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                RegisterFailure(exception.ToString());
            }
            finally
            {
                sendLoopRunning = false;
            }
        }

        //* این تابع حداکثر اندازه مجاز را بدون حذف از صف برای ارسال آماده می‌کند.
        private VoiceDedicatedSessionDelta[] CreatePendingBatch()
        {
            int batchCount = Math.Min(pendingEvents.Count, MaximumBatchEventCount);
            VoiceDedicatedSessionDelta[] snapshot = pendingEvents.ToArray();
            VoiceDedicatedSessionDelta[] batch = new VoiceDedicatedSessionDelta[batchCount];

            if (batchCount > 0)
            {
                Array.Copy(snapshot, 0, batch, 0, batchCount);
            }

            return batch;
        }

        //* این تابع پس از تغییر دوره اقتدار، صف رویدادهای مانده از دوره قبلی را پاک و ارسال را دوباره آماده می‌کند.
        public void ResetQueueAfterAuthorityEpochReset(string reason)
        {
            int clearedCount = pendingEvents.Count;
            pendingEvents.Clear();
            queueFaulted = false;
            LastFailure = string.Empty;
            currentRetryDelaySeconds = InitialRetryDelaySeconds;
            nextRetryAtRealtime = 0.0f;

            Debug.Log(
                "VOICE_G3_DELTA_SENDER_QUEUE_RESET=PASS" +
                " | cleared=" + clearedCount +
                " | reason=" + CompactForLog(reason));
        }

        //* این تابع فقط خطای موقت آماده‌نبودن اتصال صوتی را بدون حذف Queue یا تغییر Epoch دوباره قابل ارسال می‌کند.
        internal void ResumeQueueAfterTransientConflict(string reason)
        {
            if (!configured) return;

            queueFaulted = false;
            LastFailure = string.IsNullOrWhiteSpace(reason)
                ? "voice_delta_transient_conflict"
                : reason.Trim();
            currentRetryDelaySeconds = InitialRetryDelaySeconds;
            nextRetryAtRealtime =
                Time.realtimeSinceStartup + InitialRetryDelaySeconds;

            Debug.LogWarning(
                "VOICE_G4_DELTA_SENDER_TRANSIENT_CONFLICT_RETRY=PASS" +
                " | pending=" + pendingEvents.Count +
                " | retryAfterSeconds=" + InitialRetryDelaySeconds.ToString("F2") +
                " | reason=" + CompactForLog(LastFailure));
        }

        //* این تابع فقط پس از پاسخ موفق نود همان تعداد پذیرفته‌شده را از ابتدای صف حذف می‌کند.
        private void RemoveAcceptedBatch(int acceptedCount)
        {
            int safeAcceptedCount = Math.Min(Math.Max(0, acceptedCount), pendingEvents.Count);

            for (int index = 0; index < safeAcceptedCount; index += 1)
            {
                pendingEvents.Dequeue();
            }
        }

        //* این تابع متن خطا را برای ثبت کوتاه و یک‌خطی آماده می‌کند.
        private static string CompactForLog(string value)
        {
            string text = string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().Replace("\r", " ").Replace("\n", " ");

            return text.Length <= 512
                ? text
                : text.Substring(0, 512);
        }

        //* این تابع فقط Conflict مشخص آماده‌نبودن Voice Connection را موقت تشخیص می‌دهد و سایر 409ها را تغییر نمی‌دهد.
        internal static bool IsVoiceConnectionReadinessConflict(
            int statusCode,
            string rawBody)
        {
            if (statusCode != PermanentConflictStatusCode ||
                string.IsNullOrWhiteSpace(rawBody))
            {
                return false;
            }

            return rawBody.IndexOf(
                       "voice_delta_pair_voice_connection_not_ready",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        //* این تابع خطای موقت را ثبت و تلاش بعدی را با تأخیر افزایشی زمان‌بندی می‌کند.
        private void RegisterFailure(string error)
        {
            TotalFailedBatches += 1;
            LastFailure = string.IsNullOrWhiteSpace(error)
                ? "unknown_voice_delta_send_failure"
                : error.Trim();

            nextRetryAtRealtime =
                Time.realtimeSinceStartup + currentRetryDelaySeconds;

            currentRetryDelaySeconds = Mathf.Min(
                MaximumRetryDelaySeconds,
                currentRetryDelaySeconds * RetryDelayMultiplier);

            Debug.LogWarning(
                "[VoiceDedicatedSessionDeltaSender] Batch failed" +
                " | pending=" + pendingEvents.Count +
                " | retryAfterSeconds=" +
                Mathf.Max(0.0f, nextRetryAtRealtime - Time.realtimeSinceStartup).ToString("F2") +
                " | error=" + LastFailure);

            BatchFailed?.Invoke(LastFailure);
        }

        //* این تابع خطایی را ثبت می‌کند که ادامه صف بدون اصلاح ساختار امکان‌پذیر نیست.
        private void RegisterPermanentQueueFault(string error)
        {
            queueFaulted = true;
            LastFailure = string.IsNullOrWhiteSpace(error)
                ? "unknown_voice_delta_queue_fault"
                : error.Trim();

            Debug.LogError(
                "[VoiceDedicatedSessionDeltaSender] Permanent queue fault" +
                " | pending=" + pendingEvents.Count +
                " | error=" + LastFailure);

            QueueFaulted?.Invoke(LastFailure);
        }

        //* این تابع درخواست جیسون را با زمان پایان و لغو کنترل‌شده ارسال می‌کند.
        private static async Task<VoiceDedicatedHttpSendResult> SendJsonAsync(
            string url,
            string json,
            CancellationToken cancellationToken)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(json ?? string.Empty);

            using (UnityWebRequest request =
                   new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = RequestTimeoutSeconds;
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Accept", "application/json");
                request.SetRequestHeader("X-Metaverse-Dedicated-Server", "unity");
                request.SetRequestHeader("X-Metaverse-Voice-Authority", "dedicated_server");

                UnityWebRequestAsyncOperation operation = request.SendWebRequest();

                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        request.Abort();
                        throw new OperationCanceledException(cancellationToken);
                    }

                    await Task.Yield();
                }

                int statusCode = (int)request.responseCode;
                string rawBody = request.downloadHandler == null
                    ? string.Empty
                    : request.downloadHandler.text;

                bool successfulStatus = statusCode >= 200 && statusCode < 300;
                bool transportError =
                    request.result == UnityWebRequest.Result.ConnectionError ||
                    request.result == UnityWebRequest.Result.DataProcessingError;

                if (!successfulStatus || transportError)
                {
                    string requestError = string.IsNullOrWhiteSpace(request.error)
                        ? rawBody
                        : request.error;

                    return VoiceDedicatedHttpSendResult.Failed(
                        statusCode,
                        requestError,
                        rawBody);
                }

                return VoiceDedicatedHttpSendResult.Succeeded(
                    statusCode,
                    rawBody);
            }
        }

        //* این تابع هنگام غیرفعال‌شدن آبجکت، ارسال در حال اجرا را متوقف می‌کند.
        private void OnDisable()
        {
            if (lifecycleCts != null && !lifecycleCts.IsCancellationRequested)
            {
                lifecycleCts.Cancel();
            }
        }

        //* این تابع هنگام حذف آبجکت، منبع لغو را آزاد می‌کند.
        private void OnDestroy()
        {
            if (lifecycleCts == null) return;
            lifecycleCts.Cancel();
            lifecycleCts.Dispose();
            lifecycleCts = null;
        }

        private sealed class VoiceDedicatedHttpSendResult
        {
            public bool Success { get; private set; }
            public int StatusCode { get; private set; }
            public string Error { get; private set; }
            public string RawBody { get; private set; }

            //* این تابع نتیجه موفق درخواست را می‌سازد.
            public static VoiceDedicatedHttpSendResult Succeeded(
                int statusCode,
                string rawBody)
            {
                return new VoiceDedicatedHttpSendResult
                {
                    Success = true,
                    StatusCode = statusCode,
                    Error = string.Empty,
                    RawBody = rawBody ?? string.Empty
                };
            }

            //* این تابع نتیجه ناموفق درخواست را می‌سازد.
            public static VoiceDedicatedHttpSendResult Failed(
                int statusCode,
                string error,
                string rawBody)
            {
                return new VoiceDedicatedHttpSendResult
                {
                    Success = false,
                    StatusCode = statusCode,
                    Error = error ?? string.Empty,
                    RawBody = rawBody ?? string.Empty
                };
            }
        }
    }
}

/*
توضیح فایل:
این فایل رویدادهای سشن صوتی را به ترتیب قطعی و به صورت دسته‌ای به مسیر اصلی کنترل گیم سرور ارسال می‌کند. صف هیچ رویدادی را بی‌صدا حذف نمی‌کند، از سرویس توکن تازه‌شده استفاده می‌کند، خطای موقت را با تلاش دوباره مدیریت می‌کند و هنگام حذف آبجکت تمام کارهای در حال اجرا را لغو می‌کند.
*/
