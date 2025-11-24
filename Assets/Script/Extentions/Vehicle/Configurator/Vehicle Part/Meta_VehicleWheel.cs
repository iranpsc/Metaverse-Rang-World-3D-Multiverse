using System;
using System.Collections.Generic;
using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_VehicleWheel")]
    [HelpURL("https://google.com")]
    [Serializable]

    public class Meta_VehicleWheel
    {
        public List<Transform> AllWheels;
        public List<Transform> FrontWheels;
        public List<Transform> RearWheels;

        public List<WheelCollider> AllCollider;
        public List<WheelCollider> FrontCollider;
        public List<WheelCollider> RearCollider;

        public float WheelSuspensionDistance = 0.2f;
        public float WheelMass = 30f;

        public virtual void GetWheels(Transform[] _Part)
        {
            foreach (Transform _Wheels in _Part)
            {
                string _name = _Wheels.name.ToLower();
                if (!_name.Contains("wheel") || _Wheels.childCount > 0 /*|| !_Wheels.GetComponent<MeshRenderer>()*/) continue;

                AllWheels.Add(_Wheels);

                if (_name.Contains("front"))
                    FrontWheels.Add(_Wheels);
                if (_name.Contains("rear"))
                    RearWheels.Add(_Wheels);
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
                    damper = 2500f,
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