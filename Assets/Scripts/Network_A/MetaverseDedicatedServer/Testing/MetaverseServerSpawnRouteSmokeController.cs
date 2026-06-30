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
    private MetaverseNetworkIdentity firstSpawnedIdentity;
    private MetaverseNetworkIdentity secondSpawnedIdentity;

    public void Bind(MetaverseSpawnManager manager)
    {
        spawnManager = manager;
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
            Debug.Log("[MetaverseServerSpawnRouteSmokeController] Waiting for first spawn players | required=" + spawnWhenPlayerCountAtLeast);
        }

        while (playerRegistry.GetCurrentPlayerCount() < spawnWhenPlayerCountAtLeast)
        {
            yield return new WaitForSeconds(0.5f);
        }

        if (spawnDelayAfterConditionSeconds > 0f)
        {
            yield return new WaitForSeconds(spawnDelayAfterConditionSeconds);
        }

        firstSpawnedIdentity = TrySpawnTestObject("A", firstSpawnPosition);

        if (logMessages)
        {
            Debug.Log("[MetaverseServerSpawnRouteSmokeController] Waiting for second spawn players | required=" + secondSpawnWhenPlayerCountAtLeast +
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
                TryDespawnTestObject(firstSpawnedIdentity, "spawn_route_smoke_test_first_completed");
                firstSpawnedIdentity = null;
                firstDespawned = true;
            }

            if (!secondDespawned && now >= secondDespawnAt)
            {
                TryDespawnTestObject(secondSpawnedIdentity, "spawn_route_smoke_test_second_completed");
                secondSpawnedIdentity = null;
                secondDespawned = true;
            }

            if (!firstDespawned || !secondDespawned)
            {
                yield return new WaitForSeconds(0.5f);
            }
        }

        completed = true;
        if (logMessages)
        {
            Debug.Log("[MetaverseServerSpawnRouteSmokeController] Smoke flow completed | expectedRoutes=game/spawn,game/snapshot,game/despawn");
        }
    }

    private MetaverseNetworkIdentity TrySpawnTestObject(string label, Vector3 position)
    {
        EnsureReferences();
        if (spawnManager == null)
        {
            Debug.LogWarning("[MetaverseServerSpawnRouteSmokeController] Spawn smoke failed. SpawnManager is missing | label=" + label);
            return null;
        }

        GameObject prefab = MetaverseRuntimeSpawnTestPrefabInstaller.InstallRuntimeTestPrefab(spawnManager, logMessages);
        if (prefab == null)
        {
            Debug.LogWarning("[MetaverseServerSpawnRouteSmokeController] Spawn smoke failed. Runtime prefab is missing | label=" + label);
            return null;
        }

        GameObject obj = Instantiate(prefab, position, Quaternion.identity);
        obj.name = "Metaverse_Spawn_Test_Cube_Server_" + label;
        obj.SetActive(true);

        MetaverseNetworkIdentity identity = obj.GetComponent<MetaverseNetworkIdentity>();
        if (identity == null) identity = obj.AddComponent<MetaverseNetworkIdentity>();
        identity.AssignPrefabId(MetaverseRuntimeSpawnTestPrefabInstaller.TestPrefabId);

        MetaverseNetworkIdentity spawnedIdentity = spawnManager.Spawn(obj, -1);

        if (spawnedIdentity == null)
        {
            Debug.LogWarning("[MetaverseServerSpawnRouteSmokeController] Spawn smoke failed. SpawnManager returned null | label=" + label);
            Destroy(obj);
            return null;
        }

        Debug.Log("[MetaverseServerSpawnRouteSmokeController] Smoke spawn issued | label=" + label +
                  " | netId=" + spawnedIdentity.NetId +
                  " | prefabId=" + spawnedIdentity.PrefabId +
                  " | currentPlayers=" + (playerRegistry != null ? playerRegistry.GetCurrentPlayerCount() : -1) +
                  " | expectedOutgoingRoute=game/spawn");
        return spawnedIdentity;
    }

    private void TryDespawnTestObject(MetaverseNetworkIdentity identity, string reason)
    {
        if (spawnManager == null || identity == null)
        {
            return;
        }

        int netId = identity.NetId;
        spawnManager.Despawn(identity.gameObject, reason);

        Debug.Log("[MetaverseServerSpawnRouteSmokeController] Smoke despawn issued | netId=" + netId +
                  " | reason=" + reason +
                  " | expectedOutgoingRoute=game/despawn");
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
}
