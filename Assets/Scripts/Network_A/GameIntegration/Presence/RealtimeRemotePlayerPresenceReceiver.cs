using System;
using Network_A.GameServer;
using UnityEngine;

namespace Network_A.GameIntegration.Presence
{
    //* رویدادهای ورود و خروج پرزنس را از گیم‌سرورکلاینت می‌گیرد و به شناسه پلیر ریموت تبدیل می‌کند.
    public class RealtimeRemotePlayerPresenceReceiver : MonoBehaviour
    {
        [Header("Filter")]
        [SerializeField] private string localNetworkPlayerId = string.Empty;
        [SerializeField] private bool ignoreLocalPlayer = true;

        [Header("Debug")]
        [SerializeField] private bool logPresenceEvents;

        private GameServerClient gameServerClient;
        private bool isBound;

        public event Action<GameServerPresenceEvent> RemotePlayerJoined;
        public event Action<GameServerPresenceEvent> RemotePlayerLeft;

        #region <Unity Lifecycle>

        //* هنگام حذف آبجکت، رویدادهای پرزنس را جدا می‌کند.
        private void OnDestroy()
        {
            UnbindEvents();
        }

        #endregion

        #region <Public API>

        //* گیرنده پرزنس را به گیم‌سرورکلاینت وصل می‌کند و آیدی پلیر لوکال را تنظیم می‌کند.
        public void Initialize(GameServerClient client, string localPlayerId)
        {
            UnbindEvents();
            gameServerClient = client;
            localNetworkPlayerId = localPlayerId ?? string.Empty;
            BindEvents();
        }

        //* آیدی شبکه‌ای پلیر لوکال را برای فیلتر کردن رویدادهای خودی تنظیم می‌کند.
        public void SetLocalNetworkPlayerId(string playerId)
        {
            localNetworkPlayerId = playerId ?? string.Empty;
        }

        #endregion

        #region <Event Binding>

        //* رویدادهای ورود و خروج پلیر را از گیم‌سرورکلاینت دریافت می‌کند.
        private void BindEvents()
        {
            if (isBound || gameServerClient == null) return;
            isBound = true;
            gameServerClient.Events.PlayerJoinedReceived += HandlePlayerJoinedReceived;
            gameServerClient.Events.PlayerLeftReceived += HandlePlayerLeftReceived;
        }

        //* رویدادهای متصل‌شده را جدا می‌کند تا subscribe تکراری ایجاد نشود.
        private void UnbindEvents()
        {
            if (!isBound) return;
            isBound = false;

            if (gameServerClient != null)
            {
                gameServerClient.Events.PlayerJoinedReceived -= HandlePlayerJoinedReceived;
                gameServerClient.Events.PlayerLeftReceived -= HandlePlayerLeftReceived;
            }
        }

        #endregion

        #region <Handlers>

        //* ورود پلیر ریموت را بعد از فیلتر کردن پلیر خودی به گیم‌پلی اعلام می‌کند.
        private void HandlePlayerJoinedReceived(GameServerPresenceEvent presenceEvent)
        {
            if (!IsRemotePresenceEvent(presenceEvent)) return;
            if (logPresenceEvents) Debug.Log("[RealtimePresenceReceiver] remote joined: " + presenceEvent.ResolveNetworkPlayerId());
            RemotePlayerJoined?.Invoke(presenceEvent);
        }

        //* خروج پلیر ریموت را بعد از فیلتر کردن پلیر خودی به گیم‌پلی اعلام می‌کند.
        private void HandlePlayerLeftReceived(GameServerPresenceEvent presenceEvent)
        {
            if (!IsRemotePresenceEvent(presenceEvent)) return;
            if (logPresenceEvents) Debug.Log("[RealtimePresenceReceiver] remote left: " + presenceEvent.ResolveNetworkPlayerId());
            RemotePlayerLeft?.Invoke(presenceEvent);
        }

        //* بررسی می‌کند رویداد پرزنس مربوط به پلیر ریموت باشد و شناسه معتبر داشته باشد.
        private bool IsRemotePresenceEvent(GameServerPresenceEvent presenceEvent)
        {
            if (presenceEvent == null || !presenceEvent.IsValid()) return false;
            if (!ignoreLocalPlayer) return true;

            string incomingPlayerId = presenceEvent.ResolveNetworkPlayerId();
            if (string.IsNullOrWhiteSpace(localNetworkPlayerId)) return true;
            return !string.Equals(incomingPlayerId, localNetworkPlayerId, StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}
