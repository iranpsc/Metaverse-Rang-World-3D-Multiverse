using System.Collections;
using Network_A.GameServer.Players;
using UnityEngine;

public class MetaverseNetworkRpcSmokeController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MetaverseSpawnManager spawnManager;
    [SerializeField] private DedicatedPlayerRegistry playerRegistry;

    [Header("Smoke Test")]
    [SerializeField] private string prefabId = MetaverseNetworkRpcSmokePrefabInstaller.DefaultPrefabId;
    [SerializeField] private int spawnRequiredPlayers = 1;
    [SerializeField] private int snapshotRequiredPlayers = 3;
    [SerializeField] private float initialDelaySeconds = 5f;
    [SerializeField] private float minimumAliveSeconds = 30f;
    [SerializeField] private float despawnDelayAfterSnapshotSeconds = 10f;

    [Header("Debug")]
    [SerializeField] private bool logMessages = true;

    private bool started;
    private MetaverseNetworkIdentity spawnedIdentity;

    public void Bind(MetaverseSpawnManager manager, MetaverseDedicatedServerRuntimeConfig config)
    {
        spawnManager = manager;
        ApplyConfig(config);
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
        if (started) return;
        if (!Application.isBatchMode) return;
        started = true;
        StartCoroutine(RunFlow());
    }

    private IEnumerator RunFlow()
    {
        EnsureReferences();

        while (spawnManager == null || playerRegistry == null)
        {
            EnsureReferences();
            yield return new WaitForSeconds(0.5f);
        }

        MetaverseNetworkRpcSmokePrefabInstaller.InstallRuntimeRpcProbePrefab(spawnManager, prefabId, logMessages);

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkRpcSmokeController] Waiting for spawn players | required=" + spawnRequiredPlayers);
        }

        while (playerRegistry.GetCurrentPlayerCount() < Mathf.Max(1, spawnRequiredPlayers))
        {
            yield return new WaitForSeconds(1f);
        }

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkRpcSmokeController] Spawn player target reached | required=" + spawnRequiredPlayers +
                      " | current=" + playerRegistry.GetCurrentPlayerCount());
        }

        yield return new WaitForSeconds(Mathf.Max(0f, initialDelaySeconds));

        bool spawned = spawnManager.TrySpawnPrefab(
            string.IsNullOrWhiteSpace(prefabId) ? MetaverseNetworkRpcSmokePrefabInstaller.DefaultPrefabId : prefabId.Trim(),
            new Vector3(-3f, 1.5f, 0f),
            Quaternion.identity,
            -1,
            out spawnedIdentity);

        if (!spawned || spawnedIdentity == null)
        {
            Debug.LogWarning("[MetaverseNetworkRpcSmokeController] Smoke spawn failed | prefabId=" + prefabId);
            yield break;
        }

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkRpcSmokeController] Smoke spawn issued | netId=" + spawnedIdentity.NetId +
                      " | prefabId=" + spawnedIdentity.PrefabId +
                      " | expectedRoutes=game/command,game/client_rpc,game/target_rpc | snapshotRequiredPlayers=" + snapshotRequiredPlayers);
        }

        float aliveStartedAt = Time.realtimeSinceStartup;

        while (playerRegistry.GetCurrentPlayerCount() < Mathf.Max(spawnRequiredPlayers, snapshotRequiredPlayers))
        {
            if (logMessages)
            {
                Debug.Log("[MetaverseNetworkRpcSmokeController] Waiting for snapshot players | required=" + snapshotRequiredPlayers +
                          " | current=" + playerRegistry.GetCurrentPlayerCount() +
                          " | activeNetId=" + (spawnedIdentity != null ? spawnedIdentity.NetId : 0));
            }
            yield return new WaitForSeconds(5f);
        }

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkRpcSmokeController] Snapshot player target reached | required=" + snapshotRequiredPlayers +
                      " | current=" + playerRegistry.GetCurrentPlayerCount() +
                      " | expectedSnapshotCommand=game/command");
        }

        while (Time.realtimeSinceStartup - aliveStartedAt < Mathf.Max(1f, minimumAliveSeconds))
        {
            yield return new WaitForSeconds(1f);
        }

        yield return new WaitForSeconds(Mathf.Max(0f, despawnDelayAfterSnapshotSeconds));

        if (spawnedIdentity != null && spawnedIdentity.gameObject != null)
        {
            int netId = spawnedIdentity.NetId;
            spawnManager.Despawn(spawnedIdentity.gameObject, "network_rpc_smoke_completed");
            if (logMessages)
            {
                Debug.Log("[MetaverseNetworkRpcSmokeController] Smoke despawn issued | netId=" + netId +
                          " | expectedRoute=game/despawn");
            }
        }

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkRpcSmokeController] Smoke flow completed | phase=20 | expected=command_clientrpc_targetrpc");
        }
    }

    private void ApplyConfig(MetaverseDedicatedServerRuntimeConfig config)
    {
        if (config == null) return;
        prefabId = config.NetworkRpcSmokePrefabId;
        spawnRequiredPlayers = config.NetworkRpcSmokeSpawnRequiredPlayers;
        snapshotRequiredPlayers = config.NetworkRpcSmokeSnapshotRequiredPlayers;
        initialDelaySeconds = config.NetworkRpcSmokeInitialDelaySeconds;
        minimumAliveSeconds = config.NetworkRpcSmokeMinimumAliveSeconds;
        despawnDelayAfterSnapshotSeconds = config.NetworkRpcSmokeDespawnDelayAfterSnapshotSeconds;
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
