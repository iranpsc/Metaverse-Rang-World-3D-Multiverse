using UnityEngine;

public static class MetaverseRuntimeSpawnTestPrefabInstaller
{
    public const string TestPrefabId = "metaverse_spawn_test_cube";
    private const string TemplateObjectName = "Metaverse_Spawn_Test_Cube_Template";
    private const string RuntimeRegistryName = "MetaverseRuntimeSpawnTestPrefabRegistry";

    public static GameObject InstallRuntimeTestPrefab(MetaverseSpawnManager spawnManager, bool logMessages = true)
    {
        if (spawnManager == null) return null;

        MetaverseNetworkPrefabRegistry registry = spawnManager.PrefabRegistry;
        if (registry == null)
        {
            registry = ScriptableObject.CreateInstance<MetaverseNetworkPrefabRegistry>();
            registry.name = RuntimeRegistryName;
            spawnManager.SetPrefabRegistry(registry);
        }

        if (registry.TryGetPrefab(TestPrefabId, out GameObject existingPrefab) && existingPrefab != null)
        {
            EnsureComponents(existingPrefab);
            if (logMessages)
            {
                Debug.Log("[MetaverseRuntimeSpawnTestPrefabInstaller] Runtime test prefab already registered | phase=33A | prefabId=" + TestPrefabId);
            }
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

        EnsureComponents(template);
        template.SetActive(false);
        Object.DontDestroyOnLoad(template);

        registry.RegisterPrefab(TestPrefabId, template);
        registry.RebuildCache();

        if (logMessages)
        {
            Debug.Log("[MetaverseRuntimeSpawnTestPrefabInstaller] Runtime test prefab registered | phase=33A | mirrorRoute=NetworkServer.SpawnPrefab" +
                      " | prefabId=" + TestPrefabId +
                      " | registry=" + registry.name);
        }

        return template;
    }

    public static bool IsRuntimeTestPrefabInstalled(MetaverseSpawnManager spawnManager)
    {
        return spawnManager != null && spawnManager.PrefabRegistry != null && spawnManager.PrefabRegistry.TryGetPrefab(TestPrefabId, out GameObject prefab) && prefab != null;
    }

    private static void EnsureComponents(GameObject template)
    {
        if (template == null) return;
        MetaverseNetworkIdentity identity = template.GetComponent<MetaverseNetworkIdentity>();
        if (identity == null) identity = template.AddComponent<MetaverseNetworkIdentity>();
        identity.AssignPrefabId(TestPrefabId);
        identity.SetServerOwned(true);
        identity.SetLocalPlayer(false);
    }
}
