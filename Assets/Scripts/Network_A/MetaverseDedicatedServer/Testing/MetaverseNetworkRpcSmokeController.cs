using System.Collections;
using Network_A.GameServer.Players;
using UnityEngine;

public class MetaverseNetworkRpcSmokeController : MonoBehaviour
{
    private const string PhaseName = "33A";
    private const string MirrorLikeFlow = "Cmd->Server.OnCommand->Rpc->TargetRpc";
    private const string DefaultSpawnReason = "phase33a_mirror_like_rpc_smoke_spawn";
    private const string DefaultDespawnReason = "phase33a_mirror_like_rpc_smoke_completed";
    private const float DefaultRequiredPlayerStableSeconds = 2f;

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
    [SerializeField] private float requiredPlayerStableSeconds = DefaultRequiredPlayerStableSeconds;

    [Header("Mirror-Like API")]
    [SerializeField] private bool useNetworkServerApiForSpawn = true;
    [SerializeField] private bool logMirrorLikeFlow = true;

    [Header("Debug")]
    [SerializeField] private bool logMessages = true;

    private bool started;
    private MetaverseNetworkIdentity spawnedIdentity;
    private MetaverseNetworkRpcSmokeProbe spawnedProbe;
    private float flowStartedAt;

    public string ActivePrefabId => GetSafePrefabId();
    public int ActiveNetId => spawnedIdentity != null ? spawnedIdentity.NetId : 0;
    public bool HasSpawnedProbe => spawnedIdentity != null && spawnedIdentity.gameObject != null;
    public string ExpectedMirrorLikeFlow => MirrorLikeFlow;

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
        flowStartedAt = Time.realtimeSinceStartup;
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

        string safePrefabId = GetSafePrefabId();
        MetaverseNetworkRpcSmokePrefabInstaller.InstallRuntimeRpcProbePrefab(spawnManager, safePrefabId, logMessages);

        int requiredPlayersForFinalRpc = GetRequiredPlayersForFinalRpc();
        yield return WaitForRequiredPlayersBeforeSmokeSpawn(requiredPlayersForFinalRpc);

        yield return new WaitForSeconds(Mathf.Max(0f, initialDelaySeconds));

        if (playerRegistry.GetCurrentPlayerCount() < requiredPlayersForFinalRpc)
        {
            Log("Required player count dropped before smoke spawn" +
                " | phase=" + PhaseName +
                " | required=" + requiredPlayersForFinalRpc +
                " | current=" + playerRegistry.GetCurrentPlayerCount() +
                " | action=waiting_again" +
                " | expectedFlow=" + MirrorLikeFlow);

            yield return WaitForRequiredPlayersBeforeSmokeSpawn(requiredPlayersForFinalRpc);
        }

        if (!TrySpawnMirrorLikeProbe(safePrefabId, out spawnedIdentity))
        {
            Debug.LogWarning("[MetaverseNetworkRpcSmokeController] Smoke spawn failed" +
                             " | phase=" + PhaseName +
                             " | prefabId=" + safePrefabId +
                             " | useNetworkServerApiForSpawn=" + BoolText(useNetworkServerApiForSpawn));
            yield break;
        }

        ResolveSpawnedProbe();

        Log("Smoke spawn issued" +
            " | phase=" + PhaseName +
            " | netId=" + spawnedIdentity.NetId +
            " | prefabId=" + spawnedIdentity.PrefabId +
            " | mirrorRoute=NetworkServer.Spawn" +
            " | expectedRoutes=game/command,game/client_rpc,game/target_rpc" +
            " | expectedApi=Cmd,Rpc,TargetRpc" +
            " | spawnReason=" + DefaultSpawnReason +
            " | snapshotRequiredPlayers=" + snapshotRequiredPlayers);

        if (logMirrorLikeFlow)
        {
            Log("Mirror-like API smoke object is alive" +
                " | flow=" + MirrorLikeFlow +
                " | commandApi=Cmd" +
                " | broadcastApi=Rpc" +
                " | singleClientApi=TargetRpc" +
                " | probe=" + GetProbeSummary());
        }

        float aliveStartedAt = Time.realtimeSinceStartup;

        while (playerRegistry.GetCurrentPlayerCount() < Mathf.Max(spawnRequiredPlayers, snapshotRequiredPlayers))
        {
            Log("Waiting for snapshot players" +
                " | phase=" + PhaseName +
                " | required=" + snapshotRequiredPlayers +
                " | current=" + playerRegistry.GetCurrentPlayerCount() +
                " | activeNetId=" + ActiveNetId +
                " | expectedSnapshotCommand=game/command" +
                " | expectedFlow=" + MirrorLikeFlow);
            yield return new WaitForSeconds(5f);
        }

        Log("Snapshot player target reached" +
            " | phase=" + PhaseName +
            " | required=" + snapshotRequiredPlayers +
            " | current=" + playerRegistry.GetCurrentPlayerCount() +
            " | expectedSnapshotCommand=game/command" +
            " | probe=" + GetProbeSummary());

        while (Time.realtimeSinceStartup - aliveStartedAt < Mathf.Max(1f, minimumAliveSeconds))
        {
            yield return new WaitForSeconds(1f);
        }

        yield return new WaitForSeconds(Mathf.Max(0f, despawnDelayAfterSnapshotSeconds));

        if (spawnedIdentity != null && spawnedIdentity.gameObject != null)
        {
            int netId = spawnedIdentity.NetId;
            string probeSummary = GetProbeSummary();
            DespawnProbe(DefaultDespawnReason);
            Log("Smoke despawn issued" +
                " | phase=" + PhaseName +
                " | netId=" + netId +
                " | mirrorRoute=NetworkServer.Despawn" +
                " | expectedRoute=game/despawn" +
                " | finalProbe=" + probeSummary);
        }

        Log("Smoke flow completed" +
            " | phase=" + PhaseName +
            " | expected=" + MirrorLikeFlow +
            " | elapsedSeconds=" + (Time.realtimeSinceStartup - flowStartedAt).ToString("F1"));
    }

    private IEnumerator WaitForRequiredPlayersBeforeSmokeSpawn(int requiredPlayersForFinalRpc)
    {
        int safeRequired = Mathf.Max(1, requiredPlayersForFinalRpc);
        float stableStartedAt = -1f;

        Log("Waiting for final RPC players before smoke spawn" +
            " | phase=" + PhaseName +
            " | spawnRequired=" + Mathf.Max(1, spawnRequiredPlayers) +
            " | snapshotRequired=" + Mathf.Max(1, snapshotRequiredPlayers) +
            " | required=" + safeRequired +
            " | current=" + playerRegistry.GetCurrentPlayerCount() +
            " | stableSeconds=" + Mathf.Max(0f, requiredPlayerStableSeconds).ToString("F1") +
            " | expectedFlow=" + MirrorLikeFlow);

        while (true)
        {
            int currentPlayers = playerRegistry.GetCurrentPlayerCount();
            if (currentPlayers >= safeRequired)
            {
                if (stableStartedAt < 0f)
                {
                    stableStartedAt = Time.realtimeSinceStartup;
                    Log("Final RPC player target reached. Stability timer started" +
                        " | phase=" + PhaseName +
                        " | required=" + safeRequired +
                        " | current=" + currentPlayers +
                        " | stableSeconds=" + Mathf.Max(0f, requiredPlayerStableSeconds).ToString("F1"));
                }

                if (Time.realtimeSinceStartup - stableStartedAt >= Mathf.Max(0f, requiredPlayerStableSeconds))
                {
                    Log("Final RPC player target stable. Smoke spawn is allowed" +
                        " | phase=" + PhaseName +
                        " | required=" + safeRequired +
                        " | current=" + currentPlayers +
                        " | mirrorRoute=Cmd->Rpc->TargetRpc");
                    yield break;
                }
            }
            else
            {
                stableStartedAt = -1f;
                Log("Waiting for final RPC players before smoke spawn" +
                    " | phase=" + PhaseName +
                    " | required=" + safeRequired +
                    " | current=" + currentPlayers +
                    " | activeNetId=" + ActiveNetId +
                    " | expectedFinalRpcBroadcastCount=" + safeRequired +
                    " | expectedFlow=" + MirrorLikeFlow);
            }

            yield return new WaitForSeconds(1f);
        }
    }

    private int GetRequiredPlayersForFinalRpc()
    {
        return Mathf.Max(1, spawnRequiredPlayers, snapshotRequiredPlayers);
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
        if (requiredPlayerStableSeconds <= 0f) requiredPlayerStableSeconds = DefaultRequiredPlayerStableSeconds;
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

    private bool TrySpawnMirrorLikeProbe(string safePrefabId, out MetaverseNetworkIdentity identity)
    {
        identity = null;
        Vector3 spawnPosition = new Vector3(-3f, 1.5f, 0f);
        Quaternion spawnRotation = Quaternion.identity;

        if (useNetworkServerApiForSpawn)
        {
            bool spawnedByServerApi = MetaverseNetworkServer.SpawnPrefab(safePrefabId, spawnPosition, spawnRotation, out identity);
            if (spawnedByServerApi && identity != null)
            {
                identity.SetServerOwned(true);
                return true;
            }
        }

        if (spawnManager == null) return false;
        bool spawnedByManager = spawnManager.TrySpawnPrefab(safePrefabId, spawnPosition, spawnRotation, -1, out identity);
        if (spawnedByManager && identity != null) identity.SetServerOwned(true);
        return spawnedByManager && identity != null;
    }

    private void DespawnProbe(string reason)
    {
        if (spawnedIdentity == null || spawnedIdentity.gameObject == null) return;

        if (useNetworkServerApiForSpawn)
        {
            MetaverseNetworkServer.Despawn(spawnedIdentity, SafeReason(reason, DefaultDespawnReason));
            return;
        }

        if (spawnManager != null) spawnManager.Despawn(spawnedIdentity.gameObject, SafeReason(reason, DefaultDespawnReason));
    }

    private void ResolveSpawnedProbe()
    {
        spawnedProbe = spawnedIdentity != null ? spawnedIdentity.GetComponent<MetaverseNetworkRpcSmokeProbe>() : null;
    }

    private string GetProbeSummary()
    {
        if (spawnedProbe == null) ResolveSpawnedProbe();
        return spawnedProbe != null ? spawnedProbe.GetSmokeDebugSummary() : "probe_missing";
    }

    private string GetSafePrefabId()
    {
        return string.IsNullOrWhiteSpace(prefabId) ? MetaverseNetworkRpcSmokePrefabInstaller.DefaultPrefabId : prefabId.Trim();
    }

    private void Log(string message)
    {
        if (!logMessages) return;
        Debug.Log("[MetaverseNetworkRpcSmokeController] " + message);
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
