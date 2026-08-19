using UnityEngine;

[AddComponentMenu("Meta RGB/Player/Base.Player Look")]
public class PlayerLook : MonoBehaviour
{
    // ============================================================
    // SETTINGS
    // ============================================================

    [Header("Look")]
    [SerializeField] private float Sensitivity = 0.1f;
    [SerializeField] private float MinPitch = -89f;
    [SerializeField] private float MaxPitch = 89f;
    [SerializeField] private bool InvertY = false;

    // ============================================================
    // REFERENCES
    // ============================================================

    private Transform CameraPivot;
    private PlayerCursor Cursor;

    // ============================================================
    // STATE
    // ============================================================

    private float Yaw;
    private float Pitch;

    private bool CursorLocked;

    // ============================================================
    // UNITY
    // ============================================================

    private void Awake()
    {
        CameraPivot = transform.Find("Camera Pivot");
        Cursor = GetComponent<PlayerCursor>();

        if (CameraPivot == null)
        {
            Debug.LogError(
                $"[{nameof(PlayerLook)}] " +
                $"Could not find 'Camera Pivot' under '{gameObject.name}'."
            );

            enabled = false;
            return;
        }

        if (Cursor == null)
        {
            Debug.LogError(
                $"[{nameof(PlayerLook)}] " +
                $"Could not find {nameof(PlayerCursor)} on '{gameObject.name}'."
            );

            enabled = false;
            return;
        }

        Yaw = transform.eulerAngles.y;

        Pitch = NormalizeAngle(
            CameraPivot.localEulerAngles.x
        );
    }

    private void OnEnable()
    {
        if (Cursor == null)
            return;

        Cursor.OnCursorLockChanged += HandleCursorLockChanged;

        CursorLocked = Cursor.CursorLocked;
    }

    private void OnDisable()
    {
        if (Cursor == null)
            return;

        Cursor.OnCursorLockChanged -= HandleCursorLockChanged;
    }

    private void Update()
    {
        if (!CursorLocked)
            return;

        if (PlayerInput.Instance == null)
            return;

        HandleLook();
    }

    // ============================================================
    // CURSOR
    // ============================================================

    private void HandleCursorLockChanged(bool _Locked)
    {
        CursorLocked = _Locked;
    }

    // ============================================================
    // LOOK
    // ============================================================

    private void HandleLook()
    {
        Vector2 _Look = PlayerInput.Instance.Look;

        float _MouseX = _Look.x * Sensitivity;
        float _MouseY = _Look.y * Sensitivity;

        if (InvertY)
            _MouseY *= -1f;

        // --------------------------------------------------------
        // PLAYER YAW
        // --------------------------------------------------------

        Yaw += _MouseX;

        transform.rotation = Quaternion.Euler(
            0f,
            Yaw,
            0f
        );

        // --------------------------------------------------------
        // CAMERA PITCH
        // --------------------------------------------------------

        Pitch -= _MouseY;

        Pitch = Mathf.Clamp(
            Pitch,
            MinPitch,
            MaxPitch
        );

        CameraPivot.localRotation = Quaternion.Euler(
            Pitch,
            0f,
            0f
        );
    }

    // ============================================================
    // HELPERS
    // ============================================================

    private float NormalizeAngle(float _Angle)
    {
        if (_Angle > 180f)
            _Angle -= 360f;

        return _Angle;
    }
}