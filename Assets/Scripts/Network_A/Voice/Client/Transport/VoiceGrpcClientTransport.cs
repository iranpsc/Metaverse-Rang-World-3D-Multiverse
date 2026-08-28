using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;

#if !UNITY_WEBGL || UNITY_EDITOR
using Grpc.Core;
#endif

namespace Network_A.Voice.Client.Transport
{
    public sealed class VoiceGrpcClientTransport : IVoiceClientTransport
    {
        private const string ServiceName = "metaverse.voice.transport.v1.VoiceTransport";
        private const string MethodName = "Connect";

#if !UNITY_WEBGL || UNITY_EDITOR
        private static readonly Marshaller<byte[]> PacketMarshaller =
            Marshallers.Create(SerializeVoicePacket, DeserializeVoicePacket);

        private Channel channel;
        private AsyncDuplexStreamingCall<byte[], byte[]> streamCall;
#endif

        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource connectionCts;
        private bool disconnecting;

        public event Action Connected;
        public event Action<byte[]> PacketReceived;
        public event Action<string> Failed;
        public event Action<string> Disconnected;

        public bool IsConnected { get; private set; }

        //* این تابع Duplex Stream صوت را روی سرور gRPC اصلی باز می‌کند.
        public async Task<bool> ConnectAsync(string endpoint, CancellationToken cancellationToken)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            Failed?.Invoke("Voice gRPC is unavailable in WebGL.");
            return false;
#else
            if (IsConnected) return true;

            try
            {
                connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                string target = string.IsNullOrWhiteSpace(endpoint)
                    ? ServerConfig.BuildRealtimeGrpcStreamingTarget()
                    : endpoint.Trim();

                ChannelCredentials credentials = ResolveCredentials(target);
                channel = new Channel(target, credentials);
                await channel.ConnectAsync(DateTime.UtcNow.AddSeconds(9));

                Method<byte[], byte[]> method = new Method<byte[], byte[]>(
                    MethodType.DuplexStreaming,
                    ServiceName,
                    MethodName,
                    PacketMarshaller,
                    PacketMarshaller);

                streamCall = channel.CreateCallInvoker().AsyncDuplexStreamingCall(
                    method,
                    null,
                    new CallOptions(null, null, connectionCts.Token));

                IsConnected = true;
                Connected?.Invoke();
                _ = ReceiveLoopAsync(connectionCts.Token);
                return true;
            }
            catch (Exception exception)
            {
                Failed?.Invoke("Voice gRPC connect failed: " + exception.Message);
                await DisconnectAsync("connect_failed", CancellationToken.None);
                return false;
            }
#endif
        }

        //* این تابع یک Envelope خام را داخل VoicePacket protobuf ارسال می‌کند.
        public async Task<bool> SendAsync(byte[] packet, CancellationToken cancellationToken)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return false;
#else
            if (!IsConnected || streamCall == null || packet == null || packet.Length == 0) return false;

            bool lockTaken = false;
            try
            {
                await sendLock.WaitAsync(cancellationToken);
                lockTaken = true;
                await streamCall.RequestStream.WriteAsync(packet);
                return true;
            }
            catch (Exception exception)
            {
                Failed?.Invoke("Voice gRPC send failed: " + exception.Message);
                return false;
            }
            finally
            {
                if (lockTaken) sendLock.Release();
            }
#endif
        }

        //* این تابع Stream و Channel را با مهلت محدود پاک می‌کند.
        public async Task DisconnectAsync(string reason, CancellationToken cancellationToken)
        {
            if (disconnecting) return;
            disconnecting = true;
            IsConnected = false;

#if !UNITY_WEBGL || UNITY_EDITOR
            try { connectionCts?.Cancel(); } catch { }
            try
            {
                if (streamCall != null) await streamCall.RequestStream.CompleteAsync();
            }
            catch { }

            try { streamCall?.Dispose(); } catch { }
            streamCall = null;

            if (channel != null)
            {
                try { await channel.ShutdownAsync(); } catch { }
                channel = null;
            }
#endif

            connectionCts?.Dispose();
            connectionCts = null;
            Disconnected?.Invoke(string.IsNullOrWhiteSpace(reason) ? "voice_disconnect" : reason.Trim());
            disconnecting = false;
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        //* این تابع بسته‌های protobuf دریافتی را به Envelope خام برمی‌گرداند.
        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && streamCall != null)
                {
                    bool hasNext = await streamCall.ResponseStream.MoveNext(cancellationToken);
                    if (!hasNext) break;
                    byte[] packet = streamCall.ResponseStream.Current;
                    if (packet != null && packet.Length > 0) PacketReceived?.Invoke(packet);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (!disconnecting) Failed?.Invoke("Voice gRPC receive failed: " + exception.Message);
            }

            if (!disconnecting) await DisconnectAsync("remote_closed", CancellationToken.None);
        }

        private static ChannelCredentials ResolveCredentials(string target)
        {
            return target.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   target.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0
                ? ChannelCredentials.Insecure
                : new SslCredentials();
        }

        //* این تابع byte[] خام را به پیام protobuf با فیلد bytes شماره یک تبدیل می‌کند.
        private static byte[] SerializeVoicePacket(byte[] packet)
        {
            byte[] safePacket = packet ?? Array.Empty<byte>();
            byte[] length = EncodeVarint((uint)safePacket.Length);
            byte[] result = new byte[1 + length.Length + safePacket.Length];
            result[0] = 0x0a;
            Buffer.BlockCopy(length, 0, result, 1, length.Length);
            Buffer.BlockCopy(safePacket, 0, result, 1 + length.Length, safePacket.Length);
            return result;
        }

        //* این تابع پیام protobuf VoicePacket را به Envelope خام تبدیل می‌کند.
        private static byte[] DeserializeVoicePacket(byte[] message)
        {
            if (message == null || message.Length < 2 || message[0] != 0x0a)
                throw new InvalidOperationException("VoicePacket protobuf is invalid.");

            int cursor = 1;
            uint length = DecodeVarint(message, ref cursor);
            if (cursor + (long)length != message.Length)
                throw new InvalidOperationException("VoicePacket protobuf length is invalid.");

            byte[] packet = new byte[checked((int)length)];
            Buffer.BlockCopy(message, cursor, packet, 0, packet.Length);
            return packet;
        }

        private static byte[] EncodeVarint(uint value)
        {
            List<byte> bytes = new List<byte>(5);
            do
            {
                byte current = (byte)(value & 0x7f);
                value >>= 7;
                if (value != 0) current |= 0x80;
                bytes.Add(current);
            }
            while (value != 0);
            return bytes.ToArray();
        }

        private static uint DecodeVarint(byte[] bytes, ref int cursor)
        {
            uint value = 0;
            int shift = 0;
            while (cursor < bytes.Length && shift <= 28)
            {
                byte current = bytes[cursor++];
                value |= (uint)(current & 0x7f) << shift;
                if ((current & 0x80) == 0) return value;
                shift += 7;
            }
            throw new InvalidOperationException("VoicePacket protobuf varint is invalid.");
        }
#endif

        public void Dispose()
        {
            _ = DisconnectAsync("dispose", CancellationToken.None);
            sendLock.Dispose();
        }
    }
}

/*
توضیح فایل:
این فایل Transport صوت Windows و Quest را با Duplex Streaming gRPC روی سرویس اصلی موجود پیاده می‌کند و Envelope خام را داخل VoicePacket protobuf قرار می‌دهد.
*/
