using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Network_A.Voice.Client.Transport
{
    public sealed class VoiceWebGlSocketTransport : IVoiceClientTransport
    {
        private static readonly Dictionary<int, VoiceWebGlSocketTransport> Transports =
            new Dictionary<int, VoiceWebGlSocketTransport>();

        private static int nextHandle = 1;
        private readonly int handle;
        private bool disposed;

        public event Action Connected;
        public event Action<byte[]> PacketReceived;
        public event Action<string> Failed;
        public event Action<string> Disconnected;

        public bool IsConnected { get; private set; }

        public VoiceWebGlSocketTransport()
        {
            handle = nextHandle++;
            Transports[handle] = this;
        }

        //* این تابع WebSocket باینری مرورگر را از طریق Bridge جاوااسکریپت باز می‌کند.
        public Task<bool> ConnectAsync(string endpoint, CancellationToken cancellationToken)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (disposed || string.IsNullOrWhiteSpace(endpoint)) return Task.FromResult(false);
            VoiceWebGlSocketBridge.EnsureExists();
            int result = VoiceWebGlNative.Open(handle, endpoint.Trim(), VoiceWebGlSocketBridge.ObjectName);
            return Task.FromResult(result == 1);
#else
            Failed?.Invoke("Voice WebGL socket is available only in a WebGL player.");
            return Task.FromResult(false);
#endif
        }

        //* این تابع Envelope خام را به WebSocket باینری مرورگر می‌فرستد.
        public Task<bool> SendAsync(byte[] packet, CancellationToken cancellationToken)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (!IsConnected || packet == null || packet.Length == 0) return Task.FromResult(false);
            return Task.FromResult(VoiceWebGlNative.Send(handle, packet, packet.Length) == 1);
#else
            return Task.FromResult(false);
#endif
        }

        //* این تابع WebSocket مرورگر را با علت مشخص می‌بندد.
        public Task DisconnectAsync(string reason, CancellationToken cancellationToken)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            VoiceWebGlNative.Close(handle, string.IsNullOrWhiteSpace(reason) ? "voice_disconnect" : reason.Trim());
#endif
            IsConnected = false;
            return Task.CompletedTask;
        }

        internal static bool TryGet(int handleValue, out VoiceWebGlSocketTransport transport)
        {
            return Transports.TryGetValue(handleValue, out transport);
        }

        internal void NotifyOpen()
        {
            IsConnected = true;
            Connected?.Invoke();
        }

        internal void NotifyPacket(byte[] packet)
        {
            if (packet != null && packet.Length > 0) PacketReceived?.Invoke(packet);
        }

        internal void NotifyError(string message)
        {
            Failed?.Invoke(string.IsNullOrWhiteSpace(message) ? "Voice WebGL socket error." : message);
        }

        internal void NotifyClosed(string reason)
        {
            IsConnected = false;
            Disconnected?.Invoke(string.IsNullOrWhiteSpace(reason) ? "remote_closed" : reason);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            _ = DisconnectAsync("dispose", CancellationToken.None);
            Transports.Remove(handle);
        }
    }

    internal static class VoiceWebGlNative
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] internal static extern int VoiceWebGlOpen(int handle, string url, string objectName);
        [DllImport("__Internal")] internal static extern int VoiceWebGlSend(int handle, byte[] packet, int length);
        [DllImport("__Internal")] internal static extern void VoiceWebGlClose(int handle, string reason);

        internal static int Open(int handle, string url, string objectName) { return VoiceWebGlOpen(handle, url, objectName); }
        internal static int Send(int handle, byte[] packet, int length) { return VoiceWebGlSend(handle, packet, length); }
        internal static void Close(int handle, string reason) { VoiceWebGlClose(handle, reason); }
#else
        internal static int Open(int handle, string url, string objectName) { return 0; }
        internal static int Send(int handle, byte[] packet, int length) { return 0; }
        internal static void Close(int handle, string reason) { }
#endif
    }

    public sealed class VoiceWebGlSocketBridge : MonoBehaviour
    {
        public const string ObjectName = "VoiceWebGlSocketBridge";
        private static VoiceWebGlSocketBridge instance;

        //* این تابع Bridge واحد و ماندگار WebGL را بدون Inspector ایجاد می‌کند.
        public static VoiceWebGlSocketBridge EnsureExists()
        {
            if (instance != null) return instance;
            GameObject bridgeObject = GameObject.Find(ObjectName);
            if (bridgeObject == null) bridgeObject = new GameObject(ObjectName);
            instance = bridgeObject.GetComponent<VoiceWebGlSocketBridge>();
            if (instance == null) instance = bridgeObject.AddComponent<VoiceWebGlSocketBridge>();
            DontDestroyOnLoad(bridgeObject);
            return instance;
        }

        public void HandleOpen(string value)
        {
            if (TryReadHandle(value, out int handle) && VoiceWebGlSocketTransport.TryGet(handle, out VoiceWebGlSocketTransport transport))
                transport.NotifyOpen();
        }

        public void HandlePacket(string value)
        {
            if (!TrySplit(value, out int handle, out string payload)) return;
            if (!VoiceWebGlSocketTransport.TryGet(handle, out VoiceWebGlSocketTransport transport)) return;
            try { transport.NotifyPacket(Convert.FromBase64String(payload)); }
            catch (Exception exception) { transport.NotifyError("Voice WebGL Base64 packet failed: " + exception.Message); }
        }

        public void HandleError(string value)
        {
            if (TrySplit(value, out int handle, out string message) && VoiceWebGlSocketTransport.TryGet(handle, out VoiceWebGlSocketTransport transport))
                transport.NotifyError(message);
        }

        public void HandleClose(string value)
        {
            if (TrySplit(value, out int handle, out string reason) && VoiceWebGlSocketTransport.TryGet(handle, out VoiceWebGlSocketTransport transport))
                transport.NotifyClosed(reason);
        }

        private static bool TryReadHandle(string value, out int handle)
        {
            return int.TryParse((value ?? string.Empty).Trim(), out handle);
        }

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

/*
توضیح فایل:
این فایل Transport باینری WSS مخصوص WebGL و Bridge خودکار Callbackهای مرورگر را بدون نیاز به Inspector پیاده می‌کند.
*/
