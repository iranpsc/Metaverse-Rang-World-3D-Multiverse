#if !UNITY_WEBGL || UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Voice.Client.Runtime;
using UnityEngine;

namespace Network_A.Voice.Client.MediaV2.Security
{
    internal sealed class VoiceMediaPacketSecurityLiveProbe : MonoBehaviour
    {
        private const string ProbeArgument = "--vme2-phase13-packet-security-live-test";
        private const int BindTimeoutMs = 5000;
        private TaskCompletionSource<VoiceMediaV2BindResult> bindCompletion;
        private int pongCount;
        private string lastFailure = string.Empty;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!HasArgument(ProbeArgument)) return;
            GameObject target = new GameObject("VME2_Media_Packet_Security_Live_Probe");
            DontDestroyOnLoad(target);
            target.AddComponent<VoiceMediaPacketSecurityLiveProbe>();
        }

        private async void Awake()
        {
            Debug.Log("VME2_PHASE13_PACKET_SECURITY_LIVE=START | platformScope=Windows | feedsProductionVoice=False");
            try { await RunAsync(); }
            catch (Exception exception) { Debug.LogError("VME2_PHASE13_PACKET_SECURITY_LIVE=FAIL | error=" + Safe(exception.Message) + " | platformScope=Windows | feedsProductionVoice=False"); }
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
            if (!runtime.IsAuthenticated || !Guid.TryParse(runtime.VoiceConnectionId, out _)) throw new InvalidOperationException("Voice control plane was not authenticated in time.");

            string accessToken = (SecureTokenStorage.GetAccessToken() ?? string.Empty).Trim();
            string roomId = (MetaverseNetworkClient.roomId ?? string.Empty).Trim();
            if (accessToken.Length == 0 || roomId.Length == 0) throw new InvalidOperationException("Voice media identity is not ready.");

            string streamId = Guid.NewGuid().ToString("D");
            IVoiceMediaTransportV2 inner = VoiceMediaTransportV2Factory.CreateForCurrentPlatform();
            string innerTransportName = inner.GetType().Name;
            using (VoiceSecuredMediaTransportV2 transport = new VoiceSecuredMediaTransportV2(inner))
            {
                transport.PacketReceived += HandlePacketReceived;
                transport.Failed += HandleFailure;
                bool connected = await transport.ConnectAsync(ResolveLiveMediaEndpoint(), CancellationToken.None);
                if (!connected) throw new InvalidOperationException("Voice media transport connection failed.");

                bindCompletion = new TaskCompletionSource<VoiceMediaV2BindResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                VoiceMediaV2Packet bind = BuildPacket(VoiceMediaV2PacketKind.BindRequest, 1, streamId, VoiceMediaV2BindPayload.EncodeRequest(accessToken, roomId, runtime.VoiceConnectionId, 2, Application.version));
                if (!await transport.SendAsync(bind.Encode(), CancellationToken.None)) throw new InvalidOperationException("Voice media bind send failed.");
                Task bindFinished = await Task.WhenAny(bindCompletion.Task, Task.Delay(BindTimeoutMs));
                if (bindFinished != bindCompletion.Task) throw new TimeoutException("Voice media secure bind timed out: " + lastFailure);
                VoiceMediaV2BindResult bindResult = await bindCompletion.Task;
                if (!bindResult.Success || !string.Equals(bindResult.ControlConnectionId, runtime.VoiceConnectionId, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Voice media secure bind was rejected: " + bindResult.Message);
                if (!transport.IsSecurityReady) throw new InvalidOperationException("Voice media security negotiation did not complete.");

                byte[] firstPing = BuildPacket(VoiceMediaV2PacketKind.Ping, 2, streamId, new byte[] { 1, 2, 3, 4 }).Encode();
                if (!await transport.SendAsync(firstPing, CancellationToken.None)) throw new InvalidOperationException("First protected ping failed.");
                if (!await WaitForPongCountAsync(1, 2000)) throw new TimeoutException("First protected pong timed out: " + lastFailure);
                if (!await transport.SendAsync(firstPing, CancellationToken.None)) throw new InvalidOperationException("Duplicate protected ping send failed.");
                await Task.Delay(400);
                bool duplicateReplayDropped = Volatile.Read(ref pongCount) == 1;

                byte[] secondPing = BuildPacket(VoiceMediaV2PacketKind.Ping, 3, streamId, new byte[] { 5, 6, 7, 8 }).Encode();
                if (!await transport.SendAsync(secondPing, CancellationToken.None)) throw new InvalidOperationException("Second protected ping failed.");
                if (!await WaitForPongCountAsync(2, 2000)) throw new TimeoutException("Second protected pong timed out: " + lastFailure);

                VoiceSecuredMediaTransportStats stats = transport.GetStats();
                bool bidirectionalProtection = stats.OutboundProtectedPackets == 3 && stats.InboundProtectedPackets == 2;
                bool bidirectionalEncryption = stats.EncryptedPackets == 3 && stats.DecryptedPackets == 2;
                bool pass = stats.SecurityReady && stats.SecurityContextId != 0 && stats.NegotiatedContexts == 1 && duplicateReplayDropped && bidirectionalProtection && bidirectionalEncryption && Volatile.Read(ref pongCount) == 2;
                Debug.Log(
                    "VME2_PHASE13_PACKET_SECURITY_LIVE=" + (pass ? "PASS" : "FAIL") +
                    " | controlAuthenticated=True" +
                    " | mediaBound=True" +
                    " | securityNegotiated=" + stats.SecurityReady +
                    " | nonZeroContext=" + (stats.SecurityContextId != 0) +
                    " | bidirectionalProtection=" + bidirectionalProtection +
                    " | bidirectionalEncryption=" + bidirectionalEncryption +
                    " | payloadEncryption=" + bidirectionalEncryption +
                    " | duplicateReplayDropped=" + duplicateReplayDropped +
                    " | pongCount=" + Volatile.Read(ref pongCount) +
                    " | outboundProtectedPackets=" + stats.OutboundProtectedPackets +
                    " | inboundProtectedPackets=" + stats.InboundProtectedPackets +
                    " | encryptedPackets=" + stats.EncryptedPackets +
                    " | decryptedPackets=" + stats.DecryptedPackets +
                    " | mediaTransport=" + innerTransportName +
                    " | platformScope=Windows" +
                    " | feedsProductionVoice=False");
                Debug.Log(
                    "VME2_PHASE13_MEDIA_ENCRYPTION_LIVE=" + (pass ? "PASS" : "FAIL") +
                    " | securityNegotiated=" + stats.SecurityReady +
                    " | nonZeroContext=" + (stats.SecurityContextId != 0) +
                    " | bidirectionalEncryption=" + bidirectionalEncryption +
                    " | replayProtection=" + duplicateReplayDropped +
                    " | encryptedPackets=" + stats.EncryptedPackets +
                    " | decryptedPackets=" + stats.DecryptedPackets +
                    " | mediaTransport=" + innerTransportName +
                    " | platformScope=Windows" +
                    " | feedsProductionVoice=False");
                if (!pass) throw new InvalidOperationException("Voice media live packet security assertions failed.");
                transport.PacketReceived -= HandlePacketReceived;
                transport.Failed -= HandleFailure;
                await transport.DisconnectAsync("security_probe_complete", CancellationToken.None);
            }
        }

        private void HandlePacketReceived(byte[] bytes)
        {
            try
            {
                VoiceMediaV2Packet packet = VoiceMediaV2Packet.Decode(bytes);
                if (packet.Kind == VoiceMediaV2PacketKind.BindResult) bindCompletion?.TrySetResult(VoiceMediaV2BindPayload.DecodeResult(packet.Payload));
                else if (packet.Kind == VoiceMediaV2PacketKind.Pong) Interlocked.Increment(ref pongCount);
            }
            catch (Exception exception) { lastFailure = exception.Message; }
        }

        private void HandleFailure(string message) { lastFailure = message ?? string.Empty; }

        private async Task<bool> WaitForPongCountAsync(int expected, int timeoutMs)
        {
            int elapsed = 0;
            while (elapsed < timeoutMs)
            {
                if (Volatile.Read(ref pongCount) >= expected) return true;
                await Task.Delay(25);
                elapsed += 25;
            }
            return Volatile.Read(ref pongCount) >= expected;
        }

        private static VoiceMediaV2Packet BuildPacket(VoiceMediaV2PacketKind kind, uint sequence, string streamId, byte[] payload)
        {
            return new VoiceMediaV2Packet { Kind = kind, Codec = VoiceMediaV2Codec.None, Flags = VoiceMediaV2Flags.None, TransportSequence = sequence, MediaSequence = 0, MediaTimestamp100Ns = 0, AckTransportSequence = 0, AckMask = 0, SessionId = VoiceMediaV2Constants.EmptyUuid, StreamId = streamId, SenderId = VoiceMediaV2Constants.EmptyUuid, SecurityContextId = 0, Payload = payload ?? Array.Empty<byte>() };
        }

        private static string ResolveLiveMediaEndpoint()
        {
            return ServerConfig.BuildRealtimeGrpcStreamingTarget();
        }

        private static bool HasArgument(string expected)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length; index++) if (string.Equals(arguments[index], expected, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string Safe(string value) { return (value ?? string.Empty).Replace('\n', '_').Replace('\r', '_').Replace('|', '/'); }
    }
}
#endif
