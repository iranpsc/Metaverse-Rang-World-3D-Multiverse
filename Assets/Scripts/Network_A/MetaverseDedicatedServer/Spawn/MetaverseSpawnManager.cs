using System;
using System.Collections.Generic;
using UnityEngine;

public class MetaverseSpawnManager : MonoBehaviour
{
    [Serializable]
    public class MetaverseSpawnPayload
    {
        public int netId;
        public string prefabId;
        public int ownerConnectionId;
        public bool serverOwned;
        public bool localPlayer;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
    }

    [Header("Registry")]
    [SerializeField] private MetaverseNetworkPrefabRegistry prefabRegistry;
    [SerializeField] private Transform spawnedRoot;

    [Header("Runtime")]
    [SerializeField] private bool autoCreateSpawnedRoot = true;
    [SerializeField] private bool autoAddIdentityToSpawnedObjects = true;
    [SerializeField] private bool destroyOnDespawn = true;
    [SerializeField] private bool logLifecycle = true;

    private readonly Dictionary<int, MetaverseNetworkIdentity> dictSpawnedByNetId = new Dictionary<int, MetaverseNetworkIdentity>();
    private int nextNetId = 1;

    public static MetaverseSpawnManager Instance { get; private set; }
    public MetaverseNetworkPrefabRegistry PrefabRegistry => prefabRegistry;
    public int SpawnedCount => dictSpawnedByNetId.Count;

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
        if (obj == null)
        {
            Debug.LogWarning("[MetaverseSpawnManager] Spawn failed. Object is null.");
            return null;
        }

        MetaverseNetworkIdentity identity = EnsureIdentity(obj);
        if (identity == null) return null;
        if (identity.IsSpawned && identity.NetId > 0)
        {
            if (logLifecycle) Debug.LogWarning($"[MetaverseSpawnManager] Object is already spawned | netId={identity.NetId} | name={obj.name}");
            return identity;
        }

        string prefabId = ResolvePrefabId(obj, identity);
        int netId = AllocateNetId();
        identity.AssignSpawnData(prefabId, netId, ownerConnectionId, true, false);
        dictSpawnedByNetId[netId] = identity;

        MetaverseSpawnPayload payload = BuildPayload(identity);
        ServerObjectSpawned?.Invoke(identity, payload);
        if (logLifecycle) Debug.Log($"[MetaverseSpawnManager] Spawn | netId={netId} | prefabId={prefabId} | owner={ownerConnectionId}");
        return identity;
    }

    public bool TrySpawnPrefab(string prefabId, Vector3 position, Quaternion rotation, int ownerConnectionId, out MetaverseNetworkIdentity identity)
    {
        identity = null;
        if (prefabRegistry == null)
        {
            Debug.LogWarning("[MetaverseSpawnManager] Spawn prefab failed. PrefabRegistry is not assigned.");
            return false;
        }

        if (!prefabRegistry.TryGetPrefab(prefabId, out GameObject prefab) || prefab == null)
        {
            Debug.LogWarning($"[MetaverseSpawnManager] Spawn prefab failed. PrefabId not registered | prefabId={prefabId}");
            return false;
        }

        EnsureSpawnedRoot();
        GameObject obj = Instantiate(prefab, position, rotation, spawnedRoot);
        identity = EnsureIdentity(obj);
        if (identity != null) identity.AssignPrefabId(prefabId);
        identity = Spawn(obj, ownerConnectionId);
        return identity != null;
    }

    public void Despawn(GameObject obj)
    {
        Despawn(obj, "server_despawn");
    }

    public void Despawn(GameObject obj, string reason)
    {
        if (obj == null) return;
        MetaverseNetworkIdentity identity = obj.GetComponent<MetaverseNetworkIdentity>();
        if (identity == null)
        {
            if (destroyOnDespawn) Destroy(obj);
            return;
        }

        int netId = identity.NetId;
        if (netId > 0) dictSpawnedByNetId.Remove(netId);
        identity.MarkDespawned();
        ServerObjectDespawned?.Invoke(identity, SafeTrim(reason));
        if (logLifecycle) Debug.Log($"[MetaverseSpawnManager] Despawn | netId={netId} | reason={SafeTrim(reason)}");
        if (destroyOnDespawn) Destroy(obj);
        else obj.SetActive(false);
    }

    public void Destroy(GameObject obj)
    {
        if (obj == null) return;
        MetaverseNetworkIdentity identity = obj.GetComponent<MetaverseNetworkIdentity>();
        if (identity != null && identity.NetId > 0) dictSpawnedByNetId.Remove(identity.NetId);
        UnityEngine.Object.Destroy(obj);
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
        if (!dictSpawnedByNetId.TryGetValue(netId, out MetaverseNetworkIdentity identity) || identity == null) return false;
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

    public List<MetaverseNetworkIdentity> GetSpawnedObjects()
    {
        return new List<MetaverseNetworkIdentity>(dictSpawnedByNetId.Values);
    }

    public void ClearAllSpawned(string reason)
    {
        List<MetaverseNetworkIdentity> spawnedObjects = GetSpawnedObjects();
        for (int i = 0; i < spawnedObjects.Count; i++)
        {
            MetaverseNetworkIdentity identity = spawnedObjects[i];
            if (identity == null) continue;
            Despawn(identity.gameObject, reason);
        }
        dictSpawnedByNetId.Clear();
    }

    public bool ClientApplySpawn(MetaverseSpawnPayload payload)
    {
        if (payload == null || payload.netId <= 0 || string.IsNullOrWhiteSpace(payload.prefabId)) return false;
        if (dictSpawnedByNetId.ContainsKey(payload.netId)) return true;
        if (prefabRegistry == null || !prefabRegistry.TryGetPrefab(payload.prefabId, out GameObject prefab) || prefab == null)
        {
            Debug.LogWarning($"[MetaverseSpawnManager] Client spawn failed. Prefab not registered | prefabId={payload.prefabId}");
            return false;
        }

        EnsureSpawnedRoot();
        GameObject obj = Instantiate(prefab, payload.position, payload.rotation, spawnedRoot);
        obj.transform.localScale = payload.scale == Vector3.zero ? prefab.transform.localScale : payload.scale;

        MetaverseNetworkIdentity identity = EnsureIdentity(obj);
        identity.AssignSpawnData(payload.prefabId, payload.netId, payload.ownerConnectionId, payload.serverOwned, payload.localPlayer);
        dictSpawnedByNetId[payload.netId] = identity;
        nextNetId = Mathf.Max(nextNetId, payload.netId + 1);
        ClientObjectSpawned?.Invoke(identity, payload);
        if (logLifecycle) Debug.Log($"[MetaverseSpawnManager] Client spawn applied | netId={payload.netId} | prefabId={payload.prefabId}");
        return true;
    }

    public bool ClientApplyDespawn(int netId, string reason)
    {
        if (!dictSpawnedByNetId.TryGetValue(netId, out MetaverseNetworkIdentity identity) || identity == null) return false;
        dictSpawnedByNetId.Remove(netId);
        identity.MarkDespawned();
        ClientObjectDespawned?.Invoke(netId, SafeTrim(reason));
        if (logLifecycle) Debug.Log($"[MetaverseSpawnManager] Client despawn applied | netId={netId} | reason={SafeTrim(reason)}");
        if (destroyOnDespawn) UnityEngine.Object.Destroy(identity.gameObject);
        else identity.gameObject.SetActive(false);
        return true;
    }

    public MetaverseSpawnPayload BuildPayload(MetaverseNetworkIdentity identity)
    {
        if (identity == null) return null;
        Transform t = identity.transform;
        return new MetaverseSpawnPayload
        {
            netId = identity.NetId,
            prefabId = identity.PrefabId,
            ownerConnectionId = identity.OwnerConnectionId,
            serverOwned = identity.IsServerOwned,
            localPlayer = identity.IsLocalPlayer,
            position = t.position,
            rotation = t.rotation,
            scale = t.localScale
        };
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
        if (identity == null) Debug.LogWarning($"[MetaverseSpawnManager] Object has no MetaverseNetworkIdentity | name={obj.name}");
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

    private string RemoveCloneSuffix(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.Replace("(Clone)", string.Empty).Trim();
    }

    private string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
