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

        #region gRPC Streaming Realtime

        public string grpcStreamingHost = "dev-world-3d.metarang.com";
        public int grpcStreamingPort = 50052;
        public bool grpcStreamingUseTls = true;
        public string grpcStreamingServiceName = "metaverse.v1.realtime.RealtimeStreamService";
        public string grpcStreamingMethodName = "Open";
        public bool useGrpcStreamingForNative = true;
        public bool useGrpcStreamingInEditor = false;
        public int grpcConnectTimeoutMs = 15000;
        public int grpcSendTimeoutMs = 10000;
        public int grpcShutdownTimeoutMs = 3000;
        public int grpcReceiveLoopDelayMs = 1;

        #endregion

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

        #region gRPC Streaming Realtime

        //* یک کانفیگ پیش‌فرض برای تست لوکال جی‌آر‌پی‌سی اِستریمینگ می‌سازد.
        public static RealtimeConfig CreateLocalGrpcStreaming()
        {
            return new RealtimeConfig
            {
                serverUrl = "127.0.0.1:50051",
                transportKind = RealtimeTransportKind.GrpcStreaming,
                connectTimeoutMs = 15000,
                sendTimeoutMs = 10000,
                autoAuthenticateAfterConnect = false,
                logIncomingMessages = true,
                logOutgoingMessages = true,
                grpcStreamingHost = "127.0.0.1",
                grpcStreamingPort = 50051,
                grpcStreamingUseTls = false,
                useGrpcStreamingInEditor = true
            };
        }

        //* یک کانفیگ پیش‌فرض برای اتصال جی‌آر‌پی‌سی اِستریمینگ به سرور اصلی می‌سازد.
        public static RealtimeConfig CreateDedicatedGrpcStreaming()
        {
            return new RealtimeConfig
            {
                serverUrl = "grpcs://dev-world-3d.metarang.com:50052",
                transportKind = RealtimeTransportKind.GrpcStreaming,
                connectTimeoutMs = 15000,
                sendTimeoutMs = 10000,
                autoAuthenticateAfterConnect = false,
                logIncomingMessages = true,
                logOutgoingMessages = true,
                grpcStreamingHost = "dev-world-3d.metarang.com",
                grpcStreamingPort = 50052,
                grpcStreamingUseTls = true,
                useGrpcStreamingForNative = true
            };
        }

        //* مشخص می‌کند پلتفرم فعلی باید از جی‌آر‌پی‌سی اِستریمینگ استفاده کند یا نه.
        public bool ShouldUseGrpcStreamingForCurrentPlatform()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return false;
#elif UNITY_EDITOR
            return useGrpcStreamingInEditor;
#else
            return useGrpcStreamingForNative;
#endif
        }

        //* آدرس تارگت جی‌آر‌پی‌سی اِستریمینگ را برای کلاینت‌های مبتنی بر Channel آماده می‌کند.
        public string GetGrpcStreamingTarget()
        {
            return grpcStreamingHost + ":" + grpcStreamingPort;
        }

        //* آدرس کامل جی‌آر‌پی‌سی اِستریمینگ را با اسکیم برای GrpcChannel آماده می‌کند.
        public string GetGrpcStreamingAddress()
        {
            string scheme = grpcStreamingUseTls ? "https" : "http";
            return scheme + "://" + GetGrpcStreamingTarget();
        }

        //* مسیر کامل متد اِستریم ریل‌تایم را برای جی‌آر‌پی‌سی آماده می‌کند.
        public string GetGrpcStreamingMethod()
        {
            return "/" + grpcStreamingServiceName + "/" + grpcStreamingMethodName;
        }

        //* تنظیمات اندپوینت جی‌آر‌پی‌سی اِستریمینگ را بدون تغییر مسیر وب‌سوکت اعمال می‌کند.
        public void UseGrpcStreamingEndpoint(string host, int port, bool useTls)
        {
            if (!string.IsNullOrWhiteSpace(host)) grpcStreamingHost = host.Trim();
            if (port > 0) grpcStreamingPort = port;
            grpcStreamingUseTls = useTls;
        }

        #endregion

        //* مقدارهای خالی یا نامعتبر کانفیگ را به مقدار امن تبدیل می‌کند.
        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(serverUrl)) serverUrl = "ws://127.0.0.1:8080";
            if (connectTimeoutMs < 0) connectTimeoutMs = 10000;
            if (sendTimeoutMs <= 0) sendTimeoutMs = 10000;

            #region gRPC Streaming Realtime

            if (string.IsNullOrWhiteSpace(grpcStreamingHost)) grpcStreamingHost = "dev-world-3d.metarang.com";
            if (grpcStreamingPort <= 0) grpcStreamingPort = 50052;
            if (string.IsNullOrWhiteSpace(grpcStreamingServiceName)) grpcStreamingServiceName = "metaverse.v1.realtime.RealtimeStreamService";
            if (string.IsNullOrWhiteSpace(grpcStreamingMethodName)) grpcStreamingMethodName = "Open";
            if (grpcConnectTimeoutMs <= 0) grpcConnectTimeoutMs = 15000;
            if (grpcSendTimeoutMs <= 0) grpcSendTimeoutMs = 10000;
            if (grpcShutdownTimeoutMs < 0) grpcShutdownTimeoutMs = 3000;
            if (grpcReceiveLoopDelayMs < 0) grpcReceiveLoopDelayMs = 1;

            #endregion
        }
    }
}

//* این فایل تنظیمات اولیه کُر ریل‌تایم را نگه می‌دارد.
//* این فایل فقط انتخاب ترنسپورت و آدرس اتصال را مشخص می‌کند.
