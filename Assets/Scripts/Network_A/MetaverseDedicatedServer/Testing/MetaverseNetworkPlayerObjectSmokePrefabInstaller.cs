using UnityEngine;

public static class MetaverseNetworkPlayerObjectSmokePrefabInstaller
{
    public const string DefaultPrefabId = "metaverse_player_object_probe";

    public static void InstallRuntimePlayerObjectProbePrefab(MetaverseSpawnManager manager, string prefabId, bool logInstall)
    {
        if (manager == null) return;
        string safePrefabId = string.IsNullOrWhiteSpace(prefabId) ? DefaultPrefabId : prefabId.Trim();

        if (manager.PrefabRegistry != null && manager.PrefabRegistry.TryGetPrefab(safePrefabId, out GameObject existing) && existing != null)
        {
            EnsureComponents(existing, safePrefabId);
            if (logInstall) Debug.Log("[MetaverseNetworkPlayerObjectSmokePrefabInstaller] Runtime player object probe prefab already registered | phase=33A | prefabId=" + safePrefabId);
            return;
        }

        GameObject prefab = new GameObject("Metaverse_Player_Object_Probe_Prefab");
        prefab.SetActive(false);
        EnsureComponents(prefab, safePrefabId);
        manager.RegisterPrefab(safePrefabId, prefab);
        Object.DontDestroyOnLoad(prefab);

        if (logInstall)
        {
            Debug.Log("[MetaverseNetworkPlayerObjectSmokePrefabInstaller] Runtime player object probe prefab registered | phase=33A | mirrorRoute=NetworkServer.SpawnPrefab+AssignClientAuthority" +
                      " | prefabId=" + safePrefabId +
                      " | registry=" + (manager.PrefabRegistry != null ? manager.PrefabRegistry.name : "null"));
        }
    }

    public static bool IsRuntimePlayerObjectProbePrefabInstalled(MetaverseSpawnManager manager, string prefabId)
    {
        if (manager == null || manager.PrefabRegistry == null) return false;
        string safePrefabId = string.IsNullOrWhiteSpace(prefabId) ? DefaultPrefabId : prefabId.Trim();
        return manager.PrefabRegistry.TryGetPrefab(safePrefabId, out GameObject existing) && existing != null;
    }

    private static void EnsureComponents(GameObject prefab, string prefabId)
    {
        if (prefab == null) return;
        MetaverseNetworkIdentity identity = prefab.GetComponent<MetaverseNetworkIdentity>();
        if (identity == null) identity = prefab.AddComponent<MetaverseNetworkIdentity>();
        identity.AssignPrefabId(prefabId);
        if (prefab.GetComponent<MetaverseNetworkPlayerObjectSmokeProbe>() == null) prefab.AddComponent<MetaverseNetworkPlayerObjectSmokeProbe>();
        if (prefab.GetComponent<MetaverseNetworkPlayerMovementSmokeProbe>() == null) prefab.AddComponent<MetaverseNetworkPlayerMovementSmokeProbe>();
    }
}
