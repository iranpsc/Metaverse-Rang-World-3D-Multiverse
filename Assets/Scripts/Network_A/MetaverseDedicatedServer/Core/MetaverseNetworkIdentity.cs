using System;
using UnityEngine;

[DisallowMultipleComponent]
public class MetaverseNetworkIdentity : MonoBehaviour
{
    [Header("Identity")]
    [SerializeField] private string prefabId = string.Empty;
    [SerializeField] private int netId;
    [SerializeField] private int ownerConnectionId = -1;
    [SerializeField] private bool serverOwned = true;
    [SerializeField] private bool localPlayer;
    [SerializeField] private bool spawned;

    [Header("Debug")]
    [SerializeField] private bool logLifecycle;

    public string PrefabId => prefabId;
    public int NetId => netId;
    public int OwnerConnectionId => ownerConnectionId;
    public bool IsServerOwned => serverOwned;
    public bool IsLocalPlayer => localPlayer;
    public bool IsSpawned => spawned;

    public event Action<MetaverseNetworkIdentity> Spawned;
    public event Action<MetaverseNetworkIdentity> Despawned;

    public void AssignSpawnData(string newPrefabId, int newNetId, int newOwnerConnectionId, bool newServerOwned, bool newLocalPlayer)
    {
        prefabId = SafeTrim(newPrefabId);
        netId = Mathf.Max(0, newNetId);
        ownerConnectionId = newOwnerConnectionId;
        serverOwned = newServerOwned;
        localPlayer = newLocalPlayer;
        spawned = true;
        if (logLifecycle) Debug.Log($"[MetaverseNetworkIdentity] Spawn assigned | netId={netId} | prefabId={prefabId} | owner={ownerConnectionId}");
        Spawned?.Invoke(this);
    }

    public void AssignPrefabId(string newPrefabId)
    {
        prefabId = SafeTrim(newPrefabId);
    }

    public void AssignNetId(int newNetId)
    {
        netId = Mathf.Max(0, newNetId);
    }

    public void SetOwnerConnectionId(int newOwnerConnectionId)
    {
        ownerConnectionId = newOwnerConnectionId;
    }

    public void SetServerOwned(bool value)
    {
        serverOwned = value;
    }

    public void SetLocalPlayer(bool value)
    {
        localPlayer = value;
    }

    public bool IsOwnedBy(int connectionId)
    {
        return ownerConnectionId >= 0 && ownerConnectionId == connectionId;
    }

    public void MarkSpawned()
    {
        spawned = true;
        if (logLifecycle) Debug.Log($"[MetaverseNetworkIdentity] Mark spawned | netId={netId} | prefabId={prefabId}");
        Spawned?.Invoke(this);
    }

    public void MarkDespawned()
    {
        if (!spawned) return;
        spawned = false;
        if (logLifecycle) Debug.Log($"[MetaverseNetworkIdentity] Mark despawned | netId={netId} | prefabId={prefabId}");
        Despawned?.Invoke(this);
    }

    public void ClearRuntimeState()
    {
        MarkDespawned();
        netId = 0;
        ownerConnectionId = -1;
        serverOwned = true;
        localPlayer = false;
    }

    public void ResetForPool()
    {
        ClearRuntimeState();
        gameObject.SetActive(false);
    }

    private string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
