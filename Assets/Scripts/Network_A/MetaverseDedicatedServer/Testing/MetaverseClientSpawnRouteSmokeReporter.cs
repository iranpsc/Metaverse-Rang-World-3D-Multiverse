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
    private int observedPhase33ASpawnCount;
    private int observedPhase33ADespawnCount;
    private string lastObservedRoute = string.Empty;
    private string lastObservedPrefabId = string.Empty;
    private int lastObservedNetId;

    public int ObservedSpawnCount => observedSpawnCount;
    public int ObservedDespawnCount => observedDespawnCount;
    public int ObservedPhase33ASpawnCount => observedPhase33ASpawnCount;
    public int ObservedPhase33ADespawnCount => observedPhase33ADespawnCount;
    public string LastObservedRoute => lastObservedRoute;
    public int LastObservedNetId => lastObservedNetId;

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
            Debug.Log("[MetaverseClientSpawnRouteSmokeReporter] Bound | phase=33A | mirrorRoute=ClientSpawnObserver" +
                      " | spawnedCount=" + spawnManager.SpawnedCount);
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
        lastObservedRoute = payload != null ? Safe(payload.mirrorRoute) : "ClientApplySpawn";
        lastObservedPrefabId = payload != null ? Safe(payload.prefabId) : string.Empty;
        lastObservedNetId = payload != null ? payload.netId : (identity != null ? identity.NetId : 0);

        if (IsPhase33APayload(payload)) observedPhase33ASpawnCount++;
        if (!logMessages) return;

        Debug.Log("[MetaverseClientSpawnRouteSmokeReporter] Client spawn observed | phase=33A | mirrorRoute=ClientApplySpawn" +
                  " | observedSpawnCount=" + observedSpawnCount +
                  " | phase33ASpawnCount=" + observedPhase33ASpawnCount +
                  " | activeSpawnedCount=" + (spawnManager != null ? spawnManager.SpawnedCount : -1) +
                  " | netId=" + lastObservedNetId +
                  " | prefabId=" + lastObservedPrefabId +
                  " | payloadRoute=" + lastObservedRoute +
                  " | objectName=" + (identity != null ? identity.name : "null"));
    }

    private void HandleClientObjectDespawned(int netId, string reason)
    {
        observedDespawnCount++;
        observedPhase33ADespawnCount++;
        lastObservedRoute = "ClientApplyDespawn";
        lastObservedNetId = netId;
        if (!logMessages) return;

        Debug.Log("[MetaverseClientSpawnRouteSmokeReporter] Client despawn observed | phase=33A | mirrorRoute=ClientApplyDespawn" +
                  " | observedDespawnCount=" + observedDespawnCount +
                  " | phase33ADespawnCount=" + observedPhase33ADespawnCount +
                  " | activeSpawnedCount=" + (spawnManager != null ? spawnManager.SpawnedCount : -1) +
                  " | netId=" + netId +
                  " | reason=" + Safe(reason));
    }

    public string GetSmokeDebugSummary()
    {
        return "Phase33A ClientSpawnRouteReporter" +
               " | enabled=" + reporterEnabled +
               " | subscribed=" + subscribed +
               " | spawns=" + observedSpawnCount +
               " | despawns=" + observedDespawnCount +
               " | phase33ASpawns=" + observedPhase33ASpawnCount +
               " | phase33ADespawns=" + observedPhase33ADespawnCount +
               " | lastNetId=" + lastObservedNetId +
               " | lastRoute=" + Safe(lastObservedRoute) +
               " | lastPrefabId=" + Safe(lastObservedPrefabId);
    }

    private bool IsPhase33APayload(MetaverseSpawnPayload payload)
    {
        if (payload == null) return false;
        if (!string.IsNullOrWhiteSpace(payload.mirrorRoute)) return true;
        if (!string.IsNullOrWhiteSpace(payload.spawnReason) && payload.spawnReason.Contains("phase33A")) return true;
        return false;
    }

    private string Safe(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
