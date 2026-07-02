using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Network_A.DedicatedGameServer.Client
{
    public class DedicatedPlayerStateAutoSender : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private DedicatedGameServerWsClient wsClient;
        [SerializeField] private Transform targetTransform;

        [Header("Send")]
        [SerializeField] private bool autoStartAfterAuth = true;
        [SerializeField] private bool sendOnUpdate = true;
        [SerializeField] private float sendIntervalSeconds = 0.1f;

        [Header("Debug")]
        [SerializeField] private bool logSendResult = false;

        private bool isSending;
        private bool isSendInFlight;
        private long sequence;
        private float nextSendAt;
        private Vector3 lastPosition;
        private float lastSampleTime;
        private CancellationTokenSource senderCts;

        public bool IsSending => isSending;

        //* این تابع رفرنس های لازم را در شروع آبجکت پیدا می کند.
        private void Awake()
        {
            EnsureReferences();
            ResetMotionSample();
        }

        //* این تابع هنگام فعال شدن آبجکت، رویداد احراز را گوش می دهد.
        private void OnEnable()
        {
            EnsureReferences();

            if (wsClient != null)
            {
                wsClient.Authenticated += HandleAuthenticated;
                wsClient.Disconnected += HandleDisconnected;
            }
        }

        //* این تابع هنگام غیرفعال شدن آبجکت، رویدادها را پاک می کند.
        private void OnDisable()
        {
            if (wsClient != null)
            {
                wsClient.Authenticated -= HandleAuthenticated;
                wsClient.Disconnected -= HandleDisconnected;
            }

            StopSending("disabled");
        }

        //* این تابع در هر فریم اگر زمان ارسال رسیده باشد، وضعیت پلیر را ارسال می کند.
        private void Update()
        {
            if (!sendOnUpdate || !isSending || isSendInFlight) return;
            if (wsClient == null || !wsClient.IsConnected || !wsClient.IsAuthenticated) return;
            if (Time.realtimeSinceStartup < nextSendAt) return;

            nextSendAt = Time.realtimeSinceStartup + Mathf.Max(0.02f, sendIntervalSeconds);
            _ = SendOneStateAsync();
        }

        //* این تابع از اینسپکتور ارسال خودکار را شروع می کند.
        [ContextMenu("Start Sending Player State")]
        public void Btn_StartSending()
        {
            StartSending();
        }

        //* این تابع از اینسپکتور ارسال خودکار را متوقف می کند.
        [ContextMenu("Stop Sending Player State")]
        public void Btn_StopSending()
        {
            StopSending("manual_stop");
        }

        //* این تابع از اینسپکتور یک پیام player_state فوری ارسال می کند.
        [ContextMenu("Send One Player State")]
        public async void Btn_SendOnePlayerState()
        {
            await SendOneStateAsync();
        }

        //* این تابع رفرنس کلاینت و ترنسفورم را پیدا می کند.
        private void EnsureReferences()
        {
            if (wsClient == null)
            {
                wsClient = GetComponent<DedicatedGameServerWsClient>();
                if (wsClient == null) wsClient = DedicatedGameServerWsClient.Instance;
            }

            if (targetTransform == null)
            {
                targetTransform = transform;
            }
        }

        //* این تابع بعد از auth_ok ارسال خودکار را شروع می کند.
        private void HandleAuthenticated()
        {
            if (!autoStartAfterAuth) return;

            StartSending();
        }

        //* این تابع بعد از قطع اتصال، ارسال را متوقف می کند.
        private void HandleDisconnected(string reason)
        {
            StopSending(reason);
        }

        //* این تابع ارسال خودکار وضعیت پلیر را فعال می کند.
        public void StartSending()
        {
            EnsureReferences();

            if (isSending)
            {
                if (logSendResult) Debug.Log("[DedicatedPlayerStateAutoSender] Start skipped. Sender is already running.");
                return;
            }

            if (wsClient == null)
            {
                Debug.LogError("[DedicatedPlayerStateAutoSender] DedicatedGameServerWsClient is missing.");
                return;
            }

            if (!wsClient.IsAuthenticated)
            {
                Debug.LogWarning("[DedicatedPlayerStateAutoSender] Cannot start. Client is not authenticated yet.");
                return;
            }

            if (senderCts != null)
            {
                senderCts.Cancel();
                senderCts.Dispose();
                senderCts = null;
            }

            senderCts = new CancellationTokenSource();
            isSending = true;
            isSendInFlight = false;
            nextSendAt = 0f;
            ResetMotionSample();

            Debug.Log("[DedicatedPlayerStateAutoSender] Sending started.");
        }

        //* این تابع ارسال خودکار را متوقف می کند.
        public void StopSending(string reason)
        {
            bool wasActive = isSending || isSendInFlight || senderCts != null;
            if (!wasActive) return;

            isSending = false;
            isSendInFlight = false;

            if (senderCts != null)
            {
                senderCts.Cancel();
                senderCts.Dispose();
                senderCts = null;
            }

            Debug.Log("[DedicatedPlayerStateAutoSender] Sending stopped | reason=" + reason);
        }

        //* این تابع یک پیام وضعیت پلیر را می سازد و می فرستد.
        private async Task<bool> SendOneStateAsync()
        {
            EnsureReferences();

            if (wsClient == null || !wsClient.IsConnected || !wsClient.IsAuthenticated)
            {
                return false;
            }

            if (targetTransform == null)
            {
                targetTransform = transform;
            }

            isSendInFlight = true;

            try
            {
                Vector3 position = targetTransform.position;
                Quaternion rotation = targetTransform.rotation;

                float now = Time.realtimeSinceStartup;
                float dt = Mathf.Max(0.0001f, now - lastSampleTime);
                Vector3 velocity = (position - lastPosition) / dt;

                lastPosition = position;
                lastSampleTime = now;

                sequence++;

                bool sent = await wsClient.SendPlayerStateAsync(
                    position,
                    rotation,
                    velocity,
                    sequence,
                    senderCts != null ? senderCts.Token : CancellationToken.None);

                if (logSendResult)
                {
                    Debug.Log("[DedicatedPlayerStateAutoSender] Send player_state | sequence=" +
                              sequence + " | sent=" + sent);
                }

                return sent;
            }
            finally
            {
                isSendInFlight = false;
            }
        }

        //* این تابع نمونه اولیه حرکت را تنظیم می کند.
        private void ResetMotionSample()
        {
            if (targetTransform == null) targetTransform = transform;

            lastPosition = targetTransform != null ? targetTransform.position : Vector3.zero;
            lastSampleTime = Time.realtimeSinceStartup;
        }

        /*
        توضیح مکتوب فایل:
        این اسکریپت سمت کلاینت تستی ددیکیتد است.
        بعد از دریافت auth_ok، به صورت دوره ای پیام player_state را با پوزیشن، روتیشن و وِلوسیتی می فرستد.
        DedicatedGameMessageRouter سمت سرور این پیام را دریافت، ذخیره و برای بقیه پلیرهای روم پخش می کند.
        برای تست تک کلاینت، سرور پیام player_state_accepted برمی گرداند.
        */
    }
}
