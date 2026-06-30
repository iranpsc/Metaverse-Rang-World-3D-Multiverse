using UnityEngine;

public class MetaverseClientSpawnRouteSmokeReporter : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MetaverseSpawnManager spawnManager;

    [Header("Debug")]
    [SerializeField] private bool logMessages = true;

    private bool subscribed;
    private bool reporterEnabled;
    private int observedSpawnCount;
    private int observedDespawnCount;

    public void Bind(MetaverseSpawnManager manager)
    {
        reporterEnabled = IsReporterEnabled();
        if (!reporterEnabled) return;
        if (spawnManager == manager && subscribed) return;
        Unsubscribe();
        spawnManager = manager;
        Subscribe();
    }

    private void Awake()
    {
        reporterEnabled = IsReporterEnabled();
        if (!reporterEnabled) return;
        EnsureReferences();
    }

    private void OnEnable()
    {
        reporterEnabled = IsReporterEnabled();
        if (!reporterEnabled) return;
        EnsureReferences();
        Subscribe();
    }

    private void OnDisable()
    {
        Unsubscribe();
    }

    private void OnDestroy()
    {
        Unsubscribe();
    }

    private void Update()
    {
        if (!reporterEnabled) return;
        if (spawnManager != null && subscribed) return;
        EnsureReferences();
        Subscribe();
    }

    private bool IsReporterEnabled()
    {
        MetaverseDedicatedServerRuntimeConfig config = MetaverseDedicatedServerRuntimeConfig.LoadDefault();
        return config != null && config.EnableClientSpawnRouteSmokeReporter;
    }

    private void EnsureReferences()
    {
        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
    }

    private void Subscribe()
    {
        if (subscribed || spawnManager == null) return;
        spawnManager.ClientObjectSpawned -= HandleClientObjectSpawned;
        spawnManager.ClientObjectDespawned -= HandleClientObjectDespawned;
        spawnManager.ClientObjectSpawned += HandleClientObjectSpawned;
        spawnManager.ClientObjectDespawned += HandleClientObjectDespawned;
        subscribed = true;

        if (logMessages)
        {
            Debug.Log("[MetaverseClientSpawnRouteSmokeReporter] Bound | spawnedCount=" + spawnManager.SpawnedCount);
        }
    }

    private void Unsubscribe()
    {
        if (spawnManager != null)
        {
            spawnManager.ClientObjectSpawned -= HandleClientObjectSpawned;
            spawnManager.ClientObjectDespawned -= HandleClientObjectDespawned;
        }

        subscribed = false;
    }

    private void HandleClientObjectSpawned(MetaverseNetworkIdentity identity, MetaverseSpawnPayload payload)
    {
        observedSpawnCount++;
        if (!logMessages) return;

        Debug.Log("[MetaverseClientSpawnRouteSmokeReporter] Client spawn observed | observedSpawnCount=" + observedSpawnCount +
                  " | activeSpawnedCount=" + (spawnManager != null ? spawnManager.SpawnedCount : -1) +
                  " | netId=" + (payload != null ? payload.netId : 0) +
                  " | prefabId=" + (payload != null ? payload.prefabId : string.Empty) +
                  " | objectName=" + (identity != null ? identity.name : "null"));
    }

    private void HandleClientObjectDespawned(int netId, string reason)
    {
        observedDespawnCount++;
        if (!logMessages) return;

        Debug.Log("[MetaverseClientSpawnRouteSmokeReporter] Client despawn observed | observedDespawnCount=" + observedDespawnCount +
                  " | activeSpawnedCount=" + (spawnManager != null ? spawnManager.SpawnedCount : -1) +
                  " | netId=" + netId +
                  " | reason=" + (string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim()));
    }
}
