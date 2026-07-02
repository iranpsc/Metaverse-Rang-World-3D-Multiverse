using System;
using System.Collections.Generic;
using Network_A.DedicatedGameServer.Client;
using Network_A.GameServer.Players;
using Network_A.Realtime.Protocol;
using UnityEngine;

public class MetaverseNetworkPlayerMovementBridge : MonoBehaviour
{
    public static MetaverseNetworkPlayerMovementBridge Instance { get; private set; }

    [Header("References")]
    [SerializeField] private MetaverseSpawnManager spawnManager;
    [SerializeField] private MetaverseNetworkStateSyncBridge stateSyncBridge;
    [SerializeField] private DedicatedGameServerWsClient dedicatedClient;
    [SerializeField] private MetaverseDedicatedServerRuntimeConfig config;

    [Header("Movement")]
    [SerializeField] private float movementSpeed = 2.5f;
    [SerializeField] private float maxDeltaTime = 0.25f;
    [SerializeField] private bool rejectOldInputSequence = true;

    [Header("Debug")]
    [SerializeField] private bool logMessages = true;

    private int smokeRequiredPlayers = 3;
    private bool smokeCompletedLogged;
    private string lastOwnerInputRejectReason = string.Empty;
    private readonly HashSet<string> set_movedOwners = new HashSet<string>();
    private readonly Dictionary<string, long> dict_lastSequenceByOwnerAndNetId = new Dictionary<string, long>();

    public string LastOwnerInputRejectReason => lastOwnerInputRejectReason;
    public float MovementSpeed => movementSpeed;
    public float MaxDeltaTime => maxDeltaTime;
    public bool IsReady => spawnManager != null && stateSyncBridge != null;

    public void Bind(MetaverseSpawnManager manager, MetaverseDedicatedServerRuntimeConfig runtimeConfig)
    {
        spawnManager = manager;
        config = runtimeConfig;
        EnsureReferences();
        ApplyConfig();
    }

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else if (Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        EnsureReferences();
        ApplyConfig();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public bool CanSendOwnerInput(MetaverseNetworkIdentity identity)
    {
        if (Application.isBatchMode)
        {
            SetRejectReason("client_only_api");
            return false;
        }

        if (identity == null || identity.NetId <= 0)
        {
            SetRejectReason("invalid_identity");
            return false;
        }

        if (!identity.HasAuthority && !identity.IsLocalPlayer && !MetaverseNetworkClient.IsLocalOwner(identity))
        {
            SetRejectReason("local_authority_required");
            return false;
        }

        EnsureReferences();
        if (dedicatedClient == null || !dedicatedClient.IsConnected || !dedicatedClient.IsAuthenticated)
        {
            SetRejectReason("dedicated_client_not_ready");
            return false;
        }

        SetRejectReason(string.Empty);
        return true;
    }

    public bool SendOwnerInput(MetaverseNetworkIdentity identity, float moveX, float moveZ, float deltaTime, long sequence)
    {
        if (!CanSendOwnerInput(identity))
        {
            if (logMessages)
            {
                Debug.LogWarning("[MetaverseNetworkPlayerMovementBridge] Owner input send rejected | reason=" + Safe(lastOwnerInputRejectReason) +
                                 " | netId=" + (identity != null ? identity.NetId.ToString() : "0"));
            }

            return false;
        }

        MetaverseNetworkPlayerInputPayload payload = new MetaverseNetworkPlayerInputPayload
        {
            type = RealtimeMessageTypes.PlayerInput,
            netId = identity.NetId,
            roomId = MetaverseNetworkClient.roomId,
            connectionId = MetaverseNetworkClient.connectionId,
            userId = MetaverseNetworkClient.userId,
            playerId = MetaverseNetworkClient.playerId,
            sequence = sequence,
            moveX = Mathf.Clamp(moveX, -1f, 1f),
            moveZ = Mathf.Clamp(moveZ, -1f, 1f),
            deltaTime = Mathf.Clamp(deltaTime, 0.001f, maxDeltaTime),
            clientTimeUnixMs = NowUnixMs()
        };

        string json = MetaverseNetworkPlayerInputMessageCodec.CreatePlayerInputEnvelopeJson(payload, payload.roomId);
        if (string.IsNullOrWhiteSpace(json))
        {
            SetRejectReason("player_input_envelope_create_failed");
            return false;
        }

        _ = dedicatedClient.SendRawAsync(json);

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkPlayerMovementBridge] Owner input sent | netId=" + payload.netId +
                      " | sequence=" + payload.sequence +
                      " | moveX=" + payload.moveX.ToString("0.00") +
                      " | moveZ=" + payload.moveZ.ToString("0.00") +
                      " | mirrorRoute=OwnerInput/Command | outgoingRoute=game/player_input");
        }

        return true;
    }

    public bool SendOwnerInput(GameObject obj, float moveX, float moveZ, float deltaTime, long sequence)
    {
        return SendOwnerInput(obj != null ? obj.GetComponent<MetaverseNetworkIdentity>() : null, moveX, moveZ, deltaTime, sequence);
    }

    public bool SendOwnerInput(int netId, float moveX, float moveZ, float deltaTime, long sequence)
    {
        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
        if (spawnManager == null || !spawnManager.TryGetSpawnedObject(netId, out MetaverseNetworkIdentity identity)) return false;
        return SendOwnerInput(identity, moveX, moveZ, deltaTime, sequence);
    }

    public bool CmdMove(MetaverseNetworkIdentity identity, float moveX, float moveZ, float deltaTime, long sequence)
    {
        return SendOwnerInput(identity, moveX, moveZ, deltaTime, sequence);
    }

    public bool HandleServerOwnerInput(DedicatedPlayerSession session, MetaverseNetworkPlayerInputPayload payload)
    {
        if (!CanHandleServerOwnerInput(session, payload, out MetaverseNetworkIdentity identity))
        {
            if (logMessages)
            {
                Debug.LogWarning("[MetaverseNetworkPlayerMovementBridge] Owner input rejected | reason=" + Safe(lastOwnerInputRejectReason) +
                                 " | userId=" + Safe(session != null ? session.userId : string.Empty) +
                                 " | netId=" + (payload != null ? payload.netId.ToString() : "0"));
            }

            return false;
        }

        Vector3 move = new Vector3(Mathf.Clamp(payload.moveX, -1f, 1f), 0f, Mathf.Clamp(payload.moveZ, -1f, 1f));
        if (move.sqrMagnitude > 1f) move.Normalize();

        float dt = Mathf.Clamp(payload.deltaTime <= 0f ? 0.05f : payload.deltaTime, 0.001f, maxDeltaTime);
        identity.transform.position += move * movementSpeed * dt;
        if (move.sqrMagnitude > 0.0001f)
        {
            identity.transform.rotation = Quaternion.LookRotation(move.normalized, Vector3.up);
        }

        bool sent = stateSyncBridge.SendNetworkTransform(identity);
        RegisterInputSequence(session, payload);
        RegisterSmokeMove(session);

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkPlayerMovementBridge] Owner input handled on server | userId=" + Safe(session.userId) +
                      " | netId=" + payload.netId +
                      " | sequence=" + payload.sequence +
                      " | position=" + identity.transform.position +
                      " | transformSent=" + sent +
                      " | mirrorRoute=CommandAuthorityMovement | incomingRoute=game/player_input | outgoingRoute=game/network_transform");
        }

        return sent;
    }

    public bool CanHandleServerOwnerInput(DedicatedPlayerSession session, MetaverseNetworkPlayerInputPayload payload, out MetaverseNetworkIdentity identity)
    {
        identity = null;

        if (session == null)
        {
            SetRejectReason("session_missing");
            return false;
        }

        if (payload == null || payload.netId <= 0)
        {
            SetRejectReason("invalid_player_input_payload");
            return false;
        }

        EnsureReferences();
        if (spawnManager == null || stateSyncBridge == null)
        {
            SetRejectReason("movement_bridge_not_ready");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(payload.roomId) &&
            !string.IsNullOrWhiteSpace(session.roomId) &&
            !string.Equals(payload.roomId.Trim(), session.roomId.Trim(), StringComparison.Ordinal))
        {
            SetRejectReason("room_mismatch");
            return false;
        }

        if (!spawnManager.TryGetSpawnedObject(payload.netId, out identity) || identity == null)
        {
            SetRejectReason("net_id_not_found");
            return false;
        }

        if (!IsOwner(identity, session))
        {
            SetRejectReason("not_owner");
            return false;
        }

        if (rejectOldInputSequence && IsOldInputSequence(session, payload))
        {
            SetRejectReason("old_input_sequence");
            return false;
        }

        SetRejectReason(string.Empty);
        return true;
    }

    public void ClearSequenceCache()
    {
        dict_lastSequenceByOwnerAndNetId.Clear();
    }

    public string GetMovementDebugSummary()
    {
        return "Phase33A Movement | ready=" + IsReady +
               " | speed=" + movementSpeed.ToString("0.00") +
               " | maxDeltaTime=" + maxDeltaTime.ToString("0.000") +
               " | movedOwners=" + set_movedOwners.Count +
               " | lastReject=" + Safe(lastOwnerInputRejectReason);
    }

    private bool IsOwner(MetaverseNetworkIdentity identity, DedicatedPlayerSession session)
    {
        if (identity == null || session == null) return false;
        if (!string.IsNullOrWhiteSpace(identity.OwnerConnectionIdText) &&
            string.Equals(identity.OwnerConnectionIdText, session.connectionId, StringComparison.Ordinal)) return true;
        if (!string.IsNullOrWhiteSpace(identity.OwnerUserId) &&
            string.Equals(identity.OwnerUserId, session.userId, StringComparison.Ordinal)) return true;
        if (!string.IsNullOrWhiteSpace(identity.OwnerPlayerId) &&
            string.Equals(identity.OwnerPlayerId, session.playerId, StringComparison.Ordinal)) return true;
        return false;
    }

    private bool IsOldInputSequence(DedicatedPlayerSession session, MetaverseNetworkPlayerInputPayload payload)
    {
        string key = BuildSequenceKey(session, payload.netId);
        if (!dict_lastSequenceByOwnerAndNetId.TryGetValue(key, out long lastSequence)) return false;
        return payload.sequence > 0 && payload.sequence <= lastSequence;
    }

    private void RegisterInputSequence(DedicatedPlayerSession session, MetaverseNetworkPlayerInputPayload payload)
    {
        if (session == null || payload == null || payload.sequence <= 0) return;
        dict_lastSequenceByOwnerAndNetId[BuildSequenceKey(session, payload.netId)] = payload.sequence;
    }

    private string BuildSequenceKey(DedicatedPlayerSession session, int netId)
    {
        string ownerKey = !string.IsNullOrWhiteSpace(session.connectionId) ? session.connectionId.Trim() : session.userId;
        return Safe(ownerKey) + ":" + netId;
    }

    private void RegisterSmokeMove(DedicatedPlayerSession session)
    {
        if (session == null || config == null || !config.EnableNetworkPlayerMovementSmokeTest) return;
        string key = !string.IsNullOrWhiteSpace(session.connectionId) ? session.connectionId.Trim() : session.userId;
        if (string.IsNullOrWhiteSpace(key)) return;
        set_movedOwners.Add(key);
        if (smokeCompletedLogged || set_movedOwners.Count < smokeRequiredPlayers) return;
        smokeCompletedLogged = true;
        Debug.Log("[MetaverseNetworkPlayerMovementBridge] Smoke flow completed | phase=33A | expected=OwnerInput->ServerAuthority->NetworkTransform | required=" +
                  smokeRequiredPlayers + " | movedOwners=" + set_movedOwners.Count);
    }

    private void EnsureReferences()
    {
        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
        if (stateSyncBridge == null) stateSyncBridge = MetaverseNetworkStateSyncBridge.Instance;
        if (config == null) config = MetaverseDedicatedServerRuntimeConfig.LoadDefault();
        if (!Application.isBatchMode && dedicatedClient == null)
        {
            dedicatedClient = DedicatedGameServerWsClient.Instance;
#if UNITY_2023_1_OR_NEWER
            if (dedicatedClient == null) dedicatedClient = FindFirstObjectByType<DedicatedGameServerWsClient>();
#else
            if (dedicatedClient == null) dedicatedClient = FindObjectOfType<DedicatedGameServerWsClient>();
#endif
        }
    }

    private void ApplyConfig()
    {
        if (config == null) return;
        movementSpeed = config.NetworkPlayerMovementSpeed;
        maxDeltaTime = config.NetworkPlayerMovementMaxDeltaTime;
        smokeRequiredPlayers = config.NetworkPlayerMovementSmokeRequiredPlayers;
    }

    private void SetRejectReason(string reason)
    {
        lastOwnerInputRejectReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();
    }

    private long NowUnixMs()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private string Safe(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
