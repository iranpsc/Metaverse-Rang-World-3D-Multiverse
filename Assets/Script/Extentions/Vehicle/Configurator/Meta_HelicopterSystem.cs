using Mirror;
using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Meta.Vehicle
{
    // A structural note: Since your Meta_VehicleSystem uses a nested RuntimeData, 
    // we'll define a simple one here and use the 'new' keyword to shadow the base class field.
    // In a final unified project, you might make a generic RuntimeData in Meta_VehicleBase.
    [Serializable]
    public struct HelicopterRuntimeData
    {
        [ReadOnly, SerializeField] public float CollectiveInput; // Vertical/Lift input
        [ReadOnly, SerializeField] public Vector2 CyclicInput;   // Pitch (Y) and Roll (X) input
        [ReadOnly, SerializeField] public float RudderInput;     // Yaw input
        [ReadOnly, SerializeField] public float MainRotorSpeed;  // Current rotor RPM/speed
        [ReadOnly, SerializeField] public float VerticalSpeed;   // Vertical velocity magnitude
        [ReadOnly, SerializeField] public bool IsInFlight;
        [ReadOnly, SerializeField] public bool EngineOn;
    }

    [AddComponentMenu("Meta/Helicopter System")]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/DreamFaver")]
    public class Meta_HelicopterSystem : Meta_VehicleBase
    {
        // Define new keybindings structure for flight controls
        [Serializable]
        public struct FlightKeys
        {
            public InputActionReference Collective; // Controls Altitude/Thrust (e.g., Space/LShift)
            public InputActionReference Cyclic;     // Controls Pitch and Roll (e.g., WASD or Left Stick)
            public InputActionReference Rudder;     // Controls Yaw/Rotation (e.g., Q/E or Right Stick X)

            // Existing ones can be reused from the base class
            public InputActionReference HeadLightToggle;
        }

        public Rigidbody Rb;
        public FlightKeys flightKeys;
        // REVISED: ApplyCollective()

        public float GravityForce; // NEW: Defined in FixedUpdate for cleaner access
        public float MinimumLiftFactor = 0.5f; // NEW: Set this to 0.5f in the Inspector

        [Header("Flight Dynamics")]
        [Tooltip("Maximum upward force applied by the main rotor.")]
        public float MaxCollectiveForce = 50000f;

        [Tooltip("Engine multiplier for power output.")]
        public float EnginePower = 25f;

        [Tooltip("Torque applied for Pitch (forward/backward) and Roll (sideways).")]
        public float PitchRollTorque = 2500f;

        [Tooltip("Torque applied for Yaw (rotation).")]
        public float YawTorque = 700f;

        [Tooltip("Factor to help keep the helicopter level when no Cyclic input is given (Arcade style).")]
        public float StabilizationFactor = 0.5f;

        [Tooltip("Rate at which rotors spin up.")]
        public float SpinUpRate = 150f;

        [Tooltip("Maximum visual RPM for the main rotor.")]
        public float MaxRotorVisualRPM = 1500f;

        private bool HeadLightOn;

        // Runtime Data - Use 'new' to implement the helicopter-specific struct
        public HelicopterRuntimeData runtimeData;

        // --- Setup and Authority ---

        protected override void OnValidate()
        {
            if (Rb == null) Rb = GetComponent<Rigidbody>();
        }

        public override void OnStartAuthority()
        {
            base.OnStartAuthority();

            // Enable new flight action maps
            if (flightKeys.Collective != null) flightKeys.Collective.action.Enable();
            if (flightKeys.Cyclic != null) flightKeys.Cyclic.action.Enable();
            if (flightKeys.Rudder != null) flightKeys.Rudder.action.Enable();

            // Setup light toggles using the base class methods/hooks
            if (flightKeys.HeadLightToggle != null) flightKeys.HeadLightToggle.action.performed += OnHeadLightInput;
        }

        public override void OnStopAuthority()
        {
            base.OnStopAuthority();

            // Disable flight action maps
            if (flightKeys.Collective != null) flightKeys.Collective.action.Disable();
            if (flightKeys.Cyclic != null) flightKeys.Cyclic.action.Disable();
            if (flightKeys.Rudder != null) flightKeys.Rudder.action.Disable();

            // Cleanup light toggles
            if (flightKeys.HeadLightToggle != null) flightKeys.HeadLightToggle.action.performed -= OnHeadLightInput;
        }

        public virtual void OnHeadLightInput(InputAction.CallbackContext _Context)
        {
            // Ensure the action was performed (button pressed)
            if (!_Context.performed) return;

            // Check if the local client has authority (is the driver)
            if (!isOwned || !HasDriver) return;

            // Toggle the state and send the command to the server
            CmdSetHeadLight(!HeadLightOn);
        }
        [Command]
        public void CmdSetHeadLight(bool _State)
        {
            // The server receives the command and sets the SyncVar
            HeadLightOn = _State;
        }
        // --- Update Loop for Visuals and Non-Physics ---

        private void Update()
        {
            if (isOwned)
            {
                if (HasDriver)
                {
                    // Handle lights/signals using base methods (assuming they are implemented there)
                    RotateRotors();
                }
                else
                {
                    // Decelerate rotors when unoccupied
                    runtimeData.MainRotorSpeed = Mathf.Lerp(runtimeData.MainRotorSpeed, 0f, Time.deltaTime * 0.5f);
                    RotateRotors();
                }
            }
        }

        // --- Physics Loop ---

        private void FixedUpdate()
        {
            if (isOwned && Rb != null)
            {
                runtimeData.IsInFlight = !Physics.Raycast(transform.position, Vector3.down, 1.0f); // Simple check

                if (HasDriver)
                {
                    HandleDriverInput();
                    ApplyStabilization();
                }
                else if (runtimeData.IsInFlight)
                {
                    // Apply weak collective force to slow descent on engine idle/off
                    ApplyIdleCollective();
                }
            }
        }

        // --- Core Flight Logic ---

        // This is the main method called from FixedUpdate when there is a driver.
        public override void HandleDriverInput()
        {
            ReadInput();

            ApplyCollective();
            ApplyCyclic();
            ApplyRudder();

            // Apply drag to simulate air resistance
            Rb.linearDamping = 0.5f;
            Rb.angularDamping = 0.5f;
        }

        protected virtual void ReadInput()
        {
            // Read all inputs into runtime data
            runtimeData.CollectiveInput = flightKeys.Collective.action.ReadValue<float>();
            runtimeData.CyclicInput = flightKeys.Cyclic.action.ReadValue<Vector2>();
            runtimeData.RudderInput = flightKeys.Rudder.action.ReadValue<float>();

            // Spin up the main rotor to max speed based on collective input
            if (runtimeData.CollectiveInput > 0.1f)
            {
                runtimeData.MainRotorSpeed = Mathf.MoveTowards(runtimeData.MainRotorSpeed, MaxRotorVisualRPM, Time.fixedDeltaTime * SpinUpRate);
            }
            else
            {
                // Decelerate naturally
                runtimeData.MainRotorSpeed = Mathf.Lerp(runtimeData.MainRotorSpeed, 0f, Time.fixedDeltaTime * 0.5f);
            }

            runtimeData.EngineOn = runtimeData.MainRotorSpeed > 100f;
        }



        public virtual void ApplyCollective()
        {
            if (!runtimeData.EngineOn) return;

            // Gravity is constant downward acceleration (F = m * g)
            // We get this value in FixedUpdate or cache it in Start
            // For simplicity, let's calculate the required force to fight gravity here:
            GravityForce = Rb.mass * Physics.gravity.magnitude;

            // Calculate the input lift, based on the player's 1D axis input (-1.0 to 1.0)
            float inputLift = runtimeData.CollectiveInput;

            // 1. Calculate the BASE lift required to hover (equal to gravity force).
            float baseLift = GravityForce;

            // 2. Determine the player's MODIFIER lift.
            // We want the player to control lift relative to the base lift.
            // If inputLift is 0, we apply baseLift.
            // If inputLift is 1, we add MaxCollectiveForce.
            // If inputLift is -1, we reduce lift toward a minimum factor (e.g., 50% of base lift).

            float calculatedLift = 0f;

            if (inputLift >= 0) // Ascending or Hovering (Input 0 to 1)
            {
                // Add lift power on top of the base lift.
                calculatedLift = baseLift + (inputLift * MaxCollectiveForce * EnginePower);
            }
            else // Descending (Input 0 to -1)
            {
                // Reduce lift down to a minimum level (e.g., 50% of the force needed to hover)
                // This prevents the instant 0 lift drop.
                float minSafeLift = baseLift * MinimumLiftFactor;

                // Lerp from baseLift (at input 0) down to minSafeLift (at input -1).
                calculatedLift = Mathf.Lerp(baseLift, minSafeLift, -inputLift);
            }

            // Apply the final upward force
            Vector3 collectiveForce = transform.up * calculatedLift;
            Rb.AddForce(collectiveForce, ForceMode.Force); // Use ForceMode.Force for continuous force over mass/time

            // NOTE: Keep the counter-torque calculation from the previous script here.
            // Vector3 counterTorque = transform.up * runtimeData.RudderInput * -YawTorque * 0.5f;
            // Rb.AddRelativeTorque(counterTorque, ForceMode.Acceleration);
        }

        public virtual void ApplyIdleCollective()
        {
            // Apply a very small upward force to simulate slow rotor descent or idling.
            float idleCollective = MaxCollectiveForce * 0.1f;
            Rb.AddForce(transform.up * idleCollective, ForceMode.Force);
        }

        // REVISED: ApplyCyclic()
        public virtual void ApplyCyclic()
        {
            if (!runtimeData.EngineOn) return;

            Vector2 cyclicInput = runtimeData.CyclicInput;

            // 1. PITCH (Nose up/down - Rotation around X axis)
            float pitchInput = cyclicInput.y;
            float pitchTorque = pitchInput * PitchRollTorque;
            Rb.AddRelativeTorque(Vector3.right * pitchTorque, ForceMode.Acceleration);

            // 2. ROLL (Tilt left/right - Rotation around Z axis)
            float rollInput = cyclicInput.x;
            float rollTorque = -rollInput * PitchRollTorque; // Negative for standard controls
            Rb.AddRelativeTorque(Vector3.forward * rollTorque, ForceMode.Acceleration);

            // 3. APPLY DIRECTIONAL FORCE (The key fix for forward/backward movement)
            // The force is applied based on the INPUT, not the current tilt, to make it responsive.

            // Forward/Backward Force: Apply force along the helicopter's forward vector (transform.forward).
            // Use MaxCollectiveForce as a base to ensure the force is substantial.
            Vector3 forwardForce = transform.forward * pitchInput * (MaxCollectiveForce * 0.1f);

            // Side Force: Apply force along the helicopter's right vector (transform.right).
            Vector3 sideForce = transform.right * rollInput * (MaxCollectiveForce * 0.1f);

            // Apply both forces. Use ForceMode.Acceleration to ignore mass, making it feel snappier.
            Rb.AddForce(forwardForce + sideForce, ForceMode.Acceleration);
        }

        public virtual void ApplyRudder()
        {
            if (!runtimeData.EngineOn) return;

            // Rudder controls Yaw (Rotation around Y axis) via the tail rotor
            float yawInput = runtimeData.RudderInput;
            float yawTorque = yawInput * YawTorque;

            // Apply torque to rotate the helicopter
            Rb.AddRelativeTorque(Vector3.up * yawTorque, ForceMode.Acceleration);
        }

        public virtual void ApplyStabilization()
        {
            if (runtimeData.CollectiveInput > 0.1f && runtimeData.EngineOn)
            {
                // Simple stabilization: tries to right itself to a level pitch/roll, 
                // but keeps current yaw (Y-axis rotation).
                Quaternion targetRotation = Quaternion.Euler(0, transform.rotation.eulerAngles.y, 0);

                // Only stabilize if the user is not actively tilting (or only tilting slightly)
                if (runtimeData.CyclicInput.magnitude < 0.1f)
                {
                    transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.fixedDeltaTime * StabilizationFactor);
                }
            }
        }

        public virtual void RotateRotors()
        {
            // Your Meta_VehicleExhaust.cs already detects MainRotor and TailRotor transforms!
            // We can access them via the base class 'Exhaust' property.

            float deltaTime = Time.deltaTime;

            // Main Rotor Rotation
            if (Exhaust.MainRotor != null)
            {
                // Rotate around the local UP axis
                float rotationAmount = runtimeData.MainRotorSpeed * deltaTime;
                Exhaust.MainRotor.Rotate(Vector3.up, rotationAmount, Space.Self);
            }

            // Tail Rotor Rotation (Visual only, based on rudder/yaw input)
            if (Exhaust.TailRotor != null)
            {
                // Tail rotor spins relative to rudder input for visual effect
                float tailRotorSpeed = runtimeData.MainRotorSpeed * 0.2f; // Base speed
                float tailRotorInputRotation = runtimeData.RudderInput * 200f; // Add rotation based on input

                // Rotate around the local FORWARD/BACKWARD axis (depending on model)
                Exhaust.TailRotor.Rotate(Vector3.forward, (tailRotorSpeed + tailRotorInputRotation) * deltaTime, Space.Self);
            }
        }

        // You can keep all your light and signal methods (ToggleSignalLights, OnHeadLightChanged, etc.) 
        // in the base Meta_VehicleBase or copy them from Meta_VehicleSystem.cs if they are not in the base class.
        // Assuming your existing ToggleSignalLights() from Meta_VehicleSystem is now in Meta_VehicleBase or is accessible:
        // public virtual void ToggleSignalLights() { ... }
    }
}