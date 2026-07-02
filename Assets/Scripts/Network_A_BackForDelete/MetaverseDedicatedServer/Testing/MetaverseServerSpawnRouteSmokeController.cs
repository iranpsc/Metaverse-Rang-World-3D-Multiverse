using System.Collections;
using Network_A.GameServer.Players;
using UnityEngine;

public class MetaverseServerSpawnRouteSmokeController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MetaverseSpawnManager spawnManager;
    [SerializeField] private DedicatedPlayerRegistry playerRegistry;

    [Header("Auto Smoke")]
    [SerializeField] private bool autoRunInBatchMode = true;
    [SerializeField] private bool useNetworkServerApi = true;
    [SerializeField] private int spawnWhenPlayerCountAtLeast = 1;
    [SerializeField] private int secondSpawnWhenPlayerCountAtLeast = 2;
    [SerializeField] private float spawnDelayAfterConditionSeconds = 3f;
    [SerializeField] private float secondSpawnDelayAfterConditionSeconds = 6f;
    [SerializeField] private float despawnFirstDelaySeconds = 55f;
    [SerializeField] private float despawnSecondDelaySeconds = 65f;
    [SerializeField] private Vector3 firstSpawnPosition = new Vector3(0f, 1.5f, 4f);
    [SerializeField] private Vector3 secondSpawnPosition = new Vector3(2f, 1.5f, 4f);

    [Header("Debug")]
    [SerializeField] private bool logMessages = true;

    private bool started;
    private bool completed;
    private int issuedSpawnCount;
    private int issuedDespawnCount;
    private string lastRejectReason = string.Empty;
    private MetaverseNetworkIdentity firstSpawnedIdentity;
    private MetaverseNetworkIdentity secondSpawnedIdentity;

    public bool IsCompleted => completed;
    public int IssuedSpawnCount => issuedSpawnCount;
    public int IssuedDespawnCount => issuedDespawnCount;
    public string LastRejectReason => lastRejectReason;

    public void Bind(MetaverseSpawnManager manager)
    {
        spawnManager = manager;
        TryStart();
    }

    private void OnEnable()
    {
        TryStart();
    }

    private void Start()
    {
        TryStart();
    }

    private void TryStart()
    {
        if (started || completed) return;
        if (!autoRunInBatchMode || !Application.isBatchMode) return;
        if (!IsSmokeTestEnabled()) return;
        started = true;
        StartCoroutine(RunSmokeFlow());
    }

    private bool IsSmokeTestEnabled()
    {
        MetaverseDedicatedServerRuntimeConfig config = MetaverseDedicatedServerRuntimeConfig.LoadDefault();
        return config != null && config.EnableSpawnRouteSmokeTest;
    }

    private IEnumerator RunSmokeFlow()
    {
        EnsureReferences();

        while (spawnManager == null || playerRegistry == null)
        {
            EnsureReferences();
            yield return new WaitForSeconds(0.5f);
        }

        MetaverseRuntimeSpawnTestPrefabInstaller.InstallRuntimeTestPrefab(spawnManager, logMessages);

        if (logMessages)
        {
            Debug.Log("[MetaverseServerSpawnRouteSmokeController] Waiting for first spawn players | phase=33A | mirrorRoute=NetworkServer.Spawn | required=" + spawnWhenPlayerCountAtLeast);
        }

        while (playerRegistry.GetCurrentPlayerCount() < spawnWhenPlayerCountAtLeast)
        {
            yield return new WaitForSeconds(0.5f);
        }

        if (spawnDelayAfterConditionSeconds > 0f) yield return new WaitForSeconds(spawnDelayAfterConditionSeconds);
        firstSpawnedIdentity = TrySpawnTestObject("A", firstSpawnPosition);

        if (logMessages)
        {
            Debug.Log("[MetaverseServerSpawnRouteSmokeController] Waiting for second spawn players | phase=33A | mirrorRoute=NetworkServer.Spawn" +
                      " | required=" + secondSpawnWhenPlayerCountAtLeast +
                      " | current=" + playerRegistry.GetCurrentPlayerCount());
        }

        float secondSpawnStartedAt = Time.realtimeSinceStartup;
        while (playerRegistry.GetCurrentPlayerCount() < secondSpawnWhenPlayerCountAtLeast && Time.realtimeSinceStartup - secondSpawnStartedAt < secondSpawnDelayAfterConditionSeconds)
        {
            yield return new WaitForSeconds(0.5f);
        }

        secondSpawnedIdentity = TrySpawnTestObject("B", secondSpawnPosition);

        float firstDespawnAt = Time.realtimeSinceStartup + Mathf.Max(0f, despawnFirstDelaySeconds);
        float secondDespawnAt = Time.realtimeSinceStartup + Mathf.Max(0f, despawnSecondDelaySeconds);
        bool firstDespawned = false;
        bool secondDespawned = false;

        while (!firstDespawned || !secondDespawned)
        {
            float now = Time.realtimeSinceStartup;

            if (!firstDespawned && now >= firstDespawnAt)
            {
                TryDespawnTestObject(firstSpawnedIdentity, "phase33A_spawn_route_first_completed");
                firstSpawnedIdentity = null;
                firstDespawned = true;
            }

            if (!secondDespawned && now >= secondDespawnAt)
            {
                TryDespawnTestObject(secondSpawnedIdentity, "phase33A_spawn_route_second_completed");
                secondSpawnedIdentity = null;
                secondDespawned = true;
            }

            if (!firstDespawned || !secondDespawned) yield return new WaitForSeconds(0.5f);
        }

        completed = true;
        if (logMessages)
        {
            Debug.Log("[MetaverseServerSpawnRouteSmokeController] Smoke flow completed | phase=33A | expected=NetworkServer.Spawn->game/spawn->NetworkServer.Despawn->game/despawn" +
                      " | issuedSpawnCount=" + issuedSpawnCount +
                      " | issuedDespawnCount=" + issuedDespawnCount +
                      " | summary=" + GetSmokeDebugSummary());
        }
    }

    private MetaverseNetworkIdentity TrySpawnTestObject(string label, Vector3 position)
    {
        EnsureReferences();
        if (spawnManager == null)
        {
            SetRejectReason("spawn_manager_missing");
            Debug.LogWarning("[MetaverseServerSpawnRouteSmokeController] Spawn smoke failed. SpawnManager is missing | label=" + label + " | phase=33A");
            return null;
        }

        GameObject prefab = MetaverseRuntimeSpawnTestPrefabInstaller.InstallRuntimeTestPrefab(spawnManager, logMessages);
        if (prefab == null)
        {
            SetRejectReason("runtime_prefab_missing");
            Debug.LogWarning("[MetaverseServerSpawnRouteSmokeController] Spawn smoke failed. Runtime prefab is missing | label=" + label + " | phase=33A");
            return null;
        }

        MetaverseNetworkIdentity spawnedIdentity;
        bool spawned;

        if (useNetworkServerApi)
        {
            spawned = MetaverseNetworkServer.SpawnPrefab(MetaverseRuntimeSpawnTestPrefabInstaller.TestPrefabId, position, Quaternion.identity, out spawnedIdentity);
        }
        else
        {
            GameObject obj = Instantiate(prefab, position, Quaternion.identity);
            obj.name = "Metaverse_Spawn_Test_Cube_Server_" + label;
            obj.SetActive(true);
            MetaverseNetworkIdentity identity = obj.GetComponent<MetaverseNetworkIdentity>();
            if (identity == null) identity = obj.AddComponent<MetaverseNetworkIdentity>();
            identity.AssignPrefabId(MetaverseRuntimeSpawnTestPrefabInstaller.TestPrefabId);
            spawnedIdentity = spawnManager.Spawn(obj, -1);
            spawned = spawnedIdentity != null;
            if (!spawned) Destroy(obj);
        }

        if (!spawned || spawnedIdentity == null)
        {
            SetRejectReason("spawn_failed");
            Debug.LogWarning("[MetaverseServerSpawnRouteSmokeController] Spawn smoke failed. Spawn returned null | label=" + label +
                             " | phase=33A | managerReject=" + Safe(spawnManager.LastSpawnRejectReason));
            return null;
        }

        spawnedIdentity.gameObject.name = "Metaverse_Spawn_Test_Cube_Server_" + label;
        issuedSpawnCount++;
        SetRejectReason(string.Empty);

        Debug.Log("[MetaverseServerSpawnRouteSmokeController] Smoke spawn issued | phase=33A | mirrorRoute=NetworkServer.SpawnPrefab" +
                  " | label=" + label +
                  " | netId=" + spawnedIdentity.NetId +
                  " | prefabId=" + Safe(spawnedIdentity.PrefabId) +
                  " | currentPlayers=" + (playerRegistry != null ? playerRegistry.GetCurrentPlayerCount() : -1) +
                  " | expectedOutgoingRoute=game/spawn");
        return spawnedIdentity;
    }

    private void TryDespawnTestObject(MetaverseNetworkIdentity identity, string reason)
    {
        if (identity == null) return;
        int netId = identity.NetId;
        if (useNetworkServerApi) MetaverseNetworkServer.Despawn(identity, reason);
        else if (spawnManager != null) spawnManager.Despawn(identity.gameObject, reason);

        issuedDespawnCount++;
        Debug.Log("[MetaverseServerSpawnRouteSmokeController] Smoke despawn issued | phase=33A | mirrorRoute=NetworkServer.Despawn" +
                  " | netId=" + netId +
                  " | reason=" + Safe(reason) +
                  " | expectedOutgoingRoute=game/despawn");
    }

    public string GetSmokeDebugSummary()
    {
        return "Phase33A ServerSpawnRouteSmoke" +
               " | completed=" + completed +
               " | spawns=" + issuedSpawnCount +
               " | despawns=" + issuedDespawnCount +
               " | spawnedCount=" + (spawnManager != null ? spawnManager.SpawnedCount : -1) +
               " | players=" + (playerRegistry != null ? playerRegistry.GetCurrentPlayerCount() : -1) +
               " | lastReject=" + Safe(lastRejectReason);
    }

    private void EnsureReferences()
    {
        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;

        if (playerRegistry == null)
        {
#if UNITY_2023_1_OR_NEWER
            playerRegistry = FindFirstObjectByType<DedicatedPlayerRegistry>();
#else
            playerRegistry = FindObjectOfType<DedicatedPlayerRegistry>();
#endif
        }
    }

    private void SetRejectReason(string reason)
    {
        lastRejectReason = Safe(reason);
    }

    private string Safe(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
