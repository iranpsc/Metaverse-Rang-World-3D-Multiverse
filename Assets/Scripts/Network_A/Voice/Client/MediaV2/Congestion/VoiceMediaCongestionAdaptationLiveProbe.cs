using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Voice.Client.Codec;
using Network_A.Voice.Client.Codec.Adaptation;
using Network_A.Voice.Client.Runtime;
using UnityEngine;

namespace Network_A.Voice.Client.MediaV2.Congestion
{
    internal sealed class VoiceMediaCongestionAdaptationLiveProbe : MonoBehaviour
    {
        private const string Argument = "--vme2-phase12-adaptation-live-test";
        private const int BindTimeoutMs = 5000;
        private const int WarmupPackets = 12;
        private const int NormalBatchPackets = 12;
        private const int LossBatchPackets = 48;

        private readonly object sequenceSync = new object();
        private uint nextTransportSequence = 1;
        private uint lastReceivedTransportSequence;
        private uint receivedAckMask;
        private bool hasReceivedTransportSequence;
        private TaskCompletionSource<VoiceMediaV2BindResult> bindCompletion;
        private int pongCount;
        private int appliedProfiles;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!HasArgument(Argument)) return;
            GameObject target = new GameObject("VME2_PHASE12_Congestion_Adaptation_Live_Probe");
            DontDestroyOnLoad(target);
            target.AddComponent<VoiceMediaCongestionAdaptationLiveProbe>();
        }

        // این تابع آزمون زنده را در مسیر مستقل رسانه اجرا و در پایان همه منابع آزمایشی را آزاد می کند.
        private async void Awake()
        {
            Debug.Log(
                "VME2_PHASE12_ADAPTATION_LIVE=START" +
                " | impairmentScope=probe_only" +
                " | feedsProductionVoice=False");

            try
            {
                await RunAsync();
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VME2_PHASE12_ADAPTATION_LIVE=FAIL" +
                    " | error=" + Safe(exception.Message) +
                    " | impairmentScope=probe_only" +
                    " | feedsProductionVoice=False");
            }
            finally
            {
                Destroy(gameObject);
            }
        }

        private async Task RunAsync()
        {
            VoiceClientRuntime runtime = await WaitForAuthenticatedRuntimeAsync();
            string token = (SecureTokenStorage.GetAccessToken() ?? string.Empty).Trim();
            string roomId = (MetaverseNetworkClient.roomId ?? string.Empty).Trim();
            if (token.Length == 0 || roomId.Length == 0)
                throw new InvalidOperationException("Voice media identity is not ready.");

            string streamId = Guid.NewGuid().ToString("D");
            VoiceMediaTransportFeedbackTracker tracker = new VoiceMediaTransportFeedbackTracker();
            IVoiceMediaTransportV2 platformTransport = VoiceMediaTransportV2Factory.CreateForCurrentPlatform();

            using (VoiceMediaTestImpairmentTransportV2 impairment =
                new VoiceMediaTestImpairmentTransportV2(platformTransport))
            using (VoiceObservedMediaTransportV2 transport =
                new VoiceObservedMediaTransportV2(impairment, tracker))
            using (VoiceNativeOpusCodec codec = new VoiceNativeOpusCodec(40))
            {
                transport.PacketReceived += HandlePacketReceived;
                try
                {
                    await ConnectAndBindAsync(
                        transport,
                        token,
                        roomId,
                        runtime.VoiceConnectionId,
                        streamId);

                    impairment.Configure(0, 0);
                    await SendPingBatchAsync(transport, streamId, WarmupPackets, 25);
                    await Task.Delay(500);

                    VoiceMediaTransportFeedbackSnapshot warmupSnapshot = tracker.GetSnapshot();
                    if (warmupSnapshot.AcknowledgedPackets < 5 || warmupSnapshot.SmoothedRttMs <= 0d)
                        throw new InvalidOperationException("Live feedback warmup did not produce enough acknowledged packets.");

                    double baselineRttMs = warmupSnapshot.SmoothedRttMs;
                    VoiceMediaCongestionThresholds thresholds = CreateProbeThresholds(baselineRttMs);
                    VoiceMediaCongestionStateMachine stateMachine =
                        new VoiceMediaCongestionStateMachine(thresholds);
                    VoiceOpusAdaptationController controller =
                        CreateController(codec);
                    float[] testFrame = CreateTestFrame();

                    stateMachine.Observe(warmupSnapshot);

                    await ReachStateAsync(
                        "baseline_healthy",
                        VoiceMediaCongestionState.Healthy,
                        0,
                        0,
                        5,
                        NormalBatchPackets,
                        25,
                        impairment,
                        transport,
                        tracker,
                        stateMachine,
                        controller,
                        codec,
                        testFrame,
                        streamId);

                    await ReachStateAsync(
                        "rtt_degraded",
                        VoiceMediaCongestionState.Degraded,
                        220,
                        0,
                        6,
                        NormalBatchPackets,
                        25,
                        impairment,
                        transport,
                        tracker,
                        stateMachine,
                        controller,
                        codec,
                        testFrame,
                        streamId);

                    await ReachStateAsync(
                        "rtt_congested",
                        VoiceMediaCongestionState.Congested,
                        450,
                        0,
                        6,
                        NormalBatchPackets,
                        25,
                        impairment,
                        transport,
                        tracker,
                        stateMachine,
                        controller,
                        codec,
                        testFrame,
                        streamId);

                    await ReachStateAsync(
                        "rtt_critical",
                        VoiceMediaCongestionState.Critical,
                        850,
                        0,
                        6,
                        NormalBatchPackets,
                        25,
                        impairment,
                        transport,
                        tracker,
                        stateMachine,
                        controller,
                        codec,
                        testFrame,
                        streamId);

                    await ReachStateAsync(
                        "rtt_recovered",
                        VoiceMediaCongestionState.Healthy,
                        0,
                        0,
                        14,
                        NormalBatchPackets,
                        25,
                        impairment,
                        transport,
                        tracker,
                        stateMachine,
                        controller,
                        codec,
                        testFrame,
                        streamId);

                    long lostBeforeImpairment = tracker.GetSnapshot().FinalizedLostPackets;
                    VoiceMediaTransportFeedbackSnapshot lossSnapshot = await ReachStateAsync(
                        "loss_congested",
                        VoiceMediaCongestionState.Congested,
                        0,
                        3,
                        6,
                        LossBatchPackets,
                        15,
                        impairment,
                        transport,
                        tracker,
                        stateMachine,
                        controller,
                        codec,
                        testFrame,
                        streamId);

                    if (lossSnapshot.FinalizedLostPackets <= lostBeforeImpairment)
                        throw new InvalidOperationException("Controlled packet loss was not finalized by live acknowledgements.");

                    VoiceMediaTransportFeedbackSnapshot finalSnapshot = await ReachStateAsync(
                        "loss_recovered",
                        VoiceMediaCongestionState.Healthy,
                        0,
                        0,
                        14,
                        NormalBatchPackets,
                        25,
                        impairment,
                        transport,
                        tracker,
                        stateMachine,
                        controller,
                        codec,
                        testFrame,
                        streamId);

                    RequireHealthyEncoder(codec);

                    Debug.Log(
                        "VME2_PHASE12_ADAPTATION_LIVE=PASS" +
                        " | mediaBound=True" +
                        " | realFeedback=True" +
                        " | rttDegraded=True" +
                        " | rttCongested=True" +
                        " | rttCritical=True" +
                        " | rttRecovered=True" +
                        " | packetLossCongested=True" +
                        " | packetLossRecovered=True" +
                        " | finalizedLostPackets=" + finalSnapshot.FinalizedLostPackets +
                        " | droppedPings=" + impairment.DroppedOutgoingPings +
                        " | pongCount=" + pongCount +
                        " | appliedProfiles=" + appliedProfiles +
                        " | baselineRttMs=" + baselineRttMs.ToString("F3") +
                        " | finalRttMs=" + finalSnapshot.SmoothedRttMs.ToString("F3") +
                        " | productionThresholdsLocked=False" +
                        " | productionProfilesLocked=False" +
                        " | impairmentScope=probe_only" +
                        " | mediaTransport=" + platformTransport.GetType().Name +
                        " | feedsProductionVoice=False");
                }
                finally
                {
                    transport.PacketReceived -= HandlePacketReceived;
                }
            }
        }

        private static async Task<VoiceClientRuntime> WaitForAuthenticatedRuntimeAsync()
        {
            VoiceClientRuntime runtime = null;
            for (int attempt = 0; attempt < 1800; attempt++)
            {
                runtime = UnityEngine.Object.FindFirstObjectByType<VoiceClientRuntime>();
                if (runtime != null && runtime.IsAuthenticated && Guid.TryParse(runtime.VoiceConnectionId, out _))
                    return runtime;
                await Task.Delay(100);
            }

            throw new TimeoutException("Voice control plane was not authenticated in time.");
        }

        private async Task ConnectAndBindAsync(
            VoiceObservedMediaTransportV2 transport,
            string token,
            string roomId,
            string controlConnectionId,
            string streamId)
        {
            bool connected = await transport.ConnectAsync(
                ResolveLiveMediaEndpoint(),
                CancellationToken.None);
            if (!connected) throw new InvalidOperationException("Voice media transport connect failed.");

            bindCompletion = new TaskCompletionSource<VoiceMediaV2BindResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            VoiceMediaV2Packet bind = CreatePacket(
                VoiceMediaV2PacketKind.BindRequest,
                streamId,
                VoiceMediaV2BindPayload.EncodeRequest(
                    token,
                    roomId,
                    controlConnectionId,
                    ResolvePlatformCode(),
                    Application.version));

            if (!await transport.SendAsync(bind.Encode(), CancellationToken.None))
                throw new InvalidOperationException("Voice media bind send failed.");

            Task finished = await Task.WhenAny(
                bindCompletion.Task,
                Task.Delay(BindTimeoutMs));
            if (finished != bindCompletion.Task)
                throw new TimeoutException("Voice media bind result timed out.");

            VoiceMediaV2BindResult result = await bindCompletion.Task;
            if (!result.Success ||
                !string.Equals(
                    result.ControlConnectionId,
                    controlConnectionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Voice media bind was rejected: " + result.Message);
            }
        }

        private async Task<VoiceMediaTransportFeedbackSnapshot> ReachStateAsync(
            string stage,
            VoiceMediaCongestionState expectedState,
            int incomingDelayMs,
            int dropEveryNthPing,
            int maximumBatches,
            int packetsPerBatch,
            int intervalMs,
            VoiceMediaTestImpairmentTransportV2 impairment,
            VoiceObservedMediaTransportV2 transport,
            VoiceMediaTransportFeedbackTracker tracker,
            VoiceMediaCongestionStateMachine stateMachine,
            VoiceOpusAdaptationController controller,
            VoiceNativeOpusCodec codec,
            float[] testFrame,
            string streamId)
        {
            impairment.Configure(incomingDelayMs, dropEveryNthPing);
            VoiceMediaTransportFeedbackSnapshot snapshot = tracker.GetSnapshot();

            for (int batch = 1; batch <= maximumBatches; batch++)
            {
                await SendPingBatchAsync(
                    transport,
                    streamId,
                    packetsPerBatch,
                    intervalMs);
                await Task.Delay(incomingDelayMs + 500);

                snapshot = tracker.GetSnapshot();
                VoiceMediaCongestionObservation observation =
                    stateMachine.Observe(snapshot);
                ApplyStableState(
                    controller,
                    codec,
                    testFrame,
                    observation.State);

                Debug.Log(
                    "VME2_PHASE12_ADAPTATION_LIVE_STAGE" +
                    " | stage=" + stage +
                    " | batch=" + batch +
                    " | state=" + observation.State +
                    " | candidate=" + observation.Candidate +
                    " | stateChanged=" + observation.StateChanged +
                    " | acknowledgedWindow=" + observation.AcknowledgedPacketsInWindow +
                    " | lostWindow=" + observation.LostPacketsInWindow +
                    " | lossPercentWindow=" + observation.LossPercentInWindow.ToString("F3") +
                    " | smoothedRttMs=" + observation.SmoothedRttMs.ToString("F3") +
                    " | inFlight=" + observation.InFlightPackets);

                if (stateMachine.State == expectedState) return snapshot;
            }

            throw new InvalidOperationException(
                stage + " expected " + expectedState + " but reached " + stateMachine.State + ".");
        }

        private static VoiceMediaCongestionThresholds CreateProbeThresholds(double baselineRttMs)
        {
            return new VoiceMediaCongestionThresholds(
                5,
                4d,
                12d,
                35d,
                baselineRttMs + 120d,
                baselineRttMs + 320d,
                baselineRttMs + 620d,
                128,
                256,
                512,
                1000d,
                2000d,
                4000d,
                5000,
                2,
                3);
        }

        private static VoiceOpusAdaptationController CreateController(
            VoiceNativeOpusCodec codec)
        {
            return new VoiceOpusAdaptationController(
                new VoiceNativeOpusEncoderAdaptationTarget(codec),
                new VoiceOpusAdaptationProfile(
                    VoiceMediaCongestionState.Healthy,
                    40,
                    false,
                    0),
                new VoiceOpusAdaptationProfile(
                    VoiceMediaCongestionState.Degraded,
                    32,
                    true,
                    10),
                new VoiceOpusAdaptationProfile(
                    VoiceMediaCongestionState.Congested,
                    28,
                    true,
                    20),
                new VoiceOpusAdaptationProfile(
                    VoiceMediaCongestionState.Critical,
                    28,
                    true,
                    30));
        }

        private void ApplyStableState(
            VoiceOpusAdaptationController controller,
            VoiceNativeOpusCodec codec,
            float[] testFrame,
            VoiceMediaCongestionState state)
        {
            if (!controller.TryApplyState(
                    state,
                    out VoiceOpusAdaptationProfile profile))
            {
                return;
            }

            byte[] encoded = codec.Encode(testFrame);
            if (encoded == null || encoded.Length == 0)
                throw new InvalidOperationException("Adapted native encoder did not produce an Opus packet.");
            if (codec.BitrateKbps != profile.BitrateKbps ||
                codec.InbandFecEnabled != profile.InbandFecEnabled ||
                codec.ExpectedPacketLossPercent != profile.ExpectedPacketLossPercent)
            {
                throw new InvalidOperationException("Native encoder settings do not match the selected congestion profile.");
            }

            appliedProfiles++;
        }

        private static void RequireHealthyEncoder(VoiceNativeOpusCodec codec)
        {
            if (codec.BitrateKbps != 40 ||
                codec.InbandFecEnabled ||
                codec.ExpectedPacketLossPercent != 0)
            {
                throw new InvalidOperationException("Native encoder did not return to the healthy profile.");
            }
        }

        private async Task SendPingBatchAsync(
            VoiceObservedMediaTransportV2 transport,
            string streamId,
            int packetCount,
            int intervalMs)
        {
            for (int index = 0; index < packetCount; index++)
            {
                byte[] payload = BitConverter.GetBytes(
                    System.Diagnostics.Stopwatch.GetTimestamp());
                VoiceMediaV2Packet ping = CreatePacket(
                    VoiceMediaV2PacketKind.Ping,
                    streamId,
                    payload);

                if (!await transport.SendAsync(
                        ping.Encode(),
                        CancellationToken.None))
                {
                    throw new InvalidOperationException("Voice media adaptation ping send failed.");
                }

                if (intervalMs > 0) await Task.Delay(intervalMs);
            }
        }

        private void HandlePacketReceived(byte[] bytes)
        {
            VoiceMediaV2Packet packet = VoiceMediaV2Packet.Decode(bytes);
            ObserveTransportSequence(packet.TransportSequence);

            if (packet.Kind == VoiceMediaV2PacketKind.BindResult)
            {
                bindCompletion?.TrySetResult(
                    VoiceMediaV2BindPayload.DecodeResult(packet.Payload));
                return;
            }

            if (packet.Kind == VoiceMediaV2PacketKind.Pong)
                Interlocked.Increment(ref pongCount);
        }

        private VoiceMediaV2Packet CreatePacket(
            VoiceMediaV2PacketKind kind,
            string streamId,
            byte[] payload)
        {
            uint ackSequence;
            uint ackMask;
            uint sequence;

            lock (sequenceSync)
            {
                ackSequence = hasReceivedTransportSequence
                    ? lastReceivedTransportSequence
                    : 0;
                ackMask = receivedAckMask;
                sequence = nextTransportSequence;
                nextTransportSequence = sequence == uint.MaxValue
                    ? 1
                    : sequence + 1;
            }

            return new VoiceMediaV2Packet
            {
                Kind = kind,
                Codec = VoiceMediaV2Codec.None,
                Flags = VoiceMediaV2Flags.None,
                TransportSequence = sequence,
                MediaSequence = 0,
                MediaTimestamp100Ns = 0,
                AckTransportSequence = ackSequence,
                AckMask = ackMask,
                SessionId = VoiceMediaV2Constants.EmptyUuid,
                StreamId = streamId,
                SenderId = VoiceMediaV2Constants.EmptyUuid,
                SecurityContextId = 0,
                Payload = payload ?? Array.Empty<byte>()
            };
        }

        private void ObserveTransportSequence(uint sequence)
        {
            lock (sequenceSync)
            {
                if (!hasReceivedTransportSequence)
                {
                    hasReceivedTransportSequence = true;
                    lastReceivedTransportSequence = sequence;
                    receivedAckMask = 0;
                    return;
                }

                if (sequence > lastReceivedTransportSequence)
                {
                    uint delta = sequence - lastReceivedTransportSequence;
                    receivedAckMask = delta >= 32
                        ? 0u
                        : (receivedAckMask << (int)delta) |
                          (1u << ((int)delta - 1));
                    lastReceivedTransportSequence = sequence;
                    return;
                }

                uint distance = lastReceivedTransportSequence - sequence;
                if (distance > 0 && distance <= 32)
                    receivedAckMask |= 1u << ((int)distance - 1);
            }
        }

        private static float[] CreateTestFrame()
        {
            float[] samples = new float[VoiceNativeOpusCodec.FrameSamples];
            const double frequencyHz = 440d;
            const double amplitude = 0.05d;

            for (int index = 0; index < samples.Length; index++)
            {
                double phase =
                    2d * Math.PI * frequencyHz * index /
                    VoiceNativeOpusCodec.SampleRate;
                samples[index] = (float)(Math.Sin(phase) * amplitude);
            }

            return samples;
        }

        private static string ResolveLiveMediaEndpoint()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            string url = ServerConfig.RealtimeWebSocketUrl;
            return url + (url.Contains("?") ? "&" : "?") + "transport=voice-media-v2";
#else
            return ServerConfig.BuildRealtimeGrpcStreamingTarget();
#endif
        }

        private static byte ResolvePlatformCode()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return 1;
#elif UNITY_ANDROID && !UNITY_EDITOR
            return 3;
#else
            return 2;
#endif
        }

        private static bool HasArgument(string expected)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int index = 0; index < args.Length; index++)
            {
                if (string.Equals(
                        args[index],
                        expected,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string Safe(string value)
        {
            return (value ?? string.Empty)
                .Replace('\n', '_')
                .Replace('\r', '_')
                .Replace('|', '/');
        }
    }
}
