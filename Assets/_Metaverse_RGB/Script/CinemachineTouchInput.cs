using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

public class LimitedCinemachineInput : CinemachineInputProvider
{
    [Header("Touch Limits")]
    [SerializeField] private Rect AllowedArea = new Rect(0.5f, 0f, 0.5f, 1f); // Right half (normalized)

    public override float GetAxisValue(int axis)
    {
        if (Touchscreen.current == null)
            return 0f;

        if (!Touchscreen.current.primaryTouch.press.isPressed)
            return 0f;

        // Prevent look if finger is on UI
        int fingerId = Touchscreen.current.primaryTouch.touchId.ReadValue();
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(fingerId))
            return 0f;

        // Only allow touches inside the AllowedArea
        Vector2 screenPos = Touchscreen.current.primaryTouch.position.ReadValue();
        Vector2 normalized = new Vector2(screenPos.x / Screen.width, screenPos.y / Screen.height);

        if (!AllowedArea.Contains(normalized))
            return 0f;

        // Let the base provider calculate input normally
        return base.GetAxisValue(axis);
    }
}
