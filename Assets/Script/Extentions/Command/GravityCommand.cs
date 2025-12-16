using UnityEngine;

namespace Meta
{
    [CreateAssetMenu(fileName = "Set Gravity", menuName = "Meta/Console/Set World Gravity")]
    public class GravityCommand : ConsoleCommand
    {
        public override string Execute(string[] _Args)
        {
            if (_Args.Length != 1) { return "Error: Gravity Command Requires Exacly One Argument (A Float Value)."; }

            if (!float.TryParse( _Args[0], out float value)) { return $"Error: Could Not Parse '{_Args[0]}' As A Float"; }

            Physics.gravity = new Vector3 (Physics.gravity.x, value, Physics.gravity.z);
            return $"World Gravity Y-Axis Set To {value}.";
        }
    }
}