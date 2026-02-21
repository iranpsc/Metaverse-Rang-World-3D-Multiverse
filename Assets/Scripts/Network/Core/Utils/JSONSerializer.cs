using System;
using System.Text;
using UnityEngine;

namespace Assets.Scripts.Network.Core.Utils
{
    /// <summary>
    /// سریالایزر سفارشی JSON برای بهینه‌سازی عملکرد و کاهش وابستگی به پکیج‌های خارجی
    /// این کلاس از JsonUtility داخلی Unity استفاده می‌کند اما لایه انتزاعی اضافه می‌کند
    /// </summary>
    public static class JSONSerializer
    {
        /// <summary>
        /// سریالایز کردن شیء به رشته JSON
        /// </summary>
        public static string Serialize<T>(T obj) where T : class
        {
            if (obj == null)
                return "null";

            try
            {
                string json = JsonUtility.ToJson(obj, true); // pretty print برای دیباگ

                // بررسی خطا در سریالایزیشن
                if (string.IsNullOrEmpty(json) || json == "{}")
                {
                    throw new InvalidOperationException($"سریالایزیشن شیء از نوع {typeof(T).Name} شکست خورد");
                }

                return json;
            }
            catch (Exception ex)
            {
                Debug.LogError($"خطا در سریالایز کردن به JSON: {ex.Message}\nشیء: {obj}");
                throw;
            }
        }

        /// <summary>
        /// دی‌سریالایز کردن رشته JSON به شیء
        /// </summary>
        public static T Deserialize<T>(string json) where T : class, new()
        {
            if (string.IsNullOrEmpty(json) || json == "null")
                return new T();

            try
            {
                // پاک‌سازی پیش‌پردازش (حذف کامنت‌ها، فرمت‌دهی)
                json = PreprocessJson(json);

                T obj = JsonUtility.FromJson<T>(json);

                // بررسی موفقیت دی‌سریالایزیشن
                if (obj == null)
                {
                    throw new InvalidOperationException($"دی‌سریالایزیشن رشته JSON به نوع {typeof(T).Name} شکست خورد");
                }

                return obj;
            }
            catch (Exception ex)
            {
                Debug.LogError($"خطا در دی‌سریالایز کردن از JSON: {ex.Message}\nJSON: {json.Substring(0, Mathf.Min(200, json.Length))}...");
                throw;
            }
        }

        /// <summary>
        /// پیش‌پردازش رشته JSON برای رفع مشکلات رایج
        /// </summary>
        private static string PreprocessJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return json;

            // حذف کاراکترهای کنترلی غیرمجاز (مثلاً از کپی‌پیست)
            json = RemoveInvalidCharacters(json);

            // تبدیل خطوط تکی به دابل (برای سازگاری با برخی سرورها)
            json = json.Replace("'", "\"");

            return json;
        }

        /// <summary>
        /// حذف کاراکترهای غیرمجاز از رشته JSON
        /// </summary>
        private static string RemoveInvalidCharacters(string input)
        {
            StringBuilder sb = new StringBuilder(input.Length);
            foreach (char c in input)
            {
                // مجاز: حروف، اعداد، کاراکترهای خاص JSON، و فاصله
                if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) ||
                    c == '"' || c == '\'' || c == ':' || c == ',' ||
                    c == '{' || c == '}' || c == '[' || c == ']' ||
                    c == '-' || c == '.' || c == '_' || c == '@' ||
                    c == '/' || c == '\\' || c == '+' || c == '=')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// تبدیل آرایه بایت به رشته JSON (برای فایل‌ها و داده‌های باینری)
        /// </summary>
        public static string SerializeByteArray(byte[] data, string fieldName = "data")
        {
            if (data == null || data.Length == 0)
                return $"{{\"{fieldName}\":\"\"}}";

            try
            {
                string base64 = Convert.ToBase64String(data);
                return $"{{\"{fieldName}\":\"{base64}\"}}";
            }
            catch (Exception ex)
            {
                Debug.LogError($"خطا در سریالایز کردن آرایه بایت: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// تبدیل رشته JSON حاوی داده باینری به آرایه بایت
        /// </summary>
        public static byte[] DeserializeByteArray(string json, string fieldName = "data")
        {
            if (string.IsNullOrEmpty(json))
                return new byte[0];

            try
            {
                // استخراج مقدار فیلد از JSON
                int startIndex = json.IndexOf($"\"{fieldName}\":\"") + fieldName.Length + 4;
                int endIndex = json.IndexOf("\"", startIndex);

                if (startIndex < 0 || endIndex < 0)
                    return new byte[0];

                string base64 = json.Substring(startIndex, endIndex - startIndex);
                return Convert.FromBase64String(base64);
            }
            catch (Exception ex)
            {
                Debug.LogError($"خطا در دی‌سریالایز کردن آرایه بایت: {ex.Message}");
                return new byte[0];
            }
        }
    }
}