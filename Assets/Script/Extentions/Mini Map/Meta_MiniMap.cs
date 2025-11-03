using Mirror;
using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Mini Map")]
    [HelpURL("https://google.com")]
    public class Meta_MiniMap : NetworkBehaviour
    {
        [Header("References")]
        public Transform Target;
        public GameObject MiniMap;

        [Header("Settings")]
        public Vector3 Offset = new Vector3(0, 100, 0);
        public float FollowSpeed = 10f;
        public bool RotateWithPlayer = true;
        public bool IsEnabled = false;

        [Header("Zoom Settings")]
        public float ZoomSpeed = 50f;
        public float MinZoom = 20f;
        public float MaxZoom = 200f;
        private float TargetZoomY;

        private void Start()
        {
            TargetZoomY = Offset.y;
            TryAssignTarget();
            if (Target != null) transform.SetParent(Target.transform);
        }

        private void TryAssignTarget()
        {
            if (Target != null) return;

            if (NetworkClient.localPlayer != null)
            {
                Target = NetworkClient.localPlayer.transform;
                if (Target != null)
                    Debug.Log("[Meta] Local player assigned to minimap.");
            }
            else
            {
                Invoke(nameof(TryAssignTarget), 0.5f);
            }
        }

        public void ToggleMiniMap(bool _Enable)
        {
            if (Target == null)
                TryAssignTarget();

            IsEnabled = _Enable;
            MiniMap.SetActive(IsEnabled);
        }

        private void ApplyToggle()
        {
            if (Target == null) return;

            // Smooth follow
            Vector3 _TargetPos = Target.position + Offset;
            transform.position = Vector3.Lerp(transform.position, _TargetPos, Time.deltaTime * FollowSpeed);

            // Smooth zoom transition
            Offset.y = Mathf.Lerp(Offset.y, TargetZoomY, Time.deltaTime * 5f);

            // Rotation handling
            if (RotateWithPlayer)
                transform.rotation = Quaternion.Euler(0f, Target.eulerAngles.y, 0f);
            else
                transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        private void LateUpdate()
        {
            if (IsEnabled)
                ApplyToggle();
        }

        // ======== ZOOM CONTROL ========

        /// <summary>
        /// Zooms the minimap camera in (closer to player)
        /// </summary>
        public void ZoomIn()
        {
            TargetZoomY = Mathf.Max(MinZoom, TargetZoomY - ZoomSpeed * Time.deltaTime);
        }

        /// <summary>
        /// Zooms the minimap camera out (further away)
        /// </summary>
        public void ZoomOut()
        {
            TargetZoomY = Mathf.Min(MaxZoom, TargetZoomY + ZoomSpeed * Time.deltaTime);
        }

        /// <summary>
        /// Instant zoom change (useful for UI buttons)
        /// </summary>
        public void ZoomInButton()
        {
            TargetZoomY = Mathf.Max(MinZoom, TargetZoomY - 10f);
        }

        public void ZoomOutButton()
        {
            TargetZoomY = Mathf.Min(MaxZoom, TargetZoomY + 10f);
        }
    }
}
