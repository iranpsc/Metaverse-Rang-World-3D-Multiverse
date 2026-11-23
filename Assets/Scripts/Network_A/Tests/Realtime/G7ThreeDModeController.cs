using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class G7ThreeDModeController : MonoBehaviour
{
    [Header("3D Button")]
    [SerializeField] private Button button3D;

    [Header("Disable When 3D Starts")]
    [SerializeField] private GameObject[] objectsToDisableWhen3DStarts;

    [Header("Enable When 3D Starts")]
    [SerializeField] private GameObject[] objectsToEnableWhen3DStarts;

    [Header("3D World")]
    [SerializeField] private GameObject world3DRoot;
    [SerializeField] private Transform playersRoot;
    [SerializeField] private Transform localPlayerSpawnPoint;

    [Header("Player Prefabs")]
    [SerializeField] private GameObject localPlayerPrefab;
    [SerializeField] private GameObject remotePlayerPrefab;

    [Header("Third Person Camera")]
    [SerializeField] private Camera thirdPersonCamera;
    [SerializeField] private bool parentCameraToLocalPlayer = true;
    [SerializeField] private bool activateCameraIn3DMode = true;
    [SerializeField] private Vector3 cameraLocalPosition = new Vector3(0f, 2.2f, -4f);
    [SerializeField] private Vector3 cameraLocalEulerAngles = new Vector3(15f, 0f, 0f);


    [Header("Player Name Text")]
    [SerializeField] private bool showPlayerNameTexts = true;
    [SerializeField] private GameObject playerNameTextPrefab;
    [SerializeField] private string playerNameObjectName = "Name_Text";
    [SerializeField] private Vector3 playerNameLocalPosition = new Vector3(0f, 2.25f, 0f);
    [SerializeField] private Vector2 playerNameRectSize = new Vector2(4f, 0.7f);
    [SerializeField] private float playerNameLocalScale = 0.08f;
    [SerializeField] private float playerNameFontSize = 4f;
    [SerializeField] private Color localPlayerNameColor = Color.white;
    [SerializeField] private Color remotePlayerNameColor = Color.white;
    [SerializeField] private bool rotateNameTextsToCamera = true;
    [SerializeField] private bool rotateNameTextRootObject = true;
    [SerializeField] private bool flipNameTextAfterLookAt = true;
    [SerializeField] private string fallbackLocalPlayerName = "You";

    [Header("Options")]
    [SerializeField] private bool startIn3DMode;
    [SerializeField] private bool resetLocalPlayerOnEnter = true;
    [SerializeField] private bool hideCursorIn3DMode;

    [Header("Confirmed Exit Cleanup")]
    [SerializeField] private bool destroyLocalPlayerOnConfirmedExit = true;
    [SerializeField] private bool clearRemotePlayersOnConfirmedExit = true;
    [SerializeField] private bool destroyRuntimePlayerRootChildrenOnConfirmedExit = true;
    [SerializeField] private bool destroyAllChildrenUnderPlayersRootOnCleanup = true;
    [SerializeField] private bool disableWorldRootOnConfirmedExit = true;
    [SerializeField] private bool restoreUiObjectsOnConfirmedExit = true;
    [SerializeField] private bool invokeThreeDModeExitedOnConfirmedExit = true;

    private bool isThreeDModeActive;
    private GameObject localPlayerInstance;
    private Transform originalCameraParent;
    private Vector3 originalCameraPosition;
    private Quaternion originalCameraRotation = Quaternion.identity;
    private bool originalCameraActive;
    private bool hasOriginalCameraState;
    private string localPlayerDisplayName = string.Empty;
    private TMP_Text localPlayerNameText;
    private readonly Dictionary<string, GameObject> dict_RemotePlayersByUserId = new Dictionary<string, GameObject>();
    private readonly Dictionary<string, TMP_Text> dict_RemoteNameTextsByUserId = new Dictionary<string, TMP_Text>();
    private readonly HashSet<string> set_TemporarilyInactiveRemoteUserIds = new HashSet<string>();

    public bool IsThreeDModeActive => isThreeDModeActive;
    public GameObject LocalPlayerInstance => localPlayerInstance;
    public GameObject World3DRoot => world3DRoot;
    public Transform PlayersRoot => playersRoot;

    public event Action OnThreeDModeEntered;
    public event Action OnThreeDModeExited;

    //* این تابع ایونت های دکمه را وصل می کند و وضعیت اولیه سه بعدی را اعمال می کند.
    private void Awake()
    {
        ResolveThirdPersonCamera();
        CaptureOriginalCameraState();
        BindButtons();
        SetThreeDMode(startIn3DMode);
        if (startIn3DMode) SpawnOrResetLocalPlayer();
    }

    //* این تابع ایونت های دکمه را قطع می کند تا لیسنر تکراری باقی نماند.
    private void OnDestroy()
    {
        UnbindButtons();
    }

    //* این تابع تکست های نام پلیرها را هر فریم به سمت دوربین می چرخاند تا خوانا بمانند.
    private void Update()
    {
        RotateNameTextsToCamera();
    }

    //* این تابع در پایان هر فریم قفل غیرفعال بودن موقت پلیرهای ریموت را اعمال می کند.
    private void LateUpdate()
    {
        EnforceTemporarilyInactiveRemotePlayers();
    }

    //* این تابع دکمه سه بعدی را به مسیر ورود سه بعدی وصل می کند.
    private void BindButtons()
    {
        if (button3D != null) button3D.onClick.AddListener(EnterThreeDMode);
    }

    //* این تابع دکمه سه بعدی را از مسیر ورود سه بعدی جدا می کند.
    private void UnbindButtons()
    {
        if (button3D != null) button3D.onClick.RemoveListener(EnterThreeDMode);
    }
    //* این تابع حالت سه بعدی را برای ریکانکت فعال می کند، بدون اینکه موقعیت پلیر موجود ریست شود.
    public void EnterThreeDModePreservingLocalPlayer()
    {
        SetThreeDMode(true);
        SpawnOrResetLocalPlayer(false);
        ApplyCursorState();
        OnThreeDModeEntered?.Invoke();

        Debug.Log("[G7-3D] 3D mode entered | preserveLocalPlayerTransform=True");
    }
    //* این تابع حالت سه بعدی را فعال می کند، منوها را مخفی می کند و پلیر لوکال را می سازد.
    public void EnterThreeDMode()
    {
        SetThreeDMode(true);
        SpawnOrResetLocalPlayer();
        ApplyCursorState();
        OnThreeDModeEntered?.Invoke();
        Debug.Log("[G7-3D] 3D mode entered");
    }

    //* این تابع حالت سه بعدی را خاموش می کند و منوها را دوباره نمایش می دهد.
    public void ExitThreeDMode()
    {
        SetThreeDMode(false);
        RestoreOriginalCameraState();
        ApplyCursorState();
        OnThreeDModeExited?.Invoke();
        Debug.Log("[G7-3D] 3D mode exited");
    }

    //* این تابع مطمئن می شود پلیر لوکال برای ارسال وضعیت شبکه ساخته شده است.
    //* این تابع فقط وجود پلیر لوکال را تضمین می کند و پلیر موجود را به نقطه اسپاون برنمی گرداند.
    public void EnsureLocalPlayerSpawned()
    {
        SpawnOrResetLocalPlayer(false);
    }

    //* این تابع وضعیت معتبر دریافتی از گیم سرور را روی پلیر لوکال اعمال می کند.
    public bool ApplyLocalPlayerAuthoritativeTransform(
        Vector3 position,
        Quaternion rotation)
    {
        EnsureLocalPlayerSpawned();

        if (localPlayerInstance == null)
        {
            Debug.LogError(
                "[G7-3D] Authoritative local player transform apply failed. Local player is missing."
            );

            return false;
        }

        ResetCharacterControllerPosition(
            localPlayerInstance,
            position,
            rotation
        );

        localPlayerInstance.SetActive(true);
        SetLocalPlayerControl(localPlayerInstance, true);
        EnsureLocalPlayerNameText();
        AttachThirdPersonCameraToLocalPlayer();

        Debug.Log(
            "[G7-3D] Authoritative local player transform applied | position=" +
            localPlayerInstance.transform.position +
            " | rotationY=" +
            localPlayerInstance.transform.rotation.eulerAngles.y.ToString("F1")
        );

        return true;
    }

    //* این تابع نام پلیر لوکال را یک بار از سیستم لاگین می گیرد و روی تکست بالای سر پلیر اعمال می کند.
    public void SetLocalPlayerDisplayName(string displayName)
    {
        localPlayerDisplayName = BuildSafePlayerName(displayName, fallbackLocalPlayerName);
        EnsureLocalPlayerNameText();
    }

    //* این تابع در خروج قطعی از روم یا گیم سرور، کلون های زمان اجرا را پاک می کند و ریشه دنیا را فقط غیرفعال می کند.
    public void CleanupRuntimeWorldAfterConfirmedExit(string reason)
    {
        string safeReason = string.IsNullOrWhiteSpace(reason) ? "confirmed_exit" : reason.Trim();

        DetachThirdPersonCameraBeforeRuntimeCleanup(safeReason);

        if (destroyLocalPlayerOnConfirmedExit) DestroyLocalPlayerInstanceForConfirmedExit(safeReason);
        if (clearRemotePlayersOnConfirmedExit) ClearRemotePlayers();
        if (destroyRuntimePlayerRootChildrenOnConfirmedExit) DestroyRuntimePlayerRootChildrenForConfirmedExit(safeReason);

        isThreeDModeActive = false;
        localPlayerNameText = null;

        if (disableWorldRootOnConfirmedExit && world3DRoot != null) world3DRoot.SetActive(false);

        if (restoreUiObjectsOnConfirmedExit)
        {
            SetObjectsActive(objectsToDisableWhen3DStarts, true);
            SetObjectsActive(objectsToEnableWhen3DStarts, false);
        }

        ApplyCursorState();
        if (invokeThreeDModeExitedOnConfirmedExit) OnThreeDModeExited?.Invoke();

        Debug.Log("[G7-3D] Runtime world cleaned after confirmed exit | reason=" + safeReason);
    }

    //* این تابع وضعیت فعال بودن ریشه های یو ای و دنیای سه بعدی را اعمال می کند.
    private void SetThreeDMode(bool active)
    {
        isThreeDModeActive = active;

        SetObjectsActive(objectsToDisableWhen3DStarts, !active);
        SetObjectsActive(objectsToEnableWhen3DStarts, active);

        if (world3DRoot != null) world3DRoot.SetActive(active);
        if (localPlayerInstance != null) localPlayerInstance.SetActive(active);
        SetThirdPersonCameraActive(active);

        SetRemotePlayersActive(active);
        Debug.Log($"[G7-3D] Mode state applied | active={active}");
    }

    //* این تابع یک آرایه آبجکت را با ایمنی روشن یا خاموش می کند.
    private void SetObjectsActive(GameObject[] objects, bool active)
    {
        if (objects == null) return;

        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] != null) objects[i].SetActive(active);
        }
    }

    //* این تابع پلیر لوکال را می سازد و فقط در مسیرهای مجاز، پلیر موجود را به نقطه اسپاون برمی گرداند.
    private void SpawnOrResetLocalPlayer(bool allowResetExistingPlayer = true)
    {
        if (localPlayerPrefab == null)
        {
            Debug.LogError("[G7-3D] Local player prefab is missing");
            return;
        }

        if (localPlayerSpawnPoint == null)
        {
            Debug.LogError("[G7-3D] Local player spawn point is missing");
            return;
        }

        if (localPlayerInstance == null)
        {
            Transform parent =
                playersRoot != null
                    ? playersRoot
                    : world3DRoot != null
                        ? world3DRoot.transform
                        : null;

            localPlayerInstance = Instantiate(
                localPlayerPrefab,
                localPlayerSpawnPoint.position,
                localPlayerSpawnPoint.rotation,
                parent
            );

            localPlayerInstance.name = "Local_Player_Cylinder";
            SetLocalPlayerControl(localPlayerInstance, true);
            EnsureLocalPlayerNameText();
            AttachThirdPersonCameraToLocalPlayer();

            Debug.Log(
                "[G7-3D] Local player created | position=" +
                localPlayerInstance.transform.position
            );

            return;
        }

        bool shouldResetExistingPlayer =
            allowResetExistingPlayer &&
            resetLocalPlayerOnEnter;

        if (shouldResetExistingPlayer)
        {
            ResetCharacterControllerPosition(
                localPlayerInstance,
                localPlayerSpawnPoint.position,
                localPlayerSpawnPoint.rotation
            );

            Debug.Log(
                "[G7-3D] Existing local player reset to spawn | position=" +
                localPlayerInstance.transform.position
            );
        }
        else
        {
            Debug.Log(
                "[G7-3D] Existing local player transform preserved | position=" +
                localPlayerInstance.transform.position
            );
        }

        localPlayerInstance.SetActive(true);
        SetLocalPlayerControl(localPlayerInstance, true);
        EnsureLocalPlayerNameText();
        AttachThirdPersonCameraToLocalPlayer();
    }

    //* این تابع دوربین سوم شخص را از اینسپکتور یا مین کمرا پیدا می کند.
    private void ResolveThirdPersonCamera()
    {
        if (thirdPersonCamera != null) return;
        thirdPersonCamera = Camera.main;
    }

    //* این تابع وضعیت اولیه دوربین را ذخیره می کند تا هنگام خروج از حالت سه بعدی قابل برگشت باشد.
    private void CaptureOriginalCameraState()
    {
        ResolveThirdPersonCamera();
        if (thirdPersonCamera == null || hasOriginalCameraState) return;

        Transform cameraTransform = thirdPersonCamera.transform;
        originalCameraParent = cameraTransform.parent;
        originalCameraPosition = cameraTransform.position;
        originalCameraRotation = cameraTransform.rotation;
        originalCameraActive = thirdPersonCamera.gameObject.activeSelf;
        hasOriginalCameraState = true;
    }

    //* این تابع دوربین را بعد از ساخته شدن پلیر لوکال، با فاصله مشخص فرزند پلیر می کند.
    private void AttachThirdPersonCameraToLocalPlayer()
    {
        if (!isThreeDModeActive) return;
        if (localPlayerInstance == null) return;

        ResolveThirdPersonCamera();
        if (thirdPersonCamera == null)
        {
            Debug.LogWarning("[G7-3D] Third person camera is missing");
            return;
        }

        if (!hasOriginalCameraState) CaptureOriginalCameraState();

        Transform cameraTransform = thirdPersonCamera.transform;
        if (parentCameraToLocalPlayer) cameraTransform.SetParent(localPlayerInstance.transform, false);

        cameraTransform.localPosition = cameraLocalPosition;
        cameraTransform.localRotation = Quaternion.Euler(cameraLocalEulerAngles);
        if (activateCameraIn3DMode) thirdPersonCamera.gameObject.SetActive(true);
    }

    //* این تابع هنگام خروج از حالت سه بعدی دوربین را به وضعیت قبلی برمی گرداند.
    private void RestoreOriginalCameraState()
    {
        if (thirdPersonCamera == null || !hasOriginalCameraState) return;

        Transform cameraTransform = thirdPersonCamera.transform;
        cameraTransform.SetParent(originalCameraParent, true);
        cameraTransform.SetPositionAndRotation(originalCameraPosition, originalCameraRotation);
        thirdPersonCamera.gameObject.SetActive(originalCameraActive);
    }

    //* این تابع روشن یا خاموش بودن دوربین را بر اساس حالت سه بعدی کنترل می کند.
    private void SetThirdPersonCameraActive(bool active)
    {
        ResolveThirdPersonCamera();
        if (thirdPersonCamera == null || !activateCameraIn3DMode) return;
        thirdPersonCamera.gameObject.SetActive(active || originalCameraActive);
    }

    //* این تابع پلیر دارای کاراکتر کنترلر را بدون خطای برخورد به مکان جدید منتقل می کند.
    private void ResetCharacterControllerPosition(GameObject playerObject, Vector3 position, Quaternion rotation)
    {
        if (playerObject == null) return;

        CharacterController characterController = playerObject.GetComponent<CharacterController>();
        if (characterController != null) characterController.enabled = false;

        playerObject.transform.SetPositionAndRotation(position, rotation);

        if (characterController != null) characterController.enabled = true;
    }

    //* این تابع کنترل حرکت را فقط برای پلیر لوکال فعال و برای کلون ها غیرفعال می کند.
    private void SetLocalPlayerControl(GameObject playerObject, bool active)
    {
        if (playerObject == null) return;

        G7SimpleCylinderCharacterController movement = playerObject.GetComponent<G7SimpleCylinderCharacterController>();
        if (movement != null) movement.enabled = active;
    }

    //* این تابع ترنسفورم پلیر لوکال را برای ارسال وضعیت ریل تایم برمی گرداند.
    public Transform GetLocalPlayerTransform()
    {
        return localPlayerInstance != null ? localPlayerInstance.transform : null;
    }

    //* این تابع یک کلون ریموت را می سازد یا وضعیت هدف آن را برای حرکت نرم آپدیت می کند.
    //* اگر پلیر به علت قطع جریان استیت موقتاً غیرفعال باشد، آپدیت قدیمی اجازه فعال کردن دوباره آن را ندارد.
    public void SpawnOrUpdateRemotePlayer(string userId, string userName, Vector3 position, Quaternion rotation)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;

        string safeUserId = userId.Trim();
        bool shouldBeActive =
            isThreeDModeActive &&
            !set_TemporarilyInactiveRemoteUserIds.Contains(safeUserId);

        if (dict_RemotePlayersByUserId.TryGetValue(safeUserId, out GameObject existingPlayer) && existingPlayer != null)
        {
            existingPlayer.SetActive(shouldBeActive);
            ApplyRemotePlayerNameIfBetter(existingPlayer, safeUserId, userName);
            ApplyRemotePlayerTarget(existingPlayer, position, rotation, false);
            return;
        }

        GameObject prefab = remotePlayerPrefab != null ? remotePlayerPrefab : localPlayerPrefab;
        if (prefab == null)
        {
            Debug.LogError("[G7-3D] Remote player prefab is missing");
            return;
        }

        Transform parent = playersRoot != null ? playersRoot : world3DRoot != null ? world3DRoot.transform : null;
        GameObject remotePlayer = Instantiate(prefab, position, rotation, parent);
        string safeUserName = BuildSafePlayerName(userName, safeUserId);
        remotePlayer.name = "Remote_Player_" + SanitizeObjectName(safeUserName);
        remotePlayer.SetActive(shouldBeActive);
        SetLocalPlayerControl(remotePlayer, false);
        TMP_Text nameText = EnsurePlayerNameText(remotePlayer, safeUserName, false);
        ApplyRemotePlayerTarget(remotePlayer, position, rotation, true);

        dict_RemotePlayersByUserId[safeUserId] = remotePlayer;
        dict_RemoteNameTextsByUserId[safeUserId] = nameText;
        Debug.Log($"[G7-3D] Remote player spawned | userId={safeUserId} | userName={userName}");
    }

    //* این تابع روی کلون ریموت، اسکریپت حرکت نرم را پیدا یا اضافه می کند و هدف جدید را تنظیم می کند.
    private void ApplyRemotePlayerTarget(GameObject remotePlayer, Vector3 position, Quaternion rotation, bool initialize)
    {
        if (remotePlayer == null) return;

        G7RemotePlayerView remoteView = remotePlayer.GetComponent<G7RemotePlayerView>();
        if (remoteView == null) remoteView = remotePlayer.AddComponent<G7RemotePlayerView>();

        if (initialize) remoteView.Initialize(position, rotation);
        else remoteView.SetTargetState(position, rotation);
    }

    //* این تابع یک کلون ریموت را هنگام خروج یا قطع شدن همان یوزر حذف می کند.
    public void RemoveRemotePlayer(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;

        string safeUserId = userId.Trim();
        set_TemporarilyInactiveRemoteUserIds.Remove(safeUserId);

        if (!dict_RemotePlayersByUserId.TryGetValue(safeUserId, out GameObject remotePlayer)) return;

        dict_RemotePlayersByUserId.Remove(safeUserId);
        dict_RemoteNameTextsByUserId.Remove(safeUserId);
        if (remotePlayer != null) Destroy(remotePlayer);

        Debug.Log($"[G7-3D] Remote player removed | userId={safeUserId}");
    }

    //* این تابع همه کلون های ریموت را هنگام خروج از روم یا دیسکانکت پاک می کند.
    public void ClearRemotePlayers()
    {
        foreach (KeyValuePair<string, GameObject> pair in dict_RemotePlayersByUserId)
        {
            if (pair.Value != null) Destroy(pair.Value);
        }

        dict_RemotePlayersByUserId.Clear();
        dict_RemoteNameTextsByUserId.Clear();
        set_TemporarilyInactiveRemoteUserIds.Clear();
    }

    //* این تابع همه کلون های ریموت را با وضعیت حالت سه بعدی روشن یا خاموش می کند.
    //* این تابع کلون یک پلیر ریموت را بدون حذف آبجکت و دیتای آن موقتاً فعال یا غیرفعال می کند.
    public bool SetRemotePlayerActive(string userId, bool active)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;

        string safeUserId = userId.Trim();
        if (!dict_RemotePlayersByUserId.TryGetValue(safeUserId, out GameObject remotePlayer) || remotePlayer == null)
        {
            return false;
        }

        if (active)
        {
            set_TemporarilyInactiveRemoteUserIds.Remove(safeUserId);
        }
        else
        {
            set_TemporarilyInactiveRemoteUserIds.Add(safeUserId);
        }

        bool targetActive = active && isThreeDModeActive;
        if (remotePlayer.activeSelf == targetActive) return false;

        remotePlayer.SetActive(targetActive);

        Debug.Log(
            "[G7-3D] Remote player active state changed | userId=" +
            safeUserId +
            " | active=" +
            targetActive
        );

        return true;
    }

    //* این تابع اجازه نمی دهد هیچ مسیر قدیمی یا موازی پلیر موقتاً غیرفعال را دوباره روشن کند.
    private void EnforceTemporarilyInactiveRemotePlayers()
    {
        if (set_TemporarilyInactiveRemoteUserIds.Count <= 0) return;

        foreach (string userId in set_TemporarilyInactiveRemoteUserIds)
        {
            if (string.IsNullOrWhiteSpace(userId)) continue;

            if (!dict_RemotePlayersByUserId.TryGetValue(userId, out GameObject remotePlayer) ||
                remotePlayer == null)
            {
                continue;
            }

            if (remotePlayer.activeSelf)
            {
                remotePlayer.SetActive(false);
            }
        }
    }

    //* این تابع همه کلون های ریموت را با وضعیت حالت سه بعدی و غیرفعال سازی موقت همسان می کند.
    private void SetRemotePlayersActive(bool active)
    {
        foreach (KeyValuePair<string, GameObject> pair in dict_RemotePlayersByUserId)
        {
            if (pair.Value == null) continue;

            bool shouldBeActive =
                active &&
                !set_TemporarilyInactiveRemoteUserIds.Contains(pair.Key);

            pair.Value.SetActive(shouldBeActive);
        }
    }


    //* این تابع تکست نام پلیر لوکال را می سازد یا مقدار آن را به روز می کند.
    private void EnsureLocalPlayerNameText()
    {
        if (!showPlayerNameTexts || localPlayerInstance == null) return;

        string safeName = BuildSafePlayerName(localPlayerDisplayName, fallbackLocalPlayerName);
        localPlayerNameText = EnsurePlayerNameText(localPlayerInstance, safeName, true);
    }

    //* این تابع برای هر پلیر یک تکست مش پرو سه بعدی بالای سر می سازد یا نمونه موجود را تنظیم می کند.
    private TMP_Text EnsurePlayerNameText(GameObject playerObject, string displayName, bool isLocalPlayer)
    {
        if (!showPlayerNameTexts || playerObject == null) return null;

        string safeName = BuildSafePlayerName(displayName, isLocalPlayer ? fallbackLocalPlayerName : "Player");
        Transform existingNameTransform = playerObject.transform.Find(playerNameObjectName);
        GameObject nameObject = existingNameTransform != null ? existingNameTransform.gameObject : CreatePlayerNameTextObject(playerObject.transform);
        if (nameObject == null) return null;

        nameObject.name = playerNameObjectName;
        nameObject.SetActive(true);
        nameObject.transform.localPosition = playerNameLocalPosition;
        nameObject.transform.localRotation = Quaternion.identity;
        nameObject.transform.localScale = Vector3.one * Mathf.Max(0.001f, playerNameLocalScale);

        TMP_Text nameText = nameObject.GetComponent<TMP_Text>();
        if (nameText == null) nameText = nameObject.GetComponentInChildren<TMP_Text>(true);
        if (nameText == null)
        {
            TextMeshPro createdText = nameObject.AddComponent<TextMeshPro>();
            nameText = createdText;
        }

        nameText.gameObject.SetActive(true);
        nameText.text = safeName;
        nameText.fontSize = Mathf.Max(0.1f, playerNameFontSize);
        nameText.alignment = TextAlignmentOptions.Center;
        nameText.enableWordWrapping = false;
        nameText.overflowMode = TextOverflowModes.Overflow;
        nameText.color = isLocalPlayer ? localPlayerNameColor : remotePlayerNameColor;
        if (nameText.rectTransform != null) nameText.rectTransform.sizeDelta = playerNameRectSize;

        return nameText;
    }

    //* این تابع آبجکت تکست آماده اینسپکتور را زیر پلیر کپی می کند و اگر وصل نشده باشد یک تکست ساده می سازد.
    private GameObject CreatePlayerNameTextObject(Transform playerTransform)
    {
        if (playerTransform == null) return null;

        if (playerNameTextPrefab != null)
        {
            GameObject nameObject = Instantiate(playerNameTextPrefab, playerTransform);
            return nameObject;
        }

        GameObject fallbackObject = new GameObject(playerNameObjectName);
        fallbackObject.transform.SetParent(playerTransform, false);
        fallbackObject.AddComponent<TextMeshPro>();
        return fallbackObject;
    }

    //* این تابع اگر کلون قبلاً با آی دی ساخته شده باشد و بعداً نام واقعی برسد، فقط همان یک بار نام را بهتر می کند.
    private void ApplyRemotePlayerNameIfBetter(GameObject remotePlayer, string userId, string userName)
    {
        if (!showPlayerNameTexts || remotePlayer == null) return;
        if (string.IsNullOrWhiteSpace(userName)) return;

        string safeName = BuildSafePlayerName(userName, userId);
        TMP_Text nameText = null;

        if (!string.IsNullOrWhiteSpace(userId)) dict_RemoteNameTextsByUserId.TryGetValue(userId, out nameText);
        if (nameText == null) nameText = remotePlayer.GetComponentInChildren<TMP_Text>(true);
        if (nameText == null)
        {
            nameText = EnsurePlayerNameText(remotePlayer, safeName, false);
            if (!string.IsNullOrWhiteSpace(userId)) dict_RemoteNameTextsByUserId[userId] = nameText;
            return;
        }

        string currentText = nameText.text ?? string.Empty;
        bool shouldApplyName = string.IsNullOrWhiteSpace(currentText) ||
                               string.Equals(currentText.Trim(), userId?.Trim(), StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(currentText.Trim(), "Player", StringComparison.OrdinalIgnoreCase);

        if (!shouldApplyName) return;

        nameText.text = safeName;
        remotePlayer.name = "Remote_Player_" + SanitizeObjectName(safeName);
    }

    //* این تابع همه تکست های نام را به سمت دوربین فعال می چرخاند.
    private void RotateNameTextsToCamera()
    {
        if (!rotateNameTextsToCamera || !showPlayerNameTexts) return;

        ResolveThirdPersonCamera();
        if (thirdPersonCamera == null) return;

        if (localPlayerNameText != null) RotateNameTextToCamera(localPlayerNameText);

        foreach (KeyValuePair<string, TMP_Text> pair in dict_RemoteNameTextsByUserId)
        {
            if (pair.Value != null) RotateNameTextToCamera(pair.Value);
        }
    }

    //* این تابع یک تکست نام را به صورت بیلبوردی به سمت دوربین می چرخاند.
    private void RotateNameTextToCamera(TMP_Text nameText)
    {
        if (nameText == null || thirdPersonCamera == null) return;

        Transform billboardTransform = ResolveNameBillboardTransform(nameText);
        if (billboardTransform == null) return;

        Vector3 directionToCamera = thirdPersonCamera.transform.position - billboardTransform.position;
        if (directionToCamera.sqrMagnitude <= 0.0001f) return;

        billboardTransform.rotation = Quaternion.LookRotation(directionToCamera.normalized, Vector3.up);
        if (flipNameTextAfterLookAt) billboardTransform.Rotate(0f, 180f, 0f, Space.Self);
    }

    //* این تابع اگر تکست داخل یک روت آماده باشد، همان روت را برای چرخش انتخاب می کند.
    private Transform ResolveNameBillboardTransform(TMP_Text nameText)
    {
        if (nameText == null) return null;
        if (!rotateNameTextRootObject) return nameText.transform;

        Transform current = nameText.transform;

        while (current != null)
        {
            if (string.Equals(current.name, playerNameObjectName, StringComparison.OrdinalIgnoreCase)) return current;
            current = current.parent;
        }

        return nameText.transform;
    }

    //* این تابع نام امن برای نمایش بالای سر پلیر می سازد.
    private string BuildSafePlayerName(string value, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        if (!string.IsNullOrWhiteSpace(fallback)) return fallback.Trim();
        return "Player";
    }

    //* این تابع نام آبجکت یونیتی را از کاراکترهای مشکل ساز پاک می کند.
    private string SanitizeObjectName(string value)
    {
        string safeValue = BuildSafePlayerName(value, "Player");
        return safeValue.Replace("/", "_").Replace("\\", "_").Replace(":", "_").Replace("*", "_").Replace("?", "_").Replace("\"", "_").Replace("<", "_").Replace(">", "_").Replace("|", "_");
    }

    //* این تابع قبل از حذف پلیر لوکال، دوربین را از زیر کلون ها بیرون می آورد.
    private void DetachThirdPersonCameraBeforeRuntimeCleanup(string reason)
    {
        ResolveThirdPersonCamera();
        if (thirdPersonCamera == null) return;

        Transform cameraTransform = thirdPersonCamera.transform;
        if (cameraTransform == null || cameraTransform.parent == null) return;
        if (!IsTransformInsideRuntimePlayers(cameraTransform)) return;

        if (hasOriginalCameraState)
        {
            RestoreOriginalCameraState();
            Debug.Log("[G7-3D] Camera restored before runtime cleanup | reason=" + reason);
            return;
        }

        Vector3 worldPosition = cameraTransform.position;
        Quaternion worldRotation = cameraTransform.rotation;
        cameraTransform.SetParent(null, true);
        cameraTransform.SetPositionAndRotation(worldPosition, worldRotation);
        thirdPersonCamera.gameObject.SetActive(true);
        Debug.Log("[G7-3D] Camera detached before runtime cleanup | reason=" + reason);
    }

    //* این تابع بررسی می کند ترنسفورم داخل پلیرهای زمان اجرا قرار دارد یا نه.
    private bool IsTransformInsideRuntimePlayers(Transform target)
    {
        if (target == null) return false;
        if (localPlayerInstance != null && target.IsChildOf(localPlayerInstance.transform)) return true;
        if (playersRoot != null && target.IsChildOf(playersRoot)) return true;

        foreach (KeyValuePair<string, GameObject> pair in dict_RemotePlayersByUserId)
        {
            if (pair.Value != null && target.IsChildOf(pair.Value.transform)) return true;
        }

        return false;
    }

    //* این تابع کلون پلیر لوکال را حذف می کند و رفرنس داخلی را پاک می کند.
    private void DestroyLocalPlayerInstanceForConfirmedExit(string reason)
    {
        if (localPlayerInstance == null) return;

        GameObject target = localPlayerInstance;
        localPlayerInstance = null;
        localPlayerNameText = null;

        Destroy(target);
        Debug.Log("[G7-3D] Local player clone destroyed after confirmed exit | reason=" + reason);
    }

    //* این تابع بچه های ریشه پلیرها را پاک می کند، ولی خود ریشه پلیرها و ریشه دنیا را حذف نمی کند.
    private void DestroyRuntimePlayerRootChildrenForConfirmedExit(string reason)
    {
        if (playersRoot == null) return;

        bool playersRootIsWorldRoot = world3DRoot != null && playersRoot == world3DRoot.transform;
        int destroyedCount = 0;
        int skippedCount = 0;

        for (int i = playersRoot.childCount - 1; i >= 0; i--)
        {
            Transform child = playersRoot.GetChild(i);
            if (child == null) continue;

            if (!ShouldDestroyRuntimePlayerChild(child, playersRootIsWorldRoot))
            {
                skippedCount++;
                continue;
            }

            Destroy(child.gameObject);
            destroyedCount++;
        }

        if (destroyedCount > 0 || skippedCount > 0)
        {
            Debug.Log("[G7-3D] Runtime player root children cleanup | destroyed=" + destroyedCount + " | skipped=" + skippedCount + " | reason=" + reason);
        }
    }

    //* این تابع تعیین می کند کدام بچه های ریشه پلیرها، کلون زمان اجرا هستند.
    private bool ShouldDestroyRuntimePlayerChild(Transform child, bool playersRootIsWorldRoot)
    {
        if (child == null) return false;
        if (world3DRoot != null && child == world3DRoot.transform) return false;
        if (thirdPersonCamera != null && child == thirdPersonCamera.transform) return false;

        if (!playersRootIsWorldRoot && destroyAllChildrenUnderPlayersRootOnCleanup) return true;

        string childName = child.name ?? string.Empty;
        if (childName.StartsWith("Local_Player", StringComparison.OrdinalIgnoreCase)) return true;
        if (childName.StartsWith("Remote_Player", StringComparison.OrdinalIgnoreCase)) return true;
        if (child.GetComponent<G7RemotePlayerView>() != null) return true;
        if (child.GetComponent<G7SimpleCylinderCharacterController>() != null) return true;
        return false;
    }

    //* این تابع وضعیت نشانگر موس را بر اساس حالت سه بعدی اعمال می کند.
    private void ApplyCursorState()
    {
        if (!isThreeDModeActive)
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            return;
        }

        Cursor.visible = !hideCursorIn3DMode;
        Cursor.lockState = hideCursorIn3DMode ? CursorLockMode.Locked : CursorLockMode.None;
    }
}
