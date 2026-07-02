using Network_A.GameServer.Gameplay;
using UnityEngine;

public static class MetaverseSpawnSystemInstaller
{
    private const string DefaultManagerName = "MetaverseSpawnManager";
    private const string DefaultRootName = "Metaverse_Spawned_Root";
    private const string DefaultBridgeName = "MetaverseSpawnNetworkBridge";
    private const string DefaultRpcBridgeName = "MetaverseNetworkRpcBridge";
    private const string DefaultStateSyncBridgeName = "MetaverseNetworkStateSyncBridge";
    private const string DefaultOwnershipBridgeName = "MetaverseNetworkOwnershipBridge";
    private const string DefaultPlayerObjectServerName = "MetaverseNetworkPlayerObjectServer";
    private const string DefaultPlayerMovementBridgeName = "MetaverseNetworkPlayerMovementBridge";
    private const string DefaultSmokeControllerName = "MetaverseServerSpawnRouteSmokeController";
    private const string DefaultClientReporterName = "MetaverseClientSpawnRouteSmokeReporter";
    private const string DefaultNetworkBehaviourSmokeName = "MetaverseNetworkBehaviourLifecycleSmokeController";
    private const string DefaultNetworkRpcSmokeName = "MetaverseNetworkRpcSmokeController";
    private const string DefaultNetworkStateSyncSmokeName = "MetaverseNetworkStateSyncSmokeController";

    public static MetaverseSpawnManager Install(MetaverseDedicatedServerRuntimeConfig config)
    {
        MetaverseSpawnManager manager = FindOrCreateSpawnManager(config);
        if (manager == null) return null;

        MetaverseNetworkPrefabRegistry registry = config != null ? config.PrefabRegistry : null;
        manager.SetPrefabRegistry(registry);
        manager.SetSpawnedRoot(FindOrCreateSpawnedRoot(config));

        InstallRuntimePrefabs(manager, config);
        bool shouldRebindGameplayRouters = InstallMirrorLikeGameplayApiBridges(manager, config);
        InstallSmokeControllers(manager, config);
        InstallPlayerObjectServer(manager, config);
        InstallClientReporter(manager, config);

        if (shouldRebindGameplayRouters) RebindGameplayRouters();
        if (config != null && config.DontDestroyOnLoad) Object.DontDestroyOnLoad(manager.gameObject);
        if (config != null && config.LogInstall) Debug.Log(GetInstallSummary(manager, config));
        return manager;
    }

    public static MetaverseSpawnManager InstallFromResources()
    {
        return Install(MetaverseDedicatedServerRuntimeConfig.LoadDefault());
    }

    public static MetaverseSpawnManager InstallMirrorLikeGameplayApi(MetaverseDedicatedServerRuntimeConfig config)
    {
        return Install(config);
    }

    public static MetaverseSpawnManager InstallMirrorLikeGameplayApiFromResources()
    {
        return Install(MetaverseDedicatedServerRuntimeConfig.LoadDefault());
    }

    public static bool IsMirrorLikeGameplayApiInstalled()
    {
        return MetaverseSpawnManager.Instance != null &&
               HasSceneComponent<MetaverseSpawnNetworkBridge>() &&
               MetaverseNetworkRpcBridge.Instance != null &&
               MetaverseNetworkStateSyncBridge.Instance != null &&
               MetaverseNetworkOwnershipBridge.Instance != null &&
               MetaverseNetworkPlayerMovementBridge.Instance != null;
    }

    public static string GetMirrorLikeGameplayApiInstallSummary()
    {
        return "Phase33A Mirror-Like API" +
               " | spawnManager=" + BoolText(MetaverseSpawnManager.Instance != null) +
               " | spawnBridge=" + BoolText(HasSceneComponent<MetaverseSpawnNetworkBridge>()) +
               " | rpcBridge=" + BoolText(MetaverseNetworkRpcBridge.Instance != null) +
               " | stateSyncBridge=" + BoolText(MetaverseNetworkStateSyncBridge.Instance != null) +
               " | ownershipBridge=" + BoolText(MetaverseNetworkOwnershipBridge.Instance != null) +
               " | movementBridge=" + BoolText(MetaverseNetworkPlayerMovementBridge.Instance != null) +
               " | installed=" + BoolText(IsMirrorLikeGameplayApiInstalled());
    }

    private static void InstallRuntimePrefabs(MetaverseSpawnManager manager, MetaverseDedicatedServerRuntimeConfig config)
    {
        if (ShouldInstallRuntimeTestPrefab(config))
        {
            MetaverseRuntimeSpawnTestPrefabInstaller.InstallRuntimeTestPrefab(manager, config == null || config.LogInstall);
        }

        if (ShouldInstallNetworkBehaviourSmokeTest(config))
        {
            MetaverseNetworkBehaviourSmokePrefabInstaller.InstallRuntimeProbePrefab(
                manager,
                config != null ? config.NetworkBehaviourLifecycleSmokePrefabId : MetaverseNetworkBehaviourSmokePrefabInstaller.DefaultPrefabId,
                config == null || config.LogInstall);
        }

        if (ShouldInstallNetworkRpcSmokeTest(config))
        {
            MetaverseNetworkRpcSmokePrefabInstaller.InstallRuntimeRpcProbePrefab(
                manager,
                config != null ? config.NetworkRpcSmokePrefabId : MetaverseNetworkRpcSmokePrefabInstaller.DefaultPrefabId,
                config == null || config.LogInstall);
        }

        if (ShouldInstallNetworkStateSyncSmokeTest(config))
        {
            MetaverseNetworkStateSyncSmokePrefabInstaller.InstallRuntimeStateSyncProbePrefab(
                manager,
                config != null ? config.NetworkStateSyncSmokePrefabId : MetaverseNetworkStateSyncSmokePrefabInstaller.DefaultPrefabId,
                config == null || config.LogInstall);
        }

        if (ShouldInstallNetworkPlayerObjectRuntimePrefab(config))
        {
            MetaverseNetworkPlayerObjectSmokePrefabInstaller.InstallRuntimePlayerObjectProbePrefab(
                manager,
                config != null ? config.NetworkPlayerObjectSmokePrefabId : MetaverseNetworkPlayerObjectSmokePrefabInstaller.DefaultPrefabId,
                config == null || config.LogInstall);
        }
    }

    private static bool InstallMirrorLikeGameplayApiBridges(MetaverseSpawnManager manager, MetaverseDedicatedServerRuntimeConfig config)
    {
        bool shouldRebindGameplayRouters = false;
        if (config == null) return false;

        if (config.AutoInstallSpawnNetworkBridge)
        {
            MetaverseSpawnNetworkBridge bridge = FindOrCreateSpawnNetworkBridge(config);
            bridge.Bind(manager);
            if (config.DontDestroyOnLoad) Object.DontDestroyOnLoad(bridge.gameObject);
            shouldRebindGameplayRouters = true;
        }

        if (config.AutoInstallNetworkRpcBridge)
        {
            MetaverseNetworkRpcBridge rpcBridge = FindOrCreateNetworkRpcBridge(config);
            rpcBridge.Bind(manager);
            if (config.DontDestroyOnLoad) Object.DontDestroyOnLoad(rpcBridge.gameObject);
            shouldRebindGameplayRouters = true;
        }

        if (config.AutoInstallNetworkStateSyncBridge)
        {
            MetaverseNetworkStateSyncBridge stateSyncBridge = FindOrCreateNetworkStateSyncBridge(config);
            stateSyncBridge.Bind(manager);
            if (config.DontDestroyOnLoad) Object.DontDestroyOnLoad(stateSyncBridge.gameObject);
            shouldRebindGameplayRouters = true;
        }

        if (config.AutoInstallNetworkOwnershipBridge)
        {
            MetaverseNetworkOwnershipBridge ownershipBridge = FindOrCreateNetworkOwnershipBridge(config);
            ownershipBridge.Bind(manager);
            if (config.DontDestroyOnLoad) Object.DontDestroyOnLoad(ownershipBridge.gameObject);
            shouldRebindGameplayRouters = true;
        }

        if (config.AutoInstallNetworkPlayerMovementBridge)
        {
            MetaverseNetworkPlayerMovementBridge movementBridge = FindOrCreateNetworkPlayerMovementBridge(config);
            movementBridge.Bind(manager, config);
            if (config.DontDestroyOnLoad) Object.DontDestroyOnLoad(movementBridge.gameObject);
            shouldRebindGameplayRouters = true;
        }

        return shouldRebindGameplayRouters;
    }

    private static void InstallSmokeControllers(MetaverseSpawnManager manager, MetaverseDedicatedServerRuntimeConfig config)
    {
        if (ShouldInstallServerSmokeTest(config))
        {
            MetaverseServerSpawnRouteSmokeController smokeController = FindOrCreateServerSpawnRouteSmokeController(manager);
            if (config != null && config.DontDestroyOnLoad && smokeController != null) Object.DontDestroyOnLoad(smokeController.gameObject);
        }

        if (ShouldInstallNetworkBehaviourSmokeServerController(config))
        {
            MetaverseNetworkBehaviourLifecycleSmokeController networkBehaviourSmokeController = FindOrCreateNetworkBehaviourSmokeController(manager);
            if (config != null && config.DontDestroyOnLoad && networkBehaviourSmokeController != null) Object.DontDestroyOnLoad(networkBehaviourSmokeController.gameObject);
        }

        if (ShouldInstallNetworkRpcSmokeServerController(config))
        {
            MetaverseNetworkRpcSmokeController networkRpcSmokeController = FindOrCreateNetworkRpcSmokeController(manager, config);
            if (config != null && config.DontDestroyOnLoad && networkRpcSmokeController != null) Object.DontDestroyOnLoad(networkRpcSmokeController.gameObject);
        }

        if (ShouldInstallNetworkStateSyncSmokeServerController(config))
        {
            MetaverseNetworkStateSyncSmokeController networkStateSyncSmokeController = FindOrCreateNetworkStateSyncSmokeController(manager, config);
            if (config != null && config.DontDestroyOnLoad && networkStateSyncSmokeController != null) Object.DontDestroyOnLoad(networkStateSyncSmokeController.gameObject);
        }
    }

    private static void InstallPlayerObjectServer(MetaverseSpawnManager manager, MetaverseDedicatedServerRuntimeConfig config)
    {
        if (!ShouldInstallNetworkPlayerObjectServer(config)) return;
        MetaverseNetworkPlayerObjectServer playerObjectServer = FindOrCreateNetworkPlayerObjectServer(manager, config);
        if (config != null && config.DontDestroyOnLoad && playerObjectServer != null) Object.DontDestroyOnLoad(playerObjectServer.gameObject);
    }

    private static void InstallClientReporter(MetaverseSpawnManager manager, MetaverseDedicatedServerRuntimeConfig config)
    {
        if (ShouldInstallServerSmokeTest(config) || !ShouldInstallClientSmokeReporter(config)) return;
        MetaverseClientSpawnRouteSmokeReporter reporter = FindOrCreateClientSpawnRouteSmokeReporter(manager);
        if (config != null && config.DontDestroyOnLoad && reporter != null) Object.DontDestroyOnLoad(reporter.gameObject);
    }

    private static bool ShouldInstallRuntimeTestPrefab(MetaverseDedicatedServerRuntimeConfig config)
    {
        return config != null && config.EnableRuntimeSpawnTestPrefab;
    }

    private static bool ShouldInstallServerSmokeTest(MetaverseDedicatedServerRuntimeConfig config)
    {
        return config != null && config.EnableSpawnRouteSmokeTest && Application.isBatchMode;
    }

    private static bool ShouldInstallNetworkBehaviourSmokeTest(MetaverseDedicatedServerRuntimeConfig config)
    {
        return config != null && config.EnableNetworkBehaviourLifecycleSmokeTest;
    }

    private static bool ShouldInstallNetworkBehaviourSmokeServerController(MetaverseDedicatedServerRuntimeConfig config)
    {
        return ShouldInstallNetworkBehaviourSmokeTest(config) && Application.isBatchMode;
    }

    private static bool ShouldInstallNetworkRpcSmokeTest(MetaverseDedicatedServerRuntimeConfig config)
    {
        return config != null && config.EnableNetworkRpcSmokeTest;
    }

    private static bool ShouldInstallNetworkRpcSmokeServerController(MetaverseDedicatedServerRuntimeConfig config)
    {
        return ShouldInstallNetworkRpcSmokeTest(config) && Application.isBatchMode;
    }

    private static bool ShouldInstallNetworkStateSyncSmokeTest(MetaverseDedicatedServerRuntimeConfig config)
    {
        return config != null && config.EnableNetworkStateSyncSmokeTest;
    }

    private static bool ShouldInstallNetworkPlayerObjectSmokeTest(MetaverseDedicatedServerRuntimeConfig config)
    {
        return config != null && config.EnableNetworkPlayerObjectSmokeTest;
    }

    private static bool ShouldInstallNetworkPlayerMovementSmokeTest(MetaverseDedicatedServerRuntimeConfig config)
    {
        return config != null && config.EnableNetworkPlayerMovementSmokeTest;
    }

    private static bool ShouldInstallNetworkPlayerObjectRuntimePrefab(MetaverseDedicatedServerRuntimeConfig config)
    {
        return config != null && (config.AutoInstallNetworkPlayerObjectServer || config.EnableNetworkPlayerObjectSmokeTest || config.EnableNetworkPlayerMovementSmokeTest);
    }

    private static bool ShouldInstallNetworkPlayerObjectServer(MetaverseDedicatedServerRuntimeConfig config)
    {
        return config != null && config.AutoInstallNetworkPlayerObjectServer && Application.isBatchMode;
    }

    private static bool ShouldInstallNetworkStateSyncSmokeServerController(MetaverseDedicatedServerRuntimeConfig config)
    {
        return ShouldInstallNetworkStateSyncSmokeTest(config) && Application.isBatchMode;
    }

    private static bool ShouldInstallClientSmokeReporter(MetaverseDedicatedServerRuntimeConfig config)
    {
        return config != null && config.EnableClientSpawnRouteSmokeReporter && !Application.isBatchMode;
    }

    private static MetaverseSpawnManager FindOrCreateSpawnManager(MetaverseDedicatedServerRuntimeConfig config)
    {
        if (MetaverseSpawnManager.Instance != null) return MetaverseSpawnManager.Instance;

#if UNITY_2023_1_OR_NEWER
        MetaverseSpawnManager existing = Object.FindFirstObjectByType<MetaverseSpawnManager>();
#else
        MetaverseSpawnManager existing = Object.FindObjectOfType<MetaverseSpawnManager>();
#endif
        if (existing != null) return existing;

        string objectName = config != null ? config.SpawnManagerObjectName : DefaultManagerName;
        GameObject obj = new GameObject(objectName);
        return obj.AddComponent<MetaverseSpawnManager>();
    }

    private static MetaverseSpawnNetworkBridge FindOrCreateSpawnNetworkBridge(MetaverseDedicatedServerRuntimeConfig config)
    {
#if UNITY_2023_1_OR_NEWER
        MetaverseSpawnNetworkBridge existing = Object.FindFirstObjectByType<MetaverseSpawnNetworkBridge>();
#else
        MetaverseSpawnNetworkBridge existing = Object.FindObjectOfType<MetaverseSpawnNetworkBridge>();
#endif
        if (existing != null) return existing;

        string objectName = config != null ? config.SpawnBridgeObjectName : DefaultBridgeName;
        GameObject obj = new GameObject(objectName);
        return obj.AddComponent<MetaverseSpawnNetworkBridge>();
    }

    private static MetaverseNetworkRpcBridge FindOrCreateNetworkRpcBridge(MetaverseDedicatedServerRuntimeConfig config)
    {
#if UNITY_2023_1_OR_NEWER
        MetaverseNetworkRpcBridge existing = Object.FindFirstObjectByType<MetaverseNetworkRpcBridge>();
#else
        MetaverseNetworkRpcBridge existing = Object.FindObjectOfType<MetaverseNetworkRpcBridge>();
#endif
        if (existing != null) return existing;

        string objectName = config != null ? config.NetworkRpcBridgeObjectName : DefaultRpcBridgeName;
        GameObject obj = new GameObject(objectName);
        return obj.AddComponent<MetaverseNetworkRpcBridge>();
    }

    private static MetaverseNetworkStateSyncBridge FindOrCreateNetworkStateSyncBridge(MetaverseDedicatedServerRuntimeConfig config)
    {
#if UNITY_2023_1_OR_NEWER
        MetaverseNetworkStateSyncBridge existing = Object.FindFirstObjectByType<MetaverseNetworkStateSyncBridge>();
#else
        MetaverseNetworkStateSyncBridge existing = Object.FindObjectOfType<MetaverseNetworkStateSyncBridge>();
#endif
        if (existing != null) return existing;

        string objectName = config != null ? config.NetworkStateSyncBridgeObjectName : DefaultStateSyncBridgeName;
        GameObject obj = new GameObject(objectName);
        return obj.AddComponent<MetaverseNetworkStateSyncBridge>();
    }

    private static MetaverseNetworkOwnershipBridge FindOrCreateNetworkOwnershipBridge(MetaverseDedicatedServerRuntimeConfig config)
    {
#if UNITY_2023_1_OR_NEWER
        MetaverseNetworkOwnershipBridge existing = Object.FindFirstObjectByType<MetaverseNetworkOwnershipBridge>();
#else
        MetaverseNetworkOwnershipBridge existing = Object.FindObjectOfType<MetaverseNetworkOwnershipBridge>();
#endif
        if (existing != null) return existing;

        string objectName = config != null ? config.NetworkOwnershipBridgeObjectName : DefaultOwnershipBridgeName;
        GameObject obj = new GameObject(objectName);
        return obj.AddComponent<MetaverseNetworkOwnershipBridge>();
    }

    private static MetaverseNetworkPlayerMovementBridge FindOrCreateNetworkPlayerMovementBridge(MetaverseDedicatedServerRuntimeConfig config)
    {
#if UNITY_2023_1_OR_NEWER
        MetaverseNetworkPlayerMovementBridge existing = Object.FindFirstObjectByType<MetaverseNetworkPlayerMovementBridge>();
#else
        MetaverseNetworkPlayerMovementBridge existing = Object.FindObjectOfType<MetaverseNetworkPlayerMovementBridge>();
#endif
        if (existing != null) return existing;

        string objectName = config != null ? config.NetworkPlayerMovementBridgeObjectName : DefaultPlayerMovementBridgeName;
        GameObject obj = new GameObject(objectName);
        return obj.AddComponent<MetaverseNetworkPlayerMovementBridge>();
    }

    private static MetaverseServerSpawnRouteSmokeController FindOrCreateServerSpawnRouteSmokeController(MetaverseSpawnManager manager)
    {
#if UNITY_2023_1_OR_NEWER
        MetaverseServerSpawnRouteSmokeController existing = Object.FindFirstObjectByType<MetaverseServerSpawnRouteSmokeController>();
#else
        MetaverseServerSpawnRouteSmokeController existing = Object.FindObjectOfType<MetaverseServerSpawnRouteSmokeController>();
#endif
        if (existing != null)
        {
            existing.Bind(manager);
            return existing;
        }

        GameObject obj = new GameObject(DefaultSmokeControllerName);
        MetaverseServerSpawnRouteSmokeController controller = obj.AddComponent<MetaverseServerSpawnRouteSmokeController>();
        controller.Bind(manager);
        return controller;
    }

    private static MetaverseClientSpawnRouteSmokeReporter FindOrCreateClientSpawnRouteSmokeReporter(MetaverseSpawnManager manager)
    {
#if UNITY_2023_1_OR_NEWER
        MetaverseClientSpawnRouteSmokeReporter existing = Object.FindFirstObjectByType<MetaverseClientSpawnRouteSmokeReporter>();
#else
        MetaverseClientSpawnRouteSmokeReporter existing = Object.FindObjectOfType<MetaverseClientSpawnRouteSmokeReporter>();
#endif
        if (existing != null)
        {
            existing.Bind(manager);
            return existing;
        }

        GameObject obj = new GameObject(DefaultClientReporterName);
        MetaverseClientSpawnRouteSmokeReporter reporter = obj.AddComponent<MetaverseClientSpawnRouteSmokeReporter>();
        reporter.Bind(manager);
        return reporter;
    }

    private static MetaverseNetworkBehaviourLifecycleSmokeController FindOrCreateNetworkBehaviourSmokeController(MetaverseSpawnManager manager)
    {
#if UNITY_2023_1_OR_NEWER
        MetaverseNetworkBehaviourLifecycleSmokeController existing = Object.FindFirstObjectByType<MetaverseNetworkBehaviourLifecycleSmokeController>();
#else
        MetaverseNetworkBehaviourLifecycleSmokeController existing = Object.FindObjectOfType<MetaverseNetworkBehaviourLifecycleSmokeController>();
#endif
        if (existing != null)
        {
            existing.Bind(manager);
            return existing;
        }

        GameObject obj = new GameObject(DefaultNetworkBehaviourSmokeName);
        MetaverseNetworkBehaviourLifecycleSmokeController controller = obj.AddComponent<MetaverseNetworkBehaviourLifecycleSmokeController>();
        controller.Bind(manager);
        return controller;
    }

    private static MetaverseNetworkRpcSmokeController FindOrCreateNetworkRpcSmokeController(MetaverseSpawnManager manager, MetaverseDedicatedServerRuntimeConfig config)
    {
#if UNITY_2023_1_OR_NEWER
        MetaverseNetworkRpcSmokeController existing = Object.FindFirstObjectByType<MetaverseNetworkRpcSmokeController>();
#else
        MetaverseNetworkRpcSmokeController existing = Object.FindObjectOfType<MetaverseNetworkRpcSmokeController>();
#endif
        if (existing != null)
        {
            existing.Bind(manager, config);
            return existing;
        }

        GameObject obj = new GameObject(DefaultNetworkRpcSmokeName);
        MetaverseNetworkRpcSmokeController controller = obj.AddComponent<MetaverseNetworkRpcSmokeController>();
        controller.Bind(manager, config);
        return controller;
    }

    private static MetaverseNetworkPlayerObjectServer FindOrCreateNetworkPlayerObjectServer(MetaverseSpawnManager manager, MetaverseDedicatedServerRuntimeConfig config)
    {
#if UNITY_2023_1_OR_NEWER
        MetaverseNetworkPlayerObjectServer existing = Object.FindFirstObjectByType<MetaverseNetworkPlayerObjectServer>();
#else
        MetaverseNetworkPlayerObjectServer existing = Object.FindObjectOfType<MetaverseNetworkPlayerObjectServer>();
#endif
        if (existing != null)
        {
            existing.Bind(manager, config);
            return existing;
        }

        string objectName = config != null ? config.NetworkPlayerObjectServerObjectName : DefaultPlayerObjectServerName;
        GameObject obj = new GameObject(objectName);
        MetaverseNetworkPlayerObjectServer server = obj.AddComponent<MetaverseNetworkPlayerObjectServer>();
        server.Bind(manager, config);
        return server;
    }

    private static MetaverseNetworkStateSyncSmokeController FindOrCreateNetworkStateSyncSmokeController(MetaverseSpawnManager manager, MetaverseDedicatedServerRuntimeConfig config)
    {
#if UNITY_2023_1_OR_NEWER
        MetaverseNetworkStateSyncSmokeController existing = Object.FindFirstObjectByType<MetaverseNetworkStateSyncSmokeController>();
#else
        MetaverseNetworkStateSyncSmokeController existing = Object.FindObjectOfType<MetaverseNetworkStateSyncSmokeController>();
#endif
        if (existing != null)
        {
            existing.Bind(manager, config);
            return existing;
        }

        GameObject obj = new GameObject(DefaultNetworkStateSyncSmokeName);
        MetaverseNetworkStateSyncSmokeController controller = obj.AddComponent<MetaverseNetworkStateSyncSmokeController>();
        controller.Bind(manager, config);
        return controller;
    }

    private static Transform FindOrCreateSpawnedRoot(MetaverseDedicatedServerRuntimeConfig config)
    {
        string objectName = config != null ? config.SpawnedRootObjectName : DefaultRootName;
        GameObject existing = GameObject.Find(objectName);
        if (existing != null) return existing.transform;

        GameObject root = new GameObject(objectName);
        if (config != null && config.DontDestroyOnLoad) Object.DontDestroyOnLoad(root);
        return root.transform;
    }

    private static void RebindGameplayRouters()
    {
#if UNITY_2023_1_OR_NEWER
        DedicatedGameMessageRouter[] routers = Object.FindObjectsByType<DedicatedGameMessageRouter>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
        DedicatedGameMessageRouter[] routers = Object.FindObjectsOfType<DedicatedGameMessageRouter>(true);
#endif
        if (routers == null) return;

        for (int i = 0; i < routers.Length; i++)
        {
            if (routers[i] == null) continue;
            routers[i].Rebind();
        }
    }

    private static string GetInstallSummary(MetaverseSpawnManager manager, MetaverseDedicatedServerRuntimeConfig config)
    {
        return "[MetaverseSpawnSystemInstaller] Phase33A install completed" +
               " | registry=" + (manager != null && manager.PrefabRegistry != null ? manager.PrefabRegistry.name : "null") +
               " | smokeTest=" + BoolText(ShouldInstallServerSmokeTest(config)) +
               " | clientReporter=" + BoolText(ShouldInstallClientSmokeReporter(config)) +
               " | networkBehaviourSmoke=" + BoolText(ShouldInstallNetworkBehaviourSmokeTest(config)) +
               " | networkRpcBridge=" + BoolText(config != null && config.AutoInstallNetworkRpcBridge) +
               " | networkRpcSmoke=" + BoolText(ShouldInstallNetworkRpcSmokeTest(config)) +
               " | mirrorLikeApiSmoke=" + BoolText(config != null && config.EnableMirrorLikeGameplayApiSmokeTest) +
               " | networkStateSyncBridge=" + BoolText(config != null && config.AutoInstallNetworkStateSyncBridge) +
               " | networkStateSyncSmoke=" + BoolText(ShouldInstallNetworkStateSyncSmokeTest(config)) +
               " | networkOwnershipBridge=" + BoolText(config != null && config.AutoInstallNetworkOwnershipBridge) +
               " | networkPlayerObjectSmoke=" + BoolText(ShouldInstallNetworkPlayerObjectSmokeTest(config)) +
               " | networkPlayerMovementBridge=" + BoolText(config != null && config.AutoInstallNetworkPlayerMovementBridge) +
               " | networkPlayerMovementSmoke=" + BoolText(ShouldInstallNetworkPlayerMovementSmokeTest(config)) +
               " | " + GetMirrorLikeGameplayApiInstallSummary();
    }

    private static bool HasSceneComponent<T>() where T : Component
    {
#if UNITY_2023_1_OR_NEWER
        return Object.FindFirstObjectByType<T>() != null;
#else
        return Object.FindObjectOfType<T>() != null;
#endif
    }

    private static string BoolText(bool value)
    {
        return value ? "ON" : "OFF";
    }
}
