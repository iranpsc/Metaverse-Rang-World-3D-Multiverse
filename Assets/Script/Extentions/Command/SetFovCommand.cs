using Unity.Cinemachine;
using UnityEngine;

namespace Meta
{
    [CreateAssetMenu(fileName = "Set Camera FOV", menuName = "Meta/Console/Set Camera FOV")]
    public class SetFovCommand : ConsoleCommand
    {
        public override string Execute(string[] _Args)
        {
            if (_Args.Length != 1)
            {
                return "Error: FOV command requires exactly one argument (a float value). Usage: fov <value>";
            }

            if (!float.TryParse(_Args[0], out float _FovValue))
            {
                return $"Error: Could not parse '{_Args[0]}' as a float for FOV.";
            }

            // 1. Find the CinemachineBrain (usually attached to the Main Camera)
            CinemachineBrain _Brain = Camera.main?.GetComponent<CinemachineBrain>();
            if (_Brain == null)
            {
                return "Error: CinemachineBrain component not found on the Main Camera. Ensure Cinemachine is set up.";
            }

            // 2. Get the current active camera (the one the brain is controlling)
            ICinemachineCamera _CurrentVcam = _Brain.ActiveVirtualCamera;

            if (_CurrentVcam == null)
            {
                return "Error: CinemachineBrain has no active virtual camera.";
            }

            if (_CurrentVcam is CinemachineCamera _VirtualCamera)
            {
                if (_FovValue < 1 || _FovValue > 179)
                {
                    return $"Error: FOV value {_FovValue} is out of the standard range (1-179).";
                }

                _VirtualCamera.Lens.FieldOfView = _FovValue;

                return $"Active Virtual Camera ({_CurrentVcam.Name}) FOV successfully set to {_FovValue}.";
            }
            else
            {
                return $"Error: Active Virtual Camera ({_CurrentVcam.Name}) is not a CinemachineVirtualCamera and cannot be directly controlled by this command.";
            }
        }
    }
}