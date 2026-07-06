using System.Collections.Generic;
using Network_A.GameServer.Players;
using UnityEngine;

public class MetaverseNetworkPlayerObjectServer : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MetaverseSpawnManager spawnManager;
    [SerializeField] private MetaverseNetworkOwnershipBridge ownershipBridge;
    [SerializeField] private DedicatedPlayerRegistry playerRegistry;
    [SerializeField] private MetaverseDedicatedServerRuntimeConfig config;

    [Header("Debug")]
    [SerializeField] private bool logMessages = true;

    private bool registryEventsBound;
    private bool smokeCompletedLogged;
    private string lastPlayerObjectRejectReason = string.Empty;
    private readonly Dictionary<string, MetaverseNetworkIdentity> dict_playerObjectsByConnectionId = new Dictionary<string, MetaverseNetworkIdentity>();

    public int PlayerObjectCount => dict_playerObjectsByConnectionId.Count;
    public string LastPlayerObjectRejectReason => lastPlayerObjectRejectReason;
    public bool IsReady => spawnManager != null;

    public void Bind(MetaverseSpawnManager manager, MetaverseDedicatedServerRuntimeConfig runtimeConfig)
    {
        spawnManager = manager;
        config = runtimeConfig;
        EnsureReferences();
        BindRegistryEvents();
    }

    private void Awake()
    {
        EnsureReferences();
    }

    private void OnEnable()
    {
        EnsureReferences();
        BindRegistryEvents();
    }

    private void Update()
    {
        EnsureReferences();
        BindRegistryEvents();
    }

    private void OnDisable()
    {
        UnbindRegistryEvents();
    }

    private void OnDestroy()
    {
        UnbindRegistryEvents();
    }

    public bool SpawnPlayerObject(DedicatedPlayerSession session, out MetaverseNetworkIdentity identity)
    {
        identity = null;

        if (!CanSpawnPlayerObject(session))
        {
            if (logMessages)
            {
                Debug.LogWarning("[MetaverseNetworkPlayerObjectServer] Player object spawn rejected | reason=" + SafeTrim(lastPlayerObjectRejectReason) +
                                 " | userId=" + SafeTrim(session != null ? session.userId : string.Empty));
            }

            return false;
        }

        string prefabId = config != null ? config.NetworkPlayerObjectSmokePrefabId : MetaverseNetworkPlayerObjectSmokePrefabInstaller.DefaultPrefabId;
        Vector3 position = BuildSpawnPosition(dict_playerObjectsByConnectionId.Count);

        bool spawned = MetaverseNetworkServer.SpawnPrefab(prefabId, position, Quaternion.identity, session, out identity);
        if (!spawned || identity == null)
        {
            if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
            spawned = spawnManager != null && spawnManager.TrySpawnPrefab(prefabId, position, Quaternion.identity, -1, out identity);
            if (spawned && identity != null) AssignOwner(identity, session);
        }

        if (!spawned || identity == null)
        {
            SetRejectReason("spawn_prefab_failed");
            Debug.LogWarning("[MetaverseNetworkPlayerObjectServer] Player object spawn failed | userId=" + SafeTrim(session.userId) + " | prefabId=" + SafeTrim(prefabId));
            return false;
        }

        dict_playerObjectsByConnectionId[session.connectionId] = identity;
        AssignOwner(identity, session);

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkPlayerObjectServer] Player object spawned | userId=" + SafeTrim(session.userId) +
                      " | playerId=" + SafeTrim(session.playerId) +
                      " | connectionId=" + SafeTrim(session.connectionId) +
                      " | netId=" + identity.NetId +
                      " | prefabId=" + SafeTrim(prefabId) +
                      " | mirrorRoute=NetworkServer.Spawn+AssignClientAuthority | ownerAssigned=YES");
        }

        TryLogSmokeCompleted();
        SetRejectReason(string.Empty);
        return true;
    }

    public bool DespawnPlayerObject(DedicatedPlayerSession session, string reason = "player_object_owner_left")
    {
        if (session == null || string.IsNullOrWhiteSpace(session.connectionId))
        {
            SetRejectReason("invalid_session");
            return false;
        }

        return DespawnPlayerObject(session.connectionId, reason);
    }

    public bool DespawnPlayerObject(string connectionId, string reason = "player_object_owner_left")
    {
        string safeConnectionId = SafeTrim(connectionId);
        if (string.IsNullOrWhiteSpace(safeConnectionId))
        {
            SetRejectReason("invalid_connection_id");
            return false;
        }

        if (!dict_playerObjectsByConnectionId.TryGetValue(safeConnectionId, out MetaverseNetworkIdentity identity) || identity == null)
        {
            SetRejectReason("player_object_not_found");
            return false;
        }

        dict_playerObjectsByConnectionId.Remove(safeConnectionId);
        MetaverseNetworkServer.Despawn(identity, SafeTrim(reason));

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkPlayerObjectServer] Player object despawn requested | connectionId=" + safeConnectionId +
                      " | netId=" + identity.NetId +
                      " | mirrorRoute=NetworkServer.Despawn | reason=" + SafeTrim(reason));
        }

        SetRejectReason(string.Empty);
        return true;
    }

    public bool TryGetPlayerObject(DedicatedPlayerSession session, out MetaverseNetworkIdentity identity)
    {
        identity = null;
        if (session == null) return false;
        return TryGetPlayerObject(session.connectionId, out identity);
    }

    public bool TryGetPlayerObject(string connectionId, out MetaverseNetworkIdentity identity)
    {
        identity = null;
        string key = SafeTrim(connectionId);
        if (string.IsNullOrWhiteSpace(key)) return false;
        return dict_playerObjectsByConnectionId.TryGetValue(key, out identity) && identity != null;
    }

    public List<MetaverseNetworkIdentity> GetPlayerObjects()
    {
        return new List<MetaverseNetworkIdentity>(dict_playerObjectsByConnectionId.Values);
    }

    public void RemoveAllPlayerObjects(string reason = "remove_all_player_objects")
    {
        List<string> keys = new List<string>(dict_playerObjectsByConnectionId.Keys);
        for (int i = 0; i < keys.Count; i++)
        {
            DespawnPlayerObject(keys[i], reason);
        }
    }

    public string GetPlayerObjectDebugSummary()
    {
        return "Phase33A PlayerObject | ready=" + IsReady +
               " | count=" + dict_playerObjectsByConnectionId.Count +
               " | lastReject=" + SafeTrim(lastPlayerObjectRejectReason);
    }

    private void HandlePlayerRegistered(DedicatedPlayerSession session)
    {
        SpawnPlayerObject(session, out _);
    }

    private void HandlePlayerRemoved(DedicatedPlayerSession session, string reason)
    {
        if (session == null || string.IsNullOrWhiteSpace(session.connectionId)) return;
        DespawnPlayerObject(session.connectionId, "player_object_owner_left_" + SafeTrim(reason));
    }

    private bool CanSpawnPlayerObject(DedicatedPlayerSession session)
    {
        EnsureReferences();

        if (session == null)
        {
            SetRejectReason("session_missing");
            return false;
        }

        if (string.IsNullOrWhiteSpace(session.connectionId))
        {
            SetRejectReason("connection_id_missing");
            return false;
        }

        if (dict_playerObjectsByConnectionId.ContainsKey(session.connectionId))
        {
            SetRejectReason("player_object_already_exists");
            return false;
        }

        if (spawnManager == null)
        {
            SetRejectReason("spawn_manager_missing");
            return false;
        }

        SetRejectReason(string.Empty);
        return true;
    }

    private void AssignOwner(MetaverseNetworkIdentity identity, DedicatedPlayerSession session)
    {
        if (identity == null || session == null) return;

        if (ownershipBridge == null) ownershipBridge = MetaverseNetworkOwnershipBridge.Instance;
        if (ownershipBridge != null)
        {
            ownershipBridge.AssignClientAuthority(identity, session, "player_object_owner_assigned");
        }
        else
        {
            identity.SetOwnerInfo(session.connectionId, session.userId, session.playerId, false);
        }
    }

    private void TryLogSmokeCompleted()
    {
        if (smokeCompletedLogged || config == null || !config.EnableNetworkPlayerObjectSmokeTest) return;
        int requiredPlayers = config.NetworkPlayerObjectSmokeRequiredPlayers;
        if (dict_playerObjectsByConnectionId.Count < requiredPlayers) return;

        smokeCompletedLogged = true;
        Debug.Log("[MetaverseNetworkPlayerObjectServer] Smoke flow completed | phase=33A | expected=NetworkServer.Spawn->AssignClientAuthority->PlayerObject | required=" +
                  requiredPlayers + " | playerObjects=" + dict_playerObjectsByConnectionId.Count);
    }

    private Vector3 BuildSpawnPosition(int index)
    {
        int safeIndex = Mathf.Max(0, index);
        return new Vector3(safeIndex * 1.5f, 1.25f, 0f);
    }

    private void EnsureReferences()
    {
        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
        if (ownershipBridge == null) ownershipBridge = MetaverseNetworkOwnershipBridge.Instance;
        if (config == null) config = MetaverseDedicatedServerRuntimeConfig.LoadDefault();

        if (playerRegistry == null)
        {
#if UNITY_2023_1_OR_NEWER
            playerRegistry = FindFirstObjectByType<DedicatedPlayerRegistry>();
#else
            playerRegistry = FindObjectOfType<DedicatedPlayerRegistry>();
#endif
        }
    }

    private void BindRegistryEvents()
    {
        if (registryEventsBound || playerRegistry == null) return;
        playerRegistry.PlayerRegistered -= HandlePlayerRegistered;
        playerRegistry.PlayerRemoved -= HandlePlayerRemoved;
        playerRegistry.PlayerRegistered += HandlePlayerRegistered;
        playerRegistry.PlayerRemoved += HandlePlayerRemoved;
        registryEventsBound = true;
        if (logMessages) Debug.Log("[MetaverseNetworkPlayerObjectServer] Bound to player registry events.");
    }

    private void UnbindRegistryEvents()
    {
        if (playerRegistry != null)
        {
            playerRegistry.PlayerRegistered -= HandlePlayerRegistered;
            playerRegistry.PlayerRemoved -= HandlePlayerRemoved;
        }
        registryEventsBound = false;
    }

    private void SetRejectReason(string reason)
    {
        lastPlayerObjectRejectReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();
    }

    private string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
