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

namespace Network_A.Voice.Client.MediaV2.Windows
{
    public static class VoiceWindowsMediaRuntimeInstaller
    {
        private const string AdapterRootName = "Voice_Windows_Media_Runtime_Adapter";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (IsIsolatedLiveProbe())
            {
                Debug.Log(
                    "VOICE_WINDOWS_MEDIA_RUNTIME_ADAPTER=SKIPPED" +
                    " | reason=isolated_live_probe");
                return;
            }

            GameObject root = GameObject.Find(AdapterRootName);
            if (root == null) root = new GameObject(AdapterRootName);
            UnityEngine.Object.DontDestroyOnLoad(root);

            if (root.GetComponent<VoiceWindowsMediaRuntimeAdapter>() == null)
                root.AddComponent<VoiceWindowsMediaRuntimeAdapter>();

            Debug.Log(
                "VOICE_WINDOWS_MEDIA_RUNTIME_ADAPTER=READY" +
                " | platformScope=Windows" +
                " | productionIntegration=True" +
                " | webglPathChanged=False" +
                " | questPathChanged=False");
        }

        private static bool IsIsolatedLiveProbe()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int index = 0; index < args.Length; index++)
            {
                string value = args[index] ?? string.Empty;
                if (value.StartsWith("--vme2-", StringComparison.OrdinalIgnoreCase) &&
                    value.IndexOf("live-test", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public sealed class VoiceWindowsMediaRuntimeAdapter : MonoBehaviour
    {
        private const string RuntimeRootName = "Voice_Client_Runtime_Root";
        private const float RetryDelaySeconds = 2f;
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
        private VoiceMediaV2ClientSession mediaSession;
        private CancellationTokenSource mediaConnectCts;

        private FieldInfo sessionsField;
        private FieldInfo outboundSchedulerField;
        private FieldInfo lastPublishedSequenceField;
        private FieldInfo firstVoiceFrameSendLoggedField;
        private MethodInfo legacyFrameHandlerMethod;
        private Action<byte[], bool> legacyFrameHandler;

        private string boundControlConnectionId = string.Empty;
        private string boundRoomId = string.Empty;
        private string mediaStreamId = string.Empty;
        private float retryAt;
        private bool mediaConnectStarted;
        private bool mediaBound;
        private bool ownsOutboundMedia;
        private bool firstOutboundFrameLogged;
        private bool firstInboundFrameLogged;
        private int resetRequested;
        private string resetReason = string.Empty;
        private int pendingInboundCount;
        private uint nextMediaSequence = 1;
        private ulong nextMediaTimestamp100Ns;

        private void Update()
        {
            if (Interlocked.Exchange(ref resetRequested, 0) != 0)
            {
                ResetMediaSession(
                    string.IsNullOrWhiteSpace(resetReason)
                        ? "media_callback_reset"
                        : resetReason);
            }

            ResolveRuntime();
            if (runtime == null)
            {
                if (mediaSession != null || ownsOutboundMedia)
                    ResetMediaSession("runtime_missing");
                return;
            }

            DrainInboundFrames();
            DrainDeferredFrames();

            if (!runtime.IsAuthenticated ||
                !Guid.TryParse(runtime.VoiceConnectionId, out _))
            {
                if (mediaSession != null || mediaConnectStarted || ownsOutboundMedia)
                    ResetMediaSession("control_not_authenticated");
                return;
            }

            string currentConnectionId = runtime.VoiceConnectionId.Trim().ToLowerInvariant();
            string currentRoomId = (MetaverseNetworkClient.roomId ?? string.Empty).Trim();

            if (boundControlConnectionId.Length > 0 &&
                !string.Equals(
                    boundControlConnectionId,
                    currentConnectionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                ResetMediaSession("control_connection_changed");
                return;
            }

            if (boundRoomId.Length > 0 &&
                currentRoomId.Length > 0 &&
                !string.Equals(
                    boundRoomId,
                    currentRoomId,
                    StringComparison.Ordinal))
            {
                ResetMediaSession("room_changed");
                return;
            }

            if (!mediaBound &&
                !mediaConnectStarted &&
                Time.realtimeSinceStartup >= retryAt)
            {
                StartMediaConnection(currentConnectionId, currentRoomId);
            }
        }

        private void ResolveRuntime()
        {
            if (runtime != null)
            {
                GameObject currentRoot = GameObject.Find(RuntimeRootName);
                if (currentRoot != null &&
                    ReferenceEquals(
                        currentRoot.GetComponent<VoiceClientRuntime>(),
                        runtime))
                {
                    return;
                }

                ResetMediaSession("runtime_replaced");
                ClearRuntimeReferences();
            }

            GameObject root = GameObject.Find(RuntimeRootName);
            if (root == null) return;

            VoiceClientRuntime candidate = root.GetComponent<VoiceClientRuntime>();
            if (candidate == null) return;

            runtime = candidate;
            microphonePublisher = root.GetComponent<VoiceMicrophonePublisher>();
            playbackManager = root.GetComponent<VoiceSpatialPlaybackManager>();

            Type runtimeType = typeof(VoiceClientRuntime);
            const BindingFlags instancePrivate =
                BindingFlags.Instance | BindingFlags.NonPublic;

            sessionsField = runtimeType.GetField("sessions", instancePrivate);
            outboundSchedulerField = runtimeType.GetField("outboundScheduler", instancePrivate);
            lastPublishedSequenceField = runtimeType.GetField("lastPublishedSequence", instancePrivate);
            firstVoiceFrameSendLoggedField = runtimeType.GetField("firstVoiceFrameSendLogged", instancePrivate);
            legacyFrameHandlerMethod = runtimeType.GetMethod(
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
                        (Action<byte[], bool>)Delegate.CreateDelegate(
                            typeof(Action<byte[], bool>),
                            runtime,
                            legacyFrameHandlerMethod);
                }
                catch (Exception exception)
                {
                    Debug.LogError(
                        "VOICE_WINDOWS_MEDIA_RUNTIME_REFLECTION=FAIL" +
                        " | target=HandleFrameEncoded" +
                        " | error=" + Safe(exception.Message));
                }
            }

            Debug.Log(
                "VOICE_WINDOWS_MEDIA_RUNTIME_DISCOVERED=PASS" +
                " | runtime=True" +
                " | microphonePublisher=" + (microphonePublisher != null) +
                " | playbackManager=" + (playbackManager != null) +
                " | sessionsField=" + (sessionsField != null) +
                " | legacyFrameHandler=" + (legacyFrameHandler != null));
        }

        private async void StartMediaConnection(
            string controlConnectionId,
            string roomId)
        {
            if (mediaConnectStarted || mediaBound || runtime == null) return;
            if (!Guid.TryParse(controlConnectionId, out _) ||
                string.IsNullOrWhiteSpace(roomId))
            {
                retryAt = Time.realtimeSinceStartup + RetryDelaySeconds;
                return;
            }

            string accessToken = (SecureTokenStorage.GetAccessToken() ?? string.Empty).Trim();
            if (accessToken.Length == 0)
            {
                retryAt = Time.realtimeSinceStartup + RetryDelaySeconds;
                return;
            }

            mediaConnectStarted = true;
            mediaConnectCts?.Cancel();
            mediaConnectCts?.Dispose();
            mediaConnectCts = new CancellationTokenSource();

            IVoiceMediaTransportV2 transport = null;
            VoiceMediaV2ClientSession createdSession = null;

            try
            {
                transport = new VoiceGrpcMediaTransportV2();
                createdSession = new VoiceMediaV2ClientSession(transport);
                string streamId = Guid.NewGuid().ToString("D");

                createdSession.MediaReceived += packet =>
                    HandleMediaReceived(createdSession, packet);
                createdSession.Failed += reason =>
                    HandleMediaFailure(createdSession, reason);
                createdSession.Disconnected += reason =>
                    HandleMediaDisconnected(createdSession, reason);

                bool bound = await createdSession.ConnectAndBindAsync(
                    ServerConfig.BuildRealtimeGrpcStreamingTarget(),
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
                    createdSession.Dispose();
                    retryAt = Time.realtimeSinceStartup + RetryDelaySeconds;
                    return;
                }

                mediaSession = createdSession;
                mediaStreamId = streamId;
                boundControlConnectionId = controlConnectionId;
                boundRoomId = roomId;
                mediaBound = true;
                nextMediaSequence = 1;
                nextMediaTimestamp100Ns = 0;
                firstOutboundFrameLogged = false;
                firstInboundFrameLogged = false;

                ClaimOutboundMedia();

                Debug.Log(
                    "VOICE_WINDOWS_MEDIA_PRODUCTION_BIND=PASS" +
                    " | controlConnectionId=" + boundControlConnectionId +
                    " | streamId=" + mediaStreamId +
                    " | transport=VoiceGrpcMediaTransportV2" +
                    " | receiveOnlyBind=True" +
                    " | productionIntegration=True" +
                    " | securityMode=legacyCompatible" +
                    " | webglPathChanged=False");
            }
            catch (OperationCanceledException)
            {
                try { createdSession?.Dispose(); } catch { }
            }
            catch (Exception exception)
            {
                try { createdSession?.Dispose(); } catch { }
                retryAt = Time.realtimeSinceStartup + RetryDelaySeconds;

                Debug.LogWarning(
                    "VOICE_WINDOWS_MEDIA_PRODUCTION_BIND=FAIL" +
                    " | error=" + Safe(exception.Message));
            }
            finally
            {
                mediaConnectStarted = false;
            }
        }

        private void ClaimOutboundMedia()
        {
            if (ownsOutboundMedia || runtime == null) return;

            if (microphonePublisher == null)
                microphonePublisher = runtime.GetComponent<VoiceMicrophonePublisher>();

            if (microphonePublisher == null || legacyFrameHandler == null)
            {
                Debug.LogError(
                    "VOICE_WINDOWS_MEDIA_OUTBOUND_OWNER=FAIL" +
                    " | microphonePublisher=" + (microphonePublisher != null) +
                    " | legacyFrameHandler=" + (legacyFrameHandler != null));
                return;
            }

            microphonePublisher.FrameEncoded -= legacyFrameHandler;
            microphonePublisher.FrameEncoded -= HandleFrameEncoded;
            microphonePublisher.FrameEncoded += HandleFrameEncoded;
            ownsOutboundMedia = true;

            ClearLegacyMediaQueue();

            Debug.Log(
                "VOICE_WINDOWS_MEDIA_OUTBOUND_OWNER=PASS" +
                " | mediaV2OwnsEncodedFrames=True" +
                " | controlVoiceFrameDuplicate=False");
        }

        private void RestoreLegacyOutboundMedia()
        {
            if (!ownsOutboundMedia) return;

            if (microphonePublisher != null)
            {
                microphonePublisher.FrameEncoded -= HandleFrameEncoded;
                if (legacyFrameHandler != null)
                {
                    microphonePublisher.FrameEncoded -= legacyFrameHandler;
                    microphonePublisher.FrameEncoded += legacyFrameHandler;
                }
            }

            ownsOutboundMedia = false;

            Debug.Log(
                "VOICE_WINDOWS_MEDIA_OUTBOUND_OWNER=RESTORED" +
                " | reason=media_v2_unavailable");
        }

        private void ClearLegacyMediaQueue()
        {
            if (runtime == null || outboundSchedulerField == null) return;

            try
            {
                object scheduler = outboundSchedulerField.GetValue(runtime);
                if (scheduler == null) return;

                MethodInfo clearMedia = scheduler.GetType().GetMethod(
                    "ClearMedia",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(string) },
                    null);

                clearMedia?.Invoke(
                    scheduler,
                    new object[] { "windows_media_v2_activated" });
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "VOICE_WINDOWS_MEDIA_LEGACY_QUEUE_CLEAR=FAIL" +
                    " | error=" + Safe(exception.Message));
            }
        }

        private void HandleFrameEncoded(byte[] packet, bool dtx)
        {
            VoiceMediaV2ClientSession session = mediaSession;
            VoiceClientRuntime activeRuntime = runtime;

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

            uint mediaSequence = nextMediaSequence;
            ulong mediaTimestamp100Ns = nextMediaTimestamp100Ns;

            bool queued = session.TryQueueMedia(
                mediaSequence,
                mediaTimestamp100Ns,
                packet,
                VoiceMediaV2Flags.None);

            if (!queued) return;

            nextMediaSequence = mediaSequence == uint.MaxValue
                ? 1
                : mediaSequence + 1;
            nextMediaTimestamp100Ns = mediaTimestamp100Ns + OpusFrameDuration100Ns;

            MirrorPublishedSequence(mediaSequence);

            if (!firstOutboundFrameLogged)
            {
                firstOutboundFrameLogged = true;
                Debug.Log(
                    "VOICE_WINDOWS_MEDIA_FIRST_FRAME_SENT=PASS" +
                    " | mediaSequence=" + mediaSequence +
                    " | bytes=" + packet.Length +
                    " | dtx=" + dtx +
                    " | path=MediaV2" +
                    " | controlVoiceFrameDuplicate=False");
            }
        }

        private void MirrorPublishedSequence(uint sequence)
        {
            if (runtime == null) return;

            try
            {
                lastPublishedSequenceField?.SetValue(runtime, sequence);
                firstVoiceFrameSendLoggedField?.SetValue(runtime, true);
            }
            catch { }
        }

        private void HandleMediaReceived(
            VoiceMediaV2ClientSession source,
            VoiceMediaV2Packet packet)
        {
            if (!ReferenceEquals(mediaSession, source) || packet == null) return;
            if (packet.Kind != VoiceMediaV2PacketKind.Media ||
                packet.Codec != VoiceMediaV2Codec.Opus ||
                packet.Payload == null ||
                packet.Payload.Length == 0)
            {
                return;
            }

            int pending = Interlocked.Increment(ref pendingInboundCount);
            if (pending > MaximumInboundFrames)
            {
                if (inboundFrames.TryDequeue(out _))
                    Interlocked.Decrement(ref pendingInboundCount);
            }

            inboundFrames.Enqueue(
                new InboundFrame(packet, MonotonicMs()));
        }

        private void DrainInboundFrames()
        {
            int processed = 0;
            while (processed < MaximumInboundFrames &&
                   inboundFrames.TryDequeue(out InboundFrame frame))
            {
                Interlocked.Decrement(ref pendingInboundCount);
                processed++;

                if (!TryDeliverInboundFrame(frame) &&
                    MonotonicMs() - frame.FirstSeenMs <= DeferredFrameMaxAgeMs &&
                    deferredFrames.Count < MaximumDeferredFrames)
                {
                    deferredFrames.Add(frame);
                }
            }
        }

        private void DrainDeferredFrames()
        {
            if (deferredFrames.Count == 0) return;

            long now = MonotonicMs();
            for (int index = deferredFrames.Count - 1; index >= 0; index--)
            {
                InboundFrame frame = deferredFrames[index];
                if (TryDeliverInboundFrame(frame) ||
                    now - frame.FirstSeenMs > DeferredFrameMaxAgeMs)
                {
                    deferredFrames.RemoveAt(index);
                }
            }
        }

        private bool TryDeliverInboundFrame(InboundFrame frame)
        {
            if (runtime == null || playbackManager == null || frame.Packet == null)
                return false;

            string sessionId = Safe(frame.Packet.SessionId);
            string senderConnectionId = Safe(frame.Packet.SenderId);

            if (!Guid.TryParse(sessionId, out _) ||
                !Guid.TryParse(senderConnectionId, out _))
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

            string peerUserId;
            if (!TryResolvePeerUserId(
                    sessionId,
                    senderConnectionId,
                    out peerUserId))
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
                    "VOICE_WINDOWS_MEDIA_FIRST_REMOTE_FRAME=PASS" +
                    " | sessionId=" + sessionId +
                    " | senderConnectionId=" + senderConnectionId +
                    " | peerUserId=" + peerUserId +
                    " | bytes=" + frame.Packet.Payload.Length +
                    " | path=MediaV2");
            }

            return true;
        }

        private bool TryResolvePeerUserId(
            string sessionId,
            string senderConnectionId,
            out string peerUserId)
        {
            peerUserId = string.Empty;
            if (runtime == null || sessionsField == null) return false;

            try
            {
                IDictionary sessions = sessionsField.GetValue(runtime) as IDictionary;
                if (sessions == null || !sessions.Contains(sessionId)) return false;

                object activeSession = sessions[sessionId];
                if (activeSession == null) return false;

                Type activeSessionType = activeSession.GetType();
                if (!peerResolverByType.TryGetValue(
                        activeSessionType,
                        out MethodInfo resolver))
                {
                    resolver = activeSessionType.GetMethod(
                        "TryResolvePeerUserId",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new[] { typeof(string), typeof(string).MakeByRefType() },
                        null);
                    peerResolverByType[activeSessionType] = resolver;
                }

                if (resolver == null) return false;

                object[] arguments = { senderConnectionId, null };
                bool resolved = (bool)resolver.Invoke(activeSession, arguments);
                if (!resolved) return false;

                peerUserId = Safe(arguments[1] as string);
                return peerUserId.Length > 0;
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "VOICE_WINDOWS_MEDIA_PEER_RESOLVE=FAIL" +
                    " | sessionId=" + sessionId +
                    " | senderConnectionId=" + senderConnectionId +
                    " | error=" + Safe(exception.Message));
                return false;
            }
        }

        private void HandleMediaFailure(
            VoiceMediaV2ClientSession source,
            string reason)
        {
            if (!ReferenceEquals(mediaSession, source)) return;
            resetReason = "media_failed_" + Safe(reason);
            Interlocked.Exchange(ref resetRequested, 1);
        }

        private void HandleMediaDisconnected(
            VoiceMediaV2ClientSession source,
            string reason)
        {
            if (!ReferenceEquals(mediaSession, source)) return;
            resetReason = "media_disconnected_" + Safe(reason);
            Interlocked.Exchange(ref resetRequested, 1);
        }

        private void ResetMediaSession(string reason)
        {
            RestoreLegacyOutboundMedia();

            mediaConnectCts?.Cancel();
            mediaConnectCts?.Dispose();
            mediaConnectCts = null;

            VoiceMediaV2ClientSession previous = mediaSession;
            mediaSession = null;
            mediaBound = false;
            mediaConnectStarted = false;
            boundControlConnectionId = string.Empty;
            boundRoomId = string.Empty;
            mediaStreamId = string.Empty;
            firstOutboundFrameLogged = false;
            firstInboundFrameLogged = false;
            retryAt = Time.realtimeSinceStartup + RetryDelaySeconds;

            try { previous?.Dispose(); } catch { }

            while (inboundFrames.TryDequeue(out _))
                Interlocked.Decrement(ref pendingInboundCount);
            deferredFrames.Clear();

            Debug.Log(
                "VOICE_WINDOWS_MEDIA_PRODUCTION_RESET=PASS" +
                " | reason=" + Safe(reason) +
                " | legacyOutboundRestored=" + (!ownsOutboundMedia));
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
            ResetMediaSession("adapter_destroyed");
            ClearRuntimeReferences();
        }

        private static long MonotonicMs()
        {
            return (long)(
                Stopwatch.GetTimestamp() * 1000.0 /
                Stopwatch.Frequency);
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value)
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
}
#endif
