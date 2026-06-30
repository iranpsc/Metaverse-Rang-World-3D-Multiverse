using UnityEngine;

public class MetaverseNetworkBehaviourLifecycleProbe : MetaverseNetworkBehaviour
{
    [Header("Debug")]
    [SerializeField] private bool logCallbacks = true;
    [SerializeField] private string probeLabel = "phase19_probe";

    public override void OnStartServer()
    {
        Log("OnStartServer");
    }

    public override void OnStopServer()
    {
        Log("OnStopServer");
    }

    public override void OnStartClient()
    {
        Log("OnStartClient");
    }

    public override void OnStopClient()
    {
        Log("OnStopClient");
    }

    public override void OnStartAuthority()
    {
        Log("OnStartAuthority");
    }

    public override void OnStopAuthority()
    {
        Log("OnStopAuthority");
    }

    public override void OnStartLocalPlayer()
    {
        Log("OnStartLocalPlayer");
    }

    public override void OnStopLocalPlayer()
    {
        Log("OnStopLocalPlayer");
    }

    public override void OnNetworkSpawn()
    {
        Log("OnNetworkSpawn");
    }

    public override void OnNetworkDespawn()
    {
        Log("OnNetworkDespawn");
    }

    private void Log(string callbackName)
    {
        if (!logCallbacks) return;
        Debug.Log("[MetaverseNetworkBehaviourLifecycleProbe] " + callbackName +
                  " | label=" + probeLabel +
                  " | netId=" + netId +
                  " | isServer=" + BoolText(isServer) +
                  " | isClient=" + BoolText(isClient) +
                  " | hasAuthority=" + BoolText(hasAuthority) +
                  " | isLocalPlayer=" + BoolText(isLocalPlayer));
    }

    private string BoolText(bool value)
    {
        return value ? "True" : "False";
    }
}
