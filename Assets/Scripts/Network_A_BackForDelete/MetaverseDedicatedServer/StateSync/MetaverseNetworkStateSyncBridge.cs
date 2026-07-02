using System;
using System.Collections.Generic;
using Network_A.DedicatedGameServer.Client;
using Network_A.Realtime.Protocol;
using UnityEngine;

public class MetaverseNetworkStateSyncBridge : MonoBehaviour
{
    public static MetaverseNetworkStateSyncBridge Instance { get; private set; }

    [Header("References")]
    [SerializeField] private MetaverseSpawnManager spawnManager;
    [SerializeField] private DedicatedGameServerWsClient dedicatedClient;

    [Header("Rules")]
    [SerializeField] private bool rejectClientOutboundStateSync = true;
    [SerializeField] private bool ignoreOlderSyncVarVersions = true;
    [SerializeField] private bool ignoreOlderTransformSequences = true;
    [SerializeField] private bool applyTransformScale = true;
    [SerializeField] private int maxSyncKeyLength = 96;
    [SerializeField] private int maxValueJsonLength = 8192;

    [Header("Editor")]
    [SerializeField] private bool allowEditorServerSimulation = true;

    [Header("Debug")]
    [SerializeField] private bool logMessages = true;
    [SerializeField] private bool logRejectedMessages = true;

    private bool clientEventsBound;
    private long nextTransformSequence = 1;
    private string lastStateSyncRejectReason = string.Empty;
    private readonly Dictionary<string, long> dict_versions = new Dictionary<string, long>();
    private readonly Dictionary<string, string> dict_values = new Dictionary<string, string>();
    private readonly Dictionary<int, long> dict_transformSequences = new Dictionary<int, long>();

    public event Action<string> OutboundMessageReady;

    public string LastStateSyncRejectReason => lastStateSyncRejectReason;
    public long NextTransformSequence => nextTransformSequence;
    public int CachedSyncVarCount => dict_values.Count;
    public int CachedTransformSequenceCount => dict_transformSequences.Count;
    public bool IsServerWriteAllowed => CanWriteServerState();

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

    public bool SetSyncVar(MetaverseNetworkIdentity identity, string syncKey, string valueJson = "")
    {
        if (identity == null)
        {
            Reject("identity_missing");
            return false;
        }

        return SetSyncVar(identity.NetId, identity.PrefabId, syncKey, valueJson);
    }

    public bool SetSyncVar(GameObject obj, string syncKey, string valueJson = "")
    {
        return SetSyncVar(GetIdentity(obj), syncKey, valueJson);
    }


    public bool SetSyncVar(int netId, string prefabId, string syncKey, string valueJson = "")
    {
        string safeKey = SafeTrim(syncKey);
        string safeValueJson = SafeJson(valueJson);

        if (!CanSetSyncVar(netId, safeKey, safeValueJson)) return false;

        string dictKey = BuildSyncVarCacheKey(netId, safeKey);
        dict_values.TryGetValue(dictKey, out string oldValueJson);
        dict_values[dictKey] = safeValueJson;

        long version = 1;
        if (dict_versions.TryGetValue(dictKey, out long currentVersion)) version = currentVersion + 1;
        dict_versions[dictKey] = version;

        MetaverseNetworkSyncVarPayload payload = new MetaverseNetworkSyncVarPayload
        {
            type = RealtimeMessageTypes.SyncVar,
            netId = netId,
            prefabId = SafeTrim(prefabId),
            syncKey = safeKey,
            oldValueJson = string.IsNullOrWhiteSpace(oldValueJson) ? "{}" : oldValueJson,
            valueJson = safeValueJson,
            version = version,
            serverTimeUnixMs = NowUnixMs()
        };

        string json = MetaverseNetworkStateSyncMessageCodec.CreateSyncVarEnvelopeJson(payload);
        if (string.IsNullOrWhiteSpace(json))
        {
            Reject("sync_var_envelope_create_failed");
            return false;
        }

        ClearRejectReason();
        OutboundMessageReady?.Invoke(json);
        DispatchSyncVarOnServer(payload);

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkStateSyncBridge] SyncVar outbound ready | mirrorRoute=SyncVar | netId=" + netId +
                      " | key=" + safeKey +
                      " | version=" + version +
                      " | outgoingRoute=game/sync_var");
        }

        return true;
    }

    public bool SyncVar(MetaverseNetworkIdentity identity, string syncKey, string valueJson = "")
    {
        return SetSyncVar(identity, syncKey, valueJson);
    }

    public bool SyncVar(GameObject obj, string syncKey, string valueJson = "")
    {
        return SetSyncVar(obj, syncKey, valueJson);
    }


    public bool SendNetworkTransform(MetaverseNetworkIdentity identity)
    {
        if (!CanSendNetworkTransform(identity)) return false;

        Transform t = identity.transform;
        MetaverseNetworkTransformPayload payload = new MetaverseNetworkTransformPayload
        {
            type = RealtimeMessageTypes.NetworkTransform,
            netId = identity.NetId,
            prefabId = identity.PrefabId,
            position = t.position,
            rotation = t.rotation,
            scale = t.localScale,
            sequence = nextTransformSequence++,
            serverTimeUnixMs = NowUnixMs()
        };

        string json = MetaverseNetworkStateSyncMessageCodec.CreateNetworkTransformEnvelopeJson(payload);
        if (string.IsNullOrWhiteSpace(json))
        {
            Reject("network_transform_envelope_create_failed");
            return false;
        }

        dict_transformSequences[payload.netId] = payload.sequence;
        ClearRejectReason();
        OutboundMessageReady?.Invoke(json);
        DispatchNetworkTransformOnServer(identity, payload);

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkStateSyncBridge] NetworkTransform outbound ready | mirrorRoute=NetworkTransform | netId=" + payload.netId +
                      " | sequence=" + payload.sequence +
                      " | position=" + payload.position +
                      " | outgoingRoute=game/network_transform");
        }

        return true;
    }

    public bool SendNetworkTransform(GameObject obj)
    {
        return SendNetworkTransform(GetIdentity(obj));
    }

    public bool SendNetworkTransform(int netId)
    {
        if (!TryGetIdentity(netId, out MetaverseNetworkIdentity identity))
        {
            Reject("net_id_not_found");
            return false;
        }

        return SendNetworkTransform(identity);
    }

    public bool SyncNetworkTransform(MetaverseNetworkIdentity identity)
    {
        return SendNetworkTransform(identity);
    }

    public bool SyncNetworkTransform(GameObject obj)
    {
        return SendNetworkTransform(obj);
    }

    public bool SyncNetworkTransform(int netId)
    {
        return SendNetworkTransform(netId);
    }

    public bool SyncTransform(MetaverseNetworkIdentity identity)
    {
        return SendNetworkTransform(identity);
    }

    public bool SyncTransform(GameObject obj)
    {
        return SendNetworkTransform(obj);
    }

    public bool SyncTransform(int netId)
    {
        return SendNetworkTransform(netId);
    }

    public bool CanSetSyncVar(MetaverseNetworkIdentity identity, string syncKey, string valueJson = "")
    {
        if (identity == null)
        {
            Reject("identity_missing");
            return false;
        }

        return CanSetSyncVar(identity.NetId, SafeTrim(syncKey), SafeJson(valueJson));
    }

    public bool CanSetSyncVar(int netId, string syncKey, string valueJson = "")
    {
        if (!CanWriteServerState())
        {
            Reject("client_outbound_state_sync_rejected");
            return false;
        }

        if (netId <= 0)
        {
            Reject("invalid_net_id");
            return false;
        }

        string safeKey = SafeTrim(syncKey);
        if (string.IsNullOrWhiteSpace(safeKey))
        {
            Reject("invalid_sync_key");
            return false;
        }

        if (safeKey.Length > Mathf.Max(1, maxSyncKeyLength))
        {
            Reject("sync_key_too_long");
            return false;
        }

        string safeValueJson = SafeJson(valueJson);
        if (safeValueJson.Length > Mathf.Max(128, maxValueJsonLength))
        {
            Reject("sync_value_too_large");
            return false;
        }

        if (!TryGetIdentity(netId, out MetaverseNetworkIdentity identity) || identity == null)
        {
            Reject("net_id_not_found");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(identity.PrefabId) && !identity.IsSpawned)
        {
            Reject("identity_not_spawned");
            return false;
        }

        ClearRejectReason();
        return true;
    }

    public bool CanSendNetworkTransform(MetaverseNetworkIdentity identity)
    {
        if (!CanWriteServerState())
        {
            Reject("client_outbound_state_sync_rejected");
            return false;
        }

        if (identity == null)
        {
            Reject("identity_missing");
            return false;
        }

        if (identity.NetId <= 0)
        {
            Reject("invalid_net_id");
            return false;
        }

        if (!TryGetIdentity(identity.NetId, out MetaverseNetworkIdentity current) || current == null)
        {
            Reject("net_id_not_found");
            return false;
        }

        if (!ReferenceEquals(current, identity))
        {
            Reject("identity_instance_mismatch");
            return false;
        }

        ClearRejectReason();
        return true;
    }

    public bool CanSendNetworkTransform(GameObject obj)
    {
        return CanSendNetworkTransform(GetIdentity(obj));
    }

    public bool ApplyRawStateSyncMessage(string rawJson)
    {
        return HandleDedicatedClientRawMessage(rawJson);
    }

    public string GetSyncVarValue(int netId, string syncKey)
    {
        string dictKey = BuildSyncVarCacheKey(netId, SafeTrim(syncKey));
        return dict_values.TryGetValue(dictKey, out string value) ? value : string.Empty;
    }

    public long GetSyncVarVersion(int netId, string syncKey)
    {
        string dictKey = BuildSyncVarCacheKey(netId, SafeTrim(syncKey));
        return dict_versions.TryGetValue(dictKey, out long version) ? version : 0;
    }

    public long GetLastTransformSequence(int netId)
    {
        return dict_transformSequences.TryGetValue(netId, out long sequence) ? sequence : 0;
    }

    public void ClearStateCache()
    {
        dict_versions.Clear();
        dict_values.Clear();
        dict_transformSequences.Clear();
        nextTransformSequence = 1;
        ClearRejectReason();
    }

    private bool HandleDedicatedClientRawMessage(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return false;

        if (MetaverseNetworkStateSyncMessageCodec.TryReadSyncVarPayload(rawJson, out MetaverseNetworkSyncVarPayload syncPayload))
        {
            bool applied = TryApplyIncomingSyncVar(syncPayload);
            if (!applied && logRejectedMessages)
            {
                Debug.LogWarning("[MetaverseNetworkStateSyncBridge] SyncVar ignored on client | reason=" + LastStateSyncRejectReason +
                                 " | route=game/sync_var");
            }
            return applied;
        }

        if (MetaverseNetworkStateSyncMessageCodec.TryReadNetworkTransformPayload(rawJson, out MetaverseNetworkTransformPayload transformPayload))
        {
            bool applied = TryApplyIncomingNetworkTransform(transformPayload);
            if (!applied && logRejectedMessages)
            {
                Debug.LogWarning("[MetaverseNetworkStateSyncBridge] NetworkTransform ignored on client | reason=" + LastStateSyncRejectReason +
                                 " | route=game/network_transform");
            }
            return applied;
        }

        return false;
    }

    private bool TryApplyIncomingSyncVar(MetaverseNetworkSyncVarPayload payload)
    {
        if (payload == null)
        {
            Reject("sync_var_payload_missing");
            return false;
        }

        string safeKey = SafeTrim(payload.syncKey);
        if (payload.netId <= 0 || string.IsNullOrWhiteSpace(safeKey))
        {
            Reject("sync_var_payload_invalid");
            return false;
        }

        if (!TryGetClientIdentity(payload.netId, out MetaverseNetworkIdentity identity))
        {
            Reject("net_id_not_found_on_client");
            return false;
        }

        string dictKey = BuildSyncVarCacheKey(payload.netId, safeKey);
        long currentVersion = dict_versions.TryGetValue(dictKey, out long cachedVersion) ? cachedVersion : 0;
        if (ignoreOlderSyncVarVersions && payload.version > 0 && currentVersion > 0 && payload.version <= currentVersion)
        {
            Reject("stale_sync_var_version");
            return false;
        }

        string previousValue = dict_values.TryGetValue(dictKey, out string cachedValue) ? cachedValue : payload.oldValueJson;
        payload.oldValueJson = string.IsNullOrWhiteSpace(previousValue) ? "{}" : previousValue;
        payload.valueJson = SafeJson(payload.valueJson);
        payload.syncKey = safeKey;

        long version = payload.version > 0 ? payload.version : currentVersion + 1;
        payload.version = version;
        dict_versions[dictKey] = version;
        dict_values[dictKey] = payload.valueJson;

        DispatchSyncVar(identity, payload);
        ClearRejectReason();

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkStateSyncBridge] SyncVar applied on client | mirrorRoute=SyncVar | netId=" + payload.netId +
                      " | key=" + safeKey +
                      " | version=" + payload.version +
                      " | incomingRoute=game/sync_var");
        }

        return true;
    }

    private bool TryApplyIncomingNetworkTransform(MetaverseNetworkTransformPayload payload)
    {
        if (payload == null)
        {
            Reject("network_transform_payload_missing");
            return false;
        }

        if (payload.netId <= 0)
        {
            Reject("invalid_net_id");
            return false;
        }

        if (!TryGetClientIdentity(payload.netId, out MetaverseNetworkIdentity identity))
        {
            Reject("net_id_not_found_on_client");
            return false;
        }

        long currentSequence = dict_transformSequences.TryGetValue(payload.netId, out long cachedSequence) ? cachedSequence : 0;
        if (ignoreOlderTransformSequences && payload.sequence > 0 && currentSequence > 0 && payload.sequence <= currentSequence)
        {
            Reject("stale_network_transform_sequence");
            return false;
        }

        dict_transformSequences[payload.netId] = payload.sequence;
        ApplyNetworkTransform(identity, payload);
        ClearRejectReason();

        if (logMessages)
        {
            Debug.Log("[MetaverseNetworkStateSyncBridge] NetworkTransform applied on client | mirrorRoute=NetworkTransform | netId=" + payload.netId +
                      " | sequence=" + payload.sequence +
                      " | position=" + payload.position +
                      " | incomingRoute=game/network_transform");
        }

        return true;
    }

    private void DispatchSyncVarOnServer(MetaverseNetworkSyncVarPayload payload)
    {
        if (payload == null) return;
        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
        if (spawnManager == null) return;
        if (!spawnManager.TryGetSpawnedObject(payload.netId, out MetaverseNetworkIdentity identity) || identity == null) return;
        DispatchSyncVar(identity, payload);
    }

    private void DispatchNetworkTransformOnServer(MetaverseNetworkIdentity identity, MetaverseNetworkTransformPayload payload)
    {
        if (identity == null || payload == null) return;
        DispatchNetworkTransform(identity, payload);
    }

    private void DispatchSyncVar(MetaverseNetworkIdentity identity, MetaverseNetworkSyncVarPayload payload)
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
                behaviour.OnSyncVarChanged(payload.syncKey, payload.oldValueJson, payload.valueJson, payload.version);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, behaviour);
            }
        }
    }

    private void ApplyNetworkTransform(MetaverseNetworkIdentity identity, MetaverseNetworkTransformPayload payload)
    {
        if (identity == null || payload == null) return;
        identity.transform.position = payload.position;
        identity.transform.rotation = payload.rotation;
        if (applyTransformScale && payload.scale != Vector3.zero) identity.transform.localScale = payload.scale;
        DispatchNetworkTransform(identity, payload);
    }

    private void DispatchNetworkTransform(MetaverseNetworkIdentity identity, MetaverseNetworkTransformPayload payload)
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
                behaviour.OnNetworkTransform(payload);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, behaviour);
            }
        }
    }

    private bool TryGetClientIdentity(int netId, out MetaverseNetworkIdentity identity)
    {
        return TryGetIdentity(netId, out identity);
    }

    private bool TryGetIdentity(int netId, out MetaverseNetworkIdentity identity)
    {
        identity = null;
        if (spawnManager == null) spawnManager = MetaverseSpawnManager.Instance;
        if (spawnManager == null) return false;
        return spawnManager.TryGetSpawnedObject(netId, out identity) && identity != null;
    }

    private MetaverseNetworkIdentity GetIdentity(GameObject obj)
    {
        return obj != null ? obj.GetComponent<MetaverseNetworkIdentity>() : null;
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
        dedicatedClient.RawMessageReceived -= OnDedicatedClientRawMessageReceived;
        dedicatedClient.RawMessageReceived += OnDedicatedClientRawMessageReceived;
        clientEventsBound = true;
        if (logMessages) Debug.Log("[MetaverseNetworkStateSyncBridge] Bound to dedicated client raw messages.");
    }

    private void UnbindClientEvents()
    {
        if (dedicatedClient != null) dedicatedClient.RawMessageReceived -= OnDedicatedClientRawMessageReceived;
        clientEventsBound = false;
    }

    private void OnDedicatedClientRawMessageReceived(string rawJson)
    {
        HandleDedicatedClientRawMessage(rawJson);
    }

    private bool CanWriteServerState()
    {
        if (Application.isBatchMode) return true;
#if UNITY_EDITOR
        if (allowEditorServerSimulation) return true;
#endif
        return !rejectClientOutboundStateSync;
    }

    private string BuildSyncVarCacheKey(int netId, string syncKey)
    {
        return netId + ":" + SafeTrim(syncKey);
    }

    private void Reject(string reason)
    {
        lastStateSyncRejectReason = SafeTrim(reason);
        if (logRejectedMessages && !string.IsNullOrWhiteSpace(lastStateSyncRejectReason))
        {
            Debug.LogWarning("[MetaverseNetworkStateSyncBridge] State sync rejected | reason=" + lastStateSyncRejectReason);
        }
    }

    private void ClearRejectReason()
    {
        lastStateSyncRejectReason = string.Empty;
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
}
