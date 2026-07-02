#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Network_A.Realtime.Transport
{
    //* بریج ثابت WebGL است و رویدادهای JavaScript WebSocket را به ترنسپورت درست در C# برمی‌گرداند.
    public class WebGLWebSocketBridge : MonoBehaviour
    {
        public const string BridgeObjectName = "RealtimeWebGLWebSocketBridge";

        private static WebGLWebSocketBridge instance;
        private static readonly Dictionary<int, WebGLWebSocketRealtimeTransport> dict_TransportByHandle = new Dictionary<int, WebGLWebSocketRealtimeTransport>();

        //* گیم‌آبجکت بریج را فقط یک‌بار می‌سازد تا JavaScript بتواند با SendMessage به Unity برگردد.
        public static WebGLWebSocketBridge EnsureExists()
        {
            if (instance != null) return instance;

            GameObject existingObject = GameObject.Find(BridgeObjectName);
            if (existingObject != null) instance = existingObject.GetComponent<WebGLWebSocketBridge>();

            if (instance == null)
            {
                GameObject bridgeObject = existingObject != null ? existingObject : new GameObject(BridgeObjectName);
                instance = bridgeObject.AddComponent<WebGLWebSocketBridge>();
                DontDestroyOnLoad(bridgeObject);
            }

            return instance;
        }

        //* ترنسپورت فعال را با شناسه اتصال ذخیره می‌کند تا Callback مرورگر به همان نمونه برسد.
        public static void RegisterTransport(int handle, WebGLWebSocketRealtimeTransport transport)
        {
            if (handle <= 0 || transport == null) return;
            EnsureExists();
            dict_TransportByHandle[handle] = transport;
        }

        //* ترنسپورت بسته‌شده را از رجیستری حذف می‌کند تا Callback قدیمی باعث نشتی نشود.
        public static void UnregisterTransport(int handle)
        {
            if (handle <= 0) return;
            dict_TransportByHandle.Remove(handle);
        }

        //* Callback باز شدن WebSocket مرورگر را دریافت می‌کند.
        public void HandleWebSocketOpen(string payload)
        {
            if (!TryGetTransportFromHandleText(payload, out WebGLWebSocketRealtimeTransport transport)) return;
            transport.HandleBrowserOpen();
        }

        //* Callback پیام دریافتی WebSocket مرورگر را دریافت و متن UTF-8 اصلی را به C# برمی‌گرداند.
        public void HandleWebSocketMessage(string payload)
        {
            if (!TrySplitPayload(payload, out int handle, out string encodedMessage)) return;
            if (!dict_TransportByHandle.TryGetValue(handle, out WebGLWebSocketRealtimeTransport transport)) return;
            transport.HandleBrowserMessage(DecodeBase64Utf8(encodedMessage));
        }

        //* Callback خطای WebSocket مرورگر را دریافت می‌کند.
        public void HandleWebSocketError(string payload)
        {
            if (!TrySplitPayload(payload, out int handle, out string encodedError)) return;
            if (!dict_TransportByHandle.TryGetValue(handle, out WebGLWebSocketRealtimeTransport transport)) return;
            transport.HandleBrowserError(DecodeBase64Utf8(encodedError));
        }

        //* Callback بسته شدن WebSocket مرورگر را دریافت می‌کند و code و reason را به ترنسپورت می‌دهد.
        public void HandleWebSocketClose(string payload)
        {
            if (!TrySplitClosePayload(payload, out int handle, out int closeCode, out string encodedReason)) return;
            if (!dict_TransportByHandle.TryGetValue(handle, out WebGLWebSocketRealtimeTransport transport)) return;
            transport.HandleBrowserClose(closeCode, DecodeBase64Utf8(encodedReason));
        }

        //* متن handle را به ترنسپورت ثبت‌شده تبدیل می‌کند.
        private static bool TryGetTransportFromHandleText(string handleText, out WebGLWebSocketRealtimeTransport transport)
        {
            transport = null;
            if (!int.TryParse(handleText, out int handle)) return false;
            return dict_TransportByHandle.TryGetValue(handle, out transport);
        }

        //* پِیلودهای دو بخشی JavaScript را به handle و متن Base64 تبدیل می‌کند.
        private static bool TrySplitPayload(string payload, out int handle, out string encodedValue)
        {
            handle = 0;
            encodedValue = string.Empty;
            if (string.IsNullOrEmpty(payload)) return false;

            int separatorIndex = payload.IndexOf('|');
            if (separatorIndex <= 0) return false;
            if (!int.TryParse(payload.Substring(0, separatorIndex), out handle)) return false;

            encodedValue = payload.Substring(separatorIndex + 1);
            return true;
        }

        //* پِیلود بسته شدن JavaScript را به handle و code و reason تبدیل می‌کند.
        private static bool TrySplitClosePayload(string payload, out int handle, out int closeCode, out string encodedReason)
        {
            handle = 0;
            closeCode = 0;
            encodedReason = string.Empty;
            if (string.IsNullOrEmpty(payload)) return false;

            string[] parts = payload.Split(new[] { '|' }, 3);
            if (parts.Length < 3) return false;
            if (!int.TryParse(parts[0], out handle)) return false;
            if (!int.TryParse(parts[1], out closeCode)) closeCode = 0;

            encodedReason = parts[2];
            return true;
        }

        //* متن Base64 ساخته‌شده در JavaScript را به رشته UTF-8 قابل استفاده در Unity تبدیل می‌کند.
        private static string DecodeBase64Utf8(string encodedValue)
        {
            if (string.IsNullOrEmpty(encodedValue)) return string.Empty;

            try
            {
                byte[] bytes = Convert.FromBase64String(encodedValue);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[WebGLWebSocketBridge] Base64 decode failed: " + ex.Message);
                return string.Empty;
            }
        }
    }
}
#endif
