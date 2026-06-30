using System.Collections.Generic;
using UnityEngine;

public static class MetaverseNetworkServer
{
    public static bool active => Application.isBatchMode;
    public static bool isActive => active;
    public static int spawnedCount => MetaverseSpawnManager.Instance != null ? MetaverseSpawnManager.Instance.SpawnedCount : 0;

    //* این تابع آبجکت را مثل مسیر NetworkServer.Spawn روی سرور اسپاون می کند.
    public static MetaverseNetworkIdentity Spawn(GameObject obj)
    {
        return Spawn(obj, -1);
    }

    //* این تابع آبجکت را با مالک اختیاری روی سرور اسپاون می کند.
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

    //* این تابع یک پریفب ثبت شده را روی سرور اسپاون می کند.
    public static bool SpawnPrefab(string prefabId, Vector3 position, Quaternion rotation, int ownerConnectionId, out MetaverseNetworkIdentity identity)
    {
        identity = null;
        MetaverseSpawnManager manager = MetaverseSpawnManager.Instance;
        if (manager == null)
        {
            Debug.LogWarning("[MetaverseNetworkServer] SpawnPrefab failed. Spawn manager is missing.");
            return false;
        }

        return manager.TrySpawnPrefab(prefabId, position, rotation, ownerConnectionId, out identity);
    }

    //* این تابع آبجکت شبکه ای را از سرور حذف می کند.
    public static void Despawn(GameObject obj)
    {
        Despawn(obj, "network_server_despawn");
    }

    //* این تابع آبجکت شبکه ای را با دلیل مشخص روی سرور حذف می کند.
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

    //* این تابع برای شباهت با میرور، آبجکت شبکه ای را از مسیر سرور نابود می کند.
    public static void Destroy(GameObject obj)
    {
        Despawn(obj, "network_server_destroy");
    }

    //* این تابع آر پی سی را از سرور برای همه کلاینت های همان روم می فرستد.
    public static bool ClientRpc(MetaverseNetworkIdentity identity, string rpcName, string payloadJson = "")
    {
        if (identity == null || MetaverseNetworkRpcBridge.Instance == null) return false;
        return MetaverseNetworkRpcBridge.Instance.SendClientRpc(identity, rpcName, payloadJson);
    }

    //* این تابع آر پی سی را از سرور فقط برای کانکشن هدف می فرستد.
    public static bool TargetRpc(MetaverseNetworkIdentity identity, string targetConnectionId, string rpcName, string payloadJson = "")
    {
        if (identity == null || MetaverseNetworkRpcBridge.Instance == null) return false;
        return MetaverseNetworkRpcBridge.Instance.SendTargetRpc(identity, targetConnectionId, rpcName, payloadJson);
    }

    //* این تابع آبجکت اسپاون شده را با نت آی دی پیدا می کند.
    public static bool TryGetIdentity(int netId, out MetaverseNetworkIdentity identity)
    {
        identity = null;
        MetaverseSpawnManager manager = MetaverseSpawnManager.Instance;
        if (manager == null) return false;
        return manager.TryGetSpawnedObject(netId, out identity);
    }

    //* این تابع لیست آبجکت های اسپاون شده فعلی را برمی گرداند.
    public static List<MetaverseNetworkIdentity> GetSpawnedObjects()
    {
        MetaverseSpawnManager manager = MetaverseSpawnManager.Instance;
        return manager != null ? manager.GetSpawnedObjects() : new List<MetaverseNetworkIdentity>();
    }
}
