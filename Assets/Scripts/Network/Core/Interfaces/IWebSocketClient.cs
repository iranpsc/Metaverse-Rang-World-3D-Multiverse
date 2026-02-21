using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assets.Scripts.Network.Core.Interfaces
{
    /// <summary>
    /// اینترفیس استاندارد برای کلاینت WebSocket
    /// این اینترفیس مستقل از پیاده‌سازی واقعی (System.Net.WebSockets یا پیاده‌سازی سفارشی) است
    /// </summary>
    public interface IWebSocketClient
    {
        #region رویدادها (Events)

        /// <summary>
        /// رویداد اتصال موفق به سرور WebSocket
        /// </summary>
        event Action OnConnected;

        /// <summary>
        /// رویداد دریافت پیام از سرور
        /// </summary>
        event Action<string> OnMessageReceived;

        /// <summary>
        /// رویداد خطا در اتصال یا ارسال/دریافت
        /// </summary>
        event Action<string> OnError;

        /// <summary>
        /// رویداد قطع اتصال (عمدی یا ناخواسته)
        /// </summary>
        event Action OnDisconnected;

        /// <summary>
        /// رویداد بازاتصال موفق پس از قطعی
        /// </summary>
        event Action OnReconnected;

        #endregion

        #region متدهای اصلی

        /// <summary>
        /// اتصال به سرور WebSocket
        /// </summary>
        /// <param name="url">آدرس کامل WebSocket (wss:// یا ws://)</param>
        /// <param name="headers">هدرهای اضافی برای احراز هویت و متادیتا</param>
        /// <param name="cancellationToken">توکن لغو برای تایم‌اوت</param>
        /// <returns>وضعیت موفقیت‌آمیز بودن اتصال</returns>
        Task<bool> ConnectAsync(string url, Dictionary<string, string> headers = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// ارسال پیام به سرور
        /// </summary>
        /// <param name="message">پیام به صورت رشته (معمولاً JSON)</param>
        /// <param name="cancellationToken">توکن لغو</param>
        /// <returns>وضعیت موفقیت‌آمیز بودن ارسال</returns>
        Task<bool> SendAsync(string message, CancellationToken cancellationToken = default);

        /// <summary>
        /// قطع اتصال از سرور
        /// </summary>
        /// <param name="closeCode">کد بستن اتصال (پیش‌فرض: عادی)</param>
        /// <param name="reason">دلیل قطع اتصال برای سرور</param>
        void Disconnect(WebSocketCloseCode closeCode = WebSocketCloseCode.Normal, string reason = "Client disconnect");

        /// <summary>
        /// بررسی وضعیت اتصال فعلی
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// شناسه یکتای اتصال برای دیباگ
        /// </summary>
        string ConnectionId { get; }

        #endregion

        #region متدهای کمکی

        /// <summary>
        /// ارسال پیام با تأیید دریافت (Acknowledgment)
        /// برای پیام‌های حیاتی که نیاز به تأیید دارند
        /// </summary>
        Task<bool> SendWithAckAsync(string message, TimeSpan timeout, CancellationToken cancellationToken = default);

        /// <summary>
        /// دریافت وضعیت فعلی اتصال
        /// </summary>
        WebSocketConnectionState GetConnectionState();

        #endregion
    }

    /// <summary>
    /// وضعیت‌های ممکن برای اتصال WebSocket
    /// </summary>
    public enum WebSocketConnectionState
    {
        Disconnected,   // قطع شده
        Connecting,     // در حال اتصال
        Connected,      // متصل
        Reconnecting,   // در حال بازاتصال
        Closing         // در حال بسته شدن
    }

    /// <summary>
    /// کدهای استاندارد بستن اتصال WebSocket (RFC 6455)
    /// </summary>
    public enum WebSocketCloseCode
    {
        Normal = 1000,
        GoingAway = 1001,
        ProtocolError = 1002,
        UnsupportedData = 1003,
        InvalidPayloadData = 1007,
        PolicyViolation = 1008,
        MessageTooBig = 1009,
        InternalServerError = 1011,
        ServiceRestart = 1012,
        TryAgainLater = 1013,
        BadGateway = 1014,
        TLSHandshakeFailed = 1015
    }
}