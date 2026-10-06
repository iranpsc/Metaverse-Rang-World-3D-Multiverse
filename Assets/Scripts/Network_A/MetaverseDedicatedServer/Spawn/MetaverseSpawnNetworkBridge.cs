using System;
using System.Collections.Generic;
using Network_A.DedicatedGameServer.Client;
using Network_A.Realtime.Protocol;
using UnityEngine;

public class MetaverseSpawnNetworkBridge : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MetaverseSpawnManager spawnManager;
    [SerializeField] private DedicatedGameServerWsClient dedicatedClient;

    [Header("Server Outbound")]
    [SerializeField] private bool emitServerSpawnMessages = true;
    [SerializeField] private bool emitServerDespawnMessages = true;
    [SerializeField] private bool includeRoomIdInOutboundMessages = true;

    [Header("Client Inbound")]
    [SerializeField] private bool autoBindDedicatedClient = true;
    [SerializeField] private bool applyIncomingMessages = true;
    [SerializeField] private bool clearBeforeSnapshot;
    [SerializeField] private bool ignoreDuplicateIncomingSpawns = true;

    [Header("Debug")]
    [SerializeField] private bool logMessages = true;

    private bool spawnManagerBound;
    private bool dedicatedClientBound;
    private DedicatedGameServerWsClient boundDedicatedClient;

    public static MetaverseSpawnNetworkBridge Instance { get; private set; }
    public bool IsSpawnManagerBound => spawnManagerBound && spawnManager != null;
    public bool IsDedicatedClientBound => dedicatedClientBound && boundDedicatedClient != null;
    public string LastIncomingRejectReason { get; private set; } = string.Empty;
    public string LastOutboundRejectReason { get; private set; } = string.Empty;

    public event Action<string> OutboundMessageReady;

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else if (Instance != this) Debug.LogWarning("[MetaverseSpawnNetworkBridge] More than one instance exists in scene.");

        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
        if (spawnManager != null) Bind(spawnManager);
        TryBindDedicatedClient();
    }

    private void OnEnable()
    {
        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
        if (spawnManager != null) Bind(spawnManager);
        TryBindDedicatedClient();
    }

    private void Update()
    {
        if (!autoBindDedicatedClient) return;
        if (dedicatedClientBound && boundDedicatedClient != null) return;
        TryBindDedicatedClient();
    }

    private void OnDisable()
    {
        UnbindDedicatedClient();
    }

    private void OnDestroy()
    {
        UnbindDedicatedClient();
        Unbind();
        if (Instance == this) Instance = null;
    }

    public void Bind(MetaverseSpawnManager manager)
    {
        if (spawnManager == manager && spawnManagerBound) return;
        Unbind();
        spawnManager = manager;
        if (spawnManager == null) return;
        spawnManager.ServerObjectSpawned += OnServerObjectSpawned;
        spawnManager.ServerObjectDespawned += OnServerObjectDespawned;
        spawnManagerBound = true;
        if (logMessages) Debug.Log("[MetaverseSpawnNetworkBridge] Bound to spawn manager | phase=33A | mirrorRoutes=Spawn/Despawn/Destroy/Snapshot");
    }

    public void Unbind()
    {
        if (!spawnManagerBound || spawnManager == null) return;
        spawnManager.ServerObjectSpawned -= OnServerObjectSpawned;
        spawnManager.ServerObjectDespawned -= OnServerObjectDespawned;
        spawnManagerBound = false;
    }

    public void BindDedicatedClient(DedicatedGameServerWsClient client)
    {
        if (!autoBindDedicatedClient && client == null) return;
        if (dedicatedClientBound && boundDedicatedClient == client) return;

        UnbindDedicatedClient();

        dedicatedClient = client;
        if (dedicatedClient == null) return;

        dedicatedClient.RawMessageReceived -= HandleDedicatedClientRawMessage;
        dedicatedClient.RawMessageReceived += HandleDedicatedClientRawMessage;

        boundDedicatedClient = dedicatedClient;
        dedicatedClientBound = true;

        if (logMessages) Debug.Log("[MetaverseSpawnNetworkBridge] Bound to dedicated client raw messages.");
    }

    public void UnbindDedicatedClient()
    {
        if (boundDedicatedClient != null) boundDedicatedClient.RawMessageReceived -= HandleDedicatedClientRawMessage;
        boundDedicatedClient = null;
        dedicatedClientBound = false;
    }

    public bool HandleIncomingRawMessage(string rawJson)
    {
        LastIncomingRejectReason = string.Empty;
        if (!applyIncomingMessages) return RejectIncoming("incoming_apply_disabled");
        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
        if (spawnManager == null) return RejectIncoming("spawn_manager_missing");

        if (!MetaverseSpawnMessageCodec.TryReadMessage(
                rawJson,
                out string messageType,
                out MetaverseSpawnPayload spawnPayload,
                out MetaverseDespawnPayload despawnPayload,
                out MetaverseSpawnPayload[] snapshotPayloads))
        {
            return false;
        }

        if (MetaverseSpawnMessageCodec.IsSpawnMessage(messageType)) return ApplyIncomingSpawn(spawnPayload, rawJson);
        if (MetaverseSpawnMessageCodec.IsDespawnMessage(messageType)) return ApplyIncomingDespawn(despawnPayload, rawJson);
        if (MetaverseSpawnMessageCodec.IsSnapshotMessage(messageType)) return ApplyIncomingSnapshot(snapshotPayloads, rawJson);
        return false;
    }

    public void EmitSpawnSnapshotTo(Action<string> send)
    {
        EmitSpawnSnapshotTo(send, string.Empty);
    }

    public void EmitSpawnSnapshotTo(Action<string> send, string roomId)
    {
        LastOutboundRejectReason = string.Empty;
        if (send == null)
        {
            RejectOutbound("send_callback_missing");
            return;
        }
        if (spawnManager == null)
        {
            RejectOutbound("spawn_manager_missing");
            return;
        }

        MetaverseSpawnPayload[] payloads = spawnManager.BuildSnapshotPayloads(ResolveRoomId(roomId));
        string json = MetaverseSpawnMessageCodec.CreateSpawnSnapshotEnvelopeJson(payloads, ResolveRoomId(roomId));
        if (string.IsNullOrWhiteSpace(json))
        {
            RejectOutbound("snapshot_json_empty");
            return;
        }
        send.Invoke(json);
        if (logMessages) Debug.Log("[MetaverseSpawnNetworkBridge] Spawn snapshot emitted | count=" + payloads.Length + " | route=game/snapshot | mirrorRoute=NetworkServer.SpawnSnapshot");
    }

    public void EmitExistingSpawnsTo(Action<string> send)
    {
        EmitExistingSpawnsTo(send, string.Empty);
    }

    public void EmitExistingSpawnsTo(Action<string> send, string roomId)
    {
        LastOutboundRejectReason = string.Empty;
        if (send == null)
        {
            RejectOutbound("send_callback_missing");
            return;
        }
        if (spawnManager == null)
        {
            RejectOutbound("spawn_manager_missing");
            return;
        }

        List<MetaverseNetworkIdentity> identities = spawnManager.GetSpawnedObjects(ResolveRoomId(roomId));
        int sentCount = 0;
        for (int i = 0; i < identities.Count; i++)
        {
            MetaverseSpawnPayload payload = spawnManager.BuildPayload(identities[i], "emit_existing_spawns", MetaverseSpawnMessageCodec.MirrorSpawnRoute);
            if (payload == null) continue;
            string json = MetaverseSpawnMessageCodec.CreateSpawnEnvelopeJson(payload, ResolveRoomId(!string.IsNullOrWhiteSpace(roomId) ? roomId : (identities[i] != null ? identities[i].RoomId : string.Empty)));
            if (!string.IsNullOrWhiteSpace(json))
            {
                send.Invoke(json);
                sentCount++;
            }
        }
        if (logMessages) Debug.Log("[MetaverseSpawnNetworkBridge] Existing spawns emitted | count=" + sentCount + " | route=game/spawn | mirrorRoute=NetworkServer.Spawn");
    }

    public bool EmitSpawn(MetaverseNetworkIdentity identity, string roomId = "")
    {
        if (identity == null || spawnManager == null) return false;
        MetaverseSpawnPayload payload = spawnManager.BuildPayload(identity, "manual_emit_spawn", MetaverseSpawnMessageCodec.MirrorSpawnRoute);
        string json = MetaverseSpawnMessageCodec.CreateSpawnEnvelopeJson(payload, ResolveRoomId(!string.IsNullOrWhiteSpace(roomId) ? roomId : identity.RoomId));
        if (string.IsNullOrWhiteSpace(json)) return false;
        OutboundMessageReady?.Invoke(json);
        return true;
    }

    public bool EmitDespawn(int netId, string reason, string roomId = "")
    {
        string json = MetaverseSpawnMessageCodec.CreateDespawnEnvelopeJson(netId, reason, ResolveRoomId(roomId));
        if (string.IsNullOrWhiteSpace(json)) return false;
        OutboundMessageReady?.Invoke(json);
        return true;
    }

    public string GetSpawnBridgeDebugSummary()
    {
        return "spawnManagerBound=" + IsSpawnManagerBound +
               " | dedicatedClientBound=" + IsDedicatedClientBound +
               " | applyIncoming=" + applyIncomingMessages +
               " | emitSpawn=" + emitServerSpawnMessages +
               " | emitDespawn=" + emitServerDespawnMessages +
               " | lastIncomingReject=" + LastIncomingRejectReason +
               " | lastOutboundReject=" + LastOutboundRejectReason;
    }

    private bool ApplyIncomingSpawn(MetaverseSpawnPayload spawnPayload, string rawJson)
    {
        if (spawnPayload == null) return RejectIncoming("spawn_payload_missing");
        if (ignoreDuplicateIncomingSpawns && spawnManager.ContainsNetId(spawnPayload.netId)) return true;

        bool applied = spawnManager.ClientApplySpawn(spawnPayload);
        if (logMessages && applied)
        {
            Debug.Log("[MetaverseSpawnNetworkBridge] Incoming spawn applied | netId=" + spawnPayload.netId +
                      " | prefabId=" + spawnPayload.prefabId +
                      " | messageFormat=" + MetaverseSpawnMessageCodec.ReadMessageFormat(rawJson) +
                      " | route=" + MetaverseSpawnMessageCodec.ReadRouteForLog(rawJson) +
                      " | mirrorRoute=" + MetaverseSpawnMessageCodec.ReadMirrorLikeRouteForLog(rawJson));
        }
        return applied;
    }

    private bool ApplyIncomingDespawn(MetaverseDespawnPayload despawnPayload, string rawJson)
    {
        int netId = despawnPayload != null ? despawnPayload.netId : 0;
        string reason = despawnPayload != null ? despawnPayload.reason : string.Empty;
        if (netId <= 0) return RejectIncoming("invalid_despawn_net_id");

        bool applied = spawnManager.ClientApplyDespawn(netId, reason);
        if (logMessages && applied)
        {
            Debug.Log("[MetaverseSpawnNetworkBridge] Incoming despawn applied | netId=" + netId +
                      " | reason=" + reason +
                      " | messageFormat=" + MetaverseSpawnMessageCodec.ReadMessageFormat(rawJson) +
                      " | route=" + MetaverseSpawnMessageCodec.ReadRouteForLog(rawJson) +
                      " | mirrorRoute=" + MetaverseSpawnMessageCodec.ReadMirrorLikeRouteForLog(rawJson));
        }
        return applied;
    }

    private bool ApplyIncomingSnapshot(MetaverseSpawnPayload[] payloads, string rawJson)
    {
        if (spawnManager == null) return RejectIncoming("spawn_manager_missing");
        if (clearBeforeSnapshot) spawnManager.ClearAllSpawned("spawn_snapshot_refresh");
        if (payloads == null) return true;

        int appliedCount = 0;
        for (int i = 0; i < payloads.Length; i++)
        {
            if (spawnManager.ClientApplySpawn(payloads[i])) appliedCount++;
        }

        if (logMessages) Debug.Log("[MetaverseSpawnNetworkBridge] Incoming spawn snapshot applied | count=" + appliedCount + " | route=" + MetaverseSpawnMessageCodec.ReadRouteForLog(rawJson) + " | mirrorRoute=NetworkServer.SpawnSnapshot");
        return true;
    }

    private void TryBindDedicatedClient()
    {
        if (!autoBindDedicatedClient) return;
        if (dedicatedClient == null) dedicatedClient = DedicatedGameServerWsClient.Instance;
        if (dedicatedClient == null) return;
        BindDedicatedClient(dedicatedClient);
    }

    private void HandleDedicatedClientRawMessage(string rawJson)
    {
        HandleIncomingRawMessage(rawJson);
    }

    private void OnServerObjectSpawned(MetaverseNetworkIdentity identity, MetaverseSpawnPayload payload)
    {
        LastOutboundRejectReason = string.Empty;
        if (!emitServerSpawnMessages || payload == null) return;
        string roomId = includeRoomIdInOutboundMessages ? payload.roomId : string.Empty;
        string json = MetaverseSpawnMessageCodec.CreateSpawnEnvelopeJson(payload, ResolveRoomId(!string.IsNullOrWhiteSpace(roomId) ? roomId : (identity != null ? identity.RoomId : string.Empty)));
        if (string.IsNullOrWhiteSpace(json))
        {
            RejectOutbound("spawn_json_empty");
            return;
        }
        OutboundMessageReady?.Invoke(json);
        if (logMessages) Debug.Log("[MetaverseSpawnNetworkBridge] Outbound spawn ready | netId=" + payload.netId + " | prefabId=" + payload.prefabId + " | route=game/spawn | mirrorRoute=" + payload.mirrorRoute);
    }

    private void OnServerObjectDespawned(MetaverseNetworkIdentity identity, string reason)
    {
        LastOutboundRejectReason = string.Empty;
        if (!emitServerDespawnMessages || identity == null) return;
        string json = MetaverseSpawnMessageCodec.CreateDespawnEnvelopeJson(identity.NetId, reason, includeRoomIdInOutboundMessages ? (!string.IsNullOrWhiteSpace(identity.RoomId) ? identity.RoomId : MetaverseNetworkClient.roomId) : string.Empty);
        if (string.IsNullOrWhiteSpace(json))
        {
            RejectOutbound("despawn_json_empty");
            return;
        }
        OutboundMessageReady?.Invoke(json);
        if (logMessages) Debug.Log("[MetaverseSpawnNetworkBridge] Outbound despawn ready | netId=" + identity.NetId + " | reason=" + reason + " | route=game/despawn | mirrorRoute=NetworkServer.Despawn");
    }

    private string ResolveRoomId(string roomId)
    {
        if (!string.IsNullOrWhiteSpace(roomId)) return roomId.Trim();
        return MetaverseNetworkClient.roomId;
    }

    private bool RejectIncoming(string reason)
    {
        LastIncomingRejectReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
        if (logMessages) Debug.LogWarning("[MetaverseSpawnNetworkBridge] Incoming spawn message rejected | reason=" + LastIncomingRejectReason);
        return false;
    }

    private void RejectOutbound(string reason)
    {
        LastOutboundRejectReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
        if (logMessages) Debug.LogWarning("[MetaverseSpawnNetworkBridge] Outbound spawn message rejected | reason=" + LastOutboundRejectReason);
    }
}
