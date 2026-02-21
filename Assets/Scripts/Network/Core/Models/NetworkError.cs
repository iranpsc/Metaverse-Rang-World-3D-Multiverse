using System;

namespace Assets.Scripts.Network.Core.Models
{
    /// <summary>
    /// مدل استاندارد برای خطاهای شبکه
    /// این مدل تمام اطلاعات مورد نیاز برای مدیریت خطا را فراهم می‌کند
    /// </summary>
    [Serializable]
    public class NetworkError
    {
        /// <summary>
        /// کد خطا استاندارد
        /// </summary>
        public NetworkErrorCode Code { get; set; }

        /// <summary>
        /// پیام خطا برای نمایش به کاربر یا توسعه‌دهنده
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// جزئیات فنی خطا برای دیباگ
        /// </summary>
        public string Details { get; set; } = string.Empty;

        /// <summary>
        /// استثنا اصلی در صورت وجود
        /// </summary>
        public Exception OriginalException { get; set; }

        /// <summary>
        /// شناسه درخواست مرتبط با خطا
        /// </summary>
        public string RequestId { get; set; } = string.Empty;

        /// <summary>
        /// زمان رخ دادن خطا
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// سازنده با پارامترها
        /// </summary>
        public NetworkError(NetworkErrorCode code, string message, string details = "", Exception exception = null)
        {
            Code = code;
            Message = message;
            Details = details;
            OriginalException = exception;
        }

        /// <summary>
        /// سازنده پیش‌فرض
        /// </summary>
        public NetworkError() { }

        /// <summary>
        /// نمایش خلاصه خطا برای لاگ
        /// </summary>
        public override string ToString()
        {
            return $"NetworkError [{Code}] {Message} | {Details}";
        }
    }

    /// <summary>
    /// کدهای استاندارد خطا برای سیستم شبکه
    /// </summary>
    public enum NetworkErrorCode
    {
        // خطاهای عمومی
        Unknown = 0,
        ConnectionFailed = 1,
        Timeout = 2,
        Cancelled = 3,
        InvalidRequest = 4,

        // خطاهای احراز هویت
        Unauthorized = 100,
        TokenExpired = 101,
        InvalidToken = 102,
        Forbidden = 103,

        // خطاهای سرور
        ServerError = 200,
        ServiceUnavailable = 201,
        RateLimited = 202,
        MaintenanceMode = 203,

        // خطاهای داده
        InvalidResponse = 300,
        SerializationError = 301,
        DeserializationError = 302,

        // خطاهای پلتفرمی
        PlatformNotSupported = 400,
        PermissionDenied = 401,
        StorageError = 402,

        // خطاهای WebSocket
        WebSocketConnectionFailed = 500,
        WebSocketDisconnected = 501,
        WebSocketMessageTooLarge = 502,
        WebSocketProtocolError = 503
    }
}