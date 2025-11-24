using Mirror;
using UnityEngine;
using Meta.Player.Core;

namespace Meta
{
    [AddComponentMenu("Meta/Vehicle Base")]
    [RequireComponent(typeof(Rigidbody))]
    [HelpURL("https://google.com")]
    public abstract class Meta_VehicleBase : NetworkBehaviour
    {
        [Header("Base Vehicle Settings")]
        public float EnginePower = 1000f;
        public float TurnSpeed = 50f;
        public Transform DriverSeat;

        [Header("References")]
        public Rigidbody Rb;
        public Meta_VehicleConfigurator Configurator;

        protected bool _HasControl = false;
        protected Meta_PlayerCore _DriverPlayer;
        protected Vector2 _MoveInput;
        protected Vector2 _LookInput;

        public virtual void Awake()
        {
            if (Rb == null)
                Rb = GetComponent<Rigidbody>();

            if (Configurator == null)
                Configurator = GetComponent<Meta_VehicleConfigurator>();
        }

        public virtual void EnableControl(Meta_PlayerCore driver)
        {
            _DriverPlayer = driver;
            _HasControl = true;
            Debug.Log($"[{name}] Control ENABLED for {_DriverPlayer.name}");
        }

        public virtual void DisableControl(Meta_PlayerCore driver)
        {
            if (_DriverPlayer == driver)
            {
                _HasControl = false;
                _DriverPlayer = null;
                _MoveInput = Vector2.zero;
                Debug.Log($"[{name}] Control DISABLED");
            }
        }

        public void SetInput(Vector2 move, Vector2 look)
        {
            if (_HasControl)
            {
                _MoveInput = move;
                _LookInput = look;
            }
        }

        protected virtual void FixedUpdate()
        {
            if (_HasControl)
                ApplyMovement();
        }

        protected abstract void ApplyMovement();
    }
}