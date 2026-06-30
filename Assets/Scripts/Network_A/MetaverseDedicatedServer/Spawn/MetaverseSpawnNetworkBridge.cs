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

    [Header("Client Inbound")]
    [SerializeField] private bool autoBindDedicatedClient = true;
    [SerializeField] private bool applyIncomingMessages = true;
    [SerializeField] private bool clearBeforeSnapshot;

    [Header("Debug")]
    [SerializeField] private bool logMessages = true;

    private bool spawnManagerBound;
    private bool dedicatedClientBound;
    private DedicatedGameServerWsClient boundDedicatedClient;

    public event Action<string> OutboundMessageReady;

    private void Awake()
    {
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
        if (logMessages) Debug.Log("[MetaverseSpawnNetworkBridge] Bound to spawn manager.");
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

        if (logMessages)
        {
            Debug.Log("[MetaverseSpawnNetworkBridge] Bound to dedicated client raw messages.");
        }
    }

    public void UnbindDedicatedClient()
    {
        if (boundDedicatedClient != null)
        {
            boundDedicatedClient.RawMessageReceived -= HandleDedicatedClientRawMessage;
        }

        boundDedicatedClient = null;
        dedicatedClientBound = false;
    }

    public bool HandleIncomingRawMessage(string rawJson)
    {
        if (!applyIncomingMessages) return false;
        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
        if (spawnManager == null) return false;

        if (!MetaverseSpawnMessageCodec.TryReadMessage(
                rawJson,
                out string messageType,
                out MetaverseSpawnPayload spawnPayload,
                out MetaverseDespawnPayload despawnPayload,
                out MetaverseSpawnPayload[] snapshotPayloads))
        {
            return false;
        }

        if (string.Equals(messageType, RealtimeMessageTypes.Spawn, StringComparison.Ordinal) ||
            string.Equals(messageType, MetaverseDedicatedMessageTypes.Spawn, StringComparison.Ordinal))
        {
            bool applied = spawnManager.ClientApplySpawn(spawnPayload);
            if (logMessages && applied)
            {
                Debug.Log("[MetaverseSpawnNetworkBridge] Incoming spawn applied | netId=" +
                          spawnPayload.netId + " | prefabId=" + spawnPayload.prefabId +
                          " | messageFormat=" + MetaverseSpawnMessageCodec.ReadMessageFormat(rawJson) +
                          " | route=" + MetaverseSpawnMessageCodec.ReadRouteForLog(rawJson));
            }
            return applied;
        }

        if (string.Equals(messageType, RealtimeMessageTypes.Despawn, StringComparison.Ordinal) ||
            string.Equals(messageType, MetaverseDedicatedMessageTypes.Despawn, StringComparison.Ordinal))
        {
            int netId = despawnPayload != null ? despawnPayload.netId : 0;
            string reason = despawnPayload != null ? despawnPayload.reason : string.Empty;
            bool applied = spawnManager.ClientApplyDespawn(netId, reason);
            if (logMessages && applied)
            {
                Debug.Log("[MetaverseSpawnNetworkBridge] Incoming despawn applied | netId=" +
                          netId + " | reason=" + reason +
                          " | messageFormat=" + MetaverseSpawnMessageCodec.ReadMessageFormat(rawJson) +
                          " | route=" + MetaverseSpawnMessageCodec.ReadRouteForLog(rawJson));
            }
            return applied;
        }

        if (string.Equals(messageType, RealtimeMessageTypes.Snapshot, StringComparison.Ordinal) ||
            string.Equals(messageType, MetaverseDedicatedMessageTypes.LegacySpawnSnapshot, StringComparison.Ordinal))
        {
            return ApplyIncomingSnapshot(snapshotPayloads);
        }

        return false;
    }

    public void EmitSpawnSnapshotTo(Action<string> send)
    {
        EmitSpawnSnapshotTo(send, string.Empty);
    }

    public void EmitSpawnSnapshotTo(Action<string> send, string roomId)
    {
        if (send == null || spawnManager == null) return;
        MetaverseSpawnPayload[] payloads = spawnManager.BuildSnapshotPayloads();
        string json = MetaverseSpawnMessageCodec.CreateSpawnSnapshotEnvelopeJson(payloads, roomId);
        if (string.IsNullOrWhiteSpace(json)) return;
        send.Invoke(json);
        if (logMessages) Debug.Log("[MetaverseSpawnNetworkBridge] Spawn snapshot emitted | count=" + payloads.Length + " | route=game/snapshot");
    }

    public void EmitExistingSpawnsTo(Action<string> send)
    {
        EmitExistingSpawnsTo(send, string.Empty);
    }

    public void EmitExistingSpawnsTo(Action<string> send, string roomId)
    {
        if (send == null || spawnManager == null) return;
        List<MetaverseNetworkIdentity> identities = spawnManager.GetSpawnedObjects();
        for (int i = 0; i < identities.Count; i++)
        {
            MetaverseSpawnPayload payload = spawnManager.BuildPayload(identities[i]);
            if (payload == null) continue;
            string json = MetaverseSpawnMessageCodec.CreateSpawnEnvelopeJson(payload, roomId);
            if (!string.IsNullOrWhiteSpace(json)) send.Invoke(json);
        }
        if (logMessages) Debug.Log("[MetaverseSpawnNetworkBridge] Existing spawns emitted | count=" + identities.Count + " | route=game/spawn");
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

    private bool ApplyIncomingSnapshot(MetaverseSpawnPayload[] payloads)
    {
        if (spawnManager == null) return false;
        if (clearBeforeSnapshot) spawnManager.ClearAllSpawned("spawn_snapshot_refresh");
        if (payloads == null) return true;

        int appliedCount = 0;
        for (int i = 0; i < payloads.Length; i++)
        {
            if (spawnManager.ClientApplySpawn(payloads[i])) appliedCount++;
        }

        if (logMessages) Debug.Log("[MetaverseSpawnNetworkBridge] Incoming spawn snapshot applied | count=" + appliedCount + " | route=game/snapshot");
        return true;
    }

    private void OnServerObjectSpawned(MetaverseNetworkIdentity identity, MetaverseSpawnPayload payload)
    {
        if (!emitServerSpawnMessages || payload == null) return;
        string json = MetaverseSpawnMessageCodec.CreateSpawnEnvelopeJson(payload);
        if (string.IsNullOrWhiteSpace(json)) return;
        OutboundMessageReady?.Invoke(json);
        if (logMessages) Debug.Log("[MetaverseSpawnNetworkBridge] Outbound spawn ready | netId=" + payload.netId + " | prefabId=" + payload.prefabId + " | route=game/spawn");
    }

    private void OnServerObjectDespawned(MetaverseNetworkIdentity identity, string reason)
    {
        if (!emitServerDespawnMessages || identity == null) return;
        string json = MetaverseSpawnMessageCodec.CreateDespawnEnvelopeJson(identity.NetId, reason);
        if (string.IsNullOrWhiteSpace(json)) return;
        OutboundMessageReady?.Invoke(json);
        if (logMessages) Debug.Log("[MetaverseSpawnNetworkBridge] Outbound despawn ready | netId=" + identity.NetId + " | reason=" + reason + " | route=game/despawn");
    }
}
