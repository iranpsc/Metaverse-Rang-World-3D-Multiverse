#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Voice.Client.Capture;
using Network_A.Voice.Client.Playback;
using Network_A.Voice.Client.Runtime;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Network_A.Voice.Client.MediaV2.Windows
{
    public static class VoiceWindowsMediaWebGLParityInstaller
    {
        private const string RootName = "Voice_Windows_Media_WebGL_Parity_Runtime";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject root = GameObject.Find(RootName);
            if (root == null) root = new GameObject(RootName);
            UnityEngine.Object.DontDestroyOnLoad(root);

            if (root.GetComponent<VoiceWindowsMediaWebGLParityRuntimeAdapter>() == null)
                root.AddComponent<VoiceWindowsMediaWebGLParityRuntimeAdapter>();

            Debug.Log(
                "VOICE_WINDOWS_WEBGL_PARITY_RUNTIME=READY" +
                " | transport=VoiceGrpcMediaTransportV2" +
                " | receiveOnly=True" +
                " | pingIntervalMs=5000" +
                " | heartbeatTimeoutMs=15000" +
                " | bindTimeoutMs=8000" +
                " | retryScheduleMs=100,200,400,800,1600,2000" +
                " | mediaSequenceContinuity=True" +
                " | singleSerializedWriter=True" +
                " | legacyFallbackDuringReconnect=False" +
                " | stableFilesChanged=False" +
                " | webglPathChanged=False" +
                " | questPathChanged=False");
        }
    }

    [DefaultExecutionOrder(-32000)]
    [DisallowMultipleComponent]
    public sealed class VoiceWindowsMediaWebGLParityRuntimeAdapter : MonoBehaviour
    {
        private const string RuntimeRootName = "Voice_Client_Runtime_Root";
        private const string OldResilienceRootName = "Voice_Windows_Media_Resilience_Wrapper";
        private const int MaximumInboundFrames = 128;
        private const int MaximumDeferredFrames = 64;
        private const long DeferredFrameMaxAgeMs = 1500;
        private const ulong OpusFrameDuration100Ns = 200000UL;

        private readonly ConcurrentQueue<InboundFrame> inboundFrames =
            new ConcurrentQueue<InboundFrame>();
        private readonly List<InboundFrame> deferredFrames =
            new List<InboundFrame>(MaximumDeferredFrames);
        private readonly Dictionary<Type, MethodInfo> peerResolverByType =
            new Dictionary<Type, MethodInfo>();

        private VoiceClientRuntime runtime;
        private VoiceMicrophonePublisher microphonePublisher;
        private VoiceSpatialPlaybackManager playbackManager;
        private VoiceWindowsMediaWebGLParitySession mediaSession;
        private CancellationTokenSource mediaConnectCts;

        private FieldInfo sessionsField;
        private FieldInfo outboundSchedulerField;
        private FieldInfo lastPublishedSequenceField;
        private FieldInfo firstVoiceFrameSendLoggedField;
        private MethodInfo legacyFrameHandlerMethod;
        private Action<byte[], bool> legacyFrameHandler;

        private VoiceWindowsMediaRuntimeAdapter oldAdapter;
        private string boundControlConnectionId = string.Empty;
        private string boundRoomId = string.Empty;
        private string sequenceOwnerControlConnectionId = string.Empty;
        private float retryAt;
        private float nextPingAt;
        private float nextHeartbeatCheckAt;
        private bool mediaConnectStarted;
        private bool mediaBound;
        private bool ownsOutboundMedia;
        private bool firstOutboundFrameLogged;
        private bool firstInboundFrameLogged;
        private bool oldAdapterDisabledLogged;
        private bool oldWrapperDisabledLogged;
        private int resetRequested;
        private string resetReason = string.Empty;
        private int pendingInboundCount;
        private int consecutiveFailures;
        private uint nextMediaSequence = 1;
        private ulong nextMediaTimestamp100Ns;

        private void Awake()
        {
            DisableOldWindowsPath();
        }

        private void Update()
        {
            DisableOldWindowsPath();

            if (Interlocked.Exchange(ref resetRequested, 0) != 0)
            {
                ResetMediaSession(
                    string.IsNullOrWhiteSpace(resetReason)
                        ? "media_callback_reset"
                        : resetReason,
                    true);
            }

            ResolveRuntime();
            if (runtime == null)
            {
                if (mediaSession != null || mediaConnectStarted || ownsOutboundMedia)
                    ResetMediaSession("runtime_missing", false);
                return;
            }

            DrainInboundFrames();
            DrainDeferredFrames();

            if (!runtime.IsAuthenticated ||
                !Guid.TryParse(runtime.VoiceConnectionId, out _))
            {
                if (mediaSession != null || mediaConnectStarted || ownsOutboundMedia)
                    ResetMediaSession("control_not_authenticated", false);
                return;
            }

            string currentConnectionId =
                runtime.VoiceConnectionId.Trim().ToLowerInvariant();
            string currentRoomId =
                (MetaverseNetworkClient.roomId ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(currentRoomId))
            {
                if (mediaSession != null || mediaConnectStarted)
                    ResetMediaSession("room_missing", true);
                return;
            }

            if (sequenceOwnerControlConnectionId.Length == 0)
            {
                BeginSequenceOwnership(currentConnectionId);
            }
            else if (!string.Equals(
                         sequenceOwnerControlConnectionId,
                         currentConnectionId,
                         StringComparison.OrdinalIgnoreCase))
            {
                ResetMediaSession("control_connection_changed", false);
                BeginSequenceOwnership(currentConnectionId);
            }

            if (boundRoomId.Length > 0 &&
                !string.Equals(
                    boundRoomId,
                    currentRoomId,
                    StringComparison.Ordinal))
            {
                ResetMediaSession("room_changed", true);
                return;
            }

            EnsureOutboundOwnership();

            if (!mediaBound &&
                !mediaConnectStarted &&
                Time.realtimeSinceStartup >= retryAt)
            {
                StartMediaConnection(
                    currentConnectionId,
                    currentRoomId);
                return;
            }

            if (!mediaBound || mediaSession == null || !mediaSession.IsBound)
                return;

            float now = Time.realtimeSinceStartup;

            if (now >= nextPingAt)
            {
                nextPingAt =
                    now +
                    VoiceWindowsMediaConnectionPolicy.PingIntervalSeconds;
                mediaSession.TryQueuePing();
            }

            if (now >= nextHeartbeatCheckAt)
            {
                nextHeartbeatCheckAt =
                    now +
                    VoiceWindowsMediaConnectionPolicy.HeartbeatCheckIntervalSeconds;
                CheckHeartbeat();
            }
        }

        private void DisableOldWindowsPath()
        {
            if (oldAdapter == null)
            {
                oldAdapter =
                    UnityEngine.Object.FindObjectOfType<
                        VoiceWindowsMediaRuntimeAdapter>();
            }

            if (oldAdapter != null && oldAdapter.enabled)
            {
                oldAdapter.enabled = false;
                if (!oldAdapterDisabledLogged)
                {
                    oldAdapterDisabledLogged = true;
                    Debug.Log(
                        "VOICE_WINDOWS_WEBGL_PARITY_OLD_ADAPTER_DISABLED=PASS" +
                        " | source=VoiceWindowsMediaRuntimeAdapter" +
                        " | sourceFileChanged=False");
                }
            }

            GameObject oldWrapper = GameObject.Find(OldResilienceRootName);
            if (oldWrapper != null && oldWrapper.activeSelf)
            {
                oldWrapper.SetActive(false);
                if (!oldWrapperDisabledLogged)
                {
                    oldWrapperDisabledLogged = true;
                    Debug.Log(
                        "VOICE_WINDOWS_WEBGL_PARITY_OLD_WRAPPER_DISABLED=PASS" +
                        " | root=" + OldResilienceRootName);
                }
            }
        }

        private void BeginSequenceOwnership(string controlConnectionId)
        {
            sequenceOwnerControlConnectionId =
                Safe(controlConnectionId).ToLowerInvariant();

            uint previous =
                ReadRuntimePublishedSequence();

            nextMediaSequence =
                previous == 0 || previous == uint.MaxValue
                    ? 1
                    : previous + 1;

            nextMediaTimestamp100Ns = 0;

            Debug.Log(
                "VOICE_WINDOWS_MEDIA_SEQUENCE_OWNER=PASS" +
                " | controlConnectionId=" +
                sequenceOwnerControlConnectionId +
                " | startMediaSequence=" +
                nextMediaSequence +
                " | resetOnlyOnControlConnectionChange=True");
        }

        private uint ReadRuntimePublishedSequence()
        {
            if (runtime == null ||
                lastPublishedSequenceField == null)
                return 0;

            try
            {
                object value =
                    lastPublishedSequenceField.GetValue(runtime);

                if (value is uint u) return u;
                if (value is int i && i > 0) return (uint)i;
                if (value is long l && l > 0 && l <= uint.MaxValue)
                    return (uint)l;
            }
            catch
            {
            }

            return 0;
        }

        private void ResolveRuntime()
        {
            if (runtime != null)
            {
                GameObject currentRoot =
                    GameObject.Find(RuntimeRootName);

                if (currentRoot != null &&
                    ReferenceEquals(
                        currentRoot.GetComponent<VoiceClientRuntime>(),
                        runtime))
                {
                    return;
                }

                ResetMediaSession("runtime_replaced", false);
                ClearRuntimeReferences();
            }

            GameObject root =
                GameObject.Find(RuntimeRootName);
            if (root == null) return;

            VoiceClientRuntime candidate =
                root.GetComponent<VoiceClientRuntime>();
            if (candidate == null) return;

            runtime = candidate;
            microphonePublisher =
                root.GetComponent<VoiceMicrophonePublisher>();
            playbackManager =
                root.GetComponent<VoiceSpatialPlaybackManager>();

            Type runtimeType =
                typeof(VoiceClientRuntime);
            const BindingFlags instancePrivate =
                BindingFlags.Instance |
                BindingFlags.NonPublic;

            sessionsField =
                runtimeType.GetField(
                    "sessions",
                    instancePrivate);
            outboundSchedulerField =
                runtimeType.GetField(
                    "outboundScheduler",
                    instancePrivate);
            lastPublishedSequenceField =
                runtimeType.GetField(
                    "lastPublishedSequence",
                    instancePrivate);
            firstVoiceFrameSendLoggedField =
                runtimeType.GetField(
                    "firstVoiceFrameSendLogged",
                    instancePrivate);
            legacyFrameHandlerMethod =
                runtimeType.GetMethod(
                    "HandleFrameEncoded",
                    instancePrivate,
                    null,
                    new[] { typeof(byte[]), typeof(bool) },
                    null);

            legacyFrameHandler = null;

            if (legacyFrameHandlerMethod != null)
            {
                try
                {
                    legacyFrameHandler =
                        (Action<byte[], bool>)
                        Delegate.CreateDelegate(
                            typeof(Action<byte[], bool>),
                            runtime,
                            legacyFrameHandlerMethod);
                }
                catch
                {
                    legacyFrameHandler = null;
                }
            }

            Debug.Log(
                "VOICE_WINDOWS_WEBGL_PARITY_RUNTIME_DISCOVERED=PASS" +
                " | runtime=True" +
                " | microphonePublisher=" +
                (microphonePublisher != null) +
                " | playbackManager=" +
                (playbackManager != null) +
                " | sessionsField=" +
                (sessionsField != null) +
                " | legacyFrameHandler=" +
                (legacyFrameHandler != null));
        }

        private async void StartMediaConnection(
            string controlConnectionId,
            string roomId)
        {
            if (mediaConnectStarted ||
                mediaBound ||
                runtime == null)
            {
                return;
            }

            if (!Guid.TryParse(controlConnectionId, out _) ||
                string.IsNullOrWhiteSpace(roomId))
            {
                ScheduleRetry("invalid_bind_context");
                return;
            }

            string accessToken =
                (SecureTokenStorage.GetAccessToken() ??
                 string.Empty).Trim();

            if (accessToken.Length == 0)
            {
                ScheduleRetry("access_token_missing");
                return;
            }

            mediaConnectStarted = true;

            mediaConnectCts?.Cancel();
            mediaConnectCts?.Dispose();
            mediaConnectCts =
                new CancellationTokenSource();

            VoiceWindowsMediaWebGLParitySession created =
                null;

            try
            {
                created =
                    new VoiceWindowsMediaWebGLParitySession(
                        new VoiceGrpcMediaTransportV2());

                created.MediaReceived +=
                    packet =>
                        HandleMediaReceived(
                            created,
                            packet);
                created.PongReceived +=
                    () =>
                        HandlePongReceived(created);
                created.Failed +=
                    reason =>
                        HandleMediaFailure(
                            created,
                            reason);
                created.Disconnected +=
                    reason =>
                        HandleMediaDisconnected(
                            created,
                            reason);

                string streamId =
                    Guid.NewGuid().ToString("D");

                bool bound =
                    await created.ConnectAndBindAsync(
                        ServerConfig
                            .BuildRealtimeGrpcStreamingTarget(),
                        accessToken,
                        roomId,
                        controlConnectionId,
                        streamId,
                        2,
                        Application.version,
                        mediaConnectCts.Token);

                if (!bound ||
                    runtime == null ||
                    !runtime.IsAuthenticated ||
                    !string.Equals(
                        runtime.VoiceConnectionId,
                        controlConnectionId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    created.Dispose();
                    ScheduleRetry("bind_rejected_or_stale");
                    return;
                }

                mediaSession = created;
                boundControlConnectionId =
                    controlConnectionId;
                boundRoomId = roomId;
                mediaBound = true;
                consecutiveFailures = 0;
                firstOutboundFrameLogged = false;
                firstInboundFrameLogged = false;

                nextPingAt =
                    Time.realtimeSinceStartup +
                    VoiceWindowsMediaConnectionPolicy
                        .PingIntervalSeconds;
                nextHeartbeatCheckAt =
                    Time.realtimeSinceStartup +
                    VoiceWindowsMediaConnectionPolicy
                        .HeartbeatCheckIntervalSeconds;

                Debug.Log(
                    "VOICE_WINDOWS_WEBGL_PARITY_BIND=PASS" +
                    " | controlConnectionId=" +
                    boundControlConnectionId +
                    " | streamId=" +
                    created.StreamId +
                    " | transport=VoiceGrpcMediaTransportV2" +
                    " | receiveOnlyBind=True" +
                    " | pingIntervalMs=5000" +
                    " | heartbeatTimeoutMs=15000" +
                    " | bindTimeoutMs=8000" +
                    " | mediaSequenceNext=" +
                    nextMediaSequence +
                    " | legacyFallbackDuringReconnect=False");
            }
            catch (OperationCanceledException)
            {
                try { created?.Dispose(); } catch { }
            }
            catch (Exception exception)
            {
                try { created?.Dispose(); } catch { }

                Debug.LogWarning(
                    "VOICE_WINDOWS_WEBGL_PARITY_BIND=FAIL" +
                    " | error=" +
                    Safe(exception.Message));

                ScheduleRetry("bind_exception");
            }
            finally
            {
                mediaConnectStarted = false;
            }
        }

        private void EnsureOutboundOwnership()
        {
            if (ownsOutboundMedia ||
                runtime == null)
            {
                return;
            }

            if (microphonePublisher == null)
            {
                microphonePublisher =
                    runtime.GetComponent<
                        VoiceMicrophonePublisher>();
            }

            if (microphonePublisher == null ||
                legacyFrameHandler == null)
            {
                return;
            }

            microphonePublisher.FrameEncoded -=
                legacyFrameHandler;
            microphonePublisher.FrameEncoded -=
                HandleFrameEncoded;
            microphonePublisher.FrameEncoded +=
                HandleFrameEncoded;

            ownsOutboundMedia = true;
            ClearLegacyMediaQueue();

            Debug.Log(
                "VOICE_WINDOWS_WEBGL_PARITY_OUTBOUND_OWNER=PASS" +
                " | mediaOwnsEncodedFrames=True" +
                " | legacyFallbackDuringReconnect=False");
        }

        private void RestoreLegacyOutboundMedia()
        {
            if (!ownsOutboundMedia) return;

            if (microphonePublisher != null)
            {
                microphonePublisher.FrameEncoded -=
                    HandleFrameEncoded;

                if (legacyFrameHandler != null)
                {
                    microphonePublisher.FrameEncoded -=
                        legacyFrameHandler;
                    microphonePublisher.FrameEncoded +=
                        legacyFrameHandler;
                }
            }

            ownsOutboundMedia = false;

            Debug.Log(
                "VOICE_WINDOWS_WEBGL_PARITY_OUTBOUND_OWNER=RESTORED" +
                " | reason=control_path_unavailable");
        }

        private void ClearLegacyMediaQueue()
        {
            if (runtime == null ||
                outboundSchedulerField == null)
            {
                return;
            }

            try
            {
                object scheduler =
                    outboundSchedulerField.GetValue(runtime);

                if (scheduler == null) return;

                MethodInfo clearMedia =
                    scheduler.GetType().GetMethod(
                        "ClearMedia",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic,
                        null,
                        new[] { typeof(string) },
                        null);

                clearMedia?.Invoke(
                    scheduler,
                    new object[]
                    {
                        "windows_webgl_parity_media_activated"
                    });
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "VOICE_WINDOWS_WEBGL_PARITY_LEGACY_QUEUE_CLEAR=FAIL" +
                    " | error=" +
                    Safe(exception.Message));
            }
        }

        private void HandleFrameEncoded(
            byte[] packet,
            bool dtx)
        {
            VoiceWindowsMediaWebGLParitySession session =
                mediaSession;
            VoiceClientRuntime activeRuntime =
                runtime;

            if (!mediaBound ||
                session == null ||
                !session.IsBound ||
                activeRuntime == null ||
                !activeRuntime.IsAuthenticated ||
                !activeRuntime.IsVoiceEligible ||
                packet == null ||
                packet.Length == 0)
            {
                return;
            }

            uint mediaSequence =
                nextMediaSequence;
            ulong mediaTimestamp100Ns =
                nextMediaTimestamp100Ns;

            bool queued =
                session.TryQueueMedia(
                    mediaSequence,
                    mediaTimestamp100Ns,
                    packet,
                    VoiceMediaV2Flags.None);

            if (!queued) return;

            nextMediaSequence =
                mediaSequence == uint.MaxValue
                    ? 1
                    : mediaSequence + 1;

            nextMediaTimestamp100Ns =
                mediaTimestamp100Ns +
                OpusFrameDuration100Ns;

            MirrorPublishedSequence(
                mediaSequence);

            if (!firstOutboundFrameLogged)
            {
                firstOutboundFrameLogged = true;

                Debug.Log(
                    "VOICE_WINDOWS_WEBGL_PARITY_FIRST_FRAME_SENT=PASS" +
                    " | mediaSequence=" +
                    mediaSequence +
                    " | bytes=" +
                    packet.Length +
                    " | dtx=" +
                    dtx +
                    " | path=MediaV2" +
                    " | sequenceContinuity=True");
            }
        }

        private void MirrorPublishedSequence(
            uint sequence)
        {
            if (runtime == null) return;

            try
            {
                lastPublishedSequenceField?.SetValue(
                    runtime,
                    sequence);
                firstVoiceFrameSendLoggedField?.SetValue(
                    runtime,
                    true);
            }
            catch
            {
            }
        }

        private void HandlePongReceived(
            VoiceWindowsMediaWebGLParitySession source)
        {
            if (!ReferenceEquals(mediaSession, source))
                return;

            consecutiveFailures = 0;

            if (source.PongCount == 1)
            {
                Debug.Log(
                    "VOICE_WINDOWS_WEBGL_PARITY_KEEPALIVE=PASS" +
                    " | pingIntervalMs=5000" +
                    " | heartbeatTimeoutMs=15000" +
                    " | pongCount=1" +
                    " | singleSerializedWriter=True");
            }
        }

        private void HandleMediaReceived(
            VoiceWindowsMediaWebGLParitySession source,
            VoiceMediaV2Packet packet)
        {
            if (!ReferenceEquals(mediaSession, source) ||
                packet == null)
            {
                return;
            }

            consecutiveFailures = 0;

            if (packet.Kind !=
                    VoiceMediaV2PacketKind.Media ||
                packet.Codec !=
                    VoiceMediaV2Codec.Opus ||
                packet.Payload == null ||
                packet.Payload.Length == 0)
            {
                return;
            }

            int pending =
                Interlocked.Increment(
                    ref pendingInboundCount);

            if (pending > MaximumInboundFrames)
            {
                if (inboundFrames.TryDequeue(out _))
                {
                    Interlocked.Decrement(
                        ref pendingInboundCount);
                }
            }

            inboundFrames.Enqueue(
                new InboundFrame(
                    packet,
                    MonotonicMs()));
        }

        private void DrainInboundFrames()
        {
            int processed = 0;

            while (processed <
                       MaximumInboundFrames &&
                   inboundFrames.TryDequeue(
                       out InboundFrame frame))
            {
                Interlocked.Decrement(
                    ref pendingInboundCount);
                processed++;

                if (!TryDeliverInboundFrame(frame) &&
                    MonotonicMs() -
                        frame.FirstSeenMs <=
                    DeferredFrameMaxAgeMs &&
                    deferredFrames.Count <
                        MaximumDeferredFrames)
                {
                    deferredFrames.Add(frame);
                }
            }
        }

        private void DrainDeferredFrames()
        {
            if (deferredFrames.Count == 0)
                return;

            long now = MonotonicMs();

            for (int index =
                     deferredFrames.Count - 1;
                 index >= 0;
                 index--)
            {
                InboundFrame frame =
                    deferredFrames[index];

                if (TryDeliverInboundFrame(frame) ||
                    now - frame.FirstSeenMs >
                        DeferredFrameMaxAgeMs)
                {
                    deferredFrames.RemoveAt(index);
                }
            }
        }

        private bool TryDeliverInboundFrame(
            InboundFrame frame)
        {
            if (runtime == null ||
                playbackManager == null ||
                frame.Packet == null)
            {
                return false;
            }

            string sessionId =
                Safe(frame.Packet.SessionId);
            string senderConnectionId =
                Safe(frame.Packet.SenderId);

            if (!Guid.TryParse(sessionId, out _) ||
                !Guid.TryParse(
                    senderConnectionId,
                    out _))
            {
                return true;
            }

            if (string.Equals(
                    senderConnectionId,
                    runtime.VoiceConnectionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!TryResolvePeerUserId(
                    sessionId,
                    senderConnectionId,
                    out string peerUserId))
            {
                return false;
            }

            playbackManager.ReceiveFrame(
                sessionId,
                senderConnectionId,
                peerUserId,
                frame.Packet.Payload);

            if (!firstInboundFrameLogged)
            {
                firstInboundFrameLogged = true;

                Debug.Log(
                    "VOICE_WINDOWS_WEBGL_PARITY_FIRST_REMOTE_FRAME=PASS" +
                    " | sessionId=" +
                    sessionId +
                    " | senderConnectionId=" +
                    senderConnectionId +
                    " | peerUserId=" +
                    peerUserId +
                    " | bytes=" +
                    frame.Packet.Payload.Length);
            }

            return true;
        }

        private bool TryResolvePeerUserId(
            string sessionId,
            string senderConnectionId,
            out string peerUserId)
        {
            peerUserId = string.Empty;

            if (runtime == null ||
                sessionsField == null)
            {
                return false;
            }

            try
            {
                IDictionary sessions =
                    sessionsField.GetValue(runtime)
                    as IDictionary;

                if (sessions == null ||
                    !sessions.Contains(sessionId))
                {
                    return false;
                }

                object activeSession =
                    sessions[sessionId];

                if (activeSession == null)
                    return false;

                Type activeSessionType =
                    activeSession.GetType();

                if (!peerResolverByType.TryGetValue(
                        activeSessionType,
                        out MethodInfo resolver))
                {
                    resolver =
                        activeSessionType.GetMethod(
                            "TryResolvePeerUserId",
                            BindingFlags.Instance |
                            BindingFlags.Public |
                            BindingFlags.NonPublic,
                            null,
                            new[]
                            {
                                typeof(string),
                                typeof(string)
                                    .MakeByRefType()
                            },
                            null);

                    peerResolverByType[
                        activeSessionType] =
                        resolver;
                }

                if (resolver == null)
                    return false;

                object[] arguments =
                    { senderConnectionId, null };

                bool resolved =
                    (bool)resolver.Invoke(
                        activeSession,
                        arguments);

                if (!resolved)
                    return false;

                peerUserId =
                    Safe(arguments[1] as string);

                return peerUserId.Length > 0;
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "VOICE_WINDOWS_WEBGL_PARITY_PEER_RESOLVE=FAIL" +
                    " | sessionId=" +
                    sessionId +
                    " | senderConnectionId=" +
                    senderConnectionId +
                    " | error=" +
                    Safe(exception.Message));

                return false;
            }
        }

        private void HandleMediaFailure(
            VoiceWindowsMediaWebGLParitySession source,
            string reason)
        {
            if (!ReferenceEquals(
                    mediaSession,
                    source))
            {
                return;
            }

            resetReason =
                "media_failed_" +
                Safe(reason);

            Interlocked.Exchange(
                ref resetRequested,
                1);
        }

        private void HandleMediaDisconnected(
            VoiceWindowsMediaWebGLParitySession source,
            string reason)
        {
            if (!ReferenceEquals(
                    mediaSession,
                    source))
            {
                return;
            }

            resetReason =
                "media_disconnected_" +
                Safe(reason);

            Interlocked.Exchange(
                ref resetRequested,
                1);
        }

        private void CheckHeartbeat()
        {
            VoiceWindowsMediaWebGLParitySession session =
                mediaSession;

            if (session == null ||
                !session.IsBound)
            {
                return;
            }

            double ageSeconds =
                session.InboundAgeSeconds;

            if (ageSeconds <=
                VoiceWindowsMediaConnectionPolicy
                    .HeartbeatTimeoutSeconds)
            {
                return;
            }

            Debug.LogWarning(
                "VOICE_WINDOWS_WEBGL_PARITY_HEARTBEAT=TIMEOUT" +
                " | ageMs=" +
                Math.Round(ageSeconds * 1000d) +
                " | timeoutMs=15000");

            ResetMediaSession(
                "media_heartbeat_timeout",
                true);
        }

        private void ScheduleRetry(
            string reason)
        {
            consecutiveFailures += 1;

            float delay =
                VoiceWindowsMediaConnectionPolicy
                    .GetRetryDelaySeconds(
                        consecutiveFailures);

            retryAt =
                Time.realtimeSinceStartup +
                delay;

            Debug.LogWarning(
                "VOICE_WINDOWS_WEBGL_PARITY_RETRY=PASS" +
                " | reason=" +
                Safe(reason) +
                " | consecutiveFailures=" +
                consecutiveFailures +
                " | retryDelayMs=" +
                Math.Round(delay * 1000f));
        }

        private void ResetMediaSession(
            string reason,
            bool transient)
        {
            mediaConnectCts?.Cancel();
            mediaConnectCts?.Dispose();
            mediaConnectCts = null;

            VoiceWindowsMediaWebGLParitySession previous =
                mediaSession;

            mediaSession = null;
            mediaBound = false;
            mediaConnectStarted = false;
            boundControlConnectionId =
                string.Empty;
            boundRoomId =
                string.Empty;
            firstOutboundFrameLogged = false;
            firstInboundFrameLogged = false;

            try { previous?.Dispose(); }
            catch { }

            while (inboundFrames.TryDequeue(out _))
            {
                Interlocked.Decrement(
                    ref pendingInboundCount);
            }

            deferredFrames.Clear();

            if (transient)
            {
                ScheduleRetry(reason);

                Debug.Log(
                    "VOICE_WINDOWS_WEBGL_PARITY_RESET=PASS" +
                    " | reason=" +
                    Safe(reason) +
                    " | sequencePreserved=True" +
                    " | nextMediaSequence=" +
                    nextMediaSequence +
                    " | legacyFallbackDuringReconnect=False");
            }
            else
            {
                retryAt = 0f;
                consecutiveFailures = 0;
                sequenceOwnerControlConnectionId =
                    string.Empty;
                nextMediaSequence = 1;
                nextMediaTimestamp100Ns = 0;
                RestoreLegacyOutboundMedia();

                Debug.Log(
                    "VOICE_WINDOWS_WEBGL_PARITY_RESET=PASS" +
                    " | reason=" +
                    Safe(reason) +
                    " | sequencePreserved=False" +
                    " | controlPathReset=True");
            }
        }

        private void ClearRuntimeReferences()
        {
            runtime = null;
            microphonePublisher = null;
            playbackManager = null;
            sessionsField = null;
            outboundSchedulerField = null;
            lastPublishedSequenceField = null;
            firstVoiceFrameSendLoggedField = null;
            legacyFrameHandlerMethod = null;
            legacyFrameHandler = null;
            peerResolverByType.Clear();
        }

        private void OnDestroy()
        {
            ResetMediaSession(
                "adapter_destroyed",
                false);

            ClearRuntimeReferences();
        }

        private static long MonotonicMs()
        {
            return (long)(
                Stopwatch.GetTimestamp() *
                1000.0 /
                Stopwatch.Frequency);
        }

        private static string Safe(
            string value)
        {
            return string.IsNullOrWhiteSpace(
                       value)
                ? string.Empty
                : value.Trim();
        }

        private sealed class InboundFrame
        {
            public readonly VoiceMediaV2Packet Packet;
            public readonly long FirstSeenMs;

            public InboundFrame(
                VoiceMediaV2Packet packet,
                long firstSeenMs)
            {
                Packet = packet;
                FirstSeenMs = firstSeenMs;
            }
        }
    }

    internal sealed class VoiceWindowsMediaWebGLParitySession :
        IDisposable
    {
        private const int MediaQueueCapacity = 6;
        private static readonly byte[] PingPayload =
            { 14, 2, 1, 4 };

        private readonly IVoiceMediaTransportV2 transport;
        private readonly ConcurrentQueue<OutgoingItem> outbound =
            new ConcurrentQueue<OutgoingItem>();
        private readonly SemaphoreSlim outboundSignal =
            new SemaphoreSlim(0, int.MaxValue);
        private readonly object receiveStateSync =
            new object();

        private CancellationTokenSource lifetimeCts;
        private Task senderTask;
        private TaskCompletionSource<VoiceMediaV2BindResult>
            bindCompletion;

        private uint nextTransportSequence = 1;
        private uint lastReceivedTransportSequence;
        private uint receivedAckMask;
        private bool hasReceivedTransportSequence;
        private int queuedMediaCount;
        private int pingPending;
        private bool disposed;
        private ulong firstMediaTimestamp100Ns;
        private long firstMediaStopwatchTicks;
        private bool pacingAnchorReady;
        private long lastInboundStopwatchTicks;

        public event Action<VoiceMediaV2Packet> MediaReceived;
        public event Action PongReceived;
        public event Action<string> Failed;
        public event Action<string> Disconnected;

        public bool IsBound { get; private set; }
        public string StreamId { get; private set; } =
            string.Empty;
        public int PongCount { get; private set; }
        public long SentMediaFrames { get; private set; }
        public long DroppedMediaFrames { get; private set; }

        public double InboundAgeSeconds
        {
            get
            {
                long last =
                    Interlocked.Read(
                        ref lastInboundStopwatchTicks);

                if (last <= 0)
                    return 0d;

                return
                    (Stopwatch.GetTimestamp() -
                     last) /
                    (double)Stopwatch.Frequency;
            }
        }

        public VoiceWindowsMediaWebGLParitySession(
            IVoiceMediaTransportV2 transport)
        {
            this.transport =
                transport ??
                throw new ArgumentNullException(
                    nameof(transport));

            this.transport.PacketReceived +=
                HandlePacketReceived;
            this.transport.Failed +=
                HandleTransportFailure;
            this.transport.Disconnected +=
                HandleTransportDisconnected;
        }

        public async Task<bool> ConnectAndBindAsync(
            string endpoint,
            string accessToken,
            string roomId,
            string controlConnectionId,
            string streamId,
            byte platform,
            string clientBuild,
            CancellationToken cancellationToken)
        {
            if (disposed)
                return false;

            if (!Guid.TryParse(
                    controlConnectionId,
                    out _) ||
                !Guid.TryParse(
                    streamId,
                    out _) ||
                string.IsNullOrWhiteSpace(accessToken) ||
                string.IsNullOrWhiteSpace(roomId))
            {
                return false;
            }

            StreamId =
                streamId.Trim().ToLowerInvariant();
            IsBound = false;
            nextTransportSequence = 1;
            hasReceivedTransportSequence = false;
            lastReceivedTransportSequence = 0;
            receivedAckMask = 0;
            pacingAnchorReady = false;
            PongCount = 0;
            Interlocked.Exchange(
                ref lastInboundStopwatchTicks,
                Stopwatch.GetTimestamp());

            lifetimeCts?.Cancel();
            lifetimeCts?.Dispose();
            lifetimeCts =
                CancellationTokenSource
                    .CreateLinkedTokenSource(
                        cancellationToken);

            if (!await transport.ConnectAsync(
                    endpoint,
                    lifetimeCts.Token))
            {
                return false;
            }

            senderTask =
                Task.Run(
                    () =>
                        SenderLoopAsync(
                            lifetimeCts.Token));

            bindCompletion =
                new TaskCompletionSource<
                    VoiceMediaV2BindResult>(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);

            byte[] bindPayload =
                VoiceMediaV2BindPayload
                    .EncodeRequest(
                        accessToken,
                        roomId,
                        controlConnectionId,
                        platform,
                        clientBuild);

            bool bindSent =
                await SendImmediateAsync(
                    new OutgoingItem(
                        VoiceMediaV2PacketKind.BindRequest,
                        VoiceMediaV2Codec.None,
                        VoiceMediaV2Flags.None,
                        0,
                        0,
                        StreamId,
                        bindPayload,
                        false),
                    lifetimeCts.Token);

            if (!bindSent)
                return false;

            Task timeout =
                Task.Delay(
                    VoiceWindowsMediaConnectionPolicy
                        .BindTimeoutMs,
                    lifetimeCts.Token);

            Task completed =
                await Task.WhenAny(
                    bindCompletion.Task,
                    timeout);

            if (completed != bindCompletion.Task)
            {
                Failed?.Invoke(
                    "Voice media bind timed out.");
                return false;
            }

            VoiceMediaV2BindResult result =
                await bindCompletion.Task;

            if (!result.Success ||
                !string.Equals(
                    result.ControlConnectionId,
                    controlConnectionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                Failed?.Invoke(
                    "Voice media bind was rejected: " +
                    result.Message);
                return false;
            }

            IsBound = true;
            Interlocked.Exchange(
                ref lastInboundStopwatchTicks,
                Stopwatch.GetTimestamp());

            return true;
        }

        public bool TryQueueMedia(
            uint mediaSequence,
            ulong mediaTimestamp100Ns,
            byte[] payload,
            VoiceMediaV2Flags flags)
        {
            if (!IsBound ||
                disposed ||
                payload == null ||
                payload.Length == 0 ||
                payload.Length >
                    VoiceMediaV2Constants
                        .MaximumPayloadBytes)
            {
                return false;
            }

            while (true)
            {
                int current =
                    Volatile.Read(
                        ref queuedMediaCount);

                if (current >= MediaQueueCapacity)
                {
                    DroppedMediaFrames += 1;
                    return false;
                }

                if (Interlocked.CompareExchange(
                        ref queuedMediaCount,
                        current + 1,
                        current) == current)
                {
                    break;
                }
            }

            outbound.Enqueue(
                new OutgoingItem(
                    VoiceMediaV2PacketKind.Media,
                    VoiceMediaV2Codec.Opus,
                    flags,
                    mediaSequence,
                    mediaTimestamp100Ns,
                    StreamId,
                    payload,
                    true));

            outboundSignal.Release();
            return true;
        }

        public bool TryQueuePing()
        {
            if (!IsBound || disposed)
                return false;

            if (Interlocked.CompareExchange(
                    ref pingPending,
                    1,
                    0) != 0)
            {
                return true;
            }

            outbound.Enqueue(
                new OutgoingItem(
                    VoiceMediaV2PacketKind.Ping,
                    VoiceMediaV2Codec.None,
                    VoiceMediaV2Flags.None,
                    0,
                    0,
                    StreamId,
                    PingPayload,
                    false));

            outboundSignal.Release();
            return true;
        }

        private async Task<bool> SendImmediateAsync(
            OutgoingItem item,
            CancellationToken cancellationToken)
        {
            item.Completion =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);

            outbound.Enqueue(item);
            outboundSignal.Release();

            using (cancellationToken.Register(
                       () =>
                           item.Completion
                               .TrySetCanceled()))
            {
                try
                {
                    return await item.Completion.Task;
                }
                catch (TaskCanceledException)
                {
                    return false;
                }
            }
        }

        private async Task SenderLoopAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken
                           .IsCancellationRequested)
                {
                    await outboundSignal.WaitAsync(
                        cancellationToken);

                    while (outbound.TryDequeue(
                               out OutgoingItem item))
                    {
                        if (item.IsMedia)
                        {
                            Interlocked.Decrement(
                                ref queuedMediaCount);

                            await PaceAsync(
                                item.MediaTimestamp100Ns,
                                cancellationToken);
                        }
                        else if (item.Kind ==
                                 VoiceMediaV2PacketKind.Ping)
                        {
                            Interlocked.Exchange(
                                ref pingPending,
                                0);
                        }

                        VoiceMediaV2Packet packet =
                            CreatePacket(item);

                        bool sent =
                            await transport.SendAsync(
                                packet.Encode(),
                                cancellationToken);

                        item.Completion?.
                            TrySetResult(sent);

                        if (!sent)
                        {
                            Failed?.Invoke(
                                "Voice media send failed.");

                            return;
                        }

                        if (item.IsMedia)
                            SentMediaFrames += 1;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Failed?.Invoke(
                    "Voice media sender failed: " +
                    exception.Message);
            }
        }

        private async Task PaceAsync(
            ulong mediaTimestamp100Ns,
            CancellationToken cancellationToken)
        {
            if (!pacingAnchorReady)
            {
                firstMediaTimestamp100Ns =
                    mediaTimestamp100Ns;
                firstMediaStopwatchTicks =
                    Stopwatch.GetTimestamp();
                pacingAnchorReady = true;
                return;
            }

            ulong mediaDelta100Ns =
                mediaTimestamp100Ns >=
                    firstMediaTimestamp100Ns
                    ? mediaTimestamp100Ns -
                      firstMediaTimestamp100Ns
                    : 0;

            double targetSeconds =
                mediaDelta100Ns / 10000000.0;

            double elapsedSeconds =
                (Stopwatch.GetTimestamp() -
                 firstMediaStopwatchTicks) /
                (double)Stopwatch.Frequency;

            double remainingMs =
                (targetSeconds -
                 elapsedSeconds) *
                1000.0;

            if (remainingMs > 1.0)
            {
                await Task.Delay(
                    (int)Math.Min(
                        remainingMs,
                        100.0),
                    cancellationToken);
            }
        }

        private VoiceMediaV2Packet CreatePacket(
            OutgoingItem item)
        {
            uint ackSequence;
            uint ackMask;

            lock (receiveStateSync)
            {
                ackSequence =
                    hasReceivedTransportSequence
                        ? lastReceivedTransportSequence
                        : 0;

                ackMask =
                    receivedAckMask;
            }

            uint transportSequence =
                nextTransportSequence;

            nextTransportSequence =
                transportSequence ==
                    uint.MaxValue
                    ? 1
                    : transportSequence + 1;

            return new VoiceMediaV2Packet
            {
                Kind = item.Kind,
                Codec = item.Codec,
                Flags = item.Flags,
                TransportSequence =
                    transportSequence,
                MediaSequence =
                    item.MediaSequence,
                MediaTimestamp100Ns =
                    item.MediaTimestamp100Ns,
                AckTransportSequence =
                    ackSequence,
                AckMask = ackMask,
                SessionId =
                    VoiceMediaV2Constants.EmptyUuid,
                StreamId =
                    item.StreamId,
                SenderId =
                    VoiceMediaV2Constants.EmptyUuid,
                SecurityContextId = 0,
                Payload =
                    item.Payload ??
                    Array.Empty<byte>()
            };
        }

        private void HandlePacketReceived(
            byte[] bytes)
        {
            try
            {
                VoiceMediaV2Packet packet =
                    VoiceMediaV2Packet.Decode(
                        bytes);

                Interlocked.Exchange(
                    ref lastInboundStopwatchTicks,
                    Stopwatch.GetTimestamp());

                ObserveTransportSequence(
                    packet.TransportSequence);

                if (packet.Kind ==
                    VoiceMediaV2PacketKind.BindResult)
                {
                    bindCompletion?.
                        TrySetResult(
                            VoiceMediaV2BindPayload
                                .DecodeResult(
                                    packet.Payload));
                    return;
                }

                if (packet.Kind ==
                    VoiceMediaV2PacketKind.Pong)
                {
                    PongCount += 1;
                    PongReceived?.Invoke();
                    return;
                }

                if (packet.Kind ==
                    VoiceMediaV2PacketKind.Media)
                {
                    MediaReceived?.Invoke(packet);
                }
            }
            catch (Exception exception)
            {
                Failed?.Invoke(
                    "Voice media packet decode failed: " +
                    exception.Message);
            }
        }

        private void ObserveTransportSequence(
            uint sequence)
        {
            lock (receiveStateSync)
            {
                if (!hasReceivedTransportSequence)
                {
                    hasReceivedTransportSequence = true;
                    lastReceivedTransportSequence =
                        sequence;
                    receivedAckMask = 0;
                    return;
                }

                if (sequence >
                    lastReceivedTransportSequence)
                {
                    uint delta =
                        sequence -
                        lastReceivedTransportSequence;

                    receivedAckMask =
                        delta >= 32
                            ? 0u
                            : (receivedAckMask <<
                               (int)delta) |
                              (1u <<
                               ((int)delta - 1));

                    lastReceivedTransportSequence =
                        sequence;
                    return;
                }

                uint distance =
                    lastReceivedTransportSequence -
                    sequence;

                if (distance > 0 &&
                    distance <= 32)
                {
                    receivedAckMask |=
                        1u <<
                        ((int)distance - 1);
                }
            }
        }

        private void HandleTransportFailure(
            string message)
        {
            Failed?.Invoke(message);
        }

        private void HandleTransportDisconnected(
            string reason)
        {
            IsBound = false;
            Disconnected?.Invoke(reason);
        }

        public void Dispose()
        {
            if (disposed) return;

            disposed = true;
            IsBound = false;

            try { lifetimeCts?.Cancel(); }
            catch { }

            transport.PacketReceived -=
                HandlePacketReceived;
            transport.Failed -=
                HandleTransportFailure;
            transport.Disconnected -=
                HandleTransportDisconnected;

            try { transport.Dispose(); }
            catch { }

            lifetimeCts?.Dispose();
            lifetimeCts = null;

            while (outbound.TryDequeue(
                       out OutgoingItem pending))
            {
                pending.Completion?.
                    TrySetCanceled();
            }

            try { outboundSignal.Dispose(); }
            catch { }
        }

        private sealed class OutgoingItem
        {
            public readonly VoiceMediaV2PacketKind Kind;
            public readonly VoiceMediaV2Codec Codec;
            public readonly VoiceMediaV2Flags Flags;
            public readonly uint MediaSequence;
            public readonly ulong MediaTimestamp100Ns;
            public readonly string StreamId;
            public readonly byte[] Payload;
            public readonly bool IsMedia;
            public TaskCompletionSource<bool> Completion;

            public OutgoingItem(
                VoiceMediaV2PacketKind kind,
                VoiceMediaV2Codec codec,
                VoiceMediaV2Flags flags,
                uint mediaSequence,
                ulong mediaTimestamp100Ns,
                string streamId,
                byte[] payload,
                bool isMedia)
            {
                Kind = kind;
                Codec = codec;
                Flags = flags;
                MediaSequence = mediaSequence;
                MediaTimestamp100Ns =
                    mediaTimestamp100Ns;
                StreamId =
                    string.IsNullOrWhiteSpace(
                        streamId)
                        ? VoiceMediaV2Constants
                            .EmptyUuid
                        : streamId;
                Payload =
                    payload ??
                    Array.Empty<byte>();
                IsMedia = isMedia;
            }
        }
    }
}
#endif
