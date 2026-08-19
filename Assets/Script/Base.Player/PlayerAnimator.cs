using UnityEngine;

[AddComponentMenu("Meta RGB/Player/Base.Player Animator")]
[RequireComponent(typeof(Animator))]
[RequireComponent(typeof(PlayerState))]
public class PlayerAnimator : MonoBehaviour
{
    // ============================================================
    // SETTINGS
    // ============================================================

    [Header("Movement")]
    [SerializeField] private float WalkAnimationSpeed = 2f;
    [SerializeField] private float RunAnimationSpeed = 4f;

    [Header("Smoothing")]
    [SerializeField] private float MovementSmoothTime = 10f;
    [SerializeField] private float MovementDeadZone = 0.05f;

    // ============================================================
    // REFERENCES
    // ============================================================

    private Animator Animator;
    private PlayerState State;

    // ============================================================
    // INTERNAL
    // ============================================================

    private Vector2 CurrentBlendPosition;

    // ============================================================
    // ANIMATOR PARAMETERS
    // ============================================================

    private static readonly int PosXHash =
        Animator.StringToHash("PosX");

    private static readonly int PosZHash =
        Animator.StringToHash("PosZ");

    private static readonly int WalkJumpHash =
        Animator.StringToHash("WalkJump");

    private static readonly int RunJumpHash =
        Animator.StringToHash("RunJump");

    private static readonly int IsDrivingHash =
        Animator.StringToHash("IsDriving");

    private static readonly int CrouchHash =
        Animator.StringToHash("Crouch");

    private static readonly int IsGroundedHash =
        Animator.StringToHash("IsGrounded");

    private static readonly int IdleHash =
        Animator.StringToHash("Idle");

    // ============================================================
    // UNITY
    // ============================================================

    private void Awake()
    {
        Animator = GetComponent<Animator>();
        State = GetComponent<PlayerState>();
    }

    private void Update()
    {
        UpdateMovement();
        UpdateJump();
        UpdateCrouch();
        UpdateGrounded();
        UpdateIdle();
        UpdateDriving();
    }

    // ============================================================
    // MOVEMENT
    // ============================================================

    private void UpdateMovement()
    {
        Vector3 _WorldDirection = State.WorldMoveDirection;

        _WorldDirection.y = 0f;

        if (_WorldDirection.sqrMagnitude > 0.001f)
        {
            _WorldDirection.Normalize();
        }

        Vector3 _LocalDirection =
            transform.InverseTransformDirection(_WorldDirection);

        float _AnimationSpeed =
            State.IsSprinting
                ? RunAnimationSpeed
                : WalkAnimationSpeed;

        Vector2 _TargetBlendPosition =
            new Vector2(
                _LocalDirection.x,
                _LocalDirection.z
            ) * _AnimationSpeed;

        CurrentBlendPosition = Vector2.Lerp(
            CurrentBlendPosition,
            _TargetBlendPosition,
            MovementSmoothTime * Time.deltaTime
        );

        // --------------------------------------------------------
        // DEAD ZONE
        // --------------------------------------------------------

        if (Mathf.Abs(CurrentBlendPosition.x) < MovementDeadZone)
        {
            CurrentBlendPosition.x = 0f;
        }

        if (Mathf.Abs(CurrentBlendPosition.y) < MovementDeadZone)
        {
            CurrentBlendPosition.y = 0f;
        }

        Animator.SetFloat(
            PosXHash,
            CurrentBlendPosition.x
        );

        Animator.SetFloat(
            PosZHash,
            CurrentBlendPosition.y
        );
    }

    // ============================================================
    // JUMP
    // ============================================================

    private void UpdateJump()
    {
        bool _RunJump =
            State.IsJumping &&
            State.IsSprinting;

        bool _WalkJump =
            State.IsJumping &&
            !State.IsSprinting;

        Animator.SetBool(
            WalkJumpHash,
            _WalkJump
        );

        Animator.SetBool(
            RunJumpHash,
            _RunJump
        );
    }

    // ============================================================
    // CROUCH
    // ============================================================

    private void UpdateCrouch()
    {
        Animator.SetBool(
            CrouchHash,
            State.IsCrouching
        );
    }

    // ============================================================
    // GROUNDED
    // ============================================================

    private void UpdateGrounded()
    {
        Animator.SetBool(
            IsGroundedHash,
            State.IsGrounded
        );
    }

    // ============================================================
    // IDLE
    // ============================================================

    private void UpdateIdle()
    {
        bool _Idle =
            State.IsGrounded &&
            !State.IsJumping &&
            !State.IsCrouching &&
            State.HorizontalSpeed < 0.1f;

        Animator.SetBool(
            IdleHash,
            _Idle
        );
    }

    // ============================================================
    // DRIVING
    // ============================================================

    private void UpdateDriving()
    {
        Animator.SetBool(
            IsDrivingHash,
            false
        );
    }
}