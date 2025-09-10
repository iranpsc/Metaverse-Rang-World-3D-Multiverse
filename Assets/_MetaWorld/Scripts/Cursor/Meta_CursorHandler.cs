using UnityEngine;
using UnityEngine.InputSystem;

namespace Meta
{
    [AddComponentMenu("Meta/Cursor Handler")]
    public class Meta_CursorHandler : MonoBehaviour
    {
        public InputActionAsset PlayerInput;
        public InputAction ActionLock;
        private Meta_UserGlobalData Data;
        public CursorLockMode CursorMode;

        public bool MultiTap;
        public bool IsEnable;

        [Header("Debugger")]
        public bool EnableLog;

        private void OnEnable()
        {
            PlayerInput.FindActionMap("Player").Enable();
            ActionLock.performed += OnMultiTap;
            if (!Debugger()) return;
        }
        private void OnDisable()
        {
            PlayerInput.FindActionMap("Player").Disable();
            ActionLock.Disable();
            if (!Debugger()) return;
        }

        private void Awake()
        {
            ActionLock = PlayerInput.FindAction("Cursor");
            if (!Debugger()) return;
            Data = Meta_UserGlobalData.Instance;
        }
        public void Update()
        {
            Data.MouseReleased = MultiTap || IsEnable;
            OnHold();
            if (!MultiTap) Apply(IsEnable);
        }

        private void OnHold()
        {
            IsEnable = ActionLock.IsPressed();
        } // Enable Cursor On Hold
        private void OnMultiTap(InputAction.CallbackContext _Ctx)
        {
            MultiTap = !MultiTap;
            Apply(MultiTap);
        } // Enable Cursor On Double Tap

        public void Apply(bool _State)
        {
            Cursor.visible = _State;
            Cursor.lockState = _State ? CursorLockMode.None : CursorMode;
        } // Apply Changes To The Cursor

        private bool Debugger() // Throw False If Found Error
        {
            if (PlayerInput == null)
            {
                if (EnableLog) Debug.Log("[Meta_CursorHandler] InputActionAsset Not Found!");
                enabled = false;

                return false;
            }
            if (ActionLock == null)
            {
                if (EnableLog) Debug.Log("[Meta_CursorHandler] ActionLock Not Found!");
                enabled = false;

                return false;
            }
            return true;
        }
    }
}

