using System;
using System.Reflection;
using Network_A.GameServer.Gameplay;
using Network_A.GameServer.Players;
using Network_A.GameServer.WebSocket;
using UnityEngine;

namespace Network_A.GameServer.Tools
{
    public class DedicatedServerGameplayPipelineAutoBinder : MonoBehaviour
    {
        [Header("Auto Fix")]
        [SerializeField] private bool autoAddMissingComponents = true;
        [SerializeField] private bool runOnAwake = true;
        [SerializeField] private bool runOnStart = true;
        [SerializeField] private bool allowRepeatedAutoFix = false;

        [Header("Phase 33A Mirror-Like API")]
        [SerializeField] private MetaverseDedicatedServerRuntimeConfig runtimeConfig;
        [SerializeField] private bool autoInstallMetaverseSpawnPipeline = true;
        [SerializeField] private bool autoBindMirrorLikeSmokeController = true;
        [SerializeField] private bool bindMetaverseBridgesToRouter = true;

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;

        private DedicatedWebSocketServer webSocketServer;
        private DedicatedPlayerRegistry playerRegistry;
        private DedicatedPlayerStateStore playerStateStore;
        private DedicatedGameMessageRouter gameMessageRouter;
        private MetaverseSpawnManager spawnManager;
        private MetaverseSpawnNetworkBridge spawnNetworkBridge;
        private MetaverseNetworkRpcBridge rpcNetworkBridge;
        private MetaverseNetworkStateSyncBridge stateSyncBridge;
        private MetaverseNetworkOwnershipBridge ownershipBridge;
        private MetaverseNetworkPlayerMovementBridge playerMovementBridge;
        private MetaverseNetworkRpcSmokeController rpcSmokeController;
        private bool autoFixApplied;

        //* این تابع در اویک مسیر پیام های گیم ددیکیتد سرور را آماده می کند.
        private void Awake()
        {
            if (!runOnAwake) return;

            FixGameplayPipeline();
        }

        //* این تابع در استارت یک بار دیگر مسیر پیام های گیم را بررسی می کند.
        private void Start()
        {
            if (!runOnStart) return;

            FixGameplayPipeline();
        }

        //* این تابع از اینسپکتور برای اجرای دستی بررسی و اتصال مسیر گیم استفاده می شود.
        [ContextMenu("Fix Dedicated Gameplay Pipeline")]
        public void FixGameplayPipeline()
        {
            if (autoFixApplied && !allowRepeatedAutoFix)
            {
                Log("Dedicated gameplay pipeline auto fix skipped. It was already applied.");
                return;
            }

            EnsureRuntimeConfig();
            FindOrCreateComponents();
            InstallMetaversePipelineIfNeeded();
            FindMetaverseComponents();
            BindMetaverseBridges();
            BindReferences();
            BindMirrorLikeSmokeController();
            PrintStatus();
            autoFixApplied = true;
        }

        //* این تابع کانفیگ ران تایم متاورس را از اینسپکتور یا ریسورس پیدا می کند.
        private void EnsureRuntimeConfig()
        {
            if (runtimeConfig != null) return;

            runtimeConfig = MetaverseDedicatedServerRuntimeConfig.LoadDefault();
            if (runtimeConfig != null) Log("Runtime config loaded from Resources.");
        }

        //* این تابع کامپوننت های لازم مسیر player_state را پیدا می کند یا در صورت نیاز اضافه می کند.
        private void FindOrCreateComponents()
        {
            webSocketServer = GetComponent<DedicatedWebSocketServer>();
            playerRegistry = GetComponent<DedicatedPlayerRegistry>();
            playerStateStore = GetComponent<DedicatedPlayerStateStore>();
            gameMessageRouter = GetComponent<DedicatedGameMessageRouter>();

            if (!autoAddMissingComponents) return;

            if (webSocketServer == null)
            {
                webSocketServer = gameObject.AddComponent<DedicatedWebSocketServer>();
                Log("Added missing DedicatedWebSocketServer.");
            }

            if (playerRegistry == null)
            {
                playerRegistry = gameObject.AddComponent<DedicatedPlayerRegistry>();
                Log("Added missing DedicatedPlayerRegistry.");
            }

            if (playerStateStore == null)
            {
                playerStateStore = gameObject.AddComponent<DedicatedPlayerStateStore>();
                Log("Added missing DedicatedPlayerStateStore.");
            }

            if (gameMessageRouter == null)
            {
                gameMessageRouter = gameObject.AddComponent<DedicatedGameMessageRouter>();
                Log("Added missing DedicatedGameMessageRouter.");
            }
        }

        //* این تابع سیستم اسپاون و بریج های متاورس را از کانفیگ ران تایم نصب می کند.
        private void InstallMetaversePipelineIfNeeded()
        {
            if (!autoInstallMetaverseSpawnPipeline) return;
            if (runtimeConfig == null) return;
            if (!runtimeConfig.AutoInstallSpawnSystem) return;

            spawnManager = MetaverseSpawnSystemInstaller.Install(runtimeConfig);
            if (spawnManager != null) Log("Metaverse spawn pipeline installed from runtime config.");
        }

        //* این تابع بریج ها و کنترلر تست شبیه میرور را در صحنه پیدا می کند.
        private void FindMetaverseComponents()
        {
            if (spawnManager == null) spawnManager = FindComponent<MetaverseSpawnManager>();
            if (spawnNetworkBridge == null) spawnNetworkBridge = FindComponent<MetaverseSpawnNetworkBridge>();
            if (rpcNetworkBridge == null) rpcNetworkBridge = FindComponent<MetaverseNetworkRpcBridge>();
            if (stateSyncBridge == null) stateSyncBridge = FindComponent<MetaverseNetworkStateSyncBridge>();
            if (ownershipBridge == null) ownershipBridge = FindComponent<MetaverseNetworkOwnershipBridge>();
            if (playerMovementBridge == null) playerMovementBridge = FindComponent<MetaverseNetworkPlayerMovementBridge>();
            if (rpcSmokeController == null) rpcSmokeController = FindComponent<MetaverseNetworkRpcSmokeController>();
        }

        //* این تابع بریج های متاورس را به اسپاون منیجر وصل می کند.
        private void BindMetaverseBridges()
        {
            if (spawnManager == null) return;

            spawnNetworkBridge?.Bind(spawnManager);
            rpcNetworkBridge?.Bind(spawnManager);
            stateSyncBridge?.Bind(spawnManager);
            ownershipBridge?.Bind(spawnManager);
            playerMovementBridge?.Bind(spawnManager, runtimeConfig);
        }

        //* این تابع رفرنس های خصوصی رُتر گیم را با رفلکشن پر می کند.
        private void BindReferences()
        {
            if (gameMessageRouter == null) return;

            SetPrivateField(gameMessageRouter, "webSocketServer", webSocketServer);
            SetPrivateField(gameMessageRouter, "playerRegistry", playerRegistry);
            SetPrivateField(gameMessageRouter, "playerStateStore", playerStateStore);

            if (bindMetaverseBridgesToRouter)
            {
                SetPrivateField(gameMessageRouter, "spawnNetworkBridge", spawnNetworkBridge);
                SetPrivateField(gameMessageRouter, "rpcNetworkBridge", rpcNetworkBridge);
                SetPrivateField(gameMessageRouter, "stateSyncBridge", stateSyncBridge);
                SetPrivateField(gameMessageRouter, "ownershipBridge", ownershipBridge);
                SetPrivateField(gameMessageRouter, "playerMovementBridge", playerMovementBridge);
            }

            gameMessageRouter.Rebind();

            Log("DedicatedGameMessageRouter references bound.");
        }

        //* این تابع کنترلر تست Cmd/Rpc/TargetRpc را با اسپاون منیجر و کانفیگ وصل می کند.
        private void BindMirrorLikeSmokeController()
        {
            if (!autoBindMirrorLikeSmokeController) return;
            if (spawnManager == null || runtimeConfig == null) return;
            if (!runtimeConfig.EnableNetworkRpcSmokeTest && rpcSmokeController == null) return;

            if (rpcSmokeController == null)
            {
                GameObject obj = new GameObject("MetaverseNetworkRpcSmokeController");
                rpcSmokeController = obj.AddComponent<MetaverseNetworkRpcSmokeController>();
                if (runtimeConfig.DontDestroyOnLoad) DontDestroyOnLoad(obj);
                Log("Added missing MetaverseNetworkRpcSmokeController for Phase 33A.");
            }

            rpcSmokeController.Bind(spawnManager, runtimeConfig);
            Log("Phase 33A Mirror-Like RPC smoke controller bound.");
        }

        //* این تابع وضعیت کامپوننت های لازم را در کنسول چاپ می کند.
        private void PrintStatus()
        {
            string status =
                "Dedicated gameplay pipeline status" +
                " | webSocketServer=" + BoolText(webSocketServer != null) +
                " | playerRegistry=" + BoolText(playerRegistry != null) +
                " | playerStateStore=" + BoolText(playerStateStore != null) +
                " | gameMessageRouter=" + BoolText(gameMessageRouter != null) +
                " | runtimeConfig=" + BoolText(runtimeConfig != null) +
                " | spawnManager=" + BoolText(spawnManager != null) +
                " | spawnBridge=" + BoolText(spawnNetworkBridge != null) +
                " | rpcBridge=" + BoolText(rpcNetworkBridge != null) +
                " | stateSyncBridge=" + BoolText(stateSyncBridge != null) +
                " | ownershipBridge=" + BoolText(ownershipBridge != null) +
                " | playerMovementBridge=" + BoolText(playerMovementBridge != null) +
                " | phase33A_RPCSmoke=" + BoolText(runtimeConfig != null && runtimeConfig.EnableNetworkRpcSmokeTest) +
                " | rpcSmokeController=" + BoolText(rpcSmokeController != null);

            Debug.Log("[DedicatedServerGameplayPipelineAutoBinder] " + status);

            if (webSocketServer == null || playerRegistry == null || playerStateStore == null || gameMessageRouter == null)
            {
                Debug.LogError("[DedicatedServerGameplayPipelineAutoBinder] Dedicated gameplay pipeline is incomplete.");
            }

            if (runtimeConfig != null && runtimeConfig.EnableNetworkRpcSmokeTest && rpcSmokeController == null)
            {
                Debug.LogError("[DedicatedServerGameplayPipelineAutoBinder] Phase 33A RPC smoke test is enabled but controller is missing.");
            }
        }

        //* این تابع کامپوننت خواسته شده را روی همین آبجکت، فرزندان، یا کل صحنه پیدا می کند.
        private T FindComponent<T>() where T : Component
        {
            T component = GetComponent<T>();
            if (component != null) return component;

            component = GetComponentInChildren<T>(true);
            if (component != null) return component;

#if UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindFirstObjectByType<T>();
#else
            return UnityEngine.Object.FindObjectOfType<T>();
#endif
        }

        //* این تابع فیلد خصوصی یک کامپوننت را با نام مشخص مقداردهی می کند.
        private void SetPrivateField(object target, string fieldName, object value)
        {
            if (target == null || string.IsNullOrWhiteSpace(fieldName)) return;

            Type type = target.GetType();
            FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (field == null)
            {
                Debug.LogWarning("[DedicatedServerGameplayPipelineAutoBinder] Field not found | type=" +
                                 type.Name + " | field=" + fieldName);
                return;
            }

            field.SetValue(target, value);
        }

        //* این تابع مقدار بول را به متن خوانا تبدیل می کند.
        private string BoolText(bool value)
        {
            return value ? "OK" : "MISSING";
        }

        //* این تابع لاگ معمولی اتوبایندر را چاپ می کند.
        private void Log(string message)
        {
            if (!verboseLogs) return;

            Debug.Log("[DedicatedServerGameplayPipelineAutoBinder] " + message);
        }

        /*
        توضیح مکتوب فایل:
        این اسکریپت مسیر گیم پلی ددیکیتد سرور را به صورت خودکار آماده می کند.
        مسیر قدیمی player_state حفظ شده است.
        در Phase 33A بریج های Spawn/Rpc/StateSync/Ownership/PlayerMovement هم به Router وصل می شوند.
        اگر تست Cmd/Rpc/TargetRpc در RuntimeConfig فعال باشد، کنترلر Smoke Test شبیه میرور هم Bind می شود.
        این فایل هیچ تابعی از نسخه قبلی را حذف نمی کند و فقط مسیر نصب و اتصال را کامل تر می کند.
        */
    }
}
