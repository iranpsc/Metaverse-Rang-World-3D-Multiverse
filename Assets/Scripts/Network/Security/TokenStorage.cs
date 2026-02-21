using System;
using Assets.Scripts.Network.Security;

namespace Assets.Scripts.Network.Security
{
    /// <summary>
    /// اینترفیس استاندارد برای ذخیره‌سازی امن توکن‌ها
    /// این اینترفیس برای پیاده‌سازی‌های پلتفرمی مختلف استفاده می‌شود
    /// </summary>
    public interface ITokenStorage
    {
        /// <summary>
        /// ذخیره توکن‌ها به صورت ایمن
        /// </summary>
        void SaveTokens(string token, string refreshToken, int expiresIn);

        /// <summary>
        /// دریافت توکن دسترسی
        /// </summary>
        string GetToken();

        /// <summary>
        /// دریافت توکن رفرش
        /// </summary>
        string GetRefreshToken();

        /// <summary>
        /// بررسی اعتبار توکن (قبل از انقضا)
        /// </summary>
        bool IsTokenValid();

        /// <summary>
        /// پاک کردن تمام توکن‌ها (لاگ‌اوت)
        /// </summary>
        void ClearTokens();

        /// <summary>
        /// دریافت شناسه کاربر فعلی
        /// </summary>
       // string GetUserId();
    }
}