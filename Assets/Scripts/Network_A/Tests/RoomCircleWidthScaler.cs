using Network_A.Realtime.Controllers;
using UnityEngine;

public sealed class RoomCircleWidthScaler : MonoBehaviour
{
    private const string CircleObjectName = "circle_01";

    //* این تابع بعد از ساخته‌شدن محیط سه‌بعدی، عرض ساختمان انتخاب‌شده را می‌خواند و روی دایره اعمال می‌کند.
    private void Start()
    {
        RealtimeRoomGameServerManager manager = RealtimeRoomGameServerManager.Instance;

        if (manager == null)
        {
            Debug.LogError("[RoomCircleWidthScaler] RealtimeRoomGameServerManager was not found.", this);
            return;
        }

        if (!manager.TryGetSelectedBuildingDimensions(out float width, out float length, out float density))
        {
            Debug.LogError(
                "[RoomCircleWidthScaler] Selected building width is missing or invalid. value=" +
                manager.SelectedBuildingWidth,
                this
            );

            return;
        }

        Transform circleTransform = FindChildRecursive(transform, CircleObjectName);

        if (circleTransform == null)
        {
            Debug.LogError(
                "[RoomCircleWidthScaler] Child object '" + CircleObjectName +
                "' was not found under '" + gameObject.name + "'.",
                this
            );

            return;
        }

        Vector3 currentScale = circleTransform.localScale;
        circleTransform.localScale = new Vector3((width * length * density) / 100, (width * length * density) / 100, (width * length * density) / 100);

        Debug.Log(
            "[RoomCircleWidthScaler] Circle scaled successfully." +
            " | width=" + width +
            " | scale=" + circleTransform.localScale,
            circleTransform
        );
    }

    //* این تابع آبجکت دارای نام مشخص را در تمام فرزندان پیدا می‌کند.
    private static Transform FindChildRecursive(Transform parent, string childName)
    {
        if (parent == null) return null;
        if (parent.name == childName) return parent;

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform result = FindChildRecursive(parent.GetChild(i), childName);
            if (result != null) return result;
        }

        return null;
    }
}