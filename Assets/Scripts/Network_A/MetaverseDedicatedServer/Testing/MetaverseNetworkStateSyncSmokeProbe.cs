using UnityEngine;

public class MetaverseNetworkStateSyncSmokeProbe : MetaverseNetworkBehaviour
{
    [Header("Debug")]
    [SerializeField] private bool logMessages = true;
    [SerializeField] private string probeLabel = "phase33A_state_sync_probe";
    [SerializeField] private bool acceptLegacyPhase2122SyncKeys = true;

    private int syncVarCount;
    private int phase33ASyncVarCount;
    private int legacySyncVarCount;
    private int transformCount;
    private long lastSyncVarVersion;
    private long lastTransformSequence;
    private string lastSyncKey = string.Empty;
    private string lastSyncValueJson = string.Empty;
    private Vector3 lastNetworkPosition;
    private Quaternion lastNetworkRotation;

    public int SyncVarCount => syncVarCount;
    public int Phase33ASyncVarCount => phase33ASyncVarCount;
    public int LegacySyncVarCount => legacySyncVarCount;
    public int TransformCount => transformCount;
    public long LastSyncVarVersion => lastSyncVarVersion;
    public long LastTransformSequence => lastTransformSequence;
    public string LastSyncKey => lastSyncKey;
    public string LastSyncValueJson => lastSyncValueJson;
    public Vector3 LastNetworkPosition => lastNetworkPosition;
    public Quaternion LastNetworkRotation => lastNetworkRotation;

    public override void OnNetworkSpawn()
    {
        Log("OnNetworkSpawn | mirrorRoute=NetworkServer.SpawnPrefab | summary=" + GetSmokeDebugSummary());
    }

    public override void OnSyncVarChanged(string syncKey, string oldValueJson, string newValueJson, long version)
    {
        syncVarCount++;
        lastSyncKey = SafeText(syncKey);
        lastSyncValueJson = SafeText(newValueJson);
        lastSyncVarVersion = version;

        if (IsPhase33ASyncKey(syncKey)) phase33ASyncVarCount++;
        if (IsLegacySyncKey(syncKey)) legacySyncVarCount++;

        Log("OnSyncVarChanged | mirrorRoute=SyncVar | key=" + SafeText(syncKey) +
            " | version=" + version +
            " | syncVarCount=" + syncVarCount +
            " | phase33ASyncVarCount=" + phase33ASyncVarCount +
            " | legacySyncVarCount=" + legacySyncVarCount +
            " | old=" + SafeText(oldValueJson) +
            " | new=" + SafeText(newValueJson));
    }

    public override void OnNetworkTransform(MetaverseNetworkTransformPayload payload)
    {
        transformCount++;

        if (payload != null)
        {
            lastTransformSequence = payload.sequence;
            lastNetworkPosition = payload.position;
            lastNetworkRotation = payload.rotation;
        }

        Log("OnNetworkTransform | mirrorRoute=SyncTransform | transformCount=" + transformCount +
            " | sequence=" + (payload != null ? payload.sequence : 0) +
            " | position=" + (payload != null ? payload.position.ToString() : string.Empty) +
            " | rotation=" + (payload != null ? payload.rotation.eulerAngles.ToString() : string.Empty));
    }

    public override void OnNetworkDespawn()
    {
        Log("OnNetworkDespawn | mirrorRoute=NetworkServer.Despawn | summary=" + GetSmokeDebugSummary());
    }

    public bool HasReceivedPhase33ASyncVar()
    {
        return phase33ASyncVarCount > 0;
    }

    public bool HasReceivedLegacySyncVar()
    {
        return legacySyncVarCount > 0;
    }

    public bool HasReceivedNetworkTransform()
    {
        return transformCount > 0;
    }

    public bool IsPhase33AStateSyncReady()
    {
        return HasReceivedPhase33ASyncVar() && HasReceivedNetworkTransform();
    }

    public string GetSmokeDebugSummary()
    {
        return "phase=33A" +
               " | label=" + SafeText(probeLabel) +
               " | netId=" + netId +
               " | syncVarCount=" + syncVarCount +
               " | phase33ASyncVarCount=" + phase33ASyncVarCount +
               " | legacySyncVarCount=" + legacySyncVarCount +
               " | transformCount=" + transformCount +
               " | lastSyncKey=" + SafeText(lastSyncKey) +
               " | lastSyncVersion=" + lastSyncVarVersion +
               " | lastTransformSequence=" + lastTransformSequence;
    }

    public void ResetSmokeCounters()
    {
        syncVarCount = 0;
        phase33ASyncVarCount = 0;
        legacySyncVarCount = 0;
        transformCount = 0;
        lastSyncVarVersion = 0;
        lastTransformSequence = 0;
        lastSyncKey = string.Empty;
        lastSyncValueJson = string.Empty;
        lastNetworkPosition = Vector3.zero;
        lastNetworkRotation = Quaternion.identity;
        Log("ResetSmokeCounters | mirrorRoute=StateSyncSmokeProbe.Reset");
    }

    private bool IsPhase33ASyncKey(string syncKey)
    {
        return string.Equals(SafeText(syncKey), "phase33A_status", System.StringComparison.OrdinalIgnoreCase);
    }

    private bool IsLegacySyncKey(string syncKey)
    {
        if (!acceptLegacyPhase2122SyncKeys) return false;
        return string.Equals(SafeText(syncKey), "phase21_status", System.StringComparison.OrdinalIgnoreCase) ||
               string.Equals(SafeText(syncKey), "phase21_22_status", System.StringComparison.OrdinalIgnoreCase);
    }

    private void Log(string message)
    {
        if (!logMessages) return;
        Debug.Log("[MetaverseNetworkStateSyncSmokeProbe] " + message +
                  " | label=" + probeLabel +
                  " | netId=" + netId +
                  " | isServer=" + BoolText(isServer) +
                  " | isClient=" + BoolText(isClient) +
                  " | hasAuthority=" + BoolText(hasAuthority) +
                  " | isLocalOwner=" + BoolText(isLocalOwner));
    }

    private string SafeText(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Replace("\"", "'");
    }

    private string BoolText(bool value)
    {
        return value ? "True" : "False";
    }
}
