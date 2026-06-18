namespace Network_A.Realtime.Core
{
    //* وضعیت کُر ریل‌تایم را نگه می‌دارد تا یو‌آی و گیم‌سرور بدون شناختن ترنسپورت تصمیم بگیرند.
    public enum RealtimeConnectionState
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
        Authenticating = 3,
        Authenticated = 4,
        Disconnecting = 5,
        Failed = 6
    }
}

//* این فایل وضعیت‌های رسمی کُر ریل‌تایم را نگه می‌دارد.
//* این وضعیت‌ها مستقل از وب‌سوکت و جی‌آرپی‌سی هستند.
