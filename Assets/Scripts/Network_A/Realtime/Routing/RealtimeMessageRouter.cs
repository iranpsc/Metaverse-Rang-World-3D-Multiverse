using System;
using System.Collections.Generic;
using Network_A.Realtime.Protocol;
using UnityEngine;

namespace Network_A.Realtime.Routing
{
    //* رُتِر پیام‌های ریل‌تایم است و پیام را بر اساس کانال و تایپ به هَندلِر مناسب می‌رساند.
    public class RealtimeMessageRouter
    {
        private readonly Dictionary<string, Action<RealtimeEnvelope>> dict_HandlerByRouteKey = new Dictionary<string, Action<RealtimeEnvelope>>();
        private Action<RealtimeEnvelope> fallbackHandler;

        //* هَندلِر یک مسیر مشخص را ثبت می‌کند.
        public void RegisterHandler(string channel, string messageType, Action<RealtimeEnvelope> handler)
        {
            if (string.IsNullOrWhiteSpace(channel)) throw new ArgumentException("Channel is required.", nameof(channel));
            if (string.IsNullOrWhiteSpace(messageType)) throw new ArgumentException("Message type is required.", nameof(messageType));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            string routeKey = BuildRouteKey(channel, messageType);
            dict_HandlerByRouteKey[routeKey] = handler;
        }

        //* هَندلِر مسیر مشخص را اگر وجود داشته باشد حذف می‌کند.
        public void UnregisterHandler(string channel, string messageType)
        {
            if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(messageType)) return;

            string routeKey = BuildRouteKey(channel, messageType);
            if (dict_HandlerByRouteKey.ContainsKey(routeKey)) dict_HandlerByRouteKey.Remove(routeKey);
        }

        //* هَندلِر جایگزین را ثبت می‌کند تا پیام‌های بدون مسیر هم قابل کنترل باشند.
        public void SetFallbackHandler(Action<RealtimeEnvelope> handler)
        {
            fallbackHandler = handler;
        }

        //* اِنولوپ ورودی را به مسیر درست می‌فرستد و نتیجه رُت شدن را برمی‌گرداند.
        public bool Route(RealtimeEnvelope envelope)
        {
            if (envelope == null || !envelope.IsValidBasic()) return false;

            string routeKey = BuildRouteKey(envelope.ch, envelope.t);
            if (dict_HandlerByRouteKey.TryGetValue(routeKey, out Action<RealtimeEnvelope> handler))
            {
                handler.Invoke(envelope);
                return true;
            }

            if (fallbackHandler != null)
            {
                fallbackHandler.Invoke(envelope);
                return true;
            }

            Debug.LogWarning("[RealtimeMessageRouter] No handler for route: " + routeKey);
            return false;
        }

        //* بررسی می‌کند مسیر داده‌شده هَندلِر ثبت‌شده دارد یا نه.
        public bool HasHandler(string channel, string messageType)
        {
            if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(messageType)) return false;
            return dict_HandlerByRouteKey.ContainsKey(BuildRouteKey(channel, messageType));
        }

        //* همه مسیرهای ثبت‌شده را پاک می‌کند و برای تست یا ریست کُر استفاده می‌شود.
        public void ClearHandlers()
        {
            dict_HandlerByRouteKey.Clear();
            fallbackHandler = null;
        }

        //* کلید داخلی رُت را از کانال و تایپ پیام می‌سازد.
        private static string BuildRouteKey(string channel, string messageType)
        {
            return (channel ?? string.Empty).Trim().ToLowerInvariant() + "/" + (messageType ?? string.Empty).Trim().ToLowerInvariant();
        }
    }
}

//* این فایل رُت کردن پیام‌های اِنولوپ‌شده را مدیریت می‌کند.
//* این فایل هیچ وابستگی مستقیمی به وب‌سوکت یا جی‌آرپی‌سی ندارد.
