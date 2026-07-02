using System.Collections;
using Network_A.GameServer.Players;
using UnityEngine;

public class MetaverseNetworkStateSyncSmokeController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MetaverseSpawnManager spawnManager;
    [SerializeField] private DedicatedPlayerRegistry playerRegistry;
    [SerializeField] private MetaverseNetworkStateSyncBridge stateSyncBridge;

    [Header("Smoke Test")]
    [SerializeField] private string prefabId = MetaverseNetworkStateSyncSmokePrefabInstaller.DefaultPrefabId;
    [SerializeField] private int spawnRequiredPlayers = 1;
    [SerializeField] private int snapshotRequiredPlayers = 3;
    [SerializeField] private float initialDelaySeconds = 5f;
    [SerializeField] private float minimumAliveSeconds = 30f;
    [SerializeField] private float updateDelayAfterSnapshotSeconds = 3f;
    [SerializeField] private float despawnDelayAfterSnapshotSeconds = 10f;

    [Header("Phase 33A Mirror-Like API")]
    [SerializeField] private bool useNetworkServerApiForSpawn = true;
    [SerializeField] private bool useNetworkServerApiForStateSync = true;
    [SerializeField] private bool useNetworkServerApiForDespawn = true;
    [SerializeField] private string phaseName = "33A";
    [SerializeField] private string mirrorLikeSyncKey = "phase33A_status";
    [SerializeField] private string legacySyncKey = "phase21_status";
    [SerializeField] private bool sendLegacyPhase21SyncVar = true;

    [Header("Debug")]
    [SerializeField] private bool logMessages = true;

    private bool started;
    private bool spawnIssued;
    private bool firstUpdateIssued;
    private bool secondUpdateIssued;
    private bool despawnIssued;
    private int syncVarPushCount;
    private int transformPushCount;
    private string lastStatus = string.Empty;
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
        while (spawnManager == null || playerRegistry == null || stateSyncBridge == null)
        {
            EnsureReferences();
            yield return new WaitForSeconds(0.5f);
        }

        MetaverseNetworkStateSyncSmokePrefabInstaller.InstallRuntimeStateSyncProbePrefab(spawnManager, prefabId, logMessages);

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkStateSyncSmokeController] Phase 33A waiting for spawn players" +
                      " | required=" + spawnRequiredPlayers +
                      " | api=NetworkServer.Spawn->SyncVar->SyncTransform");
        }

        while (playerRegistry.GetCurrentPlayerCount() < Mathf.Max(1, spawnRequiredPlayers)) yield return new WaitForSeconds(1f);

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkStateSyncSmokeController] Phase 33A spawn player target reached" +
                      " | required=" + spawnRequiredPlayers +
                      " | current=" + playerRegistry.GetCurrentPlayerCount());
        }

        yield return new WaitForSeconds(Mathf.Max(0f, initialDelaySeconds));

        if (!SpawnSmokeObject()) yield break;

        float aliveStartedAt = Time.realtimeSinceStartup;
        yield return new WaitForSeconds(2f);

        firstUpdateIssued = PushSyncAndTransform("first_update_after_spawn", new Vector3(3f, 2.25f, 0f));

        while (playerRegistry.GetCurrentPlayerCount() < Mathf.Max(spawnRequiredPlayers, snapshotRequiredPlayers))
        {
            if (logMessages)
            {
                Debug.Log("[MetaverseNetworkStateSyncSmokeController] Phase 33A waiting for snapshot players" +
                          " | required=" + snapshotRequiredPlayers +
                          " | current=" + playerRegistry.GetCurrentPlayerCount() +
                          " | activeNetId=" + (spawnedIdentity != null ? spawnedIdentity.NetId : 0));
            }
            yield return new WaitForSeconds(5f);
        }

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkStateSyncSmokeController] Phase 33A snapshot player target reached" +
                      " | required=" + snapshotRequiredPlayers +
                      " | current=" + playerRegistry.GetCurrentPlayerCount() +
                      " | expectedRoutes=game/sync_var,game/network_transform");
        }

        yield return new WaitForSeconds(Mathf.Max(0f, updateDelayAfterSnapshotSeconds));
        secondUpdateIssued = PushSyncAndTransform("second_update_after_snapshot", new Vector3(4f, 2.75f, 1f));

        while (Time.realtimeSinceStartup - aliveStartedAt < Mathf.Max(1f, minimumAliveSeconds)) yield return new WaitForSeconds(1f);
        yield return new WaitForSeconds(Mathf.Max(0f, despawnDelayAfterSnapshotSeconds));

        DespawnSmokeObject("network_state_sync_phase33A_smoke_completed");

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkStateSyncSmokeController] Phase 33A smoke flow completed" +
                      " | expected=NetworkServer.SetSyncVar->NetworkServer.SyncTransform" +
                      " | " + GetSmokeDebugSummary());
        }
    }

    private bool SpawnSmokeObject()
    {
        string safePrefabId = string.IsNullOrWhiteSpace(prefabId) ? MetaverseNetworkStateSyncSmokePrefabInstaller.DefaultPrefabId : prefabId.Trim();
        Vector3 spawnPosition = new Vector3(3f, 1.5f, 0f);
        Quaternion spawnRotation = Quaternion.identity;
        bool spawned;

        if (useNetworkServerApiForSpawn)
        {
            spawned = MetaverseNetworkServer.SpawnPrefab(safePrefabId, spawnPosition, spawnRotation, out spawnedIdentity);
        }
        else
        {
            spawned = spawnManager.TrySpawnPrefab(safePrefabId, spawnPosition, spawnRotation, -1, out spawnedIdentity);
        }

        spawnIssued = spawned && spawnedIdentity != null;

        if (!spawnIssued)
        {
            Debug.LogWarning("[MetaverseNetworkStateSyncSmokeController] Phase 33A smoke spawn failed" +
                             " | prefabId=" + safePrefabId +
                             " | useNetworkServerApi=" + BoolText(useNetworkServerApiForSpawn));
            return false;
        }

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkStateSyncSmokeController] Phase 33A smoke spawn issued" +
                      " | netId=" + spawnedIdentity.NetId +
                      " | prefabId=" + spawnedIdentity.PrefabId +
                      " | api=" + (useNetworkServerApiForSpawn ? "NetworkServer.SpawnPrefab" : "SpawnManager.TrySpawnPrefab") +
                      " | expectedRoutes=game/sync_var,game/network_transform" +
                      " | snapshotRequiredPlayers=" + snapshotRequiredPlayers);
        }

        return true;
    }

    private bool PushSyncAndTransform(string status, Vector3 position)
    {
        if (spawnedIdentity == null || stateSyncBridge == null) return false;

        lastStatus = SafeText(status);
        string statusJson = BuildStatusJson(status, position);
        bool mirrorSyncSent = SendSyncVar(mirrorLikeSyncKey, statusJson);
        bool legacySyncSent = true;

        if (sendLegacyPhase21SyncVar) legacySyncSent = SendSyncVar(legacySyncKey, statusJson);

        spawnedIdentity.transform.position = position;
        spawnedIdentity.transform.rotation = Quaternion.Euler(0f, 45f, 0f);
        spawnedIdentity.transform.localScale = new Vector3(1.25f, 1.25f, 1.25f);

        bool transformSent = SendNetworkTransform();
        bool success = mirrorSyncSent && legacySyncSent && transformSent;

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkStateSyncSmokeController] Phase 33A state sync update issued" +
                      " | netId=" + spawnedIdentity.NetId +
                      " | status=" + SafeText(status) +
                      " | syncVarKey=" + SafeText(mirrorLikeSyncKey) +
                      " | legacySyncVar=" + BoolText(sendLegacyPhase21SyncVar) +
                      " | syncSent=" + BoolText(mirrorSyncSent) +
                      " | transformSent=" + BoolText(transformSent) +
                      " | api=" + (useNetworkServerApiForStateSync ? "NetworkServer" : "StateSyncBridge") +
                      " | rejectReason=" + SafeText(GetStateSyncRejectReason()) +
                      " | expectedRoutes=game/sync_var,game/network_transform");
        }

        return success;
    }

    private bool SendSyncVar(string syncKey, string valueJson)
    {
        if (spawnedIdentity == null || string.IsNullOrWhiteSpace(syncKey)) return false;
        bool sent = useNetworkServerApiForStateSync
            ? MetaverseNetworkServer.SetSyncVar(spawnedIdentity, syncKey, valueJson)
            : stateSyncBridge.SetSyncVar(spawnedIdentity, syncKey, valueJson);
        if (sent) syncVarPushCount++;
        return sent;
    }

    private bool SendNetworkTransform()
    {
        if (spawnedIdentity == null) return false;
        bool sent = useNetworkServerApiForStateSync
            ? MetaverseNetworkServer.SyncTransform(spawnedIdentity)
            : stateSyncBridge.SendNetworkTransform(spawnedIdentity);
        if (sent) transformPushCount++;
        return sent;
    }

    private void DespawnSmokeObject(string reason)
    {
        if (spawnedIdentity == null || spawnedIdentity.gameObject == null) return;

        int netId = spawnedIdentity.NetId;
        if (useNetworkServerApiForDespawn)
        {
            MetaverseNetworkServer.Despawn(spawnedIdentity, SafeReason(reason, "network_state_sync_phase33A_smoke_completed"));
        }
        else
        {
            spawnManager.Despawn(spawnedIdentity.gameObject, SafeReason(reason, "network_state_sync_phase33A_smoke_completed"));
        }

        despawnIssued = true;

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkStateSyncSmokeController] Phase 33A smoke despawn issued" +
                      " | netId=" + netId +
                      " | api=" + (useNetworkServerApiForDespawn ? "NetworkServer.Despawn" : "SpawnManager.Despawn") +
                      " | expectedRoute=game/despawn");
        }
    }

    public string GetSmokeDebugSummary()
    {
        return "phase=" + phaseName +
               " | spawnIssued=" + BoolText(spawnIssued) +
               " | firstUpdate=" + BoolText(firstUpdateIssued) +
               " | secondUpdate=" + BoolText(secondUpdateIssued) +
               " | despawnIssued=" + BoolText(despawnIssued) +
               " | syncVarPushCount=" + syncVarPushCount +
               " | transformPushCount=" + transformPushCount +
               " | lastStatus=" + SafeText(lastStatus) +
               " | netId=" + (spawnedIdentity != null ? spawnedIdentity.NetId : 0);
    }

    private string BuildStatusJson(string status, Vector3 position)
    {
        return "{\"phase\":\"" + SafeText(phaseName) +
               "\",\"legacyPhase\":\"21_22\"" +
               ",\"api\":\"NetworkServer.SetSyncVar->NetworkServer.SyncTransform\"" +
               ",\"status\":\"" + SafeText(status) +
               "\",\"netId\":" + (spawnedIdentity != null ? spawnedIdentity.NetId : 0) +
               ",\"x\":" + position.x.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
               ",\"y\":" + position.y.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
               ",\"z\":" + position.z.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
               ",\"syncVarPushCount\":" + syncVarPushCount +
               ",\"transformPushCount\":" + transformPushCount +
               "}";
    }

    private string GetStateSyncRejectReason()
    {
        return stateSyncBridge != null ? stateSyncBridge.LastStateSyncRejectReason : string.Empty;
    }

    private void ApplyConfig(MetaverseDedicatedServerRuntimeConfig config)
    {
        if (config == null) return;
        prefabId = config.NetworkStateSyncSmokePrefabId;
        spawnRequiredPlayers = config.NetworkStateSyncSmokeSpawnRequiredPlayers;
        snapshotRequiredPlayers = config.NetworkStateSyncSmokeSnapshotRequiredPlayers;
        initialDelaySeconds = config.NetworkStateSyncSmokeInitialDelaySeconds;
        minimumAliveSeconds = config.NetworkStateSyncSmokeMinimumAliveSeconds;
        updateDelayAfterSnapshotSeconds = config.NetworkStateSyncSmokeUpdateDelayAfterSnapshotSeconds;
        despawnDelayAfterSnapshotSeconds = config.NetworkStateSyncSmokeDespawnDelayAfterSnapshotSeconds;
    }

    private void EnsureReferences()
    {
        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
        if (stateSyncBridge == null) stateSyncBridge = MetaverseNetworkStateSyncBridge.Instance;
        if (stateSyncBridge == null)
        {
#if UNITY_2023_1_OR_NEWER
            stateSyncBridge = FindFirstObjectByType<MetaverseNetworkStateSyncBridge>();
#else
            stateSyncBridge = FindObjectOfType<MetaverseNetworkStateSyncBridge>();
#endif
        }
        if (playerRegistry == null)
        {
#if UNITY_2023_1_OR_NEWER
            playerRegistry = FindFirstObjectByType<DedicatedPlayerRegistry>();
#else
            playerRegistry = FindObjectOfType<DedicatedPlayerRegistry>();
#endif
        }
    }

    private string SafeText(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Replace("\"", "'");
    }

    private string SafeReason(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private string BoolText(bool value)
    {
        return value ? "True" : "False";
    }
}
