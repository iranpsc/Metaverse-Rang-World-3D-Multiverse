using System.Collections.Generic;
using Network_A.GameServer.Players;
using Network_A.GameServer.Gameplay;
using UnityEngine;

public class MetaverseNetworkPlayerObjectServer : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MetaverseSpawnManager spawnManager;
    [SerializeField] private MetaverseNetworkOwnershipBridge ownershipBridge;
    [SerializeField] private DedicatedPlayerRegistry playerRegistry;
    [SerializeField] private DedicatedPlayerStateStore stateStore;
    [SerializeField] private MetaverseDedicatedServerRuntimeConfig config;

    [Header("Debug")]
    [SerializeField] private bool logMessages = true;

    private bool registryEventsBound;
    private bool smokeCompletedLogged;
    private string lastPlayerObjectRejectReason = string.Empty;
    private readonly Dictionary<string, MetaverseNetworkIdentity> dict_playerObjectsByConnectionId = new Dictionary<string, MetaverseNetworkIdentity>();
    private readonly Dictionary<string, MetaverseNetworkIdentity> dict_playerObjectsByUserId = new Dictionary<string, MetaverseNetworkIdentity>();
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

        if (TryRebindExistingPlayerObjectForReconnect(session, out identity))
        {
            return true;
        }

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
        Vector3 position = BuildSpawnPositionForSession(session, dict_playerObjectsByConnectionId.Count);

        bool spawned = MetaverseNetworkServer.SpawnPrefab(prefabId, position, Quaternion.identity, session, out identity);
        if (!spawned || identity == null)
        {
            if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
            spawned = spawnManager != null && spawnManager.TrySpawnPrefab(
                prefabId,
                position,
                Quaternion.identity,
                -1,
                string.Empty,
                SafeTrim(session.userId),
                SafeTrim(session.playerId),
                SafeTrim(session.roomId),
                false,
                out identity);
            if (spawned && identity != null) AssignOwner(identity, session);
        }

        if (!spawned || identity == null)
        {
            SetRejectReason("spawn_prefab_failed");
            Debug.LogWarning("[MetaverseNetworkPlayerObjectServer] Player object spawn failed | userId=" + SafeTrim(session.userId) + " | prefabId=" + SafeTrim(prefabId));
            return false;
        }

        dict_playerObjectsByConnectionId[session.connectionId] = identity;

        string safeUserId = SafeTrim(session.userId);
        string roomUserKey = BuildRoomUserKey(session.roomId, session.userId);
        if (!string.IsNullOrWhiteSpace(safeUserId))
        {
            dict_playerObjectsByUserId[roomUserKey] = identity;
        }

        AssignOwner(identity, session);
        TryRebindPlayerStateForReconnect(session, "player_object_spawn_state_rebind");

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
        RemoveUserMappingForIdentity(identity);

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

        if (TryGetPlayerObject(session.connectionId, out identity))
        {
            return true;
        }

        string safeUserId = SafeTrim(session.userId);
        if (string.IsNullOrWhiteSpace(safeUserId)) return false;

        string roomUserKey = BuildRoomUserKey(session.roomId, session.userId);
        return dict_playerObjectsByUserId.TryGetValue(roomUserKey, out identity) && identity != null;
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

        string safeReason = SafeTrim(reason);
        if (safeReason == "duplicate_user_replaced")
        {
            if (logMessages)
            {
                Debug.Log("[MetaverseNetworkPlayerObjectServer] Player object remove skipped for reconnect rebind | userId=" +
                          SafeTrim(session.userId) + " | connectionId=" + SafeTrim(session.connectionId) +
                          " | reason=" + safeReason);
            }

            return;
        }

        DespawnPlayerObject(session.connectionId, "player_object_owner_left_" + safeReason);
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
            identity.SetRoomId(session.roomId);
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

    private Vector3 BuildSpawnPositionForSession(DedicatedPlayerSession session, int index)
    {
        EnsureReferences();

        if (session != null && stateStore != null)
        {
            DedicatedPlayerStateRecord stateRecord = stateStore.GetByUserIdInRoom(session.roomId, session.userId);

            if (stateRecord != null)
            {
                return stateRecord.Position;
            }
        }

        return BuildSpawnPosition(index);
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

        if (stateStore == null)
        {
#if UNITY_2023_1_OR_NEWER
            stateStore = FindFirstObjectByType<DedicatedPlayerStateStore>();
#else
            stateStore = FindObjectOfType<DedicatedPlayerStateStore>();
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

    private string BuildRoomUserKey(string roomId, string userId)
    {
        return SafeTrim(roomId) + "::" + SafeTrim(userId);
    }

    public int GetPlayerObjectCountInRoom(string roomId)
    {
        string safeRoomId = SafeTrim(roomId);
        if (string.IsNullOrWhiteSpace(safeRoomId)) return 0;
        int count = 0;
        foreach (MetaverseNetworkIdentity identity in dict_playerObjectsByConnectionId.Values)
        {
            if (identity == null) continue;
            if (identity.IsRoom(safeRoomId)) count++;
        }
        return count;
    }

    private string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }


    //* این تابع هنگام ریکانکت، آبجکت قبلی همان یوزر را به کانکشن جدید وصل می کند.
    private bool TryRebindExistingPlayerObjectForReconnect(DedicatedPlayerSession session, out MetaverseNetworkIdentity identity)
    {
        identity = null;

        if (session == null) return false;

        string safeUserId = SafeTrim(session.userId);
        string safeConnectionId = SafeTrim(session.connectionId);

        if (string.IsNullOrWhiteSpace(safeUserId) || string.IsNullOrWhiteSpace(safeConnectionId))
        {
            return false;
        }

        string roomUserKey = BuildRoomUserKey(session.roomId, session.userId);
        if (!dict_playerObjectsByUserId.TryGetValue(roomUserKey, out identity) || identity == null)
        {
            if (dict_playerObjectsByUserId.ContainsKey(roomUserKey))
            {
                dict_playerObjectsByUserId.Remove(roomUserKey);
            }

            return false;
        }

        RemoveConnectionMappingForIdentity(identity);

        dict_playerObjectsByConnectionId[safeConnectionId] = identity;
        dict_playerObjectsByUserId[roomUserKey] = identity;

        AssignOwner(identity, session);
        TryRebindPlayerStateForReconnect(session, "duplicate_user_replaced");

        if (logMessages)
        {
            Vector3 position = identity.transform != null ? identity.transform.position : Vector3.zero;

            Debug.Log("[MetaverseNetworkPlayerObjectServer] Player object rebound for reconnect | userId=" +
                      safeUserId + " | newConnectionId=" + safeConnectionId +
                      " | netId=" + identity.NetId +
                      " | position=" + position +
                      " | mirrorRoute=AssignClientAuthority | spawnSkipped=YES | despawnSkipped=YES");
        }

        SetRejectReason(string.Empty);
        return true;
    }

    //* این تابع وضعیت ذخیره شده پلیر را بعد از ریکانکت به کانکشن جدید وصل می کند.
    private void TryRebindPlayerStateForReconnect(DedicatedPlayerSession session, string reason)
    {
        if (session == null) return;

        EnsureReferences();

        if (stateStore == null) return;

        string safeReason = SafeTrim(reason);
        bool rebound = stateStore.RebindConnectionForUser(session, safeReason);

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkPlayerObjectServer] Player state rebind checked | userId=" +
                      SafeTrim(session.userId) + " | connectionId=" + SafeTrim(session.connectionId) +
                      " | reason=" + safeReason + " | rebound=" + rebound);
        }
    }

    //* این تابع مپ کانکشن قبلی یک آبجکت را پاک می کند.
    private void RemoveConnectionMappingForIdentity(MetaverseNetworkIdentity identity)
    {
        if (identity == null) return;

        List<string> keysToRemove = new List<string>();

        foreach (KeyValuePair<string, MetaverseNetworkIdentity> pair in dict_playerObjectsByConnectionId)
        {
            if (pair.Value == identity)
            {
                keysToRemove.Add(pair.Key);
            }
        }

        for (int i = 0; i < keysToRemove.Count; i++)
        {
            dict_playerObjectsByConnectionId.Remove(keysToRemove[i]);
        }
    }

    //* این تابع مپ یوزر یک آبجکت را پاک می کند.
    private void RemoveUserMappingForIdentity(MetaverseNetworkIdentity identity)
    {
        if (identity == null) return;

        List<string> keysToRemove = new List<string>();

        foreach (KeyValuePair<string, MetaverseNetworkIdentity> pair in dict_playerObjectsByUserId)
        {
            if (pair.Value == identity)
            {
                keysToRemove.Add(pair.Key);
            }
        }

        for (int i = 0; i < keysToRemove.Count; i++)
        {
            dict_playerObjectsByUserId.Remove(keysToRemove[i]);
        }
    }
}
