using UnityEngine;

public class MetaverseNetworkBehaviourLifecycleProbe : MetaverseNetworkBehaviour
{
    [Header("Debug")]
    [SerializeField] private bool logCallbacks = true;
    [SerializeField] private string probeLabel = "phase33A_network_behaviour_lifecycle_probe";

    private int startServerCount;
    private int stopServerCount;
    private int startClientCount;
    private int stopClientCount;
    private int startAuthorityCount;
    private int stopAuthorityCount;
    private int startLocalPlayerCount;
    private int stopLocalPlayerCount;
    private int networkSpawnCount;
    private int networkDespawnCount;
    private string lastCallback = string.Empty;

    public int StartServerCount => startServerCount;
    public int StopServerCount => stopServerCount;
    public int StartClientCount => startClientCount;
    public int StopClientCount => stopClientCount;
    public int StartAuthorityCount => startAuthorityCount;
    public int StopAuthorityCount => stopAuthorityCount;
    public int StartLocalPlayerCount => startLocalPlayerCount;
    public int StopLocalPlayerCount => stopLocalPlayerCount;
    public int NetworkSpawnCount => networkSpawnCount;
    public int NetworkDespawnCount => networkDespawnCount;
    public string LastCallback => lastCallback;
    public bool HasSpawnLifecycle => networkSpawnCount > 0;
    public bool HasDespawnLifecycle => networkDespawnCount > 0;

    public override void OnStartServer()
    {
        startServerCount++;
        Log("OnStartServer");
    }

    public override void OnStopServer()
    {
        stopServerCount++;
        Log("OnStopServer");
    }

    public override void OnStartClient()
    {
        startClientCount++;
        Log("OnStartClient");
    }

    public override void OnStopClient()
    {
        stopClientCount++;
        Log("OnStopClient");
    }

    public override void OnStartAuthority()
    {
        startAuthorityCount++;
        Log("OnStartAuthority");
    }

    public override void OnStopAuthority()
    {
        stopAuthorityCount++;
        Log("OnStopAuthority");
    }

    public override void OnStartLocalPlayer()
    {
        startLocalPlayerCount++;
        Log("OnStartLocalPlayer");
    }

    public override void OnStopLocalPlayer()
    {
        stopLocalPlayerCount++;
        Log("OnStopLocalPlayer");
    }

    public override void OnNetworkSpawn()
    {
        networkSpawnCount++;
        Log("OnNetworkSpawn");
    }

    public override void OnNetworkDespawn()
    {
        networkDespawnCount++;
        Log("OnNetworkDespawn");
    }

    public string GetSmokeDebugSummary()
    {
        return "Phase33A NetworkBehaviourLifecycleProbe" +
               " | label=" + Safe(probeLabel) +
               " | netId=" + netId +
               " | spawn=" + networkSpawnCount +
               " | despawn=" + networkDespawnCount +
               " | startServer=" + startServerCount +
               " | stopServer=" + stopServerCount +
               " | startClient=" + startClientCount +
               " | stopClient=" + stopClientCount +
               " | startAuthority=" + startAuthorityCount +
               " | stopAuthority=" + stopAuthorityCount +
               " | startLocalPlayer=" + startLocalPlayerCount +
               " | stopLocalPlayer=" + stopLocalPlayerCount +
               " | last=" + Safe(lastCallback);
    }

    public void ResetSmokeCounters()
    {
        startServerCount = 0;
        stopServerCount = 0;
        startClientCount = 0;
        stopClientCount = 0;
        startAuthorityCount = 0;
        stopAuthorityCount = 0;
        startLocalPlayerCount = 0;
        stopLocalPlayerCount = 0;
        networkSpawnCount = 0;
        networkDespawnCount = 0;
        lastCallback = string.Empty;
    }

    private void Log(string callbackName)
    {
        lastCallback = callbackName;
        if (!logCallbacks) return;
        Debug.Log("[MetaverseNetworkBehaviourLifecycleProbe] " + callbackName +
                  " | phase=33A" +
                  " | mirrorRoute=NetworkBehaviour." + callbackName +
                  " | label=" + Safe(probeLabel) +
                  " | netId=" + netId +
                  " | prefabId=" + Safe(prefabId) +
                  " | ownerUserId=" + Safe(ownerUserId) +
                  " | ownerPlayerId=" + Safe(ownerPlayerId) +
                  " | isServer=" + BoolText(isServer) +
                  " | isClient=" + BoolText(isClient) +
                  " | hasAuthority=" + BoolText(hasAuthority) +
                  " | isLocalPlayer=" + BoolText(isLocalPlayer) +
                  " | isLocalOwner=" + BoolText(isLocalOwner));
    }

    private string BoolText(bool value)
    {
        return value ? "True" : "False";
    }

    private string Safe(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
