#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Voice.Client.Transport;
using UnityEngine;

namespace Network_A.Voice.Client.Transport.WebGL
{
    public sealed class VoiceWebGLPhase9ControlTransport : IDisposable
    {
        private const int ConnectTimeoutMs = 10000;
        private readonly VoiceWebGlSocketTransport innerTransport;
        private TaskCompletionSource<bool> connectCompletion;
        private bool disposed;

        public event Action<byte[]> PacketReceived;
        public event Action<string> Failed;
        public event Action<string> Disconnected;

        public bool IsConnected { get { return innerTransport.IsConnected; } }

        // این سازنده راه انتقال موجود مرورگر را بدون تغییر برای آزمون مستقل فاز نه آماده می کند.
        public VoiceWebGLPhase9ControlTransport()
        {
            innerTransport = new VoiceWebGlSocketTransport();
            innerTransport.Connected += HandleInnerConnected;
            innerTransport.PacketReceived += HandleInnerPacketReceived;
            innerTransport.Failed += HandleInnerFailed;
            innerTransport.Disconnected += HandleInnerDisconnected;
        }

        // این تابع ساخت وب سوکت کنترل را آغاز می کند و تا باز شدن واقعی اتصال مرورگر منتظر می ماند.
        public async Task<bool> ConnectAsync(string endpoint, CancellationToken cancellationToken)
        {
            if (disposed || string.IsNullOrWhiteSpace(endpoint)) return false;
            if (innerTransport.IsConnected) return true;

            TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
            connectCompletion = completion;

            bool started = await innerTransport.ConnectAsync(endpoint, cancellationToken);
            if (!started)
            {
                if (ReferenceEquals(connectCompletion, completion)) connectCompletion = null;
                return false;
            }

            if (innerTransport.IsConnected)
            {
                if (ReferenceEquals(connectCompletion, completion)) connectCompletion = null;
                return true;
            }

            using (CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task timeoutTask = Task.Delay(ConnectTimeoutMs, timeoutCts.Token);
                Task completedTask = await Task.WhenAny(completion.Task, timeoutTask);

                if (completedTask == completion.Task)
                {
                    timeoutCts.Cancel();
                    return await completion.Task;
                }
            }

            if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(connectCompletion, completion)) connectCompletion = null;
            await innerTransport.DisconnectAsync("phase9_control_connect_timeout", CancellationToken.None);
            return false;
        }

        // این تابع بسته کنترل را فقط پس از باز شدن واقعی سوکت مرورگر ارسال می کند.
        public Task<bool> SendAsync(byte[] packet, CancellationToken cancellationToken)
        {
            if (disposed || !innerTransport.IsConnected || packet == null || packet.Length == 0) return Task.FromResult(false);
            return innerTransport.SendAsync(packet, cancellationToken);
        }

        // این تابع سوکت کنترل آزمون فاز نه را با علت مشخص می بندد.
        public Task DisconnectAsync(string reason, CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool> completion = connectCompletion;
            connectCompletion = null;
            completion?.TrySetResult(false);
            return innerTransport.DisconnectAsync(reason, cancellationToken);
        }

        // این تابع باز شدن واقعی سوکت زیرین را به انتظار اتصال اعلام می کند.
        private void HandleInnerConnected()
        {
            TaskCompletionSource<bool> completion = connectCompletion;
            connectCompletion = null;
            completion?.TrySetResult(true);
            Debug.Log("VME2_PHASE9_WEBGL_CONTROL_SOCKET=OPEN");
        }

        // این تابع بسته دریافت شده را بدون تغییر به آزمون فاز نه تحویل می دهد.
        private void HandleInnerPacketReceived(byte[] packet)
        {
            PacketReceived?.Invoke(packet);
        }

        // این تابع خطای سوکت زیرین را به انتظار اتصال و آزمون فاز نه اعلام می کند.
        private void HandleInnerFailed(string message)
        {
            if (!innerTransport.IsConnected)
            {
                TaskCompletionSource<bool> completion = connectCompletion;
                connectCompletion = null;
                completion?.TrySetResult(false);
            }

            Failed?.Invoke(string.IsNullOrWhiteSpace(message) ? "phase9_control_socket_error" : message);
        }

        // این تابع بسته شدن سوکت زیرین را به انتظار اتصال و آزمون فاز نه اعلام می کند.
        private void HandleInnerDisconnected(string reason)
        {
            TaskCompletionSource<bool> completion = connectCompletion;
            connectCompletion = null;
            completion?.TrySetResult(false);
            Disconnected?.Invoke(string.IsNullOrWhiteSpace(reason) ? "phase9_control_remote_closed" : reason);
        }

        // این تابع همه اشتراک ها و راه انتقال زیرین را فقط یک بار آزاد می کند.
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            TaskCompletionSource<bool> completion = connectCompletion;
            connectCompletion = null;
            completion?.TrySetResult(false);

            innerTransport.Connected -= HandleInnerConnected;
            innerTransport.PacketReceived -= HandleInnerPacketReceived;
            innerTransport.Failed -= HandleInnerFailed;
            innerTransport.Disconnected -= HandleInnerDisconnected;
            innerTransport.Dispose();
        }
    }
}
#endif
