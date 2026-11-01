#if UNITY_SERVER
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Network_A.GameServer;
using Network_A.GameServer.Gameplay;
using UnityEngine;
using UnityEngine.Networking;

namespace Network_A.Voice.RecordingSpatial.Dedicated
{
    [DisallowMultipleComponent]
    public sealed class VoiceSpatialTimelineDedicatedSender : MonoBehaviour
    {
        public const string RelativeEndpointPath =
            "/game-server-control/dedicated/voice-spatial-timeline";

        private const int DefaultSpatialTimelinePort = 51013;
        private const int MaxPendingEvents = 8192;
        private const int MaxBatchEvents = 256;
        private const float FlushIntervalSeconds = 0.10f;
        private const float InitialRetrySeconds = 0.50f;
        private const float MaxRetrySeconds = 4.0f;
        private const int RequestTimeoutSeconds = 15;

        private readonly Queue<SpatialStateEventDto> pending =
            new Queue<SpatialStateEventDto>();

        private DedicatedPlayerStateStore stateStore;
        private DedicatedServerRuntime runtime;
        private GameServerControlDedicatedClient controlClient;
        private CancellationTokenSource lifecycleCts;

        private bool configured;
        private bool sendRunning;
        private bool queueFaulted;
        private float nextFlushAt;
        private float nextRetryAt;
        private float retrySeconds = InitialRetrySeconds;

        public int PendingCount { get { return pending.Count; } }
        public bool IsConfigured { get { return configured; } }
        public bool IsQueueFaulted { get { return queueFaulted; } }

        public void Configure(
            DedicatedPlayerStateStore acceptedStateStore,
            DedicatedServerRuntime dedicatedRuntime,
            GameServerControlDedicatedClient dedicatedControlClient)
        {
            if (acceptedStateStore == null) throw new ArgumentNullException(nameof(acceptedStateStore));
            if (dedicatedRuntime == null) throw new ArgumentNullException(nameof(dedicatedRuntime));
            if (dedicatedControlClient == null) throw new ArgumentNullException(nameof(dedicatedControlClient));

            if (stateStore != null)
            {
                stateStore.PlayerStateUpdated -= HandlePlayerStateUpdated;
            }

            stateStore = acceptedStateStore;
            runtime = dedicatedRuntime;
            controlClient = dedicatedControlClient;

            stateStore.PlayerStateUpdated += HandlePlayerStateUpdated;

            if (lifecycleCts != null)
            {
                lifecycleCts.Cancel();
                lifecycleCts.Dispose();
            }

            lifecycleCts = new CancellationTokenSource();
            configured = true;
            queueFaulted = false;
            retrySeconds = InitialRetrySeconds;
            nextRetryAt = 0f;
            nextFlushAt = Time.realtimeSinceStartup + FlushIntervalSeconds;

            Debug.Log(
                "VOICE_SPATIAL_TIMELINE_DEDICATED_SENDER=READY" +
                " | endpoint=" + RelativeEndpointPath +
                " | transport=shared_control_http" +
                " | oldVoiceChanged=False" +
                " | oldRecordingChanged=False" +
                " | sharedServiceTokenRenewal=False");
        }

        private void HandlePlayerStateUpdated(DedicatedPlayerStateRecord record)
        {
            if (!configured || queueFaulted || record == null) return;

            if (pending.Count >= MaxPendingEvents)
            {
                queueFaulted = true;
                Debug.LogError(
                    "VOICE_SPATIAL_TIMELINE_QUEUE=FAULT" +
                    " | pending=" + pending.Count +
                    " | limit=" + MaxPendingEvents);
                return;
            }

            SpatialStateEventDto evt = SpatialStateEventDto.FromRecord(record);
            if (!evt.IsValid()) return;

            pending.Enqueue(evt);

            if (pending.Count >= MaxBatchEvents)
            {
                nextFlushAt = 0f;
            }
        }

        private void Update()
        {
            if (!configured ||
                queueFaulted ||
                sendRunning ||
                pending.Count == 0 ||
                lifecycleCts == null ||
                lifecycleCts.IsCancellationRequested)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;

            if (now < nextRetryAt) return;
            if (now < nextFlushAt && pending.Count < MaxBatchEvents) return;

            _ = SendBatchAsync(lifecycleCts.Token);
        }

        private SpatialStateEventDto[] CreateBatch()
        {
            int count = Mathf.Min(MaxBatchEvents, pending.Count);
            if (count <= 0) return new SpatialStateEventDto[0];

            SpatialStateEventDto[] all = pending.ToArray();
            SpatialStateEventDto[] batch = new SpatialStateEventDto[count];
            Array.Copy(all, batch, count);
            return batch;
        }

        private static int ResolveSpatialTimelinePort()
        {
            string raw =
                Environment.GetEnvironmentVariable(
                    "VOICE_SPATIAL_TIMELINE_PORT");

            int port;

            if (int.TryParse(raw, out port) &&
                port >= 1024 &&
                port <= 65535)
            {
                return port;
            }

            return DefaultSpatialTimelinePort;
        }

        private static string BuildSpatialTimelineUrl(
            string controlBaseUrl)
        {
            Uri controlUri;

            if (!Uri.TryCreate(
                controlBaseUrl,
                UriKind.Absolute,
                out controlUri))
            {
                return string.Empty;
            }

            string safeBaseUrl = controlBaseUrl.Trim().TrimEnd('/');
            return safeBaseUrl + RelativeEndpointPath;
        }

        private async Task SendBatchAsync(CancellationToken cancellationToken)
        {
            if (sendRunning) return;
            sendRunning = true;

            try
            {
                SpatialStateEventDto[] batch = CreateBatch();
                if (batch.Length == 0) return;

                DedicatedServerConfigData config = runtime.GetCurrentConfig();
                if (config == null)
                {
                    RegisterRetry("dedicated_config_missing");
                    return;
                }

                string serverId = Safe(config.serverId);
                string baseUrl = Safe(config.controlBaseUrl).TrimEnd('/');
                string url = BuildSpatialTimelineUrl(baseUrl);

                if (serverId.Length == 0 ||
                    baseUrl.Length == 0 ||
                    url.Length == 0)
                {
                    RegisterRetry("dedicated_config_identity_missing");
                    return;
                }

                string serviceToken =
                    controlClient.GetActiveServiceToken();

                if (string.IsNullOrWhiteSpace(serviceToken))
                {
                    RegisterRetry("service_token_missing");
                    return;
                }

                SpatialTimelineBatchRequestDto body =
                    new SpatialTimelineBatchRequestDto
                    {
                        serviceToken = serviceToken.Trim(),
                        serverId = serverId,
                        events = batch
                    };

                string json = JsonUtility.ToJson(body);

                using (UnityWebRequest request =
                    new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
                {
                    byte[] raw = Encoding.UTF8.GetBytes(json);
                    request.uploadHandler = new UploadHandlerRaw(raw);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.timeout = RequestTimeoutSeconds;

                    UnityWebRequestAsyncOperation operation = request.SendWebRequest();

                    while (!operation.isDone)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await Task.Yield();
                    }

                    long statusCode = request.responseCode;
                    bool success =
                        request.result == UnityWebRequest.Result.Success &&
                        statusCode >= 200 &&
                        statusCode <= 299;

                    if (!success)
                    {
                        RegisterRetry(
                            "http_" + statusCode +
                            "_" + Safe(request.error));
                        return;
                    }
                }

                for (int i = 0; i < batch.Length && pending.Count > 0; i++)
                {
                    pending.Dequeue();
                }

                retrySeconds = InitialRetrySeconds;
                nextRetryAt = 0f;
                nextFlushAt = Time.realtimeSinceStartup + FlushIntervalSeconds;

                Debug.Log(
                    "VOICE_SPATIAL_TIMELINE_BATCH=PASS" +
                    " | count=" + batch.Length +
                    " | pending=" + pending.Count +
                    " | serverId=" + serverId +
                    " | endpoint=" + RelativeEndpointPath);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                RegisterRetry(exception.GetType().Name + "_" + Safe(exception.Message));
            }
            finally
            {
                sendRunning = false;
            }
        }

        private void RegisterRetry(string reason)
        {
            nextRetryAt = Time.realtimeSinceStartup + retrySeconds;
            retrySeconds = Mathf.Min(MaxRetrySeconds, retrySeconds * 1.75f);

            Debug.LogWarning(
                "VOICE_SPATIAL_TIMELINE_BATCH=RETRY" +
                " | pending=" + pending.Count +
                " | retrySeconds=" + retrySeconds.ToString("F2") +
                " | reason=" + Safe(reason));
        }

        private void OnDestroy()
        {
            if (stateStore != null)
            {
                stateStore.PlayerStateUpdated -= HandlePlayerStateUpdated;
            }

            if (lifecycleCts != null)
            {
                lifecycleCts.Cancel();
                lifecycleCts.Dispose();
                lifecycleCts = null;
            }
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
        }

        [Serializable]
        private sealed class SpatialTimelineBatchRequestDto
        {
            public string serviceToken;
            public string serverId;
            public SpatialStateEventDto[] events;
        }

        [Serializable]
        private sealed class SpatialStateEventDto
        {
            public string roomId;
            public string gameSessionId;
            public string userId;
            public string playerId;
            public string connectionId;
            public long sequence;
            public long clientTimestampUnixMs;
            public long serverTimestampUnixMs;
            public float px;
            public float py;
            public float pz;
            public float rx;
            public float ry;
            public float rz;
            public float rw;

            public static SpatialStateEventDto FromRecord(DedicatedPlayerStateRecord record)
            {
                return new SpatialStateEventDto
                {
                    roomId = Safe(record.roomId),
                    gameSessionId = Safe(record.sessionId),
                    userId = Safe(record.userId),
                    playerId = Safe(record.playerId),
                    connectionId = Safe(record.connectionId),
                    sequence = record.sequence,
                    clientTimestampUnixMs = record.clientTimestampUnixMs,
                    serverTimestampUnixMs = record.serverTimestampUnixMs,
                    px = record.px,
                    py = record.py,
                    pz = record.pz,
                    rx = record.rx,
                    ry = record.ry,
                    rz = record.rz,
                    rw = record.rw
                };
            }

            public bool IsValid()
            {
                return roomId.Length > 0 &&
                       userId.Length > 0 &&
                       sequence > 0 &&
                       serverTimestampUnixMs > 0 &&
                       IsFinite(px) &&
                       IsFinite(py) &&
                       IsFinite(pz) &&
                       IsFinite(rx) &&
                       IsFinite(ry) &&
                       IsFinite(rz) &&
                       IsFinite(rw);
            }

            private static bool IsFinite(float value)
            {
                return !float.IsNaN(value) && !float.IsInfinity(value);
            }
        }
    }
}
#endif
