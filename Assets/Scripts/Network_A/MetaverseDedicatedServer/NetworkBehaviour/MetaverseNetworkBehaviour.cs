using Network_A.GameServer.Players;
using UnityEngine;

public abstract class MetaverseNetworkBehaviour : MonoBehaviour
{
    private const string EmptyJson = "{}";
    private MetaverseNetworkIdentity cachedIdentity;

    public MetaverseNetworkIdentity NetworkIdentity
    {
        get
        {
            if (cachedIdentity == null) cachedIdentity = GetComponent<MetaverseNetworkIdentity>();
            return cachedIdentity;
        }
    }

    public MetaverseNetworkIdentity netIdentity => NetworkIdentity;
    public int netId => NetworkIdentity != null ? NetworkIdentity.NetId : 0;
    public string prefabId => NetworkIdentity != null ? NetworkIdentity.PrefabId : string.Empty;
    public string ownerConnectionId => NetworkIdentity != null ? NetworkIdentity.OwnerConnectionIdText : string.Empty;
    public string ownerUserId => NetworkIdentity != null ? NetworkIdentity.OwnerUserId : string.Empty;
    public string ownerPlayerId => NetworkIdentity != null ? NetworkIdentity.OwnerPlayerId : string.Empty;
    public bool isServer => NetworkIdentity != null && NetworkIdentity.IsServer;
    public bool isClient => NetworkIdentity != null && NetworkIdentity.IsClient;
    public bool isLocalPlayer => NetworkIdentity != null && NetworkIdentity.IsLocalPlayer;
    public bool hasAuthority => NetworkIdentity != null && NetworkIdentity.HasAuthority;
    public bool isOwned => hasAuthority;
    public bool isSpawned => NetworkIdentity != null && NetworkIdentity.IsSpawned;
    public bool isServerOnly => isServer && !isClient;
    public bool isClientOnly => isClient && !isServer;
    public bool isLocalOwner => NetworkIdentity != null && MetaverseNetworkClient.IsLocalOwner(NetworkIdentity);

    protected virtual void Awake()
    {
        RefreshNetworkIdentityCache();
    }

    protected void RefreshNetworkIdentityCache()
    {
        cachedIdentity = GetComponent<MetaverseNetworkIdentity>();
    }

    public bool TryGetNetworkIdentity(out MetaverseNetworkIdentity identity)
    {
        identity = NetworkIdentity;
        return identity != null;
    }

    public bool CanSendCommand(string commandName)
    {
        return NetworkIdentity != null && MetaverseNetworkRpcBridge.Instance != null && !string.IsNullOrWhiteSpace(commandName);
    }

    public bool CanSendOwnerCommand(string commandName)
    {
        return CanSendCommand(commandName) && (hasAuthority || isLocalPlayer || isLocalOwner);
    }

    public bool CanSendClientRpc(string rpcName)
    {
        return isServer && NetworkIdentity != null && MetaverseNetworkRpcBridge.Instance != null && !string.IsNullOrWhiteSpace(rpcName);
    }

    public bool CanSendTargetRpc(string targetConnectionId, string rpcName)
    {
        return CanSendClientRpc(rpcName) && !string.IsNullOrWhiteSpace(targetConnectionId);
    }

    public bool CanSetSyncVar(string syncKey)
    {
        return isServer && NetworkIdentity != null && MetaverseNetworkStateSyncBridge.Instance != null && !string.IsNullOrWhiteSpace(syncKey);
    }

    public bool CanSyncNetworkTransform()
    {
        return isServer && NetworkIdentity != null && MetaverseNetworkStateSyncBridge.Instance != null;
    }

    public bool CanAssignClientAuthority(string targetConnectionId)
    {
        return isServer && NetworkIdentity != null && MetaverseNetworkOwnershipBridge.Instance != null && !string.IsNullOrWhiteSpace(targetConnectionId);
    }

    public bool CanSendOwnerInput()
    {
        return NetworkIdentity != null && MetaverseNetworkPlayerMovementBridge.Instance != null && (hasAuthority || isLocalPlayer || isLocalOwner);
    }

    public bool SendCommand(string commandName, string payloadJson = "")
    {
        if (!CanSendCommand(commandName)) return false;
        return MetaverseNetworkRpcBridge.Instance.SendCommand(NetworkIdentity, SafeTrim(commandName), SafeJson(payloadJson));
    }

    public bool SendOwnerCommand(string commandName, string payloadJson = "")
    {
        if (!CanSendOwnerCommand(commandName)) return false;
        return MetaverseNetworkRpcBridge.Instance.SendCommand(NetworkIdentity, SafeTrim(commandName), SafeJson(payloadJson));
    }

    public bool SendCommandFromOwner(string commandName, string payloadJson = "")
    {
        return SendOwnerCommand(commandName, payloadJson);
    }

    public bool Cmd(string commandName, string payloadJson = "")
    {
        return SendCommand(commandName, payloadJson);
    }

    public bool OwnerCmd(string commandName, string payloadJson = "")
    {
        return SendOwnerCommand(commandName, payloadJson);
    }

    public bool SendClientRpc(string rpcName, string payloadJson = "")
    {
        if (!CanSendClientRpc(rpcName)) return false;
        return MetaverseNetworkRpcBridge.Instance.SendClientRpc(NetworkIdentity, SafeTrim(rpcName), SafeJson(payloadJson));
    }

    public bool Rpc(string rpcName, string payloadJson = "")
    {
        return SendClientRpc(rpcName, payloadJson);
    }

    public bool SendTargetRpc(string targetConnectionId, string rpcName, string payloadJson = "")
    {
        if (!CanSendTargetRpc(targetConnectionId, rpcName)) return false;
        return MetaverseNetworkRpcBridge.Instance.SendTargetRpc(NetworkIdentity, SafeTrim(targetConnectionId), SafeTrim(rpcName), SafeJson(payloadJson));
    }

    public bool TargetRpc(string targetConnectionId, string rpcName, string payloadJson = "")
    {
        return SendTargetRpc(targetConnectionId, rpcName, payloadJson);
    }

    public bool SetSyncVar(string syncKey, string valueJson = "")
    {
        if (!CanSetSyncVar(syncKey)) return false;
        return MetaverseNetworkStateSyncBridge.Instance.SetSyncVar(NetworkIdentity, SafeTrim(syncKey), SafeJson(valueJson));
    }

    public bool SyncVar(string syncKey, string valueJson = "")
    {
        return SetSyncVar(syncKey, valueJson);
    }

    public bool SyncNetworkTransform()
    {
        if (!CanSyncNetworkTransform()) return false;
        return MetaverseNetworkStateSyncBridge.Instance.SendNetworkTransform(NetworkIdentity);
    }

    public bool SyncTransform()
    {
        return SyncNetworkTransform();
    }

    public bool AssignClientAuthority(string targetConnectionId, string targetUserId = "", string targetPlayerId = "")
    {
        if (!CanAssignClientAuthority(targetConnectionId)) return false;
        return MetaverseNetworkOwnershipBridge.Instance.SetOwner(NetworkIdentity, SafeTrim(targetConnectionId), SafeTrim(targetUserId), SafeTrim(targetPlayerId), false, "assign_client_authority");
    }

    public bool AssignAuthority(string targetConnectionId, string targetUserId = "", string targetPlayerId = "")
    {
        return AssignClientAuthority(targetConnectionId, targetUserId, targetPlayerId);
    }

    public bool SendOwnerInput(float moveX, float moveZ, float deltaTime = 0.05f, long sequence = 0)
    {
        if (!CanSendOwnerInput()) return false;
        return MetaverseNetworkPlayerMovementBridge.Instance.SendOwnerInput(NetworkIdentity, moveX, moveZ, deltaTime, sequence);
    }

    public virtual void OnStartServer() { }
    public virtual void OnStopServer() { }
    public virtual void OnStartClient() { }
    public virtual void OnStopClient() { }
    public virtual void OnStartLocalPlayer() { }
    public virtual void OnStopLocalPlayer() { }
    public virtual void OnStartAuthority() { }
    public virtual void OnStopAuthority() { }
    public virtual void OnNetworkSpawn() { }
    public virtual void OnNetworkDespawn() { }
    public virtual void OnCommand(string commandName, string payloadJson, DedicatedPlayerSession senderSession) { }
    public virtual void OnClientRpc(string rpcName, string payloadJson) { }
    public virtual void OnTargetRpc(string rpcName, string payloadJson) { }
    public virtual void OnSyncVarChanged(string syncKey, string oldValueJson, string newValueJson, long version) { }
    public virtual void OnNetworkTransform(MetaverseNetworkTransformPayload payload) { }
    public virtual void OnOwnershipChanged(string previousOwnerUserId, string newOwnerUserId, string previousOwnerPlayerId, string newOwnerPlayerId, bool isLocalOwner) { }

    protected string SafeJson(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? EmptyJson : value.Trim();
    }

    protected string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
