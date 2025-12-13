using Mirror;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using System.Linq;

namespace Meta.Vehicle
{
    // UPDATED: Added fields for lighting state
    [Serializable]
    public struct MotorcycleRuntimeData
    {
        [ReadOnly, SerializeField] public float MotorInput;
        [ReadOnly, SerializeField] public float SteerInput;
        [ReadOnly, SerializeField] public bool IsMoving;
        [ReadOnly, SerializeField] public bool IsBraking;
        [ReadOnly, SerializeField] public bool IsReversing;
        [ReadOnly, SerializeField] public float SignalTimer; // For blinking logic
    }

    // UPDATED: Added light inputs
    [Serializable]
    public struct MotorcycleKeys
    {
        public InputActionReference MoveAndSteer;
        public InputActionReference Brake;
        public InputActionReference SignalLeft;        // NEW
        public InputActionReference SignalRight;       // NEW
        public InputActionReference HeadLightToggle;   // NEW
    }

    [AddComponentMenu("Meta/Motorcycle System")]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/DreamFaver/MotorcycleSystem")]
    public class Meta_MotorcycleSystem : Meta_VehicleBase
    {
        // --- NETWORKED LIGHT STATE (SyncVars & Hooks from Meta_VehicleSystem) ---
        [SyncVar(hook = nameof(OnHeadLightChanged))] private bool HeadLightOn = false;
        [SyncVar(hook = nameof(OnLeftSignalChanged))] private bool LeftSignalOn = false;
        [SyncVar(hook = nameof(OnRightSignalChanged))] private bool RightSignalOn = false;
        // ------------------------------------------------------------------------

        public Rigidbody Rb;
        public MotorcycleKeys keys;
        public MotorcycleRuntimeData runtimeData;

        // ----------------------------------------------------------------------------

        [Header("Movement Settings")]
        public float MotorTorque = 1500f;
        public float BrakeTorque = 3000f;
        public float MaxSteerAngle = 30f;

        [Header("Balance and Lean Settings")]
        public float SelfRightingTorque = 5000f;
        public float MaxLeanAngle = 40f;
        public float LeanSpeed = 5f;
        public float LeanSteerTorque = 2000f;

        [Header("Animation References")]
        public Transform Handlebar;

        private float _CurrentLeanAngle = 0f;

        // --- Setup and Authority ---

        // ... (OnValidate, OnStartAuthority, OnStopAuthority, OnDriverEnter/Exit are unchanged) ...

        protected override void OnValidate()
        {
            if (Application.isPlaying) return;

            base.OnValidate();
            Reset();

        }
        protected virtual void Reset()
        {
            Rb.isKinematic = true;
            //this.enabled = false;
        }
        public override void OnStartAuthority()
        {
            base.OnStartAuthority();
            this.enabled = true;

            if (keys.MoveAndSteer != null) keys.MoveAndSteer.action.Enable();
            if (keys.Brake != null) keys.Brake.action.Enable();
            if (keys.SignalLeft != null) keys.SignalLeft.action.Enable();     // NEW
            if (keys.SignalRight != null) keys.SignalRight.action.Enable();    // NEW
            if (keys.HeadLightToggle != null) keys.HeadLightToggle.action.Enable(); // NEW

            Rb.useGravity = true;
            Rb.centerOfMass = new Vector3(0, -0.5f, 0);
            Rb.isKinematic = false;
        }

        public override void OnStopAuthority()
        {
            base.OnStopAuthority();

            Rb.isKinematic = true;
            this.enabled = false;

            if (keys.MoveAndSteer != null) keys.MoveAndSteer.action.Disable();
            if (keys.Brake != null) keys.Brake.action.Disable();
            if (keys.SignalLeft != null) keys.SignalLeft.action.Disable();      // NEW
            if (keys.SignalRight != null) keys.SignalRight.action.Disable();     // NEW
            if (keys.HeadLightToggle != null) keys.HeadLightToggle.action.Disable(); // NEW
        }

        // --- Update Loop (Input, Animation, State Lights) ---

        private void Update()
        {
            if (isOwned && HasDriver)
            {
                // Read control inputs
                HandleDriverInput();

                // Read light inputs (Headlight/Signals)
                HandleLightInput();
                HandleSignalInput();

                // Animations
                AnimateHandlebar();
            }

            // Visual Lean (local smoothing)
            SmoothLean();

            // Lights based on vehicle state (Brake/Reverse)
            UpdateStateLights();
        }

        // --- Physics Loop (Movement, Balance, Signal Blinking) ---

        private void FixedUpdate()
        {
            if (Rb != null && Rb.isKinematic == false)
            {
                // Physics application (Torque, Steering, Balance)
                if (isOwned && HasDriver)
                {
                    ApplyTorque();
                    ApplyBraking();
                    ApplySteering();
                }

                ApplyWheelPhysics();
                ApplySelfRightingForce();

                // Signal Blinking Logic
                SignalBlinking();
            }
        }

        // --- LIGHTING LOGIC (Ported from Meta_VehicleSystem.cs) ---

        protected virtual void HandleLightInput()
        {
            // Headlight Toggle
            if (keys.HeadLightToggle != null && keys.HeadLightToggle.action.triggered)
            {
                RpcToggleHeadLight();
            }
        }

        protected virtual void HandleSignalInput()
        {
            bool leftTriggered = keys.SignalLeft != null && keys.SignalLeft.action.triggered;
            bool rightTriggered = keys.SignalRight != null && keys.SignalRight.action.triggered;

            if (leftTriggered)
            {
                if (LeftSignalOn)
                {
                    // If left is on, turn off all
                    RpcDisableSignals();
                }
                else
                {
                    // Turn on left signal (will automatically turn off right via hook)
                    RpcToggleLeftSignal();
                }
            }

            if (rightTriggered)
            {
                if (RightSignalOn)
                {
                    // If right is on, turn off all
                    RpcDisableSignals();
                }
                else
                {
                    // Turn on right signal (will automatically turn off left via hook)
                    RpcToggleRightSignal();
                }
            }
        }

        protected virtual void UpdateStateLights()
        {
            if (Light == null) return;

            // NEW: Check if the motorcycle is actually moving backward using Vector3.Dot
            bool isMovingBackward = Vector3.Dot(Rb.linearVelocity, transform.forward) < -0.1f;

            // Check for Reverse Light
            runtimeData.IsReversing = isMovingBackward && runtimeData.MotorInput < 0;
            Light.ToggleLights(Light.ReverseLight, runtimeData.IsReversing);

            // Check for Brake Lights
            // Braking light is on if: (Input is Brake) OR (we are moving backward)
            runtimeData.IsBraking = keys.Brake.action.ReadValue<float>() > 0.1f || isMovingBackward;
            Light.ToggleLights(Light.BrakeLights, runtimeData.IsBraking);
        }

        protected virtual void SignalBlinking()
        {
            if (Light == null) return;

            // Only blink if a signal is currently active
            if (!LeftSignalOn && !RightSignalOn)
            {
                runtimeData.SignalTimer = 0f;
                return;
            }

            runtimeData.SignalTimer += Time.deltaTime;

            // Blink rate: Toggle state every 0.66 seconds (1.5f frequency)
            if (runtimeData.SignalTimer >= 1f / 1.5f)
            {
                runtimeData.SignalTimer = 0f;

                // 1. Determine the current light state (on or off) from the first active light
                bool _CurrentLightState = false;

                // Check Left Signal state
                if (LeftSignalOn && Light.TurnLeftSignal.Count > 0 && Light.TurnLeftSignal[0].LightComponent != null)
                {
                    _CurrentLightState = Light.TurnLeftSignal[0].LightComponent.enabled;
                }
                // Check Right Signal state (only if left is not being checked)
                else if (RightSignalOn && Light.TurnRightSignal.Count > 0 && Light.TurnRightSignal[0].LightComponent != null)
                {
                    _CurrentLightState = Light.TurnRightSignal[0].LightComponent.enabled;
                }

                // 2. Calculate the next blinking state (invert the current state)
                bool _ToggleState = !_CurrentLightState;

                // 3. Apply the state to the active lights
                if (LeftSignalOn)
                {
                    Light.ToggleLights(Light.TurnLeftSignal, _ToggleState);
                }

                if (RightSignalOn)
                {
                    Light.ToggleLights(Light.TurnRightSignal, _ToggleState);
                }
            }
        }

        // --- NETWORK COMMANDS (Client -> Server) ---

        [Command]
        private void RpcToggleHeadLight()
        {
            HeadLightOn = !HeadLightOn;
        }

        [Command]
        private void RpcToggleLeftSignal()
        {
            LeftSignalOn = !LeftSignalOn;
            if (LeftSignalOn) RightSignalOn = false;
        }

        [Command]
        private void RpcToggleRightSignal()
        {
            RightSignalOn = !RightSignalOn;
            if (RightSignalOn) LeftSignalOn = false;
        }

        [Command]
        private void RpcDisableSignals()
        {
            LeftSignalOn = false;
            RightSignalOn = false;
        }

        // --- NETWORK HOOKS (Server -> All Clients) ---

        private void OnHeadLightChanged(bool _Old, bool _New)
        {
            // This runs on all clients when HeadLightOn changes
            if (Light != null)
            {
                Light.ToggleLights(Light.HeadLights, _New);
            }
        }

        private void OnLeftSignalChanged(bool _Old, bool _New)
        {
            // Ensure the light state is synced immediately on change
            if (Light != null)
            {
                Light.ToggleLights(Light.TurnLeftSignal, _New);
            }
            // When a signal turns OFF, make sure the timer resets
            if (!_New) runtimeData.SignalTimer = 0f;
        }

        private void OnRightSignalChanged(bool _Old, bool _New)
        {
            // Ensure the light state is synced immediately on change
            if (Light != null)
            {
                Light.ToggleLights(Light.TurnRightSignal, _New);
            }
            // When a signal turns OFF, make sure the timer resets
            if (!_New) runtimeData.SignalTimer = 0f;
        }

        // --- MOVEMENT LOGIC (Unchanged) ---

        public override void HandleDriverInput()
        {
            ReadInput();
            // Torque, Braking, Steering applied in FixedUpdate
        }

        protected virtual void ReadInput()
        {
            Vector2 input = keys.MoveAndSteer.action.ReadValue<Vector2>();

            runtimeData.MotorInput = input.y;
            runtimeData.SteerInput = input.x;

            runtimeData.IsMoving = Mathf.Abs(runtimeData.MotorInput) > 0.1f || Mathf.Abs(runtimeData.SteerInput) > 0.1f;
        }

        // ... (ApplyTorque, ApplyBraking, ApplySteering, ApplyWheelPhysics, ApplySelfRightingForce, SmoothLean, AnimateHandlebar are unchanged) ...

        protected virtual void ApplyTorque()
        {
            float motorTorque = runtimeData.MotorInput * MotorTorque;
            foreach (var wheel in Wheel.RearCollider)
            {
                wheel.motorTorque = motorTorque;
            }
        }

        protected virtual void ApplyBraking()
        {
            float brakeInput = keys.Brake.action.ReadValue<float>();
            float brakeTorque = brakeInput * BrakeTorque;

            foreach (var wheel in Wheel.AllCollider)
            {
                wheel.brakeTorque = brakeTorque;
            }
        }

        protected virtual void ApplySteering()
        {
            float steerAngle = runtimeData.SteerInput * MaxSteerAngle;

            // FIX 1: Apply steer angle to front wheel. 
            // The wheel collider must be correctly populated in Wheel.FrontCollider.
            if (Wheel.FrontCollider != null)
            {
                foreach (var wheel in Wheel.FrontCollider)
                {
                    wheel.steerAngle = steerAngle;
                }
            }

            // FIX 2: Invert the Lean Angle. Steering Left (negative SteerInput) 
            // should result in a negative lean angle (roll to the left).
            _CurrentLeanAngle = runtimeData.SteerInput * -MaxLeanAngle; // <-- CRITICAL FIX: Add a negative sign
        }

        protected virtual void ApplyWheelPhysics()
        {
            if (Wheel.AllCollider.Count > 0)
            {
                for (int i = 0; i < Wheel.AllCollider.Count; i++)
                {
                    WheelCollider collider = Wheel.AllCollider[i];
                    Transform visualMesh = Wheel.AllWheels[i];

                    Vector3 position;
                    Quaternion rotation;
                    collider.GetWorldPose(out position, out rotation);

                    visualMesh.position = position;
                    visualMesh.rotation = rotation;
                }
            }
        }

        // FIX: Reworked the self-righting and damping forces for stability.
        protected virtual void ApplySelfRightingForce()
        {
            float roll = transform.eulerAngles.z;
            if (roll > 180f) roll -= 360f;

            // The target lean angle is -_CurrentLeanAngle because the bike needs to roll
            // towards the outside of the turn, but the roll difference logic handles the sign.
            float desiredRoll = -_CurrentLeanAngle;
            float rollDifference = desiredRoll - roll;

            float speedFactor = Mathf.Clamp01(Rb.linearVelocity.magnitude / 10f);

            // 1. Self-Righting Torque (The force that stands the bike up)
            // Changed to ForceMode.Force for smooth, continuous application.
            // Reduced the multiplier slightly to reduce the aggressive push.
            Rb.AddTorque(transform.forward * rollDifference * SelfRightingTorque * 0.75f * speedFactor, ForceMode.Force); // <-- Changed ForceMode and reduced torque multiplier

            // Damping Torque: Keep the aggressive squared damping
            Vector3 angularVelocity = Rb.angularVelocity;
            float angularDampingMagnitude = angularVelocity.magnitude * angularVelocity.magnitude;
            // Try increasing the multiplier slightly if jitter returns (e.g., 60f)
            Rb.AddTorque(-angularVelocity.normalized * angularDampingMagnitude * 50f, ForceMode.Acceleration);
            // Note: Use ForceMode.Acceleration to ignore the bike's mass for this damping force.
        }

        protected virtual void SmoothLean()
        {
            if (Rb.isKinematic) return;

            Quaternion targetRotation = Quaternion.Euler(
                transform.localEulerAngles.x,
                transform.localEulerAngles.y,
                _CurrentLeanAngle
            );

            transform.localRotation = Quaternion.Slerp(
                transform.localRotation,
                targetRotation,
                Time.deltaTime * LeanSpeed
            );
        }

        protected virtual void AnimateHandlebar()
        {
            if (!Handlebar) return;


            float targetAngle = Wheel.FrontCollider.FirstOrDefault()?.steerAngle ?? 0f;

            Handlebar.localRotation = Quaternion.Slerp(
                Handlebar.localRotation,
                Quaternion.Euler(0, targetAngle, 0),
                Time.deltaTime * 10f
            );
        }
    }
}