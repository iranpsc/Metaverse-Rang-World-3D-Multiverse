using System.Collections;
using Network_A.GameServer.Players;
using UnityEngine;

public class MetaverseNetworkBehaviourLifecycleSmokeController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MetaverseSpawnManager spawnManager;

    [Header("Smoke Test")]
    [SerializeField] private string prefabId = MetaverseNetworkBehaviourSmokePrefabInstaller.DefaultPrefabId;
    [SerializeField] private bool useNetworkServerApi = true;
    [SerializeField] private int requiredPlayersBeforeSpawn = 1;
    [SerializeField] private int requiredPlayersBeforeSnapshot = 3;
    [SerializeField] private float initialDelaySeconds = 5f;
    [SerializeField] private float minimumAliveSeconds = 25f;
    [SerializeField] private float maxWaitBeforeSpawnSeconds = 120f;
    [SerializeField] private float maxSnapshotWaitSeconds = 180f;
    [SerializeField] private float despawnDelayAfterSnapshotPlayersSeconds = 8f;

    [Header("Debug")]
    [SerializeField] private bool logMessages = true;

    private bool started;
    private bool snapshotPlayerTargetLogged;
    private string lastRejectReason = string.Empty;
    private MetaverseNetworkIdentity spawnedIdentity;

    public bool IsStarted => started;
    public bool SnapshotPlayerTargetLogged => snapshotPlayerTargetLogged;
    public string LastRejectReason => lastRejectReason;
    public MetaverseNetworkIdentity SpawnedIdentity => spawnedIdentity;

    public void Bind(MetaverseSpawnManager manager)
    {
        spawnManager = manager;
        TryStart();
    }

    public void Bind(MetaverseSpawnManager manager, MetaverseDedicatedServerRuntimeConfig config)
    {
        spawnManager = manager;
        ConfigureFromConfig(config);
        TryStart();
    }

    public void ConfigureFromConfig(MetaverseDedicatedServerRuntimeConfig config)
    {
        if (config == null) return;

        prefabId = config.NetworkBehaviourLifecycleSmokePrefabId;
        requiredPlayersBeforeSpawn = config.NetworkBehaviourLifecycleSmokeSpawnRequiredPlayers;
        requiredPlayersBeforeSnapshot = config.NetworkBehaviourLifecycleSmokeSnapshotRequiredPlayers;
        initialDelaySeconds = config.NetworkBehaviourLifecycleSmokeInitialDelaySeconds;
        minimumAliveSeconds = config.NetworkBehaviourLifecycleSmokeMinimumAliveSeconds;
        maxWaitBeforeSpawnSeconds = config.NetworkBehaviourLifecycleSmokeMaxWaitBeforeSpawnSeconds;
        maxSnapshotWaitSeconds = config.NetworkBehaviourLifecycleSmokeMaxSnapshotWaitSeconds;
        despawnDelayAfterSnapshotPlayersSeconds = config.NetworkBehaviourLifecycleSmokeDespawnDelayAfterSnapshotSeconds;
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
        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
        if (spawnManager == null) return;
        started = true;
        StartCoroutine(RunFlow());
    }

    private IEnumerator RunFlow()
    {
        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
        if (spawnManager == null)
        {
            SetRejectReason("spawn_manager_missing");
            Debug.LogWarning("[MetaverseNetworkBehaviourLifecycleSmokeController] Smoke skipped. Spawn manager is missing. | phase=33A");
            yield break;
        }

        string safePrefabId = string.IsNullOrWhiteSpace(prefabId) ? MetaverseNetworkBehaviourSmokePrefabInstaller.DefaultPrefabId : prefabId.Trim();
        MetaverseNetworkBehaviourSmokePrefabInstaller.InstallRuntimeProbePrefab(spawnManager, safePrefabId, logMessages);

        int spawnRequiredPlayers = Mathf.Max(1, requiredPlayersBeforeSpawn);
        int snapshotRequiredPlayers = Mathf.Max(spawnRequiredPlayers, requiredPlayersBeforeSnapshot);

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkBehaviourLifecycleSmokeController] Waiting for spawn players | phase=33A | mirrorRoute=NetworkServer.SpawnPrefab" +
                      " | required=" + spawnRequiredPlayers);
        }

        float spawnWaitStartedAt = Time.realtimeSinceStartup;
        float lastSpawnWaitLogAt = Time.realtimeSinceStartup;
        while (GetCurrentPlayers() < spawnRequiredPlayers)
        {
            if (maxWaitBeforeSpawnSeconds > 0f && Time.realtimeSinceStartup - spawnWaitStartedAt > maxWaitBeforeSpawnSeconds)
            {
                SetRejectReason("spawn_wait_timeout");
                Debug.LogWarning("[MetaverseNetworkBehaviourLifecycleSmokeController] Spawn wait timed out | phase=33A | required=" + spawnRequiredPlayers + " | current=" + GetCurrentPlayers());
                yield break;
            }

            if (logMessages && Time.realtimeSinceStartup - lastSpawnWaitLogAt >= 30f)
            {
                lastSpawnWaitLogAt = Time.realtimeSinceStartup;
                Debug.Log("[MetaverseNetworkBehaviourLifecycleSmokeController] Still waiting for spawn players | phase=33A" +
                          " | required=" + spawnRequiredPlayers +
                          " | current=" + GetCurrentPlayers());
            }

            yield return new WaitForSeconds(1f);
        }

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkBehaviourLifecycleSmokeController] Spawn player target reached | phase=33A | required=" + spawnRequiredPlayers +
                      " | current=" + GetCurrentPlayers());
        }

        yield return new WaitForSeconds(Mathf.Max(0f, initialDelaySeconds));

        bool spawned;
        if (useNetworkServerApi)
        {
            spawned = MetaverseNetworkServer.SpawnPrefab(safePrefabId, new Vector3(3f, 1.5f, 0f), Quaternion.identity, out spawnedIdentity);
        }
        else
        {
            spawned = spawnManager.TrySpawnPrefab(safePrefabId, new Vector3(3f, 1.5f, 0f), Quaternion.identity, -1, out spawnedIdentity);
        }

        if (!spawned || spawnedIdentity == null)
        {
            SetRejectReason("spawn_failed");
            Debug.LogWarning("[MetaverseNetworkBehaviourLifecycleSmokeController] Smoke spawn failed | phase=33A | prefabId=" + safePrefabId +
                             " | managerReject=" + (spawnManager != null ? spawnManager.LastSpawnRejectReason : string.Empty));
            yield break;
        }

        float spawnedAt = Time.realtimeSinceStartup;
        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkBehaviourLifecycleSmokeController] Smoke spawn issued | phase=33A | mirrorRoute=NetworkServer.SpawnPrefab" +
                      " | netId=" + spawnedIdentity.NetId +
                      " | prefabId=" + spawnedIdentity.PrefabId +
                      " | expectedCallbacks=OnStartServer,OnStartAuthority,OnNetworkSpawn,OnStartClient" +
                      " | snapshotRequiredPlayers=" + snapshotRequiredPlayers);
        }

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkBehaviourLifecycleSmokeController] Waiting for snapshot players | phase=33A | required=" + snapshotRequiredPlayers +
                      " | current=" + GetCurrentPlayers() +
                      " | activeNetId=" + spawnedIdentity.NetId);
        }

        float snapshotWaitStartedAt = Time.realtimeSinceStartup;
        float lastSnapshotWaitLogAt = Time.realtimeSinceStartup;
        while (spawnedIdentity != null && spawnedIdentity.gameObject != null && GetCurrentPlayers() < snapshotRequiredPlayers)
        {
            if (maxSnapshotWaitSeconds > 0f && Time.realtimeSinceStartup - snapshotWaitStartedAt > maxSnapshotWaitSeconds)
            {
                Debug.LogWarning("[MetaverseNetworkBehaviourLifecycleSmokeController] Snapshot wait timed out | phase=33A | required=" + snapshotRequiredPlayers + " | current=" + GetCurrentPlayers());
                break;
            }

            if (logMessages && Time.realtimeSinceStartup - lastSnapshotWaitLogAt >= 15f)
            {
                lastSnapshotWaitLogAt = Time.realtimeSinceStartup;
                Debug.Log("[MetaverseNetworkBehaviourLifecycleSmokeController] Still waiting for snapshot players | phase=33A" +
                          " | required=" + snapshotRequiredPlayers +
                          " | current=" + GetCurrentPlayers() +
                          " | activeNetId=" + spawnedIdentity.NetId);
            }

            yield return new WaitForSeconds(1f);
        }

        bool snapshotPlayerTargetReached = spawnedIdentity != null && spawnedIdentity.gameObject != null && GetCurrentPlayers() >= snapshotRequiredPlayers;
        if (snapshotPlayerTargetReached)
        {
            snapshotPlayerTargetLogged = true;
            if (logMessages)
            {
                Debug.Log("[MetaverseNetworkBehaviourLifecycleSmokeController] Snapshot player target reached | phase=33A" +
                          " | required=" + snapshotRequiredPlayers +
                          " | current=" + GetCurrentPlayers() +
                          " | activeNetId=" + spawnedIdentity.NetId +
                          " | expectedClientSnapshotCallbacks=OnStartClient,OnNetworkSpawn");
            }
        }
        else
        {
            Debug.LogWarning("[MetaverseNetworkBehaviourLifecycleSmokeController] Snapshot player target was not reached | phase=33A" +
                             " | required=" + snapshotRequiredPlayers +
                             " | current=" + GetCurrentPlayers());
        }

        float aliveElapsed = Time.realtimeSinceStartup - spawnedAt;
        float remainingMinimumAlive = Mathf.Max(0f, minimumAliveSeconds - aliveElapsed);
        if (remainingMinimumAlive > 0f)
        {
            if (logMessages)
            {
                Debug.Log("[MetaverseNetworkBehaviourLifecycleSmokeController] Holding probe for minimum alive time | phase=33A" +
                          " | remainingSeconds=" + remainingMinimumAlive.ToString("0.0") +
                          " | activeNetId=" + spawnedIdentity.NetId);
            }

            yield return new WaitForSeconds(remainingMinimumAlive);
        }

        if (snapshotPlayerTargetReached) yield return new WaitForSeconds(Mathf.Max(0f, despawnDelayAfterSnapshotPlayersSeconds));

        if (spawnedIdentity != null && spawnedIdentity.gameObject != null)
        {
            int netId = spawnedIdentity.NetId;
            if (useNetworkServerApi) MetaverseNetworkServer.Despawn(spawnedIdentity, "phase33A_network_behaviour_lifecycle_smoke_completed");
            else spawnManager.Despawn(spawnedIdentity.gameObject, "phase33A_network_behaviour_lifecycle_smoke_completed");

            if (logMessages)
            {
                Debug.Log("[MetaverseNetworkBehaviourLifecycleSmokeController] Smoke despawn issued | phase=33A | mirrorRoute=NetworkServer.Despawn" +
                          " | netId=" + netId +
                          " | snapshotPlayerTargetReached=" + BoolText(snapshotPlayerTargetReached) +
                          " | expectedCallbacks=OnNetworkDespawn,OnStopAuthority,OnStopServer,OnStopClient");
            }
        }

        SetRejectReason(string.Empty);
        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkBehaviourLifecycleSmokeController] Smoke flow completed | phase=33A | expected=NetworkBehaviour lifecycle callbacks" +
                      " | snapshotPlayerTargetReached=" + BoolText(snapshotPlayerTargetLogged) +
                      " | summary=" + GetSmokeDebugSummary());
        }
    }

    public string GetSmokeDebugSummary()
    {
        MetaverseNetworkBehaviourLifecycleProbe probe = spawnedIdentity != null ? spawnedIdentity.GetComponent<MetaverseNetworkBehaviourLifecycleProbe>() : null;
        return "Phase33A NetworkBehaviourLifecycleSmoke" +
               " | started=" + started +
               " | activeNetId=" + (spawnedIdentity != null ? spawnedIdentity.NetId : 0) +
               " | snapshotTargetLogged=" + snapshotPlayerTargetLogged +
               " | players=" + GetCurrentPlayers() +
               " | lastReject=" + Safe(lastRejectReason) +
               " | probe=" + (probe != null ? probe.GetSmokeDebugSummary() : "null");
    }

    private int GetCurrentPlayers()
    {
#if UNITY_2023_1_OR_NEWER
        DedicatedPlayerRegistry registry = Object.FindFirstObjectByType<DedicatedPlayerRegistry>();
#else
        DedicatedPlayerRegistry registry = Object.FindObjectOfType<DedicatedPlayerRegistry>();
#endif
        return registry != null ? registry.CurrentPlayerCount : 0;
    }

    private void SetRejectReason(string reason)
    {
        lastRejectReason = Safe(reason);
    }

    private string BoolText(bool value)
    {
        return value ? "true" : "false";
    }

    private string Safe(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
