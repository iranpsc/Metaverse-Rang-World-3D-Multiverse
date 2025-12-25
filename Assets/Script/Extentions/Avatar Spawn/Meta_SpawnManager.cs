using UnityEngine;

namespace Meta
{
    public class Meta_SpawnManager : MonoBehaviour
    {
        public static Meta_SpawnManager Instance;

        [Header("Detection Settings")]
        public LayerMask GroundLayer;
        public LayerMask ObstacleLayer;
        public float CheckRadius = 1.5f;

        [Header("Boundary Settings")]
        [Tooltip("فاصله ایمن از لبه‌های زمین")]
        public float EdgePadding = 2f;

        private Bounds groundBounds;
        private bool groundFound = false;
        private Transform _helperTransform;

        void Awake()
        {
            Instance = this;
            _helperTransform = new GameObject("SpawnHelper").transform;
            _helperTransform.SetParent(this.transform);
            FindGroundBounds();
        }

        private void FindGroundBounds()
        {
            foreach (GameObject obj in FindObjectsByType<GameObject>(FindObjectsSortMode.None))
            {
                if (((1 << obj.layer) & GroundLayer) != 0)
                {
                    Renderer rend = obj.GetComponent<Renderer>();
                    if (rend != null)
                    {
                        groundBounds = rend.bounds;
                        groundFound = true;
                        return;
                    }
                }
            }
        }

        public Transform GetValidSpawnTransform()
        {
            Vector3 targetPos = Vector3.up * 10f; // موقعیت پیش‌فرض بالا

            if (groundFound)
            {
                for (int i = 0; i < 30; i++) // تعداد تلاش بیشتر برای زمین‌های دایره‌ای
                {
                    // اعمال Padding در محدوده رندوم
                    float randomX = Random.Range(groundBounds.min.x + EdgePadding, groundBounds.max.x - EdgePadding);
                    float randomZ = Random.Range(groundBounds.min.z + EdgePadding, groundBounds.max.z - EdgePadding);

                    Vector3 rayOrigin = new Vector3(randomX, groundBounds.max.y + 10f, randomZ);

                    // بررسی وجود زمین زیر این نقطه (برای زمین‌های دایره‌ای یا سوراخ‌دار)
                    if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 20f, GroundLayer))
                    {
                        Vector3 candidatePos = hit.point + Vector3.up * 0.5f;

                        // حالا چک می‌کنیم که در این نقطه مانعی (Obstacle) نباشد
                        if (!Physics.CheckSphere(candidatePos, CheckRadius, ObstacleLayer))
                        {
                            targetPos = candidatePos;
                            break;
                        }
                    }
                }
            }

            _helperTransform.position = targetPos;
            _helperTransform.rotation = Quaternion.identity;
            return _helperTransform;
        }
    }
}