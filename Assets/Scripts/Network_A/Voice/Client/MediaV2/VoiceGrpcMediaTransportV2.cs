using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
#if !UNITY_WEBGL || UNITY_EDITOR
using Grpc.Core;
#endif

namespace Network_A.Voice.Client.MediaV2
{
    public sealed class VoiceGrpcMediaTransportV2 : IVoiceMediaTransportV2
    {
        private const string ServiceName = "metaverse.voice.media.v2.VoiceMediaTransport";
        private const string MethodName = "Connect";
#if !UNITY_WEBGL || UNITY_EDITOR
        private static readonly Marshaller<byte[]> PacketMarshaller = Marshallers.Create(SerializePacket, DeserializePacket);
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

        //* این تابع یک جریان دوطرفه جی آر پی سی جدا از مسیر کنترل روی همان سرور اصلی باز می کند.
        public async Task<bool> ConnectAsync(string endpoint, CancellationToken cancellationToken)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            Failed?.Invoke("Voice media gRPC is unavailable in WebGL."); return false;
#else
            if (IsConnected) return true;
            try
            {
                connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                string target = string.IsNullOrWhiteSpace(endpoint) ? ServerConfig.BuildRealtimeGrpcStreamingTarget() : endpoint.Trim();
                ChannelCredentials credentials = target.IndexOf("localhost", StringComparison.OrdinalIgnoreCase) >= 0 || target.IndexOf("127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0 ? ChannelCredentials.Insecure : new SslCredentials();
                channel = new Channel(target, credentials);
                await channel.ConnectAsync(DateTime.UtcNow.AddSeconds(9));
                Method<byte[], byte[]> method = new Method<byte[], byte[]>(MethodType.DuplexStreaming, ServiceName, MethodName, PacketMarshaller, PacketMarshaller);
                streamCall = channel.CreateCallInvoker().AsyncDuplexStreamingCall(method, null, new CallOptions(null, null, connectionCts.Token));
                IsConnected = true; Connected?.Invoke(); _ = ReceiveLoopAsync(connectionCts.Token); return true;
            }
            catch (Exception exception)
            {
                Failed?.Invoke("Voice media gRPC connect failed: " + exception.Message); await DisconnectAsync("connect_failed", CancellationToken.None); return false;
            }
#endif
        }

        //* این تابع یک بسته باینری مسیر رسانه را به صورت ترتیبی داخل جریان مستقل ارسال می کند.
        public async Task<bool> SendAsync(byte[] packet, CancellationToken cancellationToken)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return false;
#else
            if (!IsConnected || streamCall == null || packet == null || packet.Length == 0) return false;
            bool taken = false;
            try { await sendLock.WaitAsync(cancellationToken); taken = true; await streamCall.RequestStream.WriteAsync(packet); return true; }
            catch (Exception exception) { Failed?.Invoke("Voice media gRPC send failed: " + exception.Message); return false; }
            finally { if (taken) sendLock.Release(); }
#endif
        }

        //* این تابع جریان و کانال مستقل رسانه را بدون دخالت در راه انتقال کنترل می بندد.
        public async Task DisconnectAsync(string reason, CancellationToken cancellationToken)
        {
            if (disconnecting) return; disconnecting = true; IsConnected = false;
#if !UNITY_WEBGL || UNITY_EDITOR
            try { connectionCts?.Cancel(); } catch { }
            try { if (streamCall != null) await streamCall.RequestStream.CompleteAsync(); } catch { }
            try { streamCall?.Dispose(); } catch { } streamCall = null;
            if (channel != null) { try { await channel.ShutdownAsync(); } catch { } channel = null; }
#endif
            connectionCts?.Dispose(); connectionCts = null; Disconnected?.Invoke(string.IsNullOrWhiteSpace(reason) ? "media_disconnect" : reason.Trim()); disconnecting = false;
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        //* این تابع بسته های باینری دریافتی را از جریان رسانه خوانده و به مصرف کننده تحویل می دهد.
        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && streamCall != null)
                {
                    bool hasNext = await streamCall.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false); if (!hasNext) break;
                    byte[] packet = streamCall.ResponseStream.Current; if (packet != null && packet.Length > 0) PacketReceived?.Invoke(packet);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { if (!disconnecting) Failed?.Invoke("Voice media gRPC receive failed: " + exception.Message); }
            if (!disconnecting) await DisconnectAsync("remote_closed", CancellationToken.None);
        }

        //* این تابع آرایه خام را داخل پیام ساده پروتو با فیلد باینری شماره یک قرار می دهد.
        private static byte[] SerializePacket(byte[] packet)
        {
            byte[] safe = packet ?? Array.Empty<byte>(); byte[] length = EncodeVarint((uint)safe.Length); byte[] result = new byte[1 + length.Length + safe.Length]; result[0] = 0x0a; Buffer.BlockCopy(length, 0, result, 1, length.Length); Buffer.BlockCopy(safe, 0, result, 1 + length.Length, safe.Length); return result;
        }

        //* این تابع پیام ساده پروتو را باز می کند و آرایه باینری اصلی را برمی گرداند.
        private static byte[] DeserializePacket(byte[] data)
        {
            if (data == null || data.Length == 0) return Array.Empty<byte>(); int offset = 0; if (data[offset++] != 0x0a) return Array.Empty<byte>(); uint length = DecodeVarint(data, ref offset); if (length > int.MaxValue || offset + (int)length > data.Length) return Array.Empty<byte>(); byte[] result = new byte[(int)length]; Buffer.BlockCopy(data, offset, result, 0, result.Length); return result;
        }

        //* این تابع یک عدد را برای طول فیلد پروتو به نمایش متغیر بایتی تبدیل می کند.
        private static byte[] EncodeVarint(uint value)
        {
            List<byte> bytes = new List<byte>(5); do { byte current = (byte)(value & 0x7f); value >>= 7; if (value != 0) current |= 0x80; bytes.Add(current); } while (value != 0); return bytes.ToArray();
        }

        //* این تابع نمایش متغیر بایتی طول پروتو را دوباره به عدد تبدیل می کند.
        private static uint DecodeVarint(byte[] data, ref int offset)
        {
            uint value = 0; int shift = 0; while (offset < data.Length && shift < 35) { byte current = data[offset++]; value |= (uint)(current & 0x7f) << shift; if ((current & 0x80) == 0) return value; shift += 7; } throw new InvalidOperationException("Invalid protobuf varint.");
        }
#endif

        //* این تابع منابع راه انتقال رسانه را آزاد می کند.
        public void Dispose()
        {
            _ = DisconnectAsync("dispose", CancellationToken.None); sendLock.Dispose();
        }
    }
}
