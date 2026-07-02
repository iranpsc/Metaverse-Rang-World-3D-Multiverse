using UnityEngine;

public static class MetaverseNetworkStateSyncSmokePrefabInstaller
{
    public const string DefaultPrefabId = "metaverse_network_state_sync_probe";
    private const string TemplateObjectName = "Metaverse_NetworkStateSync_Probe_Template";
    private const string RuntimeRegistryName = "MetaverseNetworkStateSyncSmokePrefabRegistry";

    public static GameObject InstallRuntimeStateSyncProbePrefab(MetaverseSpawnManager spawnManager, string prefabId, bool logMessages = true)
    {
        if (spawnManager == null) return null;
        string safePrefabId = string.IsNullOrWhiteSpace(prefabId) ? DefaultPrefabId : prefabId.Trim();

        MetaverseNetworkPrefabRegistry registry = spawnManager.PrefabRegistry;
        if (registry == null)
        {
            registry = ScriptableObject.CreateInstance<MetaverseNetworkPrefabRegistry>();
            registry.name = RuntimeRegistryName;
            spawnManager.SetPrefabRegistry(registry);
        }

        if (registry.TryGetPrefab(safePrefabId, out GameObject existingPrefab) && existingPrefab != null)
        {
            EnsureProbeComponents(existingPrefab, safePrefabId);
            if (logMessages) Debug.Log("[MetaverseNetworkStateSyncSmokePrefabInstaller] Runtime state sync probe prefab already registered | phase=33A | prefabId=" + safePrefabId);
            return existingPrefab;
        }

        GameObject template = GameObject.Find(TemplateObjectName);
        if (template == null)
        {
            template = GameObject.CreatePrimitive(PrimitiveType.Cube);
            template.name = TemplateObjectName;
            template.transform.position = new Vector3(0f, -10000f, 0f);
            template.transform.rotation = Quaternion.identity;
            template.transform.localScale = Vector3.one;
        }

        EnsureProbeComponents(template, safePrefabId);
        template.SetActive(false);
        Object.DontDestroyOnLoad(template);

        registry.RegisterPrefab(safePrefabId, template);
        registry.RebuildCache();

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkStateSyncSmokePrefabInstaller] Runtime state sync probe prefab registered | phase=33A | mirrorRoute=SyncVar+SyncTransform" +
                      " | prefabId=" + safePrefabId +
                      " | registry=" + registry.name);
        }

        return template;
    }

    public static bool IsRuntimeStateSyncProbePrefabInstalled(MetaverseSpawnManager spawnManager, string prefabId)
    {
        if (spawnManager == null || spawnManager.PrefabRegistry == null) return false;
        string safePrefabId = string.IsNullOrWhiteSpace(prefabId) ? DefaultPrefabId : prefabId.Trim();
        return spawnManager.PrefabRegistry.TryGetPrefab(safePrefabId, out GameObject prefab) && prefab != null;
    }

    private static void EnsureProbeComponents(GameObject obj, string prefabId)
    {
        if (obj == null) return;
        MetaverseNetworkIdentity identity = obj.GetComponent<MetaverseNetworkIdentity>();
        if (identity == null) identity = obj.AddComponent<MetaverseNetworkIdentity>();
        identity.AssignPrefabId(prefabId);
        identity.SetServerOwned(true);
        identity.SetLocalPlayer(false);
        if (obj.GetComponent<MetaverseNetworkStateSyncSmokeProbe>() == null) obj.AddComponent<MetaverseNetworkStateSyncSmokeProbe>();
    }
}
