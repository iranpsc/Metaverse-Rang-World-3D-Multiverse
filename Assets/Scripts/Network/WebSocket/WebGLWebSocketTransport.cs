using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Assets.Scripts.Network.WebSocket
{
    public class WebGLWebSocketTransport : IWebSocketTransport
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern int WS_Create();
        [DllImport("__Internal")] private static extern void WS_Connect(int id, string url, string headersJson);
        [DllImport("__Internal")] private static extern void WS_Send(int id, string message);
        [DllImport("__Internal")] private static extern void WS_Close(int id, int code, string reason);
        [DllImport("__Internal")] private static extern void WS_Free(int id);
#else
        // Editor fallback (برای اینکه کامپایل شود)
        private static int WS_Create() => -1;
        private static void WS_Connect(int id, string url, string headersJson) { }
        private static void WS_Send(int id, string message) { }
        private static void WS_Close(int id, int code, string reason) { }
        private static void WS_Free(int id) { }
#endif

        public TransportState State { get; private set; } = TransportState.Disconnected;

        public event Action OnOpen;
        public event Action<string> OnTextMessage;
        public event Action<string> OnError;
        public event Action<int, string> OnClose;

        private int id = -1;

        public WebGLWebSocketTransport()
        {
            EnsureBridgeExists();
        }

        public Task ConnectAsync(string url, Dictionary<string, string> headers, CancellationToken ct)
        {
            if (State == TransportState.Connected || State == TransportState.Connecting)
                return Task.CompletedTask;

            State = TransportState.Connecting;

            id = WS_Create();
            WebSocketWebGLBridge.Instance.Register(id, this);

            // headers را به شکل JSON ساده می‌فرستیم
            string headersJson = MiniJson.Serialize(headers ?? new Dictionary<string, string>());

            WS_Connect(id, url, headersJson);

            // در WebGL، اتصال async واقعی با callback است. اینجا فقط “شروع” می‌کنیم.
            return Task.CompletedTask;
        }

        public Task SendTextAsync(string text, CancellationToken ct)
        {
            if (State != TransportState.Connected)
                throw new InvalidOperationException("Transport not connected.");

            WS_Send(id, text);
            return Task.CompletedTask;
        }

        public Task CloseAsync(int code, string reason, CancellationToken ct)
        {
            if (State == TransportState.Disconnected)
                return Task.CompletedTask;

            State = TransportState.Closing;
            WS_Close(id, code, reason ?? "close");
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            try
            {
                if (id >= 0)
                {
                    WebSocketWebGLBridge.Instance?.Unregister(id);
                    WS_Free(id);
                    id = -1;
                }
            }
            catch { }

            State = TransportState.Disconnected;
        }

        // ---------- Internal callbacks from Bridge ----------

        internal void Internal_OnOpen()
        {
            State = TransportState.Connected;
            OnOpen?.Invoke();
        }

        internal void Internal_OnMessage(string msg)
        {
            OnTextMessage?.Invoke(msg);
        }

        internal void Internal_OnError(string err)
        {
            OnError?.Invoke(err);
        }

        internal void Internal_OnClose(int code, string reason)
        {
            State = TransportState.Disconnected;
            OnClose?.Invoke(code, reason);
            Dispose();
        }

        private static void EnsureBridgeExists()
        {
            if (WebSocketWebGLBridge.Instance != null) return;

            var go = GameObject.Find("WebSocketWebGLBridge");
            if (go != null && go.TryGetComponent<WebSocketWebGLBridge>(out _))
                return;

            go = new GameObject("WebSocketWebGLBridge");
            go.AddComponent<WebSocketWebGLBridge>();
        }

        /// <summary>
        /// یک JSON serializer خیلی سبک برای Dictionary<string,string>
        /// (برای WebGL headers)
        /// </summary>
        private static class MiniJson
        {
            public static string Serialize(Dictionary<string, string> dict)
            {
                if (dict == null) return "{}";

                var sb = new System.Text.StringBuilder();
                sb.Append("{");
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(",");
                    first = false;

                    sb.Append("\"").Append(Escape(kv.Key)).Append("\":");
                    sb.Append("\"").Append(Escape(kv.Value)).Append("\"");
                }
                sb.Append("}");
                return sb.ToString();
            }

            private static string Escape(string s)
            {
                if (s == null) return "";
                return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
            }
        }
    }
}
