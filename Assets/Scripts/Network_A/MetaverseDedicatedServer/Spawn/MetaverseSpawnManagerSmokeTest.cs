using UnityEngine;

public class MetaverseSpawnManagerSmokeTest : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MetaverseSpawnManager spawnManager;
    [SerializeField] private GameObject testPrefab;

    [Header("Spawn")]
    [SerializeField] private int ownerConnectionId = -1;
    [SerializeField] private string ownerConnectionIdText = "";
    [SerializeField] private string ownerUserId = "";
    [SerializeField] private string ownerPlayerId = "";
    [SerializeField] private Vector3 spawnPosition = Vector3.zero;
    [SerializeField] private bool preferNetworkServerApi = true;

    [Header("Debug")]
    [SerializeField] private bool logMessages = true;

    private MetaverseNetworkIdentity lastSpawned;
    private string lastSmokeRejectReason = string.Empty;

    public MetaverseNetworkIdentity LastSpawned => lastSpawned;
    public string LastSmokeRejectReason => lastSmokeRejectReason;
    public bool HasSpawnedObject => lastSpawned != null && lastSpawned.gameObject != null;

    public void TestSpawn()
    {
        if (!EnsureReferences() || testPrefab == null)
        {
            SetRejectReason("spawn_manager_or_prefab_missing");
            Debug.LogWarning("[MetaverseSpawnManagerSmokeTest] Spawn test failed. Assign SpawnManager and TestPrefab. | phase=33A | mirrorRoute=NetworkServer.Spawn");
            return;
        }

        GameObject obj = Instantiate(testPrefab, spawnPosition, Quaternion.identity);
        obj.name = testPrefab.name + "_Phase33A_SpawnSmoke";
        EnsureIdentity(obj);

        if (preferNetworkServerApi)
        {
            if (!string.IsNullOrWhiteSpace(ownerConnectionIdText) || !string.IsNullOrWhiteSpace(ownerUserId) || !string.IsNullOrWhiteSpace(ownerPlayerId))
            {
                lastSpawned = MetaverseNetworkServer.Spawn(obj, ownerConnectionIdText, ownerUserId, ownerPlayerId);
            }
            else
            {
                lastSpawned = MetaverseNetworkServer.Spawn(obj, ownerConnectionId);
            }
        }
        else
        {
            lastSpawned = spawnManager.Spawn(obj, ownerConnectionId);
            if (lastSpawned != null && (!string.IsNullOrWhiteSpace(ownerConnectionIdText) || !string.IsNullOrWhiteSpace(ownerUserId) || !string.IsNullOrWhiteSpace(ownerPlayerId)))
            {
                MetaverseNetworkServer.SetOwner(lastSpawned, ownerConnectionIdText, ownerUserId, ownerPlayerId);
            }
        }

        if (lastSpawned == null)
        {
            SetRejectReason("spawn_failed");
            Debug.LogWarning("[MetaverseSpawnManagerSmokeTest] Spawn test failed. | phase=33A | mirrorRoute=NetworkServer.Spawn | managerReject=" + Safe(spawnManager != null ? spawnManager.LastSpawnRejectReason : string.Empty));
            Destroy(obj);
            return;
        }

        SetRejectReason(string.Empty);
        if (logMessages)
        {
            Debug.Log("[MetaverseSpawnManagerSmokeTest] Spawn test ok | phase=33A | mirrorRoute=NetworkServer.Spawn" +
                      " | netId=" + lastSpawned.NetId +
                      " | prefabId=" + Safe(lastSpawned.PrefabId) +
                      " | ownerConnectionId=" + Safe(lastSpawned.OwnerConnectionIdText) +
                      " | ownerUserId=" + Safe(lastSpawned.OwnerUserId) +
                      " | ownerPlayerId=" + Safe(lastSpawned.OwnerPlayerId));
        }
    }

    public void TestDespawn()
    {
        if (!EnsureReferences() || lastSpawned == null)
        {
            SetRejectReason("no_spawned_object");
            Debug.LogWarning("[MetaverseSpawnManagerSmokeTest] Despawn test failed. No spawned object. | phase=33A | mirrorRoute=NetworkServer.Despawn");
            return;
        }

        int netId = lastSpawned.NetId;
        if (preferNetworkServerApi) MetaverseNetworkServer.Despawn(lastSpawned, "phase33A_spawn_manager_smoke_completed");
        else spawnManager.Despawn(lastSpawned.gameObject, "phase33A_spawn_manager_smoke_completed");

        lastSpawned = null;
        SetRejectReason(string.Empty);
        if (logMessages)
        {
            Debug.Log("[MetaverseSpawnManagerSmokeTest] Despawn test ok | phase=33A | mirrorRoute=NetworkServer.Despawn | netId=" + netId);
        }
    }

    public string GetSmokeDebugSummary()
    {
        return "Phase33A SpawnManagerSmoke" +
               " | hasManager=" + (spawnManager != null) +
               " | hasPrefab=" + (testPrefab != null) +
               " | hasSpawned=" + HasSpawnedObject +
               " | lastNetId=" + (lastSpawned != null ? lastSpawned.NetId : 0) +
               " | lastReject=" + Safe(lastSmokeRejectReason) +
               " | manager=" + (spawnManager != null ? spawnManager.GetSpawnDebugSummary() : "null");
    }

    private bool EnsureReferences()
    {
        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
        if (spawnManager == null)
        {
#if UNITY_2023_1_OR_NEWER
            spawnManager = FindFirstObjectByType<MetaverseSpawnManager>();
#else
            spawnManager = FindObjectOfType<MetaverseSpawnManager>();
#endif
        }

        return spawnManager != null;
    }

    private void EnsureIdentity(GameObject obj)
    {
        if (obj == null) return;
        MetaverseNetworkIdentity identity = obj.GetComponent<MetaverseNetworkIdentity>();
        if (identity == null) identity = obj.AddComponent<MetaverseNetworkIdentity>();
        if (string.IsNullOrWhiteSpace(identity.PrefabId)) identity.AssignPrefabId(obj.name);
    }

    private void SetRejectReason(string reason)
    {
        lastSmokeRejectReason = Safe(reason);
    }

    private string Safe(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
