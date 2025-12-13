using Mirror;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using System.Linq;

namespace Meta.Vehicle
{
    // ... (Structs BoatRuntimeData and BoatKeys remain the same)
    [Serializable]
    public struct BoatRuntimeData
    {
        [ReadOnly, SerializeField] public float MotorInput;
        [ReadOnly, SerializeField] public float SteerInput;
        [ReadOnly, SerializeField] public bool IsMoving;
        [ReadOnly, SerializeField] public bool IsInWater;
    }

    [Serializable]
    public struct BoatKeys
    {
        public InputActionReference MoveAndSteer;
        public InputActionReference Brake;
    }
    // ...

    [AddComponentMenu("Meta/Boat System")]
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/DreamFaver")]
    public class Meta_BoatSystem : Meta_VehicleBase
    {
        [Serializable]
        public struct BuoyancyPoint
        {
            public Transform Point;
        }

        public Rigidbody Rb;
        public BoatKeys boatKeys;
        public BoatRuntimeData runtimeData;

        [Header("Buoyancy Settings (Simplified)")]
        [Tooltip("The world Y-coordinate of the water surface.")]
        public float WaterLevel = 0f; // <--- NEW: Fixed Water Level
        public List<BuoyancyPoint> BuoyancyPoints = new List<BuoyancyPoint>();
        [Tooltip("Higher value = more buoyant. Increase this if the boat sinks.")]
        public float BuoyancyFactor = 150f;
        [Tooltip("Keep LOW (e.g., 0.5) for a noticeable 'bounce' effect.")]
        public float DampingFactor = 0.5f;

        [Header("Auto Buoyancy Generation")]
        public int PointsAlongX = 3;
        public int PointsAlongZ = 2;
        public float PointVerticalOffset = 0.05f;

        [Header("Movement Settings")]
        public float ThrustForce = 25000f;
        public float SteeringForce = 5000f;
        public float WaterDrag = 0.5f;

        [Header("Animation References")]
        public Transform Propeller;
        public Transform SteeringWheel;

        [Header("Animation Settings")]
        public float PropellerRotationSpeed = 1000f;
        public float MaxSteeringWheelAngle = 22f;

        // --- Setup, Authority, Kinematics, Animation (Unchanged/Working) ---

        protected override void OnValidate()
        {
            if (Rb == null) Rb = GetComponent<Rigidbody>();
        }

        public override void OnStartAuthority()
        {
            base.OnStartAuthority();
            if (boatKeys.MoveAndSteer != null) boatKeys.MoveAndSteer.action.Enable();
            if (boatKeys.Brake != null) boatKeys.Brake.action.Enable();
            Rb.useGravity = true;
            Rb.linearDamping = 0.1f;
            Rb.angularDamping = 0.5f;
            if (DriverNetId == 0 && Rb.isKinematic == false) Rb.isKinematic = true;
        }

        public override void OnStopAuthority()
        {
            base.OnStopAuthority();
            if (boatKeys.MoveAndSteer != null) boatKeys.MoveAndSteer.action.Disable();
            if (boatKeys.Brake != null) boatKeys.Brake.action.Disable();
        }


        private void Update()
        {
            if (isOwned && HasDriver)
            {
                AnimatePropeller();
                AnimateSteeringWheel();
            }
        }

        void AnimatePropeller()
        {
            if (!Propeller) return;
            float propellerSpeed = runtimeData.MotorInput * PropellerRotationSpeed;
            Propeller.Rotate(Vector3.right * propellerSpeed * Time.deltaTime, Space.Self);
        }

        void AnimateSteeringWheel()
        {
            if (!SteeringWheel) return;
            float targetAngle = runtimeData.SteerInput * MaxSteeringWheelAngle;
            Vector3 currentEuler = SteeringWheel.localEulerAngles;
            float newY = Mathf.LerpAngle(currentEuler.y, -targetAngle, 5f * Time.deltaTime);
            SteeringWheel.localEulerAngles = new Vector3(currentEuler.x, newY, currentEuler.z);
        }

        private void FixedUpdate()
        {
            if (Rb != null && Rb.isKinematic == false)
            {
                ApplyBuoyancy();
            }

            if (isOwned && HasDriver)
            {
                HandleDriverInput();
            }
        }

        public override void HandleDriverInput()
        {
            ReadInput();

            if (runtimeData.IsInWater)
            {
                ApplyThrust();
                ApplySteering();
                Rb.linearDamping = WaterDrag;
                Rb.angularDamping = WaterDrag * 2f;
            }
            else
            {
                Rb.linearDamping = 0.1f;
                Rb.angularDamping = 0.5f;
            }
        }

        protected virtual void ReadInput()
        {
            Vector2 input = boatKeys.MoveAndSteer.action.ReadValue<Vector2>();
            runtimeData.MotorInput = input.y;
            runtimeData.SteerInput = input.x;
            runtimeData.IsMoving = Mathf.Abs(runtimeData.MotorInput) > 0.1f;
        }

        public virtual void ApplyThrust()
        {
            if (Mathf.Abs(runtimeData.MotorInput) < 0.1f) return;
            Vector3 thrust = transform.forward * runtimeData.MotorInput * ThrustForce;
            Rb.AddForce(thrust, ForceMode.Acceleration);
        }

        public virtual void ApplySteering()
        {
            if (Mathf.Abs(runtimeData.SteerInput) < 0.1f) return;
            float velocityFactor = Mathf.Clamp01(Rb.linearVelocity.magnitude / 5f);
            float yawTorque = runtimeData.SteerInput * SteeringForce * velocityFactor;
            Rb.AddTorque(transform.up * yawTorque, ForceMode.Acceleration);
        }

        // --- BUOYANCY FIX: FIXED WATER LEVEL ---

        // --- BUOYANCY FIX: Using ForceMode.Force for stability ---

        public virtual void ApplyBuoyancy()
        {
            runtimeData.IsInWater = false;
            if (BuoyancyPoints.Count == 0 || Rb.mass <= 0) return;

            // Note: gravityCompensationPerPoint is now too large for ForceMode.Force
            // But we keep it to ensure the boat naturally counteracts its mass.
            float gravityCompensationPerPoint = (Rb.mass * Mathf.Abs(Physics.gravity.y)) / BuoyancyPoints.Count;

            int submergedPoints = 0;

            foreach (var bp in BuoyancyPoints)
            {
                if (bp.Point == null) continue;

                float waterHeight = WaterLevel;
                float depth = waterHeight - bp.Point.position.y;

                if (depth > 0) // Point is submerged
                {
                    submergedPoints++;

                    // 1. BUOYANCY FORCE (Lift)
                    // The buoyancy factor is multiplied by depth AND gravity compensation is added.
                    // This force is now being applied continuously (ForceMode.Force)
                    float buoyancy = gravityCompensationPerPoint + (depth * BuoyancyFactor);
                    Vector3 upwardForce = Vector3.up * buoyancy;

                    // 2. DAMPING FORCE (Bobbing)
                    float verticalVelocity = Rb.GetPointVelocity(bp.Point.position).y;
                    float damping = -verticalVelocity * DampingFactor;

                    Vector3 totalForce = upwardForce + Vector3.up * damping;

                    // **** CRITICAL CHANGE: ForceMode.Force instead of Impulse ****
                    Rb.AddForceAtPosition(totalForce, bp.Point.position, ForceMode.Force);
                }
            }

            runtimeData.IsInWater = submergedPoints > 0;
        }

        // --- Auto Generation Utility (Unchanged) ---

        [ContextMenu("Generate Buoyancy Points")]
        public void AutoGenerateBuoyancyPoints()
        {
            BuoyancyPoints.Clear();

            Collider mainCollider = GetComponent<Collider>();
            if (mainCollider == null)
            {
                Debug.LogError("Boat needs a Collider component to auto-generate buoyancy points!");
                return;
            }

            Bounds bounds = mainCollider.bounds;

            int xCount = Mathf.Max(1, PointsAlongX);
            int zCount = Mathf.Max(1, PointsAlongZ);

            float xStep = bounds.size.x / (xCount > 1 ? xCount - 1 : 1);
            float zStep = bounds.size.z / (zCount > 1 ? zCount - 1 : 1);

            Vector3 startLocalPosition = transform.InverseTransformPoint(bounds.min) + new Vector3(0, 0, 0);

            Transform pointsParent = transform.Find("BuoyancyPoints_Generated");
            if (pointsParent == null)
            {
                pointsParent = new GameObject("BuoyancyPoints_Generated").transform;
                pointsParent.SetParent(transform, false);
            }

            pointsParent.GetComponentsInChildren<Transform>().Where(t => t != pointsParent).ToList().ForEach(t => DestroyImmediate(t.gameObject));

            for (int i = 0; i < xCount; i++)
            {
                for (int j = 0; j < zCount; j++)
                {
                    Vector3 localPos = startLocalPosition;
                    localPos.x += xStep * i;
                    localPos.z += zStep * j;
                    localPos.y += PointVerticalOffset;

                    GameObject pointObj = new GameObject($"Point_{i}_{j}");
                    pointObj.transform.SetParent(pointsParent, false);
                    pointObj.transform.localPosition = localPos;

                    BuoyancyPoints.Add(new BuoyancyPoint { Point = pointObj.transform });
                }
            }
            Debug.Log($"Generated {BuoyancyPoints.Count} buoyancy points for the boat.");
        }
    }
}