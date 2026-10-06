#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Network_A.Voice.Client.MediaV2.WebGL
{
    public sealed class VoiceWebGLPhase9MediaTransport : IDisposable
    {
        private const int ConnectTimeoutMs = 10000;
        private static readonly Dictionary<int, VoiceWebGLPhase9MediaTransport> Transports = new Dictionary<int, VoiceWebGLPhase9MediaTransport>();
        private static int nextHandle = 1;

        private readonly int handle;
        private TaskCompletionSource<bool> connectCompletion;
        private bool disposed;

        public event Action<byte[]> PacketReceived;
        public event Action<string> Failed;
        public event Action<string> Disconnected;

        public bool IsConnected { get; private set; }

        // این سازنده یک شناسه یکتا برای سوکت مستقل رسانه فاز نه ایجاد می کند.
        public VoiceWebGLPhase9MediaTransport()
        {
            handle = nextHandle++;
            Transports[handle] = this;
        }

        // این تابع سوکت مستقل رسانه را باز می کند و تا دریافت تایید واقعی مرورگر منتظر می ماند.
        public async Task<bool> ConnectAsync(string endpoint, CancellationToken cancellationToken)
        {
            if (disposed || string.IsNullOrWhiteSpace(endpoint)) return false;
            if (IsConnected) return true;

            VoiceWebGLPhase9MediaBridge.EnsureExists();

            TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
            connectCompletion = completion;

            int started = VoiceWebGLPhase9MediaNative.Open(handle, endpoint.Trim(), VoiceWebGLPhase9MediaBridge.ObjectName);
            if (started != 1)
            {
                if (ReferenceEquals(connectCompletion, completion)) connectCompletion = null;
                return false;
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
            VoiceWebGLPhase9MediaNative.Close(handle, "phase9_media_connect_timeout");
            return false;
        }

        // این تابع بسته باینری رسانه را فقط روی سوکت باز فاز نه ارسال می کند.
        public Task<bool> SendAsync(byte[] packet, CancellationToken cancellationToken)
        {
            if (disposed || !IsConnected || packet == null || packet.Length == 0) return Task.FromResult(false);
            return Task.FromResult(VoiceWebGLPhase9MediaNative.Send(handle, packet, packet.Length) == 1);
        }

        // این تابع سوکت مستقل رسانه فاز نه را با علت مشخص می بندد.
        public Task DisconnectAsync(string reason, CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool> completion = connectCompletion;
            connectCompletion = null;
            completion?.TrySetResult(false);

            VoiceWebGLPhase9MediaNative.Close(handle, string.IsNullOrWhiteSpace(reason) ? "phase9_media_disconnect" : reason.Trim());
            IsConnected = false;
            return Task.CompletedTask;
        }

        // این تابع نمونه ثبت شده را از روی شناسه سوکت مرورگر پیدا می کند.
        internal static bool TryGet(int value, out VoiceWebGLPhase9MediaTransport transport)
        {
            return Transports.TryGetValue(value, out transport);
        }

        // این تابع باز شدن واقعی سوکت رسانه را ثبت و انتظار اتصال را کامل می کند.
        internal void NotifyOpen()
        {
            if (disposed) return;
            IsConnected = true;

            TaskCompletionSource<bool> completion = connectCompletion;
            connectCompletion = null;
            completion?.TrySetResult(true);

            Debug.Log("VME2_PHASE9_WEBGL_MEDIA_SOCKET=OPEN");
        }

        // این تابع بسته باینری دریافت شده را به آزمون فاز نه تحویل می دهد.
        internal void NotifyPacket(byte[] packet)
        {
            if (packet != null && packet.Length > 0) PacketReceived?.Invoke(packet);
        }

        // این تابع خطای سوکت رسانه را به انتظار اتصال و آزمون فاز نه اعلام می کند.
        internal void NotifyError(string message)
        {
            if (!IsConnected)
            {
                TaskCompletionSource<bool> completion = connectCompletion;
                connectCompletion = null;
                completion?.TrySetResult(false);
            }

            Failed?.Invoke(string.IsNullOrWhiteSpace(message) ? "phase9_media_socket_error" : message);
        }

        // این تابع بسته شدن سوکت رسانه را ثبت و به آزمون فاز نه اعلام می کند.
        internal void NotifyClosed(string reason)
        {
            IsConnected = false;

            TaskCompletionSource<bool> completion = connectCompletion;
            connectCompletion = null;
            completion?.TrySetResult(false);

            Disconnected?.Invoke(string.IsNullOrWhiteSpace(reason) ? "phase9_media_remote_closed" : reason);
        }

        // این تابع همه منابع سوکت رسانه فاز نه را فقط یک بار آزاد می کند.
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            _ = DisconnectAsync("phase9_media_dispose", CancellationToken.None);
            Transports.Remove(handle);
        }
    }

    internal static class VoiceWebGLPhase9MediaNative
    {
        // این تابع بومی درخواست باز کردن سوکت رسانه را به پل مرورگر تحویل می دهد.
        [DllImport("__Internal")] private static extern int VME2Phase9WebGLMediaOpen(int handle, string url, string objectName);

        // این تابع بومی بسته باینری رسانه را به پل مرورگر تحویل می دهد.
        [DllImport("__Internal")] private static extern int VME2Phase9WebGLMediaSend(int handle, byte[] packet, int length);

        // این تابع بومی درخواست بستن سوکت رسانه را به پل مرورگر تحویل می دهد.
        [DllImport("__Internal")] private static extern void VME2Phase9WebGLMediaClose(int handle, string reason);

        // این تابع درخواست باز کردن سوکت رسانه را به پل مرورگر می فرستد.
        internal static int Open(int handle, string url, string objectName)
        {
            return VME2Phase9WebGLMediaOpen(handle, url, objectName);
        }

        // این تابع بسته باینری رسانه را به پل مرورگر می فرستد.
        internal static int Send(int handle, byte[] packet, int length)
        {
            return VME2Phase9WebGLMediaSend(handle, packet, length);
        }

        // این تابع درخواست بستن سوکت رسانه را به پل مرورگر می فرستد.
        internal static void Close(int handle, string reason)
        {
            VME2Phase9WebGLMediaClose(handle, reason);
        }
    }

    public sealed class VoiceWebGLPhase9MediaBridge : MonoBehaviour
    {
        public const string ObjectName = "VoiceWebGLPhase9MediaBridge";
        private static VoiceWebGLPhase9MediaBridge instance;

        // این تابع پل یکتای سوکت رسانه فاز نه را بدون تنظیم دستی ایجاد می کند.
        public static VoiceWebGLPhase9MediaBridge EnsureExists()
        {
            if (instance != null) return instance;

            GameObject target = GameObject.Find(ObjectName);
            if (target == null) target = new GameObject(ObjectName);

            instance = target.GetComponent<VoiceWebGLPhase9MediaBridge>();
            if (instance == null) instance = target.AddComponent<VoiceWebGLPhase9MediaBridge>();

            DontDestroyOnLoad(target);
            return instance;
        }

        // این تابع باز شدن سوکت مرورگر را به نمونه درست رسانه تحویل می دهد.
        public void HandleOpen(string value)
        {
            if (int.TryParse((value ?? string.Empty).Trim(), out int handle) && VoiceWebGLPhase9MediaTransport.TryGet(handle, out VoiceWebGLPhase9MediaTransport transport))
            {
                transport.NotifyOpen();
            }
        }

        // این تابع بسته دریافتی مرورگر را از متن انتقالی به آرایه بایت برمی گرداند.
        public void HandlePacket(string value)
        {
            if (!TrySplit(value, out int handle, out string payload)) return;
            if (!VoiceWebGLPhase9MediaTransport.TryGet(handle, out VoiceWebGLPhase9MediaTransport transport)) return;

            try
            {
                transport.NotifyPacket(Convert.FromBase64String(payload));
            }
            catch (Exception exception)
            {
                transport.NotifyError("phase9_media_packet_decode_failed:" + exception.Message);
            }
        }

        // این تابع خطای سوکت مرورگر را به نمونه درست رسانه تحویل می دهد.
        public void HandleError(string value)
        {
            if (TrySplit(value, out int handle, out string message) && VoiceWebGLPhase9MediaTransport.TryGet(handle, out VoiceWebGLPhase9MediaTransport transport))
            {
                transport.NotifyError(message);
            }
        }

        // این تابع بسته شدن سوکت مرورگر را به نمونه درست رسانه تحویل می دهد.
        public void HandleClose(string value)
        {
            if (TrySplit(value, out int handle, out string reason) && VoiceWebGLPhase9MediaTransport.TryGet(handle, out VoiceWebGLPhase9MediaTransport transport))
            {
                transport.NotifyClosed(reason);
            }
        }

        // این تابع شناسه سوکت و متن همراه آن را از پیام پل مرورگر جدا می کند.
        private static bool TrySplit(string value, out int handle, out string payload)
        {
            handle = 0;
            payload = string.Empty;

            int separator = (value ?? string.Empty).IndexOf('|');
            if (separator <= 0 || !int.TryParse(value.Substring(0, separator), out handle)) return false;

            payload = value.Substring(separator + 1);
            return true;
        }
    }
}
#endif
