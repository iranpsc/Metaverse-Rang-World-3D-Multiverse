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

    [Header("Network Role")]
    [SerializeField] private bool isServer;
    [SerializeField] private bool isClient;
    [SerializeField] private bool hasAuthority;

    [Header("Debug")]
    [SerializeField] private bool logLifecycle;
    [SerializeField] private bool logBehaviourCallbacks;

    private MetaverseNetworkBehaviour[] cachedBehaviours;
    private bool callbacksStarted;
    private bool authorityStarted;
    private bool localPlayerStarted;

    public string PrefabId => prefabId;
    public int NetId => netId;
    public int OwnerConnectionId => ownerConnectionId;
    public bool IsServerOwned => serverOwned;
    public bool IsLocalPlayer => localPlayer;
    public bool IsSpawned => spawned;
    public bool IsServer => isServer;
    public bool IsClient => isClient;
    public bool HasAuthority => hasAuthority;

    public event Action<MetaverseNetworkIdentity> Spawned;
    public event Action<MetaverseNetworkIdentity> Despawned;

    public void AssignSpawnData(string newPrefabId, int newNetId, int newOwnerConnectionId, bool newServerOwned, bool newLocalPlayer)
    {
        prefabId = SafeTrim(newPrefabId);
        netId = Mathf.Max(0, newNetId);
        ownerConnectionId = newOwnerConnectionId;
        serverOwned = newServerOwned;
        localPlayer = newLocalPlayer;
        ApplyDefaultRuntimeRole(newLocalPlayer);
        spawned = true;
        if (logLifecycle) Debug.Log($"[MetaverseNetworkIdentity] Spawn assigned | netId={netId} | prefabId={prefabId} | owner={ownerConnectionId}");
        InvokeStartCallbacks();
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
        if (spawned)
        {
            bool shouldHaveAuthority = isServer || localPlayer;
            SetNetworkRole(isServer, isClient, shouldHaveAuthority, localPlayer);
        }
    }

    public void SetNetworkRole(bool serverRole, bool clientRole, bool authorityRole, bool localPlayerRole)
    {
        bool previousAuthority = hasAuthority;
        bool previousLocalPlayer = localPlayer;

        isServer = serverRole;
        isClient = clientRole;
        hasAuthority = authorityRole;
        localPlayer = localPlayerRole;

        if (!spawned || !callbacksStarted) return;

        if (!previousAuthority && hasAuthority)
        {
            InvokeAuthorityStartCallbacks();
        }
        else if (previousAuthority && !hasAuthority)
        {
            InvokeAuthorityStopCallbacks();
        }

        if (!previousLocalPlayer && localPlayer)
        {
            InvokeLocalPlayerStartCallbacks();
        }
        else if (previousLocalPlayer && !localPlayer)
        {
            InvokeLocalPlayerStopCallbacks();
        }
    }

    public bool IsOwnedBy(int connectionId)
    {
        return ownerConnectionId >= 0 && ownerConnectionId == connectionId;
    }

    public void MarkSpawned()
    {
        if (!spawned)
        {
            ApplyDefaultRuntimeRole(localPlayer);
        }

        spawned = true;
        if (logLifecycle) Debug.Log($"[MetaverseNetworkIdentity] Mark spawned | netId={netId} | prefabId={prefabId}");
        InvokeStartCallbacks();
        Spawned?.Invoke(this);
    }

    public void MarkDespawned()
    {
        if (!spawned) return;
        InvokeStopCallbacks();
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
        isServer = false;
        isClient = false;
        hasAuthority = false;
    }

    public void ResetForPool()
    {
        ClearRuntimeState();
        gameObject.SetActive(false);
    }

    public MetaverseNetworkBehaviour[] GetNetworkBehaviours()
    {
        if (cachedBehaviours == null) cachedBehaviours = GetComponents<MetaverseNetworkBehaviour>();
        return cachedBehaviours;
    }

    public void RefreshNetworkBehaviours()
    {
        cachedBehaviours = GetComponents<MetaverseNetworkBehaviour>();
    }

    private void ApplyDefaultRuntimeRole(bool newLocalPlayer)
    {
        bool serverRole = Application.isBatchMode;
        bool clientRole = !serverRole;
        bool authorityRole = serverRole || newLocalPlayer;
        isServer = serverRole;
        isClient = clientRole;
        hasAuthority = authorityRole;
        localPlayer = newLocalPlayer;
    }

    private void InvokeStartCallbacks()
    {
        if (callbacksStarted) return;
        callbacksStarted = true;
        RefreshNetworkBehaviours();
        MetaverseNetworkBehaviour[] behaviours = GetNetworkBehaviours();

        if (isServer) InvokeOnBehaviours(behaviours, "OnStartServer", x => x.OnStartServer());
        if (isClient) InvokeOnBehaviours(behaviours, "OnStartClient", x => x.OnStartClient());
        if (hasAuthority) InvokeAuthorityStartCallbacks();
        if (localPlayer) InvokeLocalPlayerStartCallbacks();
        InvokeOnBehaviours(behaviours, "OnNetworkSpawn", x => x.OnNetworkSpawn());
    }

    private void InvokeStopCallbacks()
    {
        if (!callbacksStarted) return;
        MetaverseNetworkBehaviour[] behaviours = GetNetworkBehaviours();

        InvokeOnBehaviours(behaviours, "OnNetworkDespawn", x => x.OnNetworkDespawn());
        if (localPlayerStarted) InvokeLocalPlayerStopCallbacks();
        if (authorityStarted) InvokeAuthorityStopCallbacks();
        if (isClient) InvokeOnBehaviours(behaviours, "OnStopClient", x => x.OnStopClient());
        if (isServer) InvokeOnBehaviours(behaviours, "OnStopServer", x => x.OnStopServer());

        callbacksStarted = false;
    }

    private void InvokeAuthorityStartCallbacks()
    {
        if (authorityStarted) return;
        authorityStarted = true;
        InvokeOnBehaviours(GetNetworkBehaviours(), "OnStartAuthority", x => x.OnStartAuthority());
    }

    private void InvokeAuthorityStopCallbacks()
    {
        if (!authorityStarted) return;
        InvokeOnBehaviours(GetNetworkBehaviours(), "OnStopAuthority", x => x.OnStopAuthority());
        authorityStarted = false;
    }

    private void InvokeLocalPlayerStartCallbacks()
    {
        if (localPlayerStarted) return;
        localPlayerStarted = true;
        InvokeOnBehaviours(GetNetworkBehaviours(), "OnStartLocalPlayer", x => x.OnStartLocalPlayer());
    }

    private void InvokeLocalPlayerStopCallbacks()
    {
        if (!localPlayerStarted) return;
        InvokeOnBehaviours(GetNetworkBehaviours(), "OnStopLocalPlayer", x => x.OnStopLocalPlayer());
        localPlayerStarted = false;
    }

    private void InvokeOnBehaviours(MetaverseNetworkBehaviour[] behaviours, string callbackName, Action<MetaverseNetworkBehaviour> callback)
    {
        if (behaviours == null || callback == null) return;

        for (int i = 0; i < behaviours.Length; i++)
        {
            MetaverseNetworkBehaviour behaviour = behaviours[i];
            if (behaviour == null) continue;

            try
            {
                callback.Invoke(behaviour);
                if (logBehaviourCallbacks)
                {
                    Debug.Log("[MetaverseNetworkIdentity] Behaviour callback | netId=" + netId +
                              " | behaviour=" + behaviour.GetType().Name + " | callback=" + callbackName);
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, behaviour);
            }
        }
    }

    private string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
