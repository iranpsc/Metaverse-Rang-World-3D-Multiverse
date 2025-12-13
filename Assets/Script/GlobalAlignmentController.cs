using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Global Alignment Controller")]
    [DisallowMultipleComponent]
    [Tooltip("Forces the object to remain upright (X/Z World Rotation locked to 0) while following the parent's Yaw (Y-Axis).")]
    public class GlobalAlignmentController : MonoBehaviour
    {
        private void LateUpdate()
        {
            // 1. Get the current World Rotation, which is inherited from the parent (Camera Pivot).
            Quaternion currentWorldRotation = transform.rotation;

            // 2. Extract the Euler angles to easily read the Yaw (Y).
            Vector3 eulerAngles = currentWorldRotation.eulerAngles;

            // 3. Construct a new World Rotation:
            //    - X (Pitch) is set to 0.
            //    - Y (Yaw) is preserved (this is the unlocked axis).
            //    - Z (Roll) is set to 0.
            Quaternion newWorldRotation = Quaternion.Euler(0f, eulerAngles.y, 0f);

            // 4. Apply the constrained rotation.
            transform.rotation = newWorldRotation;
        }
    }
}