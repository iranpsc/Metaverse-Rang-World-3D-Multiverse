using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Network_A.Voice.Client.MediaV2.Congestion
{
    public sealed class VoiceObservedMediaTransportV2 : IVoiceMediaTransportV2
    {
        private readonly IVoiceMediaTransportV2 inner;
        private readonly VoiceMediaTransportFeedbackTracker tracker;
        private bool disposed;

        public event Action Connected;
        public event Action<byte[]> PacketReceived;
        public event Action<string> Failed;
        public event Action<string> Disconnected;
        public event Action<VoiceMediaTransportFeedbackSnapshot> FeedbackUpdated;

        public bool IsConnected => !disposed && inner.IsConnected;

        // این سازنده یک لایه مشاهده گر روی راه انتقال موجود قرار می دهد و هیچ بسته یا رفتار ارسال را تغییر نمی دهد.
        public VoiceObservedMediaTransportV2(IVoiceMediaTransportV2 inner, VoiceMediaTransportFeedbackTracker tracker = null)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.tracker = tracker ?? new VoiceMediaTransportFeedbackTracker();
            inner.Connected += HandleConnected;
            inner.PacketReceived += HandlePacketReceived;
            inner.Failed += HandleFailed;
            inner.Disconnected += HandleDisconnected;
        }

        public VoiceMediaTransportFeedbackSnapshot GetFeedbackSnapshot()
        {
            return tracker.GetSnapshot();
        }

        // این تابع اتصال راه انتقال اصلی را بدون تغییر به لایه زیرین می سپارد.
        public Task<bool> ConnectAsync(string endpoint, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return inner.ConnectAsync(endpoint, cancellationToken);
        }

        // این تابع فقط مدت نوشتن و نتیجه ارسال را اندازه می گیرد و همان بسته را بدون تغییر به راه انتقال اصلی می دهد.
        public async Task<bool> SendAsync(byte[] packet, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            uint transportSequence;
            try
            {
                transportSequence = tracker.BeginOutgoingPacket(packet);
            }
            catch (Exception exception)
            {
                Failed?.Invoke("Voice media feedback tracking failed: " + exception.Message);
                return false;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            bool success = false;
            try
            {
                success = await inner.SendAsync(packet, cancellationToken);
                return success;
            }
            finally
            {
                stopwatch.Stop();
                tracker.CompleteOutgoingWrite(transportSequence, stopwatch.ElapsedMilliseconds, success);
                FeedbackUpdated?.Invoke(tracker.GetSnapshot());
            }
        }

        // این تابع قطع اتصال را بدون تغییر به راه انتقال اصلی می سپارد.
        public Task DisconnectAsync(string reason, CancellationToken cancellationToken)
        {
            if (disposed) return Task.CompletedTask;
            return inner.DisconnectAsync(reason, cancellationToken);
        }

        private void HandleConnected()
        {
            Connected?.Invoke();
        }

        private void HandlePacketReceived(byte[] packet)
        {
            try
            {
                tracker.ObserveIncomingPacket(packet);
                FeedbackUpdated?.Invoke(tracker.GetSnapshot());
            }
            catch (Exception exception)
            {
                Failed?.Invoke("Voice media feedback decode failed: " + exception.Message);
            }
            PacketReceived?.Invoke(packet);
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
            if (disposed) throw new ObjectDisposedException(nameof(VoiceObservedMediaTransportV2));
        }

        // این تابع فقط اشتراک رویدادها را آزاد می کند و سپس همان راه انتقال اصلی را آزاد می کند.
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            inner.Connected -= HandleConnected;
            inner.PacketReceived -= HandlePacketReceived;
            inner.Failed -= HandleFailed;
            inner.Disconnected -= HandleDisconnected;
            try { inner.Dispose(); } catch { }
            Connected = null;
            PacketReceived = null;
            Failed = null;
            Disconnected = null;
            FeedbackUpdated = null;
        }
    }
}
