using System;
using System.Threading;
using System.Threading.Tasks;

namespace Network_A.Voice.Client.MediaV2.Congestion
{
    internal sealed class VoiceMediaTestImpairmentTransportV2 : IVoiceMediaTransportV2
    {
        private readonly IVoiceMediaTransportV2 inner;
        private readonly CancellationTokenSource delayedDeliveryCts =
            new CancellationTokenSource();

        private int incomingDelayMs;
        private int dropEveryNthPing;
        private int observedOutgoingPings;
        private int droppedOutgoingPings;
        private bool disposed;

        public VoiceMediaTestImpairmentTransportV2(IVoiceMediaTransportV2 inner)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            inner.Connected += HandleConnected;
            inner.PacketReceived += HandlePacketReceived;
            inner.Failed += HandleFailed;
            inner.Disconnected += HandleDisconnected;
        }

        public event Action Connected;
        public event Action<byte[]> PacketReceived;
        public event Action<string> Failed;
        public event Action<string> Disconnected;

        public bool IsConnected => !disposed && inner.IsConnected;
        public int DroppedOutgoingPings => Volatile.Read(ref droppedOutgoingPings);

        // این تابع تأخیر دریافت و الگوی حذف پینگ را فقط برای مرحله فعال آزمون تنظیم می کند.
        public void Configure(int delayMs, int dropEveryNth)
        {
            if (delayMs < 0 || delayMs > 5000) throw new ArgumentOutOfRangeException(nameof(delayMs));
            if (dropEveryNth < 0 || dropEveryNth == 1) throw new ArgumentOutOfRangeException(nameof(dropEveryNth));

            ThrowIfDisposed();
            Volatile.Write(ref incomingDelayMs, delayMs);
            Volatile.Write(ref dropEveryNthPing, dropEveryNth);
            Interlocked.Exchange(ref observedOutgoingPings, 0);
        }

        public Task<bool> ConnectAsync(string endpoint, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return inner.ConnectAsync(endpoint, cancellationToken);
        }

        // این تابع فقط پینگ های انتخاب شده آزمون را حذف می کند و بسته احراز یا رسانه را هرگز تغییر نمی دهد.
        public Task<bool> SendAsync(byte[] packet, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (ShouldDropPing(packet)) return Task.FromResult(true);
            return inner.SendAsync(packet, cancellationToken);
        }

        public Task DisconnectAsync(string reason, CancellationToken cancellationToken)
        {
            if (disposed) return Task.CompletedTask;
            return inner.DisconnectAsync(reason, cancellationToken);
        }

        private bool ShouldDropPing(byte[] packet)
        {
            int dropEveryNth = Volatile.Read(ref dropEveryNthPing);
            if (dropEveryNth <= 1) return false;

            VoiceMediaV2Packet decoded = VoiceMediaV2Packet.Decode(packet);
            if (decoded.Kind != VoiceMediaV2PacketKind.Ping) return false;

            int pingNumber = Interlocked.Increment(ref observedOutgoingPings);
            if (pingNumber % dropEveryNth != 0) return false;

            Interlocked.Increment(ref droppedOutgoingPings);
            return true;
        }

        private void HandleConnected()
        {
            Connected?.Invoke();
        }

        private void HandlePacketReceived(byte[] packet)
        {
            int delayMs = Volatile.Read(ref incomingDelayMs);
            if (delayMs <= 0)
            {
                PacketReceived?.Invoke(packet);
                return;
            }

            byte[] delayedPacket = new byte[packet.Length];
            Buffer.BlockCopy(packet, 0, delayedPacket, 0, packet.Length);
            _ = DeliverDelayedAsync(delayedPacket, delayMs, delayedDeliveryCts.Token);
        }

        private async Task DeliverDelayedAsync(
            byte[] packet,
            int delayMs,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(delayMs, cancellationToken);
                if (!disposed && !cancellationToken.IsCancellationRequested)
                {
                    PacketReceived?.Invoke(packet);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Failed?.Invoke("Voice media test impairment delivery failed: " + exception.Message);
            }
        }

        private void HandleFailed(string message)
        {
            Failed?.Invoke(message);
        }

        private void HandleDisconnected(string reason)
        {
            Disconnected?.Invoke(reason);
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(VoiceMediaTestImpairmentTransportV2));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            delayedDeliveryCts.Cancel();
            inner.Connected -= HandleConnected;
            inner.PacketReceived -= HandlePacketReceived;
            inner.Failed -= HandleFailed;
            inner.Disconnected -= HandleDisconnected;
            try { inner.Dispose(); } catch { }
            delayedDeliveryCts.Dispose();
            Connected = null;
            PacketReceived = null;
            Failed = null;
            Disconnected = null;
        }
    }
}
