using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem;

namespace Meta
{
    [AddComponentMenu("Meta/Meta CameraController")]
    public class Meta_CameraController : NetworkBehaviour
    {
        private Camera MainCamera;

        [Header("References")]
        Transform PlayerBody;
        Meta_UserGlobalData Data;

        [Header("Input Setting")]
        public InputActionAsset PlayerInput;
        public InputAction LookInput;

        [Header("Settings")]
        public float SensitivityX = 8f;
        public float SensitivityY = 8f;
        public float ClampY = 85f;
        public float GamepadMultiplier = 6f;

        [Header("Mobile Settings")]
        public bool TouchOnRightHalf = true;
        public float TouchSensitivity = 0.1f;

        private float Pitch;

        public override void OnStartLocalPlayer()
        {
            if(!isLocalPlayer) enabled = false;
            Data = Meta_UserGlobalData.Instance;

            MainCamera = Camera.main;
            PlayerBody = transform.parent.gameObject.transform;

            SetupInputAction();

            if (MainCamera != null)
            {
                // configure and make camera a child of player with 3rd person offset
                MainCamera.orthographic = false;
                MainCamera.transform.SetParent(transform);
                MainCamera.transform.localPosition = Vector3.zero;
                MainCamera.transform.localEulerAngles = Vector3.zero;
            }
            else
                Debug.LogWarning("[Meta_CameraController] Could not find a camera in scene with 'MainCamera' tag.");
        }

        private void SetupInputAction()
        {
            var _Map = PlayerInput.FindActionMap("Player");
            if (_Map == null)
            {
                enabled = false;
                return;
            }
            LookInput = _Map.FindAction("Look");

            if (LookInput == null)
            {
                enabled = false;
                return;
            }
        }

        private void OnEnable()
        {
            PlayerInput.FindActionMap("Player")?.Enable();
        }

        private void LateUpdate()
        {
            if (!isLocalPlayer) return;
            if (!Data.MouseReleased) HandleLook();
        }

        private void HandleLook()
        {
            Vector2 _Look = Vector2.zero;

            // ===== Touch (Mobile) ===== //
            if(Application.isMobilePlatform)
            {
                if (Touchscreen.current != null)
                {
                    foreach (var _Touch in Touchscreen.current.touches)
                    {
                        if (!_Touch.press.isPressed) continue;

                        Vector2 _Pos = _Touch.position.ReadValue();
                        bool _InRegion = TouchOnRightHalf ? _Pos.x > Screen.width / 2 : _Pos.x < Screen.width / 2;

                        if (!_InRegion) continue;
                        _Look += _Touch.delta.ReadValue() * TouchSensitivity;
                    }
                }
            }

            // ===== Mouse / Gamepad ===== //
            else
            {
                if (LookInput != null)
                    _Look += LookInput.ReadValue<Vector2>();
                if (LookInput.activeControl != null && LookInput.activeControl.device is Gamepad)
                {
                    _Look *= GamepadMultiplier;
                }
            }

            // ===== Apply Rotation ===== //
            float _MouseX = _Look.x * SensitivityX * Time.deltaTime;
            float _MouseY = _Look.y * SensitivityY * Time.deltaTime;

            PlayerBody.Rotate(Vector3.up * _MouseX);

            Pitch -= _MouseY;
            Pitch = Mathf.Clamp(Pitch, -ClampY, ClampY);

            transform.localRotation = Quaternion.Euler(Pitch, 0, 0);
        }
        #region ===== CleanUp / Camera Release =====

        public override void OnStopLocalPlayer() => ReleaseCamera();
        void OnDisable()
        {
            ReleaseCamera();
            PlayerInput.FindActionMap("Player")?.Enable();
        }
        void OnDestroy() => ReleaseCamera();
        void OnApplicationQuit() => ReleaseCamera();

        void ReleaseCamera()
        {
            if (MainCamera != null && MainCamera.transform.parent == transform)
            {
                MainCamera.transform.SetParent(null);
                MainCamera.orthographic = true;
                MainCamera.orthographicSize = 15f;
                MainCamera.transform.localPosition = new Vector3(0f, 70f, 0f);
                MainCamera.transform.localEulerAngles = new Vector3(90f, 0f, 0f);

                if (MainCamera.gameObject.scene != SceneManager.GetActiveScene())
                    SceneManager.MoveGameObjectToScene(MainCamera.gameObject, SceneManager.GetActiveScene());
            }
        }
        #endregion
    }
}

