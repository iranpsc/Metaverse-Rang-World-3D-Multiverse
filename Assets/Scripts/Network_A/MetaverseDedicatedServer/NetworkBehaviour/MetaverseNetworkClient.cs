using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Network_A.DedicatedGameServer.Client;
using UnityEngine;

public static class MetaverseNetworkClient
{
    private const string EmptyJson = "{}";

    public static bool active => DedicatedGameServerWsClient.Instance != null && DedicatedGameServerWsClient.Instance.IsConnected;
    public static bool isConnected => DedicatedGameServerWsClient.Instance != null && DedicatedGameServerWsClient.Instance.IsConnected;
    public static bool isAuthenticated => DedicatedGameServerWsClient.Instance != null && DedicatedGameServerWsClient.Instance.IsAuthenticated;
    public static bool isReady => isConnected && isAuthenticated;
    public static bool hasClient => DedicatedGameServerWsClient.Instance != null;
    public static bool hasSpawnManager => MetaverseSpawnManager.Instance != null;
    public static bool hasRpcBridge => MetaverseNetworkRpcBridge.Instance != null;
    public static bool hasMovementBridge => MetaverseNetworkPlayerMovementBridge.Instance != null;
    public static string connectionId => DedicatedGameServerWsClient.Instance != null ? DedicatedGameServerWsClient.Instance.ConnectionId : string.Empty;
    public static string userId => DedicatedGameServerWsClient.Instance != null ? DedicatedGameServerWsClient.Instance.UserId : string.Empty;
    public static string playerId => DedicatedGameServerWsClient.Instance != null ? DedicatedGameServerWsClient.Instance.PlayerId : string.Empty;
    public static string roomId => DedicatedGameServerWsClient.Instance != null ? DedicatedGameServerWsClient.Instance.RoomId : string.Empty;
    public static string serverId => DedicatedGameServerWsClient.Instance != null ? DedicatedGameServerWsClient.Instance.ServerId : string.Empty;
    public static string sessionId => DedicatedGameServerWsClient.Instance != null ? DedicatedGameServerWsClient.Instance.SessionId : string.Empty;
    public static string lastError => DedicatedGameServerWsClient.Instance != null ? DedicatedGameServerWsClient.Instance.LastError : string.Empty;
    public static string lastAuthReason => DedicatedGameServerWsClient.Instance != null ? DedicatedGameServerWsClient.Instance.LastAuthReason : string.Empty;
    public static string lastRawMessage => DedicatedGameServerWsClient.Instance != null ? DedicatedGameServerWsClient.Instance.LastRawMessage : string.Empty;
    public static int spawnedCount => MetaverseSpawnManager.Instance != null ? MetaverseSpawnManager.Instance.SpawnedCount : 0;

    public static DedicatedGameServerWsClient GetDedicatedClient()
    {
        return DedicatedGameServerWsClient.Instance;
    }

    public static bool TryGetDedicatedClient(out DedicatedGameServerWsClient client)
    {
        client = DedicatedGameServerWsClient.Instance;
        return client != null;
    }

    public static async Task<bool> ConnectAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!TryGetDedicatedClient(out DedicatedGameServerWsClient client)) return false;
        return await client.ConnectAsync(SafeTrim(url), cancellationToken);
    }

    public static async Task<bool> ConnectToDedicatedServerAsync(string host, int port, bool secure, CancellationToken cancellationToken = default)
    {
        if (!TryGetDedicatedClient(out DedicatedGameServerWsClient client)) return false;
        return await client.ConnectToDedicatedServerAsync(SafeTrim(host), Mathf.Max(1, port), secure, cancellationToken);
    }

    public static async Task<bool> AuthenticateWithTicketAsync(
        string ticketId,
        string signature,
        string userIdValue,
        string roomIdValue,
        string serverIdValue,
        string sessionIdValue,
        string playerIdValue = "",
        string userNameValue = "",
        CancellationToken cancellationToken = default)
    {
        if (!TryGetDedicatedClient(out DedicatedGameServerWsClient client)) return false;
        return await client.AuthenticateWithTicketAsync(
            SafeTrim(ticketId),
            SafeTrim(signature),
            SafeTrim(userIdValue),
            SafeTrim(roomIdValue),
            SafeTrim(serverIdValue),
            SafeTrim(sessionIdValue),
            SafeTrim(playerIdValue),
            SafeTrim(userNameValue),
            cancellationToken);
    }

    public static void Disconnect(string reason = "network_client_disconnect")
    {
        if (!TryGetDedicatedClient(out DedicatedGameServerWsClient client)) return;
        client.Disconnect(SafeReason(reason, "network_client_disconnect"));
    }

    public static async Task<bool> SendRawAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!TryGetDedicatedClient(out DedicatedGameServerWsClient client)) return false;
        return await client.SendRawAsync(text ?? string.Empty, cancellationToken);
    }

    public static async Task<bool> SendPlayerStateAsync(Vector3 position, Quaternion rotation, Vector3 velocity, long sequence, CancellationToken cancellationToken = default)
    {
        if (!TryGetDedicatedClient(out DedicatedGameServerWsClient client)) return false;
        return await client.SendPlayerStateAsync(position, rotation, velocity, sequence, cancellationToken);
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

    public static List<MetaverseNetworkIdentity> GetLocalOwnedObjects()
    {
        List<MetaverseNetworkIdentity> result = new List<MetaverseNetworkIdentity>();
        List<MetaverseNetworkIdentity> spawnedObjects = GetSpawnedObjects();
        for (int i = 0; i < spawnedObjects.Count; i++)
        {
            MetaverseNetworkIdentity identity = spawnedObjects[i];
            if (IsLocalOwner(identity)) result.Add(identity);
        }
        return result;
    }

    public static bool TryGetLocalPlayer(out MetaverseNetworkIdentity identity)
    {
        identity = null;
        List<MetaverseNetworkIdentity> spawnedObjects = GetSpawnedObjects();
        for (int i = 0; i < spawnedObjects.Count; i++)
        {
            MetaverseNetworkIdentity current = spawnedObjects[i];
            if (current == null) continue;
            if (!current.IsLocalPlayer && !IsLocalOwner(current)) continue;
            identity = current;
            return true;
        }
        return false;
    }

    public static bool CanQueueCommand(MetaverseNetworkIdentity identity, string commandName)
    {
        return identity != null && identity.NetId > 0 && MetaverseNetworkRpcBridge.Instance != null && !string.IsNullOrWhiteSpace(commandName);
    }

    public static bool CanSendCommand(MetaverseNetworkIdentity identity, string commandName)
    {
        return isReady && CanQueueCommand(identity, commandName);
    }

    public static bool CanSendOwnerCommand(MetaverseNetworkIdentity identity, string commandName)
    {
        return CanSendCommand(identity, commandName) && IsLocalOwner(identity);
    }

    public static bool CanSendOwnerInput(MetaverseNetworkIdentity identity)
    {
        return isReady && identity != null && identity.NetId > 0 && MetaverseNetworkPlayerMovementBridge.Instance != null && IsLocalOwner(identity);
    }

    public static bool SendCommand(MetaverseNetworkIdentity identity, string commandName, string payloadJson = "")
    {
        if (!CanQueueCommand(identity, commandName)) return false;
        return MetaverseNetworkRpcBridge.Instance.SendCommand(identity, SafeTrim(commandName), SafeJson(payloadJson));
    }

    public static bool SendCommand(GameObject obj, string commandName, string payloadJson = "")
    {
        return SendCommand(GetIdentity(obj), commandName, payloadJson);
    }

    public static bool SendCommand(int netId, string commandName, string payloadJson = "")
    {
        if (!TryGetIdentity(netId, out MetaverseNetworkIdentity identity))
        {
            if (netId <= 0 || MetaverseNetworkRpcBridge.Instance == null || string.IsNullOrWhiteSpace(commandName)) return false;
            return MetaverseNetworkRpcBridge.Instance.SendCommand(netId, string.Empty, SafeTrim(commandName), SafeJson(payloadJson));
        }

        return SendCommand(identity, commandName, payloadJson);
    }

    public static bool SendOwnerCommand(MetaverseNetworkIdentity identity, string commandName, string payloadJson = "")
    {
        if (!CanSendOwnerCommand(identity, commandName)) return false;
        return MetaverseNetworkRpcBridge.Instance.SendCommand(identity, SafeTrim(commandName), SafeJson(payloadJson));
    }

    public static bool SendOwnerCommand(GameObject obj, string commandName, string payloadJson = "")
    {
        return SendOwnerCommand(GetIdentity(obj), commandName, payloadJson);
    }

    public static bool SendOwnerCommand(int netId, string commandName, string payloadJson = "")
    {
        if (!TryGetIdentity(netId, out MetaverseNetworkIdentity identity)) return false;
        return SendOwnerCommand(identity, commandName, payloadJson);
    }

    public static bool Cmd(MetaverseNetworkIdentity identity, string commandName, string payloadJson = "")
    {
        return SendCommand(identity, commandName, payloadJson);
    }

    public static bool OwnerCmd(MetaverseNetworkIdentity identity, string commandName, string payloadJson = "")
    {
        return SendOwnerCommand(identity, commandName, payloadJson);
    }

    public static bool SendOwnerInput(MetaverseNetworkIdentity identity, float moveX, float moveZ, float deltaTime = 0.05f, long sequence = 0)
    {
        if (!CanSendOwnerInput(identity)) return false;
        return MetaverseNetworkPlayerMovementBridge.Instance.SendOwnerInput(identity, moveX, moveZ, deltaTime, sequence);
    }

    public static bool SendOwnerInput(GameObject obj, float moveX, float moveZ, float deltaTime = 0.05f, long sequence = 0)
    {
        return SendOwnerInput(GetIdentity(obj), moveX, moveZ, deltaTime, sequence);
    }

    public static bool SendOwnerInput(int netId, float moveX, float moveZ, float deltaTime = 0.05f, long sequence = 0)
    {
        if (!TryGetIdentity(netId, out MetaverseNetworkIdentity identity)) return false;
        return SendOwnerInput(identity, moveX, moveZ, deltaTime, sequence);
    }

    public static bool IsLocalOwner(MetaverseNetworkIdentity identity)
    {
        if (identity == null) return false;
        if (identity.IsLocalPlayer || identity.HasAuthority) return true;
        if (!string.IsNullOrWhiteSpace(identity.OwnerConnectionIdText) && !string.IsNullOrWhiteSpace(connectionId) && string.Equals(identity.OwnerConnectionIdText, connectionId, System.StringComparison.Ordinal)) return true;
        if (!string.IsNullOrWhiteSpace(identity.OwnerUserId) && !string.IsNullOrWhiteSpace(userId) && string.Equals(identity.OwnerUserId, userId, System.StringComparison.Ordinal)) return true;
        if (!string.IsNullOrWhiteSpace(identity.OwnerPlayerId) && !string.IsNullOrWhiteSpace(playerId) && string.Equals(identity.OwnerPlayerId, playerId, System.StringComparison.Ordinal)) return true;
        return false;
    }

    public static bool IsLocalOwner(GameObject obj)
    {
        return IsLocalOwner(GetIdentity(obj));
    }

    public static bool IsLocalOwner(int netId)
    {
        if (!TryGetIdentity(netId, out MetaverseNetworkIdentity identity)) return false;
        return IsLocalOwner(identity);
    }

    public static bool IsInRoom(string targetRoomId)
    {
        if (string.IsNullOrWhiteSpace(targetRoomId)) return false;
        return string.Equals(roomId, targetRoomId.Trim(), System.StringComparison.Ordinal);
    }

    public static bool IsSameUser(string targetUserId)
    {
        return !string.IsNullOrWhiteSpace(targetUserId) && !string.IsNullOrWhiteSpace(userId) && string.Equals(targetUserId.Trim(), userId, System.StringComparison.Ordinal);
    }

    public static bool IsSamePlayer(string targetPlayerId)
    {
        return !string.IsNullOrWhiteSpace(targetPlayerId) && !string.IsNullOrWhiteSpace(playerId) && string.Equals(targetPlayerId.Trim(), playerId, System.StringComparison.Ordinal);
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
