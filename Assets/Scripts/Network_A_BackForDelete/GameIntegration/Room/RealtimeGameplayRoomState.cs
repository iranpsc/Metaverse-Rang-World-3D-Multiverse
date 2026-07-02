namespace Network_A.GameIntegration.Room
{
    //* وضعیت‌های اجرایی اتصال گیم‌پلی به روم ریل‌تایم را مشخص می‌کند.
    public enum RealtimeGameplayRoomState
    {
        Idle,
        Binding,
        Joining,
        Ready,
        Leaving,
        Stopped,
        Failed
    }
}
