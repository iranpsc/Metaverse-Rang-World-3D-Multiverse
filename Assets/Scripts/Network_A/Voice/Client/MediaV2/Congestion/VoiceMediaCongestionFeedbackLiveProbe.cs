using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Voice.Client.Runtime;
using UnityEngine;

namespace Network_A.Voice.Client.MediaV2.Congestion
{
    internal sealed class VoiceMediaCongestionFeedbackLiveProbe : MonoBehaviour
    {
        private const string LiveArgument = "--vme2-phase12-live-test";
        private const int BindTimeoutMs = 5000;
        private readonly object sequenceSync = new object();
        private uint nextTransportSequence = 1;
        private uint lastReceivedTransportSequence;
        private uint receivedAckMask;
        private bool hasReceivedTransportSequence;
        private TaskCompletionSource<VoiceMediaV2BindResult> bindCompletion;
        private int pongCount;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!HasArgument(LiveArgument)) return;
            GameObject target = new GameObject("VME2_PHASE12_Feedback_Live_Probe");
            DontDestroyOnLoad(target);
            target.AddComponent<VoiceMediaCongestionFeedbackLiveProbe>();
        }

        // این تابع آزمون زنده را فقط روی اتصال رسانه مستقل اجرا می کند و هیچ فریم صدای واقعی را وارد این مسیر نمی کند.
        private async void Awake()
        {
            Debug.Log("VME2_PHASE12_LIVE_FEEDBACK=START | feedsProductionVoice=False");
            try
            {
                await RunAsync();
            }
            catch (Exception exception)
            {
                Debug.LogError("VME2_PHASE12_LIVE_FEEDBACK=FAIL | error=" + Safe(exception.Message) + " | feedsProductionVoice=False");
            }
        }

        private async Task RunAsync()
        {
            VoiceClientRuntime runtime = null;
            while (runtime == null)
            {
                runtime = UnityEngine.Object.FindFirstObjectByType<VoiceClientRuntime>();
                if (runtime == null) await Task.Delay(100);
            }

            for (int attempt = 0; attempt < 300; attempt++)
            {
                if (runtime.IsAuthenticated && Guid.TryParse(runtime.VoiceConnectionId, out _)) break;
                await Task.Delay(100);
            }

            if (!runtime.IsAuthenticated || !Guid.TryParse(runtime.VoiceConnectionId, out _))
                throw new InvalidOperationException("Voice control plane was not authenticated in time after runtime creation.");

            string token = (SecureTokenStorage.GetAccessToken() ?? string.Empty).Trim();
            string room = (MetaverseNetworkClient.roomId ?? string.Empty).Trim();
            if (token.Length == 0 || room.Length == 0) throw new InvalidOperationException("Voice media identity is not ready.");

            string streamId = Guid.NewGuid().ToString("D");
            VoiceMediaTransportFeedbackTracker tracker = new VoiceMediaTransportFeedbackTracker();
            IVoiceMediaTransportV2 inner = VoiceMediaTransportV2Factory.CreateForCurrentPlatform();
            using VoiceObservedMediaTransportV2 transport = new VoiceObservedMediaTransportV2(inner, tracker);
            transport.PacketReceived += HandlePacketReceived;

            bool connected = await transport.ConnectAsync(ResolveLiveMediaEndpoint(), CancellationToken.None);
            if (!connected) throw new InvalidOperationException("Voice media transport connect failed.");

            bindCompletion = new TaskCompletionSource<VoiceMediaV2BindResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            VoiceMediaV2Packet bind = CreatePacket(
                VoiceMediaV2PacketKind.BindRequest,
                streamId,
                VoiceMediaV2BindPayload.EncodeRequest(token, room, runtime.VoiceConnectionId, ResolvePlatformCode(), Application.version));
            if (!await transport.SendAsync(bind.Encode(), CancellationToken.None)) throw new InvalidOperationException("Voice media bind send failed.");

            Task finished = await Task.WhenAny(bindCompletion.Task, Task.Delay(BindTimeoutMs));
            if (finished != bindCompletion.Task) throw new TimeoutException("Voice media bind result timed out.");
            VoiceMediaV2BindResult result = await bindCompletion.Task;
            if (!result.Success || !string.Equals(result.ControlConnectionId, runtime.VoiceConnectionId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Voice media bind was rejected: " + result.Message);

            for (int index = 0; index < 20; index++)
            {
                byte[] payload = BitConverter.GetBytes(System.Diagnostics.Stopwatch.GetTimestamp());
                VoiceMediaV2Packet ping = CreatePacket(VoiceMediaV2PacketKind.Ping, streamId, payload);
                if (!await transport.SendAsync(ping.Encode(), CancellationToken.None)) throw new InvalidOperationException("Voice media feedback ping send failed.");
                await Task.Delay(100);
            }

            await Task.Delay(500);
            VoiceMediaTransportFeedbackSnapshot snapshot = tracker.GetSnapshot();
            bool pass = snapshot.AcknowledgedPackets > 0 &&
                        snapshot.LatestRttMs > 0d &&
                        snapshot.SmoothedRttMs > 0d &&
                        snapshot.AcknowledgedThroughputKbps > 0d &&
                        snapshot.SendFailures == 0 &&
                        pongCount > 0;

            Debug.Log(
                "VME2_PHASE12_LIVE_FEEDBACK=" + (pass ? "PASS" : "FAIL") +
                " | controlAuthenticated=True" +
                " | mediaBound=True" +
                " | pongCount=" + pongCount +
                " | acknowledgedPackets=" + snapshot.AcknowledgedPackets +
                " | finalizedLostPackets=" + snapshot.FinalizedLostPackets +
                " | finalizedLossPercent=" + snapshot.FinalizedLossPercent.ToString("F3") +
                " | latestRttMs=" + snapshot.LatestRttMs.ToString("F3") +
                " | smoothedRttMs=" + snapshot.SmoothedRttMs.ToString("F3") +
                " | throughputKbps=" + snapshot.AcknowledgedThroughputKbps.ToString("F3") +
                " | inFlight=" + snapshot.InFlightPackets +
                " | mediaTransport=" + inner.GetType().Name +
                " | feedsProductionVoice=False");

            transport.PacketReceived -= HandlePacketReceived;
        }

        private void HandlePacketReceived(byte[] bytes)
        {
            VoiceMediaV2Packet packet = VoiceMediaV2Packet.Decode(bytes);
            ObserveTransportSequence(packet.TransportSequence);
            if (packet.Kind == VoiceMediaV2PacketKind.BindResult)
            {
                bindCompletion?.TrySetResult(VoiceMediaV2BindPayload.DecodeResult(packet.Payload));
                return;
            }
            if (packet.Kind == VoiceMediaV2PacketKind.Pong) Interlocked.Increment(ref pongCount);
        }

        private VoiceMediaV2Packet CreatePacket(VoiceMediaV2PacketKind kind, string streamId, byte[] payload)
        {
            uint ackSequence;
            uint ackMask;
            uint sequence;
            lock (sequenceSync)
            {
                ackSequence = hasReceivedTransportSequence ? lastReceivedTransportSequence : 0;
                ackMask = receivedAckMask;
                sequence = nextTransportSequence;
                nextTransportSequence = sequence == uint.MaxValue ? 1 : sequence + 1;
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
                    receivedAckMask = delta >= 32 ? 0u : (receivedAckMask << (int)delta) | (1u << ((int)delta - 1));
                    lastReceivedTransportSequence = sequence;
                    return;
                }

                uint distance = lastReceivedTransportSequence - sequence;
                if (distance > 0 && distance <= 32) receivedAckMask |= 1u << ((int)distance - 1);
            }
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
                if (string.Equals(args[index], expected, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string Safe(string value)
        {
            return (value ?? string.Empty).Replace('\n', '_').Replace('\r', '_').Replace('|', '/');
        }
    }
}
