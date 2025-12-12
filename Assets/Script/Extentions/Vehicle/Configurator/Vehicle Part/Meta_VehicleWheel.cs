using Mirror;
using System;
using System.Collections.Generic;
using UnityEngine;
using static Meta.Vehicle.Meta_VehiclePart;

namespace Meta
{
    [AddComponentMenu("Meta/Vehicle Wheel")]
    [HelpURL("https://github.com/DreamFaver")]
    [Serializable]
    public class Meta_VehicleWheel
    {
        public List<Transform> AllWheels = new List<Transform>();
        public List<Transform> FrontWheels = new List<Transform>();
        public List<Transform> RearWheels = new List<Transform>();

        public List<WheelCollider> AllCollider = new List<WheelCollider>();
        public List<WheelCollider> FrontCollider = new List<WheelCollider>();
        public List<WheelCollider> RearCollider = new List<WheelCollider>();

        public Transform SteeringWheel;
        public Transform Handlebar;

        public float WheelSuspensionDistance = 0.2f;
        public float WheelMass = 30f;

        public virtual void GetWheelsAndSteering(Transform[] _Parts)
        {
            AllWheels.Clear();
            FrontWheels.Clear();
            RearWheels.Clear();
            SteeringWheel = null;
            Handlebar = null;

            foreach (Transform _Part in _Parts)
            {
                string _Name = _Part.name.ToLower();

                if (_Name.Contains("wheel") || _Name.Contains("tire"))
                {
                    if (!IsVisualOrAttachmentPart(_Part)) continue;

                    AllWheels.Add(_Part);
                    if (_Name.Contains("front"))
                        FrontWheels.Add(_Part);
                    if (_Name.Contains("rear"))
                        RearWheels.Add(_Part);
                    continue;
                }

                if (_Name.Contains("steering") && SteeringWheel == null)
                {
                    if (IsVisualOrAttachmentPart(_Part))
                        SteeringWheel = _Part;
                    continue;
                }

                if ((_Name.Contains("handlebar") || _Name.Contains("rudder")) && Handlebar == null)
                {
                    if (IsVisualOrAttachmentPart(_Part))
                        Handlebar = _Part;
                }
            }
        }
        public virtual void SetWheels()
        {
            foreach (Transform _Wheels in AllWheels)
            {
                if (_Wheels.GetComponentInChildren<WheelCollider>()) continue;

                MeshFilter _MeshFilter = _Wheels.GetComponent<MeshFilter>();
                if (!_MeshFilter || _MeshFilter.sharedMesh == null)
                {
                    Debug.Log($"[VehicleWheel]  Missing mesh on {_Wheels.name}");
                    continue;
                }
                float _SuspensionDistance = WheelSuspensionDistance;
                float _WheelRadius = _MeshFilter.sharedMesh.bounds.extents.y * _Wheels.transform.lossyScale.y;

                GameObject _Collider = new GameObject(_Wheels.name + "_Collider");
                _Collider.transform.SetParent(_Wheels.transform);
                _Collider.transform.localPosition = new Vector3(0f, _SuspensionDistance * 0.5f, 0f);
                _Collider.transform.localRotation = Quaternion.identity;
                _Collider.transform.SetParent(_Wheels.transform.parent, true);

                WheelCollider _WheelCollider = _Collider.AddComponent<WheelCollider>();
                _WheelCollider.radius = _WheelRadius;
                _WheelCollider.suspensionDistance = _SuspensionDistance;
                _WheelCollider.mass = WheelMass;

                JointSpring _Spring = new JointSpring
                {
                    spring = 15000f,
                    damper = 5000, // 5000 or 2500 عدد بالاتر یعنی کمک فنر سفت تر
                    targetPosition = 0.5f
                };

                _WheelCollider.suspensionSpring = _Spring;
                WheelFrictionCurve _ForwardFriction = _WheelCollider.forwardFriction;
                _ForwardFriction.stiffness = 1.5f;
                _WheelCollider.forwardFriction = _ForwardFriction;

                WheelFrictionCurve _SideFriction = _WheelCollider.sidewaysFriction;
                _SideFriction.stiffness = 2.0f;
                _WheelCollider.sidewaysFriction = _SideFriction;

                if (FrontWheels.Contains(_Wheels))
                    FrontCollider.Add(_WheelCollider);
                if (RearWheels.Contains(_Wheels))
                    RearCollider.Add(_WheelCollider);
                AllCollider.Add(_WheelCollider);
            }
        }
    }
}