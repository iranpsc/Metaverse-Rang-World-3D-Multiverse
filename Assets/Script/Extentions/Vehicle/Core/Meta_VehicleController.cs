using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_VehicleController")]
    [HelpURL("https://google.com")]
    public class Meta_VehicleController : NetworkBehaviour
    {
        [Header("References")]
        protected Meta_VehicleConfigurator Configurator;
        protected Rigidbody RB;

        [Header("Input References")]
        public InputActionReference MoveAction;
        public InputActionReference BrakeAction;
        public InputActionReference SignalLeftAction;
        public InputActionReference SignalRightAction;

        [Header("Stats")]
        public float Acceleration = 800f;
        public float TurnSpeed = 60f;
        public float BrakeForce = 1200f;

        private Vector2 MoveInput;
        private bool IsBraking;

        private void OnEnable()
        {
            if (isLocalPlayer)
            {
                MoveAction.action.Enable();
                BrakeAction.action.Enable();
                SignalLeftAction.action.Enable();
                SignalRightAction.action.Enable();
            }
        }
        private void Start()
        {
            if (Configurator == null)
                Configurator = GetComponent<Meta_VehicleConfigurator>();

            RB = GetComponent<Rigidbody>();

            if (isLocalPlayer)
            {
                MoveAction.action.performed += OnMove;
                MoveAction.action.canceled += OnMove;
                BrakeAction.action.performed += ctx => IsBraking = true;
                BrakeAction.action.performed -= ctx => IsBraking = false;
            }
        }

        private void OnDestroy()
        {
            if (isLocalPlayer)
            {
                MoveAction.action.Disable();
                BrakeAction.action.Disable();
                SignalLeftAction.action.Disable();
                SignalRightAction.action.Disable();

                MoveAction.action.performed -= OnMove;
                MoveAction.action.canceled -= OnMove;
            }
        }

        private void FixedUpdate()
        {
            if (!isLocalPlayer) return;
            HandleMovement();
        }

        private void OnMove(InputAction.CallbackContext _Ctx)
        {
            MoveInput = _Ctx.ReadValue<Vector2>();
        }
        private void HandleMovement()
        {
            float _Forward = MoveInput.y;
            float _Turn = MoveInput.x;

            if (IsBraking)
            {
                RB.linearDamping = 3f;
                //Configurator.Lights.SeatBrakeLights(false);
                RB.AddForce(transform.forward * _Forward * Acceleration * Time.fixedDeltaTime, ForceMode.Acceleration);
                RB.AddTorque(Vector3.up * _Turn * TurnSpeed * Time.fixedDeltaTime);
            }
        }
    }
}