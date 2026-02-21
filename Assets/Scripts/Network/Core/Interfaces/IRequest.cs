using System;
using System.Threading;
using System.Threading.Tasks;
using Assets.Scripts.Network.Core.Models;
namespace Assets.Scripts.Network.Core.Interfaces
{
    /// <summary>
    /// اینترفیس استاندارد برای تمام درخواست‌های شبکه
    /// این اینترفیس پایه‌ای برای پیاده‌سازی‌های مختلف (HTTP, WebSocket, gRPC) است
    /// </summary>
    public interface IRequest
    {
        /// <summary>
        /// ارسال درخواست به صورت همگام‌سازی‌نشده (Async)
        /// </summary>
        /// <param name="request">مدل درخواست حاوی تمام پارامترها</param>
        /// <param name="cancellationToken">توکن لغو برای مدیریت تایم‌اوت و لغو دستی</param>
        /// <returns>پاسخ استاندارد شده</returns>
        Task<IResponse> SendAsync(RequestModel request, CancellationToken cancellationToken = default);

        /// <summary>
        /// لغو درخواست در حال اجرا
        /// این متد برای پیاده‌سازی تایم‌اوت و لغو دستی استفاده می‌شود
        /// </summary>
        void Cancel();

        /// <summary>
        /// بررسی وضعیت درخواست
        /// </summary>
        RequestState State { get; }

        /// <summary>
        /// شناسه یکتای درخواست برای دیباگ و لاگ‌گیری
        /// </summary>
        string RequestId { get; }
    }

    /// <summary>
    /// وضعیت‌های ممکن برای یک درخواست
    /// </summary>
    public enum RequestState
    {
        Idle,           // درخواست آماده ارسال
        Sending,        // در حال ارسال
        Waiting,        // در حال انتظار برای پاسخ
        Completed,      // پاسخ دریافت شد
        Failed,         // خطا رخ داد
        Cancelled       // درخواست لغو شد
    }
}