using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Network_A.Realtime.Transport
{
    //* وضعیت ترنسپورت بلادرنگ را نگه می‌دارد تا لایه کُر بدون وابستگی به وب‌سوکت یا جی‌آرپی‌سی تصمیم بگیرد.
    public enum RealtimeTransportState
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
        Disconnecting = 3,
        Failed = 4
    }

    //* قرارداد مشترک تمام ترنسپورت‌های بلادرنگ است و فقط حمل پیام خام را مدیریت می‌کند.
    public interface IRealtimeTransport
    {
        event Action Connected;
        event Action<string> MessageReceived;
        event Action<string> ErrorReceived;
        event Action<string> Disconnected;

        RealtimeTransportKind Kind { get; }
        RealtimeTransportState State { get; }
        bool IsConnected { get; }

        //* اتصال ترنسپورت را با آدرس و هدرهای ورودی شروع می‌کند.
        Task<bool> ConnectAsync(string url, Dictionary<string, string> headers, CancellationToken cancellationToken = default);

        //* پیام آماده‌شده توسط کُر را بدون دخالت در منطق بازی ارسال می‌کند.
        Task<bool> SendAsync(string message, CancellationToken cancellationToken = default);

        //* اتصال فعال را با دلیل مشخص می‌بندد.
        Task DisconnectAsync(string reason = "Client disconnect", CancellationToken cancellationToken = default);
    }
}
