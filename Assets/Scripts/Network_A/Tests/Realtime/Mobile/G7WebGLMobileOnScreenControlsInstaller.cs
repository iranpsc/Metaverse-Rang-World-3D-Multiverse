#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.OnScreen;
using UnityEngine.UI;

[DefaultExecutionOrder(-500)]
public sealed class G7WebGLMobileOnScreenControlsInstaller : MonoBehaviour
{
    [DllImport("__Internal")]
    private static extern int G7WebGL_IsMobileBrowser();

    private static G7WebGLMobileOnScreenControlsInstaller instance;

    private Canvas controlsCanvas;
    private RectTransform safeAreaRoot;
    private GameObject joystickRoot;
    private GameObject jumpRoot;
    private G7ThreeDModeController threeDModeController;
    private Rect lastSafeArea;
    private Vector2Int lastScreenSize;
    private bool isMobileBrowser;
    private bool controlsVisible;
    private GameObject moveSpeedAdjustedPlayer;

    private static readonly FieldInfo MoveSpeedField =
        typeof(G7SimpleCylinderCharacterController).GetField(
            "moveSpeed",
            BindingFlags.Instance | BindingFlags.NonPublic
        );

    private const float JoystickBaseSize = 250f;
    private const float JoystickHandleSize = 150f;
    private const float JoystickMovementRange = 72f;
    private const float JumpButtonSize = 150f;
    private const float EdgeMargin = 52f;
    private const float JumpAboveVoiceButtonsOffset = 180f;
    private const float JumpTowardCenterOffset = 260f;
    private const float MobileMoveSpeedMultiplier = 0.5f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void InstallBeforeSceneLoad()
    {
        if (instance != null) return;

        GameObject root = new GameObject("G7_WebGL_Mobile_OnScreen_Controls");
        DontDestroyOnLoad(root);
        instance = root.AddComponent<G7WebGLMobileOnScreenControlsInstaller>();
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        DontDestroyOnLoad(gameObject);

        try
        {
            isMobileBrowser = G7WebGL_IsMobileBrowser() == 1;
        }
        catch (Exception exception)
        {
            isMobileBrowser = false;
            Debug.LogWarning(
                "[G7-MobileInput] Mobile browser detection failed | reason=" +
                exception.GetType().Name
            );
        }

        if (!isMobileBrowser)
        {
            Debug.Log(
                "[G7-MobileInput] Disabled | reason=not_mobile_browser | webglDesktopChanged=false | windowsChanged=false"
            );
            enabled = false;
            return;
        }

        BuildControls();
        SetControlsVisible(false);

        Debug.Log(
            "[G7-MobileInput] READY | platform=WebGLMobile | move=<Gamepad>/leftStick | jump=<Gamepad>/buttonSouth | existingMovementChanged=false | dedicatedStateChanged=false | windowsChanged=false"
        );
    }

    private void Update()
    {
        ResolveThreeDModeController();
        ApplyMobileMoveSpeedIfNeeded();
        RefreshSafeAreaIfNeeded();

        bool shouldShow =
            threeDModeController != null &&
            threeDModeController.IsThreeDModeActive;

        SetControlsVisible(shouldShow);
    }

    private void ResolveThreeDModeController()
    {
        if (threeDModeController != null) return;

        threeDModeController = FindFirstObjectByType<G7ThreeDModeController>(FindObjectsInactive.Include);

        if (threeDModeController != null)
        {
            Debug.Log(
                "[G7-MobileInput] 3D controller bound | controlsVisible=" +
                threeDModeController.IsThreeDModeActive
            );
        }
    }

    private void BuildControls()
    {
        GameObject canvasObject = new GameObject(
            "G7_WebGL_Mobile_Controls_Canvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster)
        );

        canvasObject.transform.SetParent(transform, false);

        controlsCanvas = canvasObject.GetComponent<Canvas>();
        controlsCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        controlsCanvas.sortingOrder = 32000;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        GameObject safeObject = new GameObject(
            "Safe_Area",
            typeof(RectTransform)
        );

        safeObject.transform.SetParent(canvasObject.transform, false);
        safeAreaRoot = safeObject.GetComponent<RectTransform>();
        safeAreaRoot.anchorMin = Vector2.zero;
        safeAreaRoot.anchorMax = Vector2.one;
        safeAreaRoot.offsetMin = Vector2.zero;
        safeAreaRoot.offsetMax = Vector2.zero;

        BuildJoystick();
        BuildJumpButton();
        ApplySafeArea();

        if (EventSystem.current == null)
        {
            Debug.LogWarning(
                "[G7-MobileInput] No EventSystem found. Existing project UI should provide EventSystem for touch controls."
            );
        }
    }

    private void BuildJoystick()
    {
        joystickRoot = new GameObject(
            "Move_Joystick_Base",
            typeof(RectTransform),
            typeof(Image)
        );

        joystickRoot.transform.SetParent(safeAreaRoot, false);

        RectTransform baseRect = joystickRoot.GetComponent<RectTransform>();
        baseRect.anchorMin = new Vector2(0f, 0f);
        baseRect.anchorMax = new Vector2(0f, 0f);
        baseRect.pivot = new Vector2(0.5f, 0.5f);
        baseRect.sizeDelta = new Vector2(JoystickBaseSize, JoystickBaseSize);
        baseRect.anchoredPosition = new Vector2(
            EdgeMargin + JoystickBaseSize * 0.5f,
            EdgeMargin + JoystickBaseSize * 0.5f
        );

        Image baseImage = joystickRoot.GetComponent<Image>();
        baseImage.color = new Color(0f, 0f, 0f, 0.28f);
        baseImage.raycastTarget = false;

        GameObject handleObject = new GameObject(
            "Move_Joystick_Handle",
            typeof(RectTransform),
            typeof(Image),
            typeof(OnScreenStick)
        );

        handleObject.transform.SetParent(joystickRoot.transform, false);

        RectTransform handleRect = handleObject.GetComponent<RectTransform>();
        handleRect.anchorMin = new Vector2(0.5f, 0.5f);
        handleRect.anchorMax = new Vector2(0.5f, 0.5f);
        handleRect.pivot = new Vector2(0.5f, 0.5f);
        handleRect.sizeDelta = new Vector2(JoystickHandleSize, JoystickHandleSize);
        handleRect.anchoredPosition = Vector2.zero;

        Image handleImage = handleObject.GetComponent<Image>();
        handleImage.color = new Color(1f, 1f, 1f, 0.62f);
        handleImage.raycastTarget = true;

        OnScreenStick stick = handleObject.GetComponent<OnScreenStick>();
        stick.controlPath = "<Gamepad>/leftStick";
        stick.movementRange = JoystickMovementRange;
    }

    private void BuildJumpButton()
    {
        jumpRoot = new GameObject(
            "Jump_Button",
            typeof(RectTransform),
            typeof(Image),
            typeof(OnScreenButton)
        );

        jumpRoot.transform.SetParent(safeAreaRoot, false);

        RectTransform jumpRect = jumpRoot.GetComponent<RectTransform>();
        jumpRect.anchorMin = new Vector2(1f, 0f);
        jumpRect.anchorMax = new Vector2(1f, 0f);
        jumpRect.pivot = new Vector2(0.5f, 0.5f);
        jumpRect.sizeDelta = new Vector2(JumpButtonSize, JumpButtonSize);
        jumpRect.anchoredPosition = new Vector2(
            -(EdgeMargin + JumpButtonSize * 0.5f + JumpTowardCenterOffset),
            EdgeMargin + JumpButtonSize * 0.5f + JumpAboveVoiceButtonsOffset
        );

        Image jumpImage = jumpRoot.GetComponent<Image>();
        jumpImage.color = new Color(1f, 1f, 1f, 0.72f);
        jumpImage.raycastTarget = true;

        OnScreenButton button = jumpRoot.GetComponent<OnScreenButton>();
        button.controlPath = "<Gamepad>/buttonSouth";

        GameObject labelObject = new GameObject(
            "Jump_Label",
            typeof(RectTransform),
            typeof(Text)
        );

        labelObject.transform.SetParent(jumpRoot.transform, false);

        RectTransform labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        Text label = labelObject.GetComponent<Text>();
        label.text = "JUMP";
        label.alignment = TextAnchor.MiddleCenter;
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize = 28;
        label.color = new Color(0f, 0f, 0f, 0.9f);
        label.raycastTarget = false;
    }

    private void ApplyMobileMoveSpeedIfNeeded()
    {
        if (threeDModeController == null || !threeDModeController.IsThreeDModeActive)
        {
            return;
        }

        GameObject localPlayer = threeDModeController.LocalPlayerInstance;
        if (localPlayer == null || localPlayer == moveSpeedAdjustedPlayer)
        {
            return;
        }

        G7SimpleCylinderCharacterController movement =
            localPlayer.GetComponent<G7SimpleCylinderCharacterController>();

        if (movement == null)
        {
            return;
        }

        if (MoveSpeedField == null)
        {
            Debug.LogWarning(
                "[G7-MobileInput] Mobile move speed scale skipped | reason=moveSpeed_field_not_found"
            );
            moveSpeedAdjustedPlayer = localPlayer;
            return;
        }

        float originalSpeed = (float)MoveSpeedField.GetValue(movement);
        float mobileSpeed = originalSpeed * MobileMoveSpeedMultiplier;
        MoveSpeedField.SetValue(movement, mobileSpeed);
        moveSpeedAdjustedPlayer = localPlayer;

        Debug.Log(
            "[G7-MobileInput] Mobile move speed scaled | original=" +
            originalSpeed.ToString("F3") +
            " | mobile=" + mobileSpeed.ToString("F3") +
            " | multiplier=" + MobileMoveSpeedMultiplier.ToString("F2") +
            " | windowsChanged=false | webglDesktopChanged=false"
        );
    }

    private void RefreshSafeAreaIfNeeded()
    {
        if (safeAreaRoot == null) return;

        Rect currentSafeArea = Screen.safeArea;
        Vector2Int currentScreenSize = new Vector2Int(Screen.width, Screen.height);

        if (currentSafeArea == lastSafeArea && currentScreenSize == lastScreenSize)
        {
            return;
        }

        ApplySafeArea();
    }

    private void ApplySafeArea()
    {
        if (safeAreaRoot == null || Screen.width <= 0 || Screen.height <= 0)
        {
            return;
        }

        Rect safe = Screen.safeArea;
        lastSafeArea = safe;
        lastScreenSize = new Vector2Int(Screen.width, Screen.height);

        Vector2 anchorMin = safe.position;
        Vector2 anchorMax = safe.position + safe.size;

        anchorMin.x /= Screen.width;
        anchorMin.y /= Screen.height;
        anchorMax.x /= Screen.width;
        anchorMax.y /= Screen.height;

        safeAreaRoot.anchorMin = anchorMin;
        safeAreaRoot.anchorMax = anchorMax;
        safeAreaRoot.offsetMin = Vector2.zero;
        safeAreaRoot.offsetMax = Vector2.zero;
    }

    private void SetControlsVisible(bool visible)
    {
        if (controlsCanvas == null || controlsVisible == visible) return;

        controlsVisible = visible;
        controlsCanvas.gameObject.SetActive(visible);

        Debug.Log(
            "[G7-MobileInput] Controls visibility changed | visible=" + visible
        );
    }
}
#endif
