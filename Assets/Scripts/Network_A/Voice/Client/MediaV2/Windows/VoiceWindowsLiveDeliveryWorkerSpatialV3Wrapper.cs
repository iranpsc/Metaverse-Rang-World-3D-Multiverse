#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Network_A.Voice.Client.Capture;
using Network_A.Voice.Client.Codec;
using Network_A.Voice.Client.Playback;
using Network_A.Voice.Client.Runtime;
using Network_A.Voice.Client.Spatial;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Network_A.Voice.Client.MediaV2.Windows
{
    public static class VoiceWindowsLiveDeliveryWorkerSpatialV3Installer
    {
        private const string RootName = "Voice_Windows_Live_Delivery_Worker_Spatial_V3";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject root = GameObject.Find(RootName);
            if (root == null) root = new GameObject(RootName);
            UnityEngine.Object.DontDestroyOnLoad(root);

            if (root.GetComponent<VoiceWindowsLiveDeliveryWorkerSpatialV3Wrapper>() == null)
                root.AddComponent<VoiceWindowsLiveDeliveryWorkerSpatialV3Wrapper>();

            Debug.Log(
                "VOICE_WINDOWS_WORKER_SPATIAL_V3=READY" +
                " | platform=Windows" +
                " | wrapperOnly=True" +
                " | directMediaToWorker=True" +
                " | workerPumpMs=5" +
                " | audioThreadPlayback=True" +
                " | unityStreamingPcmCallback=True" +
                " | unityMainThreadAudioTiming=False" +
                " | minPlayoutDelayMs=40" +
                " | maxPlayoutDelayMs=200" +
                " | maxRemoteFrames=64" +
                " | maxPlaybackFrames=50" +
                " | stableFilesChanged=False" +
                " | webglPathChanged=False" +
                " | questPathChanged=False" +
                " | outboundOwner=VoiceWindowsMediaWebGLParityRuntimeAdapter" +
                " | outboundInterception=False");
        }
    }

    [DefaultExecutionOrder(-31999)]
    [DisallowMultipleComponent]
    public sealed class VoiceWindowsLiveDeliveryWorkerSpatialV3Wrapper : MonoBehaviour
    {
        private const string RuntimeRootName = "Voice_Client_Runtime_Root";
        private const string PreviousRootName = "Voice_Windows_Live_Delivery_Worker_Parity";
        private const float SpatialRefreshSeconds = 1f;

        private readonly Dictionary<string, PlaybackBinding> playbackBindings =
            new Dictionary<string, PlaybackBinding>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Transform> peerTransformByUserId =
            new Dictionary<string, Transform>(StringComparer.Ordinal);
        private readonly Dictionary<Type, MethodInfo> peerResolverByType =
            new Dictionary<Type, MethodInfo>();

        private VoiceClientRuntime runtime;
        private VoiceMicrophonePublisher microphonePublisher;
        private VoiceSpatialPlaybackManager stablePlaybackManager;
        private VoiceWindowsMediaWebGLParityRuntimeAdapter sourceAdapter;
        private VoiceWindowsMediaWebGLParitySession subscribedSession;
        private VoiceWindowsLiveDeliveryWorkerSpatialV3 worker;
        private MonoBehaviour previousWrapper;

        private FieldInfo sourcePlaybackManagerField;
        private FieldInfo sourceMediaSessionField;
        private FieldInfo sourceMediaBoundField;
        private FieldInfo sourceOwnsOutboundMediaField;
        private FieldInfo sourceNextMediaSequenceField;
        private FieldInfo sourceNextMediaTimestampField;
        private FieldInfo runtimeSessionsField;
        private FieldInfo runtimeLastPublishedSequenceField;
        private FieldInfo runtimeFirstVoiceFrameSendLoggedField;
        private MethodInfo sourceFrameHandlerMethod;
        private Action<byte[], bool> sourceFrameHandler;
        private Action<byte[], bool> wrapperFrameHandler;

        private bool outboundIntercepted;
        private bool sourceInterceptedLogged;
        private bool previousWrapperDisabledLogged;
        private bool disposed;
        private double nextStatsAtMs;
        private long outboundQueuedFrames;
        private long outboundBackpressureDrops;
        private bool firstAudioThreadPlaybackLogged;

        private void Awake()
        {
            worker = new VoiceWindowsLiveDeliveryWorkerSpatialV3();
            worker.Start();
        }

        private void Update()
        {
            if (disposed) return;

            DisablePreviousWrapper();
            ResolveRuntime();
            if (worker != null)
                worker.LocalConnectionId = runtime != null ? Safe(runtime.VoiceConnectionId) : string.Empty;
            ResolveSourceAdapter();
            InterceptSourcePlayback();
            BindDirectMediaSession();
            DrainWorkerNotices();
            RefreshPlaybackBindings();
            SyncSpeakerState();
            ReportStats();
        }

        private void DisablePreviousWrapper()
        {
            if (previousWrapper == null)
            {
                GameObject root = GameObject.Find(PreviousRootName);
                if (root != null)
                {
                    MonoBehaviour[] behaviours = root.GetComponents<MonoBehaviour>();
                    for (int index = 0; index < behaviours.Length; index++)
                    {
                        MonoBehaviour candidate = behaviours[index];
                        if (candidate == null) continue;
                        if (!string.Equals(
                                candidate.GetType().FullName,
                                "Network_A.Voice.Client.MediaV2.Windows.VoiceWindowsLiveDeliveryWorkerParityWrapper",
                                StringComparison.Ordinal))
                        {
                            continue;
                        }

                        previousWrapper = candidate;
                        break;
                    }
                }
            }

            if (previousWrapper == null || !previousWrapper.enabled) return;

            previousWrapper.enabled = false;
            if (previousWrapperDisabledLogged) return;

            previousWrapperDisabledLogged = true;
            Debug.Log(
                "VOICE_WINDOWS_WORKER_SPATIAL_V3_PREVIOUS_WRAPPER_DISABLED=PASS" +
                " | previous=VoiceWindowsLiveDeliveryWorkerParityWrapper" +
                " | sourceFileChanged=False");
        }

        private void ResolveRuntime()
        {
            GameObject root = GameObject.Find(RuntimeRootName);
            VoiceClientRuntime candidate =
                root != null ? root.GetComponent<VoiceClientRuntime>() : null;
            VoiceMicrophonePublisher candidatePublisher =
                root != null ? root.GetComponent<VoiceMicrophonePublisher>() : null;

            if (ReferenceEquals(runtime, candidate) &&
                ReferenceEquals(microphonePublisher, candidatePublisher))
            {
                return;
            }

            if (outboundIntercepted &&
                !ReferenceEquals(microphonePublisher, candidatePublisher))
            {
                RestoreOutboundTimelineOwnership();
            }

            runtime = candidate;
            microphonePublisher = candidatePublisher;
            stablePlaybackManager =
                root != null ? root.GetComponent<VoiceSpatialPlaybackManager>() : null;

            if (worker != null)
            {
                worker.LocalConnectionId =
                    runtime != null ? Safe(runtime.VoiceConnectionId) : string.Empty;
            }

            Type runtimeType = typeof(VoiceClientRuntime);
            const BindingFlags privateInstance =
                BindingFlags.Instance | BindingFlags.NonPublic;

            runtimeSessionsField = runtime == null
                ? null
                : runtimeType.GetField("sessions", privateInstance);
            runtimeLastPublishedSequenceField = runtime == null
                ? null
                : runtimeType.GetField("lastPublishedSequence", privateInstance);
            runtimeFirstVoiceFrameSendLoggedField = runtime == null
                ? null
                : runtimeType.GetField("firstVoiceFrameSendLogged", privateInstance);

            peerResolverByType.Clear();
            peerTransformByUserId.Clear();
        }

        private void ResolveSourceAdapter()
        {
            if (sourceAdapter != null) return;

            sourceAdapter = UnityEngine.Object.FindObjectOfType<
                VoiceWindowsMediaWebGLParityRuntimeAdapter>();
            if (sourceAdapter == null) return;

            Type sourceType = typeof(VoiceWindowsMediaWebGLParityRuntimeAdapter);
            const BindingFlags privateInstance =
                BindingFlags.Instance | BindingFlags.NonPublic;

            sourcePlaybackManagerField = sourceType.GetField(
                "playbackManager",
                privateInstance);
            sourceMediaSessionField = sourceType.GetField(
                "mediaSession",
                privateInstance);
            sourceMediaBoundField = sourceType.GetField(
                "mediaBound",
                privateInstance);
            sourceOwnsOutboundMediaField = sourceType.GetField(
                "ownsOutboundMedia",
                privateInstance);
            sourceNextMediaSequenceField = sourceType.GetField(
                "nextMediaSequence",
                privateInstance);
            sourceNextMediaTimestampField = sourceType.GetField(
                "nextMediaTimestamp100Ns",
                privateInstance);
            sourceFrameHandlerMethod = sourceType.GetMethod(
                "HandleFrameEncoded",
                privateInstance,
                null,
                new[] { typeof(byte[]), typeof(bool) },
                null);

            sourceFrameHandler = null;
            if (sourceFrameHandlerMethod != null)
            {
                try
                {
                    sourceFrameHandler = (Action<byte[], bool>)Delegate.CreateDelegate(
                        typeof(Action<byte[], bool>),
                        sourceAdapter,
                        sourceFrameHandlerMethod);
                }
                catch
                {
                    sourceFrameHandler = null;
                }
            }

            if (wrapperFrameHandler == null)
                wrapperFrameHandler = HandleFrameEncoded;
        }

        private void InterceptSourcePlayback()
        {
            if (sourceAdapter == null || sourcePlaybackManagerField == null) return;

            object current = sourcePlaybackManagerField.GetValue(sourceAdapter);
            if (current != null)
                sourcePlaybackManagerField.SetValue(sourceAdapter, null);

            if (sourceInterceptedLogged) return;

            sourceInterceptedLogged = true;
            Debug.Log(
                "VOICE_WINDOWS_WORKER_AUDIO_INTERCEPT=PASS" +
                " | source=VoiceWindowsMediaWebGLParityRuntimeAdapter" +
                " | directSessionEventToWorker=True" +
                " | sourcePlaybackBypassed=True" +
                " | unityUpdateJitterDecode=False" +
                " | stablePlaybackFileChanged=False");
        }

        private void BindDirectMediaSession()
        {
            if (sourceAdapter == null || sourceMediaSessionField == null || worker == null)
                return;

            VoiceWindowsMediaWebGLParitySession current =
                sourceMediaSessionField.GetValue(sourceAdapter) as
                    VoiceWindowsMediaWebGLParitySession;

            if (ReferenceEquals(subscribedSession, current)) return;

            if (subscribedSession != null)
                subscribedSession.MediaReceived -= HandleDirectMediaReceived;

            subscribedSession = current;
            if (subscribedSession == null) return;

            subscribedSession.MediaReceived -= HandleDirectMediaReceived;
            subscribedSession.MediaReceived += HandleDirectMediaReceived;

            Debug.Log(
                "VOICE_WINDOWS_WORKER_AUDIO_DIRECT_MEDIA=PASS" +
                " | streamId=" + Safe(subscribedSession.StreamId) +
                " | sourceEvent=MediaReceived" +
                " | destination=DedicatedWorker" +
                " | unityMainThreadHop=False");
        }

        private void HandleDirectMediaReceived(VoiceMediaV2Packet packet)
        {
            VoiceWindowsLiveDeliveryWorkerSpatialV3 activeWorker = worker;
            if (activeWorker == null || disposed) return;
            activeWorker.Enqueue(packet);
        }

        private void EnsureOutboundTimelineOwnership()
        {
            if (sourceAdapter == null ||
                runtime == null ||
                microphonePublisher == null ||
                sourceFrameHandler == null ||
                sourceMediaSessionField == null ||
                sourceMediaBoundField == null ||
                sourceOwnsOutboundMediaField == null ||
                sourceNextMediaSequenceField == null ||
                sourceNextMediaTimestampField == null)
            {
                return;
            }

            bool sourceOwnsOutbound =
                sourceOwnsOutboundMediaField.GetValue(sourceAdapter) is bool owns && owns;

            if (!sourceOwnsOutbound)
            {
                if (outboundIntercepted) RestoreOutboundTimelineOwnership();
                return;
            }

            if (outboundIntercepted) return;
            if (wrapperFrameHandler == null) wrapperFrameHandler = HandleFrameEncoded;

            microphonePublisher.FrameEncoded -= sourceFrameHandler;
            microphonePublisher.FrameEncoded -= wrapperFrameHandler;
            microphonePublisher.FrameEncoded += wrapperFrameHandler;
            outboundIntercepted = true;

            Debug.Log(
                "VOICE_WINDOWS_WORKER_AUDIO_OUTBOUND_TIMELINE=PASS" +
                " | sequenceAdvancesOnDrop=True" +
                " | timestampAdvancesOnDrop=True" +
                " | transportChanged=False");
        }

        private void HandleFrameEncoded(byte[] packet, bool dtx)
        {
            if (sourceAdapter == null ||
                runtime == null ||
                !runtime.IsAuthenticated ||
                !runtime.IsVoiceEligible ||
                packet == null ||
                packet.Length == 0 ||
                sourceMediaSessionField == null ||
                sourceMediaBoundField == null ||
                sourceNextMediaSequenceField == null ||
                sourceNextMediaTimestampField == null)
            {
                return;
            }

            bool mediaBound =
                sourceMediaBoundField.GetValue(sourceAdapter) is bool value && value;
            VoiceWindowsMediaWebGLParitySession session =
                sourceMediaSessionField.GetValue(sourceAdapter) as
                    VoiceWindowsMediaWebGLParitySession;

            if (!mediaBound || session == null || !session.IsBound) return;

            uint mediaSequence =
                sourceNextMediaSequenceField.GetValue(sourceAdapter) is uint sequence
                    ? sequence
                    : 1u;
            ulong mediaTimestamp100Ns =
                sourceNextMediaTimestampField.GetValue(sourceAdapter) is ulong timestamp
                    ? timestamp
                    : 0UL;

            bool queued = session.TryQueueMedia(
                mediaSequence,
                mediaTimestamp100Ns,
                packet,
                VoiceMediaV2Flags.None);

            uint nextSequence =
                mediaSequence == uint.MaxValue ? 1u : mediaSequence + 1u;
            ulong nextTimestamp = mediaTimestamp100Ns + 200000UL;

            sourceNextMediaSequenceField.SetValue(sourceAdapter, nextSequence);
            sourceNextMediaTimestampField.SetValue(sourceAdapter, nextTimestamp);
            MirrorRuntimePublishedSequence(mediaSequence);

            if (queued)
            {
                Interlocked.Increment(ref outboundQueuedFrames);
                return;
            }

            Interlocked.Increment(ref outboundBackpressureDrops);
        }

        private void MirrorRuntimePublishedSequence(uint sequence)
        {
            if (runtime == null) return;

            try
            {
                runtimeLastPublishedSequenceField?.SetValue(runtime, sequence);
                runtimeFirstVoiceFrameSendLoggedField?.SetValue(runtime, true);
            }
            catch
            {
            }
        }

        private void DrainWorkerNotices()
        {
            VoiceWindowsLiveDeliveryWorkerSpatialV3 activeWorker = worker;
            if (activeWorker == null) return;

            while (activeWorker.TryDequeueNotice(out VoiceWindowsSpatialV3StreamNotice notice))
            {
                if (notice.Kind == VoiceWindowsSpatialV3StreamNoticeKind.Added)
                {
                    EnsurePlaybackBinding(notice);
                    continue;
                }

                RemovePlaybackBinding(notice.StreamKey);
            }
        }

        private void EnsurePlaybackBinding(VoiceWindowsSpatialV3StreamNotice notice)
        {
            if (playbackBindings.ContainsKey(notice.StreamKey)) return;

            GameObject playbackObject = new GameObject(
                "Voice_Worker_Playback_" + notice.StreamKey);
            playbackObject.transform.SetParent(transform, false);
            playbackObject.transform.localPosition = Vector3.zero;

            AudioSource audioSource = playbackObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.loop = true;
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.minDistance = 3f;
            audioSource.maxDistance = 3.5f;
            audioSource.dopplerLevel = 0f;
            audioSource.priority = 0;
            audioSource.bypassEffects = true;
            audioSource.bypassListenerEffects = true;
            audioSource.bypassReverbZones = true;

            VoiceWindowsSpatialV3StreamingPlayback filter =
                playbackObject.AddComponent<VoiceWindowsSpatialV3StreamingPlayback>();
            filter.Initialize(notice.Output, audioSource, notice.StreamKey);

            PlaybackBinding binding = new PlaybackBinding(
                notice.StreamKey,
                notice.SessionId,
                notice.SenderConnectionId,
                playbackObject,
                audioSource,
                filter);

            playbackBindings.Add(notice.StreamKey, binding);
            ApplyFallbackSpatial(binding);
            audioSource.Play();

            Debug.Log(
                "VOICE_WINDOWS_WORKER_SPATIAL_V3_STREAM=READY" +
                " | streamId=" + notice.StreamKey +
                " | workerDecodedPcm=True" +
                " | audioThreadConsumer=True" +
                " | unityStreamingPcmCallback=True");
        }

        private void RefreshPlaybackBindings()
        {
            float now = Time.unscaledTime;

            foreach (PlaybackBinding binding in playbackBindings.Values)
            {
                if (now < binding.NextSpatialRefreshAt) continue;
                binding.NextSpatialRefreshAt = now + SpatialRefreshSeconds;

                if (TryResolvePeerUserId(
                        binding.SessionId,
                        binding.SenderConnectionId,
                        out string peerUserId))
                {
                    binding.PeerUserId = peerUserId;
                }

                Transform peerTransform = FindPeerTransform(binding.PeerUserId);
                Transform targetParent =
                    peerTransform != null && peerTransform.gameObject.activeInHierarchy
                        ? peerTransform
                        : transform;

                if (binding.PlaybackObject.transform.parent != targetParent)
                    binding.PlaybackObject.transform.SetParent(targetParent, false);

                binding.PlaybackObject.transform.localPosition = Vector3.zero;

                if (!VoiceWindowsSpatialPlaybackPolicy.TryApply(
                        binding.AudioSource,
                        targetParent,
                        peerTransform))
                {
                    ApplyFallbackSpatial(binding);
                }
            }
        }

        private void ApplyFallbackSpatial(PlaybackBinding binding)
        {
            binding.AudioSource.spatialBlend = 0f;
            binding.AudioSource.rolloffMode = AudioRolloffMode.Linear;
            binding.AudioSource.minDistance = 10000f;
            binding.AudioSource.maxDistance = 10000f;
            binding.AudioSource.priority = 0;
        }

        private Transform FindPeerTransform(string peerUserId)
        {
            if (string.IsNullOrWhiteSpace(peerUserId)) return null;

            string normalized = peerUserId.Trim();
            if (peerTransformByUserId.TryGetValue(normalized, out Transform cached))
            {
                if (cached != null && cached.gameObject.activeInHierarchy) return cached;
                peerTransformByUserId.Remove(normalized);
            }

            MetaverseNetworkIdentity[] identities =
                FindObjectsOfType<MetaverseNetworkIdentity>(true);

            for (int index = 0; index < identities.Length; index++)
            {
                MetaverseNetworkIdentity identity = identities[index];
                if (identity == null || !identity.gameObject.activeInHierarchy) continue;
                if (!identity.IsOwnedByUser(normalized)) continue;

                peerTransformByUserId[normalized] = identity.transform;
                return identity.transform;
            }

            return null;
        }

        private bool TryResolvePeerUserId(
            string sessionId,
            string senderConnectionId,
            out string peerUserId)
        {
            peerUserId = string.Empty;
            if (runtime == null || runtimeSessionsField == null) return false;

            try
            {
                IDictionary sessions = runtimeSessionsField.GetValue(runtime) as IDictionary;
                if (sessions == null || !sessions.Contains(sessionId)) return false;

                object activeSession = sessions[sessionId];
                if (activeSession == null) return false;

                Type sessionType = activeSession.GetType();
                if (!peerResolverByType.TryGetValue(sessionType, out MethodInfo resolver))
                {
                    resolver = sessionType.GetMethod(
                        "TryResolvePeerUserId",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new[] { typeof(string), typeof(string).MakeByRefType() },
                        null);
                    peerResolverByType[sessionType] = resolver;
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
                    "VOICE_WINDOWS_WORKER_AUDIO_PEER_RESOLVE=FAIL" +
                    " | sessionId=" + sessionId +
                    " | senderConnectionId=" + senderConnectionId +
                    " | error=" + Safe(exception.Message));
                return false;
            }
        }

        private void SyncSpeakerState()
        {
            bool audible = runtime == null || !runtime.IsSpeakerOff;
            foreach (PlaybackBinding binding in playbackBindings.Values)
                binding.Filter.SetAudible(audible);
        }

        private void RemovePlaybackBinding(string streamKey)
        {
            if (!playbackBindings.TryGetValue(streamKey, out PlaybackBinding binding))
                return;

            playbackBindings.Remove(streamKey);
            if (binding.PlaybackObject != null)
                UnityEngine.Object.Destroy(binding.PlaybackObject);
        }

        private void ReportStats()
        {
            double nowMs = MonotonicMs();
            if (nowMs < nextStatsAtMs) return;
            nextStatsAtMs = nowMs + 1000.0;

            VoiceWindowsSpatialV3Stats stats =
                worker != null ? worker.GetStats() : default;

            long audioUnderflows = 0;
            long audioDrops = 0;
            long audioConsumedSamples = 0;
            int playbackQueuedFrames = 0;

            foreach (PlaybackBinding binding in playbackBindings.Values)
            {
                audioUnderflows += binding.Filter.Underflows;
                audioDrops += binding.Filter.Drops;
                audioConsumedSamples += binding.Filter.ConsumedSamples;
                playbackQueuedFrames += binding.Filter.QueuedFrames;
            }

            if (!firstAudioThreadPlaybackLogged && audioConsumedSamples > 0)
            {
                firstAudioThreadPlaybackLogged = true;
                Debug.Log(
                    "VOICE_WINDOWS_WORKER_SPATIAL_V3_FIRST_AUDIO_THREAD_PLAYOUT=PASS" +
                    " | consumedSamples=" + audioConsumedSamples +
                    " | unityMainThreadAudioTiming=False" +
                    " | audioThreadPlayback=True");
            }

            Debug.Log(
                "VOICE_WINDOWS_WORKER_SPATIAL_V3_STATS" +
                " | streams=" + stats.RemoteStreams +
                " | inboundQueued=" + stats.InboundQueued +
                " | inboundDrops=" + stats.InboundDrops +
                " | remoteBufferedFrames=" + stats.RemoteBufferedFrames +
                " | targetDelayMs=" + stats.TargetDelayMs +
                " | jitterMicros=" + stats.JitterMicros +
                " | lateDrops=" + stats.LateDrops +
                " | duplicateDrops=" + stats.DuplicateDrops +
                " | reordered=" + stats.Reordered +
                " | missing=" + stats.Missing +
                " | overflowDrops=" + stats.OverflowDrops +
                " | normal=" + stats.Normal +
                " | fec=" + stats.Fec +
                " | plc=" + stats.Plc +
                " | playbackQueuedFrames=" + playbackQueuedFrames +
                " | audioUnderflows=" + audioUnderflows +
                " | audioDrops=" + audioDrops +
                " | audioConsumedSamples=" + audioConsumedSamples +
                " | outboundQueued=" + Interlocked.Read(ref outboundQueuedFrames) +
                " | outboundBackpressureDrops=0" +
                " | outboundOwner=VoiceWindowsMediaWebGLParityRuntimeAdapter" +
                " | outboundInterception=False");
        }

        private void RestoreOutboundTimelineOwnership()
        {
            if (!outboundIntercepted) return;

            try
            {
                if (microphonePublisher != null)
                {
                    if (wrapperFrameHandler != null)
                        microphonePublisher.FrameEncoded -= wrapperFrameHandler;

                    if (sourceFrameHandler != null)
                    {
                        microphonePublisher.FrameEncoded -= sourceFrameHandler;
                        microphonePublisher.FrameEncoded += sourceFrameHandler;
                    }
                }
            }
            catch
            {
            }

            outboundIntercepted = false;
        }

        private void RestoreSourcePlayback()
        {
            if (sourceAdapter == null || sourcePlaybackManagerField == null) return;

            try
            {
                if (stablePlaybackManager == null)
                {
                    GameObject root = GameObject.Find(RuntimeRootName);
                    stablePlaybackManager =
                        root != null ? root.GetComponent<VoiceSpatialPlaybackManager>() : null;
                }

                if (stablePlaybackManager != null)
                    sourcePlaybackManagerField.SetValue(sourceAdapter, stablePlaybackManager);
            }
            catch
            {
            }
        }

        private void DisposeRuntime()
        {
            if (disposed) return;
            disposed = true;

            RestoreOutboundTimelineOwnership();

            if (subscribedSession != null)
            {
                try { subscribedSession.MediaReceived -= HandleDirectMediaReceived; }
                catch { }
                subscribedSession = null;
            }

            RestoreSourcePlayback();

            if (worker != null)
            {
                worker.Dispose();
                worker = null;
            }

            foreach (PlaybackBinding binding in playbackBindings.Values)
            {
                if (binding.PlaybackObject != null)
                    UnityEngine.Object.Destroy(binding.PlaybackObject);
            }
            playbackBindings.Clear();
        }

        private void OnDisable()
        {
            if (Application.isPlaying) DisposeRuntime();
        }

        private void OnDestroy()
        {
            DisposeRuntime();
        }

        private static double MonotonicMs()
        {
            return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private sealed class PlaybackBinding
        {
            public readonly string StreamKey;
            public readonly string SessionId;
            public readonly string SenderConnectionId;
            public readonly GameObject PlaybackObject;
            public readonly AudioSource AudioSource;
            public readonly VoiceWindowsSpatialV3StreamingPlayback Filter;
            public string PeerUserId = string.Empty;
            public float NextSpatialRefreshAt;

            public PlaybackBinding(
                string streamKey,
                string sessionId,
                string senderConnectionId,
                GameObject playbackObject,
                AudioSource audioSource,
                VoiceWindowsSpatialV3StreamingPlayback filter)
            {
                StreamKey = streamKey;
                SessionId = sessionId;
                SenderConnectionId = senderConnectionId;
                PlaybackObject = playbackObject;
                AudioSource = audioSource;
                Filter = filter;
            }
        }
    }

    internal sealed class VoiceWindowsLiveDeliveryWorkerSpatialV3 : IDisposable
    {
        private const int FrameSamples = 960;
        private const int SampleRate = 48000;
        private const int Channels = 1;
        private const int PlayoutFrameMs = 20;
        private const int MinimumPlayoutDelayMs = 40;
        private const int MaximumPlayoutDelayMs = 200;
        private const int MaximumRemoteFrames = 64;
        private const int MaximumRemoteStreams = 128;
        private const int MaximumPacketBytes = 1112;
        private const int MaximumInboundPackets = 128;
        private const int RemoteIdleTimeoutMs = 10000;
        private const int MaximumCatchupFramesPerPump = 4;
        private const int PumpIntervalMs = 5;

        private readonly ConcurrentQueue<VoiceMediaV2Packet> inbound =
            new ConcurrentQueue<VoiceMediaV2Packet>();
        private readonly ConcurrentQueue<VoiceWindowsSpatialV3StreamNotice> notices =
            new ConcurrentQueue<VoiceWindowsSpatialV3StreamNotice>();
        private readonly Dictionary<string, RemoteStreamState> remoteStreams =
            new Dictionary<string, RemoteStreamState>(StringComparer.OrdinalIgnoreCase);
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly object statsSync = new object();

        private Thread thread;
        private volatile bool running;
        private volatile string localConnectionId = string.Empty;
        private int inboundCount;
        private long inboundDrops;
        private long lateDrops;
        private long duplicateDrops;
        private long reordered;
        private long missing;
        private long overflowDrops;
        private long normal;
        private long fec;
        private long plc;
        private VoiceWindowsSpatialV3Stats latestStats;
        private double lastStatsPublishMs;

        public string LocalConnectionId
        {
            set { localConnectionId = value ?? string.Empty; }
        }

        public void Start()
        {
            if (running) return;

            running = true;
            thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "VoiceWindowsLiveDeliveryWorkerSpatialV3",
                Priority = System.Threading.ThreadPriority.Normal
            };
            thread.Start();
        }

        public void Enqueue(VoiceMediaV2Packet packet)
        {
            if (!running || packet == null) return;

            int pending = Interlocked.Increment(ref inboundCount);
            if (pending > MaximumInboundPackets)
            {
                if (inbound.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref inboundCount);
                    Interlocked.Increment(ref inboundDrops);
                }
            }

            inbound.Enqueue(packet);
            wake.Set();
        }

        public bool TryDequeueNotice(out VoiceWindowsSpatialV3StreamNotice notice)
        {
            return notices.TryDequeue(out notice);
        }

        public VoiceWindowsSpatialV3Stats GetStats()
        {
            lock (statsSync) return latestStats;
        }

        private void WorkerLoop()
        {
            long pumpIntervalTicks = Math.Max(
                1,
                Stopwatch.Frequency * PumpIntervalMs / 1000);
            long nextPump = Stopwatch.GetTimestamp();

            while (running)
            {
                DrainInbound();

                long nowTicks = Stopwatch.GetTimestamp();
                if (nowTicks >= nextPump)
                {
                    double nowMs = nowTicks * 1000.0 / Stopwatch.Frequency;
                    PumpRemoteStreams(nowMs);
                    PublishStats(nowMs);
                    nextPump = nowTicks + pumpIntervalTicks;
                }

                long remainingTicks = nextPump - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0) continue;

                int waitMs = (int)Math.Min(
                    PumpIntervalMs,
                    Math.Max(1, remainingTicks * 1000 / Stopwatch.Frequency));
                wake.WaitOne(waitMs);
            }

            DisposeRemoteStreams();
        }

        private void DrainInbound()
        {
            while (inbound.TryDequeue(out VoiceMediaV2Packet packet))
            {
                Interlocked.Decrement(ref inboundCount);
                try
                {
                    ProcessPacket(packet);
                }
                catch
                {
                }
            }
        }

        private void ProcessPacket(VoiceMediaV2Packet packet)
        {
            if (packet.Kind != VoiceMediaV2PacketKind.Media ||
                packet.Codec != VoiceMediaV2Codec.Opus ||
                packet.Payload == null ||
                packet.Payload.Length == 0 ||
                packet.Payload.Length > MaximumPacketBytes ||
                packet.MediaSequence == 0)
            {
                return;
            }

            string senderConnectionId = Safe(packet.SenderId);
            if (senderConnectionId.Length == 0 ||
                string.Equals(
                    senderConnectionId,
                    localConnectionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string sessionId = Safe(packet.SessionId);
            string streamId = Safe(packet.StreamId);
            string key = Guid.TryParse(streamId, out _)
                ? streamId.ToLowerInvariant()
                : sessionId + "|" + senderConnectionId;

            if (!remoteStreams.TryGetValue(key, out RemoteStreamState state))
            {
                if (remoteStreams.Count >= MaximumRemoteStreams) return;

                state = new RemoteStreamState(
                    key,
                    sessionId,
                    senderConnectionId);
                remoteStreams.Add(key, state);
                notices.Enqueue(new VoiceWindowsSpatialV3StreamNotice(
                    VoiceWindowsSpatialV3StreamNoticeKind.Added,
                    key,
                    sessionId,
                    senderConnectionId,
                    state.Output));
            }

            state.SessionId = sessionId;
            state.SenderConnectionId = senderConnectionId;

            double nowMs = MonotonicMs();
            state.LastArrivalAtMs = nowMs;
            uint mediaSequence = packet.MediaSequence;

            if (state.Initialized && mediaSequence < state.ExpectedSequence)
            {
                Interlocked.Increment(ref lateDrops);
                return;
            }

            if (state.Frames.ContainsKey(mediaSequence))
            {
                Interlocked.Increment(ref duplicateDrops);
                return;
            }

            if (state.Frames.Count >= MaximumRemoteFrames)
            {
                Interlocked.Increment(ref overflowDrops);
                return;
            }

            if (state.HighestSequence != 0 && mediaSequence < state.HighestSequence)
                Interlocked.Increment(ref reordered);

            double transitMs = nowMs - packet.MediaTimestamp100Ns / 10000.0;
            if (state.LastTransitMs.HasValue)
            {
                double deviation = Math.Abs(transitMs - state.LastTransitMs.Value);
                state.JitterMs += (deviation - state.JitterMs) / 16.0;
                state.TargetDelayMs = ClampPlayoutDelay(
                    MinimumPlayoutDelayMs + state.JitterMs * 4.0);
            }

            state.LastTransitMs = transitMs;
            state.HighestSequence = Math.Max(state.HighestSequence, mediaSequence);
            state.Frames.Add(
                mediaSequence,
                new BufferedRemotePacket(packet.Payload));

            if (!state.Initialized)
            {
                state.Initialized = true;
                state.ExpectedSequence = mediaSequence;
                state.NextPlayoutAtMs = nowMs + state.TargetDelayMs;
            }
        }

        private void PumpRemoteStreams(double nowMs)
        {
            if (remoteStreams.Count == 0) return;

            List<string> remove = null;

            foreach (KeyValuePair<string, RemoteStreamState> pair in remoteStreams)
            {
                RemoteStreamState state = pair.Value;

                if (nowMs - state.LastArrivalAtMs >= RemoteIdleTimeoutMs &&
                    state.Frames.Count == 0)
                {
                    if (remove == null) remove = new List<string>();
                    remove.Add(pair.Key);
                    continue;
                }

                PlayoutRemoteState(state, nowMs);
            }

            if (remove == null) return;

            for (int index = 0; index < remove.Count; index++)
            {
                string key = remove[index];
                if (!remoteStreams.TryGetValue(key, out RemoteStreamState state)) continue;

                remoteStreams.Remove(key);
                notices.Enqueue(new VoiceWindowsSpatialV3StreamNotice(
                    VoiceWindowsSpatialV3StreamNoticeKind.Removed,
                    key,
                    state.SessionId,
                    state.SenderConnectionId,
                    state.Output));
                state.Dispose();
            }
        }

        private void PlayoutRemoteState(RemoteStreamState state, double nowMs)
        {
            if (state.Frames.Count == 0 &&
                nowMs - state.LastArrivalAtMs > state.TargetDelayMs + PlayoutFrameMs)
            {
                state.Initialized = false;
                return;
            }

            int safety = 0;
            while (state.Initialized &&
                   nowMs >= state.NextPlayoutAtMs &&
                   safety < MaximumCatchupFramesPerPump)
            {
                uint sequence = state.ExpectedSequence;

                if (state.Frames.TryGetValue(sequence, out BufferedRemotePacket packet))
                {
                    state.Frames.Remove(sequence);
                    DecodeAndQueue(state, packet.Payload, false, false);
                    Interlocked.Increment(ref normal);
                }
                else
                {
                    Interlocked.Increment(ref missing);
                    uint nextSequence = sequence == uint.MaxValue ? 1u : sequence + 1u;

                    if (state.Frames.TryGetValue(nextSequence, out BufferedRemotePacket nextPacket))
                    {
                        DecodeAndQueue(state, nextPacket.Payload, true, false);
                        Interlocked.Increment(ref fec);
                    }
                    else
                    {
                        DecodeAndQueue(state, null, false, true);
                        Interlocked.Increment(ref plc);
                    }
                }

                state.ExpectedSequence =
                    sequence == uint.MaxValue ? 1u : sequence + 1u;
                state.NextPlayoutAtMs += PlayoutFrameMs;
                safety += 1;
            }
        }

        private static void DecodeAndQueue(
            RemoteStreamState state,
            byte[] packet,
            bool decodeFec,
            bool packetLossConcealment)
        {
            float[] pcm = state.Output.AcquireFrame();
            int samples;

            if (packetLossConcealment)
            {
                samples = VoiceOpusNative.DecodeFloat(
                    state.Decoder,
                    null,
                    0,
                    pcm,
                    FrameSamples,
                    0);
            }
            else
            {
                if (packet == null || packet.Length == 0)
                {
                    state.Output.RecycleFrame(pcm);
                    return;
                }

                samples = VoiceOpusNative.DecodeFloat(
                    state.Decoder,
                    packet,
                    packet.Length,
                    pcm,
                    FrameSamples,
                    decodeFec ? 1 : 0);
            }

            if (samples != FrameSamples)
            {
                state.Output.RecycleFrame(pcm);
                return;
            }

            state.Output.EnqueueDecodedFrame(pcm);
        }

        private void PublishStats(double nowMs)
        {
            if (nowMs - lastStatsPublishMs < 1000.0) return;
            lastStatsPublishMs = nowMs;

            int buffered = 0;
            double targetDelayMs = 0;
            double jitterMs = 0;

            foreach (RemoteStreamState state in remoteStreams.Values)
            {
                buffered += state.Frames.Count;
                targetDelayMs = Math.Max(targetDelayMs, state.TargetDelayMs);
                jitterMs = Math.Max(jitterMs, state.JitterMs);
            }

            VoiceWindowsSpatialV3Stats snapshot = new VoiceWindowsSpatialV3Stats(
                remoteStreams.Count,
                Volatile.Read(ref inboundCount),
                Interlocked.Read(ref inboundDrops),
                buffered,
                (long)Math.Round(targetDelayMs),
                (long)Math.Round(jitterMs * 1000.0),
                Interlocked.Read(ref lateDrops),
                Interlocked.Read(ref duplicateDrops),
                Interlocked.Read(ref reordered),
                Interlocked.Read(ref missing),
                Interlocked.Read(ref overflowDrops),
                Interlocked.Read(ref normal),
                Interlocked.Read(ref fec),
                Interlocked.Read(ref plc));

            lock (statsSync) latestStats = snapshot;
        }

        private void DisposeRemoteStreams()
        {
            foreach (RemoteStreamState state in remoteStreams.Values)
                state.Dispose();
            remoteStreams.Clear();
        }

        public void Dispose()
        {
            if (!running) return;
            running = false;
            wake.Set();

            try
            {
                if (thread != null && thread.IsAlive)
                    thread.Join(1000);
            }
            catch
            {
            }

            thread = null;
            wake.Dispose();
        }

        private static double ClampPlayoutDelay(double value)
        {
            double bounded = Math.Max(
                MinimumPlayoutDelayMs,
                Math.Min(MaximumPlayoutDelayMs, value));
            return Math.Ceiling(bounded / PlayoutFrameMs) * PlayoutFrameMs;
        }

        private static double MonotonicMs()
        {
            return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private sealed class RemoteStreamState : IDisposable
        {
            public readonly string StreamKey;
            public readonly IntPtr Decoder;
            public readonly VoiceWindowsSpatialV3PcmQueue Output;
            public readonly Dictionary<uint, BufferedRemotePacket> Frames =
                new Dictionary<uint, BufferedRemotePacket>();

            public string SessionId;
            public string SenderConnectionId;
            public bool Initialized;
            public uint ExpectedSequence;
            public uint HighestSequence;
            public double NextPlayoutAtMs;
            public double LastArrivalAtMs;
            public double? LastTransitMs;
            public double JitterMs;
            public double TargetDelayMs = MinimumPlayoutDelayMs;

            public RemoteStreamState(
                string streamKey,
                string sessionId,
                string senderConnectionId)
            {
                StreamKey = streamKey;
                SessionId = sessionId;
                SenderConnectionId = senderConnectionId;
                LastArrivalAtMs = MonotonicMs();
                Output = new VoiceWindowsSpatialV3PcmQueue(FrameSamples, 50);

                Decoder = VoiceOpusNative.DecoderCreate(
                    SampleRate,
                    Channels,
                    out int error);
                if (error < 0 || Decoder == IntPtr.Zero)
                    throw new InvalidOperationException(
                        "Voice Windows worker Opus decoder create failed: " + error);
            }

            public void Dispose()
            {
                if (Decoder != IntPtr.Zero)
                    VoiceOpusNative.DecoderDestroy(Decoder);
            }
        }

        private readonly struct BufferedRemotePacket
        {
            public readonly byte[] Payload;

            public BufferedRemotePacket(byte[] payload)
            {
                Payload = payload;
            }
        }
    }

    internal enum VoiceWindowsSpatialV3StreamNoticeKind
    {
        Added = 1,
        Removed = 2
    }

    internal readonly struct VoiceWindowsSpatialV3StreamNotice
    {
        public readonly VoiceWindowsSpatialV3StreamNoticeKind Kind;
        public readonly string StreamKey;
        public readonly string SessionId;
        public readonly string SenderConnectionId;
        public readonly VoiceWindowsSpatialV3PcmQueue Output;

        public VoiceWindowsSpatialV3StreamNotice(
            VoiceWindowsSpatialV3StreamNoticeKind kind,
            string streamKey,
            string sessionId,
            string senderConnectionId,
            VoiceWindowsSpatialV3PcmQueue output)
        {
            Kind = kind;
            StreamKey = streamKey;
            SessionId = sessionId;
            SenderConnectionId = senderConnectionId;
            Output = output;
        }
    }

    internal readonly struct VoiceWindowsSpatialV3Stats
    {
        public readonly int RemoteStreams;
        public readonly int InboundQueued;
        public readonly long InboundDrops;
        public readonly int RemoteBufferedFrames;
        public readonly long TargetDelayMs;
        public readonly long JitterMicros;
        public readonly long LateDrops;
        public readonly long DuplicateDrops;
        public readonly long Reordered;
        public readonly long Missing;
        public readonly long OverflowDrops;
        public readonly long Normal;
        public readonly long Fec;
        public readonly long Plc;

        public VoiceWindowsSpatialV3Stats(
            int remoteStreams,
            int inboundQueued,
            long inboundDrops,
            int remoteBufferedFrames,
            long targetDelayMs,
            long jitterMicros,
            long lateDrops,
            long duplicateDrops,
            long reordered,
            long missing,
            long overflowDrops,
            long normal,
            long fec,
            long plc)
        {
            RemoteStreams = remoteStreams;
            InboundQueued = inboundQueued;
            InboundDrops = inboundDrops;
            RemoteBufferedFrames = remoteBufferedFrames;
            TargetDelayMs = targetDelayMs;
            JitterMicros = jitterMicros;
            LateDrops = lateDrops;
            DuplicateDrops = duplicateDrops;
            Reordered = reordered;
            Missing = missing;
            OverflowDrops = overflowDrops;
            Normal = normal;
            Fec = fec;
            Plc = plc;
        }
    }

    internal sealed class VoiceWindowsSpatialV3PcmQueue : IDisposable
    {
        private readonly ConcurrentQueue<float[]> queued =
            new ConcurrentQueue<float[]>();
        private readonly ConcurrentQueue<float[]> recycled =
            new ConcurrentQueue<float[]>();
        private readonly int frameSamples;
        private readonly int maximumFrames;

        private float[] currentFrame;
        private int currentOffset;
        private int queuedCount;
        private long underflows;
        private long drops;
        private long consumedSamples;
        private volatile bool disposed;

        public int QueuedFrames => Math.Max(0, Volatile.Read(ref queuedCount));
        public long Underflows => Interlocked.Read(ref underflows);
        public long Drops => Interlocked.Read(ref drops);
        public long ConsumedSamples => Interlocked.Read(ref consumedSamples);

        public VoiceWindowsSpatialV3PcmQueue(int frameSamples, int maximumFrames)
        {
            this.frameSamples = frameSamples;
            this.maximumFrames = maximumFrames;

            for (int index = 0; index < maximumFrames + 8; index++)
                recycled.Enqueue(new float[frameSamples]);
        }

        public float[] AcquireFrame()
        {
            if (recycled.TryDequeue(out float[] frame)) return frame;
            return new float[frameSamples];
        }

        public void RecycleFrame(float[] frame)
        {
            if (frame == null || frame.Length != frameSamples) return;
            recycled.Enqueue(frame);
        }

        public void EnqueueDecodedFrame(float[] frame)
        {
            if (disposed || frame == null || frame.Length != frameSamples)
            {
                RecycleFrame(frame);
                return;
            }

            while (Volatile.Read(ref queuedCount) >= maximumFrames)
            {
                if (!queued.TryDequeue(out float[] dropped)) break;
                Interlocked.Decrement(ref queuedCount);
                Interlocked.Increment(ref drops);
                RecycleFrame(dropped);
            }

            queued.Enqueue(frame);
            Interlocked.Increment(ref queuedCount);
        }

        public void Fill(float[] data, int channels, bool audible)
        {
            if (data == null || data.Length == 0) return;
            if (channels <= 0) channels = 1;

            int frameCount = data.Length / channels;
            int writtenFrames = 0;
            bool hadAudio = false;

            while (writtenFrames < frameCount)
            {
                if (currentFrame == null || currentOffset >= currentFrame.Length)
                {
                    if (currentFrame != null)
                    {
                        RecycleFrame(currentFrame);
                        currentFrame = null;
                        currentOffset = 0;
                    }

                    if (!queued.TryDequeue(out currentFrame)) break;
                    Interlocked.Decrement(ref queuedCount);
                    currentOffset = 0;
                }

                int available = currentFrame.Length - currentOffset;
                int copyFrames = Math.Min(frameCount - writtenFrames, available);
                hadAudio = true;

                for (int frame = 0; frame < copyFrames; frame++)
                {
                    float sample = audible ? currentFrame[currentOffset + frame] : 0f;
                    int output = (writtenFrames + frame) * channels;
                    for (int channel = 0; channel < channels; channel++)
                        data[output + channel] = sample;
                }

                currentOffset += copyFrames;
                writtenFrames += copyFrames;
                Interlocked.Add(ref consumedSamples, copyFrames);
            }

            if (writtenFrames < frameCount)
            {
                int start = writtenFrames * channels;
                Array.Clear(data, start, data.Length - start);
            }

            if (!hadAudio) Interlocked.Increment(ref underflows);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            while (queued.TryDequeue(out float[] frame))
                RecycleFrame(frame);
            Interlocked.Exchange(ref queuedCount, 0);

            if (currentFrame != null)
            {
                RecycleFrame(currentFrame);
                currentFrame = null;
                currentOffset = 0;
            }
        }
    }

    [DisallowMultipleComponent]
    internal sealed class VoiceWindowsSpatialV3StreamingPlayback : MonoBehaviour
    {
        private VoiceWindowsSpatialV3PcmQueue output;
        private AudioSource audioSource;
        private AudioClip streamingClip;
        private volatile bool audible = true;

        public long Underflows => output != null ? output.Underflows : 0;
        public long Drops => output != null ? output.Drops : 0;
        public int QueuedFrames => output != null ? output.QueuedFrames : 0;
        public long ConsumedSamples => output != null ? output.ConsumedSamples : 0;

        public void Initialize(
            VoiceWindowsSpatialV3PcmQueue queue,
            AudioSource source,
            string streamKey)
        {
            output = queue;
            audioSource = source;

            streamingClip = AudioClip.Create(
                "Voice_Worker_SpatialV3_PCM_" + streamKey,
                48000,
                1,
                48000,
                true,
                FillPcm);

            if (audioSource != null)
                audioSource.clip = streamingClip;
        }

        public void SetAudible(bool value)
        {
            audible = value;
            if (audioSource != null) audioSource.mute = !value;
        }

        private void FillPcm(float[] data)
        {
            VoiceWindowsSpatialV3PcmQueue active = output;
            if (active == null)
            {
                if (data != null) Array.Clear(data, 0, data.Length);
                return;
            }

            active.Fill(data, 1, audible);
        }

        private void OnDestroy()
        {
            if (streamingClip != null)
            {
                UnityEngine.Object.Destroy(streamingClip);
                streamingClip = null;
            }

            output = null;
            audioSource = null;
        }
    }

}
#endif
