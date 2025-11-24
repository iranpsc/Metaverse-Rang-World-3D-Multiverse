using System;
using UnityEngine;

namespace Meta.Vehicle
{
    [AddComponentMenu("Meta/Meta_VehiclePart")]
    [HelpURL("https://google.com")]
    [Serializable]
    public class Meta_VehiclePart
    {
        public Transform[] Parts;

        public Meta_VehiclePart(GameObject _Vehicle)
        {
            Parts = _Vehicle.GetComponentsInChildren<Transform>(true);
        }
    }
}