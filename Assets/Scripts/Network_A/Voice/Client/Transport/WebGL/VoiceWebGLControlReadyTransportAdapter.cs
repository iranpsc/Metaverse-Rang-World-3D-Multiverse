#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Voice.Client.Transport;

namespace Network_A.Voice.Client.Transport.WebGL
{
    public sealed class VoiceWebGLControlReadyTransportAdapter : IVoiceClientTransport
    {
        private const int ConnectTimeoutMs = 15000;
        private readonly VoiceWebGlSocketTransport innerTransport;
        private TaskCompletionSource<bool> connectCompletion;
        private bool disposed;

        public event Action Connected;
        public event Action<byte[]> PacketReceived;
        public event Action<string> Failed;
        public event Action<string> Disconnected;

        public bool IsConnected { get { return innerTransport.IsConnected; } }

        // این سازنده راه انتقال موجود مرورگر را بدون تغییر درون یک لایه انتظار مستقل قرار می دهد.
        public VoiceWebGLControlReadyTransportAdapter()
        {
            innerTransport = new VoiceWebGlSocketTransport();
            innerTransport.Connected += HandleInnerConnected;
            innerTransport.PacketReceived += HandleInnerPacketReceived;
            innerTransport.Failed += HandleInnerFailed;
            innerTransport.Disconnected += HandleInnerDisconnected;
        }

        // این تابع ساخت اتصال را آغاز می کند و فقط پس از باز شدن واقعی سوکت مرورگر نتیجه موفق می دهد.
        public async Task<bool> ConnectAsync(string endpoint, CancellationToken cancellationToken)
        {
            if (disposed || string.IsNullOrWhiteSpace(endpoint)) return false;
            if (innerTransport.IsConnected) return true;

            TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
            connectCompletion = completion;
            UnityEngine.Debug.Log("VOICE_WEBGL_CONTROL_WAIT_OPEN=START | platform=WebGL");

            bool started = await innerTransport.ConnectAsync(endpoint, cancellationToken);
            if (!started)
            {
                if (ReferenceEquals(connectCompletion, completion)) connectCompletion = null;
                return false;
            }

            if (innerTransport.IsConnected)
            {
                if (ReferenceEquals(connectCompletion, completion)) connectCompletion = null;
                UnityEngine.Debug.Log("VOICE_WEBGL_CONTROL_TRANSPORT_READY=PASS | source=immediate_state");
                return true;
            }

            using (CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task timeoutTask = Task.Delay(ConnectTimeoutMs, timeoutCts.Token);
                Task completedTask = await Task.WhenAny(completion.Task, timeoutTask);

                if (completedTask == completion.Task)
                {
                    timeoutCts.Cancel();
                    bool ready = await completion.Task;
                    UnityEngine.Debug.Log("VOICE_WEBGL_CONTROL_TRANSPORT_READY=" + (ready ? "PASS" : "FAIL") + " | source=open_callback");
                    return ready;
                }
            }

            if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(connectCompletion, completion)) connectCompletion = null;
            UnityEngine.Debug.LogError("VOICE_WEBGL_CONTROL_WAIT_OPEN=FAIL | reason=timeout");
            await innerTransport.DisconnectAsync("webgl_control_connect_timeout", CancellationToken.None);
            return false;
        }

        // این تابع بسته را فقط از راه انتقالی می فرستد که باز شدن واقعی آن تایید شده است.
        public Task<bool> SendAsync(byte[] packet, CancellationToken cancellationToken)
        {
            if (disposed || !innerTransport.IsConnected) return Task.FromResult(false);
            return innerTransport.SendAsync(packet, cancellationToken);
        }

        // این تابع انتظار اتصال را پایان می دهد و همان راه انتقال مرورگر را می بندد.
        public Task DisconnectAsync(string reason, CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool> completion = connectCompletion;
            connectCompletion = null;
            completion?.TrySetResult(false);
            return innerTransport.DisconnectAsync(reason, cancellationToken);
        }

        // این تابع باز شدن واقعی راه انتقال مرورگر را به انتظار جاری و مصرف کننده اعلام می کند.
        private void HandleInnerConnected()
        {
            UnityEngine.Debug.Log("VOICE_WEBGL_CONTROL_INNER_OPEN=PASS | platform=WebGL");
            TaskCompletionSource<bool> completion = connectCompletion;
            connectCompletion = null;
            completion?.TrySetResult(true);
            Connected?.Invoke();
        }

        // این تابع بسته دریافتی را بدون تغییر به مصرف کننده بالادست تحویل می دهد.
        private void HandleInnerPacketReceived(byte[] packet)
        {
            PacketReceived?.Invoke(packet);
        }

        // این تابع خطای راه انتقال زیرین را به انتظار اتصال و مصرف کننده بالادست اعلام می کند.
        private void HandleInnerFailed(string message)
        {
            UnityEngine.Debug.LogError("VOICE_WEBGL_CONTROL_INNER_ERROR | message=" + message);
            if (!innerTransport.IsConnected)
            {
                TaskCompletionSource<bool> completion = connectCompletion;
                connectCompletion = null;
                completion?.TrySetResult(false);
            }

            Failed?.Invoke(message);
        }

        // این تابع بسته شدن راه انتقال زیرین را به انتظار اتصال و مصرف کننده بالادست اعلام می کند.
        private void HandleInnerDisconnected(string reason)
        {
            UnityEngine.Debug.LogWarning("VOICE_WEBGL_CONTROL_INNER_CLOSE | reason=" + reason);
            TaskCompletionSource<bool> completion = connectCompletion;
            connectCompletion = null;
            completion?.TrySetResult(false);
            Disconnected?.Invoke(reason);
        }

        // این تابع اشتراک ها و راه انتقال زیرین را فقط یک بار آزاد می کند.
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
