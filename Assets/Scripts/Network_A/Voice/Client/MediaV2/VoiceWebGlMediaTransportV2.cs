using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Network_A.Voice.Client.MediaV2
{
    public sealed class VoiceWebGlMediaTransportV2 : IVoiceMediaTransportV2
    {
        private static readonly Dictionary<int, VoiceWebGlMediaTransportV2> Transports = new Dictionary<int, VoiceWebGlMediaTransportV2>();
        private static int nextHandle = 1;
        private readonly int handle;
        private bool disposed;
        public event Action Connected;
        public event Action<byte[]> PacketReceived;
        public event Action<string> Failed;
        public event Action<string> Disconnected;
        public bool IsConnected { get; private set; }

        //* این سازنده یک شناسه داخلی جدا برای هر اتصال رسانه وب جی ال ایجاد می کند.
        public VoiceWebGlMediaTransportV2()
        {
            handle = nextHandle++; Transports[handle] = this;
        }

        //* این تابع یک وب سوکت باینری جدا از وب سوکت مسیر کنترل در مرورگر باز می کند.
        public Task<bool> ConnectAsync(string endpoint, CancellationToken cancellationToken)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (disposed || string.IsNullOrWhiteSpace(endpoint)) return Task.FromResult(false);
            VoiceWebGlMediaBridgeV2.EnsureExists(); int result = VoiceWebGlMediaNativeV2.Open(handle, endpoint.Trim(), VoiceWebGlMediaBridgeV2.ObjectName); return Task.FromResult(result == 1);
#else
            Failed?.Invoke("Voice media WebGL socket is available only in a WebGL player."); return Task.FromResult(false);
#endif
        }

        //* این تابع بسته باینری مسیر رسانه را روی وب سوکت جداگانه مرورگر می فرستد.
        public Task<bool> SendAsync(byte[] packet, CancellationToken cancellationToken)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!IsConnected || packet == null || packet.Length == 0) return Task.FromResult(false); return Task.FromResult(VoiceWebGlMediaNativeV2.Send(handle, packet, packet.Length) == 1);
#else
            return Task.FromResult(false);
#endif
        }

        //* این تابع فقط وب سوکت مسیر رسانه مرورگر را می بندد.
        public Task DisconnectAsync(string reason, CancellationToken cancellationToken)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            VoiceWebGlMediaNativeV2.Close(handle, string.IsNullOrWhiteSpace(reason) ? "media_disconnect" : reason.Trim());
#endif
            IsConnected = false; return Task.CompletedTask;
        }

        //* این تابع اتصال داخلی را از روی شناسه مرورگر پیدا می کند.
        internal static bool TryGet(int value, out VoiceWebGlMediaTransportV2 transport)
        {
            return Transports.TryGetValue(value, out transport);
        }

        //* این تابع باز شدن موفق وب سوکت رسانه را به مصرف کننده اعلام می کند.
        internal void NotifyOpen() { IsConnected = true; Connected?.Invoke(); }

        //* این تابع بسته دریافتی مرورگر را به مصرف کننده مسیر رسانه تحویل می دهد.
        internal void NotifyPacket(byte[] packet) { if (packet != null && packet.Length > 0) PacketReceived?.Invoke(packet); }

        //* این تابع خطای مرورگر را به مصرف کننده مسیر رسانه اعلام می کند.
        internal void NotifyError(string message) { Failed?.Invoke(string.IsNullOrWhiteSpace(message) ? "Voice media WebGL socket error." : message); }

        //* این تابع بسته شدن وب سوکت رسانه را به مصرف کننده اعلام می کند.
        internal void NotifyClosed(string reason) { IsConnected = false; Disconnected?.Invoke(string.IsNullOrWhiteSpace(reason) ? "remote_closed" : reason); }

        //* این تابع اتصال رسانه مرورگر را آزاد و از جدول داخلی حذف می کند.
        public void Dispose() { if (disposed) return; disposed = true; _ = DisconnectAsync("dispose", CancellationToken.None); Transports.Remove(handle); }
    }

    internal static class VoiceWebGlMediaNativeV2
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] internal static extern int VoiceMediaV2WebGlOpen(int handle, string url, string objectName);
        [DllImport("__Internal")] internal static extern int VoiceMediaV2WebGlSend(int handle, byte[] packet, int length);
        [DllImport("__Internal")] internal static extern void VoiceMediaV2WebGlClose(int handle, string reason);
        //* این تابع فراخوانی باز کردن وب سوکت را به پل مرورگر می فرستد.
        internal static int Open(int handle, string url, string objectName) { return VoiceMediaV2WebGlOpen(handle, url, objectName); }
        //* این تابع فراخوانی ارسال بسته را به پل مرورگر می فرستد.
        internal static int Send(int handle, byte[] packet, int length) { return VoiceMediaV2WebGlSend(handle, packet, length); }
        //* این تابع فراخوانی بستن وب سوکت را به پل مرورگر می فرستد.
        internal static void Close(int handle, string reason) { VoiceMediaV2WebGlClose(handle, reason); }
#else
        //* این تابع در پلتفرم غیر مرورگری مقدار ناموفق برمی گرداند.
        internal static int Open(int handle, string url, string objectName) { return 0; }
        //* این تابع در پلتفرم غیر مرورگری ارسال را غیرفعال نگه می دارد.
        internal static int Send(int handle, byte[] packet, int length) { return 0; }
        //* این تابع در پلتفرم غیر مرورگری کاری انجام نمی دهد.
        internal static void Close(int handle, string reason) { }
#endif
    }

    public sealed class VoiceWebGlMediaBridgeV2 : MonoBehaviour
    {
        public const string ObjectName = "VoiceWebGlMediaBridgeV2";
        private static VoiceWebGlMediaBridgeV2 instance;

        //* این تابع پل یکتای مرورگر را بدون نیاز به تنظیم در اینسپکتور می سازد.
        public static VoiceWebGlMediaBridgeV2 EnsureExists()
        {
            if (instance != null) return instance; GameObject target = GameObject.Find(ObjectName); if (target == null) target = new GameObject(ObjectName); instance = target.GetComponent<VoiceWebGlMediaBridgeV2>(); if (instance == null) instance = target.AddComponent<VoiceWebGlMediaBridgeV2>(); DontDestroyOnLoad(target); return instance;
        }

        //* این تابع باز شدن وب سوکت را از پل مرورگر دریافت می کند.
        public void HandleOpen(string value) { if (int.TryParse((value ?? string.Empty).Trim(), out int handle) && VoiceWebGlMediaTransportV2.TryGet(handle, out VoiceWebGlMediaTransportV2 transport)) transport.NotifyOpen(); }

        //* این تابع بسته دریافتی مرورگر را از متن انتقالی به آرایه بایت برمی گرداند.
        public void HandlePacket(string value)
        {
            if (!TrySplit(value, out int handle, out string payload) || !VoiceWebGlMediaTransportV2.TryGet(handle, out VoiceWebGlMediaTransportV2 transport)) return;
            try { transport.NotifyPacket(Convert.FromBase64String(payload)); } catch (Exception exception) { transport.NotifyError("Voice media WebGL packet failed: " + exception.Message); }
        }

        //* این تابع خطای وب سوکت را از پل مرورگر دریافت می کند.
        public void HandleError(string value) { if (TrySplit(value, out int handle, out string message) && VoiceWebGlMediaTransportV2.TryGet(handle, out VoiceWebGlMediaTransportV2 transport)) transport.NotifyError(message); }

        //* این تابع بسته شدن وب سوکت را از پل مرورگر دریافت می کند.
        public void HandleClose(string value) { if (TrySplit(value, out int handle, out string reason) && VoiceWebGlMediaTransportV2.TryGet(handle, out VoiceWebGlMediaTransportV2 transport)) transport.NotifyClosed(reason); }

        //* این تابع شناسه اتصال و متن همراه آن را از پیام پل مرورگر جدا می کند.
        private static bool TrySplit(string value, out int handle, out string payload)
        {
            handle = 0; payload = string.Empty; int separator = (value ?? string.Empty).IndexOf('|'); if (separator <= 0 || !int.TryParse(value.Substring(0, separator), out handle)) return false; payload = value.Substring(separator + 1); return true;
        }
    }
}
