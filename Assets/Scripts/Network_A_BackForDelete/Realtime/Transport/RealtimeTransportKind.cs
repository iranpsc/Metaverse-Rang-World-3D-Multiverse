namespace Network_A.Realtime.Transport
{
    //* نوع ترنسپورت بلادرنگ را مشخص می‌کند تا کلاینت بدون شناختن جزئیات وب‌سوکت یا جی‌آرپی‌سی انتخاب انجام دهد.
    public enum RealtimeTransportKind
    {
        Auto = 0,
        WebSocket = 1,
        GrpcStreaming = 2
    }
}
