using System.Collections.Generic;
using Assets.Scripts.Network.Core.Models;
using Assets.Scripts.Network.Security;
using UnityEngine;

namespace Assets.Scripts.Network.HTTP
{
    /// <summary>
    /// مدیریت هدرهای پیش‌فرض و سفارشی برای درخواست‌های HTTP
    /// این کلاس مسئولیت تزریق خودکار توکن و هدرهای استاندارد را بر عهده دارد
    /// </summary>
    public class HTTPHeadersManager
    {
        private readonly Dictionary<string, string> defaultHeaders = new Dictionary<string, string>();
        private readonly AuthManager authManager;

        public HTTPHeadersManager(AuthManager authManager)
        {
            this.authManager = authManager;
            InitializeDefaultHeaders();
        }

        /// <summary>
        /// مقداردهی اولیه هدرهای پیش‌فرض
        /// </summary>
        private void InitializeDefaultHeaders()
        {
            defaultHeaders["Content-Type"] = "application/json";
            defaultHeaders["Accept"] = "application/json";
            defaultHeaders["X-Metaverse-Client"] = Application.platform.ToString();
            defaultHeaders["X-Metaverse-Version"] = Application.version;
            defaultHeaders["X-Device-Fingerprint"] = Security.CryptoService.GenerateDeviceFingerprint();
        }

        /// <summary>
        /// دریافت تمام هدرهای نهایی برای یک درخواست
        /// شامل هدرهای پیش‌فرض + هدرهای سفارشی + توکن احراز هویت (اگر موجود باشد)
        /// </summary>
        public Dictionary<string, string> GetHeaders(RequestModel request)
        {
            var headers = new Dictionary<string, string>(defaultHeaders);

            // اضافه کردن هدرهای سفارشی درخواست
            if (request.Headers != null)
            {
                foreach (var header in request.Headers)
                    headers[header.Key] = header.Value;
            }

            // ✅ Content-Type را از خود RequestModel بگیر
            // ⚠️ برای multipart نباید دستی ست شود (Unity boundary می‌سازد)
            if (!string.IsNullOrEmpty(request.ContentType))
            {
                if (request.ContentType == "multipart/form-data")
                {
                    headers.Remove("Content-Type");
                }
                else
                {
                    headers["Content-Type"] = request.ContentType;
                }
            }

            // تزریق خودکار توکن احراز هویت (اگر درخواست نیاز به احراز هویت داشته باشد)
            if (ShouldIncludeAuthToken(request))
            {
                string token = authManager?.GetAuthToken();
                if (!string.IsNullOrEmpty(token))
                    headers["Authorization"] = $"Bearer {token}";
            }

            return headers;
        }

        /// <summary>
        /// بررسی آیا درخواست نیاز به توکن احراز هویت دارد یا خیر
        /// </summary>
        private bool ShouldIncludeAuthToken(RequestModel request)
        {
            // برخی مسیرها نیاز به احراز هویت ندارند
            string[] publicEndpoints = new string[]
            {
                "oauth/token",
                "auth/register",
                "auth/refresh",
            };

            foreach (string publicEndpoint in publicEndpoints)
            {
                if (request.Url.Contains(publicEndpoint))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// اضافه کردن هدر پیش‌فرض جدید
        /// </summary>
        public void AddDefaultHeader(string key, string value)
        {
            defaultHeaders[key] = value;
        }

        /// <summary>
        /// حذف هدر پیش‌فرض
        /// </summary>
        public void RemoveDefaultHeader(string key)
        {
            if (defaultHeaders.ContainsKey(key))
                defaultHeaders.Remove(key);
        }

        /// <summary>
        /// پاک کردن تمام هدرهای پیش‌فرض
        /// </summary>
        public void ClearDefaultHeaders()
        {
            defaultHeaders.Clear();
            InitializeDefaultHeaders(); // بازنشانی به مقادیر پیش‌فرض
        }
    }
}
