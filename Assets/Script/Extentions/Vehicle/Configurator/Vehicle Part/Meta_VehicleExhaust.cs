using System;
using System.Collections.Generic;
using UnityEngine;

namespace Meta.Vehicle
{
    [AddComponentMenu("Meta/Meta_VehicleExhaust")]
    [HelpURL("https://google.com")]
    [Serializable]

    public class Meta_VehicleExhaust
    {
        public List<Transform> AllExhausts;
        public GameObject SmokeEffect;
        public virtual void GetExhausts(Transform[] _Part)
        {
            foreach (Transform _Exhaust in _Part)
            {
                string _name = _Exhaust.name.ToLower();
                if ((_name.Contains("exhaust") || _name.Contains("thruster")) && _Exhaust.childCount == 0 && _Exhaust.GetComponent<MeshRenderer>())
                {
                    AllExhausts.Add(_Exhaust);
                }
            }
        }
        public virtual void SetExhaust()
        {
            if (!SmokeEffect) return;
            foreach(Transform _Exhaust in AllExhausts)
            {
                GameObject Particle = UnityEngine.Object.Instantiate(SmokeEffect, _Exhaust);
            }
        }
    }
}