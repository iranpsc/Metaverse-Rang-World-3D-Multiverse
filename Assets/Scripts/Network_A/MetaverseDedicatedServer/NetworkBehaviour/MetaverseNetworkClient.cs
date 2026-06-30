using Network_A.DedicatedGameServer.Client;
using UnityEngine;

public static class MetaverseNetworkClient
{
    public static bool active => DedicatedGameServerWsClient.Instance != null && DedicatedGameServerWsClient.Instance.IsConnected;
    public static bool isConnected => DedicatedGameServerWsClient.Instance != null && DedicatedGameServerWsClient.Instance.IsConnected;
    public static bool isAuthenticated => DedicatedGameServerWsClient.Instance != null && DedicatedGameServerWsClient.Instance.IsAuthenticated;
    public static string connectionId => DedicatedGameServerWsClient.Instance != null ? DedicatedGameServerWsClient.Instance.ConnectionId : string.Empty;
    public static string userId => DedicatedGameServerWsClient.Instance != null ? DedicatedGameServerWsClient.Instance.UserId : string.Empty;
    public static string playerId => DedicatedGameServerWsClient.Instance != null ? DedicatedGameServerWsClient.Instance.PlayerId : string.Empty;
    public static string roomId => DedicatedGameServerWsClient.Instance != null ? DedicatedGameServerWsClient.Instance.RoomId : string.Empty;
    public static int spawnedCount => MetaverseSpawnManager.Instance != null ? MetaverseSpawnManager.Instance.SpawnedCount : 0;

    //* این تابع آبجکت شبکه ای کلاینت را با نت آی دی پیدا می کند.
    public static bool TryGetIdentity(int netId, out MetaverseNetworkIdentity identity)
    {
        identity = null;
        MetaverseSpawnManager manager = MetaverseSpawnManager.Instance;
        if (manager == null) return false;
        return manager.TryGetSpawnedObject(netId, out identity);
    }

    //* این تابع یک کامند را از کلاینت به سرور می فرستد.
    public static bool SendCommand(MetaverseNetworkIdentity identity, string commandName, string payloadJson = "")
    {
        if (identity == null || MetaverseNetworkRpcBridge.Instance == null) return false;
        return MetaverseNetworkRpcBridge.Instance.SendCommand(identity, commandName, payloadJson);
    }

    //* این تابع بررسی می کند کلاینت به همان روم مورد نظر وصل است یا نه.
    public static bool IsInRoom(string targetRoomId)
    {
        if (string.IsNullOrWhiteSpace(targetRoomId)) return false;
        return string.Equals(roomId, targetRoomId.Trim(), System.StringComparison.Ordinal);
    }
}
