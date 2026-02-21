using System;
using System.Collections.Generic;
using Assets.Scripts.Network.Core.Models;
namespace Assets.Scripts.Network.Core.Interfaces
{
    /// <summary>
    /// اینترفیس استاندارد برای تمام پاسخ‌های شبکه
    /// این اینترفیس اطمینان می‌دهد که تمام پاسخ‌ها ساختار یکسانی دارند
    /// </summary>
    public interface IResponse
    {
        /// <summary>
        /// موفقیت یا شکست درخواست
        /// </summary>
        bool IsSuccess { get; }

        /// <summary>
        /// کد وضعیت HTTP یا کد خطا سفارشی
        /// </summary>
        int StatusCode { get; }

        /// <summary>
        /// داده خام پاسخ به صورت رشته
        /// </summary>
        string RawData { get; }

        /// <summary>
        /// خطا در صورت وجود
        /// </summary>
        NetworkError Error { get; }

        /// <summary>
        /// زمان دریافت پاسخ (برای محاسبه تأخیر)
        /// </summary>
        DateTime ResponseTime { get; }

        /// <summary>
        /// هدرهای پاسخ (در صورت پشتیبانی توسط پروتکل)
        /// </summary>
        Dictionary<string, string> Headers { get; }

        /// <summary>
        /// تبدیل پاسخ به مدل داده‌ای مشخص
        /// </summary>
        /// <typeparam name="T">نوع مدل داده‌ای</typeparam>
        /// <returns>مدل داده‌ای تبدیل شده</returns>
        T GetData<T>() where T : class, new();
    }
}