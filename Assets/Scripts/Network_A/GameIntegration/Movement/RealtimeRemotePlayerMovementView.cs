using UnityEngine;

namespace Network_A.GameIntegration.Movement
{
    //* نمای یک پلیر ریموت است و اسنپ‌شات‌های دریافتی را با نرم‌سازی روی ترنسفورم اعمال می‌کند.
    public class RealtimeRemotePlayerMovementView : MonoBehaviour
    {
        [Header("Smoothing")]
        [SerializeField] private float positionLerpSpeed = 14f;
        [SerializeField] private float rotationLerpSpeed = 18f;
        [SerializeField] private bool snapFirstSnapshot = true;

        private RealtimeMovementSnapshot latestSnapshot;
        private Vector3 targetPosition;
        private Quaternion targetRotation = Quaternion.identity;
        private bool hasSnapshot;

        public string PlayerId { get; private set; } = string.Empty;
        public long LastSequence { get; private set; }

        #region <Unity Lifecycle>

        //* در هر فریم ترنسفورم را به سمت آخرین اسنپ‌شات معتبر حرکت می‌دهد.
        private void Update()
        {
            if (!hasSnapshot) return;

            float positionT = 1f - Mathf.Exp(-positionLerpSpeed * Time.deltaTime);
            float rotationT = 1f - Mathf.Exp(-rotationLerpSpeed * Time.deltaTime);

            transform.position = Vector3.Lerp(transform.position, targetPosition, positionT);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotationT);
        }

        #endregion

        #region <Public API>

        //* شناسه پلیر ریموت را هنگام ساخت از پرزنس تنظیم می‌کند.
        public void InitializeIdentity(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return;
            PlayerId = playerId.Trim();
            gameObject.name = "RemotePlayer_" + PlayerId;
        }

        //* اسنپ‌شات تازه پلیر ریموت را دریافت و به هدف نرم‌سازی تبدیل می‌کند.
        public void ApplySnapshot(RealtimeMovementSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.IsValid()) return;
            if (snapshot.sequence > 0 && LastSequence > 0 && snapshot.sequence < LastSequence) return;

            latestSnapshot = snapshot;
            PlayerId = snapshot.playerId;
            LastSequence = snapshot.sequence;
            targetPosition = snapshot.position;
            targetRotation = snapshot.rotation;

            if (!hasSnapshot && snapFirstSnapshot)
            {
                transform.position = targetPosition;
                transform.rotation = targetRotation;
            }

            hasSnapshot = true;
        }

        //* آخرین اسنپ‌شات دریافتی را برای دیباگ یا سیستم‌های دیگر برمی‌گرداند.
        public RealtimeMovementSnapshot GetLatestSnapshot()
        {
            return latestSnapshot;
        }

        #endregion
    }
}
