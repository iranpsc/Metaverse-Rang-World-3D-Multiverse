using System.Collections.Generic;
using Network_A.GameServer.Players;
using UnityEngine;

public static class MetaverseNetworkServer
{
    private const string EmptyJson = "{}";

    public static bool active => Application.isBatchMode;
    public static bool isActive => active;
    public static bool isServer => active;
    public static bool hasSpawnManager => MetaverseSpawnManager.Instance != null;
    public static bool hasRpcBridge => MetaverseNetworkRpcBridge.Instance != null;
    public static bool hasStateSyncBridge => MetaverseNetworkStateSyncBridge.Instance != null;
    public static bool hasOwnershipBridge => MetaverseNetworkOwnershipBridge.Instance != null;
    public static int spawnedCount => MetaverseSpawnManager.Instance != null ? MetaverseSpawnManager.Instance.SpawnedCount : 0;

    public static MetaverseNetworkIdentity Spawn(GameObject obj)
    {
        return Spawn(obj, -1);
    }

    public static MetaverseNetworkIdentity Spawn(GameObject obj, int ownerConnectionId)
    {
        MetaverseSpawnManager manager = MetaverseSpawnManager.Instance;
        if (manager == null)
        {
            Debug.LogWarning("[MetaverseNetworkServer] Spawn failed. Spawn manager is missing.");
            return null;
        }

        return manager.Spawn(obj, ownerConnectionId);
    }

    public static MetaverseNetworkIdentity Spawn(GameObject obj, string ownerConnectionId, string ownerUserId = "", string ownerPlayerId = "")
    {
        MetaverseNetworkIdentity identity = Spawn(obj, ParseConnectionId(ownerConnectionId));
        ApplyOwnerAfterSpawn(identity, ownerConnectionId, ownerUserId, ownerPlayerId, false, "network_server_spawn_owner");
        return identity;
    }

    public static MetaverseNetworkIdentity Spawn(GameObject obj, DedicatedPlayerSession ownerSession)
    {
        if (ownerSession == null) return Spawn(obj);
        return Spawn(obj, ownerSession.connectionId, ownerSession.userId, ownerSession.playerId);
    }

    public static bool SpawnPrefab(string prefabId, Vector3 position, Quaternion rotation, int ownerConnectionId, out MetaverseNetworkIdentity identity)
    {
        identity = null;
        MetaverseSpawnManager manager = MetaverseSpawnManager.Instance;
        if (manager == null)
        {
            Debug.LogWarning("[MetaverseNetworkServer] SpawnPrefab failed. Spawn manager is missing.");
            return false;
        }

        return manager.TrySpawnPrefab(SafeTrim(prefabId), position, rotation, ownerConnectionId, out identity);
    }

    public static bool SpawnPrefab(string prefabId, Vector3 position, Quaternion rotation, out MetaverseNetworkIdentity identity)
    {
        return SpawnPrefab(prefabId, position, rotation, -1, out identity);
    }

    public static bool SpawnPrefab(string prefabId, Vector3 position, Quaternion rotation, string ownerConnectionId, string ownerUserId, string ownerPlayerId, out MetaverseNetworkIdentity identity)
    {
        bool spawned = SpawnPrefab(prefabId, position, rotation, ParseConnectionId(ownerConnectionId), out identity);
        if (!spawned || identity == null) return false;
        ApplyOwnerAfterSpawn(identity, ownerConnectionId, ownerUserId, ownerPlayerId, false, "network_server_spawn_prefab_owner");
        return true;
    }

    public static bool SpawnPrefab(string prefabId, Vector3 position, Quaternion rotation, DedicatedPlayerSession ownerSession, out MetaverseNetworkIdentity identity)
    {
        if (ownerSession == null) return SpawnPrefab(prefabId, position, rotation, out identity);
        return SpawnPrefab(prefabId, position, rotation, ownerSession.connectionId, ownerSession.userId, ownerSession.playerId, out identity);
    }

    public static void Despawn(GameObject obj)
    {
        Despawn(obj, "network_server_despawn");
    }

    public static void Despawn(GameObject obj, string reason)
    {
        MetaverseSpawnManager manager = MetaverseSpawnManager.Instance;
        if (manager == null)
        {
            Debug.LogWarning("[MetaverseNetworkServer] Despawn failed. Spawn manager is missing.");
            return;
        }

        manager.Despawn(obj, string.IsNullOrWhiteSpace(reason) ? "network_server_despawn" : reason.Trim());
    }

    public static void Despawn(MetaverseNetworkIdentity identity)
    {
        Despawn(identity, "network_server_despawn");
    }

    public static void Despawn(MetaverseNetworkIdentity identity, string reason)
    {
        if (identity == null) return;
        Despawn(identity.gameObject, reason);
    }

    public static bool Despawn(int netId, string reason = "network_server_despawn")
    {
        MetaverseSpawnManager manager = MetaverseSpawnManager.Instance;
        if (manager == null) return false;
        return manager.TryDespawnNetId(netId, string.IsNullOrWhiteSpace(reason) ? "network_server_despawn" : reason.Trim());
    }

    public static void Destroy(GameObject obj)
    {
        Despawn(obj, "network_server_destroy");
    }

    public static void Destroy(MetaverseNetworkIdentity identity)
    {
        if (identity == null) return;
        Destroy(identity.gameObject);
    }

    public static bool Destroy(int netId)
    {
        return Despawn(netId, "network_server_destroy");
    }

    public static bool ClientRpc(MetaverseNetworkIdentity identity, string rpcName, string payloadJson = "")
    {
        if (!CanSendRpc(identity, rpcName)) return false;
        return MetaverseNetworkRpcBridge.Instance.SendClientRpc(identity, SafeTrim(rpcName), SafeJson(payloadJson));
    }

    public static bool ClientRpc(GameObject obj, string rpcName, string payloadJson = "")
    {
        return ClientRpc(GetIdentity(obj), rpcName, payloadJson);
    }

    public static bool ClientRpc(int netId, string rpcName, string payloadJson = "")
    {
        if (!TryGetIdentity(netId, out MetaverseNetworkIdentity identity)) return false;
        return ClientRpc(identity, rpcName, payloadJson);
    }

    public static bool Rpc(MetaverseNetworkIdentity identity, string rpcName, string payloadJson = "")
    {
        return ClientRpc(identity, rpcName, payloadJson);
    }

    public static bool TargetRpc(MetaverseNetworkIdentity identity, string targetConnectionId, string rpcName, string payloadJson = "")
    {
        if (!CanSendRpc(identity, rpcName) || string.IsNullOrWhiteSpace(targetConnectionId)) return false;
        return MetaverseNetworkRpcBridge.Instance.SendTargetRpc(identity, SafeTrim(targetConnectionId), SafeTrim(rpcName), SafeJson(payloadJson));
    }

    public static bool TargetRpc(MetaverseNetworkIdentity identity, DedicatedPlayerSession targetSession, string rpcName, string payloadJson = "")
    {
        if (targetSession == null) return false;
        return TargetRpc(identity, targetSession.connectionId, rpcName, payloadJson);
    }

    public static bool TargetRpc(GameObject obj, string targetConnectionId, string rpcName, string payloadJson = "")
    {
        return TargetRpc(GetIdentity(obj), targetConnectionId, rpcName, payloadJson);
    }

    public static bool TargetRpc(GameObject obj, DedicatedPlayerSession targetSession, string rpcName, string payloadJson = "")
    {
        return TargetRpc(GetIdentity(obj), targetSession, rpcName, payloadJson);
    }

    public static bool TargetRpc(int netId, string targetConnectionId, string rpcName, string payloadJson = "")
    {
        if (!TryGetIdentity(netId, out MetaverseNetworkIdentity identity)) return false;
        return TargetRpc(identity, targetConnectionId, rpcName, payloadJson);
    }

    public static bool TargetRpc(int netId, DedicatedPlayerSession targetSession, string rpcName, string payloadJson = "")
    {
        if (targetSession == null) return false;
        return TargetRpc(netId, targetSession.connectionId, rpcName, payloadJson);
    }

    public static bool SetOwner(MetaverseNetworkIdentity identity, string ownerConnectionId, string ownerUserId = "", string ownerPlayerId = "")
    {
        return SetOwner(identity, ownerConnectionId, ownerUserId, ownerPlayerId, false, "network_server_set_owner");
    }

    public static bool SetOwner(MetaverseNetworkIdentity identity, DedicatedPlayerSession ownerSession, bool serverOwned = false, string reason = "network_server_set_owner")
    {
        if (identity == null || ownerSession == null) return false;
        return SetOwner(identity, ownerSession.connectionId, ownerSession.userId, ownerSession.playerId, serverOwned, reason);
    }

    public static bool SetOwner(GameObject obj, DedicatedPlayerSession ownerSession, bool serverOwned = false, string reason = "network_server_set_owner")
    {
        return SetOwner(GetIdentity(obj), ownerSession, serverOwned, reason);
    }

    public static bool SetOwner(MetaverseNetworkIdentity identity, string ownerConnectionId, string ownerUserId, string ownerPlayerId, bool serverOwned, string reason)
    {
        if (identity == null) return false;
        if (MetaverseNetworkOwnershipBridge.Instance != null)
        {
            return MetaverseNetworkOwnershipBridge.Instance.SetOwner(identity, SafeTrim(ownerConnectionId), SafeTrim(ownerUserId), SafeTrim(ownerPlayerId), serverOwned, SafeReason(reason, "network_server_set_owner"));
        }

        identity.SetOwnerInfo(SafeTrim(ownerConnectionId), SafeTrim(ownerUserId), SafeTrim(ownerPlayerId), serverOwned);
        return true;
    }

    public static bool AssignClientAuthority(MetaverseNetworkIdentity identity, string ownerConnectionId, string ownerUserId = "", string ownerPlayerId = "")
    {
        return SetOwner(identity, ownerConnectionId, ownerUserId, ownerPlayerId, false, "network_server_assign_authority");
    }

    public static bool AssignClientAuthority(MetaverseNetworkIdentity identity, DedicatedPlayerSession ownerSession)
    {
        return SetOwner(identity, ownerSession, false, "network_server_assign_authority");
    }

    public static bool RemoveClientAuthority(MetaverseNetworkIdentity identity)
    {
        return SetOwner(identity, string.Empty, string.Empty, string.Empty, true, "network_server_remove_authority");
    }

    public static bool RemoveClientAuthority(GameObject obj)
    {
        return RemoveClientAuthority(GetIdentity(obj));
    }

    public static bool SetSyncVar(MetaverseNetworkIdentity identity, string syncKey, string valueJson = "")
    {
        if (!CanSetSync(identity, syncKey)) return false;
        return MetaverseNetworkStateSyncBridge.Instance.SetSyncVar(identity, SafeTrim(syncKey), SafeJson(valueJson));
    }

    public static bool SetSyncVar(GameObject obj, string syncKey, string valueJson = "")
    {
        return SetSyncVar(GetIdentity(obj), syncKey, valueJson);
    }

    public static bool SetSyncVar(int netId, string syncKey, string valueJson = "")
    {
        if (!TryGetIdentity(netId, out MetaverseNetworkIdentity identity)) return false;
        return SetSyncVar(identity, syncKey, valueJson);
    }

    public static bool SyncTransform(MetaverseNetworkIdentity identity)
    {
        if (identity == null || MetaverseNetworkStateSyncBridge.Instance == null) return false;
        return MetaverseNetworkStateSyncBridge.Instance.SendNetworkTransform(identity);
    }

    public static bool SyncTransform(GameObject obj)
    {
        return SyncTransform(GetIdentity(obj));
    }

    public static bool SyncTransform(int netId)
    {
        if (!TryGetIdentity(netId, out MetaverseNetworkIdentity identity)) return false;
        return SyncTransform(identity);
    }

    public static bool TryGetIdentity(int netId, out MetaverseNetworkIdentity identity)
    {
        identity = null;
        MetaverseSpawnManager manager = MetaverseSpawnManager.Instance;
        if (manager == null) return false;
        return manager.TryGetSpawnedObject(netId, out identity);
    }

    public static MetaverseNetworkIdentity GetIdentity(int netId)
    {
        TryGetIdentity(netId, out MetaverseNetworkIdentity identity);
        return identity;
    }

    public static MetaverseNetworkIdentity GetIdentity(GameObject obj)
    {
        return obj != null ? obj.GetComponent<MetaverseNetworkIdentity>() : null;
    }

    public static bool TryGetGameObject(int netId, out GameObject obj)
    {
        obj = null;
        if (!TryGetIdentity(netId, out MetaverseNetworkIdentity identity) || identity == null) return false;
        obj = identity.gameObject;
        return obj != null;
    }

    public static List<MetaverseNetworkIdentity> GetSpawnedObjects()
    {
        MetaverseSpawnManager manager = MetaverseSpawnManager.Instance;
        return manager != null ? manager.GetSpawnedObjects() : new List<MetaverseNetworkIdentity>();
    }

    public static bool IsSpawned(MetaverseNetworkIdentity identity)
    {
        return identity != null && identity.IsSpawned && identity.NetId > 0;
    }

    public static bool IsOwner(MetaverseNetworkIdentity identity, DedicatedPlayerSession session)
    {
        if (identity == null || session == null) return false;
        if (!string.IsNullOrWhiteSpace(identity.OwnerConnectionIdText) && string.Equals(identity.OwnerConnectionIdText, session.connectionId, System.StringComparison.Ordinal)) return true;
        if (!string.IsNullOrWhiteSpace(identity.OwnerUserId) && string.Equals(identity.OwnerUserId, session.userId, System.StringComparison.Ordinal)) return true;
        if (!string.IsNullOrWhiteSpace(identity.OwnerPlayerId) && string.Equals(identity.OwnerPlayerId, session.playerId, System.StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool CanSendRpc(MetaverseNetworkIdentity identity, string rpcName)
    {
        return identity != null && identity.NetId > 0 && MetaverseNetworkRpcBridge.Instance != null && !string.IsNullOrWhiteSpace(rpcName);
    }

    private static bool CanSetSync(MetaverseNetworkIdentity identity, string syncKey)
    {
        return identity != null && identity.NetId > 0 && MetaverseNetworkStateSyncBridge.Instance != null && !string.IsNullOrWhiteSpace(syncKey);
    }

    private static void ApplyOwnerAfterSpawn(MetaverseNetworkIdentity identity, string ownerConnectionId, string ownerUserId, string ownerPlayerId, bool serverOwned, string reason)
    {
        if (identity == null) return;
        if (string.IsNullOrWhiteSpace(ownerConnectionId) && string.IsNullOrWhiteSpace(ownerUserId) && string.IsNullOrWhiteSpace(ownerPlayerId)) return;
        SetOwner(identity, ownerConnectionId, ownerUserId, ownerPlayerId, serverOwned, reason);
    }

    private static int ParseConnectionId(string ownerConnectionId)
    {
        if (string.IsNullOrWhiteSpace(ownerConnectionId)) return -1;
        return int.TryParse(ownerConnectionId.Trim(), out int parsed) ? parsed : -1;
    }

    private static string SafeJson(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? EmptyJson : value.Trim();
    }

    private static string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string SafeReason(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
