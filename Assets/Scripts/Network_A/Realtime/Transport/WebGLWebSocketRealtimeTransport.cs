#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Network_A.Core;

namespace Network_A.Realtime.Transport
{
    //* ترنسپورت مخصوص WebGL است و WebSocket مرورگر را از طریق JavaScript Plugin به قرارداد IRealtimeTransport وصل می‌کند.
    public class WebGLWebSocketRealtimeTransport : IRealtimeTransport, IDisposable
    {
        private const int BrowserOpenState = 1;
        private const int NormalCloseCode = 1000;
        private const int DisconnectDelayMs = 80;

        private static int nextHandle = 1;

        private readonly int handle;
        private RealtimeTransportState state = RealtimeTransportState.Disconnected;
        private TaskCompletionSource<bool> connectCompletionSource;
        private CancellationTokenRegistration connectCancellationRegistration;
        private bool isDisconnecting;
        private bool disconnectedEventSent;
        private string lastCloseReason = string.Empty;

        public event Action Connected;
        public event Action<string> MessageReceived;
        public event Action<string> ErrorReceived;
        public event Action<string> Disconnected;

        public RealtimeTransportKind Kind => RealtimeTransportKind.WebSocket;
        public RealtimeTransportState State => state;
        public static bool IsBrowserOnline => RealtimeWebGLWebSocketGetBrowserOnlineState() == 1;
        public bool IsConnected =>
            state == RealtimeTransportState.Connected &&
            RealtimeWebGLWebSocketGetReadyState(handle) == BrowserOpenState &&
            IsBrowserOnline;

        //* نمونه WebGL را با handle یکتا می‌سازد تا Callbackهای JavaScript به همین ترنسپورت برگردند.
        public WebGLWebSocketRealtimeTransport()
        {
            handle = nextHandle++;
            WebGLWebSocketBridge.RegisterTransport(handle, this);
        }

        //* ترنسپورت WebGL را در کارخانه ثبت می‌کند تا Auto/WebSocket در WebGL واقعی به Adapter مرورگر برسد.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RegisterTransportOnLoad()
        {
            RealtimeTransportFactory.RegisterTransport(RealtimeTransportKind.WebSocket, () => new WebGLWebSocketRealtimeTransport());
        }

        //* اتصال WebSocket مرورگر را شروع می‌کند و تا رسیدن onopen یا خطا منتظر می‌ماند.
        public async Task<bool> ConnectAsync(string url, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        {
            if (IsConnected) return true;
            if (string.IsNullOrWhiteSpace(url)) return FailConnect("WebGL WebSocket url is empty.");

            CleanupConnectWaiter();
            WebGLWebSocketBridge.RegisterTransport(handle, this);
            SetState(RealtimeTransportState.Connecting);
            isDisconnecting = false;
            disconnectedEventSent = false;
            lastCloseReason = string.Empty;
            connectCompletionSource = new TaskCompletionSource<bool>();
            TaskCompletionSource<bool> connectWaiter = connectCompletionSource;

            if (headers != null && headers.Count > 0) Debug.LogWarning("[WebGLWebSocketRealtimeTransport] Browser WebSocket cannot attach custom headers. Realtime auth must be sent as the first system/auth message.");
            if (cancellationToken.CanBeCanceled) connectCancellationRegistration = cancellationToken.Register(HandleConnectCanceled);

            Debug.Log("[WebGLWebSocketRealtimeTransport] Browser connect starting. handle=" + handle + " url=" + url);
            int started = RealtimeWebGLWebSocketConnect(handle, url);
            if (started != 1) return FailConnect("WebGL WebSocket browser connect did not start.");
            Debug.Log("[WebGLWebSocketRealtimeTransport] Browser connect accepted. handle=" + handle);

            bool connected = await connectWaiter.Task;
            if (!connected) SetState(RealtimeTransportState.Failed);
            return connected;
        }

        //* پیام خام آماده‌شده توسط کُر را از طریق WebSocket مرورگر ارسال می‌کند.
        public Task<bool> SendAsync(string message, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(message)) return Task.FromResult(false);
            if (cancellationToken.IsCancellationRequested) return Task.FromResult(false);
            if (!IsConnected) return Task.FromResult(false);

            int sent = RealtimeWebGLWebSocketSend(handle, message);
            if (sent == 1) return Task.FromResult(true);

            ErrorReceived?.Invoke("WebGL WebSocket send failed in browser layer.");
            return Task.FromResult(false);
        }

        //* اتصال WebSocket مرورگر را با close frame استاندارد می‌بندد و وضعیت ترنسپورت را پاکسازی می‌کند.
        public async Task DisconnectAsync(string reason = "Client disconnect", CancellationToken cancellationToken = default)
        {
            if (state == RealtimeTransportState.Disconnected) return;
            if (isDisconnecting) return;

            isDisconnecting = true;
            SetState(RealtimeTransportState.Disconnecting);
            lastCloseReason = string.IsNullOrWhiteSpace(reason) ? "Client disconnect" : reason;
            RealtimeWebGLWebSocketClose(handle, NormalCloseCode, lastCloseReason);

            await WaitDisconnectDelayAsync(cancellationToken);

            CompleteDisconnect(lastCloseReason);
            isDisconnecting = false;
        }

        //* تاخیر کوتاه دیسکانکت را در WebGL با کوروتین یونیتی جلو می برد تا await روی تَسک دیلی مرورگر گیر نکند.
        private async Task WaitDisconnectDelayAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return;
            await WaitDisconnectDelayWithUnityCoroutineAsync(DisconnectDelayMs, cancellationToken);
        }

        //* تاخیر دیسکانکت WebGL را روی مین ترد یونیتی اجرا می کند و نتیجه را به تَسک برمی گرداند.
        private async Task WaitDisconnectDelayWithUnityCoroutineAsync(int delayMs, CancellationToken cancellationToken)
        {
            var waiter = new TaskCompletionSource<bool>();
            Coroutine delayCoroutine = null;
            CancellationTokenRegistration registration = default;

            try
            {
                Debug.Log("[WebGLWebSocketRealtimeTransport] Browser disconnect unity delay started. handle=" + handle + " delayMs=" + delayMs);
                delayCoroutine = CoroutineRunner_A.Run(CompleteDisconnectDelayWithUnityCoroutine(delayMs, cancellationToken, waiter));

                if (cancellationToken.CanBeCanceled)
                {
                    registration = cancellationToken.Register(() =>
                    {
                        CoroutineRunner_A.Stop(delayCoroutine);
                        Debug.Log("[WebGLWebSocketRealtimeTransport] Browser disconnect unity delay canceled. handle=" + handle);
                        waiter.TrySetResult(false);
                    });
                }

                bool completed = await waiter.Task;
                if (completed) Debug.Log("[WebGLWebSocketRealtimeTransport] Browser disconnect unity delay completed. handle=" + handle);
            }
            finally
            {
                registration.Dispose();
                CoroutineRunner_A.Stop(delayCoroutine);
            }
        }

        //* کوروتین تاخیر دیسکانکت را فریم به فریم جلو می برد و لغو شدن عملیات را هم کنترل می کند.
        private IEnumerator CompleteDisconnectDelayWithUnityCoroutine(int delayMs, CancellationToken cancellationToken, TaskCompletionSource<bool> waiter)
        {
            float endTime = Time.realtimeSinceStartup + (Mathf.Max(0, delayMs) / 1000f);

            while (Time.realtimeSinceStartup < endTime)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    waiter.TrySetResult(false);
                    yield break;
                }

                yield return null;
            }

            waiter.TrySetResult(!cancellationToken.IsCancellationRequested);
        }

        //* رویداد onopen مرورگر را از Bridge دریافت می‌کند و اتصال را موفق اعلام می‌کند.
        internal void HandleBrowserOpen()
        {
            Debug.Log("[WebGLWebSocketRealtimeTransport] Browser open received. handle=" + handle);
            CleanupConnectWaiter();
            SetState(RealtimeTransportState.Connected);
            connectCompletionSource?.TrySetResult(true);
            Connected?.Invoke();
        }

        //* پیام دریافتی مرورگر را بدون تغییر به کُر ریل‌تایم می‌دهد.
        internal void HandleBrowserMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            MessageReceived?.Invoke(message);
        }

        //* خطای مرورگر را به رویداد خطای ترنسپورت تبدیل می‌کند.
        internal void HandleBrowserError(string errorMessage)
        {
            string safeMessage = string.IsNullOrWhiteSpace(errorMessage) ? "WebGL WebSocket browser error." : errorMessage;
            Debug.LogWarning("[WebGLWebSocketRealtimeTransport] Browser error received. handle=" + handle + " error=" + safeMessage);
            ErrorReceived?.Invoke(safeMessage);

            if (state == RealtimeTransportState.Connecting)
            {
                SetState(RealtimeTransportState.Failed);
                CleanupConnectWaiter();
                connectCompletionSource?.TrySetResult(false);
            }
        }

        //* بسته شدن WebSocket مرورگر را دریافت می‌کند و فقط یک‌بار رویداد قطع اتصال می‌فرستد.
        internal void HandleBrowserClose(int closeCode, string reason)
        {
            string safeReason = string.IsNullOrWhiteSpace(reason) ? "WebGL WebSocket closed. code=" + closeCode : reason;
            Debug.Log("[WebGLWebSocketRealtimeTransport] Browser close received. handle=" + handle + " code=" + closeCode + " reason=" + safeReason);

            if (state == RealtimeTransportState.Connecting)
            {
                SetState(RealtimeTransportState.Failed);
                CleanupConnectWaiter();
                connectCompletionSource?.TrySetResult(false);
            }

            CompleteDisconnect(safeReason);
            isDisconnecting = false;
        }

        //* لغو اتصال در حال انجام را از سمت CancellationToken مدیریت می‌کند.
        private void HandleConnectCanceled()
        {
            Debug.LogWarning("[WebGLWebSocketRealtimeTransport] Browser connect canceled. handle=" + handle);
            RealtimeWebGLWebSocketClose(handle, NormalCloseCode, "Connect canceled");
            CleanupConnectWaiter();
            connectCompletionSource?.TrySetResult(false);
        }

        //* اتصال ناموفق را ثبت می‌کند و خطا را به کُر گزارش می‌دهد.
        private bool FailConnect(string message)
        {
            CleanupConnectWaiter();
            SetState(RealtimeTransportState.Failed);
            ErrorReceived?.Invoke(message);
            Debug.LogWarning("[WebGLWebSocketRealtimeTransport] " + message);
            return false;
        }

        //* قطع اتصال را کامل می‌کند و از ارسال چندباره رویداد Disconnected جلوگیری می‌کند.
        private void CompleteDisconnect(string reason)
        {
            CleanupConnectWaiter();
            SetState(RealtimeTransportState.Disconnected);
            RealtimeWebGLWebSocketDispose(handle);

            if (disconnectedEventSent) return;
            disconnectedEventSent = true;
            Disconnected?.Invoke(string.IsNullOrWhiteSpace(reason) ? "WebGL WebSocket disconnected." : reason);
        }

        //* اطلاعات انتظار اتصال را پاک می‌کند تا CancellationToken قدیمی در اتصال بعدی دخالت نکند.
        private void CleanupConnectWaiter()
        {
            connectCancellationRegistration.Dispose();
        }

        //* وضعیت داخلی ترنسپورت را تغییر می‌دهد.
        private void SetState(RealtimeTransportState newState)
        {
            state = newState;
        }

        //* منابع ترنسپورت را هنگام خروج یا تعویض اتصال آزاد می‌کند.
        public void Dispose()
        {
            _ = DisconnectAsync("WebGLWebSocketRealtimeTransport disposed");
            WebGLWebSocketBridge.UnregisterTransport(handle);
        }

        [DllImport("__Internal")]
        private static extern int RealtimeWebGLWebSocketConnect(int handle, string url);

        [DllImport("__Internal")]
        private static extern int RealtimeWebGLWebSocketSend(int handle, string message);

        [DllImport("__Internal")]
        private static extern void RealtimeWebGLWebSocketClose(int handle, int code, string reason);

        [DllImport("__Internal")]
        private static extern int RealtimeWebGLWebSocketGetReadyState(int handle);

        [DllImport("__Internal")]
        private static extern int RealtimeWebGLWebSocketGetBrowserOnlineState();

        [DllImport("__Internal")]
        private static extern void RealtimeWebGLWebSocketDispose(int handle);
    }
}
#endif
