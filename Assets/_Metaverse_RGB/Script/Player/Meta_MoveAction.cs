using Mirror;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Meta
{
    [AddComponentMenu("Meta/Move Action (CC)")]
    [HelpURL("https://google.com")]
    public class Meta_MoveAction : NetworkBehaviour
    {
        [Header("References")]
        [SerializeField] private CharacterController Controller;
        [SerializeField] private Transform Player;
        [SerializeField] private Meta_GroundCheck GroundCheck;
        [SerializeField] private CinemachinePanTilt CameraDirection;

        [Header("Inputs")]
        [SerializeField] private InputActionReference MoveAction;
        [SerializeField] private InputActionReference RunAction;
        [SerializeField] private InputActionReference JumpAction;
        [SerializeField] private InputActionReference CrouchAction;

        [Header("Animations")]
        [SerializeField] private Animator Anim;
        [SerializeField] private float CurrentPosZ;
        [SerializeField] private float CurrentPosX;
        [SerializeField] private float Acceleration = 0.1f;

        [Header("Settings")]
        [SerializeField] private float MoveSpeed = 2f;
        [SerializeField] private float RunSpeed = 4f;
        [SerializeField] private float CurrentSpeed;
        [SerializeField] private float JumpForce = 1.5f;
        [SerializeField] private float Gravity = -9.81f;

        private bool IsMoving;
        private bool IsRunning;

        int HashPosX;
        int HashPosZ;
        int HashWalkJump;
        int HashRunJump;
        int HashGrounded;

        private Vector2 MoveInput;
        private Vector3 Velocity;
        private bool IsGrounded;

        [Header("Network Debugger")]
        [SerializeField] private bool EnableLog;

        public override void OnStartAuthority()
        {
            MoveAction.action.Enable();
            JumpAction.action.Enable();
            CrouchAction.action.Enable();

            HashPosX = Animator.StringToHash("PosX");
            HashPosZ = Animator.StringToHash("PosZ");
            HashWalkJump = Animator.StringToHash("WalkJump");
            HashRunJump = Animator.StringToHash("RunJump");
            HashGrounded = Animator.StringToHash("IsGrounded");

            if (EnableLog) Debug.Log("[Meta_MoveAction] Input enabled for local player.");
        }
        public override void OnStopAuthority()
        {
            MoveAction.action.Disable();
            JumpAction.action.Disable();
            CrouchAction.action.Disable();
        }
        private void Update()
        {
            if (!isLocalPlayer) return;
            MoveInput = MoveAction.action.ReadValue<Vector2>();
            IsRunning = RunAction.action.IsPressed();
            IsMoving = MoveInput.sqrMagnitude > 0.01f;
            CurrentSpeed = IsRunning ? RunSpeed : MoveSpeed;

            AnimationHandler();
            MoveHandler();
            JumpHandler();
        }

        private void AnimationHandler()
        {
            // Get Move and Run Speed
            CurrentPosX = MoveInput.x * CurrentSpeed;
            CurrentPosZ = MoveInput.y * CurrentSpeed;

            //TEMP
            Vector2 NetAnim = new Vector2(CurrentPosX, CurrentPosZ);
            //TEMP
            CmdSendMove(NetAnim, IsRunning);
            // Smooth Damping
            //Anim.SetFloat(HashPosX, CurrentPosX, Acceleration, Time.deltaTime);
            //Anim.SetFloat(HashPosZ, CurrentPosZ, Acceleration, Time.deltaTime);

            // Reset Value
            if (!IsMoving)
            {
                if (Mathf.Abs(Anim.GetFloat(HashPosX)) < 0.01f)
                    Anim.SetFloat(HashPosX, 0f);
                if (Mathf.Abs(Anim.GetFloat(HashPosZ)) < 0.01f)
                    Anim.SetFloat(HashPosZ, 0f);
            }
        }

        private void MoveHandler()
        {
            RotationHandler();
            Vector3 _Move = Player.forward * CurrentPosZ + Player.right * CurrentPosX;
            _Move.Normalize();
            float _RunSpeed = RunAction.action.IsPressed() ? RunSpeed : 1f;
            Controller.Move(_Move * (MoveSpeed * _RunSpeed) * Time.deltaTime);
        }

        private void JumpHandler()
        {
            IsGrounded = GroundCheck.IsGrounded;

            if (IsGrounded && Velocity.y < 0)
                Velocity.y = -2f;

            if (IsGrounded && JumpAction.action.IsPressed())
            {
                Velocity.y = Mathf.Sqrt(JumpForce * -2f * Gravity);
            }

            bool isJumping = !IsGrounded;

            if (IsMoving && isJumping && !IsRunning)
            {
                //Anim.SetBool(HashWalkJump, isJumping);
                CmdSendJump(isJumping, IsMoving, false);
            }
            else if (IsRunning && isJumping)
            {
                //Anim.SetBool(HashRunJump, isJumping);
                CmdSendJump(isJumping, false, IsRunning);

            }
            else
            {
                //Anim.SetBool(HashWalkJump, false);
                CmdSendJump(IsGrounded, false, false);
                //Anim.SetBool(HashRunJump, false);
                CmdSendJump(IsGrounded, false, false);
            }
            Anim.SetBool(HashGrounded, IsGrounded);

            Velocity.y += Gravity * Time.deltaTime;
            Controller.Move(Velocity * Time.deltaTime);
        }

        private void RotationHandler()
        {
            if (CameraDirection == null || Player == null) return;

            Player.rotation = Quaternion.Euler(0, CameraDirection.PanAxis.Value, 0);
        }

        [Command]
        void CmdSendMove(Vector2 moveInput, bool running)
        {
            RpcUpdateAnim(moveInput, running);
        }
        [Command]
        void CmdSendJump(bool grounded, bool WalkJump, bool RunJump)
        {
            RpcUpdateJump(grounded, WalkJump, RunJump);
        }

        [ClientRpc]
        void RpcUpdateAnim(Vector2 moveInput, bool running)
        {
            Anim.SetFloat("PosX", moveInput.x, Acceleration, Time.deltaTime);
            Anim.SetFloat("PosZ", moveInput.y, Acceleration, Time.deltaTime);
        }

        [ClientRpc]
        void RpcUpdateJump(bool grounded, bool WalkJump, bool RunJump)
        {
            Anim.SetBool("IsGrounded", grounded);
            Anim.SetBool("WalkJump", WalkJump);
            Anim.SetBool("RunJump", RunJump);
        }
    }
}
