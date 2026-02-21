using System;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Network.WebSocket
{
    public class WebSocketWebGLBridge : MonoBehaviour
    {
        public static WebSocketWebGLBridge Instance { get; private set; }

        private readonly Dictionary<int, WebGLWebSocketTransport> transports = new();

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            gameObject.name = "WebSocketWebGLBridge";
            DontDestroyOnLoad(gameObject);
        }

        public void Register(int id, WebGLWebSocketTransport t)
        {
            transports[id] = t;
        }

        public void Unregister(int id)
        {
            transports.Remove(id);
        }

        // JS -> Unity: "id|payload"
        public void HandleMessage(string data)
        {
            var split = data.IndexOf('|');
            if (split <= 0) return;

            var idStr = data.Substring(0, split);
            var payload = data.Substring(split + 1);

            if (!int.TryParse(idStr, out var id)) return;
            if (transports.TryGetValue(id, out var t))
                t.Internal_OnMessage(payload);
        }

        // JS -> Unity: "id|error"
        public void HandleError(string data)
        {
            var split = data.IndexOf('|');
            if (split <= 0) return;

            var idStr = data.Substring(0, split);
            var payload = data.Substring(split + 1);

            if (!int.TryParse(idStr, out var id)) return;
            if (transports.TryGetValue(id, out var t))
                t.Internal_OnError(payload);
        }

        // JS -> Unity: "id|open"
        public void HandleOpen(string data)
        {
            var split = data.IndexOf('|');
            if (split <= 0) return;

            var idStr = data.Substring(0, split);
            if (!int.TryParse(idStr, out var id)) return;

            if (transports.TryGetValue(id, out var t))
                t.Internal_OnOpen();
        }

        // JS -> Unity: "id|code|reason"
        public void HandleClose(string data)
        {
            var parts = data.Split('|');
            if (parts.Length < 2) return;

            if (!int.TryParse(parts[0], out var id)) return;
            int code = 1000;
            if (parts.Length >= 2) int.TryParse(parts[1], out code);
            var reason = parts.Length >= 3 ? parts[2] : "closed";

            if (transports.TryGetValue(id, out var t))
                t.Internal_OnClose(code, reason);
        }
    }
}
