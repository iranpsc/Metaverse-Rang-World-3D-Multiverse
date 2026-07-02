using System.Collections;
using UnityEngine;

public class MetaverseNetworkPlayerMovementSmokeProbe : MetaverseNetworkBehaviour
{
    [SerializeField] private string label = "phase33A_player_movement_probe";
    [SerializeField] private bool sendSmokeInput = true;
    [SerializeField] private bool preferMirrorLikeApi = true;
    [SerializeField] private float firstInputDelaySeconds = 1.5f;
    [SerializeField] private float inputIntervalSeconds = 0.6f;
    [SerializeField] private float inputDeltaTime = 0.1f;

    private bool smokeStarted;
    private Coroutine smokeRoutine;
    private long sequence;
    private int sentInputCount;
    private int receivedTransformCount;
    private string lastRejectReason = string.Empty;

    public long LastSequence => sequence;
    public int SentInputCount => sentInputCount;
    public int ReceivedTransformCount => receivedTransformCount;
    public string LastRejectReason => lastRejectReason;
    public bool IsSmokeStarted => smokeStarted;

    private void OnDestroy()
    {
        if (smokeRoutine == null) return;
        MetaverseNetworkPlayerMovementSmokeRunner.StopSmoke(smokeRoutine);
        smokeRoutine = null;
    }

    public override void OnStartLocalPlayer()
    {
        Debug.Log(BuildLog("OnStartLocalPlayer") + " | mirrorRoute=NetworkBehaviour.OnStartLocalPlayer");
        TryStartSmokeInput();
    }

    public override void OnStartAuthority()
    {
        Debug.Log(BuildLog("OnStartAuthority") + " | mirrorRoute=NetworkBehaviour.OnStartAuthority");
        TryStartSmokeInput();
    }

    public override void OnOwnershipChanged(string previousOwnerUserId, string newOwnerUserId, string previousOwnerPlayerId, string newOwnerPlayerId, bool isLocalOwner)
    {
        Debug.Log(BuildLog("OnOwnershipChanged") +
                  " | mirrorRoute=NetworkBehaviour.OnOwnershipChanged" +
                  " | previousOwnerUserId=" + Safe(previousOwnerUserId) +
                  " | newOwnerUserId=" + Safe(newOwnerUserId) +
                  " | previousOwnerPlayerId=" + Safe(previousOwnerPlayerId) +
                  " | newOwnerPlayerId=" + Safe(newOwnerPlayerId) +
                  " | isLocalOwner=" + isLocalOwner);
        if (isLocalOwner) TryStartSmokeInput();
    }

    public override void OnNetworkTransform(MetaverseNetworkTransformPayload payload)
    {
        receivedTransformCount++;
        Debug.Log(BuildLog("OnNetworkTransform") +
                  " | mirrorRoute=NetworkBehaviour.OnNetworkTransform" +
                  " | receivedTransformCount=" + receivedTransformCount +
                  " | sequence=" + (payload != null ? payload.sequence : 0) +
                  " | position=" + (payload != null ? payload.position.ToString() : string.Empty));
    }

    private void TryStartSmokeInput()
    {
        if (!sendSmokeInput || smokeStarted || smokeRoutine != null) return;
        if (!CanStartOwnerInputSmoke()) return;

        MetaverseDedicatedServerRuntimeConfig config = MetaverseDedicatedServerRuntimeConfig.LoadDefault();
        if (config == null || !config.EnableNetworkPlayerMovementSmokeTest) return;

        smokeStarted = true;
        smokeRoutine = MetaverseNetworkPlayerMovementSmokeRunner.StartSmoke(SendSmokeInputs());
        Debug.Log(BuildLog("OwnerInputSmokeRunnerStarted") +
                  " | phase=33A" +
                  " | mirrorRoute=CmdMove/OwnerInput" +
                  " | activeSelf=" + gameObject.activeSelf +
                  " | activeInHierarchy=" + gameObject.activeInHierarchy +
                  " | runner=MetaverseNetworkPlayerMovementSmokeRunner");
    }

    private bool CanStartOwnerInputSmoke()
    {
        bool canStart = hasAuthority || isLocalPlayer || isLocalOwner;
        if (!canStart)
        {
            SetRejectReason("not_local_owner");
            return false;
        }

        SetRejectReason(string.Empty);
        return true;
    }

    private IEnumerator SendSmokeInputs()
    {
        yield return new WaitForSeconds(Mathf.Max(0f, firstInputDelaySeconds));
        while (!MetaverseNetworkClient.isAuthenticated)
        {
            yield return new WaitForSeconds(0.2f);
        }

        if (this == null || !CanStartOwnerInputSmoke()) yield break;
        SendInput(1f, 0f);

        yield return new WaitForSeconds(Mathf.Max(0.1f, inputIntervalSeconds));
        if (this == null || !CanStartOwnerInputSmoke()) yield break;
        SendInput(0f, 1f);

        yield return new WaitForSeconds(Mathf.Max(0.1f, inputIntervalSeconds));
        if (this == null || !CanStartOwnerInputSmoke()) yield break;
        SendInput(1f, 1f);

        Debug.Log(BuildLog("OwnerInputSmokeFinished") +
                  " | phase=33A" +
                  " | mirrorRoute=OwnerInput/CmdMove" +
                  " | sentCount=" + sentInputCount +
                  " | receivedTransformCount=" + receivedTransformCount);
        smokeRoutine = null;
    }

    private void SendInput(float moveX, float moveZ)
    {
        sequence++;
        bool sent = false;

        if (preferMirrorLikeApi)
        {
            sent = SendOwnerInput(moveX, moveZ, inputDeltaTime, sequence);
        }
        else
        {
            sent = MetaverseNetworkPlayerMovementBridge.Instance != null && MetaverseNetworkPlayerMovementBridge.Instance.SendOwnerInput(NetworkIdentity, moveX, moveZ, inputDeltaTime, sequence);
        }

        if (sent) sentInputCount++;
        else SetRejectReason(MetaverseNetworkPlayerMovementBridge.Instance != null ? MetaverseNetworkPlayerMovementBridge.Instance.LastOwnerInputRejectReason : "movement_bridge_missing");

        Debug.Log(BuildLog("OwnerInputSmokeSent") +
                  " | phase=33A" +
                  " | mirrorRoute=" + (preferMirrorLikeApi ? "NetworkBehaviour.SendOwnerInput" : "MovementBridge.SendOwnerInput") +
                  " | sequence=" + sequence +
                  " | moveX=" + moveX.ToString("0.00") +
                  " | moveZ=" + moveZ.ToString("0.00") +
                  " | sent=" + sent +
                  " | outgoingRoute=game/player_input" +
                  " | reject=" + Safe(lastRejectReason));
    }

    public string GetSmokeDebugSummary()
    {
        return "Phase33A PlayerMovementSmokeProbe" +
               " | label=" + Safe(label) +
               " | netId=" + netId +
               " | started=" + smokeStarted +
               " | sent=" + sentInputCount +
               " | receivedTransforms=" + receivedTransformCount +
               " | sequence=" + sequence +
               " | isLocalOwner=" + isLocalOwner +
               " | lastReject=" + Safe(lastRejectReason);
    }

    public void ResetSmokeCounters()
    {
        smokeStarted = false;
        sequence = 0;
        sentInputCount = 0;
        receivedTransformCount = 0;
        lastRejectReason = string.Empty;
    }

    private string BuildLog(string callback)
    {
        MetaverseNetworkIdentity identity = NetworkIdentity;
        return "[MetaverseNetworkPlayerMovementSmokeProbe] " + callback +
               " | phase=33A" +
               " | label=" + Safe(label) +
               " | netId=" + netId +
               " | ownerUserId=" + (identity != null ? Safe(identity.OwnerUserId) : string.Empty) +
               " | ownerPlayerId=" + (identity != null ? Safe(identity.OwnerPlayerId) : string.Empty) +
               " | ownerConnectionId=" + (identity != null ? Safe(identity.OwnerConnectionIdText) : string.Empty) +
               " | isServer=" + isServer +
               " | isClient=" + isClient +
               " | hasAuthority=" + hasAuthority +
               " | isLocalPlayer=" + isLocalPlayer +
               " | isLocalOwner=" + isLocalOwner;
    }

    private void SetRejectReason(string reason)
    {
        lastRejectReason = Safe(reason);
    }

    private string Safe(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}

public class MetaverseNetworkPlayerMovementSmokeRunner : MonoBehaviour
{
    private static MetaverseNetworkPlayerMovementSmokeRunner instance;

    public static Coroutine StartSmoke(IEnumerator routine)
    {
        EnsureInstance();
        return instance.StartCoroutine(routine);
    }

    public static void StopSmoke(Coroutine routine)
    {
        if (instance == null || routine == null) return;
        instance.StopCoroutine(routine);
    }

    private static void EnsureInstance()
    {
        if (instance != null) return;

        GameObject runnerObject = new GameObject("MetaverseNetworkPlayerMovementSmokeRunner");
        DontDestroyOnLoad(runnerObject);
        instance = runnerObject.AddComponent<MetaverseNetworkPlayerMovementSmokeRunner>();
    }
}
