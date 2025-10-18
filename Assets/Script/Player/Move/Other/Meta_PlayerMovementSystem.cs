using Mirror;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_PlayerMovementSystem")]
    [HelpURL("https://google.com")]
    public class Meta_PlayerMovementSystem : NetworkBehaviour
{
        [Header("References")]
        [SerializeField] private CharacterController Controller;
        [SerializeField] private Transform Player;
        [SerializeField] private Meta_GroundCheck GroundCheck;
        [SerializeField] private CinemachinePanTilt CameraDirection;
        [SerializeField] private Meta_PlayerAnimationSystem AnimSync;

        [Header("Inputs")]
        [SerializeField] private InputActionReference MoveAction;
        [SerializeField] private InputActionReference RunAction;
        [SerializeField] private InputActionReference JumpAction;

        [Header("Settings")]
        [SerializeField] private float MoveSpeed = 2f;
        [SerializeField] private float RunSpeed = 4f;
        [SerializeField] private float JumpForce = 1.5f;
        [SerializeField] private float Gravity = -9.81f;
        [SerializeField] private float AnimationMultiplier = 4f;

        private Vector2 MoveInput;
        private Vector3 Velocity;
        private bool IsGrounded;
        private bool IsRunning;
        private bool IsMoving;

        public override void OnStartAuthority()
        {
            MoveAction.action.Enable();
            RunAction.action.Enable();
            JumpAction.action.Enable();
        }

        private void Update()
        {
            if (!isLocalPlayer) return;

            MoveInput = MoveAction.action.ReadValue<Vector2>();
            IsRunning = RunAction.action.IsPressed();
            IsMoving = MoveInput.sqrMagnitude > 0.01f;
            IsGrounded = GroundCheck.Grounded;

            MoveHandler();
            JumpHandler();
            RotationHandler();
            AnimationHandler();
        }

        private void MoveHandler()
        {
            Vector3 _Move = Player.forward * MoveInput.y + Player.right * MoveInput.x;
            _Move.Normalize();

            float _Speed = IsRunning ? RunSpeed : MoveSpeed;
            Controller.Move(_Move * _Speed * Time.deltaTime);
        }

        private void JumpHandler()
        {
            if (IsGrounded && Velocity.y < 0)
                Velocity.y = -2f;

            if (IsGrounded && JumpAction.action.IsPressed())
                Velocity.y = Mathf.Sqrt(JumpForce * -2f * Gravity);

            Velocity.y += Gravity * Time.deltaTime;
            Controller.Move(Velocity * Time.deltaTime);
        }

        private void RotationHandler()
        {
            if (CameraDirection == null || Player == null) return;
            Player.rotation = Quaternion.Euler(0, CameraDirection.PanAxis.Value, 0);
        }

        private void AnimationHandler()
        {
            Vector2 _AnimInput = MoveInput * (IsRunning ? RunSpeed : MoveSpeed);
            _AnimInput *= AnimationMultiplier; // boost to match blend tree 0–4 range

            bool _WalkJump = !IsGrounded && IsMoving && !IsRunning;
            bool _RunJump = !IsGrounded && IsRunning;

            AnimSync.CmdSyncAnim(_AnimInput, IsGrounded, _WalkJump, _RunJump);
        }
    }
}