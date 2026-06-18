using System;
using Network_A.GameServer;
using UnityEngine;

namespace Network_A.GameIntegration.World
{
    //* رویدادهای جهان را از گیم‌سرورکلاینت می‌گیرد و به مدل قابل استفاده گیم‌پلی تبدیل می‌کند.
    public class RealtimeWorldEventReceiver : MonoBehaviour
    {
        [SerializeField] private bool ignoreLocalSender = true;
        [SerializeField] private bool logReceivedEvents;

        private GameServerClient gameServerClient;
        private string localNetworkPlayerId = string.Empty;
        private bool isInitialized;

        public event Action<RealtimeWorldEventData> WorldEventReceived;

        //* گیرنده رویداد جهان را با کلاینت و شناسه پلیر لوکال آماده می‌کند.
        public void Initialize(GameServerClient client, string localPlayerId)
        {
            if (gameServerClient != null) gameServerClient.Events.WorldEventReceived -= HandleWorldEventEnvelope;

            gameServerClient = client;
            localNetworkPlayerId = string.IsNullOrWhiteSpace(localPlayerId) ? string.Empty : localPlayerId.Trim();
            isInitialized = gameServerClient != null;

            if (gameServerClient != null) gameServerClient.Events.WorldEventReceived += HandleWorldEventEnvelope;
        }

        //* اتصال eventها را هنگام حذف کامپوننت جدا می‌کند.
        private void OnDestroy()
        {
            if (gameServerClient != null) gameServerClient.Events.WorldEventReceived -= HandleWorldEventEnvelope;
            gameServerClient = null;
            isInitialized = false;
        }

        //* اِنولوپ world_event را parse می‌کند و بعد از فیلتر فرستنده لوکال به گیم‌پلی می‌دهد.
        private void HandleWorldEventEnvelope(Network_A.Realtime.Protocol.RealtimeEnvelope envelope)
        {
            if (!isInitialized) return;

            RealtimeWorldEventData eventData = RealtimeWorldEventData.FromEnvelope(envelope);
            if (eventData == null || !eventData.IsValid()) return;
            if (ShouldIgnoreEvent(eventData)) return;

            if (logReceivedEvents) Debug.Log("[RealtimeWorldEventReceiver] World event received: " + eventData.eventType + " | object=" + eventData.objectId);
            WorldEventReceived?.Invoke(eventData);
        }

        //* بررسی می‌کند رویداد فرستنده خودمان را باید نادیده بگیریم یا نه.
        private bool ShouldIgnoreEvent(RealtimeWorldEventData eventData)
        {
            if (!ignoreLocalSender) return false;
            if (eventData == null || string.IsNullOrWhiteSpace(eventData.senderPlayerId)) return false;
            return string.Equals(eventData.senderPlayerId, localNetworkPlayerId, StringComparison.OrdinalIgnoreCase);
        }
    }
}
