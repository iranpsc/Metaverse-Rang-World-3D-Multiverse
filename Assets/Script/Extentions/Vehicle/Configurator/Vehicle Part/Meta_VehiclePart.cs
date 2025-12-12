using System;
using UnityEngine;

namespace Meta.Vehicle
{
    [AddComponentMenu("Meta/Vehicle Part")]
    [HelpURL("https://github.com/DreamFaver")]
    [Serializable]
    public class Meta_VehiclePart
    {
        public Transform[] AllParts;

        public Meta_VehiclePart(GameObject _Vehicle)
        {
            if (_Vehicle != null)
                AllParts = _Vehicle.GetComponentsInChildren<Transform>(true);
            else
                AllParts = new Transform[0];
        }

        public static bool IsVisualOrAttachmentPart(Transform _Part)
        {
            string _Name = _Part.name.ToLower();

            if (_Part.GetComponent<MeshRenderer>() != null)
                return true;

            if (_Name.Contains("exhaust") || _Name.Contains("thruster") || _Name.Contains("rotor") || _Name.Contains("propeller") || _Name.Contains("handlebar") || _Name.Contains("rudder"))
            {
                return true;
            }

            return false;
        }
    }
}