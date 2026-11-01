#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Realtime.Transport;
using UnityEngine;

namespace Network_A.Realtime.Stability.WebGL
{
    public static class RealtimeWebGLConnectAttemptTransportInstaller
    {
        //* این تابع پس از ثبت ترنسپرت اصلی، نمونه محافظت شده وب را در کارخانه جایگزین می کند.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            RealtimeTransportFactory.RegisterTransport(
                RealtimeTransportKind.WebSocket,
                () => new RealtimeWebGLConnectAttemptTransportAdapter());

            Debug.Log(
                "REALTIME_WEBGL_CONNECT_ATTEMPT_TRANSPORT=READY" +
                " | failedConnectDisconnectSuppressed=True" +
                " | establishedDisconnectPreserved=True" +
                " | windowsPathChanged=False" +
                " | questPathChanged=False");
        }
    }

    //* این آداپتر اجازه نمی دهد بسته شدن یک تلاش ناموفق، حلقه ریکانکت عمومی دوم ایجاد کند.
    public sealed class RealtimeWebGLConnectAttemptTransportAdapter :
        IRealtimeTransport,
        IDisposable
    {
        private readonly IRealtimeTransport innerTransport;
        private bool establishedConnectionObserved;
        private bool disposed;
        private int suppressedFailedConnectDisconnects;

        public event Action Connected;
        public event Action<string> MessageReceived;
        public event Action<string> ErrorReceived;
        public event Action<string> Disconnected;

        public RealtimeTransportKind Kind => innerTransport.Kind;
        public RealtimeTransportState State => innerTransport.State;
        public bool IsConnected => innerTransport.IsConnected;

        public RealtimeWebGLConnectAttemptTransportAdapter()
            : this(new WebGLWebSocketRealtimeTransport())
        {
        }

        internal RealtimeWebGLConnectAttemptTransportAdapter(
            IRealtimeTransport innerTransport)
        {
            this.innerTransport = innerTransport ??
                throw new ArgumentNullException(nameof(innerTransport));

            this.innerTransport.Connected += HandleInnerConnected;
            this.innerTransport.MessageReceived += HandleInnerMessageReceived;
            this.innerTransport.ErrorReceived += HandleInnerErrorReceived;
            this.innerTransport.Disconnected += HandleInnerDisconnected;
        }

        //* این تابع اتصال داخلی را اجرا می کند و نتیجه ناموفق را بدون ساخت رویداد قطع دوم برمی گرداند.
        public async Task<bool> ConnectAsync(
            string url,
            Dictionary<string, string> headers,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (innerTransport.IsConnected)
            {
                establishedConnectionObserved = true;
                return true;
            }

            establishedConnectionObserved = false;
            bool connected = await innerTransport.ConnectAsync(
                url,
                headers,
                cancellationToken);

            if (!connected)
            {
                Debug.LogWarning(
                    "REALTIME_WEBGL_CONNECT_ATTEMPT_TRANSPORT=CONNECT_FAILED" +
                    " | suppressedDisconnects=" +
                        suppressedFailedConnectDisconnects +
                    " | nestedReconnectPrevented=True" +
                    " | failedAttemptDisposed=True");

                // RealtimeClient releases only its interface reference after a
                // failed connect.  Dispose the WebGL-only wrapper here as well
                // so the bridge handle and browser callbacks cannot accumulate
                // across repeated offline/online cycles.
                Dispose();
            }

            return connected;
        }

        public Task<bool> SendAsync(
            string message,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return innerTransport.SendAsync(message, cancellationToken);
        }

        public Task DisconnectAsync(
            string reason = "Client disconnect",
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            return innerTransport.DisconnectAsync(reason, cancellationToken);
        }

        private void HandleInnerConnected()
        {
            if (disposed) return;

            establishedConnectionObserved = true;
            Connected?.Invoke();
        }

        private void HandleInnerMessageReceived(string message)
        {
            if (disposed) return;
            MessageReceived?.Invoke(message);
        }

        private void HandleInnerErrorReceived(string error)
        {
            if (disposed) return;
            ErrorReceived?.Invoke(error);
        }

        private void HandleInnerDisconnected(string reason)
        {
            if (disposed) return;

            if (!establishedConnectionObserved)
            {
                suppressedFailedConnectDisconnects += 1;

                Debug.LogWarning(
                    "REALTIME_WEBGL_CONNECT_ATTEMPT_TRANSPORT=FAILED_CONNECT_DISCONNECT_SUPPRESSED" +
                    " | count=" + suppressedFailedConnectDisconnects +
                    " | reason=" + Safe(reason) +
                    " | nestedReconnectPrevented=True");
                return;
            }

            establishedConnectionObserved = false;
            Disconnected?.Invoke(reason);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            innerTransport.Connected -= HandleInnerConnected;
            innerTransport.MessageReceived -= HandleInnerMessageReceived;
            innerTransport.ErrorReceived -= HandleInnerErrorReceived;
            innerTransport.Disconnected -= HandleInnerDisconnected;

            if (innerTransport is IDisposable disposable)
            {
                disposable.Dispose();
            }

            Connected = null;
            MessageReceived = null;
            ErrorReceived = null;
            Disconnected = null;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(
                    nameof(RealtimeWebGLConnectAttemptTransportAdapter));
            }
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().Replace("|", "/");
        }
    }
}
#endif
