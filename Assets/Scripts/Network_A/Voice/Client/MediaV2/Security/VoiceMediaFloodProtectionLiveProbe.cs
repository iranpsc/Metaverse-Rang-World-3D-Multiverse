#if !UNITY_WEBGL || UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Voice.Client.Runtime;
using UnityEngine;

namespace Network_A.Voice.Client.MediaV2.Security
{
    internal sealed class VoiceMediaFloodProtectionLiveProbe : MonoBehaviour
    {
        private const string ProbeArgument = "--vme2-phase13-flood-protection-live-test";
        private const int AuthenticationTimeoutMs = 30000;
        private const int OperationTimeoutMs = 5000;
        private const int OversizedPacketBytes = 1201;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!HasArgument(ProbeArgument)) return;
            GameObject target = new GameObject("VME2_Media_Flood_Protection_Live_Probe");
            DontDestroyOnLoad(target);
            target.AddComponent<VoiceMediaFloodProtectionLiveProbe>();
        }

        private async void Awake()
        {
            Debug.Log("VME2_PHASE13_FLOOD_PROTECTION_LIVE=START | platformScope=Windows | feedsProductionVoice=False");
            try { await RunAsync(); }
            catch (Exception exception) { Debug.LogError("VME2_PHASE13_FLOOD_PROTECTION_LIVE=FAIL | error=" + Safe(exception.Message) + " | platformScope=Windows | feedsProductionVoice=False"); }
        }

        private static async Task RunAsync()
        {
            VoiceClientRuntime runtime = null;
            int waitedMs = 0;
            while (waitedMs < AuthenticationTimeoutMs)
            {
                runtime = UnityEngine.Object.FindFirstObjectByType<VoiceClientRuntime>();
                if (runtime != null && runtime.IsAuthenticated && Guid.TryParse(runtime.VoiceConnectionId, out _)) break;
                await Task.Delay(100);
                waitedMs += 100;
            }
            if (runtime == null || !runtime.IsAuthenticated || !Guid.TryParse(runtime.VoiceConnectionId, out _)) throw new InvalidOperationException("Voice control plane was not authenticated in time.");

            string accessToken = (SecureTokenStorage.GetAccessToken() ?? string.Empty).Trim();
            string roomId = (MetaverseNetworkClient.roomId ?? string.Empty).Trim();
            if (accessToken.Length == 0 || roomId.Length == 0) throw new InvalidOperationException("Voice media identity is not ready.");

            string streamId = Guid.NewGuid().ToString("D");
            IVoiceMediaTransportV2 inner = VoiceMediaTransportV2Factory.CreateForCurrentPlatform();
            string transportName = inner.GetType().Name;
            using (VoiceSecuredMediaTransportV2 transport = new VoiceSecuredMediaTransportV2(inner))
            {
                TaskCompletionSource<VoiceMediaV2BindResult> bindCompletion = new TaskCompletionSource<VoiceMediaV2BindResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<string> disconnectCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                int pongCount = 0;
                string failure = string.Empty;
                Action<string> failureHandler = message => failure = message ?? string.Empty;
                Action<string> disconnectedHandler = reason => disconnectCompletion.TrySetResult(reason ?? string.Empty);
                Action<byte[]> packetHandler = bytes =>
                {
                    try
                    {
                        VoiceMediaV2Packet packet = VoiceMediaV2Packet.Decode(bytes);
                        if (packet.Kind == VoiceMediaV2PacketKind.BindResult) bindCompletion.TrySetResult(VoiceMediaV2BindPayload.DecodeResult(packet.Payload));
                        if (packet.Kind == VoiceMediaV2PacketKind.Pong) Interlocked.Increment(ref pongCount);
                    }
                    catch (Exception exception) { failure = exception.Message; }
                };
                transport.Failed += failureHandler;
                transport.Disconnected += disconnectedHandler;
                transport.PacketReceived += packetHandler;
                try
                {
                    if (!await transport.ConnectAsync(ServerConfig.BuildRealtimeGrpcStreamingTarget(), CancellationToken.None)) throw new InvalidOperationException("Voice media transport connection failed: " + failure);
                    VoiceMediaV2Packet bind = BuildPacket(VoiceMediaV2PacketKind.BindRequest, 1, streamId, VoiceMediaV2BindPayload.EncodeRequest(accessToken, roomId, runtime.VoiceConnectionId, 2, Application.version));
                    if (!await transport.SendAsync(bind.Encode(), CancellationToken.None)) throw new InvalidOperationException("Voice media bind send failed: " + failure);
                    if (await Task.WhenAny(bindCompletion.Task, Task.Delay(OperationTimeoutMs)) != bindCompletion.Task) throw new TimeoutException("Voice media bind timed out: " + failure);
                    VoiceMediaV2BindResult bindResult = await bindCompletion.Task;
                    bool mediaBound = bindResult.Success && string.Equals(bindResult.ControlConnectionId, runtime.VoiceConnectionId, StringComparison.OrdinalIgnoreCase);
                    bool securityNegotiated = transport.IsSecurityReady;
                    if (!mediaBound || !securityNegotiated) throw new InvalidOperationException("Voice media secure bind failed: " + bindResult.Message);

                    bool oversizedPacketSent = await inner.SendAsync(new byte[OversizedPacketBytes], CancellationToken.None);
                    Task disconnected = await Task.WhenAny(disconnectCompletion.Task, Task.Delay(OperationTimeoutMs));
                    bool serverDisconnected = disconnected == disconnectCompletion.Task && !transport.IsConnected;
                    string disconnectReason = disconnected == disconnectCompletion.Task ? disconnectCompletion.Task.Result : string.Empty;
                    bool noUnprotectedPong = Volatile.Read(ref pongCount) == 0;
                    bool pass = mediaBound && securityNegotiated && oversizedPacketSent && serverDisconnected && noUnprotectedPong;
                    Debug.Log(
                        "VME2_PHASE13_FLOOD_PROTECTION_LIVE=" + (pass ? "PASS" : "FAIL") +
                        " | controlAuthenticated=True" +
                        " | mediaBound=" + mediaBound +
                        " | securityNegotiated=" + securityNegotiated +
                        " | packetBytes=" + OversizedPacketBytes +
                        " | oversizedPacketSent=" + oversizedPacketSent +
                        " | serverDisconnected=" + serverDisconnected +
                        " | disconnectReason=" + Safe(disconnectReason) +
                        " | unprotectedPongCount=" + Volatile.Read(ref pongCount) +
                        " | mediaTransport=" + transportName +
                        " | platformScope=Windows" +
                        " | feedsProductionVoice=False");
                    if (!pass) throw new InvalidOperationException("Voice media live flood protection assertions failed: " + failure);
                }
                finally
                {
                    transport.Failed -= failureHandler;
                    transport.Disconnected -= disconnectedHandler;
                    transport.PacketReceived -= packetHandler;
                    if (transport.IsConnected) await transport.DisconnectAsync("flood_probe_complete", CancellationToken.None);
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
    }
}
#endif
