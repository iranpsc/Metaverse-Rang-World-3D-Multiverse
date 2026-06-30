using Network_A.GameServer.Players;
using UnityEngine;

public abstract class MetaverseNetworkBehaviour : MonoBehaviour
{
    private MetaverseNetworkIdentity cachedIdentity;

    public MetaverseNetworkIdentity NetworkIdentity
    {
        get
        {
            if (cachedIdentity == null) cachedIdentity = GetComponent<MetaverseNetworkIdentity>();
            return cachedIdentity;
        }
    }

    public int netId => NetworkIdentity != null ? NetworkIdentity.NetId : 0;
    public bool isServer => NetworkIdentity != null && NetworkIdentity.IsServer;
    public bool isClient => NetworkIdentity != null && NetworkIdentity.IsClient;
    public bool isLocalPlayer => NetworkIdentity != null && NetworkIdentity.IsLocalPlayer;
    public bool hasAuthority => NetworkIdentity != null && NetworkIdentity.HasAuthority;
    public bool isOwned => hasAuthority;

    protected virtual void Awake()
    {
        cachedIdentity = GetComponent<MetaverseNetworkIdentity>();
    }

    public bool SendCommand(string commandName, string payloadJson = "")
    {
        if (NetworkIdentity == null || MetaverseNetworkRpcBridge.Instance == null) return false;
        return MetaverseNetworkRpcBridge.Instance.SendCommand(NetworkIdentity, commandName, payloadJson);
    }

    public bool SendClientRpc(string rpcName, string payloadJson = "")
    {
        if (NetworkIdentity == null || MetaverseNetworkRpcBridge.Instance == null) return false;
        return MetaverseNetworkRpcBridge.Instance.SendClientRpc(NetworkIdentity, rpcName, payloadJson);
    }

    public bool SendTargetRpc(string targetConnectionId, string rpcName, string payloadJson = "")
    {
        if (NetworkIdentity == null || MetaverseNetworkRpcBridge.Instance == null) return false;
        return MetaverseNetworkRpcBridge.Instance.SendTargetRpc(NetworkIdentity, targetConnectionId, rpcName, payloadJson);
    }

    //* این تابع وقتی آبجکت روی سرور اسپاون شد صدا زده می شود.
    public virtual void OnStartServer() { }

    //* این تابع وقتی آبجکت روی سرور از شبکه خارج شد صدا زده می شود.
    public virtual void OnStopServer() { }

    //* این تابع وقتی آبجکت روی کلاینت اسپاون شد صدا زده می شود.
    public virtual void OnStartClient() { }

    //* این تابع وقتی آبجکت روی کلاینت از شبکه خارج شد صدا زده می شود.
    public virtual void OnStopClient() { }

    //* این تابع وقتی این آبجکت پلیر لوکال همان کلاینت باشد صدا زده می شود.
    public virtual void OnStartLocalPlayer() { }

    //* این تابع وقتی حالت پلیر لوکال از این آبجکت گرفته شود صدا زده می شود.
    public virtual void OnStopLocalPlayer() { }

    //* این تابع وقتی این آبجکت اختیار اجرا روی این سمت داشته باشد صدا زده می شود.
    public virtual void OnStartAuthority() { }

    //* این تابع وقتی اختیار اجرا از این سمت گرفته شود صدا زده می شود.
    public virtual void OnStopAuthority() { }

    //* این تابع برای شروع مشترک اسپاون شبکه ای صدا زده می شود.
    public virtual void OnNetworkSpawn() { }

    //* این تابع برای پایان مشترک اسپاون شبکه ای صدا زده می شود.
    public virtual void OnNetworkDespawn() { }

    //* این تابع روی سرور وقتی کلاینت برای این آبجکت کامند می فرستد صدا زده می شود.
    public virtual void OnCommand(string commandName, string payloadJson, DedicatedPlayerSession senderSession) { }

    //* این تابع روی کلاینت وقتی سرور برای همه کلاینت ها آر پی سی می فرستد صدا زده می شود.
    public virtual void OnClientRpc(string rpcName, string payloadJson) { }

    //* این تابع روی کلاینت هدف وقتی سرور برای همان کلاینت آر پی سی می فرستد صدا زده می شود.
    public virtual void OnTargetRpc(string rpcName, string payloadJson) { }
}
