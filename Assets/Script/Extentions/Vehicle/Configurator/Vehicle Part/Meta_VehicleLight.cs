using System;
using System.Collections.Generic;
using UnityEngine;
using static Meta.Vehicle.Meta_VehiclePart;

namespace Meta
{
    [AddComponentMenu("Meta/Vehicle Light")]
    [HelpURL("https://github.com/DreamFaver")]
    [Serializable]
    public class Meta_VehicleLight
    {
        public List<VehicleLight> AllLights = new List<VehicleLight>();
        public List<VehicleLight> BrakeLights = new List<VehicleLight>();
        public List<VehicleLight> HeadLights = new List<VehicleLight>();
        public List<VehicleLight> TurnLeftSignal = new List<VehicleLight>();
        public List<VehicleLight> TurnRightSignal = new List<VehicleLight>();
        public List<VehicleLight> ReverseLight = new List<VehicleLight>();

        public float EmissiveIntensity = 5f;

        [Serializable]
        public class VehicleLight
        {
            public Transform Light;
            public Light LightComponent; // ✅ اضافه شد: برای ذخیره کامپوننت Light
            public Material LightMaterial;
            public float BaseIntensity = 2f; // ✅ اضافه شد: شدت نور پیش‌فرض

            // کانستراکتور را به روز کنید (LightComponent در ابتدا null است)
            public VehicleLight(Transform _Light, Material _LightMaterial)
            {
                Light = _Light;
                LightMaterial = _LightMaterial;
                LightComponent = null; // مقداردهی اولیه
            }
        }

        public virtual void GetLight(Transform[] _Parts)
        {
            AllLights.Clear();
            BrakeLights.Clear();
            HeadLights.Clear();
            TurnLeftSignal.Clear();
            TurnRightSignal.Clear();
            ReverseLight.Clear();

            foreach (Transform _Part in _Parts)
            {
                string _Name = _Part.name.ToLower();

                if (!_Name.Contains("light")) continue;

                if (!IsVisualOrAttachmentPart(_Part)) continue;

                VehicleLight _NewLight = new VehicleLight(_Part, null);

                AllLights.Add(_NewLight);

                if (_Name.Contains("brake"))
                    BrakeLights.Add(_NewLight);

                if (_Name.Contains("head"))
                    HeadLights.Add(_NewLight);

                if (_Name.Contains("reverse"))
                    ReverseLight.Add(_NewLight);

                if (_Name.Contains("signal"))
                {
                    if (_Name.Contains("left"))
                        TurnLeftSignal.Add(_NewLight);

                    if (_Name.Contains("right"))
                        TurnRightSignal.Add(_NewLight);
                }
            }
        }
        public virtual void SetLight()
        {
            LightValidate(BrakeLights, Color.red);
            LightValidate(HeadLights, Color.white);
            LightValidate(TurnLeftSignal, new Color(1f, 0.65f, 0f));
            LightValidate(TurnRightSignal, new Color(1f, 0.65f, 0f));
            LightValidate(ReverseLight, Color.white);
        }
        // در فایل Meta_VehicleLight.cs
        public virtual void LightValidate(List<VehicleLight> _LightList, Color _Color)
        {
            foreach (VehicleLight _Light in _LightList)
            {
                Light lightComp = _Light.Light.gameObject.AddComponent<Light>();
                lightComp.type = LightType.Point;
                lightComp.color = _Color;
                lightComp.enabled = false;
                lightComp.intensity = _Light.BaseIntensity; // تنظیم شدت نور

                // ✅ ذخیره کامپوننت Light
                _Light.LightComponent = lightComp;

                // بقیه کد برای متریال...
                MeshRenderer rend = _Light.Light.gameObject.GetComponent<MeshRenderer>();
                if (rend != null)
                {
                    Material mat = rend.material;
                    _Light.LightMaterial = mat;
                }
            }
        }
        // در فایل Meta_VehicleLight.cs
        public virtual void ToggleLights(List<VehicleLight> _LightList, bool _State)
        {
            foreach (VehicleLight _Light in _LightList)
            {
                if (_Light.LightComponent != null)
                {
                    _Light.LightComponent.enabled = _State;
                }

                if (_Light.LightMaterial != null)
                {
                    // تنظیم رنگ Emission برای درخشش متریال
                    if (_State)
                    {
                        // فرض می‌کنیم BaseIntensity همان شدت رنگ متریال است
                        Color _EmissionColor = _Light.LightComponent.color * _Light.BaseIntensity;
                        _Light.LightMaterial.SetColor("_EmissionColor", _EmissionColor);
                        // برای فعال کردن Emission
                        _Light.LightMaterial.EnableKeyword("_EMISSION");
                    }
                    else
                    {
                        // خاموش کردن Emission
                        _Light.LightMaterial.SetColor("_EmissionColor", Color.black);
                        _Light.LightMaterial.DisableKeyword("_EMISSION");
                    }
                }
            }
        }
    }
}