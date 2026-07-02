using System;
using Network_A.Realtime.Protocol;

namespace Network_A.Realtime.Stability
{
    //* تنظیمات ارسال قابل اطمینان را برای پیام هایی که نیاز به اَک دارند نگه می دارد.
    [Serializable]
    public class RealtimeReliableSendOptions
    {
        public int ackTimeoutMs = 5000;
        public int maxSendAttempts = 3;
        public int retryDelayMs = 300;
        public bool retryOnAckTimeout = true;
        public bool retryOnTransportSendFailed = true;

        //* مقدارهای نامعتبر تنظیمات اَک و ریتِرای را به مقدار امن تبدیل می کند.
        public void Normalize()
        {
            if (ackTimeoutMs <= 0) ackTimeoutMs = 5000;
            if (maxSendAttempts <= 0) maxSendAttempts = 1;
            if (retryDelayMs < 0) retryDelayMs = 0;
        }

        //* تنظیمات پیش فرض ارسال قابل اطمینان را می سازد.
        public static RealtimeReliableSendOptions Default()
        {
            return new RealtimeReliableSendOptions();
        }
    }

    //* نتیجه نهایی ارسال قابل اطمینان را برای کُر و تست ها نگه می دارد.
    [Serializable]
    public class RealtimeReliableSendResult
    {
        public bool isSuccess;
        public bool wasQueued;
        public bool wasDropped;
        public bool ackTimedOut;
        public bool wasCancelled;
        public int attempts;
        public string messageId = string.Empty;
        public string ackStatus = string.Empty;
        public string errorMessage = string.Empty;
        public RealtimeAck ack;

        //* نتیجه موفق ارسال و دریافت اَک را می سازد.
        public static RealtimeReliableSendResult Success(string messageId, int attempts, RealtimeAck ack)
        {
            return new RealtimeReliableSendResult
            {
                isSuccess = true,
                attempts = attempts,
                messageId = messageId ?? string.Empty,
                ack = ack,
                ackStatus = ack == null ? string.Empty : ack.status
            };
        }

        //* نتیجه صف شدن پیام را می سازد تا مصرف کننده بداند ارسال مستقیم انجام نشده است.
        public static RealtimeReliableSendResult Queued(string messageId)
        {
            return new RealtimeReliableSendResult
            {
                isSuccess = true,
                wasQueued = true,
                messageId = messageId ?? string.Empty
            };
        }

        //* نتیجه حذف کنترل شده پیام را می سازد.
        public static RealtimeReliableSendResult Dropped(string messageId, string errorMessage)
        {
            return new RealtimeReliableSendResult
            {
                isSuccess = false,
                wasDropped = true,
                messageId = messageId ?? string.Empty,
                errorMessage = errorMessage ?? string.Empty
            };
        }

        //* نتیجه شکست ارسال قابل اطمینان را می سازد.
        public static RealtimeReliableSendResult Failed(string messageId, int attempts, string errorMessage, bool ackTimedOut = false, bool wasCancelled = false)
        {
            return new RealtimeReliableSendResult
            {
                isSuccess = false,
                attempts = attempts,
                messageId = messageId ?? string.Empty,
                errorMessage = errorMessage ?? string.Empty,
                ackTimedOut = ackTimedOut,
                wasCancelled = wasCancelled
            };
        }
    }
}

//* این فایل تنظیمات و نتیجه ارسال قابل اطمینان ریل تایم را نگه می دارد.
//* هدف آن کنترل اَک تایم اوت، تلاش مجدد و گزارش شفاف نتیجه ارسال است.
