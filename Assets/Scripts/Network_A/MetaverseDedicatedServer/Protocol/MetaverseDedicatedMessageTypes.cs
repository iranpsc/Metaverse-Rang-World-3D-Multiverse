using Network_A.Realtime.Protocol;

public static class MetaverseDedicatedMessageTypes
{
    public const string Spawn = RealtimeMessageTypes.Spawn;
    public const string Despawn = RealtimeMessageTypes.Despawn;
    public const string SpawnSnapshot = RealtimeMessageTypes.Snapshot;
    public const string LegacySpawnSnapshot = "spawn_snapshot";
}

//* این فایل نام پیام های اختصاصی ددیکیتد را با قرارداد رسمی ریل تایم هماهنگ نگه می دارد.
//* از این فاز به بعد اسپاون و دیسپاون از مسیر game/spawn و game/despawn ارسال می شوند.
