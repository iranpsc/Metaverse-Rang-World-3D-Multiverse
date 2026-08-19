using UnityEngine;
using Unity.Cinemachine;

[AddComponentMenu("Meta RGB/Player/Base.Player Camera")]
public class PlayerCamera : MonoBehaviour
{
    // ============================================================
    // REFERENCES
    // ============================================================

    private CinemachineCamera Camera;
    private Transform CameraPivot;

    // ============================================================
    // UNITY
    // ============================================================

    private void Awake()
    {
        Camera = FindFirstObjectByType<CinemachineCamera>();

        if (Camera == null)
        {
            Debug.LogError($"{nameof(PlayerCamera)} could not find a cinemachine camera in the scene");
            enabled = false;
            return;
        }
        CameraPivot = FindCameraPivot();

        if (CameraPivot == null)
        {
            Debug.LogError($"{nameof(PlayerCamera)} could not find a CameraPivot");
            enabled = false;
            return;
        }
    }

    private void Start()
    {
        SetupCamera();
    }

    // ============================================================
    // CAMERA SETUP
    // ============================================================

    private void SetupCamera()
    {
        Camera.Follow = CameraPivot;
        Camera.LookAt = CameraPivot;
        Camera.Priority = 100;
    }

    // ============================================================
    // FIND PIVOT
    // ============================================================

    private Transform FindCameraPivot()
    {
        Transform _Pivot = transform.Find("[Player] Camera Pivot");

        if (_Pivot != null)
            return _Pivot;

        Debug.LogError($"Player '{gameObject.name}' does not contain a child named '[Player] Camera Pivot'");
        return null;
    }
}
