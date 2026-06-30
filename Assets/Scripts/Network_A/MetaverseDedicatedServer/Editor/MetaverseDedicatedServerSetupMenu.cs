#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

public static class MetaverseDedicatedServerSetupMenu
{
    private const string RootPath = "Assets/Scripts/Network_A/MetaverseDedicatedServer";
    private const string ResourcesPath = RootPath + "/Resources";
    private const string RegistryPath = ResourcesPath + "/MetaverseNetworkPrefabRegistry.asset";
    private const string RuntimeConfigPath = ResourcesPath + "/MetaverseDedicatedServerRuntimeConfig.asset";
    private const string PrefabScanRoot = "Assets/Scripts/Network_A";

    [MenuItem("Metaverse/Dedicated Server/Setup/Create Or Update Runtime Assets")]
    public static void CreateOrUpdateRuntimeAssets()
    {
        EnsureFolders();
        MetaverseNetworkPrefabRegistry registry = LoadOrCreateAsset<MetaverseNetworkPrefabRegistry>(RegistryPath);
        MetaverseDedicatedServerRuntimeConfig config = LoadOrCreateAsset<MetaverseDedicatedServerRuntimeConfig>(RuntimeConfigPath);

        config.SetPrefabRegistry(registry);
        config.SetAutoInstallSpawnSystem(true);
        config.SetDontDestroyOnLoad(true);
        config.SetSpawnManagerObjectName("MetaverseSpawnManager");
        config.SetSpawnedRootObjectName("Metaverse_Spawned_Root");
        config.SetAutoInstallSpawnNetworkBridge(true);
        config.SetSpawnBridgeObjectName("MetaverseSpawnNetworkBridge");
        config.SetAutoInstallNetworkRpcBridge(true);
        config.SetNetworkRpcBridgeObjectName("MetaverseNetworkRpcBridge");
        config.SetLogInstall(true);
        config.SetEnableRuntimeSpawnTestPrefab(false);
        config.SetEnableSpawnRouteSmokeTest(false);
        config.SetEnableClientSpawnRouteSmokeReporter(false);
        config.SetEnableNetworkBehaviourLifecycleSmokeTest(false);
        config.SetNetworkBehaviourLifecycleSmokePrefabId("metaverse_network_behaviour_probe");
        config.SetNetworkBehaviourLifecycleSmokeSpawnRequiredPlayers(1);
        config.SetNetworkBehaviourLifecycleSmokeSnapshotRequiredPlayers(3);
        config.SetNetworkBehaviourLifecycleSmokeInitialDelaySeconds(5f);
        config.SetNetworkBehaviourLifecycleSmokeMinimumAliveSeconds(25f);
        config.SetNetworkBehaviourLifecycleSmokeMaxWaitBeforeSpawnSeconds(120f);
        config.SetNetworkBehaviourLifecycleSmokeMaxSnapshotWaitSeconds(180f);
        config.SetNetworkBehaviourLifecycleSmokeDespawnDelayAfterSnapshotSeconds(8f);
        config.SetEnableNetworkRpcSmokeTest(false);
        config.SetNetworkRpcSmokePrefabId("metaverse_network_rpc_probe");
        config.SetNetworkRpcSmokeSpawnRequiredPlayers(1);
        config.SetNetworkRpcSmokeSnapshotRequiredPlayers(3);
        config.SetNetworkRpcSmokeInitialDelaySeconds(5f);
        config.SetNetworkRpcSmokeMinimumAliveSeconds(30f);
        config.SetNetworkRpcSmokeDespawnDelayAfterSnapshotSeconds(10f);

        EditorUtility.SetDirty(registry);
        EditorUtility.SetDirty(config);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[MetaverseDedicatedServerSetupMenu] Runtime assets created or updated.");
    }

    [MenuItem("Metaverse/Dedicated Server/Setup/Scan And Register Network_A Prefabs")]
    public static void ScanAndRegisterNetworkAPrefabs()
    {
        CreateOrUpdateRuntimeAssets();
        MetaverseNetworkPrefabRegistry registry = AssetDatabase.LoadAssetAtPath<MetaverseNetworkPrefabRegistry>(RegistryPath);
        if (registry == null)
        {
            Debug.LogError("[MetaverseDedicatedServerSetupMenu] Registry asset was not found.");
            return;
        }

        int count = 0;
        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { PrefabScanRoot });
        for (int i = 0; i < prefabGuids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(prefabGuids[i]);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null || prefab.GetComponent<MetaverseNetworkIdentity>() == null) continue;

            string prefabId = BuildStablePrefabId(path, prefab.name);
            AssignPrefabIdInsidePrefab(path, prefabId, false);
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            registry.RegisterPrefab(prefabId, prefab);
            count++;
        }

        registry.ValidateRegistry();
        EditorUtility.SetDirty(registry);
        AssetDatabase.SaveAssets();
        Debug.Log($"[MetaverseDedicatedServerSetupMenu] Registered prefabs with MetaverseNetworkIdentity | count={count}");
    }

    [MenuItem("Metaverse/Dedicated Server/Setup/Add Identity To Selected Prefabs And Register")]
    public static void AddIdentityToSelectedPrefabsAndRegister()
    {
        CreateOrUpdateRuntimeAssets();
        MetaverseNetworkPrefabRegistry registry = AssetDatabase.LoadAssetAtPath<MetaverseNetworkPrefabRegistry>(RegistryPath);
        if (registry == null)
        {
            Debug.LogError("[MetaverseDedicatedServerSetupMenu] Registry asset was not found.");
            return;
        }

        int changedCount = 0;
        int registeredCount = 0;
        Object[] selectedObjects = Selection.objects;

        for (int i = 0; i < selectedObjects.Length; i++)
        {
            string path = AssetDatabase.GetAssetPath(selectedObjects[i]);
            if (string.IsNullOrWhiteSpace(path) || Path.GetExtension(path) != ".prefab") continue;

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) continue;

            string prefabId = BuildStablePrefabId(path, prefab.name);
            bool changed = AssignPrefabIdInsidePrefab(path, prefabId, true);
            if (changed) changedCount++;

            GameObject savedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (savedPrefab == null) continue;
            registry.RegisterPrefab(prefabId, savedPrefab);
            registeredCount++;
        }

        registry.ValidateRegistry();
        EditorUtility.SetDirty(registry);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[MetaverseDedicatedServerSetupMenu] Selected prefabs processed | changed={changedCount} | registered={registeredCount}");
    }

    private static bool AssignPrefabIdInsidePrefab(string path, string prefabId, bool addIdentityIfMissing)
    {
        GameObject prefabRoot = PrefabUtility.LoadPrefabContents(path);
        if (prefabRoot == null) return false;

        bool changed = false;
        try
        {
            MetaverseNetworkIdentity identity = prefabRoot.GetComponent<MetaverseNetworkIdentity>();
            if (identity == null && addIdentityIfMissing)
            {
                identity = prefabRoot.AddComponent<MetaverseNetworkIdentity>();
                changed = true;
            }

            if (identity != null && identity.PrefabId != prefabId)
            {
                identity.AssignPrefabId(prefabId);
                changed = true;
            }

            if (changed) PrefabUtility.SaveAsPrefabAsset(prefabRoot, path);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(prefabRoot);
        }

        return changed;
    }

    private static string BuildStablePrefabId(string assetPath, string prefabName)
    {
        string guid = AssetDatabase.AssetPathToGUID(assetPath);
        if (!string.IsNullOrWhiteSpace(guid)) return "pf_" + guid.Substring(0, 16);
        return prefabName.Trim().ToLowerInvariant().Replace(" ", "_");
    }

    private static void EnsureFolders()
    {
        EnsureFolder("Assets/Scripts");
        EnsureFolder("Assets/Scripts/Network_A");
        EnsureFolder(RootPath);
        EnsureFolder(ResourcesPath);
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path)?.Replace("\\", "/");
        string folder = Path.GetFileName(path);
        if (!string.IsNullOrWhiteSpace(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, folder);
    }

    private static T LoadOrCreateAsset<T>(string path) where T : ScriptableObject
    {
        T asset = AssetDatabase.LoadAssetAtPath<T>(path);
        if (asset != null) return asset;

        asset = ScriptableObject.CreateInstance<T>();
        AssetDatabase.CreateAsset(asset, path);
        return asset;
    }
}
#endif
