using Mirror;
using UnityEngine;
using UnityEngine.UI;

namespace Meta
{
    [AddComponentMenu("Meta/Mini Map")]
    public class Meta_MiniMap : NetworkBehaviour
    {
        [Header("Platform")]
        public PlatformType Platform;

        [Header("References")]
        public Transform Target;
        public GameObject MiniMap;
        public GameObject VRMiniMap;
        public Camera MiniMapCamera; // رفرنس مستقیم به دوربین مینی‌مپ

        [Header("Player Direction UI")]
        public Image PlayerDirectionImage;
        public Image VRPlayerDirectionImage;

        [Header("Settings")]
        public Vector3 Offset = new Vector3(0, 100, 0);
        public float FollowSpeed = 10f;
        public bool RotateWithPlayer = true;
        public bool IsEnabled = false;

        [Header("Zoom Settings")]
        public float ZoomSpeed = 50f;
        public float MinZoom = 5f;   // برای Ortho معمولاً اعداد کوچکترند
        public float MaxZoom = 100f;
        private float TargetZoomValue; // مقدار هدف (یا Y یا Size)

        private Transform CameraTransform;

        private void Start()
        {
            if (MiniMapCamera == null) MiniMapCamera = GetComponentInChildren<Camera>();

            // مقدار اولیه بر اساس نوع دوربین
            if (MiniMapCamera != null && MiniMapCamera.orthographic)
            { 
                TargetZoomValue = MiniMapCamera.orthographicSize;
                MinZoom = MinZoom / 4;
                MaxZoom = MaxZoom / 2;
            }
            else
            {
                TargetZoomValue = Offset.y;
            }

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
                Camera _PlayerCamera = Target.GetComponentInChildren<Camera>(true);
                CameraTransform = (_PlayerCamera != null) ? _PlayerCamera.transform : Target;
            }
            else
            {
                Invoke(nameof(TryAssignTarget), 0.5f);
            }
        }

        private void ApplyToggle()
        {
            if (Target == null || MiniMapCamera == null) return;

            // 1. Follow Position
            Vector3 _TargetPos = Target.position + new Vector3(Offset.x, MiniMapCamera.orthographic ? Offset.y : Offset.y, Offset.z);
            transform.position = Vector3.Lerp(transform.position, _TargetPos, Time.deltaTime * FollowSpeed);

            // 2. Dual-Mode Zoom Transition
            if (MiniMapCamera.orthographic)
            {
                // زوم برای دوربین ارتودوگرافیک
                MiniMapCamera.orthographicSize = Mathf.Lerp(MiniMapCamera.orthographicSize, TargetZoomValue, Time.deltaTime * 5f);
            }
            else
            {
                // زوم برای دوربین پرسپکتیو (تغییر ارتفاع)
                Offset.y = Mathf.Lerp(Offset.y, TargetZoomValue, Time.deltaTime * 5f);
            }

            // 3. Rotation
            if (RotateWithPlayer && CameraTransform != null)
                transform.rotation = Quaternion.Euler(0f, CameraTransform.eulerAngles.y, 0f);
            else
                transform.rotation = Quaternion.Euler(0f, 0f, 0f);

            UpdateDirectionImage();
        }

        private void LateUpdate()
        {
            if (IsEnabled) ApplyToggle();
        }

        // ======== ZOOM CONTROLS (Updated) ========
        public void ZoomIn()
        {
            TargetZoomValue = Mathf.Max(MinZoom, TargetZoomValue - ZoomSpeed * Time.deltaTime);
        }

        public void ZoomOut()
        {
            TargetZoomValue = Mathf.Min(MaxZoom, TargetZoomValue + ZoomSpeed * Time.deltaTime);
        }

        public void ZoomInButton()
        {
            float step = MiniMapCamera.orthographic ? 2f : 10f;
            TargetZoomValue = Mathf.Max(MinZoom, TargetZoomValue - step);
        }

        public void ZoomOutButton()
        {
            float step = MiniMapCamera.orthographic ? 2f : 10f;
            TargetZoomValue = Mathf.Min(MaxZoom, TargetZoomValue + step);
        }

        private void UpdateDirectionImage()
        {
            if (PlayerDirectionImage == null || CameraTransform == null || RotateWithPlayer) return;
            float _YRotation = CameraTransform.eulerAngles.y;
            if(Platform == PlatformType.Windows) PlayerDirectionImage.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -_YRotation);
            if (VRPlayerDirectionImage == null) return;
            if (Platform == PlatformType.VR) VRPlayerDirectionImage.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -_YRotation);
        }

        public void ToggleMiniMap(bool _Enable)
        {
            if (Target == null) TryAssignTarget();
            IsEnabled = _Enable;
            if (Platform == PlatformType.Windows) MiniMap?.SetActive(IsEnabled);
            if (Platform == PlatformType.VR) VRMiniMap?.SetActive(IsEnabled);
        }
    }
}