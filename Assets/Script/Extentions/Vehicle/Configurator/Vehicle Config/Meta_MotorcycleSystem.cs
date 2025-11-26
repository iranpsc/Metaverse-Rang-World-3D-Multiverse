using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Meta.Vehicle
{
    public class Meta_MotorcycleSystem : Meta_VehicleBase
    {
        [Header("Input")]
        public InputActionReference Move;
        public InputActionReference Brake;
        public InputActionReference SignalLeft;
        public InputActionReference SignalRight;
        [Header("Drive Settings")]
        public float MotorTorque = 1500f;
        public float BrakeTorque = 3000f;
        public float MaxSteerAngle = 30f;
        public float steerInput;
        public float motorInput;
        public bool isBraking;
        public Vector3 baseCenterOfMass = new Vector3(0f, -0.3f, 0f);
        public float comLoweringFactor = 0.2f; // How much lower CoM goes at high speed
        public float maxSpeedForCoM = 100f; // km/h or m/s depending on your units
        public Rigidbody rb;
        public Meta_VehiclePart Body;
        //public Meta_VehicleSeat Seat;
        public Meta_VehicleWheel Wheel;
        public Meta_VehicleLight Light;
        public Meta_VehicleExhaust Exhaust;
        private void OnEnable()
        {
            Move.action.Enable();
            Brake.action.Enable();
            SignalLeft.action.Enable();
            SignalRight.action.Enable();
        }
        private void OnDisable()
        {
            Move.action.Disable();
            Brake.action.Disable();
            SignalLeft.action.Disable();
            SignalRight.action.Disable();
        }
        [Command]
        public void GivePer(NetworkIdentity vehicleNetId)
        {
            vehicleNetId.AssignClientAuthority(connectionToClient);
        }
        [Command]
        public void RemPer(NetworkIdentity vehicleNetId)
        {
            vehicleNetId.RemoveClientAuthority();
        }
        public void Start()
        {
            GivePer(gameObject.GetComponentInChildren<NetworkIdentity>());
            rb = GetComponent<Rigidbody>();
            GetData();
            AdjustCenterOfMassBySpeed();
        }
        private void GetData()
        {
            Body = new Meta_VehiclePart(gameObject);
            Seat.GetSeats(Body.Parts);
            Wheel.GetWheels(Body.Parts);
            Wheel.SetWheels();
            Light.GetLight(Body.Parts);
            Light.SetLight();
            Exhaust.GetExhausts(Body.Parts);
            Exhaust.SetExhaust();
        }
        private void Update()
        {
            //if (!isOwned) return;

            if (HasDriver)
            {
                steerInput = Move.action.ReadValue<Vector2>().x;
                motorInput = Move.action.ReadValue<Vector2>().y;
                isBraking = Brake.action.IsPressed();
            }
            else
            {
                RemPer(gameObject.GetComponent<NetworkIdentity>());

                motorInput = 0;
                isBraking = true;
            }
        }
        private void FixedUpdate()
        {
            //if (!isOwned) return;

            ApplyMotorTorque();
            ApplySteering();
            UpdateWheelMeshes();
        }
        private void ApplyMotorTorque()
        {
            float brake = isBraking ? BrakeTorque : 0f;
            // Front Wheel Drive by default
            for (int i = 0; i < Wheel.FrontCollider.Count; i++)
            {
                var wc = Wheel.FrontCollider[i];
                wc.motorTorque = motorInput * MotorTorque;
                wc.brakeTorque = brake;
            }
            for (int i = 0; i < Wheel.RearCollider.Count; i++)
            {
                var wc = Wheel.RearCollider[i];
                wc.motorTorque = 0f;
                wc.brakeTorque = brake;
            }
        }
        private void ApplySteering()
        {
            float steerAngle = steerInput * MaxSteerAngle;
            for (int i = 0; i < Wheel.FrontCollider.Count; i++)
            {
                var wc = Wheel.FrontCollider[i];
                wc.steerAngle = steerAngle;
            }
        }
        private void UpdateWheelMeshes()
        {
            int count = Mathf.Min(Wheel.AllWheels.Count, Wheel.AllCollider.Count);
            for (int i = 0; i < count; i++)
            {
                GameObject visual = Wheel.AllWheels[i].gameObject;
                WheelCollider wc = Wheel.AllCollider[i];
                if (!visual || !wc) continue;
                wc.GetWorldPose(out Vector3 pos, out Quaternion rot);
                visual.transform.position = pos;
                visual.transform.rotation = rot;
            }
        }
        public void AdjustCenterOfMassBySpeed()
        {
            if (rb == null) return;
            float speed = rb.linearVelocity.magnitude; // in m/s
            float t = Mathf.Clamp01(speed / maxSpeedForCoM); // 0 to 1
                                                             // Interpolate Y offset from base value to lowered value
            float loweredY = baseCenterOfMass.y - comLoweringFactor;
            float currentY = Mathf.Lerp(baseCenterOfMass.y, loweredY, t);
            rb.centerOfMass = new Vector3(baseCenterOfMass.x, currentY, baseCenterOfMass.z);
        }
    }
        
}
