using System;
using System.Collections.Generic;
using Network_A.DedicatedGameServer.Client;
using Network_A.GameServer.Players;
using Network_A.Realtime.Protocol;
using UnityEngine;

public class MetaverseNetworkRpcBridge : MonoBehaviour
{
    public static MetaverseNetworkRpcBridge Instance { get; private set; }

    [Header("References")]
    [SerializeField] private MetaverseSpawnManager spawnManager;
    [SerializeField] private DedicatedGameServerWsClient dedicatedClient;

    [Header("Client Queue")]
    [SerializeField] private float queuedCommandLifetimeSeconds = 20f;

    [Header("Server Command Validation")]
    [SerializeField] private bool requireAuthorityForClientCommands = true;
    [SerializeField] private bool allowServerOwnedCommandsWithoutOwner = true;
    [SerializeField] private bool rejectPrefabMismatch = true;
    [SerializeField] private int maxCommandNameLength = 96;
    [SerializeField] private int maxRpcNameLength = 96;
    [SerializeField] private int maxPayloadLength = 32768;

    [Header("Server Rpc Rules")]
    [SerializeField] private bool requireServerForOutboundRpc = true;

    [Header("Debug")]
    [SerializeField] private bool logMessages = true;
    [SerializeField] private bool logRejectedCommands = true;

    private bool clientEventsBound;
    private long nextClientCommandSequence = 1;
    private readonly List<QueuedCommand> list_queuedCommands = new List<QueuedCommand>();

    public event Action<string> OutboundMessageReady;

    public string LastServerCommandRejectReason { get; private set; } = string.Empty;
    public bool RequireAuthorityForClientCommands => requireAuthorityForClientCommands;
    public bool AllowServerOwnedCommandsWithoutOwner => allowServerOwnedCommandsWithoutOwner;

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
        FlushQueuedCommands();
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

    public bool SendCommand(MetaverseNetworkIdentity identity, string commandName, string payloadJson = "")
    {
        if (identity == null) return false;
        return SendCommand(identity.NetId, identity.PrefabId, commandName, payloadJson);
    }

    public bool SendCommand(GameObject obj, string commandName, string payloadJson = "")
    {
        MetaverseNetworkIdentity identity = obj != null ? obj.GetComponent<MetaverseNetworkIdentity>() : null;
        return SendCommand(identity, commandName, payloadJson);
    }

    public bool SendCommand(int netId, string prefabId, string commandName, string payloadJson = "")
    {
        string safeCommandName = SafeTrim(commandName);
        if (!IsValidMethodName(safeCommandName, maxCommandNameLength)) return false;
        if (netId <= 0) return false;
        if (!IsValidPayloadLength(payloadJson)) return false;

        if (!CanClientSendNow())
        {
            QueueCommand(netId, prefabId, safeCommandName, payloadJson);
            return false;
        }

        MetaverseNetworkRpcPayload payload = BuildBasePayload(RealtimeMessageTypes.Command, netId, prefabId, safeCommandName, payloadJson);
        payload.senderConnectionId = MetaverseNetworkClient.connectionId;
        payload.senderUserId = MetaverseNetworkClient.userId;
        payload.senderPlayerId = MetaverseNetworkClient.playerId;
        payload.roomId = MetaverseNetworkClient.roomId;
        payload.sequence = nextClientCommandSequence++;
        payload.clientTimeUnixMs = NowUnixMs();

        string json = MetaverseNetworkRpcMessageCodec.CreateCommandEnvelopeJson(payload, payload.roomId);
        if (string.IsNullOrWhiteSpace(json)) return false;

        _ = dedicatedClient.SendRawAsync(json);

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkRpcBridge] Command sent | netId=" + payload.netId +
                      " | command=" + payload.methodName +
                      " | sequence=" + payload.sequence +
                      " | outgoingRoute=game/command");
        }

        return true;
    }

    public bool Cmd(MetaverseNetworkIdentity identity, string commandName, string payloadJson = "")
    {
        return SendCommand(identity, commandName, payloadJson);
    }

    public bool Cmd(GameObject obj, string commandName, string payloadJson = "")
    {
        return SendCommand(obj, commandName, payloadJson);
    }

    public bool Cmd(int netId, string prefabId, string commandName, string payloadJson = "")
    {
        return SendCommand(netId, prefabId, commandName, payloadJson);
    }

    public bool SendClientRpc(MetaverseNetworkIdentity identity, string rpcName, string payloadJson = "")
    {
        if (identity == null) return false;
        return SendClientRpc(identity.NetId, identity.PrefabId, rpcName, payloadJson);
    }

    public bool SendClientRpc(GameObject obj, string rpcName, string payloadJson = "")
    {
        MetaverseNetworkIdentity identity = obj != null ? obj.GetComponent<MetaverseNetworkIdentity>() : null;
        return SendClientRpc(identity, rpcName, payloadJson);
    }

    public bool SendClientRpc(int netId, string prefabId, string rpcName, string payloadJson = "")
    {
        string safeRpcName = SafeTrim(rpcName);
        if (!CanServerSendRpc(netId, safeRpcName)) return false;
        if (!IsValidPayloadLength(payloadJson)) return false;

        MetaverseNetworkRpcPayload payload = BuildBasePayload(RealtimeMessageTypes.ClientRpc, netId, prefabId, safeRpcName, payloadJson);
        payload.serverTimeUnixMs = NowUnixMs();

        string json = MetaverseNetworkRpcMessageCodec.CreateClientRpcEnvelopeJson(payload);
        if (string.IsNullOrWhiteSpace(json)) return false;

        OutboundMessageReady?.Invoke(json);

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkRpcBridge] ClientRpc outbound ready | netId=" + netId +
                      " | rpc=" + safeRpcName +
                      " | outgoingRoute=game/client_rpc");
        }

        return true;
    }

    public bool Rpc(MetaverseNetworkIdentity identity, string rpcName, string payloadJson = "")
    {
        return SendClientRpc(identity, rpcName, payloadJson);
    }

    public bool Rpc(GameObject obj, string rpcName, string payloadJson = "")
    {
        return SendClientRpc(obj, rpcName, payloadJson);
    }

    public bool Rpc(int netId, string prefabId, string rpcName, string payloadJson = "")
    {
        return SendClientRpc(netId, prefabId, rpcName, payloadJson);
    }

    public bool SendTargetRpc(MetaverseNetworkIdentity identity, string targetConnectionId, string rpcName, string payloadJson = "")
    {
        if (identity == null) return false;
        return SendTargetRpc(identity.NetId, identity.PrefabId, targetConnectionId, rpcName, payloadJson);
    }

    public bool SendTargetRpc(MetaverseNetworkIdentity identity, DedicatedPlayerSession targetSession, string rpcName, string payloadJson = "")
    {
        if (targetSession == null) return false;
        return SendTargetRpc(identity, targetSession.connectionId, rpcName, payloadJson);
    }

    public bool SendTargetRpc(GameObject obj, string targetConnectionId, string rpcName, string payloadJson = "")
    {
        MetaverseNetworkIdentity identity = obj != null ? obj.GetComponent<MetaverseNetworkIdentity>() : null;
        return SendTargetRpc(identity, targetConnectionId, rpcName, payloadJson);
    }

    public bool SendTargetRpc(GameObject obj, DedicatedPlayerSession targetSession, string rpcName, string payloadJson = "")
    {
        if (targetSession == null) return false;
        return SendTargetRpc(obj, targetSession.connectionId, rpcName, payloadJson);
    }

    public bool SendTargetRpc(int netId, string prefabId, string targetConnectionId, string rpcName, string payloadJson = "")
    {
        string safeRpcName = SafeTrim(rpcName);
        string safeTargetConnectionId = SafeTrim(targetConnectionId);
        if (!CanServerSendRpc(netId, safeRpcName)) return false;
        if (string.IsNullOrWhiteSpace(safeTargetConnectionId)) return false;
        if (!IsValidPayloadLength(payloadJson)) return false;

        MetaverseNetworkRpcPayload payload = BuildBasePayload(RealtimeMessageTypes.TargetRpc, netId, prefabId, safeRpcName, payloadJson);
        payload.targetConnectionId = safeTargetConnectionId;
        payload.serverTimeUnixMs = NowUnixMs();

        string json = MetaverseNetworkRpcMessageCodec.CreateTargetRpcEnvelopeJson(payload);
        if (string.IsNullOrWhiteSpace(json)) return false;

        OutboundMessageReady?.Invoke(json);

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkRpcBridge] TargetRpc outbound ready | netId=" + netId +
                      " | targetConnectionId=" + safeTargetConnectionId +
                      " | rpc=" + safeRpcName +
                      " | outgoingRoute=game/target_rpc");
        }

        return true;
    }

    public bool SendTargetRpcToUser(MetaverseNetworkIdentity identity, string targetUserId, string rpcName, string payloadJson = "")
    {
        if (identity == null) return false;
        return SendTargetRpcToUser(identity.NetId, identity.PrefabId, targetUserId, rpcName, payloadJson);
    }

    public bool SendTargetRpcToUser(int netId, string prefabId, string targetUserId, string rpcName, string payloadJson = "")
    {
        string safeRpcName = SafeTrim(rpcName);
        string safeTargetUserId = SafeTrim(targetUserId);
        if (!CanServerSendRpc(netId, safeRpcName)) return false;
        if (string.IsNullOrWhiteSpace(safeTargetUserId)) return false;
        if (!IsValidPayloadLength(payloadJson)) return false;

        MetaverseNetworkRpcPayload payload = BuildBasePayload(RealtimeMessageTypes.TargetRpc, netId, prefabId, safeRpcName, payloadJson);
        payload.targetUserId = safeTargetUserId;
        payload.serverTimeUnixMs = NowUnixMs();

        string json = MetaverseNetworkRpcMessageCodec.CreateTargetRpcEnvelopeJson(payload);
        if (string.IsNullOrWhiteSpace(json)) return false;

        OutboundMessageReady?.Invoke(json);
        return true;
    }

    public bool SendTargetRpcToPlayer(MetaverseNetworkIdentity identity, string targetPlayerId, string rpcName, string payloadJson = "")
    {
        if (identity == null) return false;
        return SendTargetRpcToPlayer(identity.NetId, identity.PrefabId, targetPlayerId, rpcName, payloadJson);
    }

    public bool SendTargetRpcToPlayer(int netId, string prefabId, string targetPlayerId, string rpcName, string payloadJson = "")
    {
        string safeRpcName = SafeTrim(rpcName);
        string safeTargetPlayerId = SafeTrim(targetPlayerId);
        if (!CanServerSendRpc(netId, safeRpcName)) return false;
        if (string.IsNullOrWhiteSpace(safeTargetPlayerId)) return false;
        if (!IsValidPayloadLength(payloadJson)) return false;

        MetaverseNetworkRpcPayload payload = BuildBasePayload(RealtimeMessageTypes.TargetRpc, netId, prefabId, safeRpcName, payloadJson);
        payload.targetPlayerId = safeTargetPlayerId;
        payload.serverTimeUnixMs = NowUnixMs();

        string json = MetaverseNetworkRpcMessageCodec.CreateTargetRpcEnvelopeJson(payload);
        if (string.IsNullOrWhiteSpace(json)) return false;

        OutboundMessageReady?.Invoke(json);
        return true;
    }

    public bool TargetRpc(MetaverseNetworkIdentity identity, string targetConnectionId, string rpcName, string payloadJson = "")
    {
        return SendTargetRpc(identity, targetConnectionId, rpcName, payloadJson);
    }

    public bool TargetRpc(MetaverseNetworkIdentity identity, DedicatedPlayerSession targetSession, string rpcName, string payloadJson = "")
    {
        return SendTargetRpc(identity, targetSession, rpcName, payloadJson);
    }

    public bool TargetRpc(GameObject obj, string targetConnectionId, string rpcName, string payloadJson = "")
    {
        return SendTargetRpc(obj, targetConnectionId, rpcName, payloadJson);
    }

    public bool TargetRpc(int netId, string prefabId, string targetConnectionId, string rpcName, string payloadJson = "")
    {
        return SendTargetRpc(netId, prefabId, targetConnectionId, rpcName, payloadJson);
    }

    public bool HandleServerCommand(DedicatedPlayerSession senderSession, MetaverseNetworkRpcPayload payload)
    {
        EnsureReferences();

        if (!ValidateServerCommand(senderSession, payload, out MetaverseNetworkIdentity identity))
        {
            return false;
        }

        payload.type = RealtimeMessageTypes.Command;
        payload.senderConnectionId = SafeTrim(senderSession.connectionId);
        payload.senderUserId = SafeTrim(senderSession.userId);
        payload.senderPlayerId = SafeTrim(senderSession.playerId);
        payload.roomId = SafeTrim(senderSession.roomId);
        payload.prefabId = string.IsNullOrWhiteSpace(payload.prefabId) ? identity.PrefabId : SafeTrim(payload.prefabId);
        payload.methodName = SafeTrim(payload.methodName);
        payload.payloadJson = SafeJson(payload.payloadJson);
        payload.serverTimeUnixMs = NowUnixMs();

        DispatchServerCommand(identity, payload, senderSession);

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkRpcBridge] Command dispatched on server | netId=" + payload.netId +
                      " | command=" + SafeTrim(payload.methodName) +
                      " | senderUserId=" + SafeTrim(senderSession.userId) +
                      " | authorityMode=" + ResolveAuthorityMode(identity, senderSession) +
                      " | incomingRoute=game/command");
        }

        return true;
    }

    public bool CanHandleServerCommand(DedicatedPlayerSession senderSession, MetaverseNetworkRpcPayload payload)
    {
        return ValidateServerCommand(senderSession, payload, out MetaverseNetworkIdentity _);
    }

    private bool ValidateServerCommand(DedicatedPlayerSession senderSession, MetaverseNetworkRpcPayload payload, out MetaverseNetworkIdentity identity)
    {
        identity = null;
        LastServerCommandRejectReason = string.Empty;

        if (senderSession == null) return RejectServerCommand("session_missing", senderSession, payload);
        if (!senderSession.isAuthenticated) return RejectServerCommand("session_not_authenticated", senderSession, payload);
        if (payload == null) return RejectServerCommand("payload_missing", senderSession, payload);

        payload.methodName = SafeTrim(payload.methodName);
        payload.payloadJson = SafeJson(payload.payloadJson);

        if (payload.netId <= 0) return RejectServerCommand("invalid_net_id", senderSession, payload);
        if (!IsValidMethodName(payload.methodName, maxCommandNameLength)) return RejectServerCommand("invalid_command_name", senderSession, payload);
        if (!IsValidPayloadLength(payload.payloadJson)) return RejectServerCommand("payload_too_large", senderSession, payload);

        if (!string.IsNullOrWhiteSpace(payload.roomId) &&
            !string.Equals(payload.roomId.Trim(), SafeTrim(senderSession.roomId), StringComparison.Ordinal))
        {
            return RejectServerCommand("room_mismatch", senderSession, payload);
        }

        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
        if (spawnManager == null) return RejectServerCommand("spawn_manager_missing", senderSession, payload);

        if (!spawnManager.TryGetSpawnedObject(payload.netId, out identity) || identity == null)
        {
            return RejectServerCommand("net_id_not_found", senderSession, payload);
        }

        if (rejectPrefabMismatch &&
            !string.IsNullOrWhiteSpace(payload.prefabId) &&
            !string.IsNullOrWhiteSpace(identity.PrefabId) &&
            !string.Equals(payload.prefabId.Trim(), identity.PrefabId, StringComparison.Ordinal))
        {
            return RejectServerCommand("prefab_mismatch", senderSession, payload, identity);
        }

        if (!IsCommandAuthorityAllowed(identity, senderSession))
        {
            return RejectServerCommand("authority_rejected", senderSession, payload, identity);
        }

        return true;
    }

    private bool IsCommandAuthorityAllowed(MetaverseNetworkIdentity identity, DedicatedPlayerSession senderSession)
    {
        if (identity == null || senderSession == null) return false;
        if (!requireAuthorityForClientCommands) return true;
        if (IsOwnedBySession(identity, senderSession)) return true;
        if (identity.IsServerOwned && allowServerOwnedCommandsWithoutOwner) return true;
        return false;
    }

    private bool IsOwnedBySession(MetaverseNetworkIdentity identity, DedicatedPlayerSession senderSession)
    {
        if (identity == null || senderSession == null) return false;

        string connectionId = SafeTrim(senderSession.connectionId);
        string userId = SafeTrim(senderSession.userId);
        string playerId = SafeTrim(senderSession.playerId);

        if (!string.IsNullOrWhiteSpace(connectionId))
        {
            if (identity.IsOwnedBy(connectionId)) return true;
            if (int.TryParse(connectionId, out int numericConnectionId) && identity.IsOwnedBy(numericConnectionId)) return true;
        }

        if (!string.IsNullOrWhiteSpace(userId) && identity.IsOwnedByUser(userId)) return true;

        if (!string.IsNullOrWhiteSpace(playerId) &&
            string.Equals(SafeTrim(identity.OwnerPlayerId), playerId, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private string ResolveAuthorityMode(MetaverseNetworkIdentity identity, DedicatedPlayerSession senderSession)
    {
        if (identity == null || senderSession == null) return "unknown";
        if (IsOwnedBySession(identity, senderSession)) return "owner";
        if (identity.IsServerOwned && allowServerOwnedCommandsWithoutOwner) return "server_owned_requires_authority_false";
        if (!requireAuthorityForClientCommands) return "requires_authority_false";
        return "rejected";
    }

    private bool RejectServerCommand(string reason, DedicatedPlayerSession senderSession, MetaverseNetworkRpcPayload payload, MetaverseNetworkIdentity identity = null)
    {
        LastServerCommandRejectReason = SafeTrim(reason);

        if (logRejectedCommands)
        {
            Debug.LogWarning("[MetaverseNetworkRpcBridge] Command rejected | reason=" + LastServerCommandRejectReason +
                             " | netId=" + (payload != null ? payload.netId : 0) +
                             " | prefabId=" + SafeTrim(payload != null ? payload.prefabId : string.Empty) +
                             " | identityPrefabId=" + SafeTrim(identity != null ? identity.PrefabId : string.Empty) +
                             " | command=" + SafeTrim(payload != null ? payload.methodName : string.Empty) +
                             " | senderConnectionId=" + SafeTrim(senderSession != null ? senderSession.connectionId : string.Empty) +
                             " | senderUserId=" + SafeTrim(senderSession != null ? senderSession.userId : string.Empty) +
                             " | senderPlayerId=" + SafeTrim(senderSession != null ? senderSession.playerId : string.Empty));
        }

        return false;
    }

    private void DispatchServerCommand(MetaverseNetworkIdentity identity, MetaverseNetworkRpcPayload payload, DedicatedPlayerSession senderSession)
    {
        if (identity == null || payload == null) return;

        MetaverseNetworkBehaviour[] behaviours = identity.GetNetworkBehaviours();
        if (behaviours == null) return;

        for (int i = 0; i < behaviours.Length; i++)
        {
            MetaverseNetworkBehaviour behaviour = behaviours[i];
            if (behaviour == null) continue;

            try
            {
                behaviour.OnCommand(payload.methodName, payload.payloadJson, senderSession);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, behaviour);
            }
        }
    }

    private void DispatchClientRpc(MetaverseNetworkIdentity identity, MetaverseNetworkRpcPayload payload)
    {
        if (identity == null || payload == null) return;

        MetaverseNetworkBehaviour[] behaviours = identity.GetNetworkBehaviours();
        if (behaviours == null) return;

        for (int i = 0; i < behaviours.Length; i++)
        {
            MetaverseNetworkBehaviour behaviour = behaviours[i];
            if (behaviour == null) continue;

            try
            {
                behaviour.OnClientRpc(payload.methodName, payload.payloadJson);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, behaviour);
            }
        }
    }

    private void DispatchTargetRpc(MetaverseNetworkIdentity identity, MetaverseNetworkRpcPayload payload)
    {
        if (identity == null || payload == null) return;

        MetaverseNetworkBehaviour[] behaviours = identity.GetNetworkBehaviours();
        if (behaviours == null) return;

        for (int i = 0; i < behaviours.Length; i++)
        {
            MetaverseNetworkBehaviour behaviour = behaviours[i];
            if (behaviour == null) continue;

            try
            {
                behaviour.OnTargetRpc(payload.methodName, payload.payloadJson);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, behaviour);
            }
        }
    }

    private void HandleDedicatedClientRawMessage(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return;

        if (MetaverseNetworkRpcMessageCodec.TryReadClientRpcPayload(rawJson, out MetaverseNetworkRpcPayload clientRpcPayload))
        {
            if (TryGetClientIdentity(clientRpcPayload.netId, out MetaverseNetworkIdentity identity))
            {
                DispatchClientRpc(identity, clientRpcPayload);
                if (logMessages)
                {
                    Debug.Log("[MetaverseNetworkRpcBridge] ClientRpc dispatched on client | netId=" + clientRpcPayload.netId +
                              " | rpc=" + SafeTrim(clientRpcPayload.methodName) +
                              " | incomingRoute=game/client_rpc");
                }
            }
            return;
        }

        if (MetaverseNetworkRpcMessageCodec.TryReadTargetRpcPayload(rawJson, out MetaverseNetworkRpcPayload targetRpcPayload))
        {
            if (TryGetClientIdentity(targetRpcPayload.netId, out MetaverseNetworkIdentity identity))
            {
                DispatchTargetRpc(identity, targetRpcPayload);
                if (logMessages)
                {
                    Debug.Log("[MetaverseNetworkRpcBridge] TargetRpc dispatched on client | netId=" + targetRpcPayload.netId +
                              " | rpc=" + SafeTrim(targetRpcPayload.methodName) +
                              " | incomingRoute=game/target_rpc");
                }
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

    private MetaverseNetworkRpcPayload BuildBasePayload(string type, int netId, string prefabId, string methodName, string payloadJson)
    {
        return new MetaverseNetworkRpcPayload
        {
            type = SafeTrim(type),
            netId = Mathf.Max(0, netId),
            prefabId = SafeTrim(prefabId),
            behaviourName = string.Empty,
            methodName = SafeTrim(methodName),
            payloadJson = SafeJson(payloadJson),
            roomId = string.Empty
        };
    }

    private bool CanClientSendNow()
    {
        return dedicatedClient != null && dedicatedClient.IsConnected && dedicatedClient.IsAuthenticated;
    }

    private bool CanServerSendRpc(int netId, string methodName)
    {
        if (requireServerForOutboundRpc && !Application.isBatchMode) return false;
        if (netId <= 0) return false;
        return IsValidMethodName(methodName, maxRpcNameLength);
    }

    private bool IsValidMethodName(string methodName, int maxLength)
    {
        string safeMethodName = SafeTrim(methodName);
        if (string.IsNullOrWhiteSpace(safeMethodName)) return false;
        return safeMethodName.Length <= Mathf.Max(1, maxLength);
    }

    private bool IsValidPayloadLength(string payloadJson)
    {
        string safePayload = SafeJson(payloadJson);
        return safePayload.Length <= Mathf.Max(256, maxPayloadLength);
    }

    private void QueueCommand(int netId, string prefabId, string commandName, string payloadJson)
    {
        float expiresAt = Time.realtimeSinceStartup + Mathf.Max(1f, queuedCommandLifetimeSeconds);
        list_queuedCommands.Add(new QueuedCommand
        {
            netId = netId,
            prefabId = SafeTrim(prefabId),
            commandName = SafeTrim(commandName),
            payloadJson = SafeJson(payloadJson),
            expiresAt = expiresAt
        });

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkRpcBridge] Command queued until client auth is ready | netId=" + netId +
                      " | command=" + SafeTrim(commandName));
        }
    }

    private void FlushQueuedCommands()
    {
        if (list_queuedCommands.Count <= 0) return;

        for (int i = list_queuedCommands.Count - 1; i >= 0; i--)
        {
            QueuedCommand queued = list_queuedCommands[i];

            if (Time.realtimeSinceStartup > queued.expiresAt)
            {
                list_queuedCommands.RemoveAt(i);
                if (logMessages)
                {
                    Debug.LogWarning("[MetaverseNetworkRpcBridge] Queued command expired | netId=" + queued.netId +
                                     " | command=" + queued.commandName);
                }
                continue;
            }

            if (!CanClientSendNow()) continue;

            list_queuedCommands.RemoveAt(i);
            SendCommand(queued.netId, queued.prefabId, queued.commandName, queued.payloadJson);
        }
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

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkRpcBridge] Bound to dedicated client raw messages.");
        }
    }

    private void UnbindClientEvents()
    {
        if (dedicatedClient != null)
        {
            dedicatedClient.RawMessageReceived -= HandleDedicatedClientRawMessage;
        }

        clientEventsBound = false;
    }

    private long NowUnixMs()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private string SafeJson(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "{}" : value.Trim();
    }

    private string SafeTrim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private struct QueuedCommand
    {
        public int netId;
        public string prefabId;
        public string commandName;
        public string payloadJson;
        public float expiresAt;
    }
}
