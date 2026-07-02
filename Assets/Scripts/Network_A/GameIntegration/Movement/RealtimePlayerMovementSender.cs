using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.GameServer;
using UnityEngine;

namespace Network_A.GameIntegration.Movement
{
    //* حرکت پلیر لوکال را با نرخ کنترل‌شده به گیم‌سرورکلاینت می‌فرستد.
    public class RealtimePlayerMovementSender : MonoBehaviour
    {
        [Header("Binding")]
        [SerializeField] private Transform targetTransform;
        [SerializeField] private string localPlayerId = "local_player";
        [SerializeField] private bool autoUseSelfTransform = true;

        [Header("Rate Limit")]
        [SerializeField] private float sendRatePerSecond = 12f;
        [SerializeField] private float minPositionDelta = 0.02f;
        [SerializeField] private float minRotationDeltaDegrees = 1f;
        [SerializeField] private bool sendFirstSnapshotImmediately = true;

        [Header("Debug")]
        [SerializeField] private bool logSendResult;

        private GameServerClient gameServerClient;
        private CancellationTokenSource lifecycleCts;
        private Vector3 lastObservedPosition;
        private Vector3 lastSentPosition;
        private Quaternion lastSentRotation = Quaternion.identity;
        private float sendTimer;
        private long sequence;
        private bool hasLastObservedPosition;
        private bool hasLastSentSnapshot;
        private bool sendInFlight;
        private bool isInitialized;

        public string LocalPlayerId => localPlayerId;
        public long Sequence => sequence;
        public bool IsInitialized => isInitialized;

        #region <Unity Lifecycle>

        //* مقدارهای اولیه را آماده می‌کند و اگر ترنسفورم تنظیم نشده باشد از ترنسفورم خود آبجکت استفاده می‌کند.
        private void Awake()
        {
            if (targetTransform == null && autoUseSelfTransform) targetTransform = transform;
            lifecycleCts = new CancellationTokenSource();
        }

        //* در هر فریم بررسی می‌کند که آیا زمان ارسال اسنپ‌شات جدید رسیده است یا نه.
        private void Update()
        {
            if (!CanTick()) return;

            float interval = 1f / Mathf.Max(1f, sendRatePerSecond);
            sendTimer += Time.deltaTime;
            if (sendTimer < interval) return;

            sendTimer = 0f;
            if (!ShouldSendSnapshot()) return;

            _ = SendSnapshotAsync(lifecycleCts.Token);
        }

        //* هنگام غیرفعال شدن آبجکت، ارسال‌های در حال انتظار را لغو می‌کند.
        private void OnDisable()
        {
            lifecycleCts?.Cancel();
            lifecycleCts?.Dispose();
            lifecycleCts = new CancellationTokenSource();
            sendInFlight = false;
        }

        //* هنگام حذف آبجکت، توکن داخلی را آزاد می‌کند.
        private void OnDestroy()
        {
            lifecycleCts?.Cancel();
            lifecycleCts?.Dispose();
            lifecycleCts = null;
        }

        #endregion

        #region <Public API>

        //* فرستنده حرکت را با گیم‌سرورکلاینت آماده می‌کند.
        public void Initialize(GameServerClient client, string playerId)
        {
            gameServerClient = client;
            if (!string.IsNullOrWhiteSpace(playerId)) localPlayerId = playerId.Trim();
            if (targetTransform == null && autoUseSelfTransform) targetTransform = transform;

            ResetSendState();
            isInitialized = gameServerClient != null && targetTransform != null && !string.IsNullOrWhiteSpace(localPlayerId);
        }

        //* ترنسفورم هدف را برای ارسال حرکت تغییر می‌دهد.
        public void SetTargetTransform(Transform target)
        {
            targetTransform = target;
            ResetSendState();
        }

        //* ارسال اجباری یک اسنپ‌شات را بدون توجه به threshold انجام می‌دهد.
        public async Task<bool> ForceSendSnapshotAsync(CancellationToken cancellationToken = default)
        {
            if (!CanSendNow()) return false;
            return await SendSnapshotInternalAsync(cancellationToken);
        }

        #endregion

        #region <Tick Logic>

        //* بررسی می‌کند فرستنده در وضعیت قابل پردازش فریم باشد.
        private bool CanTick()
        {
            return isInitialized && gameServerClient != null && targetTransform != null && enabled && gameObject.activeInHierarchy;
        }

        //* بررسی می‌کند ارسال الان از نظر اتصال و وضعیت داخلی مجاز باشد.
        private bool CanSendNow()
        {
            return CanTick() && !sendInFlight;
        }

        //* بررسی می‌کند آیا تغییر حرکت برای ارسال اسنپ‌شات جدید کافی است یا نه.
        private bool ShouldSendSnapshot()
        {
            if (sendInFlight || targetTransform == null) return false;
            if (!hasLastSentSnapshot) return sendFirstSnapshotImmediately;

            float positionDeltaSqr = (targetTransform.position - lastSentPosition).sqrMagnitude;
            if (positionDeltaSqr >= minPositionDelta * minPositionDelta) return true;

            float rotationDelta = Quaternion.Angle(lastSentRotation, targetTransform.rotation);
            return rotationDelta >= minRotationDeltaDegrees;
        }

        //* اسنپ‌شات فعلی را می‌سازد و به مسیر ارسال داخلی می‌فرستد.
        private async Task SendSnapshotAsync(CancellationToken cancellationToken)
        {
            if (!CanSendNow()) return;
            await SendSnapshotInternalAsync(cancellationToken);
        }

        //* اسنپ‌شات حرکت را از طریق گیم‌سرورکلاینت ارسال می‌کند.
        private async Task<bool> SendSnapshotInternalAsync(CancellationToken cancellationToken)
        {
            sendInFlight = true;

            try
            {
                Vector3 currentPosition = targetTransform.position;
                Quaternion currentRotation = targetTransform.rotation;
                Vector3 velocity = CalculateVelocity(currentPosition);
                sequence++;

                bool sent = await gameServerClient.SendPlayerStateAsync(localPlayerId, currentPosition, currentRotation, velocity, sequence, cancellationToken);
                if (sent)
                {
                    lastSentPosition = currentPosition;
                    lastSentRotation = currentRotation;
                    hasLastSentSnapshot = true;
                }

                if (logSendResult) Debug.Log("[RealtimeMovementSender] sent=" + sent + " | playerId=" + localPlayerId + " | seq=" + sequence);
                return sent;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[RealtimeMovementSender] Send failed: " + ex.Message);
                return false;
            }
            finally
            {
                sendInFlight = false;
            }
        }

        //* سرعت تقریبی پلیر را بر اساس تغییر position بین فریم‌ها محاسبه می‌کند.
        private Vector3 CalculateVelocity(Vector3 currentPosition)
        {
            if (!hasLastObservedPosition)
            {
                lastObservedPosition = currentPosition;
                hasLastObservedPosition = true;
                return Vector3.zero;
            }

            float deltaTime = Mathf.Max(Time.deltaTime, 0.0001f);
            Vector3 velocity = (currentPosition - lastObservedPosition) / deltaTime;
            lastObservedPosition = currentPosition;
            return velocity;
        }

        //* وضعیت ارسال را برای شروع تازه یا تغییر target پاک می‌کند.
        private void ResetSendState()
        {
            sendTimer = 0f;
            sequence = 0;
            hasLastObservedPosition = false;
            hasLastSentSnapshot = false;
            sendInFlight = false;
        }

        #endregion
    }
}
