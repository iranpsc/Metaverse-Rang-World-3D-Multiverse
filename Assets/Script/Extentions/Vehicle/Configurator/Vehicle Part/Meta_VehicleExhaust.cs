using System;
using System.Collections.Generic;
using UnityEngine;
using static Meta.Vehicle.Meta_VehiclePart;

namespace Meta.Vehicle
{
    [AddComponentMenu("Meta/Vehicle Exhaust")]
    [HelpURL("https://github.com/DreamFaver")]
    [Serializable]
    public class Meta_VehicleExhaust
    {
        public List<Transform> AllExhausts = new List<Transform>();
        public GameObject SmokeEffect;
        public Transform MainRotor;
        public Transform TailRotor;
        public virtual void GetExhaustsAndPropellers(Transform[] _Parts)
        {

            AllExhausts.Clear();
            MainRotor = null;
            TailRotor = null;

            foreach (Transform _Part in _Parts)
            {
                string _Name = _Part.name.ToLower();

                if (!IsVisualOrAttachmentPart(_Part)) continue;

                if (_Name.Contains("exhaust") || _Name.Contains("thruster"))
                {
                    AllExhausts.Add(_Part);
                    continue;
                }

                if (_Name.Contains("rotor") || _Name.Contains("propeller"))
                {
                    if (_Name.Contains("main") && MainRotor ==  null)
                        MainRotor = _Part;
                    else if (_Name.Contains("tail") && TailRotor == null)
                        TailRotor = _Part;
                }
            }
        }
        public virtual void SetExhaust()
        {
            if (!SmokeEffect) return;
            foreach(Transform _Exhaust in AllExhausts)
            {
                GameObject Particle = UnityEngine.Object.Instantiate(SmokeEffect, _Exhaust);
                // Particle.transform.SetParent(_Exhaust); // already done by Instantiate(..., _Exhaust)
            }
        }
    }
}