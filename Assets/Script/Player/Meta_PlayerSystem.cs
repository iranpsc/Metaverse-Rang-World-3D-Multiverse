using Mirror;
using Mirror.Examples.Common;
using System;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Meta.Player.System
{
    [AddComponentMenu("Meta/Player System")]
    [HelpURL("https://github.com/DreamFaver")]
    [DisallowMultipleComponent]
    public class Meta_PlayerSystem : NetworkBehaviour
    {

        [Header("References")]
        public CharacterController Controller;
        public Transform Player;
        public CinemachinePanTilt CameraRot;
        public Animator PlayerAnimation;
        [Header("Settings")]
        

        [Header("Inputs")]
        public PlayerInput PlayerInput;

        public enum PlayerState : byte
        {
            Grounded,
            Jumping,
            Falling,
            Driving,
            InUI,
        }
        [Serializable]
        public struct RuntimeData
        {
            [Header("Movement")]
            [ReadOnly, SerializeField] float _Horizontal;
            [ReadOnly, SerializeField] float _Vertical;
            [ReadOnly, SerializeField] float _JumpForce;
            [ReadOnly, SerializeField] Vector3 _Direction;
            [ReadOnly, SerializeField] Vector2 _MoveInput;
            [ReadOnly, SerializeField] Vector3 _Velocity;
            [Header("State")]
            [ReadOnly, SerializeField] PlayerState _PlayerState;
            [ReadOnly, SerializeField] bool _IsGrounded;
            [Header("Animation")]
            [ReadOnly, SerializeField] float _InitialJumpSpeed;
            [ReadOnly, SerializeField] float _CoyoteTimeDuration;
            [ReadOnly, SerializeField] float _CoyoteTimer;

            #region Properties
            // Movement
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
            // State
            public PlayerState PlayerState
            {
                get => _PlayerState;
                internal set => _PlayerState = value;
            }
            public bool IsGrounded
            {
                get => _IsGrounded;
                internal set => _IsGrounded = value;
            }
            // Animation
            public float InitialJumpSpeed
            {
                get => _InitialJumpSpeed;
                internal set => _InitialJumpSpeed = value;
            }
            public float CoyoteTimeDuration
            {
                get => _CoyoteTimeDuration;
                internal set => _CoyoteTimeDuration = value;
            }
            public float CoyoteTimer
            {
                get => _CoyoteTimer;
                internal set => _CoyoteTimer = value;
            }
            #endregion
        }

        [Header("Diagnostics")]
        [SerializeField] private bool EnableLog;
        public RuntimeData Data;
        #region Hashed Data
        [ReadOnly] public int HashPosX;
        [ReadOnly] public int HashPosZ;
        [ReadOnly] public int HashGrounded;
        [ReadOnly] public int HashWalkJump;
        [ReadOnly] public int HashRunJump;
        #endregion

        #region Networking
        protected override void OnValidate()
        {
            if (Application.isPlaying) return;
            //PlayerCamera?.SetActive(false);
            base.OnValidate();
            Reset();
        }
        private void Reset()
        {
            if (Controller == null) Controller = transform.root.GetComponent<CharacterController>();
            Controller.enabled = false;
            Controller.skinWidth = 0.02f;
            Controller.minMoveDistance = 0f;

            enabled = false;
        }
        private void OnDisable()
        {
            //PlayerAction.FindActionMap("Player").Disable();
        }
        public override void OnStartAuthority()
        {
            //PlayerAction.FindActionMap("Player").Enable();

            HashPosX = Animator.StringToHash("PosX");
            HashPosZ = Animator.StringToHash("PosZ");
            HashGrounded = Animator.StringToHash("IsGrounded");
            HashWalkJump = Animator.StringToHash("WalkJump");
            HashRunJump = Animator.StringToHash("RunJump");

            Controller.enabled = true;
            enabled = true;
        }
        public override void OnStopAuthority()
        {
            //PlayerAction.FindActionMap("Player").Disable();

            Controller.enabled = false;
            enabled = false;
        }
        #endregion
        void Start()
        {
            if (!isLocalPlayer)
            {
                CameraRot?.transform.parent.gameObject.SetActive(false); // turn off player camera for none local players
            }
            if (EnableLog) Debug.Log("[Meta_PlayerSystem] PutLogHere");
        }

        void Update()
        {
            
        }

        public virtual void MoveHandler(float _DeltaTime) { }
        public virtual void JumpHandler(float _DeltaTime) { }
        public virtual void RotationHandler(float _DeltaTime) { }
        public virtual void AnimationHandler(float _DeltaTime) { }
        public virtual void ApplyMove(float _DetlaTime)
        {
            Data.Direction = new Vector3(Data.Horizontal, 0f, Data.Vertical);
            Data.Direction = Vector3.ClampMagnitude(Data.Direction, 1f);
            Data.Direction = transform.TransformDirection(Data.Direction);
            //Data.Direction *= PlayerAction.FindAction
        }
    }
}