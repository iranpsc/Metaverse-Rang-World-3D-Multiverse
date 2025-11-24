using Mirror;
using UnityEngine;
using UnityEngine.UI;

namespace Meta
{
    [AddComponentMenu("Meta/Mini Map")]
    [HelpURL("https://google.com")]
    public class Meta_MiniMap : NetworkBehaviour
    {
        [Header("References")]
        public Transform Target;
        public GameObject MiniMap;

        [Header("Player Direction UI")]
        [Tooltip("The UI Image (arrow or triangle) that shows player direction.")]
        public Image PlayerDirectionImage;

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

        // Internal
        private Transform CameraTransform;

        private void Start()
        {
            TargetZoomY = Offset.y;
            TryAssignTarget();
            if (Target != null)
                transform.SetParent(Target.transform);
        }

        private void TryAssignTarget()
        {
            if (Target != null && CameraTransform != null) return;

            if (NetworkClient.localPlayer != null)
            {
                Target = NetworkClient.localPlayer.transform;

                // Find the active camera inside the player's hierarchy
                Camera _PlayerCamera = Target.GetComponentInChildren<Camera>(true);
                if (_PlayerCamera != null)
                {
                    CameraTransform = _PlayerCamera.transform;
                    Debug.Log("[Meta] MiniMap using player camera: " + CameraTransform.name);
                }
                else
                {
                    Debug.LogWarning("[Meta] No camera found under player! MiniMap will use player root instead.");
                    CameraTransform = Target;
                }
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
            if (RotateWithPlayer && CameraTransform != null)
                transform.rotation = Quaternion.Euler(0f, CameraTransform.eulerAngles.y, 0f);
            else
                transform.rotation = Quaternion.Euler(0f, 0f, 0f);

            // Update UI indicator
            UpdateDirectionImage();
        }

        private void LateUpdate()
        {
            if (IsEnabled)
                ApplyToggle();
        }

        // ======== ZOOM CONTROL ========
        public void ZoomIn()
        {
            TargetZoomY = Mathf.Max(MinZoom, TargetZoomY - ZoomSpeed * Time.deltaTime);
        }

        public void ZoomOut()
        {
            TargetZoomY = Mathf.Min(MaxZoom, TargetZoomY + ZoomSpeed * Time.deltaTime);
        }

        public void ZoomInButton()
        {
            TargetZoomY = Mathf.Max(MinZoom, TargetZoomY - 10f);
        }

        public void ZoomOutButton()
        {
            TargetZoomY = Mathf.Min(MaxZoom, TargetZoomY + 10f);
        }

        // ======== PLAYER DIRECTION INDICATOR ========
        private void UpdateDirectionImage()
        {
            if (PlayerDirectionImage == null || CameraTransform == null) return;
            if (RotateWithPlayer) return;
            // Use camera Y rotation to match where the player is looking
            float _YRotation = CameraTransform.eulerAngles.y;

            // Invert Z rotation so arrow points correctly on minimap
            PlayerDirectionImage.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -_YRotation);
        }
    }
}
