using Mirror;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Meta
{
    [AddComponentMenu("Meta/Meta PlayerMovement")]
    [HelpURL("https://google.com")]
    public class Meta_PlayerMovement : NetworkBehaviour
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
        [SerializeField] private float Acceleration = 0.1f;

        [Header("Settings")]
        [SerializeField] private float MoveSpeed = 2f;
        [SerializeField] private float RunSpeed = 4f;
        [SerializeField] private float JumpForce = 1.5f;
        [SerializeField] private float Gravity = -9.81f;
        [SerializeField] private float SyncRate = 0.05f;

        private Vector3 Velocity;
        private Vector3 ServerPosition;
        private Quaternion ServerRotation;
        private Vector2 MoveInput;
        private bool IsRunning;
        private bool IsGrounded;

        private float LastSyncTime;

        // Animator hashes
        private int HashPosX;
        private int HashPosZ;
        private int HashWalkJump;
        private int HashRunJump;
        private int HashGrounded;

        [Header("Network Debugger")]
        [SerializeField] private bool EnableLog;

        public override void OnStartAuthority()
        {
            MoveAction.action.Enable();
            RunAction.action.Enable();
            JumpAction.action.Enable();
            CrouchAction.action.Enable();

            HashPosX = Animator.StringToHash("PosX");
            HashPosZ = Animator.StringToHash("PosZ");
            HashWalkJump = Animator.StringToHash("WalkJump");
            HashRunJump = Animator.StringToHash("RunJump");
            HashGrounded = Animator.StringToHash("IsGrounded");

            if (EnableLog) Debug.Log("[Meta_PlayerMovement] Input enabled for local player.");
        }

        public override void OnStopAuthority()
        {
            MoveAction.action.Disable();
            RunAction.action.Disable();
            JumpAction.action.Disable();
            CrouchAction.action.Disable();
        }

        private void Update()
        {
            if (isLocalPlayer)
            {
                HandleLocalInput();
                HandleMovement();
                HandleJump();
                HandleAnimation();

                if (Time.time - LastSyncTime >= SyncRate)
                {
                    LastSyncTime = Time.time;
                    // Send actual blend values instead of raw input
                    float _animX = MoveInput.x * (IsRunning ? RunSpeed : MoveSpeed);
                    float _animZ = MoveInput.y * (IsRunning ? RunSpeed : MoveSpeed);
                    CmdSyncState(transform.position, transform.rotation, new Vector2(_animX, _animZ), IsRunning, IsGrounded);
                }
            }
            else
            {
                InterpolateRemotePlayer();
            }
        }

        private void HandleLocalInput()
        {
            MoveInput = MoveAction.action.ReadValue<Vector2>();
            IsRunning = RunAction.action.IsPressed();
        }

        private void HandleMovement()
        {
            RotationHandler();

            Vector3 _move = Player.forward * MoveInput.y + Player.right * MoveInput.x;
            _move.Normalize();

            float _speed = IsRunning ? RunSpeed : MoveSpeed;
            Controller.Move(_move * _speed * Time.deltaTime);
        }

        private void HandleJump()
        {
            IsGrounded = GroundCheck.Grounded;

            if (IsGrounded && Velocity.y < 0)
                Velocity.y = -2f;

            if (IsGrounded && JumpAction.action.triggered)
                Velocity.y = Mathf.Sqrt(JumpForce * -2f * Gravity);

            Velocity.y += Gravity * Time.deltaTime;
            Controller.Move(Velocity * Time.deltaTime);
        }

        private void HandleAnimation()
        {
            // Convert input to animator scale (0–4)
            float _animX = MoveInput.x * (IsRunning ? RunSpeed : MoveSpeed);
            float _animZ = MoveInput.y * (IsRunning ? RunSpeed : MoveSpeed);

            Anim.SetFloat(HashPosX, _animX, Acceleration, Time.deltaTime);
            Anim.SetFloat(HashPosZ, _animZ, Acceleration, Time.deltaTime);
            Anim.SetBool(HashGrounded, IsGrounded);

            bool _isJumping = !IsGrounded;
            Anim.SetBool(HashWalkJump, _isJumping && !IsRunning);
            Anim.SetBool(HashRunJump, _isJumping && IsRunning);
        }

        private void RotationHandler()
        {
            if (CameraDirection == null || Player == null) return;
            Player.rotation = Quaternion.Euler(0, CameraDirection.PanAxis.Value, 0);
        }

        private void InterpolateRemotePlayer()
        {
            transform.position = Vector3.Lerp(transform.position, ServerPosition, Time.deltaTime * 10f);
            transform.rotation = Quaternion.Slerp(transform.rotation, ServerRotation, Time.deltaTime * 10f);
        }

        // ---- Networking ----

        [Command]
        private void CmdSyncState(Vector3 _pos, Quaternion _rot, Vector2 _animBlend, bool _running, bool _grounded)
        {
            ServerPosition = _pos;
            ServerRotation = _rot;
            RpcSyncState(_pos, _rot, _animBlend, _running, _grounded);
        }

        [ClientRpc(includeOwner = false)]
        private void RpcSyncState(Vector3 _pos, Quaternion _rot, Vector2 _animBlend, bool _running, bool _grounded)
        {
            ServerPosition = _pos;
            ServerRotation = _rot;

            Anim.SetFloat("PosX", _animBlend.x, Acceleration, Time.deltaTime);
            Anim.SetFloat("PosZ", _animBlend.y, Acceleration, Time.deltaTime);
            Anim.SetBool("IsGrounded", _grounded);
            Anim.SetBool("WalkJump", !_grounded && !_running);
            Anim.SetBool("RunJump", !_grounded && _running);
        }
    }
}
