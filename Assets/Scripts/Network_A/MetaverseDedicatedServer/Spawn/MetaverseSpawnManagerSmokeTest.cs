using UnityEngine;

public class MetaverseSpawnManagerSmokeTest : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MetaverseSpawnManager spawnManager;
    [SerializeField] private GameObject testPrefab;

    [Header("Spawn")]
    [SerializeField] private int ownerConnectionId = -1;
    [SerializeField] private Vector3 spawnPosition = Vector3.zero;

    private MetaverseNetworkIdentity lastSpawned;

    public void TestSpawn()
    {
        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
        if (spawnManager == null) spawnManager = FindObjectOfType<MetaverseSpawnManager>();
        if (spawnManager == null || testPrefab == null)
        {
            Debug.LogWarning("[MetaverseSpawnManagerSmokeTest] Spawn test failed. Assign SpawnManager and TestPrefab.");
            return;
        }

        GameObject obj = Instantiate(testPrefab, spawnPosition, Quaternion.identity);
        lastSpawned = spawnManager.Spawn(obj, ownerConnectionId);
        Debug.Log(lastSpawned != null
            ? $"[MetaverseSpawnManagerSmokeTest] Spawn test ok | netId={lastSpawned.NetId} | prefabId={lastSpawned.PrefabId}"
            : "[MetaverseSpawnManagerSmokeTest] Spawn test failed.");
    }

    public void TestDespawn()
    {
        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
        if (spawnManager == null) spawnManager = FindObjectOfType<MetaverseSpawnManager>();
        if (spawnManager == null || lastSpawned == null)
        {
            Debug.LogWarning("[MetaverseSpawnManagerSmokeTest] Despawn test failed. No spawned object.");
            return;
        }

        spawnManager.Despawn(lastSpawned.gameObject, "smoke_test");
        lastSpawned = null;
    }
}
