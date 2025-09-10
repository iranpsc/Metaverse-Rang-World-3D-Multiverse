using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

namespace Meta
{
    [HelpURL("GitHub")]
    [AddComponentMenu("Meta/Meta PlayerMovement")]
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(CapsuleCollider))]
    [RequireComponent(typeof(NetworkIdentity))]
    [DisallowMultipleComponent]
    public class Meta_PlayerMovement : NetworkBehaviour
    {
        public enum GroundState : byte { Grounded, Jumping, Falling }

        public InputActionAsset PlayerInput;
        public InputAction MoveAction;
        public InputAction JumpAction;

        [Header("Avatar Component")]
        public Rigidbody Rb;
        public CapsuleCollider Collider;
        #region
        [Header("Movement")]
        [Range(0, 20)]
        [FormerlySerializedAs("MoveSpeedMultiplier")]
        public float MaxMoveSpeed = 2f;

        [Range(0, 10f)]
        public float InputSensitivity = 2f;

        [Range(0, 10f)]
        public float InputGravity = 2f;

        [Range(0, 10f)]
        public float InitialJumpSpeed = 2.5f;

        [Range(0, 10f)]
        public float MaxJumpSpeed = 3.5f;

        [Range(0, 10f)]
        [FormerlySerializedAs("JumpDelta")]
        public float JumpAcceleration = 4f;
        #endregion
        [Header("Debugger")]
        public bool EnableLog;

        #region Networking
        protected override void OnValidate()
        {
            // Skip if Editor is in Play mode
            if (Application.isPlaying) return;

            base.OnValidate();
            Reset();
        }

        public virtual void Reset()
        {
            if(Rb == null) Rb = GetComponent<Rigidbody>();
            if(Collider == null) Collider = GetComponent<CapsuleCollider>();

            Rb.useGravity = true;
            Rb.interpolation = RigidbodyInterpolation.None;
            Rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            Rb.isKinematic = true;

            Rb.constraints = RigidbodyConstraints.FreezeRotationZ;

            this.enabled = false;
        }


        #endregion

        private void Awake()
        {
            var _Map = PlayerInput.FindActionMap("Player");
            if (_Map == null)
            {
                enabled = false;
                return;
            }
            MoveAction = _Map.FindAction("Move");
            JumpAction = _Map.FindAction("Jump");

            if (MoveAction == null || JumpAction == null)
            {
                enabled = false;
                return;
            }
        }

        private void OnEnable()
        {
            PlayerInput.FindActionMap("Player")?.Enable();
        }
        private void OnDisable()
        {
            PlayerInput.FindActionMap("Player")?.Disable();
        }

        void Start()
        {
            if (EnableLog) Debug.Log("[Meta_PlayerMovement] Player Movement System Initialized.");

            Application.targetFrameRate = NetworkManager.singleton.sendRate;
            Time.fixedDeltaTime = 1f / NetworkManager.singleton.sendRate;
        }

        void FixedUpdate()
        {

        }
        private void PlayerHandler()
        {

        }

    }
}