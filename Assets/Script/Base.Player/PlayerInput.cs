using System;
using UnityEngine;
using UnityEngine.InputSystem;

[DefaultExecutionOrder(-100)]
[AddComponentMenu("Meta RGB/Input/Base.Player Input")]
public class PlayerInput : MonoBehaviour
{
    public static PlayerInput Instance { get; private set; }

    // ============================================================
    // INPUT ACTIONS
    // ============================================================

    [Header("Gameplay Actions")]
    [SerializeField] private InputActionReference MoveAction;
    [SerializeField] private InputActionReference LookAction;
    [SerializeField] private InputActionReference JumpAction;
    [SerializeField] private InputActionReference SprintAction;
    [SerializeField] private InputActionReference CrouchAction;

    [Header("Cursor Actions")]
    [SerializeField] private InputActionReference CursorHoldAction;
    [SerializeField] private InputActionReference CursorToggleAction;

    // ============================================================
    // VALUES
    // ============================================================

    public Vector2 Move { get; private set; }
    public Vector2 Look { get; private set; }

    public bool SprintHeld { get; private set; }
    public bool CrouchHeld { get; private set; }

    // ============================================================
    // EVENTS
    // ============================================================

    public event Action OnJump;

    public event Action OnSprintStarted;
    public event Action OnSprintCanceled;

    public event Action OnCrouchStarted;
    public event Action OnCrouchCanceled;

    public event Action OnCursorHoldStarted;
    public event Action OnCursorHoldCanceled;

    public event Action OnCursorToggle;

    // ============================================================
    // UNITY
    // ============================================================

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    private void OnEnable()
    {
        SubscribeActions();

        PlayerInputContext.OnContextChanged += HandleContextChanged;

        ApplyContext(PlayerInputContext.Current);
    }

    private void OnDisable()
    {
        UnsubscribeActions();

        PlayerInputContext.OnContextChanged -= HandleContextChanged;

        if (Instance == this)
        {
            Instance = null;
        }
    }

    // ============================================================
    // ACTION SUBSCRIPTION
    // ============================================================

    private void SubscribeActions()
    {
        // --------------------------------------------------------
        // MOVE
        // --------------------------------------------------------

        if (MoveAction != null)
        {
            MoveAction.action.performed += OnMovePerformed;
            MoveAction.action.canceled += OnMoveCanceled;
        }

        // --------------------------------------------------------
        // LOOK
        // --------------------------------------------------------

        if (LookAction != null)
        {
            LookAction.action.performed += OnLookPerformed;
            LookAction.action.canceled += OnLookCanceled;
        }

        // --------------------------------------------------------
        // JUMP
        // --------------------------------------------------------

        if (JumpAction != null)
        {
            JumpAction.action.performed += OnJumpPerformed;
        }

        // --------------------------------------------------------
        // SPRINT
        // --------------------------------------------------------

        if (SprintAction != null)
        {
            SprintAction.action.started += OnSprintStartedInternal;
            SprintAction.action.canceled += OnSprintCanceledInternal;
        }

        // --------------------------------------------------------
        // CROUCH
        // --------------------------------------------------------

        if (CrouchAction != null)
        {
            CrouchAction.action.started += OnCrouchStartedInternal;
            CrouchAction.action.canceled += OnCrouchCanceledInternal;
        }

        // --------------------------------------------------------
        // CURSOR HOLD
        // --------------------------------------------------------

        if (CursorHoldAction != null)
        {
            CursorHoldAction.action.started += OnCursorHoldStartedInternal;
            CursorHoldAction.action.canceled += OnCursorHoldCanceledInternal;
        }

        // --------------------------------------------------------
        // CURSOR TOGGLE
        // --------------------------------------------------------

        if (CursorToggleAction != null)
        {
            CursorToggleAction.action.performed += OnCursorTogglePerformed;
        }
    }

    private void UnsubscribeActions()
    {
        // --------------------------------------------------------
        // MOVE
        // --------------------------------------------------------

        if (MoveAction != null)
        {
            MoveAction.action.performed -= OnMovePerformed;
            MoveAction.action.canceled -= OnMoveCanceled;
        }

        // --------------------------------------------------------
        // LOOK
        // --------------------------------------------------------

        if (LookAction != null)
        {
            LookAction.action.performed -= OnLookPerformed;
            LookAction.action.canceled -= OnLookCanceled;
        }

        // --------------------------------------------------------
        // JUMP
        // --------------------------------------------------------

        if (JumpAction != null)
        {
            JumpAction.action.performed -= OnJumpPerformed;
        }

        // --------------------------------------------------------
        // SPRINT
        // --------------------------------------------------------

        if (SprintAction != null)
        {
            SprintAction.action.started -= OnSprintStartedInternal;
            SprintAction.action.canceled -= OnSprintCanceledInternal;
        }

        // --------------------------------------------------------
        // CROUCH
        // --------------------------------------------------------

        if (CrouchAction != null)
        {
            CrouchAction.action.started -= OnCrouchStartedInternal;
            CrouchAction.action.canceled -= OnCrouchCanceledInternal;
        }

        // --------------------------------------------------------
        // CURSOR HOLD
        // --------------------------------------------------------

        if (CursorHoldAction != null)
        {
            CursorHoldAction.action.started -= OnCursorHoldStartedInternal;
            CursorHoldAction.action.canceled -= OnCursorHoldCanceledInternal;
        }

        // --------------------------------------------------------
        // CURSOR TOGGLE
        // --------------------------------------------------------

        if (CursorToggleAction != null)
        {
            CursorToggleAction.action.performed -= OnCursorTogglePerformed;
        }
    }

    // ============================================================
    // CONTEXT
    // ============================================================

    private void HandleContextChanged(InputContext _Context)
    {
        ApplyContext(_Context);
    }

    private void ApplyContext(InputContext _Context)
    {
        bool _GameplayEnabled =
            _Context == InputContext.Gameplay ||
            _Context == InputContext.Vehicle;

        if (_GameplayEnabled)
        {
            EnableGameplayActions();
        }
        else
        {
            DisableGameplayActions();
        }

        ResetValues();
    }

    private void EnableGameplayActions()
    {
        MoveAction?.action.Enable();
        LookAction?.action.Enable();
        JumpAction?.action.Enable();
        SprintAction?.action.Enable();
        CrouchAction?.action.Enable();

        CursorHoldAction?.action.Enable();
        CursorToggleAction?.action.Enable();
    }

    private void DisableGameplayActions()
    {
        MoveAction?.action.Disable();
        LookAction?.action.Disable();
        JumpAction?.action.Disable();
        SprintAction?.action.Disable();
        CrouchAction?.action.Disable();

        CursorHoldAction?.action.Disable();
        CursorToggleAction?.action.Disable();
    }

    private void ResetValues()
    {
        Move = Vector2.zero;
        Look = Vector2.zero;

        SprintHeld = false;
        CrouchHeld = false;
    }

    // ============================================================
    // MOVE
    // ============================================================

    private void OnMovePerformed(InputAction.CallbackContext _Context)
    {
        if (!IsGameplayContext())
            return;

        Move = _Context.ReadValue<Vector2>();
    }

    private void OnMoveCanceled(InputAction.CallbackContext _Context)
    {
        Move = Vector2.zero;
    }

    // ============================================================
    // LOOK
    // ============================================================

    private void OnLookPerformed(InputAction.CallbackContext _Context)
    {
        if (!IsGameplayContext())
            return;

        Look = _Context.ReadValue<Vector2>();
    }

    private void OnLookCanceled(InputAction.CallbackContext _Context)
    {
        Look = Vector2.zero;
    }

    // ============================================================
    // JUMP
    // ============================================================

    private void OnJumpPerformed(InputAction.CallbackContext _Context)
    {
        if (!IsGameplayContext())
            return;

        OnJump?.Invoke();
    }

    // ============================================================
    // SPRINT
    // ============================================================

    private void OnSprintStartedInternal(InputAction.CallbackContext _Context)
    {
        if (!IsGameplayContext())
            return;

        SprintHeld = true;

        OnSprintStarted?.Invoke();
    }

    private void OnSprintCanceledInternal(InputAction.CallbackContext _Context)
    {
        SprintHeld = false;

        OnSprintCanceled?.Invoke();
    }

    // ============================================================
    // CROUCH
    // ============================================================

    private void OnCrouchStartedInternal(InputAction.CallbackContext _Context)
    {
        if (!IsGameplayContext())
            return;

        CrouchHeld = true;

        OnCrouchStarted?.Invoke();
    }

    private void OnCrouchCanceledInternal(InputAction.CallbackContext _Context)
    {
        CrouchHeld = false;

        OnCrouchCanceled?.Invoke();
    }

    // ============================================================
    // CURSOR HOLD
    // ============================================================

    private void OnCursorHoldStartedInternal(InputAction.CallbackContext _Context)
    {
        if (!IsGameplayContext())
            return;

        OnCursorHoldStarted?.Invoke();
    }

    private void OnCursorHoldCanceledInternal(InputAction.CallbackContext _Context)
    {
        if (!IsGameplayContext())
            return;

        OnCursorHoldCanceled?.Invoke();
    }

    // ============================================================
    // CURSOR TOGGLE
    // ============================================================

    private void OnCursorTogglePerformed(InputAction.CallbackContext _Context)
    {
        if (!IsGameplayContext())
            return;

        OnCursorToggle?.Invoke();
    }

    // ============================================================
    // HELPERS
    // ============================================================

    private bool IsGameplayContext()
    {
        return PlayerInputContext.Is(InputContext.Gameplay) ||
               PlayerInputContext.Is(InputContext.Vehicle);
    }
}