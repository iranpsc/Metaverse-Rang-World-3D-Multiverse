using System;
using UnityEngine;

[AddComponentMenu("Meta RGB/Player/Base.Player State")]
public class PlayerState : MonoBehaviour
{
    // ============================================================
    // MOVEMENT STATE
    // ============================================================

    public Vector3 Velocity { get; internal set; }
    public Vector3 WorldMoveDirection { get; internal set; }

    public float HorizontalSpeed { get; internal set; }

    public bool IsGrounded { get; internal set; }
    public bool WasGrounded { get; internal set; }

    public bool IsMoving => new Vector2(Velocity.x, Velocity.z).sqrMagnitude > 0.01f;

    public bool IsSprinting { get; internal set; }
    public bool IsCrouching { get; internal set; }
    public bool IsJumping { get; internal set; }

    public bool IsFalling => !IsGrounded && Velocity.y < 0f;

    // ============================================================
    // EVENTS
    // ============================================================

    public event Action OnJumed;
    public event Action OnLaned;

    public event Action OnStartedSprinting;
    public event Action OnStoppedSprinting;

    public event Action OnStartedCrouching;
    public event Action OnStoppedCrouching;

    // ============================================================
    // INTERNAL EVENT METHODS
    // ============================================================

    internal void RaiseJumped()
    {
        OnJumed?.Invoke();
    }
    internal void RaiseLanded()
    {
        OnLaned?.Invoke();
    }

    internal void RaiseStartedSprinting()
    {
        OnStartedSprinting?.Invoke();
    }
    internal void RaiseStoppedSprinting()
    {
        OnStoppedSprinting?.Invoke();
    }

    internal void RaiseStartedCrouching()
    {
        OnStartedCrouching?.Invoke();
    }
    internal void RaiseStoppedCrouching()
    {
        OnStartedCrouching?.Invoke();
    }
}

