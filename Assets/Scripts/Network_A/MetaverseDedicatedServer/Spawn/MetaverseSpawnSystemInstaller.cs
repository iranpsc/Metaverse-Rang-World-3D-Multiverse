using Network_A.GameServer.Gameplay;
using UnityEngine;

public static class MetaverseSpawnSystemInstaller
{
    private const string DefaultManagerName = "MetaverseSpawnManager";
    private const string DefaultRootName = "Metaverse_Spawned_Root";
    private const string DefaultBridgeName = "MetaverseSpawnNetworkBridge";
    private const string DefaultRpcBridgeName = "MetaverseNetworkRpcBridge";
    private const string DefaultSmokeControllerName = "MetaverseServerSpawnRouteSmokeController";
    private const string DefaultClientReporterName = "MetaverseClientSpawnRouteSmokeReporter";
    private const string DefaultNetworkBehaviourSmokeName = "MetaverseNetworkBehaviourLifecycleSmokeController";
    private const string DefaultNetworkRpcSmokeName = "MetaverseNetworkRpcSmokeController";

    public static MetaverseSpawnManager Install(MetaverseDedicatedServerRuntimeConfig config)
    {
        MetaverseSpawnManager manager = FindOrCreateSpawnManager(config);
        if (manager == null) return null;

        MetaverseNetworkPrefabRegistry registry = config != null ? config.PrefabRegistry : null;
        manager.SetPrefabRegistry(registry);
        manager.SetSpawnedRoot(FindOrCreateSpawnedRoot(config));

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

        bool shouldRebindGameplayRouters = false;

        if (config != null && config.AutoInstallSpawnNetworkBridge)
        {
            MetaverseSpawnNetworkBridge bridge = FindOrCreateSpawnNetworkBridge(config);
            bridge.Bind(manager);
            if (config.DontDestroyOnLoad) Object.DontDestroyOnLoad(bridge.gameObject);
            shouldRebindGameplayRouters = true;
        }

        if (config != null && config.AutoInstallNetworkRpcBridge)
        {
            MetaverseNetworkRpcBridge rpcBridge = FindOrCreateNetworkRpcBridge(config);
            rpcBridge.Bind(manager);
            if (config.DontDestroyOnLoad) Object.DontDestroyOnLoad(rpcBridge.gameObject);
            shouldRebindGameplayRouters = true;
        }

        if (shouldRebindGameplayRouters)
        {
            RebindGameplayRouters();
        }

        if (ShouldInstallServerSmokeTest(config))
        {
            MetaverseServerSpawnRouteSmokeController smokeController = FindOrCreateServerSpawnRouteSmokeController(manager);
            if (config != null && config.DontDestroyOnLoad && smokeController != null)
            {
                Object.DontDestroyOnLoad(smokeController.gameObject);
            }
        }

        if (ShouldInstallNetworkBehaviourSmokeServerController(config))
        {
            MetaverseNetworkBehaviourLifecycleSmokeController networkBehaviourSmokeController = FindOrCreateNetworkBehaviourSmokeController(manager);
            if (config != null && config.DontDestroyOnLoad && networkBehaviourSmokeController != null)
            {
                Object.DontDestroyOnLoad(networkBehaviourSmokeController.gameObject);
            }
        }

        if (ShouldInstallNetworkRpcSmokeServerController(config))
        {
            MetaverseNetworkRpcSmokeController networkRpcSmokeController = FindOrCreateNetworkRpcSmokeController(manager, config);
            if (config != null && config.DontDestroyOnLoad && networkRpcSmokeController != null)
            {
                Object.DontDestroyOnLoad(networkRpcSmokeController.gameObject);
            }
        }

        if (!ShouldInstallServerSmokeTest(config) && ShouldInstallClientSmokeReporter(config))
        {
            MetaverseClientSpawnRouteSmokeReporter reporter = FindOrCreateClientSpawnRouteSmokeReporter(manager);
            if (config != null && config.DontDestroyOnLoad && reporter != null)
            {
                Object.DontDestroyOnLoad(reporter.gameObject);
            }
        }

        if (config != null && config.DontDestroyOnLoad) Object.DontDestroyOnLoad(manager.gameObject);
        if (config != null && config.LogInstall)
        {
            Debug.Log("[MetaverseSpawnSystemInstaller] Spawn system installed | registry=" +
                      (manager.PrefabRegistry != null ? manager.PrefabRegistry.name : "null") +
                      " | smokeTest=" + BoolText(ShouldInstallServerSmokeTest(config)) +
                      " | clientReporter=" + BoolText(ShouldInstallClientSmokeReporter(config)) +
                      " | networkBehaviourSmoke=" + BoolText(ShouldInstallNetworkBehaviourSmokeTest(config)) +
                      " | networkRpcBridge=" + BoolText(config.AutoInstallNetworkRpcBridge) +
                      " | networkRpcSmoke=" + BoolText(ShouldInstallNetworkRpcSmokeTest(config)));
        }
        return manager;
    }

    public static MetaverseSpawnManager InstallFromResources()
    {
        return Install(MetaverseDedicatedServerRuntimeConfig.LoadDefault());
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

    private static string BoolText(bool value)
    {
        return value ? "ON" : "OFF";
    }
}
