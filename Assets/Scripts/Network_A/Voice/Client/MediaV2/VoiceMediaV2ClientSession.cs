using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Network_A.Voice.Client.MediaV2
{
    public sealed class VoiceMediaV2ClientSession : IDisposable
    {
        private const int BindTimeoutMs = 5000;
        private const int MediaQueueCapacity = 6;
        private readonly IVoiceMediaTransportV2 transport;
        private readonly ConcurrentQueue<QueuedMediaFrame> mediaQueue = new ConcurrentQueue<QueuedMediaFrame>();
        private readonly SemaphoreSlim mediaSignal = new SemaphoreSlim(0, int.MaxValue);
        private readonly object stateSync = new object();
        private CancellationTokenSource lifetimeCts;
        private Task mediaPumpTask;
        private TaskCompletionSource<VoiceMediaV2BindResult> bindCompletion;
        private uint nextTransportSequence = 1;
        private uint lastReceivedTransportSequence;
        private uint receivedAckMask;
        private bool hasReceivedTransportSequence;
        private int queuedMediaCount;
        private bool disposed;
        private string endpoint = string.Empty;
        private string accessToken = string.Empty;
        private string roomId = string.Empty;
        private string controlConnectionId = string.Empty;
        private string streamId = string.Empty;
        private byte platform;
        private string clientBuild = string.Empty;
        private ulong firstMediaTimestamp100Ns;
        private long firstMediaStopwatchTicks;
        private bool pacingAnchorReady;

        public event Action<VoiceMediaV2Packet> MediaReceived;
        public event Action<string> Failed;
        public event Action<string> Disconnected;
        public bool IsBound { get; private set; }
        public long DroppedQueuedMediaFrames { get; private set; }
        public long SentMediaFrames { get; private set; }
        public long ReceivedMediaFrames { get; private set; }

        //* این سازنده راه انتقال مستقل رسانه را دریافت می کند و رویدادهای آن را متصل می کند.
        public VoiceMediaV2ClientSession(IVoiceMediaTransportV2 transport)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.transport.PacketReceived += HandlePacketReceived;
            this.transport.Failed += HandleTransportFailure;
            this.transport.Disconnected += HandleTransportDisconnected;
        }

        //* این تابع مسیر رسانه را باز می کند و آن را با اتصال فعال مسیر کنترل احراز می کند.
        public async Task<bool> ConnectAndBindAsync(string endpoint, string accessToken, string roomId, string controlConnectionId, string streamId, byte platform, string clientBuild, CancellationToken cancellationToken)
        {
            if (disposed) return false;
            if (!Guid.TryParse(controlConnectionId, out _) || !Guid.TryParse(streamId, out _)) return false;
            if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(roomId)) return false;
            this.endpoint = endpoint ?? string.Empty;
            this.accessToken = accessToken.Trim();
            this.roomId = roomId.Trim();
            this.controlConnectionId = controlConnectionId.Trim().ToLowerInvariant();
            this.streamId = streamId.Trim().ToLowerInvariant();
            this.platform = platform;
            this.clientBuild = (clientBuild ?? string.Empty).Trim();
            IsBound = false;

            lifetimeCts?.Cancel(); lifetimeCts?.Dispose(); lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (!await transport.ConnectAsync(this.endpoint, lifetimeCts.Token)) return false;
            bindCompletion = new TaskCompletionSource<VoiceMediaV2BindResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            VoiceMediaV2Packet request = CreatePacket(VoiceMediaV2PacketKind.BindRequest, VoiceMediaV2Codec.None, 0, 0, VoiceMediaV2Constants.EmptyUuid, this.streamId, VoiceMediaV2Constants.EmptyUuid, VoiceMediaV2BindPayload.EncodeRequest(this.accessToken, this.roomId, this.controlConnectionId, this.platform, this.clientBuild));
            if (!await transport.SendAsync(request.Encode(), lifetimeCts.Token)) return false;

            Task timeout = Task.Delay(BindTimeoutMs, lifetimeCts.Token);
            Task completed = await Task.WhenAny(bindCompletion.Task, timeout);
            if (completed != bindCompletion.Task) { Failed?.Invoke("Voice media bind timed out."); return false; }
            VoiceMediaV2BindResult result = await bindCompletion.Task;
            if (!result.Success || !string.Equals(result.ControlConnectionId, this.controlConnectionId, StringComparison.OrdinalIgnoreCase)) { Failed?.Invoke("Voice media bind was rejected: " + result.Message); return false; }
            IsBound = true;
            pacingAnchorReady = false;
            mediaPumpTask = Task.Run(() => MediaPumpAsync(lifetimeCts.Token));
            return true;
        }

        //* این تابع یک فریم فشرده رسانه را در صف کوتاه و محدود قرار می دهد تا از ارسال جهشی جلوگیری شود.
        public bool TryQueueMedia(uint mediaSequence, ulong mediaTimestamp100Ns, byte[] payload, VoiceMediaV2Flags flags = VoiceMediaV2Flags.None)
        {
            if (!IsBound || disposed || payload == null || payload.Length == 0 || payload.Length > VoiceMediaV2Constants.MaximumPayloadBytes) return false;
            while (Volatile.Read(ref queuedMediaCount) >= MediaQueueCapacity && mediaQueue.TryDequeue(out _)) { Interlocked.Decrement(ref queuedMediaCount); DroppedQueuedMediaFrames += 1; }
            mediaQueue.Enqueue(new QueuedMediaFrame(mediaSequence, mediaTimestamp100Ns, payload, flags));
            Interlocked.Increment(ref queuedMediaCount);
            mediaSignal.Release();
            return true;
        }

        //* این تابع اتصال رسانه را با همان اطلاعات احراز قبلی دوباره باز و متصل می کند.
        public async Task<bool> ReconnectAsync(CancellationToken cancellationToken)
        {
            if (disposed || string.IsNullOrWhiteSpace(controlConnectionId) || string.IsNullOrWhiteSpace(streamId)) return false;
            try { await transport.DisconnectAsync("media_reconnect", cancellationToken); } catch { }
            return await ConnectAndBindAsync(endpoint, accessToken, roomId, controlConnectionId, streamId, platform, clientBuild, cancellationToken);
        }

        //* این تابع بسته های صف رسانه را بر اساس زمان رسانه به صورت پیوسته و بدون جهش ارسال می کند.
        private async Task MediaPumpAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await mediaSignal.WaitAsync(cancellationToken);
                    while (mediaQueue.TryDequeue(out QueuedMediaFrame frame))
                    {
                        Interlocked.Decrement(ref queuedMediaCount);
                        await PaceAsync(frame.MediaTimestamp100Ns, cancellationToken);
                        VoiceMediaV2Packet packet = CreatePacket(VoiceMediaV2PacketKind.Media, VoiceMediaV2Codec.Opus, frame.MediaSequence, frame.MediaTimestamp100Ns, VoiceMediaV2Constants.EmptyUuid, streamId, VoiceMediaV2Constants.EmptyUuid, frame.Payload);
                        packet.Flags = frame.Flags;
                        if (!await transport.SendAsync(packet.Encode(), cancellationToken)) { Failed?.Invoke("Voice media send failed."); continue; }
                        SentMediaFrames += 1;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { Failed?.Invoke("Voice media pacing failed: " + exception.Message); }
        }

        //* این تابع زمان محلی ارسال را با اختلاف زمان رسانه هماهنگ می کند تا بسته ها پشت سر هم جهشی فرستاده نشوند.
        private async Task PaceAsync(ulong mediaTimestamp100Ns, CancellationToken cancellationToken)
        {
            if (!pacingAnchorReady)
            {
                firstMediaTimestamp100Ns = mediaTimestamp100Ns;
                firstMediaStopwatchTicks = Stopwatch.GetTimestamp();
                pacingAnchorReady = true;
                return;
            }
            ulong mediaDelta100Ns = mediaTimestamp100Ns >= firstMediaTimestamp100Ns ? mediaTimestamp100Ns - firstMediaTimestamp100Ns : 0;
            double targetSeconds = mediaDelta100Ns / 10000000.0;
            double elapsedSeconds = (Stopwatch.GetTimestamp() - firstMediaStopwatchTicks) / (double)Stopwatch.Frequency;
            double remainingMs = (targetSeconds - elapsedSeconds) * 1000.0;
            if (remainingMs > 1.0) await Task.Delay((int)Math.Min(remainingMs, 100.0), cancellationToken);
        }

        //* این تابع بسته ورودی را بررسی می کند و نتیجه احراز یا فریم رسانه را به مصرف کننده تحویل می دهد.
        private void HandlePacketReceived(byte[] bytes)
        {
            try
            {
                VoiceMediaV2Packet packet = VoiceMediaV2Packet.Decode(bytes);
                ObserveTransportSequence(packet.TransportSequence);
                if (packet.Kind == VoiceMediaV2PacketKind.BindResult)
                {
                    bindCompletion?.TrySetResult(VoiceMediaV2BindPayload.DecodeResult(packet.Payload));
                    return;
                }
                if (packet.Kind == VoiceMediaV2PacketKind.Media)
                {
                    ReceivedMediaFrames += 1;
                    MediaReceived?.Invoke(packet);
                }
            }
            catch (Exception exception) { Failed?.Invoke("Voice media packet decode failed: " + exception.Message); }
        }

        //* این تابع شماره بسته دریافتی را برای ساخت تایید دریافت بسته های بعدی نگه می دارد.
        private void ObserveTransportSequence(uint sequence)
        {
            lock (stateSync)
            {
                if (!hasReceivedTransportSequence) { hasReceivedTransportSequence = true; lastReceivedTransportSequence = sequence; receivedAckMask = 0; return; }
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

        //* این تابع یک بسته تازه را با شماره راه انتقال و آخرین تاییدهای دریافت شده می سازد.
        private VoiceMediaV2Packet CreatePacket(VoiceMediaV2PacketKind kind, VoiceMediaV2Codec codec, uint mediaSequence, ulong mediaTimestamp100Ns, string sessionId, string streamId, string senderId, byte[] payload)
        {
            uint ackSequence; uint ackMask;
            lock (stateSync) { ackSequence = hasReceivedTransportSequence ? lastReceivedTransportSequence : 0; ackMask = receivedAckMask; }
            uint transportSequence = nextTransportSequence; nextTransportSequence = transportSequence == uint.MaxValue ? 1 : transportSequence + 1;
            return new VoiceMediaV2Packet
            {
                Kind = kind,
                Codec = codec,
                Flags = VoiceMediaV2Flags.None,
                TransportSequence = transportSequence,
                MediaSequence = mediaSequence,
                MediaTimestamp100Ns = mediaTimestamp100Ns,
                AckTransportSequence = ackSequence,
                AckMask = ackMask,
                SessionId = sessionId,
                StreamId = streamId,
                SenderId = senderId,
                SecurityContextId = 0,
                Payload = payload ?? Array.Empty<byte>()
            };
        }

        //* این تابع خطای راه انتقال را بدون تغییر مسیر کنترل به مصرف کننده اعلام می کند.
        private void HandleTransportFailure(string message) { Failed?.Invoke(message); }

        //* این تابع بسته شدن راه انتقال رسانه را بدون تغییر مسیر کنترل اعلام می کند.
        private void HandleTransportDisconnected(string reason) { IsBound = false; Disconnected?.Invoke(reason); }

        //* این تابع همه منابع مسیر رسانه و صف ارسال را آزاد می کند.
        public void Dispose()
        {
            if (disposed) return; disposed = true; IsBound = false;
            try { lifetimeCts?.Cancel(); } catch { }
            transport.PacketReceived -= HandlePacketReceived; transport.Failed -= HandleTransportFailure; transport.Disconnected -= HandleTransportDisconnected;
            try { transport.Dispose(); } catch { }
            lifetimeCts?.Dispose(); lifetimeCts = null;
        }

        private readonly struct QueuedMediaFrame
        {
            public readonly uint MediaSequence;
            public readonly ulong MediaTimestamp100Ns;
            public readonly byte[] Payload;
            public readonly VoiceMediaV2Flags Flags;

            //* این سازنده داده یک فریم صف شده را بدون تغییر نگه می دارد.
            public QueuedMediaFrame(uint mediaSequence, ulong mediaTimestamp100Ns, byte[] payload, VoiceMediaV2Flags flags)
            {
                MediaSequence = mediaSequence; MediaTimestamp100Ns = mediaTimestamp100Ns; Payload = payload; Flags = flags;
            }
        }
    }
}
