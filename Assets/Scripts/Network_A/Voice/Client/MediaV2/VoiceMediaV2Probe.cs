using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Voice.Client.Runtime;
using UnityEngine;

namespace Network_A.Voice.Client.MediaV2
{
    internal sealed class VoiceMediaV2Probe : MonoBehaviour
    {
        private const string ProbeArgument = "--vme2-phase9-media-test";
        private const string LiveProbeArgument = "--vme2-phase9-live-test";

        //* این تابع فقط در صورت وجود آرگومان آزمون، نمونه مستقل فاز نه را ایجاد می کند.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!HasArgument(ProbeArgument) && !HasArgument(LiveProbeArgument)) return;
            GameObject target = new GameObject("VME2_PHASE9_Media_Probe"); DontDestroyOnLoad(target); target.AddComponent<VoiceMediaV2Probe>();
        }

        //* این تابع خط فرمان برنامه را برای نشانگر اجرای آزمون فاز نه بررسی می کند.
        private static bool HasArgument(string expected)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int index = 0; index < args.Length; index++) if (string.Equals(args[index], expected, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        //* این تابع آزمون قرارداد باینری، جداسازی کارخانه راه انتقال و صف محدود رسانه را اجرا می کند.
        private async void Awake()
        {
            Debug.Log("VME2_PHASE9_MEDIA_PROBE=START | feedsProductionVoice=False");
            if (HasArgument(ProbeArgument)) await RunSyntheticAsync();
            if (HasArgument(LiveProbeArgument)) await RunLiveBindAsync();
        }

        //* این تابع آزمون های بدون شبکه قرارداد بسته و صف محدود مسیر رسانه را اجرا می کند.
        private static async Task RunSyntheticAsync()
        {
            try
            {
                RunProtocolRoundTrip();
                await RunSessionQueueTest();
                using IVoiceMediaTransportV2 platformTransport = VoiceMediaTransportV2Factory.CreateForCurrentPlatform();
                string transportName = platformTransport.GetType().Name;
                bool windowsSeparated = Application.platform != RuntimePlatform.WebGLPlayer ? transportName == nameof(VoiceGrpcMediaTransportV2) : true;
                Debug.Log("VME2_PHASE9_SYNTHETIC=PASS | protocolRoundTrip=True | mtuEnforced=True | queueBounded=True | platformMediaTransport=" + transportName + " | windowsSeparated=" + windowsSeparated + " | feedsProductionVoice=False");
            }
            catch (Exception exception)
            {
                Debug.LogError("VME2_PHASE9_SYNTHETIC=FAIL | error=" + Safe(exception.Message) + " | feedsProductionVoice=False");
            }
        }

        //* این تابع پس از احراز مسیر کنترل، یک اتصال واقعی و جداگانه مسیر رسانه را به همان شناسه اتصال متصل می کند.
        private static async Task RunLiveBindAsync()
        {
            try
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

                if (!runtime.IsAuthenticated || !Guid.TryParse(runtime.VoiceConnectionId, out _)) throw new InvalidOperationException("Voice control plane was not authenticated in time after runtime creation.");
                string token = (SecureTokenStorage.GetAccessToken() ?? string.Empty).Trim();
                string room = (MetaverseNetworkClient.roomId ?? string.Empty).Trim();
                if (token.Length == 0 || room.Length == 0) throw new InvalidOperationException("Voice media identity is not ready.");
                string stream = Guid.NewGuid().ToString("D");
                using IVoiceMediaTransportV2 mediaTransport = VoiceMediaTransportV2Factory.CreateForCurrentPlatform();
                using VoiceMediaV2ClientSession session = new VoiceMediaV2ClientSession(mediaTransport);
                string endpoint = ResolveLiveMediaEndpoint();
                byte platform = ResolvePlatformCode();
                bool bound = await session.ConnectAndBindAsync(endpoint, token, room, runtime.VoiceConnectionId, stream, platform, Application.version, CancellationToken.None);
                if (!bound) throw new InvalidOperationException("Voice media plane bind failed.");
                Debug.Log("VME2_PHASE9_LIVE=PASS | controlAuthenticated=True | mediaBound=True | controlConnectionId=" + runtime.VoiceConnectionId + " | mediaTransport=" + mediaTransport.GetType().Name + " | feedsProductionVoice=False");
                await Task.Delay(1500);
            }
            catch (Exception exception)
            {
                Debug.LogError("VME2_PHASE9_LIVE=FAIL | error=" + Safe(exception.Message) + " | feedsProductionVoice=False");
            }
        }

        //* این تابع نشانی مسیر رسانه را برای ویندوز و وب جی ال بدون استفاده از مسیر کنترل می سازد.
        private static string ResolveLiveMediaEndpoint()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            string url = ServerConfig.RealtimeWebSocketUrl;
            return url + (url.Contains("?") ? "&" : "?") + "transport=voice-media-v2";
#else
            return ServerConfig.BuildRealtimeGrpcStreamingTarget();
#endif
        }

        //* این تابع شماره پلتفرم را مطابق قرارداد احراز مسیر رسانه برمی گرداند.
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

        //* این تابع ساخت و بازکردن بسته رسانه را با همه شناسه ها و زمان رسانه بررسی می کند.
        private static void RunProtocolRoundTrip()
        {
            string sessionId = "11111111-1111-4111-8111-111111111111";
            string streamId = "22222222-2222-4222-8222-222222222222";
            string senderId = "33333333-3333-4333-8333-333333333333";
            byte[] payload = { 1, 2, 3, 4, 5 };
            VoiceMediaV2Packet original = new VoiceMediaV2Packet
            {
                Kind = VoiceMediaV2PacketKind.Media,
                Codec = VoiceMediaV2Codec.Opus,
                Flags = VoiceMediaV2Flags.Recovery,
                TransportSequence = 17,
                MediaSequence = 91,
                MediaTimestamp100Ns = 18200000,
                AckTransportSequence = 16,
                AckMask = 7,
                SessionId = sessionId,
                StreamId = streamId,
                SenderId = senderId,
                Payload = payload
            };
            VoiceMediaV2Packet decoded = VoiceMediaV2Packet.Decode(original.Encode());
            if (decoded.TransportSequence != 17 || decoded.MediaSequence != 91 || decoded.MediaTimestamp100Ns != 18200000 || decoded.SessionId != sessionId || decoded.StreamId != streamId || decoded.SenderId != senderId || decoded.Payload.Length != payload.Length) throw new InvalidOperationException("Media protocol round trip failed.");
            bool mtuRejected = false;
            try { new VoiceMediaV2Packet { Kind = VoiceMediaV2PacketKind.Media, Codec = VoiceMediaV2Codec.Opus, Payload = new byte[VoiceMediaV2Constants.MaximumPayloadBytes + 1] }.Encode(); } catch { mtuRejected = true; }
            if (!mtuRejected) throw new InvalidOperationException("Media MTU policy was not enforced.");
        }

        //* این تابع صف محدود و احراز جلسه رسانه را با راه انتقال حافظه ای و بدون شبکه واقعی بررسی می کند.
        private static async Task RunSessionQueueTest()
        {
            FakeMediaTransport transport = new FakeMediaTransport();
            using VoiceMediaV2ClientSession session = new VoiceMediaV2ClientSession(transport);
            string connectionId = "44444444-4444-4444-8444-444444444444";
            string streamId = "55555555-5555-4555-8555-555555555555";
            transport.OnSend = bytes =>
            {
                VoiceMediaV2Packet packet = VoiceMediaV2Packet.Decode(bytes);
                if (packet.Kind == VoiceMediaV2PacketKind.BindRequest)
                {
                    byte[] resultPayload = BuildBindResult(connectionId);
                    transport.Emit(new VoiceMediaV2Packet { Kind = VoiceMediaV2PacketKind.BindResult, Codec = VoiceMediaV2Codec.None, TransportSequence = 1, StreamId = streamId, SenderId = connectionId, Payload = resultPayload }.Encode());
                }
            };
            bool bound = await session.ConnectAndBindAsync("memory", "token", "room", connectionId, streamId, 1, "probe", CancellationToken.None);
            if (!bound) throw new InvalidOperationException("Media session bind failed.");
            for (uint index = 0; index < 20; index++) session.TryQueueMedia(index, index * 200000UL, new byte[] { 1, 2, 3 });
            await Task.Delay(120);
            if (session.DroppedQueuedMediaFrames <= 0) throw new InvalidOperationException("Media queue capacity was not enforced.");
        }

        //* این تابع نتیجه موفق احراز حافظه ای را با قالب باینری قرارداد می سازد.
        private static byte[] BuildBindResult(string connectionId)
        {
            byte[] message = System.Text.Encoding.UTF8.GetBytes("ok");
            byte[] result = new byte[22 + message.Length];
            result[0] = 1; result[1] = 0; VoiceMediaV2Packet.WriteUInt16(result, 2, 0); VoiceMediaV2Packet.WriteUuid(result, 4, connectionId); VoiceMediaV2Packet.WriteUInt16(result, 20, message.Length); Buffer.BlockCopy(message, 0, result, 22, message.Length); return result;
        }

        //* این تابع متن گزارش را برای جلوگیری از شکستن یک خط پاک سازی می کند.
        private static string Safe(string value) { return (value ?? string.Empty).Replace('\n', '_').Replace('\r', '_').Replace('|', '/'); }

        private sealed class FakeMediaTransport : IVoiceMediaTransportV2
        {
            public Action<byte[]> OnSend;
            public event Action Connected;
            public event Action<byte[]> PacketReceived;
            public event Action<string> Failed;
            public event Action<string> Disconnected;
            public bool IsConnected { get; private set; }

            //* این تابع راه انتقال حافظه ای را برای آزمون باز می کند.
            public Task<bool> ConnectAsync(string endpoint, CancellationToken cancellationToken) { IsConnected = true; Connected?.Invoke(); return Task.FromResult(true); }

            //* این تابع بسته حافظه ای را به تابع آزمون تحویل می دهد.
            public Task<bool> SendAsync(byte[] packet, CancellationToken cancellationToken) { OnSend?.Invoke(packet); return Task.FromResult(true); }

            //* این تابع راه انتقال حافظه ای را برای آزمون می بندد.
            public Task DisconnectAsync(string reason, CancellationToken cancellationToken) { IsConnected = false; Disconnected?.Invoke(reason); return Task.CompletedTask; }

            //* این تابع یک بسته ساختگی را مانند دریافت شبکه به جلسه تحویل می دهد.
            public void Emit(byte[] packet) { PacketReceived?.Invoke(packet); }

            //* این تابع منابع راه انتقال حافظه ای را آزاد می کند.
            public void Dispose() { IsConnected = false; }
        }
    }
}
