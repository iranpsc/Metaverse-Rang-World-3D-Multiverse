using UnityEngine;

public class MetaverseNetworkPlayerObjectSmokeProbe : MetaverseNetworkBehaviour
{
    [SerializeField] private string label = "phase33A_player_object_probe";

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
    private int ownershipChangedCount;
    private string lastCallback = string.Empty;

    public int StartAuthorityCount => startAuthorityCount;
    public int OwnershipChangedCount => ownershipChangedCount;
    public int NetworkSpawnCount => networkSpawnCount;
    public int NetworkDespawnCount => networkDespawnCount;
    public string LastCallback => lastCallback;
    public bool IsPlayerObjectReady => networkSpawnCount > 0 && !string.IsNullOrWhiteSpace(ownerPlayerId);

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

    public override void OnOwnershipChanged(string previousOwnerUserId, string newOwnerUserId, string previousOwnerPlayerId, string newOwnerPlayerId, bool isLocalOwner)
    {
        ownershipChangedCount++;
        Debug.Log(BuildLog("OnOwnershipChanged") +
                  " | mirrorRoute=NetworkBehaviour.OnOwnershipChanged" +
                  " | ownershipChangedCount=" + ownershipChangedCount +
                  " | previousOwnerUserId=" + Safe(previousOwnerUserId) +
                  " | newOwnerUserId=" + Safe(newOwnerUserId) +
                  " | previousOwnerPlayerId=" + Safe(previousOwnerPlayerId) +
                  " | newOwnerPlayerId=" + Safe(newOwnerPlayerId) +
                  " | isLocalOwner=" + isLocalOwner);
    }

    public string GetSmokeDebugSummary()
    {
        return "Phase33A PlayerObjectSmokeProbe" +
               " | label=" + Safe(label) +
               " | netId=" + netId +
               " | spawned=" + networkSpawnCount +
               " | despawned=" + networkDespawnCount +
               " | authority=" + startAuthorityCount + "/" + stopAuthorityCount +
               " | ownershipChanged=" + ownershipChangedCount +
               " | ownerUserId=" + Safe(ownerUserId) +
               " | ownerPlayerId=" + Safe(ownerPlayerId) +
               " | isLocalOwner=" + isLocalOwner +
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
        ownershipChangedCount = 0;
        lastCallback = string.Empty;
    }

    private void Log(string callback)
    {
        lastCallback = callback;
        Debug.Log(BuildLog(callback) + " | mirrorRoute=NetworkBehaviour." + callback);
    }

    private string BuildLog(string callback)
    {
        MetaverseNetworkIdentity identity = NetworkIdentity;
        return "[MetaverseNetworkPlayerObjectSmokeProbe] " + callback +
               " | phase=33A" +
               " | label=" + Safe(label) +
               " | netId=" + netId +
               " | ownerConnectionId=" + (identity != null ? Safe(identity.OwnerConnectionIdText) : string.Empty) +
               " | ownerUserId=" + (identity != null ? Safe(identity.OwnerUserId) : string.Empty) +
               " | ownerPlayerId=" + (identity != null ? Safe(identity.OwnerPlayerId) : string.Empty) +
               " | isServer=" + isServer +
               " | isClient=" + isClient +
               " | hasAuthority=" + hasAuthority +
               " | isLocalPlayer=" + isLocalPlayer +
               " | isLocalOwner=" + isLocalOwner;
    }

    private string Safe(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
