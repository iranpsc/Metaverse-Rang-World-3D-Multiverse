using System;
using UnityEngine;

namespace Network_A.GameIntegration.Movement
{
    //* مدل ساده وضعیت حرکتی پلیر است و برای ارسال و دریافت position و rotation استفاده می‌شود.
    [Serializable]
    public class RealtimeMovementSnapshot
    {
        public string roomId = string.Empty;
        public string playerId = string.Empty;
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public Vector3 velocity;
        public long sequence;
        public long sentAtMs;
        public long receivedAtMs;

        //* بررسی می‌کند اسنپ‌شات حداقل شناسه پلیر و زمان دریافت معتبر داشته باشد.
        public bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(playerId);
        }
    }
}
