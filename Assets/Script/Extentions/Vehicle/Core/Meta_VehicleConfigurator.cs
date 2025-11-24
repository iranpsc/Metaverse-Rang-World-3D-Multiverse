using System;
using System.Collections.Generic;
using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Vehicle Configurator")]
    [HelpURL("https://google.com")]
    public class Meta_VehicleConfigurator : MonoBehaviour
    {
        [Header("Vehicle Setup")]
        public Transform ExitPoint;
        public VehicleSeatGroup Seats = new VehicleSeatGroup();

        [Header("Vehicle Components")]
        public Rigidbody Rigidbody;
        public Animator Animator;

        [Header("Wheels")]
        public List<WheelCollider> WheelColliders = new List<WheelCollider>();
        public List<Transform> WheelMeshes = new List<Transform>();

        [Header("Lights")]
        public List<Light> HeadLights = new List<Light>();
        public List<Light> BrakeLights = new List<Light>();
        public List<Light> SignalLeftLights = new List<Light>();
        public List<Light> SignalRightLights = new List<Light>();

        [Header("Light Materials")]
        public List<Material> HeadLightMaterials = new List<Material>();
        public List<Material> BrakeLightMaterials = new List<Material>();
        public List<Material> SignalLeftMaterials = new List<Material>();
        public List<Material> SignalRightMaterials = new List<Material>();

        private void Awake()
        {
            if (Rigidbody == null)
                Rigidbody = GetComponent<Rigidbody>();

            if (Animator == null)
                Animator = GetComponentInChildren<Animator>();

            ScanVehicleParts();
        }

        private void ScanVehicleParts()
        {
            foreach (Transform _Child in GetComponentsInChildren<Transform>(true))
            {
                string _Name = _Child.name.ToLower();

                // ---- Seats ----
                if (_Name.Contains("seat"))
                {
                    Seats.AllSeats.Add(_Child);
                    if (_Name.Contains("driver"))
                        Seats.DriverSeats.Add(_Child);
                }

                // ---- Wheels ----
                if (_Child.TryGetComponent(out WheelCollider _Wheel))
                    WheelColliders.Add(_Wheel);

                if (_Name.Contains("wheel") && !_Child.TryGetComponent(out WheelCollider _))
                    WheelMeshes.Add(_Child);

                // ---- Lights ----
                if (_Child.TryGetComponent(out Light _Light))
                {
                    if (_Name.Contains("head"))
                        HeadLights.Add(_Light);
                    else if (_Name.Contains("brake"))
                        BrakeLights.Add(_Light);
                    else if (_Name.Contains("signal") && _Name.Contains("left"))
                        SignalLeftLights.Add(_Light);
                    else if (_Name.Contains("signal") && _Name.Contains("right"))
                        SignalRightLights.Add(_Light);
                }

                // ---- Light Materials ----
                if (_Child.TryGetComponent(out Renderer _Renderer))
                {
                    foreach (var _Mat in _Renderer.materials)
                    {
                        if (_Name.Contains("head"))
                            HeadLightMaterials.Add(_Mat);
                        else if (_Name.Contains("brake"))
                            BrakeLightMaterials.Add(_Mat);
                        else if (_Name.Contains("signal") && _Name.Contains("left"))
                            SignalLeftMaterials.Add(_Mat);
                        else if (_Name.Contains("signal") && _Name.Contains("right"))
                            SignalRightMaterials.Add(_Mat);
                    }
                }
            }
        }

        [System.Serializable]
        public class VehicleSeatGroup
        {
            public List<Transform> AllSeats = new List<Transform>();
            public List<Transform> DriverSeats = new List<Transform>();
        }
    }

    public class VehiclePart
    {

    }
    public class VehicleLight
    {

    }
    public class VehicleWheel
    {
        
    }
    [Serializable]
    public class VehicleExhaust
    {
        public List<Transform> Exhausts = new List<Transform>();
        public GameObject ExhausParticle;

    }
}
