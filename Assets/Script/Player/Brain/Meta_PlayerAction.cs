using System.Collections.Generic;
using UnityEngine;
using Mirror;
using System;
using UnityEngine.InputSystem;
using Unity.Cinemachine;

/*
	Documentation: https://mirror-networking.gitbook.io/docs/guides/networkbehaviour
	API Reference: https://mirror-networking.com/docs/api/Mirror.NetworkBehaviour.html
*/
namespace Meta.Player
{
    public enum PlayerState : byte { Grounded, Jumping, Falling, Walking, Running, Idle, Crouching}
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
        [ReadOnly, SerializeField] PlayerState _PlayerState;
        [ReadOnly, SerializeField] Vector3 _Direction;
        [ReadOnly, SerializeField] Vector2 _MoveInput;
        [ReadOnly, SerializeField] Vector3 _Velocity;
        [ReadOnly, SerializeField] float _CoyoteTimer;
        [ReadOnly, SerializeField] int _HashPosX;
        [ReadOnly, SerializeField] int _HashPosZ;
        [ReadOnly, SerializeField] int _HashGroounded;
        [ReadOnly, SerializeField] int _HashWalkJump;
        [ReadOnly, SerializeField] int _HashRunJump;
        [ReadOnly, SerializeField] bool _IsGrounded;

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
        public PlayerState PlayerState
        {
            get => _PlayerState;
            internal set => _PlayerState = value;
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
        public float CoyoteTimer
        {
            get => _CoyoteTimer;
            internal set => _CoyoteTimer = value;
        }
        public int HashPosX
        {
            get => _HashPosX;
            internal set => _HashPosX = value;
        }
        public int HashPosZ
        {
            get => _HashPosZ;
            internal set => _HashPosZ = value;
        }
        public int HashGroounded
        {
            get => _HashGroounded;
            internal set => _HashGroounded = value;
        }
        public int HashWalkJump
        {
            get => _HashWalkJump;
            internal set => _HashWalkJump = value;
        }
        public int HashRunJump
        {
            get => _HashRunJump;
            internal set => _HashRunJump = value;
        }
        public bool IsGrounded
        {
            get => _IsGrounded;
            internal set => _IsGrounded = value;
        }
    }
    [AddComponentMenu("Meta/Player Action")]
    [DisallowMultipleComponent]
    public class Meta_PlayerAction : NetworkBehaviour
    {

        [Header("References")]
        [SerializeField] private CharacterController PlayerController;
        [SerializeField] private Transform Player;
        [SerializeField] private CinemachinePanTilt CameraRotation;
        [SerializeField] private Camera CameraHolder;
        [SerializeField] private Animator PlayerAnimator;
        [SerializeField] private Meta_CursorAction PlayerCursor;

        [Header("Setting")]
        [SerializeField, Range(0, 10)] private float MoveSpeed = 4f;
        [SerializeField, Range(0, 10)] private float RunSpeed = 6f;
        //[SerializeField, Range(0, 10)] private float JumpForce = 2.5f;
        [SerializeField, Range(0, 10)] private float InitialJumpSpeed = 4f;
        [SerializeField, Range(0, 10)] private float MaxJumpSpeed = 4f;
        [SerializeField, Range(0, 10)] private float JumpAcceleration = 4f;
        [SerializeField, Range(0, 10)] private float CoyoteTimeDuration = 0.2f;
        //[SerializeField, Range(0, 10)] private float LedgeTolerance = 0.2f;
        [SerializeField, Range(0, 10)] private float AnimationMultiplier = 1f;
        [SerializeField, Range(0, 10)] private float AnimationAcceleration = 2f;

        [Header("Diagnostics")]
        public PlayerAction PlayerAction;
        public RuntimeData PlayerData;

        #region Unity Callbacks

        /// <summary>
        /// Add your validation code here after the base.OnValidate(); call.
        /// </summary>
        protected override void OnValidate()
        {
            if (Application.isPlaying) return;
            Reset();
        }

        private void Reset()
        {
            if (PlayerController == null) PlayerController = transform.root.GetComponent<CharacterController>();

            PlayerController.enabled = false;
            PlayerController.skinWidth = 0.02f;
            PlayerController.minMoveDistance = 0f;

            CameraHolder.gameObject.SetActive(false);
            PlayerCursor.enabled = false;
            
            enabled = false;
        }

        #endregion

        #region Start & Stop Callbacks
        /// <summary>
        /// Called when the local player object has been set up.
        /// <para>This happens after OnStartClient(), as it is triggered by an ownership message from the server. This is an appropriate place to activate components or functionality that should only be active for the local player, such as cameras and input.</para>
        /// </summary>
        public override void OnStartLocalPlayer()
        {
            PlayerAction.MoveAction.action.Enable();
            PlayerAction.JumpAction.action.Enable();
            PlayerAction.RunAction.action.Enable();

            PlayerData.HashPosX = Animator.StringToHash("PosX");
            PlayerData.HashPosZ = Animator.StringToHash("PosZ");
            PlayerData.HashGroounded = Animator.StringToHash("PosZ");
            PlayerData.HashWalkJump = Animator.StringToHash("PosZ");
            PlayerData.HashRunJump = Animator.StringToHash("PosZ");

            PlayerController.enabled = true;
            CameraHolder.gameObject.SetActive(true);
            enabled = true;
        }

        /// <summary>
        /// Called when the local player object is being stopped.
        /// <para>This happens before OnStopClient(), as it may be triggered by an ownership message from the server, or because the player object is being destroyed. This is an appropriate place to deactivate components or functionality that should only be active for the local player, such as cameras and input.</para>
        /// </summary>
        public override void OnStopLocalPlayer()
        {
            PlayerAction.MoveAction.action.Disable();
            PlayerAction.JumpAction.action.Disable();
            PlayerAction.RunAction.action.Disable();

            PlayerController.enabled = false;
            CameraHolder.gameObject.SetActive(false);
            PlayerCursor.enabled = false;

            enabled = false;
        }
        #endregion

        private void Update()
        {
            if (!isLocalPlayer) return;

            float _DeltaTime = Time.deltaTime;

            MoveHandler();
            JumpHandler(_DeltaTime);
            //AnimationHandler(_DeltaTime);
            ApplyAction(_DeltaTime);

            if (PlayerController.isGrounded)
            {
                PlayerData.PlayerState = PlayerState.Grounded;
                PlayerData.CoyoteTimer = CoyoteTimeDuration;
            }
            else if (PlayerData.PlayerState != PlayerState.Jumping)
            {
                PlayerData.PlayerState = PlayerState.Falling;
            }
            else
            {
                if (PlayerData.CoyoteTimer > 0f)
                {
                    PlayerData.CoyoteTimer -= _DeltaTime;
                }
                if (PlayerData.PlayerState != PlayerState.Jumping)
                {
                    PlayerData.PlayerState = PlayerState.Falling;

                }
            }
            PlayerData.Velocity = Vector3Int.FloorToInt(PlayerData.Velocity);
        }
        public virtual void MoveHandler()
        {
            PlayerData.MoveInput = PlayerAction.MoveAction.action.ReadValue<Vector2>();
            PlayerData.Horizontal = PlayerData.MoveInput.x;
            PlayerData.Vertical = PlayerData.MoveInput.y;

            Player.rotation = Quaternion.Euler(0f, CameraHolder.transform.rotation.y, 0f);
        }
        public virtual void JumpHandler(float _DeltaTime)
        {
            bool _JumpPressed = PlayerAction.JumpAction.action.IsPressed();
            bool _CanCoyoteJump = PlayerData.CoyoteTimer > 0f;
            bool _CanJump = PlayerData.IsGrounded || _CanCoyoteJump;
            if (_CanJump && _JumpPressed)
            {

                if (PlayerData.PlayerState != PlayerState.Jumping)
                {
                    PlayerData.PlayerState = PlayerState.Jumping;
                    PlayerData.JumpForce = InitialJumpSpeed;
                    PlayerData.CoyoteTimer = 0f;
                }
                else if (PlayerData.JumpForce < MaxJumpSpeed)
                {
                    float _JumpProgress = (PlayerData.JumpForce - InitialJumpSpeed) / (MaxJumpSpeed - InitialJumpSpeed);
                    PlayerData.JumpForce += (JumpAcceleration * Mathf.Sqrt(1 - _JumpProgress)) * _DeltaTime;
                }
                if (PlayerData.JumpForce >= MaxJumpSpeed)
                {
                    PlayerData.JumpForce = MaxJumpSpeed;
                    PlayerData.PlayerState = PlayerState.Falling;
                }
            }
            else if (PlayerData.PlayerState != PlayerState.Grounded)
            {
                PlayerData.PlayerState = PlayerState.Falling;
                PlayerData.JumpForce = Mathf.Min(PlayerData.JumpForce, MaxJumpSpeed);
                PlayerData.JumpForce += Physics.gravity.y * _DeltaTime;
            }
            else
            {
                PlayerData.JumpForce = Physics.gravity.y * _DeltaTime;
            }
        }
        public virtual void AnimationHandler(float _DeltaTime)
        {
            bool _IsRunning = PlayerAction.RunAction.action.IsPressed();
            bool _IsJumping = PlayerAction.JumpAction.action.WasPressedThisFrame();

            Vector2 _AnimInput = PlayerData.MoveInput * (_IsRunning ? RunSpeed : MoveSpeed);
            _AnimInput *= AnimationMultiplier;

            bool _WalkJump = !PlayerData.IsGrounded && _IsJumping && !_IsRunning;
            bool _RunJump = !PlayerData.IsGrounded && _IsJumping && _IsRunning;

            PlayerAnimator.SetFloat(PlayerData.HashPosX, _AnimInput.x, AnimationAcceleration, _DeltaTime);
            PlayerAnimator.SetFloat(PlayerData.HashPosZ, _AnimInput.y, AnimationAcceleration, _DeltaTime);

            PlayerAnimator.SetBool(PlayerData.HashGroounded, PlayerController.isGrounded);
            PlayerAnimator.SetBool(PlayerData.HashWalkJump, _WalkJump);
            PlayerAnimator.SetBool(PlayerData.HashRunJump, _RunJump);

        }
        public virtual void ApplyAction(float _DeltaTime)
        {
            PlayerData.Direction = new Vector3(PlayerData.Horizontal, 0f, PlayerData.Vertical);
            PlayerData.Direction = Vector3.ClampMagnitude(PlayerData.Direction, 1f);
            PlayerData.Direction = transform.TransformDirection(PlayerData.Direction);
            PlayerData.Direction *= PlayerAction.RunAction.action.IsPressed()? RunSpeed : RunSpeed;
            PlayerData.Direction = new Vector3(PlayerData.Direction.x, PlayerData.JumpForce, PlayerData.Direction.z);

            PlayerController.Move(PlayerData.Direction * _DeltaTime);
        }
    }
}