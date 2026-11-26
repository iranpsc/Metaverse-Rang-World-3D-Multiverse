using Mirror;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Meta.Vehicle
{
    // --- Data Structures ---

    [Serializable]
    public struct JetpackRuntimeData
    {
        [ReadOnly, SerializeField] public Vector2 MoveInput;
        [ReadOnly, SerializeField] public float YawInput;       // 1D Axis: -1 (left) to 1 (right)
        [ReadOnly, SerializeField] public bool IsAscending;
        [ReadOnly, SerializeField] public bool IsDescending;
        [ReadOnly, SerializeField] public bool IsThrusting;     // True if any movement input is active
    }

    [Serializable]
    public struct JetpackKeys
    {
        public InputActionReference Move;
        public InputActionReference Up;
        public InputActionReference Down;
        // CHANGED: Combined left/right yaw into a single 1D Axis
        public InputActionReference YawAxis;
    }

    // --- Controller Class ---

    [AddComponentMenu("Meta/Jetpack Controller")]
    public class Meta_JetpackSystem : Meta_VehicleBase // Inherits from your base class
    {
        [Header("System References")]
        public Rigidbody Rb;
        public Camera playerCamera;

        [Header("Jetpack Configuration")]
        public JetpackKeys keys;
        public JetpackRuntimeData runtimeData;

        [Header("Movement Settings")]
        public float moveForce = 15f;
        public float ascendForce = 10f;
        public float descendForce = 5f;
        public float gravityForce = 2f;
        public float rotateSpeed = 60f;
        public float cameraFollowSpeed = 5f;

        [Header("Camera Dead Zone")]
        [Tooltip("The maximum angle the camera can rotate horizontally before the vehicle starts turning.")]
        public float CameraDeadZoneAngle = 20f;
        // --- Setup and Authority ---
        [Header("Particle System")]
        [Tooltip("Prefab containing the ParticleSystem component to spawn.")]
        public GameObject ThrustParticlePrefab; // <--- NEW: Drag your ParticleSystem Prefab here

        // <--- NEW: Private list to hold instantiated references for control
        private List<ParticleSystem> _instantiatedParticles = new List<ParticleSystem>();
        protected override void OnValidate()
        {
            if (Application.isPlaying) return;
            base.OnValidate();
            if (Rb == null) Rb = GetComponent<Rigidbody>();
        }

        public override void OnStartAuthority()
        {
            base.OnStartAuthority();
            this.enabled = true;

            // Enable inputs
            keys.Move.action.Enable();
            keys.Up.action.Enable();
            keys.Down.action.Enable();
            keys.YawAxis.action.Enable();

            if (Rb == null) Rb = GetComponent<Rigidbody>();
            Rb.useGravity = false;
            Rb.freezeRotation = true;
            Rb.isKinematic = false;

            if (playerCamera == null) playerCamera = Camera.main;
            InitializeThrusterParticles();
        }

        public override void OnStopAuthority()
        {
            base.OnStopAuthority();

            if (Rb != null)
            {
                Rb.isKinematic = false;
                Rb.linearVelocity = Vector3.zero;
                Rb.angularVelocity = Vector3.zero;
                Rb.useGravity = true;

            }
            this.enabled = false;

            // Disable inputs
            keys.Move.action.Disable();
            keys.Up.action.Disable();
            keys.Down.action.Disable();
            keys.YawAxis.action.Disable();

            // Visuals: Turn off smoke effects when driver exits
            // ----------------------------------------------------
            //if (Exhaust != null) Exhaust.ToggleSmokeEffect(false);
            // ----------------------------------------------------
        }
        private void InitializeThrusterParticles()
        {
            if (Exhaust == null || Exhaust.AllExhausts == null || ThrustParticlePrefab == null)
            {
                Debug.LogWarning($"Jetpack: Cannot initialize particles. Check if Exuast, Exuast.AllExhaust, or ThrustParticlePrefab is null.");
                return;
            }

            _instantiatedParticles.Clear();

            // Loop through all defined exhaust transforms (thruster locations)
            foreach (Transform thruster in Exhaust.AllExhausts)
            {
                // 1. Instantiate the prefab
                GameObject particleObj = Instantiate(ThrustParticlePrefab, thruster.position, thruster.rotation, thruster);

                // 2. Get the ParticleSystem component
                ParticleSystem ps = particleObj.GetComponent<ParticleSystem>();

                if (ps != null)
                {
                    // Stop any ongoing emission immediately
                    ps.Stop();
                    _instantiatedParticles.Add(ps);
                }
                else
                {
                    Debug.LogError($"ThrustParticlePrefab is missing a ParticleSystem component!");
                    Destroy(particleObj);
                }
            }
        }
        // --- Core Loops ---

        private void Update()
        {
            if (isOwned && HasDriver)
            {
                HandleDriverInput();
                HandleRotation();

                HandleThrusterParticles();
                // Visuals: Update smoke effects based on movement state
                // ----------------------------------------------------
                //if (Exhaust != null)
                //{
                //    Exhaust.ToggleSmokeEffect(runtimeData.IsThrusting);
                //    // Add logic here to manipulate Exuast.SmokeEffect properties (e.g., velocity, color)
                //    // based on runtimeData.IsAscending, runtimeData.MoveInput, etc.
                //}
                // ----------------------------------------------------
            }
        }
        protected virtual void HandleThrusterParticles()
        {
            // Check if any thrusting input is active
            bool isThrusting = runtimeData.IsThrusting;

            // Calculate base velocity based on ascent/descent
            float thrustVelocityY = 0f;
            if (runtimeData.IsAscending)
            {
                thrustVelocityY = -7f; // More smoke/force downward when ascending
            }
            else if (runtimeData.IsDescending)
            {
                thrustVelocityY = -1f; // Lighter smoke when descending
            }
            else if (isThrusting)
            {
                thrustVelocityY = -3f; // Default smoke for horizontal/yaw movement
            }

            // Calculate horizontal thrust direction (opposite of movement)
            Vector3 inputDir = new Vector3(runtimeData.MoveInput.x, 0, runtimeData.MoveInput.y);
            Vector3 localThrustDir = transform.InverseTransformDirection(inputDir);

            // Iterate through all instantiated particle systems
            foreach (var ps in _instantiatedParticles)
            {
                if (isThrusting)
                {
                    if (!ps.isPlaying) ps.Play();

                    // Dynamically update the velocity over lifetime module
                    var vel = ps.velocityOverLifetime;
                    vel.enabled = true;
                    vel.space = ParticleSystemSimulationSpace.Local;

                    // Set the primary vertical thrust force
                    vel.y = thrustVelocityY;

                    // Set the horizontal push force (opposite of travel direction)
                    vel.x = -localThrustDir.x * 2f;
                    vel.z = -localThrustDir.z * 2f;
                }
                else
                {
                    if (ps.isPlaying) ps.Stop();
                }
            }
        }
        private void FixedUpdate()
        {
            if (Rb != null && Rb.isKinematic == false && isOwned && HasDriver)
            {
                ApplyThrustForces();
            }
        }

        // --- Input & Rotation ---

        public override void HandleDriverInput()
        {
            // Read all inputs into runtimeData
            runtimeData.MoveInput = keys.Move.action.ReadValue<Vector2>();
            runtimeData.IsAscending = keys.Up.action.IsPressed();
            runtimeData.IsDescending = keys.Down.action.IsPressed();

            // Read Yaw directly from the 1D Axis
            runtimeData.YawInput = keys.YawAxis.action.ReadValue<float>();

            // Check if any input is active for physics and particle effect logic
            runtimeData.IsThrusting = runtimeData.MoveInput.sqrMagnitude > 0.01f ||
                                      runtimeData.IsAscending ||
                                      runtimeData.IsDescending ||
                                      Mathf.Abs(runtimeData.YawInput) > 0.01f;
        }

        protected virtual void HandleRotation()
        {
            if (!playerCamera) return;

            // 1. Calculate Yaw Delta (Horizontal difference between Camera and Vehicle)

            // Get forward vectors, ignoring pitch (Y-axis)
            Vector3 camForward = playerCamera.transform.forward;
            camForward.y = 0;
            camForward.Normalize();

            Vector3 vehicleForward = transform.forward;
            vehicleForward.y = 0;
            vehicleForward.Normalize();

            // Calculate the angle difference between the two horizontal directions
            float yawDelta = Vector3.SignedAngle(vehicleForward, camForward, Vector3.up);

            // 2. Apply Dead Zone and Rotation
            float rotationInput = 0f;

            if (Mathf.Abs(yawDelta) > CameraDeadZoneAngle)
            {
                // If the camera is outside the dead zone, calculate rotation input

                // Determine the direction to turn: -1 (left) or 1 (right)
                float sign = Mathf.Sign(yawDelta);

                // Calculate the angle *outside* the dead zone
                float angleOutsideDeadZone = Mathf.Abs(yawDelta) - CameraDeadZoneAngle;

                // Use a smooth step (or clamp) to control the rotation strength
                // The rotation input ramps up the further outside the dead zone the camera is.
                // We use the rotation speed factor to control how fast it turns.
                rotationInput = sign * angleOutsideDeadZone / (90f - CameraDeadZoneAngle); // Normalize input from 0 to 1

                // Clamp the input to prevent hyper-rotation if the camera is flipped 180 degrees
                rotationInput = Mathf.Clamp(rotationInput, -1f, 1f);
            }

            // Combine Camera-Driven Rotation and Manual Yaw (Q/E)
            float combinedYawInput = rotationInput * 0.5f + runtimeData.YawInput * 0.5f; // Blend the two inputs

            // Apply rotation to the vehicle (both camera-driven and manual)
            transform.Rotate(0f, combinedYawInput * rotateSpeed * Time.deltaTime, 0f, Space.World);


            // 3. Lock Pitch and Roll to ensure the jetpack stays perfectly upright
            Vector3 currentRotation = transform.eulerAngles;
            transform.eulerAngles = new Vector3(0f, currentRotation.y, 0f);
        }

        // --- Physics Application ---

        protected virtual void ApplyThrustForces()
        {
            Vector3 inputDir = new Vector3(runtimeData.MoveInput.x, 0, runtimeData.MoveInput.y).normalized;
            Vector3 moveDir = transform.TransformDirection(inputDir);

            // Horizontal movement (WASD)
            Rb.AddForce(moveDir * moveForce, ForceMode.Acceleration);

            // Vertical forces (Lift and Descent)
            if (runtimeData.IsAscending)
            {
                Rb.AddForce(Vector3.up * ascendForce, ForceMode.Acceleration);
            }
            // Descend is applied *in addition* to gravity force
            if (runtimeData.IsDescending)
            {
                Rb.AddForce(Vector3.down * descendForce, ForceMode.Acceleration);
            }

            // Apply custom gravity/stabilization force when not actively flying up
            if (!runtimeData.IsAscending)
            {
                Rb.AddForce(Vector3.down * gravityForce, ForceMode.Acceleration);
            }
        }
    }
}