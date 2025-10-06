using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Interactions;
using Unity.Cinemachine;

namespace Meta
{
    [AddComponentMenu("Meta/Cursor Action")]
    [HelpURL("https://google.com")]
    public class Meta_CursorAction : MonoBehaviour
    {
        [Header("References")]
        public CinemachineInputAxisController MachineCamera;

        [Header("Inputs")]
        [SerializeField] private InputActionReference CursorAction;

        [Header("Settings")]
        [SerializeField] private CursorLockMode CursorMode;
        [SerializeField] private bool MultiTap;
        [SerializeField] private bool IsEnable;

        [Header("Public")]
        public static bool CursorState;

        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        private void OnEnable()
        {
            CursorAction.action.Enable();
            CursorAction.action.started += OnActionPerformed;
            CursorAction.action.performed += OnActionPerformed;
            CursorAction.action.canceled += OnActionCaneled;
        }

        private void OnDisable()
        {
            CursorAction.action.started -= OnActionPerformed;
            CursorAction.action.performed -= OnActionPerformed;
            CursorAction.action.canceled -= OnActionCaneled;
            CursorAction.action.Disable();
        }

        private void Awake()
        {
            
        }

        private void Start()
        {
            Apply(false);
        }
        private void Update()
        {
            CursorState = MultiTap || IsEnable;
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
        private void OnActionCaneled(InputAction.CallbackContext _Ctx)
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
            Cursor.visible = _State;
            Cursor.lockState = _State ? CursorLockMode.None : CursorMode;
            MachineCamera.enabled = !_State;
        }
    }
}

