using System;
using Network_A.Realtime.Transport;

namespace Network_A.Realtime.Core
{
    [Serializable]
    public class RealtimeConfig
    {
        public string serverUrl = "ws://127.0.0.1:8080";
        public RealtimeTransportKind transportKind = RealtimeTransportKind.Auto;
        public int connectTimeoutMs = 10000;
        public int sendTimeoutMs = 10000;
        public bool autoAuthenticateAfterConnect = false;
        public bool logIncomingMessages = true;
        public bool logOutgoingMessages = true;

        //* یک کانفیگ پیش‌فرض برای تست لوکال وب‌سوکت می‌سازد.
        public static RealtimeConfig CreateLocalWebSocket()
        {
            return new RealtimeConfig
            {
                serverUrl = "ws://127.0.0.1:8080",
                transportKind = RealtimeTransportKind.WebSocket,
                connectTimeoutMs = 10000,
                sendTimeoutMs = 10000,
                autoAuthenticateAfterConnect = false,
                logIncomingMessages = true,
                logOutgoingMessages = true
            };
        }

        //* مقدارهای خالی یا نامعتبر کانفیگ را به مقدار امن تبدیل می‌کند.
        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(serverUrl)) serverUrl = "ws://127.0.0.1:8080";
            if (connectTimeoutMs <= 0) connectTimeoutMs = 10000;
            if (sendTimeoutMs <= 0) sendTimeoutMs = 10000;
        }
    }
}

//* این فایل تنظیمات اولیه کُر ریل‌تایم را نگه می‌دارد.
//* این فایل فقط انتخاب ترنسپورت و آدرس اتصال را مشخص می‌کند.
