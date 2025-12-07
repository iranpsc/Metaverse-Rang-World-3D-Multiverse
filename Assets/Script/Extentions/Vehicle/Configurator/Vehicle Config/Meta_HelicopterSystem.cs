// File: Meta_HelicopterSystem.cs
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Meta.Vehicle
{
    [AddComponentMenu("Meta/Meta_HelicopterSystem")]
    [HelpURL("https://google.com")]
    public class Meta_HelicopterSystem : Meta_VehicleBase
    {
        [Header("Helicopter Input")]
        public InputActionReference CollectiveInput; // Vertical/Lift
        public InputActionReference CyclicInput;     // Pitch/Roll
        public InputActionReference PedalInput;      // Yaw/Rudder

        [Header("Flight Settings")]
        public float MaxLiftForce = 2000f;
        public float MaxRotorSpeed = 500f;

        private Rigidbody rb;

        public void Start()
        {
            rb = GetComponent<Rigidbody>();
            // ... (GetData() and other setup similar to Meta_CarSystem) ...
        }

        private void OnEnable()
        {
            // Enable actions
        }
        private void OnDisable()
        {
            // Disable actions
        }

        private void FixedUpdate()
        {
            if (!HasDriver) return; // Only control if a driver is present

            Vector2 cyclic = CyclicInput.action.ReadValue<Vector2>();
            float collective = CollectiveInput.action.ReadValue<float>();
            float pedal = PedalInput.action.ReadValue<float>();

            // Apply Lift (Collective)
            Vector3 liftForce = Vector3.up * (collective * MaxLiftForce);
            rb.AddForce(liftForce);

            // Apply Pitch/Roll (Cyclic) - Simplified
            rb.AddRelativeTorque(Vector3.right * cyclic.y * MaxRotorSpeed); // Pitch
            rb.AddRelativeTorque(-Vector3.forward * cyclic.x * MaxRotorSpeed); // Roll

            // Apply Yaw (Pedal) - Simplified
            rb.AddRelativeTorque(Vector3.up * pedal * MaxRotorSpeed * 0.5f); // Yaw
        }
    }
}