using System;
using System.Collections.Generic;
using Assets.Scripts.Network.Core.Interfaces;
using UnityEngine;
using Assets.Scripts.Network.Core.Utils;

namespace Assets.Scripts.Network.Core.Models
{
    /// <summary>
    /// مدل استاندارد برای تمام پاسخ‌های شبکه
    /// این مدل پیاده‌سازی اینترفیس IResponse است
    /// </summary>
    public class ResponseModel : IResponse
    {
        #region پیاده‌سازی IResponse

        public bool IsSuccess { get; private set; }
        public int StatusCode { get; private set; }
        public string RawData { get; private set; } = string.Empty;
        public NetworkError Error { get; private set; }
        public DateTime ResponseTime { get; private set; }
        public Dictionary<string, string> Headers { get; private set; } = new Dictionary<string, string>();

        public T GetData<T>() where T : class, new()
        {
            if (string.IsNullOrEmpty(RawData))
                return new T();

            try
            {
                return JSONSerializer.Deserialize<T>(RawData);
            }
            catch (Exception ex)
            {
                Debug.LogError($"خطا در دی‌سریالایز کردن پاسخ به نوع {typeof(T).Name}: {ex.Message}");
                return new T();
            }
        }

        #endregion

        #region فیلدهای اضافی برای دیباگ

        /// <summary>
        /// زمان شروع درخواست (برای محاسبه کل تأخیر)
        /// </summary>
        public DateTime RequestStartTime { get; set; }

        /// <summary>
        /// زمان پایان درخواست (برای محاسبه کل تأخیر)
        /// </summary>
        public DateTime RequestEndTime { get; set; }

        /// <summary>
        /// تأخیر کل درخواست به میلی‌ثانیه
        /// </summary>
        public float TotalLatencyMs => (float)(RequestEndTime - RequestStartTime).TotalMilliseconds;

        /// <summary>
        /// شناسه درخواست مرتبط برای ردیابی
        /// </summary>
        public string RelatedRequestId { get; set; } = string.Empty;

        /// <summary>
        /// تگ‌های مرتبط با درخواست برای دسته‌بندی
        /// </summary>
        public List<string> Tags { get; set; } = new List<string>();

        #endregion

        #region سازنده‌ها و متدهای کارخانه

        /// <summary>
        /// سازنده پیش‌فرض
        /// </summary>
        public ResponseModel()
        {
            ResponseTime = DateTime.UtcNow;
        }

        /// <summary>
        /// ایجاد پاسخ موفق
        /// </summary>
        public static ResponseModel Success(string rawData, int statusCode = 200, Dictionary<string, string> headers = null)
        {
            return new ResponseModel
            {
                IsSuccess = true,
                StatusCode = statusCode,
                RawData = rawData,
                Headers = headers ?? new Dictionary<string, string>(),
                ResponseTime = DateTime.UtcNow
            };
        }

        /// <summary>
        /// ایجاد پاسخ خطا
        /// </summary>
        public static ResponseModel Failure(NetworkError error, string rawData = "", int statusCode = 0)
        {
            return new ResponseModel
            {
                IsSuccess = false,
                StatusCode = statusCode,
                RawData = rawData,
                Error = error,
                ResponseTime = DateTime.UtcNow
            };
        }

        /// <summary>
        /// ایجاد پاسخ تایم‌اوت
        /// </summary>
        public static ResponseModel Timeout(string requestId, int timeoutMs)
        {
            return Failure(
                new NetworkError
                {
                    Code = NetworkErrorCode.Timeout,
                    Message = $"درخواست با شناسه {requestId} پس از {timeoutMs}ms تایم‌اوت شد",
                    Details = $"RequestId: {requestId}, Timeout: {timeoutMs}ms"
                },
                "",
                0
            );
        }

        /// <summary>
        /// ایجاد پاسخ لغو شده
        /// </summary>
        public static ResponseModel Cancelled(string requestId)
        {
            return Failure(
                new NetworkError
                {
                    Code = NetworkErrorCode.Cancelled,
                    Message = $"درخواست با شناسه {requestId} لغو شد",
                    Details = $"RequestId: {requestId}"
                },
                "",
                0
            );
        }

        #endregion

        #region متدهای کمکی برای دیباگ

        /// <summary>
        /// نمایش خلاصه پاسخ برای لاگ
        /// </summary>
        public override string ToString()
        {
            return $"Response [Success={IsSuccess}, StatusCode={StatusCode}, Latency={TotalLatencyMs:F2}ms, RequestId={RelatedRequestId}]";
        }

        /// <summary>
        /// بررسی آیا پاسخ نیاز به رفرش توکن دارد
        /// </summary>
        public bool RequiresTokenRefresh()
        {
            return StatusCode == 401 &&
                   (Error?.Code == NetworkErrorCode.Unauthorized ||
                    RawData.Contains("token_expired", StringComparison.OrdinalIgnoreCase));
        }

        #endregion
    }
}