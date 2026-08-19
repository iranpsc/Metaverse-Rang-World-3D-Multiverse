using UnityEngine;

[AddComponentMenu("Meta RGB/Player/Base.Player Motor")]
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(PlayerState))]
public class PlayerMotor : MonoBehaviour
{
    // ============================================================
    // MOVEMENT
    // ============================================================

    [Header("Movement")]
    [SerializeField] private float WalkSpeed = 2f;
    [SerializeField] private float SprintSpeed = 4f;
    [SerializeField] private float CrouchSpeed = 1f;
    [SerializeField] private float Acceleration = 25f;
    [SerializeField] private float Deceleration = 30f;

    // ============================================================
    // JUMP
    // ============================================================

    [Header("Jump")]
    [SerializeField] private float JumpHeight = 1.2f;
    [SerializeField] private float Gravity = -25f;
    [SerializeField] private float GroundedVelocity = -2f;

    // ============================================================
    // CROUCH
    // ============================================================

    [Header("Crouch")]
    [SerializeField] private float StandingHeight = 2f;
    [SerializeField] private float CrouchingHeight = 1f;
    [SerializeField] private float CrouchTransitionSpeed = 10f;

    // ============================================================
    // REFERENCES
    // ============================================================

    private CharacterController Controller;
    private PlayerState State;

    // ============================================================
    // INTERNAL
    // ============================================================

    private Vector3 CurrentHorizontalVelocity;

    private bool JumpRequested;
    private bool SprintRequested;
    private bool CrouchRequested;

    // ============================================================
    // UNITY
    // ============================================================

    private void Awake()
    {
        Controller = GetComponent<CharacterController>();
        State = GetComponent<PlayerState>();

        Controller.height = StandingHeight;
        Controller.center = Vector3.up * (StandingHeight * 0.5f);
    }

    private void OnEnable()
    {
        if (PlayerInput.Instance == null)
        {
            Debug.LogError($"{nameof(PlayerMotor)} requires {nameof(PlayerInput)}.");
            return;
        }

        PlayerInput.Instance.OnJump += HandleJump;

        PlayerInput.Instance.OnSprintStarted += HandleSprintStarted;
        PlayerInput.Instance.OnSprintCanceled += HandleSprintCanceled;

        PlayerInput.Instance.OnCrouchStarted += HandleCrouchStarted;
        PlayerInput.Instance.OnCrouchCanceled += HandleCrouchCanceled;
    }

    private void OnDisable()
    {
        if (PlayerInput.Instance == null)
            return;

        PlayerInput.Instance.OnJump -= HandleJump;

        PlayerInput.Instance.OnSprintStarted -= HandleSprintStarted;
        PlayerInput.Instance.OnSprintCanceled -= HandleSprintCanceled;

        PlayerInput.Instance.OnCrouchStarted -= HandleCrouchStarted;
        PlayerInput.Instance.OnCrouchCanceled -= HandleCrouchCanceled;
    }

    private void Update()
    {
        UpdateGroundedState();
        UpdateMovement();
        UpdateVerticalMovement();
        UpdateCrouch();

        ApplyMovement();
        UpdateState();
    }

    // ============================================================
    // INPUT
    // ============================================================

    private void HandleJump()
    {
        JumpRequested = true;
    }

    private void HandleSprintStarted()
    {
        SprintRequested = true;
    }

    private void HandleSprintCanceled()
    {
        SprintRequested = false;
    }

    private void HandleCrouchStarted()
    {
        CrouchRequested = true;
    }

    private void HandleCrouchCanceled()
    {
        CrouchRequested = false;
    }

    // ============================================================
    // GROUND
    // ============================================================

    private void UpdateGroundedState()
    {
        State.WasGrounded = State.IsGrounded;

        State.IsGrounded = Controller.isGrounded;

        if (State.IsGrounded && !State.WasGrounded)
        {
            State.RaiseLanded();
        }
    }

    // ============================================================
    // HORIZONTAL MOVEMENT
    // ============================================================

    private void UpdateMovement()
    {
        if (PlayerInput.Instance == null)
            return;

        Vector2 _Input = PlayerInput.Instance.Move;

        Vector3 _MoveDirection =
            transform.forward * _Input.y +
            transform.right * _Input.x;

        _MoveDirection.y = 0f;

        _MoveDirection = Vector3.ClampMagnitude(
            _MoveDirection,
            1f
        );

        State.WorldMoveDirection = _MoveDirection;

        float _TargetSpeed = WalkSpeed;

        if (CrouchRequested)
        {
            _TargetSpeed = CrouchSpeed;
        }

        if (SprintRequested && _Input.sqrMagnitude > 0.01f)
        {
            _TargetSpeed = SprintSpeed;
        }

        Vector3 _TargetVelocity =
            _MoveDirection *
            _TargetSpeed *
            _Input.magnitude;

        float _Rate =
            _TargetVelocity.sqrMagnitude >
            CurrentHorizontalVelocity.sqrMagnitude
                ? Acceleration
                : Deceleration;

        CurrentHorizontalVelocity = Vector3.MoveTowards(
            CurrentHorizontalVelocity,
            _TargetVelocity,
            _Rate * Time.deltaTime
        );

        State.HorizontalSpeed =
            new Vector2(
                CurrentHorizontalVelocity.x,
                CurrentHorizontalVelocity.z
            ).magnitude;

        bool _ShouldSprint =
            SprintRequested &&
            _Input.sqrMagnitude > 0.01f &&
            State.HorizontalSpeed > 0.1f;

        if (_ShouldSprint != State.IsSprinting)
        {
            State.IsSprinting = _ShouldSprint;

            if (_ShouldSprint)
                State.RaiseStartedSprinting();
            else
                State.RaiseStoppedSprinting();
        }
    }

    // ============================================================
    // VERTICAL MOVEMENT
    // ============================================================

    private void UpdateVerticalMovement()
    {
        if (State.IsGrounded && State.Velocity.y < 0f)
        {
            State.Velocity = new Vector3(
                State.Velocity.x,
                GroundedVelocity,
                State.Velocity.z
            );
        }

        // --------------------------------------------------------
        // JUMP
        // --------------------------------------------------------

        if (JumpRequested)
        {
            if (State.IsGrounded && !CrouchRequested)
            {
                float _JumpVelocity =
                    Mathf.Sqrt(
                        JumpHeight *
                        -2f *
                        Gravity
                    );

                State.Velocity = new Vector3(
                    State.Velocity.x,
                    _JumpVelocity,
                    State.Velocity.z
                );

                State.IsJumping = true;

                State.RaiseJumped();
            }
        }
        else
        {
            State.Velocity +=
                Vector3.up *
                Gravity *
                Time.deltaTime;
        }

        // --------------------------------------------------------
        // RESET JUMP REQUEST
        // --------------------------------------------------------

        JumpRequested = false;

        // --------------------------------------------------------
        // LANDING
        // --------------------------------------------------------

        if (State.IsGrounded && State.Velocity.y <= 0f)
        {
            State.IsJumping = false;
        }
    }

    // ============================================================
    // CROUCH
    // ============================================================

    private void UpdateCrouch()
    {
        bool _ShouldCrouch =
            CrouchRequested &&
            State.IsGrounded;

        if (_ShouldCrouch != State.IsCrouching)
        {
            State.IsCrouching = _ShouldCrouch;

            if (_ShouldCrouch)
                State.RaiseStartedCrouching();
            else
                State.RaiseStoppedCrouching();
        }

        float _TargetHeight =
            State.IsCrouching
                ? CrouchingHeight
                : StandingHeight;

        Controller.height = Mathf.MoveTowards(
            Controller.height,
            _TargetHeight,
            CrouchTransitionSpeed *
            Time.deltaTime
        );

        Controller.center =
            Vector3.up *
            (Controller.height * 0.5f);
    }

    // ============================================================
    // APPLY MOVEMENT
    // ============================================================

    private void ApplyMovement()
    {
        Vector3 _Movement =
            CurrentHorizontalVelocity +
            Vector3.up *
            State.Velocity.y;

        Controller.Move(
            _Movement *
            Time.deltaTime
        );
    }

    // ============================================================
    // STATE
    // ============================================================

    private void UpdateState()
    {
        State.Velocity = new Vector3(
            CurrentHorizontalVelocity.x,
            State.Velocity.y,
            CurrentHorizontalVelocity.z
        );
    }
}