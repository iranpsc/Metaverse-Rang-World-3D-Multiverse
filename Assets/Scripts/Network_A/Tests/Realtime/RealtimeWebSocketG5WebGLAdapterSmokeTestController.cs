using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Realtime.Core;
using Network_A.Realtime.Transport;
using UnityEngine;

namespace Network_A.Tests.Realtime
{
    //* کنترلر تست جی‌فایو است و فقط مسیر انتخاب ترنسپورت و اتصال WebGL WebSocket Adapter را بررسی می‌کند.
    public class RealtimeWebSocketG5WebGLAdapterSmokeTestController : MonoBehaviour
    {
        [Header("Connection")]
        [SerializeField] private string serverUrl = "ws://127.0.0.1:8080";
        [SerializeField] private RealtimeTransportKind transportKind = RealtimeTransportKind.WebSocket;
        [SerializeField] private bool runOnStart;
        [SerializeField] private bool disconnectAtEnd = true;

        [Header("Timeout")]
        [SerializeField] private int connectTimeoutMs = 10000;
        [SerializeField] private int sendTimeoutMs = 10000;

        private RealtimeClient realtimeClient;
        private CancellationTokenSource lifecycleCts;
        private bool isRunning;
        private bool eventsBound;

        #region <Unity Lifecycle>

        //* منبع لغو تست را می‌سازد و کُر ریل‌تایم را برای تست آماده می‌کند.
        private void Awake()
        {
            lifecycleCts = new CancellationTokenSource();
            CreateRealtimeClient();
        }

        //* اگر اجرای خودکار فعال باشد، تست جی‌فایو را بعد از شروع صحنه اجرا می‌کند.
        private async void Start()
        {
            if (!runOnStart) return;
            await RunG5WebGLAdapterSmokeTestAsync();
        }

        //* هنگام حذف آبجکت، اتصال باز را می‌بندد و منابع تست را آزاد می‌کند.
        private async void OnDestroy()
        {
            lifecycleCts?.Cancel();
            await CleanupAsync("G5 test object destroyed");
            lifecycleCts?.Dispose();
            lifecycleCts = null;
        }

        #endregion

        #region <Inspector Buttons>

        //* از اینسپکتور یا دکمه یوآی برای اجرای تست جی‌فایو صدا زده می‌شود.
        public async void RunG5WebGLAdapterSmokeTestButton()
        {
            await RunG5WebGLAdapterSmokeTestAsync();
        }

        //* از اینسپکتور یا دکمه یوآی برای قطع دستی اتصال تست صدا زده می‌شود.
        public async void DisconnectButton()
        {
            await CleanupAsync("Manual G5 disconnect");
        }

        #endregion

        #region <Main Test Flow>

        //* مسیر ثبت ترنسپورت، ساخت ترنسپورت، اتصال، و قطع استاندارد را برای فاز جی‌فایو تست می‌کند.
        public async Task<bool> RunG5WebGLAdapterSmokeTestAsync()
        {
            if (isRunning)
            {
                Log("G5 test is already running.");
                return false;
            }

            isRunning = true;
            Log("G5 WebGL adapter smoke test started.");

            try
            {
                CreateRealtimeClient();
                bool factoryReady = ValidateTransportFactory();
                if (!factoryReady) return Fail("Transport factory validation failed.");

                bool connected = await ConnectAsync();
                if (!connected) return Fail("G5 connect failed.");

                if (disconnectAtEnd) await CleanupAsync("G5 completed");

                Log("G5 WebGL adapter smoke test completed successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("G5 test canceled.");
            }
            catch (Exception ex)
            {
                return Fail("G5 test exception: " + ex.Message);
            }
            finally
            {
                isRunning = false;
            }
        }

        //* ثبت بودن ترنسپورت انتخاب‌شده را قبل از اتصال بررسی می‌کند تا خطای Factory زودتر دیده شود.
        private bool ValidateTransportFactory()
        {
            RealtimeTransportKind resolvedKind = RealtimeTransportFactory.ResolveTransportKind(transportKind);
            bool registered = RealtimeTransportFactory.HasRegisteredTransport(transportKind);

            Log("Requested transport: " + transportKind);
            Log("Resolved transport: " + resolvedKind);
            Log("Registered: " + registered);
            Log("Runtime platform: " + Application.platform);

#if UNITY_WEBGL && !UNITY_EDITOR
            if (resolvedKind != RealtimeTransportKind.WebSocket) return Fail("WebGL must resolve realtime transport to WebSocket.");
#endif

            return registered;
        }

        //* اتصال کُر ریل‌تایم را از طریق ترنسپورت انتخاب‌شده شروع می‌کند.
        private async Task<bool> ConnectAsync()
        {
            Log("Connecting to " + serverUrl);
            bool connected = await realtimeClient.ConnectAsync(null, lifecycleCts.Token);
            Log("Connect result: " + connected);
            return connected;
        }

        #endregion

        #region <Client Setup>

        //* کُر ریل‌تایم را با کانفیگ تست می‌سازد و رویدادهای قابل مشاهده را وصل می‌کند.
        private void CreateRealtimeClient()
        {
            UnbindEvents();
            realtimeClient?.Dispose();

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

            config.Normalize();
            realtimeClient = new RealtimeClient(config);
            BindEvents();
        }

        //* رویدادهای کُر را به لاگ تست وصل می‌کند.
        private void BindEvents()
        {
            if (realtimeClient == null || eventsBound) return;
            realtimeClient.StateChanged += HandleStateChanged;
            realtimeClient.TransportErrorReceived += HandleTransportErrorReceived;
            realtimeClient.Disconnected += HandleDisconnected;
            eventsBound = true;
        }

        //* رویدادهای کُر را جدا می‌کند تا بعد از ساخت مجدد کلاینت، رویداد تکراری نداشته باشیم.
        private void UnbindEvents()
        {
            if (realtimeClient == null || !eventsBound) return;
            realtimeClient.StateChanged -= HandleStateChanged;
            realtimeClient.TransportErrorReceived -= HandleTransportErrorReceived;
            realtimeClient.Disconnected -= HandleDisconnected;
            eventsBound = false;
        }

        #endregion

        #region <Event Handlers>

        //* تغییر وضعیت کُر را در لاگ تست نمایش می‌دهد.
        private void HandleStateChanged(RealtimeConnectionState newState)
        {
            Log("State changed: " + newState);
        }

        //* خطای خام ترنسپورت را در لاگ تست نمایش می‌دهد.
        private void HandleTransportErrorReceived(string error)
        {
            LogWarning("Transport error: " + error);
        }

        //* قطع اتصال ترنسپورت را در لاگ تست نمایش می‌دهد.
        private void HandleDisconnected(string reason)
        {
            Log("Disconnected: " + reason);
        }

        #endregion

        #region <Cleanup>

        //* اتصال تست را می‌بندد و رویدادها را پاکسازی می‌کند.
        private async Task CleanupAsync(string reason)
        {
            UnbindEvents();

            if (realtimeClient != null && realtimeClient.IsConnected) await realtimeClient.DisconnectAsync(reason, CancellationToken.None);

            realtimeClient?.Dispose();
            realtimeClient = null;
        }

        #endregion

        #region <Logging>

        //* پیام عادی تست را با پیشوند ثابت ثبت می‌کند.
        private void Log(string message)
        {
            Debug.Log("[G5-WebGL-Adapter] " + message);
        }

        //* پیام هشدار تست را با پیشوند ثابت ثبت می‌کند.
        private void LogWarning(string message)
        {
            Debug.LogWarning("[G5-WebGL-Adapter] " + message);
        }

        //* شکست تست را لاگ می‌کند و مقدار false برمی‌گرداند.
        private bool Fail(string message)
        {
            Debug.LogError("[G5-WebGL-Adapter] " + message);
            return false;
        }

        #endregion
    }
}
