using UnityEngine;
using Mirror;
using System;
using UnityEngine.InputSystem;
using Unity.Cinemachine;

/// TODO => add ledge assist


namespace Meta.Player.Core
{
    [AddComponentMenu("Meta/Meta PlayerCore")]
    [HelpURL("https://github.com/DreamFaver")]
    [DisallowMultipleComponent]
    public class Meta_PlayerCore : NetworkBehaviour
    {
        public enum GroundState : byte { Grounded, Jumping, Falling }

        [Serializable]
        public struct PlayerAction
        {
            public InputActionReference MoveAction;
            public InputActionReference RunAction;
            public InputActionReference JumpAction;
        }

        [Serializable]
        public struct RuntimeData
        {
            [ReadOnly, SerializeField] float _Horizontal;
            [ReadOnly, SerializeField] float _Vertical;
            [ReadOnly, SerializeField] float _JumpForce;
            [ReadOnly, SerializeField] GroundState _GroundState;
            [ReadOnly, SerializeField] Vector3 _Direction;
            [ReadOnly, SerializeField] Vector2 _MoveInput;
            [ReadOnly, SerializeField] Vector3 _Velocity;
            [ReadOnly, SerializeField] bool _IsGrounded;

            #region Properties
            public float Horizontal
            {
                get => _Horizontal;
                internal set => _Horizontal = value;
            }
            public float Vertical
            {
                get => _Vertical;
                internal set => _Vertical = value;
            }
            public float JumpForce
            {
                get => _JumpForce;
                internal set => _JumpForce = value;
            }
            public GroundState PlayerGroundState
            {
                get => _GroundState;
                internal set => _GroundState = value;
            }
            public Vector3 Direction
            {
                get => _Direction;
                internal set => _Direction = value;
            }
            public Vector2 MoveInput
            {
                get => _MoveInput;
                internal set => _MoveInput = value;
            }
            public Vector3 Velocity
            {
                get => _Velocity;
                internal set => _Velocity = value;
            }
            public bool IsGrounded
            {
                get => _IsGrounded;
                internal set => _IsGrounded = value;
            }
            #endregion
        }

        [Header("References")]
        public CharacterController PlayerController;
        public Transform Player;
        public Camera Camera;
        //public Camera Camera;

        public Animator PlayerAnimation;

        [Header("Settings")]
        [Range(0,10)] public float MoveSpeed = 4f;
        [Range(0, 10)] public float RunSpeed = 6f;
        [Range(0, 10)] public float JumpForce = 2.5f;
        [Range(0, 10)] public float InitialJumpSpeed = 4f;
        [Range(0, 10)] public float MaxJumpSpeed = 4f;
        [Range(0, 10)] public float JumpAcceleration = 4f;
        [Range(0, 1)] public float CoyoteTimeDuration = 0.2f;
        //[Range(0, 5.5f)] public float LedgeTolerance = 0.2f;
        [Range(0, 10)] public float AnimationMultiplier = 1f;
        [Range(0, 10)] public float AnimationAcceleration = 2f;

        private float CoyoteTimer;

        [Header("Diagnostics")]
        public RuntimeData PlayerData;
        public PlayerAction PlayerInputAction;

        public bool EnableLog;

        [ReadOnly] public int HashPosX;
        [ReadOnly] public int HashPosZ;
        [ReadOnly] public int HashGrounded;
        [ReadOnly] public int HashWalkJump;
        [ReadOnly] public int HashRunJump;

        #region Network Setup
        protected override void OnValidate()
        {
            if (Application.isPlaying) return;

            base.OnValidate();
            Reset();
        }
        private void Reset()
        {
            if (PlayerController == null) PlayerController = transform.root.GetComponent<CharacterController>();

            PlayerController.enabled = false;
            PlayerController.skinWidth = 0.02f;
            PlayerController.minMoveDistance = 0f;

            this.enabled = false;
        }
        private void OnDisable()
        {
            PlayerInputAction.MoveAction.action.Disable();
            PlayerInputAction.RunAction.action.Disable();
            PlayerInputAction.JumpAction.action.Disable();

        }
        public override void OnStartAuthority()
        {
            PlayerInputAction.MoveAction.action.Enable();
            PlayerInputAction.RunAction.action.Enable();
            PlayerInputAction.JumpAction.action.Enable();

            HashPosX = Animator.StringToHash("PosX");
            HashPosZ = Animator.StringToHash("PosZ");
            HashGrounded = Animator.StringToHash("IsGrounded");
            HashWalkJump = Animator.StringToHash("WalkJump");
            HashRunJump = Animator.StringToHash("RunJump");

            PlayerController.enabled = true;

            if (Camera != null)
            {
                Camera.gameObject.SetActive(true);
            }

            this.enabled = true;
        }
        public override void OnStopAuthority()
        {
            PlayerInputAction.MoveAction.action.Disable();
            PlayerInputAction.RunAction.action.Disable();
            PlayerInputAction.JumpAction.action.Disable();

            PlayerController.enabled = false;

            if (Camera != null)
            {
                Camera.gameObject.SetActive(false);
            }

            this.enabled = false;
        }
        public override void OnStartClient()
        {
            base.OnStartClient();

            if (!isLocalPlayer && Camera != null)
            {
                Camera.gameObject.SetActive(false);
            }
        }
        #endregion
        void Update()
        {
            if (!PlayerController.enabled) return;

            float _DeltaTime = Time.deltaTime;

            MoveHandler();
            JumpHandler(_DeltaTime);
            ApplyMove(_DeltaTime);
            RotateHandler();
            AnimationHandler(_DeltaTime);

            if (PlayerController.isGrounded)
            {
                PlayerData.PlayerGroundState = GroundState.Grounded;
                CoyoteTimer = CoyoteTimeDuration;
            }
            else if (PlayerData.PlayerGroundState != GroundState.Jumping)
            {
                PlayerData.PlayerGroundState = GroundState.Falling;
            }
            else
            {
                if (CoyoteTimer > 0f)
                {
                    CoyoteTimer -= _DeltaTime;
                }
                if (PlayerData.PlayerGroundState != GroundState.Jumping)
                {
                    PlayerData.PlayerGroundState = GroundState.Falling;
                }
            }    

            PlayerData.Velocity = Vector3Int.FloorToInt(PlayerController.velocity);
        }
        public virtual void MoveHandler()
        {
            PlayerData.MoveInput = PlayerInputAction.MoveAction.action.ReadValue<Vector2>();

            PlayerData.Horizontal = PlayerData.MoveInput.x;
            PlayerData.Vertical = PlayerData.MoveInput.y;
        }
        public virtual void JumpHandler(float _DeltaTime)
        {
            bool _JumpPressed = PlayerInputAction.JumpAction.action.IsPressed();
            bool _CanCoyoteJump = CoyoteTimer > 0f;
            //bool _CanLedgeJump = false;

            //if (!PlayerData.IsGrounded && !_CanCoyoteJump)
            //{
            //    if (Physics.Raycast(Player.position, Player.forward, out RaycastHit _Hit, LedgeTolerance + 0.05f))
            //    {
            //        float _Dist = _Hit.distance;
            //        if (_Dist <= LedgeTolerance)
            //            _CanLedgeJump = true;
            //    }
            //}

            bool _CanJump = PlayerData.IsGrounded || _CanCoyoteJump /*|| _CanLedgeJump*/;
            if (_CanJump /*PlayerData.PlayerGroundState != GroundState.Falling*/&& _JumpPressed)
            {
                if (PlayerData.PlayerGroundState != GroundState.Jumping)
                {
                    PlayerData.PlayerGroundState = GroundState.Jumping;
                    PlayerData.JumpForce = InitialJumpSpeed;
                    CoyoteTimer = 0f;
                }
                else if (PlayerData.JumpForce < MaxJumpSpeed)
                {
                    float _JumpProgress = (PlayerData.JumpForce - InitialJumpSpeed) / (MaxJumpSpeed - InitialJumpSpeed);
                    PlayerData.JumpForce += (JumpAcceleration * MathF.Sqrt(1 - _JumpProgress)) * _DeltaTime;
                }

                if (PlayerData.JumpForce >= MaxJumpSpeed)
                {
                    PlayerData.JumpForce = MaxJumpSpeed;
                    PlayerData.PlayerGroundState = GroundState.Falling;
                }
            }
            else if (PlayerData.PlayerGroundState != GroundState.Grounded)
            {
                PlayerData.PlayerGroundState = GroundState.Falling;
                PlayerData.JumpForce = MathF.Min(PlayerData.JumpForce, MaxJumpSpeed);
                PlayerData.JumpForce += Physics.gravity.y * _DeltaTime;
            }
            else
            {
                PlayerData.JumpForce = Physics.gravity.y * _DeltaTime;
            }    
        }
        public virtual void RotateHandler()
        {
            if (PlayerAnimation == null)
                return;

            // Find the active Cinemachine camera under this player
            if (Camera == null)
            {
                Camera = GetActiveChildCamera();
                if (Camera == null)
                    return;
            }

            // Use camera's Y rotation to rotate PlayerAnimation
            float _CameraY = Camera.transform.rotation.eulerAngles.y;
            PlayerAnimation.transform.rotation = Quaternion.Euler(0f, _CameraY, 0f);
        }
        private Camera GetActiveChildCamera()
        {
            // Try to find any active camera under this player
            Camera[] _Cameras = GetComponentsInChildren<Camera>(true);
            foreach (Camera _Cam in _Cameras)
            {
                if (_Cam.isActiveAndEnabled)
                    return _Cam;
            }

            return null;
        }

        public virtual void AnimationHandler(float _DeltaTime)
        {
            bool IsRunning = PlayerInputAction.RunAction.action.IsPressed();
            bool IsJumping = PlayerInputAction.JumpAction.action.IsPressed();

            Vector2 _AnimInput = PlayerData.MoveInput * (IsRunning? RunSpeed : MoveSpeed);
            _AnimInput *= AnimationMultiplier; // boost to match blend tree 0–4 range

            bool _WalkJump = !PlayerData.IsGrounded && IsJumping && !IsRunning;
            bool _RunJump = !PlayerData.IsGrounded && IsJumping && IsRunning;

            PlayerAnimation.SetFloat(HashPosX, _AnimInput.x, AnimationAcceleration, _DeltaTime);
            PlayerAnimation.SetFloat(HashPosZ, _AnimInput.y, AnimationAcceleration, _DeltaTime);
            PlayerAnimation.SetBool(HashGrounded, PlayerController.isGrounded);
            PlayerAnimation.SetBool(HashWalkJump, _WalkJump);
            PlayerAnimation.SetBool(HashRunJump, _RunJump);
        }

        public virtual void ApplyMove(float _DeltaTime)
        {
            // Movement input
            PlayerData.Direction = new Vector3(PlayerData.Horizontal, 0f, PlayerData.Vertical);
            PlayerData.Direction = Vector3.ClampMagnitude(PlayerData.Direction, 1f);

            // Make it relative to PlayerAnimation’s facing direction
            Vector3 _Forward = PlayerAnimation != null ? PlayerAnimation.transform.forward : transform.forward;
            Vector3 _Right = PlayerAnimation != null ? PlayerAnimation.transform.right : transform.right;

            Vector3 _MoveDir = (_Forward * PlayerData.Direction.z + _Right * PlayerData.Direction.x).normalized;

            float _Speed = PlayerInputAction.RunAction.action.IsPressed() ? RunSpeed : MoveSpeed;
            Vector3 _FinalVelocity = _MoveDir * _Speed;

            // Apply jump/gravity
            _FinalVelocity.y = PlayerData.JumpForce;

            // Apply to controller
            PlayerController.Move(_FinalVelocity * _DeltaTime);

            // Store velocity for diagnostics
            PlayerData.Velocity = _FinalVelocity;
        }

    }
}