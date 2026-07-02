using System;
using Network_A.GameServer;
using Network_A.Realtime.Protocol;
using UnityEngine;

namespace Network_A.GameIntegration.Movement
{
    //* پیام‌های player_state دریافتی را به اسنپ‌شات حرکت قابل استفاده در گیم‌پلی تبدیل می‌کند.
    public class RealtimeRemotePlayerMovementReceiver : MonoBehaviour
    {
        [Header("Filter")]
        [SerializeField] private string localPlayerId = string.Empty;
        [SerializeField] private bool ignoreLocalPlayer = true;

        [Header("Debug")]
        [SerializeField] private bool logReceivedSnapshots;

        private GameServerClient gameServerClient;
        private bool isBound;

        public event Action<RealtimeMovementSnapshot> SnapshotReceived;

        #region <Unity Lifecycle>

        //* هنگام حذف آبجکت، رویدادهای گیم‌سرورکلاینت را جدا می‌کند.
        private void OnDestroy()
        {
            UnbindEvents();
        }

        #endregion

        #region <Public API>

        //* گیرنده حرکت را به گیم‌سرورکلاینت وصل می‌کند.
        public void Initialize(GameServerClient client, string playerId)
        {
            UnbindEvents();
            gameServerClient = client;
            localPlayerId = playerId ?? string.Empty;
            BindEvents();
        }

        //* آیدی پلیر لوکال را برای فیلتر کردن پیام‌های خودی تنظیم می‌کند.
        public void SetLocalPlayerId(string playerId)
        {
            localPlayerId = playerId ?? string.Empty;
        }

        #endregion

        #region <Event Binding>

        //* رویداد player_state را از گیم‌سرورکلاینت دریافت می‌کند.
        private void BindEvents()
        {
            if (isBound || gameServerClient == null) return;
            isBound = true;
            gameServerClient.Events.PlayerStateReceived += HandlePlayerStateReceived;
        }

        //* رویدادهای متصل‌شده را جدا می‌کند تا subscribe تکراری ایجاد نشود.
        private void UnbindEvents()
        {
            if (!isBound) return;
            isBound = false;
            if (gameServerClient != null) gameServerClient.Events.PlayerStateReceived -= HandlePlayerStateReceived;
        }

        #endregion

        #region <Handlers>

        //* اِنولوپ player_state را parse می‌کند و اسنپ‌شات معتبر را به گیم‌پلی اعلام می‌کند.
        private void HandlePlayerStateReceived(RealtimeEnvelope envelope)
        {
            if (envelope == null) return;
            if (!RealtimeMovementPayloadJson.TryParse(envelope.payloadJson, out RealtimeMovementSnapshot snapshot)) return;
            if (ignoreLocalPlayer && !string.IsNullOrWhiteSpace(localPlayerId) && string.Equals(snapshot.playerId, localPlayerId, StringComparison.OrdinalIgnoreCase)) return;

            if (logReceivedSnapshots) Debug.Log("[RealtimeMovementReceiver] snapshot playerId=" + snapshot.playerId + " | seq=" + snapshot.sequence);
            SnapshotReceived?.Invoke(snapshot);
        }

        #endregion
    }
}
