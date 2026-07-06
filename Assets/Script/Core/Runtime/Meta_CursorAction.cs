using Mirror;
using System.Collections.Generic;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Interactions;

namespace Meta
{
    [AddComponentMenu("Meta/Cursor Action")]
    [HelpURL("https://google.com")]
    public class Meta_CursorAction : NetworkBehaviour
    {
        [Header("References")]
        [Tooltip("Reference to your Cinemachine Input Axis Controller component")]
        public CinemachineInputAxisController InputProvider;

        [Header("Inputs")]
        [SerializeField] private InputActionReference CursorAction;

        [Header("Settings")]
        [SerializeField] private CursorLockMode CursorMode = CursorLockMode.Locked;
        [SerializeField] private bool MultiTap;
        [SerializeField] private bool IsEnable;

        [Header("Public")]
        public static bool CursorState;

        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        private bool _WasOverUI;
        private bool _IsCursorControlAllowed;

        //private void Awake()
        //{
        //    // On Android or VR, we still run, but disable cursor control only
        //    _IsCursorControlAllowed = !(Application.platform == RuntimePlatform.Android);
        //}

        protected override void OnValidate()
        {
            if (Application.isPlaying) return;
            base.OnValidate();
            this.enabled = false;
        }

        private void OnEnable()
        {
            if (CursorAction != null)
            {
                CursorAction.action.Enable();
                CursorAction.action.started += OnActionPerformed;
                CursorAction.action.performed += OnActionPerformed;
                CursorAction.action.canceled += OnActionCanceled;
            }
        }

        private void OnDisable()
        {
            if (CursorAction != null)
            {
                CursorAction.action.started -= OnActionPerformed;
                CursorAction.action.performed -= OnActionPerformed;
                CursorAction.action.canceled -= OnActionCanceled;
                CursorAction.action.Disable();
            }
        }
        public override void OnStartAuthority()
        {
            _IsCursorControlAllowed = !(Application.platform == RuntimePlatform.Android);
            this.enabled = true;
        }

        private void Start()
        {
            Apply(false);
        }

        private void Update()
        {
            if (InputProvider == null || EventSystem.current == null)
            {
                Debug.Log("[Input Action] Input provider or Event system is null");
                return;
            }

            CursorState = MultiTap || IsEnable;

            bool _IsOverUI = IsPointerOverUI();

            // Disable camera input when cursor is active OR UI is under pointer
            bool _ShouldDisableInput = CursorState || _IsOverUI;

            InputProvider.enabled = !_ShouldDisableInput;

            if (EnableLog && _WasOverUI != _IsOverUI)
                Debug.Log($"[Meta_CursorAndCinemachineInput] Over UI: {_IsOverUI}, CursorState: {CursorState}, Input Enabled: {!_ShouldDisableInput}");

            _WasOverUI = _IsOverUI;
        }

        private void OnActionPerformed(InputAction.CallbackContext _Ctx)
        {
            if (_Ctx.interaction is MultiTapInteraction && _Ctx.performed)
            {
                MultiTap = !MultiTap;
                Apply(MultiTap);
                return;
            }

            if (_Ctx.interaction is HoldInteraction)
            {
                if (_Ctx.performed)
                {
                    IsEnable = _Ctx.ReadValueAsButton();
                    Apply(IsEnable);
                }
            }
        }

        private void OnActionCanceled(InputAction.CallbackContext _Ctx)
        {
            if (_Ctx.interaction is MultiTapInteraction && _Ctx.canceled)
            {
                MultiTap = false;
                Apply(IsEnable);
                return;
            }

            if (_Ctx.interaction is HoldInteraction)
            {
                IsEnable = false;
                Apply(IsEnable);
            }
        }

        private void Apply(bool _State)
        {
            // Skip cursor control on Android or VR
            if (!_IsCursorControlAllowed)
                return;

            Cursor.visible = _State;
            Cursor.lockState = _State ? CursorLockMode.None : CursorMode;
        }

        private bool IsPointerOverUI()
        {
            if (EventSystem.current == null)
                return false;

            Vector2 _Pos;
            bool _Pressed = false;

            if (Pointer.current != null)
            {
                _Pos = Pointer.current.position.ReadValue();
                _Pressed = Pointer.current.press.isPressed;
            }
            else if (Mouse.current != null)
            {
                _Pos = Mouse.current.position.ReadValue();
                _Pressed = Mouse.current.leftButton.isPressed;
            }
            else return false;

            if (!_Pressed)
                return false;

            PointerEventData _EventData = new(EventSystem.current)
            {
                position = _Pos
            };

            List<RaycastResult> _Results = new();
            EventSystem.current.RaycastAll(_EventData, _Results);

            return _Results.Count > 0;
        }
    }
}