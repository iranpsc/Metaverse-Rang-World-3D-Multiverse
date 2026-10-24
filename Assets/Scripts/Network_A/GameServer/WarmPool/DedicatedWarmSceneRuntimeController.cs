using System;
using System.Collections.Generic;
using Network_A.GameServer.Players;
using UnityEngine;

[DefaultExecutionOrder(-8500)]
public class DedicatedWarmSceneRuntimeController : MonoBehaviour
{
    [Header("Warm Mode References")]
    [SerializeField] private DedicatedPlayerRegistry playerRegistry;

    [Tooltip("Only the gameplay/world root must be assigned here. Do not assign Dedicated_Server_Core.")]
    [SerializeField] private GameObject dedicatedWorldRoot;

    [Tooltip("Runtime cloned prefabs will be parented here. If empty, this controller transform will be used.")]
    [SerializeField] private Transform runtimeSpawnRoot;

    [Header("Warm Mode")]
    [SerializeField] private bool startAsWarmWhenNoActiveRoom = true;
    [SerializeField] private bool deactivateWorldRootOnWarmStart = true;
    [SerializeField] private bool activateWorldRootOnFirstPlayer = true;
    [SerializeField] private bool keepWorldActiveAfterActivation = true;

    [Header("Runtime Prefab Spawn List")]
    [SerializeField] private bool cloneRuntimePrefabsOnRoomActivated = false;
    [SerializeField] private List<GameObject> list_RuntimePrefab = new List<GameObject>();
    [SerializeField] private bool skipNullPrefabs = true;
    [SerializeField] private bool cloneOnlyOnce = true;

    [Header("Cleanup")]
    [SerializeField] private bool destroySpawnedRuntimeObjectsOnDestroy = true;

    [Header("Debug")]
    [SerializeField] private bool logMessages = true;

    private readonly List<GameObject> list_SpawnedRuntimeObject = new List<GameObject>();
    private bool warmGateApplied;
    private bool roomActivated;
    private bool runtimePrefabsCloned;
    private string activeRoomId = string.Empty;

    public bool IsRoomActivated => roomActivated;
    public string ActiveRoomId => activeRoomId;
    public int SpawnedRuntimeObjectCount => list_SpawnedRuntimeObject.Count;

    //* این تابع رفرنس های لازم را قبل از شروع سین پیدا می کند.
    private void Awake()
    {
        EnsureReferences();
    }

    //* این تابع رویداد ورود پلیر را زودتر از اسپان آبجکت های پلیر گوش می دهد.
    private void OnEnable()
    {
        EnsureReferences();

        if (playerRegistry != null)
        {
            playerRegistry.PlayerRegistered += HandlePlayerRegistered;
        }
    }

    //* این تابع رویداد ورود پلیر را هنگام غیرفعال شدن کامپوننت پاک می کند.
    private void OnDisable()
    {
        if (playerRegistry != null)
        {
            playerRegistry.PlayerRegistered -= HandlePlayerRegistered;
        }
    }

    //* این تابع در شروع سین، اگر هنوز روم فعال نداریم، ورلد را در حالت گرم سبک نگه می دارد.
    private void Start()
    {
        ApplyWarmGateIfNeeded();
    }

    //* این تابع هنگام نابودی کامپوننت آبجکت های کلون شده را در صورت نیاز پاک می کند.
    private void OnDestroy()
    {
        if (!destroySpawnedRuntimeObjectsOnDestroy) return;

        DestroySpawnedRuntimeObjects();
    }

    //* این تابع از اینسپکتور برای اعمال دستی حالت گرم استفاده می شود.
    [ContextMenu("Apply Warm Gate")]
    public void ApplyWarmGateIfNeeded()
    {
        EnsureReferences();

        if (!startAsWarmWhenNoActiveRoom)
        {
            Log("Warm gate skipped because startAsWarmWhenNoActiveRoom is disabled.");
            return;
        }

        if (playerRegistry != null)
        {
            string registryRoomId = SafeTrim(playerRegistry.GetPrimaryRoomId());

            if (!string.IsNullOrWhiteSpace(registryRoomId) || playerRegistry.CurrentPlayerCount > 0)
            {
                activeRoomId = registryRoomId;
                roomActivated = true;
                Log("Warm gate skipped because registry already has an active room/player | roomId=" + activeRoomId);
                return;
            }
        }

        warmGateApplied = true;

        if (deactivateWorldRootOnWarmStart)
        {
            SetWorldRootActive(false, "warm_gate_applied");
        }

        Log("Warm gate applied | worldActive=" + BoolText(IsWorldRootActive()) +
            " | runtimeSpawnRoot=" + SafeObjectName(runtimeSpawnRoot == null ? null : runtimeSpawnRoot.gameObject));
    }

    //* این تابع از اینسپکتور برای فعال کردن دستی روم استفاده می شود.
    [ContextMenu("Activate Room Manually")]
    public void ActivateRoomManually()
    {
        ActivateRoom("manual_activation");
    }

    //* این تابع ورلد و پریفب های ران تایم را برای روم واقعی فعال می کند.
    public void ActivateRoom(string roomId)
    {
        EnsureReferences();

        string safeRoomId = SafeTrim(roomId);
        if (string.IsNullOrWhiteSpace(safeRoomId)) safeRoomId = "unknown_room";

        if (roomActivated && cloneOnlyOnce)
        {
            Log("Room activation skipped because room is already activated | activeRoomId=" + activeRoomId);
            return;
        }

        activeRoomId = safeRoomId;
        roomActivated = true;

        if (activateWorldRootOnFirstPlayer)
        {
            SetWorldRootActive(true, "room_activated");
        }

        if (cloneRuntimePrefabsOnRoomActivated)
        {
            CloneRuntimePrefabs();
        }

        Log("Room activated | roomId=" + activeRoomId +
            " | worldActive=" + BoolText(IsWorldRootActive()) +
            " | cloned=" + runtimePrefabsCloned +
            " | spawnedRuntimeObjects=" + list_SpawnedRuntimeObject.Count);
    }

    //* این تابع همه آبجکت های ران تایم کلون شده را پاک می کند.
    [ContextMenu("Destroy Spawned Runtime Objects")]
    public void DestroySpawnedRuntimeObjects()
    {
        for (int i = list_SpawnedRuntimeObject.Count - 1; i >= 0; i--)
        {
            GameObject spawned = list_SpawnedRuntimeObject[i];
            if (spawned == null) continue;

            Destroy(spawned);
        }

        list_SpawnedRuntimeObject.Clear();
        runtimePrefabsCloned = false;

        Log("Spawned runtime objects destroyed.");
    }

    //* این تابع رفرنس ها را از همین آبجکت، والد، فرزند یا سین پیدا می کند.
    private void EnsureReferences()
    {
        if (playerRegistry == null)
        {
            playerRegistry = GetComponent<DedicatedPlayerRegistry>();
            if (playerRegistry == null) playerRegistry = GetComponentInParent<DedicatedPlayerRegistry>();
            if (playerRegistry == null) playerRegistry = GetComponentInChildren<DedicatedPlayerRegistry>(true);
            if (playerRegistry == null) playerRegistry = FindObjectOfType<DedicatedPlayerRegistry>();
        }

        if (runtimeSpawnRoot == null)
        {
            runtimeSpawnRoot = transform;
        }
    }

    //* این تابع بعد از اولین رجیستر شدن پلیر، ورلد را قبل از ادامه مسیر بازی فعال می کند.
    private void HandlePlayerRegistered(DedicatedPlayerSession session)
    {
        string roomId = session == null ? string.Empty : SafeTrim(session.roomId);
        ActivateRoom(roomId);
    }

    //* این تابع همه پریفب های لیست ران تایم را داخل Runtime Root کلون می کند.
    private void CloneRuntimePrefabs()
    {
        if (runtimePrefabsCloned && cloneOnlyOnce)
        {
            Log("Runtime prefab cloning skipped because it was already done.");
            return;
        }

        EnsureReferences();

        if (runtimeSpawnRoot == null)
        {
            Debug.LogWarning("[DedicatedWarmSceneRuntimeController] Runtime spawn root is missing.");
            return;
        }

        int clonedCount = 0;

        for (int i = 0; i < list_RuntimePrefab.Count; i++)
        {
            GameObject prefab = list_RuntimePrefab[i];

            if (prefab == null)
            {
                if (!skipNullPrefabs)
                {
                    Debug.LogWarning("[DedicatedWarmSceneRuntimeController] Runtime prefab is null | index=" + i);
                }

                continue;
            }

            GameObject clone = Instantiate(prefab, runtimeSpawnRoot);
            clone.name = prefab.name + "_RuntimeClone";
            clone.SetActive(true);

            list_SpawnedRuntimeObject.Add(clone);
            clonedCount++;
        }

        runtimePrefabsCloned = true;

        Log("Runtime prefabs cloned | count=" + clonedCount +
            " | totalSpawned=" + list_SpawnedRuntimeObject.Count);
    }

    //* این تابع ورلد روت را فعال یا غیرفعال می کند.
    private void SetWorldRootActive(bool active, string reason)
    {
        if (dedicatedWorldRoot == null)
        {
            Debug.LogWarning("[DedicatedWarmSceneRuntimeController] Dedicated world root is not assigned | reason=" + reason);
            return;
        }

        if (!keepWorldActiveAfterActivation && active == false && roomActivated)
        {
            Log("World root deactivate skipped because room is active.");
            return;
        }

        if (dedicatedWorldRoot.activeSelf == active)
        {
            Log("World root already has requested state | active=" + BoolText(active) + " | reason=" + reason);
            return;
        }

        dedicatedWorldRoot.SetActive(active);

        Log("World root state changed | active=" + BoolText(active) +
            " | reason=" + reason +
            " | root=" + dedicatedWorldRoot.name);
    }

    //* این تابع وضعیت فعال بودن ورلد روت را برمی گرداند.
    private bool IsWorldRootActive()
    {
        return dedicatedWorldRoot != null && dedicatedWorldRoot.activeSelf;
    }

    //* این تابع مقدار رشته ای را امن و بدون فاصله اضافه می کند.
    private string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    //* این تابع نام آبجکت را امن برمی گرداند.
    private string SafeObjectName(GameObject target)
    {
        return target == null ? "NULL" : target.name;
    }

    //* این تابع مقدار بولی را برای لاگ قابل خواندن می کند.
    private string BoolText(bool value)
    {
        return value ? "YES" : "NO";
    }

    //* این تابع پیام دیباگ را در صورت فعال بودن لاگ چاپ می کند.
    private void Log(string message)
    {
        if (!logMessages) return;

        Debug.Log("[DedicatedWarmSceneRuntimeController] " + message);
    }
}

// این فایل فقط گیت سبک سازی Warm Mode را برای صحنه ددیکیتد مدیریت می کند؛ کُر سرور، وب سوکت، هارت بیت و آث نباید داخل world root خاموش شوند.
