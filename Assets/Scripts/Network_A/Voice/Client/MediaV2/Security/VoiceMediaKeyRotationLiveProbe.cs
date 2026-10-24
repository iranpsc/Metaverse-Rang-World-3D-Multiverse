#if !UNITY_WEBGL || UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Voice.Client.Runtime;
using UnityEngine;

namespace Network_A.Voice.Client.MediaV2.Security
{
    internal sealed class VoiceMediaKeyRotationLiveProbe : MonoBehaviour
    {
        private const string ProbeArgument = "--vme2-phase13-key-rotation-live-test";
        private const int AuthenticationTimeoutMs = 30000;
        private const int PacketTimeoutMs = 5000;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!HasArgument(ProbeArgument)) return;
            GameObject target = new GameObject("VME2_Media_Key_Rotation_Live_Probe");
            DontDestroyOnLoad(target);
            target.AddComponent<VoiceMediaKeyRotationLiveProbe>();
        }

        private async void Awake()
        {
            Debug.Log("VME2_PHASE13_KEY_ROTATION_LIVE=START | platformScope=Windows | feedsProductionVoice=False");
            try { await RunAsync(); }
            catch (Exception exception) { Debug.LogError("VME2_PHASE13_KEY_ROTATION_LIVE=FAIL | error=" + Safe(exception.Message) + " | platformScope=Windows | feedsProductionVoice=False"); }
        }

        private static async Task RunAsync()
        {
            VoiceClientRuntime runtime = null;
            while (runtime == null)
            {
                runtime = UnityEngine.Object.FindFirstObjectByType<VoiceClientRuntime>();
                if (runtime == null) await Task.Delay(100);
            }
            int waitedMs = 0;
            while ((!runtime.IsAuthenticated || !Guid.TryParse(runtime.VoiceConnectionId, out _)) && waitedMs < AuthenticationTimeoutMs)
            {
                await Task.Delay(100);
                waitedMs += 100;
            }
            if (!runtime.IsAuthenticated || !Guid.TryParse(runtime.VoiceConnectionId, out _)) throw new InvalidOperationException("Voice control plane was not authenticated in time.");
            string accessToken = (SecureTokenStorage.GetAccessToken() ?? string.Empty).Trim();
            string roomId = (MetaverseNetworkClient.roomId ?? string.Empty).Trim();
            if (accessToken.Length == 0 || roomId.Length == 0) throw new InvalidOperationException("Voice media identity is not ready.");

            string streamId = Guid.NewGuid().ToString("D");
            RotationConnectionResult first = await ConnectOnceAsync(accessToken, roomId, runtime.VoiceConnectionId, streamId, 1);
            await Task.Delay(500);
            RotationConnectionResult second = await ConnectOnceAsync(accessToken, roomId, runtime.VoiceConnectionId, streamId, 2);
            bool freshContext = first.SecurityContextId != 0 && second.SecurityContextId != 0 && first.SecurityContextId != second.SecurityContextId;
            bool twoEncryptedConnections = first.EncryptedPackets == 1 && first.DecryptedPackets == 1 && second.EncryptedPackets == 1 && second.DecryptedPackets == 1;
            bool pass = first.Bound && second.Bound && first.PongReceived && second.PongReceived && freshContext && twoEncryptedConnections;
            Debug.Log(
                "VME2_PHASE13_KEY_ROTATION_LIVE=" + (pass ? "PASS" : "FAIL") +
                " | firstContext=" + first.SecurityContextId +
                " | secondContext=" + second.SecurityContextId +
                " | freshContext=" + freshContext +
                " | reconnectWithNewTransport=True" +
                " | twoEncryptedConnections=" + twoEncryptedConnections +
                " | firstPong=" + first.PongReceived +
                " | secondPong=" + second.PongReceived +
                " | mediaTransport=" + second.TransportName +
                " | platformScope=Windows" +
                " | feedsProductionVoice=False");
            if (!pass) throw new InvalidOperationException("Voice media live key rotation assertions failed.");
        }

        private static async Task<RotationConnectionResult> ConnectOnceAsync(string accessToken, string roomId, string controlConnectionId, string streamId, byte payloadValue)
        {
            IVoiceMediaTransportV2 inner = VoiceMediaTransportV2Factory.CreateForCurrentPlatform();
            string transportName = inner.GetType().Name;
            using (VoiceSecuredMediaTransportV2 transport = new VoiceSecuredMediaTransportV2(inner))
            {
                TaskCompletionSource<VoiceMediaV2BindResult> bindCompletion = new TaskCompletionSource<VoiceMediaV2BindResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> pongCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                string failure = string.Empty;
                Action<string> failureHandler = message => failure = message ?? string.Empty;
                Action<byte[]> packetHandler = bytes =>
                {
                    try
                    {
                        VoiceMediaV2Packet packet = VoiceMediaV2Packet.Decode(bytes);
                        if (packet.Kind == VoiceMediaV2PacketKind.BindResult) bindCompletion.TrySetResult(VoiceMediaV2BindPayload.DecodeResult(packet.Payload));
                        if (packet.Kind == VoiceMediaV2PacketKind.Pong) pongCompletion.TrySetResult(true);
                    }
                    catch (Exception exception) { failure = exception.Message; }
                };
                transport.Failed += failureHandler;
                transport.PacketReceived += packetHandler;
                try
                {
                    if (!await transport.ConnectAsync(ServerConfig.BuildRealtimeGrpcStreamingTarget(), CancellationToken.None)) throw new InvalidOperationException("Voice media transport connection failed: " + failure);
                    VoiceMediaV2Packet bind = BuildPacket(VoiceMediaV2PacketKind.BindRequest, 1, streamId, VoiceMediaV2BindPayload.EncodeRequest(accessToken, roomId, controlConnectionId, 2, Application.version));
                    if (!await transport.SendAsync(bind.Encode(), CancellationToken.None)) throw new InvalidOperationException("Voice media bind send failed: " + failure);
                    if (await Task.WhenAny(bindCompletion.Task, Task.Delay(PacketTimeoutMs)) != bindCompletion.Task) throw new TimeoutException("Voice media bind timed out: " + failure);
                    VoiceMediaV2BindResult bindResult = await bindCompletion.Task;
                    if (!bindResult.Success || !transport.IsSecurityReady) throw new InvalidOperationException("Voice media secure bind failed: " + bindResult.Message);
                    VoiceMediaV2Packet ping = BuildPacket(VoiceMediaV2PacketKind.Ping, 2, streamId, new byte[] { payloadValue, 7, 8, 9 });
                    if (!await transport.SendAsync(ping.Encode(), CancellationToken.None)) throw new InvalidOperationException("Voice media protected ping failed: " + failure);
                    if (await Task.WhenAny(pongCompletion.Task, Task.Delay(PacketTimeoutMs)) != pongCompletion.Task) throw new TimeoutException("Voice media protected pong timed out: " + failure);
                    VoiceSecuredMediaTransportStats stats = transport.GetStats();
                    await transport.DisconnectAsync("key_rotation_probe_connection_complete", CancellationToken.None);
                    return new RotationConnectionResult(true, pongCompletion.Task.Result, stats.SecurityContextId, stats.EncryptedPackets, stats.DecryptedPackets, transportName);
                }
                finally
                {
                    transport.Failed -= failureHandler;
                    transport.PacketReceived -= packetHandler;
                }
            }
        }

        private static VoiceMediaV2Packet BuildPacket(VoiceMediaV2PacketKind kind, uint sequence, string streamId, byte[] payload)
        {
            return new VoiceMediaV2Packet { Kind = kind, Codec = VoiceMediaV2Codec.None, Flags = VoiceMediaV2Flags.None, TransportSequence = sequence, MediaSequence = 0, MediaTimestamp100Ns = 0, AckTransportSequence = 0, AckMask = 0, SessionId = VoiceMediaV2Constants.EmptyUuid, StreamId = streamId, SenderId = VoiceMediaV2Constants.EmptyUuid, SecurityContextId = 0, Payload = payload ?? Array.Empty<byte>() };
        }

        private static bool HasArgument(string expected)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length; index++) if (string.Equals(arguments[index], expected, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string Safe(string value) { return (value ?? string.Empty).Replace('\n', '_').Replace('\r', '_').Replace('|', '/'); }

        private readonly struct RotationConnectionResult
        {
            public readonly bool Bound;
            public readonly bool PongReceived;
            public readonly uint SecurityContextId;
            public readonly long EncryptedPackets;
            public readonly long DecryptedPackets;
            public readonly string TransportName;

            public RotationConnectionResult(bool bound, bool pongReceived, uint securityContextId, long encryptedPackets, long decryptedPackets, string transportName)
            {
                Bound = bound;
                PongReceived = pongReceived;
                SecurityContextId = securityContextId;
                EncryptedPackets = encryptedPackets;
                DecryptedPackets = decryptedPackets;
                TransportName = transportName ?? string.Empty;
            }
        }
    }
}
#endif
