namespace Network_A.Realtime.Stability
{
    //* سیاست ارسال پیام ریل‌تایم را مشخص می‌کند تا هر پیام بداند هنگام قطعی باید صف شود یا حذف شود.
    public enum RealtimeDeliveryPolicy
    {
        ReliableQueued = 0,
        ReliableNoQueue = 1,
        UnreliableLatestOnly = 2,
        UnreliableDropWhenDisconnected = 3
    }
}

//* این فایل سیاست ارسال پیام‌های ریل‌تایم را تعریف می‌کند.
//* پیام‌های مهم می‌توانند صف شوند، اما پیام‌های لحظه‌ای مثل وضعیت پلیر در زمان قطعی حذف می‌شوند.
