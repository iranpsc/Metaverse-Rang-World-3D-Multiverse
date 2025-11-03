using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Meta
{
    public enum VehicleType
    {
        Car,
        Bus,
        Motorcycle,
        Helicopter,
        Jetpack,
        Boat,
        Ship,
    }

    [AddComponentMenu("Meta/Meta_VehicleConfigurator")]
    [HelpURL("https://google.com")]
    public class Meta_VehicleConfigurator : MonoBehaviour
    {

        [Header("References")]
        public VehicleType Type;

        public VehicleBody Part;
        public VehicleWheel Wheels;
        public VehicleLight Lights;

        [Header("Settings")]
        public Rigidbody Rb;

        [Header("Inputs")]


        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        void Start()
        {
            if (EnableLog) Debug.Log("[Meta_VehicleConfigurator] PutLogHere");
            AutoConfigure();
        }

        public void AutoConfigure()
        {
            // NOTE : first setup
            Part.GetPart(gameObject);

            // NOTE: add rigidbody
            if (Rb == null)
            {
                Rb = gameObject.AddComponent<Rigidbody>();
                Rb.mass = 100.0f;
                Rb.linearDamping = 1.0f;
                Rb.angularDamping = 1.0f;
                Rb.useGravity = true;
            }
            else
            {
                Rb = GetComponent<Rigidbody>();
            }
            // NOTE: add wheelcollider
            Wheels.SetupWheels(gameObject);
        }
    }

    [System.Serializable]
    public class LoadConfig
    {
        // NOTE: load vehicle config from .json file
    }

    [System.Serializable]
    public class VehicleBody
    {
        public Transform[] VehiclePart;

        public virtual void GetPart(GameObject _Vehicle)
        {
            VehiclePart = _Vehicle.GetComponentsInChildren<Transform>(true);
        }

    }

    [System.Serializable]
    public class VehicleWheel : VehicleBody
    {
        public List<GameObject> SteeringWheels = new List<GameObject>();
        public List<GameObject> RearWheels = new List<GameObject>();
        public List<GameObject> Wheels = new List<GameObject>();

        public bool NewGLTF = true;

        public virtual void SetupWheels(GameObject _Vehicle)
        {
            AddCollider(_Vehicle);
        }
        public virtual void FindWheels(GameObject _Vehicle)
        {
            if (_Vehicle == null) return;

            foreach (Transform _Transform in VehiclePart)
            {
                string _Name = _Transform.name.ToLower();

                if (!_Name.Contains("wheel") || _Transform.GetComponent<MeshRenderer>() == null) continue; // Dont Use Wheel Holder

                if (NewGLTF)
                {
                    if (_Name.Contains("front") && _Transform.childCount > 0) SteeringWheels.Add(_Transform.gameObject);
                    if (_Name.Contains("rear") && _Transform.childCount > 0) RearWheels.Add(_Transform.gameObject);
                }
                else
                {
                    if (_Name.Contains("front")) SteeringWheels.Add(_Transform.gameObject);
                    if (_Name.Contains("rear")) RearWheels.Add(_Transform.gameObject);
                }
            }
            Wheels.AddRange(SteeringWheels);
            Wheels.AddRange(RearWheels);
        }

        public virtual void AddCollider(GameObject _Vehicle)
        {
            FindWheels(_Vehicle);
            if (_Vehicle == null) return;

            foreach (GameObject _Wheels in Wheels)
            {
                if (_Wheels == null) continue;

                float _SuspensionDistance = 0.2f;

                GameObject _ColliderObj = new GameObject(_Wheels.name + " Collider");
                _ColliderObj.transform.SetParent(_Wheels.transform);
                _ColliderObj.transform.localPosition = new Vector3(0f, _SuspensionDistance * 0.5f, 0f);
                _ColliderObj.transform.localRotation = Quaternion.identity;
                _ColliderObj.transform.SetParent(_Wheels.transform.parent, true);

                WheelCollider _WheelCollider = _ColliderObj.AddComponent<WheelCollider>();
                _WheelCollider.radius = CalculateWheelRadius(_Wheels);
                _WheelCollider.center = new Vector3(0f, _WheelCollider.radius, 0f);
                _WheelCollider.suspensionDistance = _SuspensionDistance;
                _WheelCollider.mass = 30f;

                JointSpring _Spring = new JointSpring
                {
                    spring = 15000f,
                    damper = 2500,
                    targetPosition = 0.5f
                };
                _WheelCollider.suspensionSpring = _Spring;

                WheelFrictionCurve _ForwardFriction = _WheelCollider.forwardFriction;
                _ForwardFriction.stiffness = 1.5f;
                _WheelCollider.forwardFriction = _ForwardFriction;

                WheelFrictionCurve _SideFriction = _WheelCollider.sidewaysFriction;
                _SideFriction.stiffness = 2.0f;
                _WheelCollider.sidewaysFriction = _SideFriction;
            }
        }
        private float CalculateWheelRadius(GameObject _Wheel)
        {
            MeshFilter _MeshFilter = _Wheel.GetComponent<MeshFilter>();
            if (_MeshFilter == null) return 0.35f;

            Bounds _Bounds = _MeshFilter.sharedMesh.bounds;

            float _Radius = Mathf.Max(_Bounds.extents.x, _Bounds.extents.z);
            _Radius *= Mathf.Max(_Wheel.transform.localScale.x, _Wheel.transform.localScale.z);

            return _Radius;
        }
    }

    [System.Serializable]
    public class VehicleLight
    {
        public List<GameObject> HeadLights = new List<GameObject>();
        public List<GameObject> BrakLights = new List<GameObject>();
        public List<GameObject> ReverseLights = new List<GameObject>();
        public List<GameObject> TurnLeftSignals = new List<GameObject>();
        public List<GameObject> TurnRightSignals = new List<GameObject>();
        //public List<GameObject> FogLights = new List<GameObject>();

        public virtual void FindLight(GameObject _Vehicle)
        {
            if (_Vehicle == null) return;



        }

    }

    [System.Serializable]
    public class VehicleSeat
    {
        public List<GameObject> DriverSeat = new List<GameObject>();
        public List<GameObject> PassengerSeat = new List<GameObject>();

    }

    [System.Serializable]
    public class VehicleExhaust
    {
        public List<GameObject> Exhausts = new List<GameObject>();
        public ParticleSystem SmokeEffect;

        public virtual void FindExhaust(GameObject _Vehicle)
        {

        }

    }

    [System.Serializable]
    public class VehicleLock
    {
        public bool HasLock; // can lock the vehicle
        public bool IsLock; // is it locked
        public List<GameObject> HasKey = new List<GameObject>(); // player that have access to vehicle
    }

    [System.Serializable]
    public class VehicleFuel
    {
        public float MaxFuel;
        public float FuelConsume;
        public float CurrentFuel;
        public bool HasFuel;
    }

    [System.Serializable]
    public class VehicleEngine
    {
        public float EnginePower;
        public float EngineAcceleration;
        public float CurrentSpeed;
        public float RotateSpeed;
        public float BrakForce;
    }
}