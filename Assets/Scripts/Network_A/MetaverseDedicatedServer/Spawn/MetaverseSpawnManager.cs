using System;
using System.Collections.Generic;
using UnityEngine;

public class MetaverseSpawnManager : MonoBehaviour
{
    [Header("Registry")]
    [SerializeField] private MetaverseNetworkPrefabRegistry prefabRegistry;
    [SerializeField] private Transform spawnedRoot;

    [Header("Runtime")]
    [SerializeField] private bool autoCreateSpawnedRoot = true;
    [SerializeField] private bool autoAddIdentityToSpawnedObjects = true;
    [SerializeField] private bool destroyOnDespawn = true;
    [SerializeField] private bool logLifecycle = true;
    [SerializeField] private bool requireServerRuntimeForNetworkServerApi = true;
    [SerializeField] private bool allowEditorSimulation = true;

    private readonly Dictionary<int, MetaverseNetworkIdentity> dictSpawnedByNetId = new Dictionary<int, MetaverseNetworkIdentity>();
    private long spawnSequence;
    private int nextNetId = 1;

    public static MetaverseSpawnManager Instance { get; private set; }
    public MetaverseNetworkPrefabRegistry PrefabRegistry => prefabRegistry;
    public int SpawnedCount => dictSpawnedByNetId.Count;
    public string LastSpawnRejectReason { get; private set; } = string.Empty;
    public string LastDespawnRejectReason { get; private set; } = string.Empty;
    public bool IsMirrorLikeSpawnManagerReady => prefabRegistry != null && spawnedRoot != null;

    public event Action<MetaverseNetworkIdentity, MetaverseSpawnPayload> ServerObjectSpawned;
    public event Action<MetaverseNetworkIdentity, string> ServerObjectDespawned;
    public event Action<MetaverseNetworkIdentity, MetaverseSpawnPayload> ClientObjectSpawned;
    public event Action<int, string> ClientObjectDespawned;

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else if (Instance != this) Debug.LogWarning("[MetaverseSpawnManager] More than one instance exists in scene.");
        EnsureSpawnedRoot();
        prefabRegistry?.RebuildCache();
    }

    public MetaverseNetworkIdentity Spawn(GameObject obj)
    {
        return Spawn(obj, -1);
    }

    public MetaverseNetworkIdentity Spawn(GameObject obj, int ownerConnectionId)
    {
        string ownerConnectionIdText = ownerConnectionId >= 0 ? ownerConnectionId.ToString() : string.Empty;
        return Spawn(obj, ownerConnectionId, ownerConnectionIdText, string.Empty, string.Empty, false, "network_server_spawn");
    }

    public MetaverseNetworkIdentity Spawn(GameObject obj, string ownerConnectionId, string ownerUserId = "", string ownerPlayerId = "")
    {
        return Spawn(obj, ParseConnectionId(ownerConnectionId), ownerConnectionId, ownerUserId, ownerPlayerId, false, "network_server_spawn");
    }

    public MetaverseNetworkIdentity Spawn(GameObject obj, string ownerConnectionId, string ownerUserId, string ownerPlayerId, bool serverOwned, string reason)
    {
        return Spawn(obj, ParseConnectionId(ownerConnectionId), ownerConnectionId, ownerUserId, ownerPlayerId, serverOwned, reason);
    }

    public MetaverseNetworkIdentity SpawnServerOwned(GameObject obj, string reason = "network_server_spawn_server_owned")
    {
        return Spawn(obj, -1, string.Empty, string.Empty, string.Empty, true, reason);
    }

    public MetaverseNetworkIdentity Spawn(GameObject obj, int ownerConnectionId, string ownerConnectionIdText, string ownerUserId, string ownerPlayerId, bool serverOwned, string reason)
    {
        LastSpawnRejectReason = string.Empty;
        if (!CanUseNetworkServerApi("NetworkServer.Spawn")) return null;
        if (obj == null)
        {
            RejectSpawn("object_null");
            return null;
        }

        MetaverseNetworkIdentity identity = EnsureIdentity(obj);
        if (identity == null)
        {
            RejectSpawn("identity_missing");
            return null;
        }

        if (identity.IsSpawned && identity.NetId > 0)
        {
            if (logLifecycle) Debug.LogWarning("[MetaverseSpawnManager] Object is already spawned | netId=" + identity.NetId + " | name=" + obj.name);
            return identity;
        }

        string prefabId = ResolvePrefabId(obj, identity);
        int netId = AllocateNetId();
        string resolvedOwnerConnectionIdText = SafeTrim(ownerConnectionIdText);
        if (ownerConnectionId < 0) ownerConnectionId = ParseConnectionId(resolvedOwnerConnectionIdText);
        bool resolvedServerOwned = serverOwned || (ownerConnectionId < 0 && string.IsNullOrWhiteSpace(resolvedOwnerConnectionIdText) && string.IsNullOrWhiteSpace(ownerUserId) && string.IsNullOrWhiteSpace(ownerPlayerId));

        identity.AssignSpawnData(prefabId, netId, ownerConnectionId, resolvedOwnerConnectionIdText, SafeTrim(ownerUserId), SafeTrim(ownerPlayerId), resolvedServerOwned, false);
        dictSpawnedByNetId[netId] = identity;

        MetaverseSpawnPayload payload = BuildPayload(identity, SafeReason(reason, "network_server_spawn"), MetaverseSpawnMessageCodec.MirrorSpawnRoute);
        ServerObjectSpawned?.Invoke(identity, payload);
        if (logLifecycle) Debug.Log("[MetaverseSpawnManager] NetworkServer.Spawn | netId=" + netId + " | prefabId=" + prefabId + " | owner=" + resolvedOwnerConnectionIdText + " | serverOwned=" + resolvedServerOwned);
        return identity;
    }

    public bool TrySpawnPrefab(string prefabId, Vector3 position, Quaternion rotation, int ownerConnectionId, out MetaverseNetworkIdentity identity)
    {
        string ownerConnectionIdText = ownerConnectionId >= 0 ? ownerConnectionId.ToString() : string.Empty;
        return TrySpawnPrefab(prefabId, position, rotation, ownerConnectionId, ownerConnectionIdText, string.Empty, string.Empty, false, out identity);
    }

    public bool TrySpawnPrefab(string prefabId, Vector3 position, Quaternion rotation, out MetaverseNetworkIdentity identity)
    {
        return TrySpawnPrefab(prefabId, position, rotation, -1, string.Empty, string.Empty, string.Empty, true, out identity);
    }

    public bool TrySpawnPrefab(string prefabId, Vector3 position, Quaternion rotation, string ownerConnectionId, string ownerUserId, string ownerPlayerId, out MetaverseNetworkIdentity identity)
    {
        return TrySpawnPrefab(prefabId, position, rotation, ParseConnectionId(ownerConnectionId), ownerConnectionId, ownerUserId, ownerPlayerId, false, out identity);
    }

    public bool TrySpawnServerOwnedPrefab(string prefabId, Vector3 position, Quaternion rotation, out MetaverseNetworkIdentity identity)
    {
        return TrySpawnPrefab(prefabId, position, rotation, -1, string.Empty, string.Empty, string.Empty, true, out identity);
    }

    public bool TrySpawnPrefab(string prefabId, Vector3 position, Quaternion rotation, int ownerConnectionId, string ownerConnectionIdText, string ownerUserId, string ownerPlayerId, bool serverOwned, out MetaverseNetworkIdentity identity)
    {
        identity = null;
        LastSpawnRejectReason = string.Empty;
        if (!CanUseNetworkServerApi("NetworkServer.SpawnPrefab")) return false;
        if (!CanSpawnPrefab(prefabId)) return false;

        EnsureSpawnedRoot();
        GameObject prefab = null;
        prefabRegistry.TryGetPrefab(SafeTrim(prefabId), out prefab);
        GameObject obj = Instantiate(prefab, position, rotation, spawnedRoot);
        identity = EnsureIdentity(obj);
        if (identity != null) identity.AssignPrefabId(SafeTrim(prefabId));
        identity = Spawn(obj, ownerConnectionId, ownerConnectionIdText, ownerUserId, ownerPlayerId, serverOwned, "network_server_spawn_prefab");
        return identity != null;
    }

    public bool CanSpawnPrefab(string prefabId)
    {
        LastSpawnRejectReason = string.Empty;
        if (prefabRegistry == null)
        {
            RejectSpawn("prefab_registry_missing");
            return false;
        }

        if (string.IsNullOrWhiteSpace(prefabId))
        {
            RejectSpawn("prefab_id_empty");
            return false;
        }

        if (!prefabRegistry.TryGetPrefab(prefabId.Trim(), out GameObject prefab) || prefab == null)
        {
            RejectSpawn("prefab_not_registered:" + prefabId.Trim());
            return false;
        }

        return true;
    }

    public void Despawn(GameObject obj)
    {
        Despawn(obj, "server_despawn");
    }

    public void Despawn(GameObject obj, string reason)
    {
        LastDespawnRejectReason = string.Empty;
        if (!CanUseNetworkServerApi("NetworkServer.Despawn")) return;
        if (obj == null)
        {
            RejectDespawn("object_null");
            return;
        }

        MetaverseNetworkIdentity identity = obj.GetComponent<MetaverseNetworkIdentity>();
        if (identity == null)
        {
            RejectDespawn("identity_missing");
            if (destroyOnDespawn) UnityEngine.Object.Destroy(obj);
            return;
        }

        int netId = identity.NetId;
        if (netId > 0) dictSpawnedByNetId.Remove(netId);
        string safeReason = SafeReason(reason, "network_server_despawn");
        ServerObjectDespawned?.Invoke(identity, safeReason);
        identity.MarkDespawned();
        if (logLifecycle) Debug.Log("[MetaverseSpawnManager] NetworkServer.Despawn | netId=" + netId + " | reason=" + safeReason);
        if (destroyOnDespawn) UnityEngine.Object.Destroy(obj);
        else obj.SetActive(false);
    }

    public void Destroy(GameObject obj)
    {
        if (obj == null) return;
        MetaverseNetworkIdentity identity = obj.GetComponent<MetaverseNetworkIdentity>();
        if (identity != null && identity.NetId > 0)
        {
            dictSpawnedByNetId.Remove(identity.NetId);
            ServerObjectDespawned?.Invoke(identity, "network_server_destroy");
            identity.MarkDespawned();
        }
        UnityEngine.Object.Destroy(obj);
    }

    public bool DestroyNetId(int netId)
    {
        return TryDespawnNetId(netId, "network_server_destroy");
    }

    public void RegisterPrefab(GameObject prefab)
    {
        if (prefabRegistry == null)
        {
            Debug.LogWarning("[MetaverseSpawnManager] RegisterPrefab failed. PrefabRegistry is not assigned.");
            return;
        }
        prefabRegistry.RegisterPrefab(prefab);
    }

    public void RegisterPrefab(string prefabId, GameObject prefab)
    {
        if (prefabRegistry == null)
        {
            Debug.LogWarning("[MetaverseSpawnManager] RegisterPrefab failed. PrefabRegistry is not assigned.");
            return;
        }
        prefabRegistry.RegisterPrefab(prefabId, prefab);
    }

    public void UnregisterPrefab(GameObject prefab)
    {
        if (prefabRegistry == null) return;
        prefabRegistry.UnregisterPrefab(prefab);
    }

    public void UnregisterPrefab(string prefabId)
    {
        if (prefabRegistry == null) return;
        prefabRegistry.UnregisterPrefab(prefabId);
    }

    public bool TryDespawnNetId(int netId, string reason)
    {
        if (!TryGetSpawnedObject(netId, out MetaverseNetworkIdentity identity) || identity == null)
        {
            RejectDespawn("net_id_not_found:" + netId);
            return false;
        }
        Despawn(identity.gameObject, reason);
        return true;
    }

    public bool TryGetSpawnedObject(int netId, out MetaverseNetworkIdentity identity)
    {
        return dictSpawnedByNetId.TryGetValue(netId, out identity) && identity != null;
    }

    public MetaverseNetworkIdentity GetSpawnedObject(int netId)
    {
        TryGetSpawnedObject(netId, out MetaverseNetworkIdentity identity);
        return identity;
    }

    public bool ContainsNetId(int netId)
    {
        return dictSpawnedByNetId.ContainsKey(netId);
    }

    public List<MetaverseNetworkIdentity> GetSpawnedObjects()
    {
        return new List<MetaverseNetworkIdentity>(dictSpawnedByNetId.Values);
    }

    public MetaverseSpawnPayload[] BuildSnapshotPayloads()
    {
        List<MetaverseNetworkIdentity> identities = GetSpawnedObjects();
        List<MetaverseSpawnPayload> payloads = new List<MetaverseSpawnPayload>();
        for (int i = 0; i < identities.Count; i++)
        {
            MetaverseSpawnPayload payload = BuildPayload(identities[i], "network_server_spawn_snapshot", MetaverseSpawnMessageCodec.MirrorSnapshotRoute);
            if (payload != null) payloads.Add(payload);
        }
        return payloads.ToArray();
    }

    public void ClearAllSpawned(string reason)
    {
        List<MetaverseNetworkIdentity> spawnedObjects = GetSpawnedObjects();
        for (int i = 0; i < spawnedObjects.Count; i++)
        {
            MetaverseNetworkIdentity identity = spawnedObjects[i];
            if (identity == null) continue;
            int netId = identity.NetId;
            if (netId > 0) dictSpawnedByNetId.Remove(netId);
            identity.MarkDespawned();
            ClientObjectDespawned?.Invoke(netId, SafeTrim(reason));
            if (destroyOnDespawn) UnityEngine.Object.Destroy(identity.gameObject);
            else identity.gameObject.SetActive(false);
        }
        dictSpawnedByNetId.Clear();
    }

    public bool ClientApplySpawn(MetaverseSpawnPayload payload)
    {
        if (payload == null) return false;
        payload.NormalizeDefaults("client_apply_spawn");
        if (!payload.IsValidForSpawn()) return false;
        if (dictSpawnedByNetId.ContainsKey(payload.netId)) return true;
        if (prefabRegistry == null || !prefabRegistry.TryGetPrefab(payload.prefabId, out GameObject prefab) || prefab == null)
        {
            Debug.LogWarning("[MetaverseSpawnManager] Client spawn failed. Prefab not registered | prefabId=" + payload.prefabId);
            return false;
        }

        EnsureSpawnedRoot();
        GameObject obj = Instantiate(prefab, payload.position, payload.rotation, spawnedRoot);
        obj.transform.localScale = payload.scale == Vector3.zero ? prefab.transform.localScale : payload.scale;

        MetaverseNetworkIdentity identity = EnsureIdentity(obj);
        bool resolvedLocalPlayer = payload.localPlayer || IsPayloadOwnedByLocalClient(payload);
        identity.AssignSpawnData(
            payload.prefabId,
            payload.netId,
            payload.ownerConnectionId,
            payload.ownerConnectionIdText,
            payload.ownerUserId,
            payload.ownerPlayerId,
            payload.serverOwned,
            resolvedLocalPlayer);
        dictSpawnedByNetId[payload.netId] = identity;
        nextNetId = Mathf.Max(nextNetId, payload.netId + 1);
        ClientObjectSpawned?.Invoke(identity, payload);
        if (logLifecycle) Debug.Log("[MetaverseSpawnManager] Client spawn applied | netId=" + payload.netId + " | prefabId=" + payload.prefabId + " | mirrorRoute=" + payload.mirrorRoute);
        return true;
    }

    public bool ClientApplyDespawn(int netId, string reason)
    {
        if (!dictSpawnedByNetId.TryGetValue(netId, out MetaverseNetworkIdentity identity) || identity == null) return false;
        dictSpawnedByNetId.Remove(netId);
        identity.MarkDespawned();
        ClientObjectDespawned?.Invoke(netId, SafeTrim(reason));
        if (logLifecycle) Debug.Log("[MetaverseSpawnManager] Client despawn applied | netId=" + netId + " | reason=" + SafeTrim(reason));
        if (destroyOnDespawn) UnityEngine.Object.Destroy(identity.gameObject);
        else identity.gameObject.SetActive(false);
        return true;
    }

    public MetaverseSpawnPayload BuildPayload(MetaverseNetworkIdentity identity)
    {
        return BuildPayload(identity, "network_server_spawn", MetaverseSpawnMessageCodec.MirrorSpawnRoute);
    }

    public MetaverseSpawnPayload BuildPayload(MetaverseNetworkIdentity identity, string reason, string mirrorRoute)
    {
        if (identity == null) return null;
        Transform t = identity.transform;
        MetaverseSpawnPayload payload = new MetaverseSpawnPayload
        {
            netId = identity.NetId,
            prefabId = identity.PrefabId,
            ownerConnectionId = identity.OwnerConnectionId,
            ownerConnectionIdText = identity.OwnerConnectionIdText,
            ownerUserId = identity.OwnerUserId,
            ownerPlayerId = identity.OwnerPlayerId,
            serverOwned = identity.IsServerOwned,
            localPlayer = identity.IsLocalPlayer,
            position = t.position,
            rotation = t.rotation,
            scale = t.localScale,
            roomId = MetaverseNetworkClient.roomId,
            sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
            spawnReason = SafeReason(reason, "network_server_spawn"),
            mirrorRoute = SafeReason(mirrorRoute, MetaverseSpawnMessageCodec.MirrorSpawnRoute),
            sequence = ++spawnSequence,
            spawnedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            sceneObject = false,
            destroyOnDespawn = destroyOnDespawn
        };
        payload.NormalizeDefaults(reason);
        return payload;
    }

    public string GetSpawnDebugSummary()
    {
        return "spawned=" + SpawnedCount +
               " | prefabRegistry=" + (prefabRegistry != null ? "ON" : "OFF") +
               " | spawnedRoot=" + (spawnedRoot != null ? spawnedRoot.name : "NULL") +
               " | lastSpawnReject=" + SafeTrim(LastSpawnRejectReason) +
               " | lastDespawnReject=" + SafeTrim(LastDespawnRejectReason);
    }

    private bool IsPayloadOwnedByLocalClient(MetaverseSpawnPayload payload)
    {
        if (payload == null || Application.isBatchMode) return false;
        return payload.IsOwnedByAny(MetaverseNetworkClient.connectionId, MetaverseNetworkClient.userId, MetaverseNetworkClient.playerId);
    }

    public void SetPrefabRegistry(MetaverseNetworkPrefabRegistry registry)
    {
        prefabRegistry = registry;
        prefabRegistry?.RebuildCache();
    }

    public void SetSpawnedRoot(Transform root)
    {
        spawnedRoot = root;
    }

    private MetaverseNetworkIdentity EnsureIdentity(GameObject obj)
    {
        if (obj == null) return null;
        MetaverseNetworkIdentity identity = obj.GetComponent<MetaverseNetworkIdentity>();
        if (identity == null && autoAddIdentityToSpawnedObjects) identity = obj.AddComponent<MetaverseNetworkIdentity>();
        if (identity == null) Debug.LogWarning("[MetaverseSpawnManager] Object has no MetaverseNetworkIdentity | name=" + obj.name);
        return identity;
    }

    private string ResolvePrefabId(GameObject obj, MetaverseNetworkIdentity identity)
    {
        if (identity != null && !string.IsNullOrWhiteSpace(identity.PrefabId)) return identity.PrefabId;
        if (prefabRegistry != null && prefabRegistry.TryGetPrefabId(obj, out string prefabId)) return prefabId;
        return RemoveCloneSuffix(obj.name).Trim().ToLowerInvariant().Replace(" ", "_");
    }

    private int AllocateNetId()
    {
        while (dictSpawnedByNetId.ContainsKey(nextNetId)) nextNetId++;
        return nextNetId++;
    }

    private void EnsureSpawnedRoot()
    {
        if (spawnedRoot != null || !autoCreateSpawnedRoot) return;
        GameObject root = new GameObject("Metaverse_Spawned_Root");
        spawnedRoot = root.transform;
    }

    private bool CanUseNetworkServerApi(string apiName)
    {
        if (!requireServerRuntimeForNetworkServerApi) return true;
        if (Application.isBatchMode) return true;
        if (allowEditorSimulation && Application.isEditor) return true;
        RejectSpawn("server_only_api:" + apiName);
        return false;
    }

    private void RejectSpawn(string reason)
    {
        LastSpawnRejectReason = SafeTrim(reason);
        if (logLifecycle) Debug.LogWarning("[MetaverseSpawnManager] Spawn rejected | reason=" + LastSpawnRejectReason);
    }

    private void RejectDespawn(string reason)
    {
        LastDespawnRejectReason = SafeTrim(reason);
        if (logLifecycle) Debug.LogWarning("[MetaverseSpawnManager] Despawn rejected | reason=" + LastDespawnRejectReason);
    }

    private int ParseConnectionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return -1;
        return int.TryParse(value.Trim(), out int parsed) ? parsed : -1;
    }

    private string RemoveCloneSuffix(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.Replace("(Clone)", string.Empty).Trim();
    }

    private string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private string SafeReason(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
