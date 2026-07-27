using System;
using UnityEngine;

[DisallowMultipleComponent]
public class MetaverseNetworkIdentity : MonoBehaviour
{
    [Header("Identity")]
    [SerializeField] private string prefabId = string.Empty;
    [SerializeField] private int netId;
    [SerializeField] private int ownerConnectionId = -1;
    [SerializeField] private string ownerConnectionIdText = string.Empty;
    [SerializeField] private string ownerUserId = string.Empty;
    [SerializeField] private string ownerPlayerId = string.Empty;
    [SerializeField] private string roomId = string.Empty;
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
    public string OwnerConnectionIdText => ownerConnectionIdText;
    public string OwnerUserId => ownerUserId;
    public string OwnerPlayerId => ownerPlayerId;
    public string RoomId => roomId;
    public bool IsServerOwned => serverOwned;
    public bool IsLocalPlayer => localPlayer;
    public bool IsSpawned => spawned;
    public bool IsServer => isServer;
    public bool IsClient => isClient;
    public bool HasAuthority => hasAuthority;

    public bool HasValidNetId => netId > 0;
    public bool IsServerOnly => isServer && !isClient;
    public bool IsClientOnly => isClient && !isServer;
    public bool IsOwned => !serverOwned && (!string.IsNullOrWhiteSpace(ownerConnectionIdText) || !string.IsNullOrWhiteSpace(ownerUserId) || !string.IsNullOrWhiteSpace(ownerPlayerId) || ownerConnectionId >= 0);
    public bool IsLocalOwner => IsLocalClientOwner(ownerConnectionIdText, ownerUserId, ownerPlayerId);
    public bool HasStartedCallbacks => callbacksStarted;
    public bool HasStartedAuthorityCallbacks => authorityStarted;
    public bool HasStartedLocalPlayerCallbacks => localPlayerStarted;

    public event Action<MetaverseNetworkIdentity> Spawned;
    public event Action<MetaverseNetworkIdentity> Despawned;
    public event Action<MetaverseNetworkIdentity> OwnershipChanged;

    public void AssignSpawnData(string newPrefabId, int newNetId, int newOwnerConnectionId, bool newServerOwned, bool newLocalPlayer)
    {
        AssignSpawnData(newPrefabId, newNetId, newOwnerConnectionId, string.Empty, string.Empty, string.Empty, string.Empty, newServerOwned, newLocalPlayer);
    }

    public void AssignSpawnData(
        string newPrefabId,
        int newNetId,
        int newOwnerConnectionId,
        string newOwnerConnectionIdText,
        string newOwnerUserId,
        string newOwnerPlayerId,
        bool newServerOwned,
        bool newLocalPlayer)
    {
        AssignSpawnData(newPrefabId, newNetId, newOwnerConnectionId, newOwnerConnectionIdText, newOwnerUserId, newOwnerPlayerId, string.Empty, newServerOwned, newLocalPlayer);
    }

    public void AssignSpawnData(
        string newPrefabId,
        int newNetId,
        int newOwnerConnectionId,
        string newOwnerConnectionIdText,
        string newOwnerUserId,
        string newOwnerPlayerId,
        string newRoomId,
        bool newServerOwned,
        bool newLocalPlayer)
    {
        prefabId = SafeTrim(newPrefabId);
        netId = Mathf.Max(0, newNetId);
        ownerConnectionId = newOwnerConnectionId;
        ownerConnectionIdText = SafeTrim(newOwnerConnectionIdText);
        if (ownerConnectionId < 0) ownerConnectionId = ParseConnectionId(ownerConnectionIdText);
        ownerUserId = SafeTrim(newOwnerUserId);
        ownerPlayerId = SafeTrim(newOwnerPlayerId);
        roomId = SafeTrim(newRoomId);
        serverOwned = newServerOwned;
        bool resolvedLocalPlayer = newLocalPlayer || IsLocalClientOwner(ownerConnectionIdText, ownerUserId, ownerPlayerId);
        ApplyDefaultRuntimeRole(resolvedLocalPlayer);
        spawned = true;
        if (logLifecycle) Debug.Log($"[MetaverseNetworkIdentity] Spawn assigned | netId={netId} | prefabId={prefabId} | ownerUserId={ownerUserId}");
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
        ownerConnectionIdText = newOwnerConnectionId >= 0 ? newOwnerConnectionId.ToString() : string.Empty;
    }

    public void SetOwnerInfo(string newOwnerConnectionIdText, string newOwnerUserId, string newOwnerPlayerId, bool newServerOwned)
    {
        string previousOwnerConnectionId = ownerConnectionIdText;
        string previousOwnerUserId = ownerUserId;
        string previousOwnerPlayerId = ownerPlayerId;

        ownerConnectionIdText = SafeTrim(newOwnerConnectionIdText);
        ownerConnectionId = ParseConnectionId(ownerConnectionIdText);
        ownerUserId = SafeTrim(newOwnerUserId);
        ownerPlayerId = SafeTrim(newOwnerPlayerId);
        serverOwned = newServerOwned;

        bool resolvedLocalPlayer = IsLocalClientOwner(ownerConnectionIdText, ownerUserId, ownerPlayerId);
        bool authorityRole = isServer || resolvedLocalPlayer;
        SetNetworkRole(isServer, isClient, authorityRole, resolvedLocalPlayer);

        if (spawned)
        {
            InvokeOwnershipChangedCallbacks(previousOwnerConnectionId, ownerConnectionIdText, previousOwnerUserId, ownerUserId, previousOwnerPlayerId, ownerPlayerId);
        }
    }

    public void SetOwnerInfo(int newOwnerConnectionId, string newOwnerUserId, string newOwnerPlayerId, bool newServerOwned)
    {
        SetOwnerInfo(newOwnerConnectionId >= 0 ? newOwnerConnectionId.ToString() : string.Empty, newOwnerUserId, newOwnerPlayerId, newServerOwned);
    }

    public void ClearOwnerInfo(bool makeServerOwned = true)
    {
        SetOwnerInfo(string.Empty, string.Empty, string.Empty, makeServerOwned);
    }

    public void SetRoomId(string newRoomId)
    {
        roomId = SafeTrim(newRoomId);
    }

    public bool IsRoom(string targetRoomId)
    {
        string safeTargetRoomId = SafeTrim(targetRoomId);
        if (string.IsNullOrWhiteSpace(safeTargetRoomId)) return false;
        return string.Equals(roomId, safeTargetRoomId, StringComparison.Ordinal);
    }

    public void SetServerOwned(bool value)
    {
        serverOwned = value;
        if (value && spawned)
        {
            SetNetworkRole(isServer, isClient, isServer, false);
        }
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

    public void RefreshRuntimeRoleFromCurrentOwner()
    {
        bool resolvedLocalPlayer = IsLocalClientOwner(ownerConnectionIdText, ownerUserId, ownerPlayerId);
        SetNetworkRole(Application.isBatchMode, !Application.isBatchMode, Application.isBatchMode || resolvedLocalPlayer, resolvedLocalPlayer);
    }

    public bool IsOwnedBy(int connectionId)
    {
        return ownerConnectionId >= 0 && ownerConnectionId == connectionId;
    }

    public bool IsOwnedBy(string connectionId)
    {
        return !string.IsNullOrWhiteSpace(connectionId) && string.Equals(ownerConnectionIdText, connectionId.Trim(), StringComparison.Ordinal);
    }

    public bool IsOwnedByUser(string userId)
    {
        return !string.IsNullOrWhiteSpace(userId) && string.Equals(ownerUserId, userId.Trim(), StringComparison.Ordinal);
    }

    public bool IsOwnedByPlayer(string playerId)
    {
        return !string.IsNullOrWhiteSpace(playerId) && string.Equals(ownerPlayerId, playerId.Trim(), StringComparison.Ordinal);
    }

    public bool IsOwnedByAny(string connectionId, string userId, string playerId)
    {
        if (!string.IsNullOrWhiteSpace(connectionId) && IsOwnedBy(connectionId)) return true;
        if (!string.IsNullOrWhiteSpace(userId) && IsOwnedByUser(userId)) return true;
        if (!string.IsNullOrWhiteSpace(playerId) && IsOwnedByPlayer(playerId)) return true;
        return false;
    }

    public bool IsOwnedByLocalClient()
    {
        return IsLocalClientOwner(ownerConnectionIdText, ownerUserId, ownerPlayerId);
    }

    public void MarkSpawned()
    {
        if (!spawned)
        {
            ApplyDefaultRuntimeRole(localPlayer || IsLocalClientOwner(ownerConnectionIdText, ownerUserId, ownerPlayerId));
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
        ownerConnectionIdText = string.Empty;
        ownerUserId = string.Empty;
        ownerPlayerId = string.Empty;
        roomId = string.Empty;
        serverOwned = true;
        localPlayer = false;
        isServer = false;
        isClient = false;
        hasAuthority = false;
        callbacksStarted = false;
        authorityStarted = false;
        localPlayerStarted = false;
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

    private bool IsLocalClientOwner(string connectionIdText, string userId, string playerId)
    {
        if (Application.isBatchMode) return false;

        if (!string.IsNullOrWhiteSpace(connectionIdText) &&
            string.Equals(SafeTrim(connectionIdText), MetaverseNetworkClient.connectionId, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(userId) &&
            string.Equals(SafeTrim(userId), MetaverseNetworkClient.userId, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(playerId) &&
            string.Equals(SafeTrim(playerId), MetaverseNetworkClient.playerId, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
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

    private void InvokeOwnershipChangedCallbacks(string previousOwnerConnectionId, string newOwnerConnectionId, string previousOwnerUserId, string newOwnerUserId, string previousOwnerPlayerId, string newOwnerPlayerId)
    {
        bool isLocalOwner = IsLocalClientOwner(newOwnerConnectionId, newOwnerUserId, newOwnerPlayerId);
        MetaverseNetworkBehaviour[] behaviours = GetNetworkBehaviours();
        if (behaviours == null) return;

        for (int i = 0; i < behaviours.Length; i++)
        {
            MetaverseNetworkBehaviour behaviour = behaviours[i];
            if (behaviour == null) continue;

            try
            {
                behaviour.OnOwnershipChanged(previousOwnerUserId, newOwnerUserId, previousOwnerPlayerId, newOwnerPlayerId, isLocalOwner);
                if (logBehaviourCallbacks)
                {
                    Debug.Log("[MetaverseNetworkIdentity] Behaviour callback | netId=" + netId +
                              " | behaviour=" + behaviour.GetType().Name + " | callback=OnOwnershipChanged");
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, behaviour);
            }
        }

        OwnershipChanged?.Invoke(this);
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

    private int ParseConnectionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return -1;
        return int.TryParse(value.Trim(), out int parsed) ? parsed : -1;
    }

    private string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
