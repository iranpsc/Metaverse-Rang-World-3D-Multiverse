#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Network_A.Voice.Client.Codec;
using Network_A.Voice.Client.Capture;
using Network_A.Voice.Client.Playback;
using Network_A.Voice.Client.Runtime;
using Network_A.Voice.Client.Spatial;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Network_A.Voice.Client.MediaV2.Windows
{
    public static class VoiceWindowsLiveDeliveryWebGLParityInstaller
    {
        private const string RootName = "Voice_Windows_Live_Delivery_WebGL_Parity";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            Debug.LogWarning(
                "VOICE_WINDOWS_LIVE_DELIVERY_WEBGL_PARITY=DISABLED" +
                " | reason=single_owner_webgl_parity" +
                " | replacement=VoiceWindowsLiveDeliveryWorkerSpatialV3Wrapper" +
                " | stableFilesChanged=False");
        }
    }

    [DefaultExecutionOrder(-31999)]
    [DisallowMultipleComponent]
    public sealed class VoiceWindowsLiveDeliveryWebGLParityWrapper : MonoBehaviour
    {
        private const string RuntimeRootName = "Voice_Client_Runtime_Root";
        private const int FrameSamples = 960;
        private const int SampleRate = 48000;
        private const int Channels = 1;
        private const int PlayoutFrameMs = 20;
        private const int MinimumPlayoutDelayMs = 40;
        private const int MaximumPlayoutDelayMs = 200;
        private const int MaximumRemoteFrames = 64;
        private const int MaximumRemoteStreams = 128;
        private const int MaximumPacketBytes = 1112;
        private const int MaximumPlaybackFrames = 50;
        private const int RemoteIdleTimeoutMs = 10000;
        private const int MaximumCatchupFramesPerUpdate = 4;
        private const float SpatialTargetRefreshSeconds = 1f;

        private readonly Dictionary<string, RemoteStreamState> remoteStreams =
            new Dictionary<string, RemoteStreamState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Transform> peerTransformByUserId =
            new Dictionary<string, Transform>(StringComparer.Ordinal);
        private readonly Dictionary<Type, FieldInfo> packetFieldByInboundType =
            new Dictionary<Type, FieldInfo>();
        private readonly Dictionary<Type, MethodInfo> peerResolverByType =
            new Dictionary<Type, MethodInfo>();

        private VoiceWindowsMediaWebGLParityRuntimeAdapter sourceAdapter;
        private VoiceClientRuntime runtime;
        private VoiceSpatialPlaybackManager stablePlaybackManager;
        private VoiceMicrophonePublisher microphonePublisher;
        private FieldInfo sourcePlaybackManagerField;
        private FieldInfo sourceDeferredFramesField;
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
        private bool sourceIntercepted;
        private bool outboundIntercepted;
        private bool firstBufferedLogged;
        private bool firstPlayedLogged;
        private bool disposed;
        private double nextStatsAtMs;

        private long interceptedFrames;
        private long lateDrops;
        private long duplicateDrops;
        private long reorderedPackets;
        private long missingFrames;
        private long overflowDrops;
        private long normalFrames;
        private long fecFrames;
        private long plcFrames;
        private long fallbackPeerFrames;
        private long playbackDrops;
        private long outboundQueuedFrames;
        private long outboundBackpressureDrops;

        private void Update()
        {
            if (disposed) return;

            ResolveRuntime();
            ResolveAndInterceptSourceAdapter();
            EnsureOutboundTimelineOwnership();
            DrainSourceFrames();

            double nowMs = MonotonicMs();
            PumpRemoteStreams(nowMs);
            SyncSpeakerState();
            ReportStats(nowMs);
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
            stablePlaybackManager =
                root != null ? root.GetComponent<VoiceSpatialPlaybackManager>() : null;
            microphonePublisher = candidatePublisher;
            if (wrapperFrameHandler == null)
                wrapperFrameHandler = HandleFrameEncoded;

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

        private void ResolveAndInterceptSourceAdapter()
        {
            if (sourceAdapter == null)
            {
                sourceAdapter = UnityEngine.Object.FindObjectOfType<
                    VoiceWindowsMediaWebGLParityRuntimeAdapter>();

                if (sourceAdapter == null) return;

                Type sourceType = typeof(VoiceWindowsMediaWebGLParityRuntimeAdapter);
                sourcePlaybackManagerField = sourceType.GetField(
                    "playbackManager",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                const BindingFlags privateInstance =
                    BindingFlags.Instance | BindingFlags.NonPublic;

                sourceDeferredFramesField = sourceType.GetField(
                    "deferredFrames",
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

                outboundIntercepted = false;
            }

            if (sourcePlaybackManagerField == null ||
                sourceDeferredFramesField == null)
            {
                return;
            }

            object current = sourcePlaybackManagerField.GetValue(sourceAdapter);
            if (current != null)
                sourcePlaybackManagerField.SetValue(sourceAdapter, null);

            if (sourceIntercepted) return;

            sourceIntercepted = true;
            Debug.Log(
                "VOICE_WINDOWS_LIVE_DELIVERY_INTERCEPT=PASS" +
                " | source=VoiceWindowsMediaWebGLParityRuntimeAdapter" +
                " | networkSessionChanged=False" +
                " | outboundPathChanged=False" +
                " | stablePlaybackFileChanged=False" +
                " | inboundDeliveryOwnedByWrapper=True");
        }

        private void DrainSourceFrames()
        {
            if (!sourceIntercepted ||
                sourceAdapter == null ||
                sourceDeferredFramesField == null)
            {
                return;
            }

            IList deferred = sourceDeferredFramesField.GetValue(sourceAdapter) as IList;
            if (deferred == null || deferred.Count == 0) return;

            int count = deferred.Count;
            for (int index = 0; index < count; index++)
            {
                object inbound = deferred[index];
                VoiceMediaV2Packet packet = ReadPacket(inbound);
                if (packet != null) BufferRemotePacket(packet);
            }

            deferred.Clear();
        }

        private VoiceMediaV2Packet ReadPacket(object inbound)
        {
            if (inbound == null) return null;

            Type type = inbound.GetType();
            if (!packetFieldByInboundType.TryGetValue(type, out FieldInfo field))
            {
                field = type.GetField(
                    "Packet",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                packetFieldByInboundType[type] = field;
            }

            return field?.GetValue(inbound) as VoiceMediaV2Packet;
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
                if (outboundIntercepted && wrapperFrameHandler != null)
                    microphonePublisher.FrameEncoded -= wrapperFrameHandler;
                outboundIntercepted = false;
                return;
            }

            if (outboundIntercepted) return;

            if (wrapperFrameHandler == null)
                wrapperFrameHandler = HandleFrameEncoded;

            microphonePublisher.FrameEncoded -= sourceFrameHandler;
            microphonePublisher.FrameEncoded -= wrapperFrameHandler;
            microphonePublisher.FrameEncoded += wrapperFrameHandler;

            outboundIntercepted = true;
            Debug.Log(
                "VOICE_WINDOWS_OUTBOUND_TIMELINE_WEBGL_PARITY=PASS" +
                " | source=VoiceWindowsMediaWebGLParityRuntimeAdapter" +
                " | sequenceAdvancesOnBackpressureDrop=True" +
                " | timestampAdvancesOnBackpressureDrop=True" +
                " | mediaSessionChanged=False" +
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

            uint nextSequence = mediaSequence == uint.MaxValue
                ? 1u
                : mediaSequence + 1u;
            ulong nextTimestamp = mediaTimestamp100Ns + 200000UL;

            sourceNextMediaSequenceField.SetValue(sourceAdapter, nextSequence);
            sourceNextMediaTimestampField.SetValue(sourceAdapter, nextTimestamp);
            MirrorRuntimePublishedSequence(mediaSequence);

            if (queued)
            {
                outboundQueuedFrames += 1;
                return;
            }

            outboundBackpressureDrops += 1;
            Debug.LogWarning(
                "VOICE_WINDOWS_OUTBOUND_TIMELINE_DROP=OBSERVED" +
                " | mediaSequence=" + mediaSequence +
                " | mediaTimestamp100Ns=" + mediaTimestamp100Ns +
                " | dtx=" + dtx +
                " | sequenceAdvanced=True" +
                " | timestampAdvanced=True" +
                " | policy=WebGLTimelineParity");
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

        private void BufferRemotePacket(VoiceMediaV2Packet packet)
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

            string sessionId = Safe(packet.SessionId);
            string senderConnectionId = Safe(packet.SenderId);
            string streamId = Safe(packet.StreamId);

            if (!Guid.TryParse(sessionId, out _) ||
                !Guid.TryParse(senderConnectionId, out _))
            {
                return;
            }

            if (runtime != null &&
                string.Equals(
                    senderConnectionId,
                    runtime.VoiceConnectionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string key = Guid.TryParse(streamId, out _)
                ? streamId.ToLowerInvariant()
                : sessionId + "|" + senderConnectionId;

            if (!remoteStreams.TryGetValue(key, out RemoteStreamState state))
            {
                if (remoteStreams.Count >= MaximumRemoteStreams)
                {
                    Debug.LogWarning(
                        "VOICE_WINDOWS_LIVE_DELIVERY_STREAM_CREATE=FAIL" +
                        " | reason=remote_decoder_capacity_exceeded" +
                        " | capacity=" + MaximumRemoteStreams);
                    return;
                }

                try
                {
                    state = new RemoteStreamState(
                        key,
                        sessionId,
                        senderConnectionId,
                        transform,
                        this);
                    remoteStreams.Add(key, state);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "VOICE_WINDOWS_LIVE_DELIVERY_STREAM_CREATE=FAIL" +
                        " | streamId=" + key +
                        " | error=" + Safe(exception.Message));
                    return;
                }
            }

            state.SessionId = sessionId;
            state.SenderConnectionId = senderConnectionId;

            if (TryResolvePeerUserId(
                    sessionId,
                    senderConnectionId,
                    out string peerUserId))
            {
                state.PeerUserId = peerUserId;
            }
            else
            {
                fallbackPeerFrames += 1;
            }

            double nowMs = MonotonicMs();
            state.LastArrivalAtMs = nowMs;
            uint mediaSequence = packet.MediaSequence;

            if (state.Initialized && mediaSequence < state.ExpectedSequence)
            {
                lateDrops += 1;
                return;
            }

            if (state.Frames.ContainsKey(mediaSequence))
            {
                duplicateDrops += 1;
                return;
            }

            if (state.Frames.Count >= MaximumRemoteFrames)
            {
                overflowDrops += 1;
                return;
            }

            if (state.HighestSequence != 0 && mediaSequence < state.HighestSequence)
                reorderedPackets += 1;

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
                new BufferedRemotePacket(
                    packet.Payload,
                    mediaSequence,
                    packet.MediaTimestamp100Ns));
            interceptedFrames += 1;

            if (!state.Initialized)
            {
                state.Initialized = true;
                state.ExpectedSequence = mediaSequence;
                state.NextPlayoutAtMs = nowMs + state.TargetDelayMs;
            }

            if (firstBufferedLogged) return;

            firstBufferedLogged = true;
            Debug.Log(
                "VOICE_WINDOWS_LIVE_DELIVERY_FIRST_BUFFERED=PASS" +
                " | mediaSequence=" + mediaSequence +
                " | targetDelayMs=" + state.TargetDelayMs.ToString("F0") +
                " | peerResolved=" + (!string.IsNullOrWhiteSpace(state.PeerUserId)) +
                " | playbackBlockedByPeerResolution=False");
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

                RefreshSpatialTarget(state);
                PlayoutRemoteState(state, nowMs);
            }

            if (remove == null) return;

            for (int index = 0; index < remove.Count; index++)
            {
                string key = remove[index];
                if (!remoteStreams.TryGetValue(key, out RemoteStreamState state)) continue;
                remoteStreams.Remove(key);
                state.Dispose();
            }
        }

        private void PlayoutRemoteState(RemoteStreamState state, double nowMs)
        {
            if (state.Frames.Count == 0 &&
                nowMs - state.LastArrivalAtMs >
                state.TargetDelayMs + PlayoutFrameMs)
            {
                state.Initialized = false;
                return;
            }

            int safety = 0;
            while (state.Initialized &&
                   nowMs >= state.NextPlayoutAtMs &&
                   safety < MaximumCatchupFramesPerUpdate)
            {
                uint sequence = state.ExpectedSequence;

                if (state.Frames.TryGetValue(sequence, out BufferedRemotePacket packet))
                {
                    state.Frames.Remove(sequence);
                    DecodeAndQueue(state, packet.Payload, false, false);
                    normalFrames += 1;
                }
                else
                {
                    missingFrames += 1;
                    uint nextSequence = sequence == uint.MaxValue ? 1 : sequence + 1;

                    if (state.Frames.TryGetValue(
                            nextSequence,
                            out BufferedRemotePacket nextPacket))
                    {
                        DecodeAndQueue(state, nextPacket.Payload, true, false);
                        fecFrames += 1;
                    }
                    else
                    {
                        DecodeAndQueue(state, null, false, true);
                        plcFrames += 1;
                    }
                }

                state.ExpectedSequence = sequence == uint.MaxValue ? 1 : sequence + 1;
                state.NextPlayoutAtMs += PlayoutFrameMs;
                safety += 1;
            }
        }

        private void DecodeAndQueue(
            RemoteStreamState state,
            byte[] packet,
            bool decodeFec,
            bool packetLossConcealment)
        {
            float[] pcm = new float[FrameSamples];
            int samples;

            if (packetLossConcealment)
            {
                samples = VoiceWindowsOpusPlcNative.DecodeFloat(
                    state.Decoder,
                    IntPtr.Zero,
                    0,
                    pcm,
                    FrameSamples,
                    0);
            }
            else
            {
                if (packet == null || packet.Length == 0) return;
                samples = VoiceOpusNative.DecodeFloat(
                    state.Decoder,
                    packet,
                    packet.Length,
                    pcm,
                    FrameSamples,
                    decodeFec ? 1 : 0);
            }

            if (samples < 0)
            {
                Debug.LogWarning(
                    "VOICE_WINDOWS_LIVE_DELIVERY_DECODE=FAIL" +
                    " | code=" + samples +
                    " | fec=" + decodeFec +
                    " | plc=" + packetLossConcealment);
                return;
            }

            if (samples != FrameSamples)
            {
                Debug.LogWarning(
                    "VOICE_WINDOWS_LIVE_DELIVERY_DECODE=FAIL" +
                    " | reason=decoded_sample_count_invalid" +
                    " | samples=" + samples +
                    " | expected=" + FrameSamples +
                    " | fec=" + decodeFec +
                    " | plc=" + packetLossConcealment);
                return;
            }

            playbackDrops += state.Output.Enqueue(pcm);

            if (firstPlayedLogged) return;

            firstPlayedLogged = true;
            Debug.Log(
                "VOICE_WINDOWS_LIVE_DELIVERY_FIRST_PLAYOUT=PASS" +
                " | streamId=" + state.StreamKey +
                " | targetDelayMs=" + state.TargetDelayMs.ToString("F0") +
                " | webglJitterPolicy=True" +
                " | legacyPcmPrebufferBypassed=True");
        }

        private void RefreshSpatialTarget(RemoteStreamState state)
        {
            float now = Time.unscaledTime;
            if (now < state.NextSpatialRefreshAt) return;

            state.NextSpatialRefreshAt = now + SpatialTargetRefreshSeconds;
            Transform peerTransform = FindPeerTransform(state.PeerUserId);
            state.Output.UpdateSpatialTarget(peerTransform);
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
                    "VOICE_WINDOWS_LIVE_DELIVERY_PEER_RESOLVE=FAIL" +
                    " | sessionId=" + sessionId +
                    " | senderConnectionId=" + senderConnectionId +
                    " | error=" + Safe(exception.Message));
                return false;
            }
        }

        private void SyncSpeakerState()
        {
            bool audible = runtime == null || !runtime.IsSpeakerOff;
            foreach (RemoteStreamState state in remoteStreams.Values)
                state.Output.SetAudible(audible);
        }

        private void ReportStats(double nowMs)
        {
            if (nowMs < nextStatsAtMs) return;
            nextStatsAtMs = nowMs + 1000.0;

            int buffered = 0;
            double targetDelayMs = 0;
            double jitterMs = 0;

            foreach (RemoteStreamState state in remoteStreams.Values)
            {
                buffered += state.Frames.Count;
                targetDelayMs = Math.Max(targetDelayMs, state.TargetDelayMs);
                jitterMs = Math.Max(jitterMs, state.JitterMs);
            }

            Debug.Log(
                "VOICE_WINDOWS_LIVE_DELIVERY_WEBGL_PARITY_STATS" +
                " | streams=" + remoteStreams.Count +
                " | intercepted=" + interceptedFrames +
                " | remoteBufferedFrames=" + buffered +
                " | targetDelayMs=" + targetDelayMs.ToString("F0") +
                " | jitterMicros=" + Math.Round(jitterMs * 1000.0) +
                " | lateDrops=" + lateDrops +
                " | duplicateDrops=" + duplicateDrops +
                " | reordered=" + reorderedPackets +
                " | missing=" + missingFrames +
                " | overflowDrops=" + overflowDrops +
                " | normal=" + normalFrames +
                " | fec=" + fecFrames +
                " | plc=" + plcFrames +
                " | fallbackPeerFrames=" + fallbackPeerFrames +
                " | playbackDrops=" + playbackDrops +
                " | outboundQueued=" + outboundQueuedFrames +
                " | outboundBackpressureDrops=" + outboundBackpressureDrops);
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

        private void RestoreSourceAdapter()
        {
            RestoreOutboundTimelineOwnership();

            if (!sourceIntercepted ||
                sourceAdapter == null ||
                sourcePlaybackManagerField == null)
            {
                return;
            }

            try
            {
                if (stablePlaybackManager == null)
                {
                    GameObject root = GameObject.Find(RuntimeRootName);
                    stablePlaybackManager = root != null
                        ? root.GetComponent<VoiceSpatialPlaybackManager>()
                        : null;
                }

                if (stablePlaybackManager != null)
                    sourcePlaybackManagerField.SetValue(sourceAdapter, stablePlaybackManager);
            }
            catch
            {
            }

            sourceIntercepted = false;
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
                    bool sourceOwnsOutbound =
                        sourceAdapter != null &&
                        sourceOwnsOutboundMediaField != null &&
                        sourceOwnsOutboundMediaField.GetValue(sourceAdapter) is bool owns &&
                        owns;

                    if (sourceOwnsOutbound && sourceFrameHandler != null)
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

        private void DisposeRemoteStreams()
        {
            foreach (RemoteStreamState state in remoteStreams.Values)
                state.Dispose();
            remoteStreams.Clear();
        }

        private void OnDisable()
        {
            if (disposed) return;
            RestoreSourceAdapter();
            DisposeRemoteStreams();
        }

        private void OnDestroy()
        {
            if (disposed) return;
            disposed = true;
            RestoreSourceAdapter();
            DisposeRemoteStreams();
        }

        private sealed class RemoteStreamState : IDisposable
        {
            public readonly string StreamKey;
            public readonly SortedDictionary<uint, BufferedRemotePacket> Frames =
                new SortedDictionary<uint, BufferedRemotePacket>();
            public readonly IntPtr Decoder;
            public readonly WindowsPlaybackOutput Output;

            public string SessionId;
            public string SenderConnectionId;
            public string PeerUserId = string.Empty;
            public bool Initialized;
            public uint ExpectedSequence;
            public uint HighestSequence;
            public double NextPlayoutAtMs;
            public double LastArrivalAtMs;
            public double? LastTransitMs;
            public double JitterMs;
            public double TargetDelayMs = MinimumPlayoutDelayMs;
            public float NextSpatialRefreshAt;

            public RemoteStreamState(
                string streamKey,
                string sessionId,
                string senderConnectionId,
                Transform fallbackParent,
                VoiceWindowsLiveDeliveryWebGLParityWrapper owner)
            {
                StreamKey = streamKey;
                SessionId = sessionId;
                SenderConnectionId = senderConnectionId;
                LastArrivalAtMs = MonotonicMs();

                int error;
                IntPtr decoder = VoiceOpusNative.DecoderCreate(
                    SampleRate,
                    Channels,
                    out error);
                if (error < 0 || decoder == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "Voice Windows parity Opus decoder create failed: " + error);
                }

                try
                {
                    Output = new WindowsPlaybackOutput(
                        streamKey,
                        fallbackParent,
                        owner);
                    Decoder = decoder;
                }
                catch
                {
                    VoiceOpusNative.DecoderDestroy(decoder);
                    throw;
                }
            }

            public void Dispose()
            {
                Output.Dispose();
                if (Decoder != IntPtr.Zero) VoiceOpusNative.DecoderDestroy(Decoder);
            }
        }

        private readonly struct BufferedRemotePacket
        {
            public readonly byte[] Payload;
            public readonly uint MediaSequence;
            public readonly ulong MediaTimestamp100Ns;

            public BufferedRemotePacket(
                byte[] payload,
                uint mediaSequence,
                ulong mediaTimestamp100Ns)
            {
                Payload = payload;
                MediaSequence = mediaSequence;
                MediaTimestamp100Ns = mediaTimestamp100Ns;
            }
        }

        private sealed class WindowsPlaybackOutput : IDisposable
        {
            private readonly ConcurrentQueue<float[]> pcmFrames =
                new ConcurrentQueue<float[]>();
            private readonly Transform fallbackParent;
            private readonly GameObject playbackObject;
            private readonly AudioSource audioSource;
            private float[] currentFrame;
            private int currentOffset;
            private volatile bool disposed;

            public WindowsPlaybackOutput(
                string streamKey,
                Transform fallbackParent,
                MonoBehaviour owner)
            {
                this.fallbackParent = fallbackParent != null
                    ? fallbackParent
                    : owner.transform;

                playbackObject = new GameObject(
                    "Voice_WebGLParity_Playback_" + streamKey);
                playbackObject.transform.SetParent(this.fallbackParent, false);
                playbackObject.transform.localPosition = Vector3.zero;
                playbackObject.SetActive(true);

                audioSource = playbackObject.AddComponent<AudioSource>();
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
                ApplySpatialMode(null);

                audioSource.clip = AudioClip.Create(
                    "Voice_WebGLParity_PCM_" + streamKey,
                    SampleRate,
                    Channels,
                    SampleRate,
                    true,
                    FillPcm);
                audioSource.Play();
            }

            public int Enqueue(float[] pcm)
            {
                if (disposed || pcm == null || pcm.Length == 0) return 0;

                int dropped = 0;
                while (pcmFrames.Count >= MaximumPlaybackFrames)
                {
                    if (!pcmFrames.TryDequeue(out _)) break;
                    dropped += 1;
                }

                pcmFrames.Enqueue(pcm);
                EnsurePlayable();
                return dropped;
            }

            public void UpdateSpatialTarget(Transform peerTransform)
            {
                if (disposed || playbackObject == null || audioSource == null) return;

                Transform targetParent = IsPlayableParent(peerTransform)
                    ? peerTransform
                    : fallbackParent;

                if (targetParent != null && playbackObject.transform.parent != targetParent)
                    playbackObject.transform.SetParent(targetParent, false);

                playbackObject.transform.localPosition = Vector3.zero;
                ApplySpatialMode(peerTransform);
                EnsurePlayable();
            }

            public void SetAudible(bool audible)
            {
                if (disposed || audioSource == null) return;
                audioSource.mute = !audible;
            }

            private void ApplySpatialMode(Transform peerTransform)
            {
                if (audioSource == null) return;

                Transform resolvedParent = playbackObject != null
                    ? playbackObject.transform.parent
                    : fallbackParent;

                if (VoiceWindowsSpatialPlaybackPolicy.TryApply(
                        audioSource,
                        resolvedParent,
                        peerTransform))
                {
                    return;
                }

                audioSource.spatialBlend = 0f;
                audioSource.rolloffMode = AudioRolloffMode.Linear;
                audioSource.minDistance = 10000f;
                audioSource.maxDistance = 10000f;
                audioSource.priority = 0;
            }

            private static bool IsPlayableParent(Transform parent)
            {
                return parent != null && parent.gameObject.activeInHierarchy;
            }

            private void EnsurePlayable()
            {
                if (disposed || playbackObject == null || audioSource == null) return;
                if (!playbackObject.activeSelf) playbackObject.SetActive(true);
                if (!audioSource.enabled) audioSource.enabled = true;
                if (playbackObject.activeInHierarchy && !audioSource.isPlaying)
                    audioSource.Play();
            }

            private void FillPcm(float[] data)
            {
                if (disposed)
                {
                    Array.Clear(data, 0, data.Length);
                    return;
                }

                int output = 0;

                while (output < data.Length)
                {
                    if (currentFrame == null || currentOffset >= currentFrame.Length)
                    {
                        if (!pcmFrames.TryDequeue(out currentFrame))
                        {
                            currentFrame = null;
                            currentOffset = 0;
                            Array.Clear(data, output, data.Length - output);
                            return;
                        }

                        currentOffset = 0;
                    }

                    int copy = Math.Min(
                        data.Length - output,
                        currentFrame.Length - currentOffset);
                    Array.Copy(currentFrame, currentOffset, data, output, copy);
                    currentOffset += copy;
                    output += copy;
                }
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;

                while (pcmFrames.TryDequeue(out _)) { }
                currentFrame = null;
                currentOffset = 0;

                if (audioSource != null) audioSource.Stop();
                if (playbackObject != null) UnityEngine.Object.Destroy(playbackObject);
            }
        }
    }

    internal static class VoiceWindowsOpusPlcNative
    {
        private const string LibraryName = "opus";

        [DllImport(
            LibraryName,
            EntryPoint = "opus_decode_float",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int DecodeFloat(
            IntPtr decoder,
            IntPtr data,
            int length,
            [Out] float[] pcm,
            int frameSize,
            int decodeFec);
    }
}
#endif
