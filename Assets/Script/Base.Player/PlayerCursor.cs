using System;
using UnityEngine;

[AddComponentMenu("Meta RGB/Player/Base.Player Cursor")]
public class PlayerCursor : MonoBehaviour
{
    // ============================================================
    // STATE
    // ============================================================

    private bool CursorToggled;
    private bool CursorHeld;
    private bool GameplayContext;

    public bool CursorLocked;

    // ============================================================
    // EVENTS
    // ============================================================

    public event Action<bool> OnCursorLockChanged;

    // ============================================================
    // UNITY
    // ============================================================

    private void OnEnable()
    {
        if (PlayerInput.Instance != null)
        {
            PlayerInput.Instance.OnCursorHoldStarted += HandleCursorHoldStarted;
            PlayerInput.Instance.OnCursorHoldCanceled += HandleCursorHoldCanceled;
            PlayerInput.Instance.OnCursorToggle += HandleCursorToggle;
        }

        PlayerInputContext.OnContextChanged += HandleContextChanged;

        HandleContextChanged(PlayerInputContext.Current);
    }

    private void OnDisable()
    {
        if (PlayerInput.Instance != null)
        {
            PlayerInput.Instance.OnCursorHoldStarted -= HandleCursorHoldStarted;
            PlayerInput.Instance.OnCursorHoldCanceled -= HandleCursorHoldCanceled;
            PlayerInput.Instance.OnCursorToggle -= HandleCursorToggle;
        }

        PlayerInputContext.OnContextChanged -= HandleContextChanged;
    }

    // ============================================================
    // CONTEXT
    // ============================================================

    private void HandleContextChanged(InputContext _Context)
    {
        switch (_Context)
        {
            case InputContext.Gameplay:
            case InputContext.Vehicle:

                GameplayContext = true;

                ApplyCursorState();

                break;

            default:

                GameplayContext = false;

                CursorHeld = false;
                CursorToggled = false;

                ApplyCursorState();

                break;
        }
    }

    // ============================================================
    // INPUT
    // ============================================================

    private void HandleCursorHoldStarted()
    {
        if (!GameplayContext)
            return;

        CursorHeld = true;

        ApplyCursorState();
    }

    private void HandleCursorHoldCanceled()
    {
        if (!GameplayContext)
            return;

        CursorHeld = false;

        ApplyCursorState();
    }

    private void HandleCursorToggle()
    {
        if (!GameplayContext)
            return;

        CursorToggled = !CursorToggled;

        ApplyCursorState();
    }

    // ============================================================
    // CURSOR
    // ============================================================

    private void ApplyCursorState()
    {
        if (!GameplayContext)
        {
            UnlockCursor();
            return;
        }

        if (CursorToggled)
        {
            UnlockCursor();
            return;
        }

        if (CursorHeld)
        {
            UnlockCursor();
            return;
        }

        LockCursor();
    }

    private void LockCursor()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        SetCursorLockState(true);
    }

    private void UnlockCursor()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        SetCursorLockState(false);
    }

    private void SetCursorLockState(bool _Locked)
    {
        if (CursorLocked == _Locked)
            return;

        CursorLocked = _Locked;

        OnCursorLockChanged?.Invoke(CursorLocked);
    }
}