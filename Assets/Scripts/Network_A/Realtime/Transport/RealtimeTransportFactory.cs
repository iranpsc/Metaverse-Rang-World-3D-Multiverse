using System;
using System.Collections.Generic;
using UnityEngine;

namespace Network_A.Realtime.Transport
{
    //* کارخانه ساخت ترنسپورت بلادرنگ است و انتخاب پلتفرمی را از ریل‌تایم‌کلاینت جدا می‌کند.
    public static class RealtimeTransportFactory
    {
        private static readonly Dictionary<RealtimeTransportKind, Func<IRealtimeTransport>> dict_TransportCreatorByKind = new Dictionary<RealtimeTransportKind, Func<IRealtimeTransport>>();

        //* سازنده ترنسپورت را ثبت می‌کند تا فازهای بعدی بدون تغییر ریل‌تایم‌کلاینت ترنسپورت واقعی اضافه کنند.
        public static void RegisterTransport(RealtimeTransportKind kind, Func<IRealtimeTransport> creator)
        {
            if (kind == RealtimeTransportKind.Auto) throw new ArgumentException("Auto transport cannot be registered.", nameof(kind));
            if (creator == null) throw new ArgumentNullException(nameof(creator));
            dict_TransportCreatorByKind[kind] = creator;
        }

        //* ترنسپورت مناسب را می‌سازد و اگر پیاده‌سازی هنوز ثبت نشده باشد، مقدار نال برمی‌گرداند.
        public static IRealtimeTransport Create(RealtimeTransportKind requestedKind)
        {
            RealtimeTransportKind resolvedKind = ResolveTransportKind(requestedKind);

            if (dict_TransportCreatorByKind.TryGetValue(resolvedKind, out Func<IRealtimeTransport> creator)) return creator();

            Debug.LogWarning("Realtime transport is not registered yet: " + resolvedKind);
            return null;
        }

        //* نوع ترنسپورت نهایی را بر اساس انتخاب ورودی و پلتفرم فعلی مشخص می‌کند.
        public static RealtimeTransportKind ResolveTransportKind(RealtimeTransportKind requestedKind)
        {
            if (requestedKind != RealtimeTransportKind.Auto) return requestedKind;

#if UNITY_WEBGL && !UNITY_EDITOR
            return RealtimeTransportKind.WebSocket;
#elif UNITY_STANDALONE_WIN || UNITY_ANDROID
            return RealtimeTransportKind.GrpcStreaming;
#else
            return RealtimeTransportKind.WebSocket;
#endif
        }

        //* بررسی می‌کند برای نوع ترنسپورت داده‌شده، سازنده واقعی ثبت شده است یا نه.
        public static bool HasRegisteredTransport(RealtimeTransportKind kind)
        {
            RealtimeTransportKind resolvedKind = ResolveTransportKind(kind);
            return dict_TransportCreatorByKind.ContainsKey(resolvedKind);
        }

        //* ثبت‌های فعلی کارخانه را پاک می‌کند و برای تست‌های ادیتور استفاده می‌شود.
        public static void ClearRegisteredTransports()
        {
            dict_TransportCreatorByKind.Clear();
        }
    }
}
