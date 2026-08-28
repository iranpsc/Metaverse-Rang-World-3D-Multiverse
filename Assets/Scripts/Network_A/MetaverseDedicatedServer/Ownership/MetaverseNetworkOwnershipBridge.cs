using System;
using System.Collections.Generic;
using Network_A.DedicatedGameServer.Client;
using Network_A.GameServer.Players;
using Network_A.Realtime.Protocol;
using UnityEngine;

public class MetaverseNetworkOwnershipBridge : MonoBehaviour
{
    public static MetaverseNetworkOwnershipBridge Instance { get; private set; }

    [Header("References")]
    [SerializeField] private MetaverseSpawnManager spawnManager;
    [SerializeField] private DedicatedGameServerWsClient dedicatedClient;

    [Header("Authority")]
    [SerializeField] private bool requireServerForOwnershipChanges = true;
    [SerializeField] private bool allowEditorSimulation = true;

    [Header("Debug")]
    [SerializeField] private bool logMessages = true;

    private bool clientEventsBound;
    private long nextOwnershipVersion = 1;
    private string lastOwnershipRejectReason = string.Empty;
    private readonly Dictionary<int, long> dict_lastAppliedOwnershipVersionByNetId = new Dictionary<int, long>();

    public string LastOwnershipRejectReason => lastOwnershipRejectReason;
    public long NextOwnershipVersion => nextOwnershipVersion;
    public bool IsReady => spawnManager != null;

    public event Action<string> OutboundMessageReady;

    public void Bind(MetaverseSpawnManager manager)
    {
        spawnManager = manager;
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
    }

    private void OnEnable()
    {
        EnsureReferences();
        BindClientEvents();
    }

    private void Update()
    {
        EnsureReferences();
        BindClientEvents();
    }

    private void OnDisable()
    {
        UnbindClientEvents();
    }

    private void OnDestroy()
    {
        UnbindClientEvents();
        if (Instance == this) Instance = null;
    }

    public bool SetOwner(MetaverseNetworkIdentity identity, DedicatedPlayerSession session, bool serverOwned, string reason = "")
    {
        if (identity == null || session == null)
        {
            SetRejectReason("invalid_identity_or_session");
            return false;
        }

        return SetOwner(identity, session.connectionId, session.userId, session.playerId, serverOwned, reason);
    }

    public bool SetOwner(MetaverseNetworkIdentity identity, int ownerConnectionId, string ownerUserId = "", string ownerPlayerId = "", bool serverOwned = false, string reason = "")
    {
        return SetOwner(identity, ownerConnectionId >= 0 ? ownerConnectionId.ToString() : string.Empty, ownerUserId, ownerPlayerId, serverOwned, reason);
    }

    public bool SetOwner(MetaverseNetworkIdentity identity, string ownerConnectionId, string ownerUserId, string ownerPlayerId, bool serverOwned, string reason = "")
    {
        if (!CanSetOwner(identity))
        {
            if (logMessages)
            {
                Debug.LogWarning("[MetaverseNetworkOwnershipBridge] Ownership rejected | reason=" + SafeTrim(lastOwnershipRejectReason) +
                                 " | netId=" + (identity != null ? identity.NetId.ToString() : "0"));
            }

            return false;
        }

        MetaverseNetworkOwnershipPayload payload = new MetaverseNetworkOwnershipPayload
        {
            type = RealtimeMessageTypes.Ownership,
            netId = identity.NetId,
            prefabId = identity.PrefabId,
            roomId = identity.RoomId,
            ownerConnectionId = SafeTrim(ownerConnectionId),
            ownerUserId = SafeTrim(ownerUserId),
            ownerPlayerId = SafeTrim(ownerPlayerId),
            previousOwnerConnectionId = identity.OwnerConnectionIdText,
            previousOwnerUserId = identity.OwnerUserId,
            previousOwnerPlayerId = identity.OwnerPlayerId,
            serverOwned = serverOwned,
            reason = string.IsNullOrWhiteSpace(reason) ? "ownership_assigned" : reason.Trim(),
            version = nextOwnershipVersion++,
            serverTimeUnixMs = NowUnixMs()
        };

        if (!string.IsNullOrWhiteSpace(payload.roomId)) identity.SetRoomId(payload.roomId);
        identity.SetOwnerInfo(payload.ownerConnectionId, payload.ownerUserId, payload.ownerPlayerId, serverOwned);

        string json = MetaverseNetworkOwnershipMessageCodec.CreateOwnershipEnvelopeJson(payload);
        if (string.IsNullOrWhiteSpace(json))
        {
            SetRejectReason("ownership_envelope_create_failed");
            return false;
        }

        OutboundMessageReady?.Invoke(json);
        lastOwnershipRejectReason = string.Empty;

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkOwnershipBridge] Ownership outbound ready | netId=" + payload.netId +
                      " | ownerUserId=" + SafeTrim(payload.ownerUserId) +
                      " | ownerConnectionId=" + SafeTrim(payload.ownerConnectionId) +
                      " | serverOwned=" + payload.serverOwned +
                      " | version=" + payload.version +
                      " | mirrorRoute=NetworkServer.SetOwner/AssignClientAuthority | outgoingRoute=game/ownership");
        }

        return true;
    }

    public bool AssignClientAuthority(MetaverseNetworkIdentity identity, DedicatedPlayerSession session, string reason = "assign_client_authority")
    {
        if (session == null)
        {
            SetRejectReason("invalid_target_session");
            return false;
        }

        return SetOwner(identity, session.connectionId, session.userId, session.playerId, false, reason);
    }

    public bool AssignClientAuthority(MetaverseNetworkIdentity identity, string ownerConnectionId, string ownerUserId = "", string ownerPlayerId = "", string reason = "assign_client_authority")
    {
        return SetOwner(identity, ownerConnectionId, ownerUserId, ownerPlayerId, false, reason);
    }

    public bool RemoveClientAuthority(MetaverseNetworkIdentity identity, string reason = "remove_client_authority")
    {
        return SetOwner(identity, string.Empty, string.Empty, string.Empty, true, reason);
    }

    public bool SetServerOwned(MetaverseNetworkIdentity identity, string reason = "set_server_owned")
    {
        return RemoveClientAuthority(identity, reason);
    }

    public bool CanSetOwner(MetaverseNetworkIdentity identity)
    {
        if (identity == null)
        {
            SetRejectReason("identity_missing");
            return false;
        }

        if (identity.NetId <= 0)
        {
            SetRejectReason("invalid_net_id");
            return false;
        }

        if (requireServerForOwnershipChanges && !IsServerWriteAllowed())
        {
            SetRejectReason("server_authority_required");
            return false;
        }

        SetRejectReason(string.Empty);
        return true;
    }

    public bool IsOwner(MetaverseNetworkIdentity identity, DedicatedPlayerSession session)
    {
        if (identity == null || session == null) return false;
        if (!string.IsNullOrWhiteSpace(identity.OwnerConnectionIdText) && string.Equals(identity.OwnerConnectionIdText, session.connectionId, StringComparison.Ordinal)) return true;
        if (!string.IsNullOrWhiteSpace(identity.OwnerUserId) && string.Equals(identity.OwnerUserId, session.userId, StringComparison.Ordinal)) return true;
        if (!string.IsNullOrWhiteSpace(identity.OwnerPlayerId) && string.Equals(identity.OwnerPlayerId, session.playerId, StringComparison.Ordinal)) return true;
        return false;
    }

    public bool IsLocalOwner(MetaverseNetworkIdentity identity)
    {
        return identity != null && MetaverseNetworkClient.IsLocalOwner(identity);
    }

    public bool TryGetIdentity(int netId, out MetaverseNetworkIdentity identity)
    {
        return TryGetClientIdentity(netId, out identity);
    }

    public void ClearAppliedOwnershipVersionCache()
    {
        dict_lastAppliedOwnershipVersionByNetId.Clear();
    }

    private void HandleDedicatedClientRawMessage(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return;
        if (!MetaverseNetworkOwnershipMessageCodec.TryReadOwnershipPayload(rawJson, out MetaverseNetworkOwnershipPayload payload) || payload == null) return;

        if (payload.netId <= 0) return;
        if (dict_lastAppliedOwnershipVersionByNetId.TryGetValue(payload.netId, out long lastVersion) && payload.version <= lastVersion)
        {
            if (logMessages)
            {
                Debug.LogWarning("[MetaverseNetworkOwnershipBridge] Ownership ignored on client | reason=old_version | netId=" + payload.netId +
                                 " | version=" + payload.version + " | lastVersion=" + lastVersion);
            }

            return;
        }

        if (TryGetClientIdentity(payload.netId, out MetaverseNetworkIdentity identity))
        {
            dict_lastAppliedOwnershipVersionByNetId[payload.netId] = payload.version;
            if (!string.IsNullOrWhiteSpace(payload.roomId)) identity.SetRoomId(payload.roomId);
            identity.SetOwnerInfo(payload.ownerConnectionId, payload.ownerUserId, payload.ownerPlayerId, payload.serverOwned);
            if (logMessages)
            {
                Debug.Log("[MetaverseNetworkOwnershipBridge] Ownership applied on client | netId=" + payload.netId +
                          " | ownerUserId=" + SafeTrim(payload.ownerUserId) +
                          " | ownerConnectionId=" + SafeTrim(payload.ownerConnectionId) +
                          " | hasAuthority=" + identity.HasAuthority +
                          " | isLocalPlayer=" + identity.IsLocalPlayer +
                          " | mirrorRoute=OnStartAuthority/OnStopAuthority/OnOwnershipChanged | incomingRoute=game/ownership");
            }
        }
    }

    private bool TryGetClientIdentity(int netId, out MetaverseNetworkIdentity identity)
    {
        identity = null;
        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
        if (spawnManager == null) return false;
        return spawnManager.TryGetSpawnedObject(netId, out identity) && identity != null;
    }

    private void EnsureReferences()
    {
        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
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

    private void BindClientEvents()
    {
        if (Application.isBatchMode) return;
        if (clientEventsBound || dedicatedClient == null) return;
        dedicatedClient.RawMessageReceived -= HandleDedicatedClientRawMessage;
        dedicatedClient.RawMessageReceived += HandleDedicatedClientRawMessage;
        clientEventsBound = true;
        if (logMessages) Debug.Log("[MetaverseNetworkOwnershipBridge] Bound to dedicated client raw messages.");
    }

    private void UnbindClientEvents()
    {
        if (dedicatedClient != null) dedicatedClient.RawMessageReceived -= HandleDedicatedClientRawMessage;
        clientEventsBound = false;
    }

    private bool IsServerWriteAllowed()
    {
        if (Application.isBatchMode) return true;
#if UNITY_EDITOR
        return allowEditorSimulation;
#else
        return false;
#endif
    }

    private void SetRejectReason(string reason)
    {
        lastOwnershipRejectReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();
    }

    private long NowUnixMs()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }
}
