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

    [Header("Debug")]
    [SerializeField] private bool logMessages = true;

    private bool clientEventsBound;
    private long nextClientCommandSequence = 1;
    private readonly List<QueuedCommand> list_queuedCommands = new List<QueuedCommand>();

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

    public bool SendCommand(int netId, string prefabId, string commandName, string payloadJson = "")
    {
        string safeCommandName = SafeTrim(commandName);
        if (netId <= 0 || string.IsNullOrWhiteSpace(safeCommandName)) return false;

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

    public bool SendClientRpc(MetaverseNetworkIdentity identity, string rpcName, string payloadJson = "")
    {
        if (identity == null) return false;
        return SendClientRpc(identity.NetId, identity.PrefabId, rpcName, payloadJson);
    }

    public bool SendClientRpc(int netId, string prefabId, string rpcName, string payloadJson = "")
    {
        string safeRpcName = SafeTrim(rpcName);
        if (netId <= 0 || string.IsNullOrWhiteSpace(safeRpcName)) return false;

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

    public bool SendTargetRpc(MetaverseNetworkIdentity identity, string targetConnectionId, string rpcName, string payloadJson = "")
    {
        if (identity == null) return false;
        return SendTargetRpc(identity.NetId, identity.PrefabId, targetConnectionId, rpcName, payloadJson);
    }

    public bool SendTargetRpc(int netId, string prefabId, string targetConnectionId, string rpcName, string payloadJson = "")
    {
        string safeRpcName = SafeTrim(rpcName);
        string safeTargetConnectionId = SafeTrim(targetConnectionId);
        if (netId <= 0 || string.IsNullOrWhiteSpace(safeRpcName) || string.IsNullOrWhiteSpace(safeTargetConnectionId)) return false;

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

    public bool HandleServerCommand(DedicatedPlayerSession senderSession, MetaverseNetworkRpcPayload payload)
    {
        EnsureReferences();
        if (senderSession == null || payload == null) return false;
        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
        if (spawnManager == null) return false;

        payload.type = RealtimeMessageTypes.Command;
        payload.senderConnectionId = senderSession.connectionId;
        payload.senderUserId = senderSession.userId;
        payload.senderPlayerId = senderSession.playerId;
        payload.roomId = senderSession.roomId;
        payload.serverTimeUnixMs = NowUnixMs();

        if (!spawnManager.TryGetSpawnedObject(payload.netId, out MetaverseNetworkIdentity identity) || identity == null)
        {
            Debug.LogWarning("[MetaverseNetworkRpcBridge] Command ignored. NetId not found | netId=" + payload.netId +
                             " | command=" + SafeTrim(payload.methodName));
            return false;
        }

        DispatchServerCommand(identity, payload, senderSession);

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkRpcBridge] Command dispatched on server | netId=" + payload.netId +
                      " | command=" + SafeTrim(payload.methodName) +
                      " | senderUserId=" + SafeTrim(senderSession.userId) +
                      " | incomingRoute=game/command");
        }

        return true;
    }

    private void DispatchServerCommand(MetaverseNetworkIdentity identity, MetaverseNetworkRpcPayload payload, DedicatedPlayerSession senderSession)
    {
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
            payloadJson = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson.Trim(),
            roomId = string.Empty
        };
    }

    private bool CanClientSendNow()
    {
        return dedicatedClient != null && dedicatedClient.IsConnected && dedicatedClient.IsAuthenticated;
    }

    private void QueueCommand(int netId, string prefabId, string commandName, string payloadJson)
    {
        float expiresAt = Time.realtimeSinceStartup + Mathf.Max(1f, queuedCommandLifetimeSeconds);
        list_queuedCommands.Add(new QueuedCommand
        {
            netId = netId,
            prefabId = SafeTrim(prefabId),
            commandName = SafeTrim(commandName),
            payloadJson = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson.Trim(),
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
