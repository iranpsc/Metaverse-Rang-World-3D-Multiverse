using System;
using System.Collections.Generic;
using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_VehicleLight")]
    [HelpURL("https://google.com")]
    [Serializable]

    public class Meta_VehicleLight
    {
        public List<VehicleLight> AllLights;
        public List<VehicleLight> BrakeLights;
        public List<VehicleLight> HeadLights;
        public List<VehicleLight> TurnLeftSignal;
        public List<VehicleLight> TurnRightSignal;
        public List<VehicleLight> ReverseLight;

        [Serializable]
        public class VehicleLight
        {
            public Transform Light;
            public Material LightMaterial;
            public VehicleLight(Transform _Light, Material _LightMaterial)
            {
                Light = _Light;
                LightMaterial = _LightMaterial;
            }
        }

        public virtual void GetLight(Transform[] _Part)
        {
            foreach (Transform _Light in _Part)
            {
                string _name = _Light.name.ToLower();
                if (!_name.Contains("light") || _Light.childCount > 0 || !_Light.GetComponent<MeshRenderer>()) continue;

                AllLights.Add(new VehicleLight(_Light, null));

                if (_name.Contains("brake"))
                    BrakeLights.Add(new VehicleLight(_Light, null));

                if (_name.Contains("head"))
                    HeadLights.Add(new VehicleLight(_Light, null));

                if (_name.Contains("reverse"))
                    ReverseLight.Add(new VehicleLight(_Light, null));

                if (_name.Contains("signal"))
                {
                    if (_name.Contains("left"))
                        TurnLeftSignal.Add(new VehicleLight(_Light, null));

                    if (_name.Contains("right"))
                        TurnRightSignal.Add(new VehicleLight(_Light, null));
                }
            }
        }
        public virtual void SetLight()
        {
            LightValidate(BrakeLights, Color.red);
            LightValidate(HeadLights, Color.white);
            LightValidate(TurnLeftSignal, Color.orange);
            LightValidate(TurnRightSignal, Color.orange);
            LightValidate(ReverseLight, Color.white);
        }
        public virtual void LightValidate(List<VehicleLight> _LightList, Color _Color)
        {
            foreach (VehicleLight _Light in _LightList)
            {
                //GameObject lightObj = new GameObject("Light");
                //lightObj.transform.SetParent(_Light.Light);
                //lightObj.transform.localPosition = Vector3.zero;
                //lightObj.transform.localRotation = Quaternion.identity;
                

                Light lightComp = _Light.Light.gameObject.AddComponent<Light>();
                lightComp.type = LightType.Point;
                lightComp.color = _Color;
                lightComp.enabled = false;

                MeshRenderer rend = _Light.Light.gameObject.GetComponent<MeshRenderer>();
                Material mat = rend.material;

                _Light.LightMaterial = mat;
            }
        }
    }
}