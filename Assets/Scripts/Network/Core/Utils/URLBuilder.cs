using System;
using System.Collections.Generic;
using System.Text;
using Assets.Scripts.Network.Core.Models;
using UnityEngine;

namespace Assets.Scripts.Network.Core.Utils
{
    /// <summary>
    /// ساخت‌دهنده هوشمند آدرس‌های URL
    /// این کلاس برای ساخت آدرس‌های استاندارد و ایمن استفاده می‌شود
    /// </summary>
    public static class URLBuilder
    {
        /// <summary>
        /// سیاست پروژه:
        /// - در Production: فقط https/wss
        /// - در Development/Local: اجازه‌ی http/ws فقط برای localhost و 127.0.0.1
        /// </summary>
        public static bool AllowInsecureLocal
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// ساخت آدرس کامل با پارامترهای Query
        /// </summary>
        public static string Build(string baseUrl, string endpoint, Dictionary<string, string> queryParams = null)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("baseUrl نمی‌تواند خالی باشد", nameof(baseUrl));

            if (string.IsNullOrWhiteSpace(endpoint))
                throw new ArgumentException("endpoint نمی‌تواند خالی باشد", nameof(endpoint));

            // حذف اسلش اضافه در انتهای baseUrl
            if (baseUrl.EndsWith("/"))
                baseUrl = baseUrl.TrimEnd('/');

            // حذف اسلش اضافه در ابتدای endpoint
            if (endpoint.StartsWith("/"))
                endpoint = endpoint.TrimStart('/');

            var full = $"{baseUrl}/{endpoint}";

            // قبل از اضافه کردن query، validate کنیم
            if (!IsValidUrl(full))
                throw new ArgumentException($"Base URL/Endpoint نامعتبر است: {full}", nameof(baseUrl));

            // اضافه کردن پارامترهای Query
            if (queryParams != null && queryParams.Count > 0)
            {
                full = BuildWithQueryParams(full, queryParams);
            }

            return full;
        }

        /// <summary>
        /// ساخت آدرس از مدل درخواست
        /// </summary>
        public static string BuildFromRequest(RequestModel request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.Url))
                throw new ArgumentException("request.Url نمی‌تواند خالی باشد", nameof(request));

            // اگر آدرس کامل داده شده، فقط پارامترهای Query اضافه شود
            if (request.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                request.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                request.Url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
                request.Url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                var built = BuildWithQueryParams(request.Url, request.QueryParams);

                // validate نهایی
                if (!IsValidUrl(built))
                    throw new ArgumentException($"Request URL نامعتبر است: {built}", nameof(request));

                return built;
            }

            // در غیر این صورت، از محیط فعلی استفاده شود
            string baseUrl = EnvironmentConfig.Instance.GetApiBaseUrl();
            return Build(baseUrl, request.Url, request.QueryParams);
        }

        /// <summary>
        /// اضافه کردن پارامترهای Query به آدرس موجود
        /// </summary>
        private static string BuildWithQueryParams(string url, Dictionary<string, string> queryParams)
        {
            if (queryParams == null || queryParams.Count == 0)
                return url;

            bool hasExistingParams = url.Contains("?");
            StringBuilder result = new StringBuilder(url);
            bool firstParam = !hasExistingParams;

            foreach (var param in queryParams)
            {
                if (firstParam)
                {
                    result.Append("?");
                    firstParam = false;
                }
                else
                {
                    result.Append("&");
                }

                result.Append($"{Uri.EscapeDataString(param.Key)}={Uri.EscapeDataString(param.Value ?? string.Empty)}");
            }

            return result.ToString();
        }

        /// <summary>
        /// تبدیل URL http/https به ws/wss برای WebSocket
        /// - https -> wss
        /// - http -> ws
        /// اگر از قبل ws/wss بود، همان را برمی‌گرداند
        /// </summary>
        public static string ToWebSocketUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return url;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return url;

            var scheme = uri.Scheme?.ToLowerInvariant();
            if (scheme == "https") scheme = "wss";
            else if (scheme == "http") scheme = "ws";
            else if (scheme == "ws" || scheme == "wss") return url;

            var builder = new UriBuilder(uri) { Scheme = scheme };
            return builder.Uri.ToString();
        }

        /// <summary>
        /// اعتبارسنجی آدرس برای امنیت (جلوگیری از حملات)
        /// </summary>
        public static bool IsValidUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return false;

            // طول آدرس (جلوگیری از ورودی‌های عجیب)
            if (url.Length > 2048)
            {
                Debug.LogWarning("آدرس نامعتبر: طول آدرس بیش از حد مجاز (2048 کاراکتر) است.");
                return false;
            }

            // کاراکترهای ممنوعه
            if (url.Contains("..") || url.Contains("\\") || url.Contains("%00"))
            {
                Debug.LogWarning("آدرس نامعتبر: حاوی کاراکترهای ممنوعه است.");
                return false;
            }

            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    Debug.LogWarning("آدرس نامعتبر: URI قابل parse نیست.");
                    return false;
                }

                var scheme = uri.Scheme?.ToLowerInvariant();

                // --- حالت Production: فقط https/wss ---
                if (scheme == "https" || scheme == "wss")
                    return true;

                // --- حالت Dev/Local: فقط http/ws برای localhost ---
                if (AllowInsecureLocal && (scheme == "http" || scheme == "ws"))
                {
                    var host = uri.Host?.ToLowerInvariant();
                    if (host == "localhost" || host == "127.0.0.1")
                        return true;

                    Debug.LogWarning($"آدرس نامعتبر: {scheme} فقط برای localhost/127.0.0.1 مجاز است. host={uri.Host}");
                    return false;
                }

                Debug.LogWarning($"آدرس نامعتبر: پروتکل {uri.Scheme} مجاز نیست. فقط https/wss (و در dev لوکال http/ws) مجاز هستند.");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"آدرس نامعتبر: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// استخراج پارامترهای Query از آدرس
        /// </summary>
        public static Dictionary<string, string> ExtractQueryParams(string url)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(url))
                return result;

            int queryStart = url.IndexOf('?');
            if (queryStart == -1)
                return result;

            string query = url.Substring(queryStart + 1);
            string[] pairs = query.Split('&');

            foreach (string pair in pairs)
            {
                if (string.IsNullOrEmpty(pair))
                    continue;

                string[] parts = pair.Split(new[] { '=' }, 2);
                if (parts.Length == 2)
                {
                    string key = Uri.UnescapeDataString(parts[0]);
                    string value = parts[1].Length > 0 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
                    result[key] = value;
                }
                else if (parts.Length == 1 && !string.IsNullOrEmpty(parts[0]))
                {
                    // پارامتر بدون مقدار (مثل ?debug)
                    result[Uri.UnescapeDataString(parts[0])] = "true";
                }
            }

            return result;
        }
    }
}
