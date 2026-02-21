using System;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Network.Core.Models
{
    /// <summary>
    /// مدل استاندارد برای تمام درخواست‌های شبکه
    /// این مدل تمام پارامترهای مورد نیاز برای یک درخواست را در خود جای می‌دهد
    /// </summary>
    [Serializable]
    public class RequestModel
    {
        #region فیلدهای اجباری

        /// <summary>
        /// متد HTTP (GET, POST, PUT, DELETE, PATCH)
        /// </summary>
        public HttpMethod Method { get; set; } = HttpMethod.GET;

        /// <summary>
        /// آدرس کامل درخواست (با پارامترهای query)
        /// مثال: "https://api.metaverse.gov.ir/v1/user/profile?userId=123"
        /// </summary>
        public string Url { get; set; } = string.Empty;

        #endregion

        #region فیلدهای اختیاری

        /// <summary>
        /// بدنه درخواست (برای POST/PUT/PATCH)
        /// می‌تواند رشته، بایت آرایه یا شیء سریالایز شده باشد
        /// </summary>
        public object Body { get; set; }

        /// <summary>
        /// هدرهای اضافی درخواست
        /// هدرهای پیش‌فرض (مانند Authorization) به صورت خودکار اضافه می‌شوند
        /// </summary>
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// پارامترهای Query برای اضافه شدن به آدرس
        /// این پارامترها به صورت خودکار به آدرس اضافه می‌شوند
        /// </summary>
        public Dictionary<string, string> QueryParams { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// تایم‌اوت درخواست به میلی‌ثانیه
        /// پیش‌فرض: 10000 (10 ثانیه)
        /// </summary>
        public int TimeoutMs { get; set; } = 10000;

        /// <summary>
        /// نوع محتوای بدنه (Content-Type)
        /// پیش‌فرض: "application/json"
        /// </summary>
        public string ContentType { get; set; } = "application/json";

        /// <summary>
        /// فرمت بدنه درخواست (برای یکپارچگی انتخاب نوع Body)
        /// برنامه‌نویس‌ها باید به جای بازی با ContentType،
        /// از SetJsonBody / SetFormBody / SetMultipartFormBody / SetBinaryBody استفاده کنند.
        /// </summary>
        public BodyFormat BodyFormat { get; private set; } = BodyFormat.None;

        /// <summary>
        /// شناسه یکتای درخواست برای دیباگ و ردیابی
        /// اگر خالی باشد، به صورت خودکار تولید می‌شود
        /// </summary>
        public string RequestId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// تگ‌های دلخواه برای دسته‌بندی درخواست‌ها
        /// مثال: "auth", "avatar", "world"
        /// </summary>
        public List<string> Tags { get; set; } = new List<string>();

        #endregion

        #region متدهای کمکی

        /// <summary>
        /// اضافه کردن هدر به درخواست
        /// </summary>
        public RequestModel AddHeader(string key, string value)
        {
            Headers[key] = value;
            return this;
        }

        /// <summary>
        /// اضافه کردن پارامتر Query به درخواست
        /// </summary>
        public RequestModel AddQueryParam(string key, string value)
        {
            QueryParams[key] = value;
            return this;
        }

        /// <summary>
        /// اضافه کردن تگ به درخواست
        /// </summary>
        public RequestModel AddTag(string tag)
        {
            if (!Tags.Contains(tag))
                Tags.Add(tag);
            return this;
        }

        /// <summary>
        /// تنظیم بدنه درخواست از نوع شیء (سریالایز به JSON)
        /// </summary>
        public RequestModel SetJsonBody(object obj)
        {
            Body = obj;
            ContentType = "application/json";
            BodyFormat = BodyFormat.Json;
            return this;
        }

        /// <summary>
        /// تنظیم بدنه درخواست از نوع فرم (Form UrlEncoded)
        /// </summary>
        public RequestModel SetFormBody(Dictionary<string, string> formData)
        {
            Body = formData;
            ContentType = "application/x-www-form-urlencoded";
            BodyFormat = BodyFormat.UrlEncoded;
            return this;
        }

        /// <summary>
        /// تنظیم بدنه درخواست از نوع Multipart Form-Data (مثل Postman form-data)
        /// </summary>
        public RequestModel SetMultipartFormBody(Dictionary<string, string> formData)
        {
            Body = formData;
            ContentType = "multipart/form-data";
            BodyFormat = BodyFormat.MultipartFormData;
            return this;
        }

        public RequestModel SetFormUrlEncodedBody(Dictionary<string, string> formData)
        {
            Body = formData;
            ContentType = "application/x-www-form-urlencoded";
            BodyFormat = BodyFormat.FormUrlEncoded;   // اگر enum شما این مقدار را ندارد، پایین‌تر گفتم چی کار کن
            return this;
        }

        /// <summary>
        /// تنظیم بدنه درخواست از نوع باینری
        /// </summary>
        public RequestModel SetBinaryBody(byte[] data)
        {
            Body = data;
            ContentType = "application/octet-stream";
            BodyFormat = BodyFormat.Binary;
            return this;
        }

        /// <summary>
        /// ایجاد کپی عمیق از درخواست
        /// </summary>
        public RequestModel Clone()
        {
            return new RequestModel
            {
                Method = this.Method,
                Url = this.Url,
                Body = this.Body,
                Headers = new Dictionary<string, string>(this.Headers),
                QueryParams = new Dictionary<string, string>(this.QueryParams),
                TimeoutMs = this.TimeoutMs,
                ContentType = this.ContentType,
                BodyFormat = this.BodyFormat, // ✅ حفظ فرمت بدنه
                RequestId = Guid.NewGuid().ToString("N"), // شناسه جدید
                Tags = new List<string>(this.Tags)
            };
        }

        #endregion
    }

    /// <summary>
    /// متدهای HTTP پشتیبانی‌شده
    /// </summary>
    public enum HttpMethod
    {
        GET,
        POST,
        PUT,
        DELETE,
        PATCH,
        HEAD,
        OPTIONS
    }

    /// <summary>
    /// فرمت‌های بدنه درخواست (برای یکپارچگی انتخاب Body)
    /// </summary>
    public enum BodyFormat
    {
        None,
        Json,
        UrlEncoded,
        MultipartFormData,
        Binary,
        FormUrlEncoded
    }
}
