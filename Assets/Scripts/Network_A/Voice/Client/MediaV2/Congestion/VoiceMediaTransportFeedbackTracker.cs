using System;
using System.Collections.Generic;

namespace Network_A.Voice.Client.MediaV2.Congestion
{
    public readonly struct VoiceMediaTransportFeedbackSnapshot
    {
        public VoiceMediaTransportFeedbackSnapshot(
            long sentPackets,
            long sentBytes,
            long acknowledgedPackets,
            long acknowledgedBytes,
            long finalizedLostPackets,
            long sendFailures,
            int inFlightPackets,
            double latestRttMs,
            double smoothedRttMs,
            double smoothedWriteDurationMs,
            double acknowledgedThroughputKbps,
            long estimatedIncomingLostPackets,
            long feedbackAgeMs)
        {
            SentPackets = sentPackets;
            SentBytes = sentBytes;
            AcknowledgedPackets = acknowledgedPackets;
            AcknowledgedBytes = acknowledgedBytes;
            FinalizedLostPackets = finalizedLostPackets;
            SendFailures = sendFailures;
            InFlightPackets = inFlightPackets;
            LatestRttMs = latestRttMs;
            SmoothedRttMs = smoothedRttMs;
            SmoothedWriteDurationMs = smoothedWriteDurationMs;
            AcknowledgedThroughputKbps = acknowledgedThroughputKbps;
            EstimatedIncomingLostPackets = estimatedIncomingLostPackets;
            FeedbackAgeMs = feedbackAgeMs;
        }

        public long SentPackets { get; }
        public long SentBytes { get; }
        public long AcknowledgedPackets { get; }
        public long AcknowledgedBytes { get; }
        public long FinalizedLostPackets { get; }
        public long SendFailures { get; }
        public int InFlightPackets { get; }
        public double LatestRttMs { get; }
        public double SmoothedRttMs { get; }
        public double SmoothedWriteDurationMs { get; }
        public double AcknowledgedThroughputKbps { get; }
        public long EstimatedIncomingLostPackets { get; }
        public long FeedbackAgeMs { get; }

        public double FinalizedLossPercent
        {
            get
            {
                long total = AcknowledgedPackets + FinalizedLostPackets;
                return total <= 0 ? 0d : FinalizedLostPackets * 100d / total;
            }
        }
    }

    public sealed class VoiceMediaTransportFeedbackTracker
    {
        private const int MaximumTrackedPackets = 2048;
        private const long ThroughputWindowMs = 1000;
        private const double RttSmoothingFactor = 0.125d;
        private const double WriteSmoothingFactor = 0.125d;

        private readonly object sync = new object();
        private readonly Func<long> nowMs;
        private readonly Dictionary<uint, SentPacketRecord> pending = new Dictionary<uint, SentPacketRecord>();
        private readonly Queue<AcknowledgedBytesRecord> acknowledgedBytesWindow = new Queue<AcknowledgedBytesRecord>();

        private long sentPackets;
        private long sentBytes;
        private long acknowledgedPackets;
        private long acknowledgedBytes;
        private long finalizedLostPackets;
        private long sendFailures;
        private long estimatedIncomingLostPackets;
        private uint lastIncomingTransportSequence;
        private bool hasIncomingTransportSequence;
        private double latestRttMs;
        private double smoothedRttMs;
        private bool hasRtt;
        private double smoothedWriteDurationMs;
        private bool hasWriteDuration;
        private long lastFeedbackAtMs = -1;

        public VoiceMediaTransportFeedbackTracker(Func<long> nowMs = null)
        {
            this.nowMs = nowMs != null ? nowMs : new Func<long>(GetMonotonicMilliseconds);
        }

        private static long GetMonotonicMilliseconds()
        {
            return (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000d / System.Diagnostics.Stopwatch.Frequency);
        }

        // این تابع بسته را درست پیش از نوشتن ثبت می کند تا پاسخ بسیار سریع طرف مقابل نیز از دست نرود.
        public uint BeginOutgoingPacket(byte[] encodedPacket)
        {
            VoiceMediaV2Packet packet = VoiceMediaV2Packet.Decode(encodedPacket);
            long now = nowMs();
            int bytes = encodedPacket == null ? 0 : encodedPacket.Length;
            lock (sync)
            {
                sentPackets++;
                sentBytes += bytes;
                pending[packet.TransportSequence] = new SentPacketRecord(now, bytes);
                TrimPendingLocked();
            }
            return packet.TransportSequence;
        }

        // این تابع پایان نوشتن بسته را ثبت می کند و در صورت شکست، بسته ناموفق را از انتظار تایید خارج می کند.
        public void CompleteOutgoingWrite(uint transportSequence, long writeDurationMs, bool success)
        {
            lock (sync)
            {
                ObserveWriteDurationLocked(Math.Max(0L, writeDurationMs));
                if (success) return;
                sendFailures++;
                if (pending.TryGetValue(transportSequence, out SentPacketRecord record))
                {
                    pending.Remove(transportSequence);
                    sentPackets = Math.Max(0L, sentPackets - 1L);
                    sentBytes = Math.Max(0L, sentBytes - record.Bytes);
                }
            }
        }

        // این تابع برای آزمون های مستقیم، ثبت شروع و پایان نوشتن را در یک فراخوانی انجام می دهد.
        public void ObserveOutgoingPacket(byte[] encodedPacket, long writeDurationMs, bool success)
        {
            uint sequence = BeginOutgoingPacket(encodedPacket);
            CompleteOutgoingWrite(sequence, writeDurationMs, success);
        }

        // این تابع بسته دریافتی را فقط برای شماره راه انتقال و تاییدهای برگشتی مشاهده می کند و هیچ تغییری در خود بسته نمی دهد.
        public void ObserveIncomingPacket(byte[] encodedPacket)
        {
            VoiceMediaV2Packet packet = VoiceMediaV2Packet.Decode(encodedPacket);
            long now = nowMs();
            lock (sync)
            {
                ObserveIncomingSequenceLocked(packet.TransportSequence);
                ApplyAcknowledgementLocked(packet.AckTransportSequence, packet.AckMask, now);
                lastFeedbackAtMs = now;
            }
        }

        // این تابع نمای فقط خواندنی اندازه گیری های فعلی را برمی گرداند و هیچ تصمیمی برای تغییر کیفیت نمی گیرد.
        public VoiceMediaTransportFeedbackSnapshot GetSnapshot()
        {
            long now = nowMs();
            lock (sync)
            {
                TrimAcknowledgedWindowLocked(now);
                long windowBytes = 0;
                foreach (AcknowledgedBytesRecord record in acknowledgedBytesWindow) windowBytes += record.Bytes;
                double throughputKbps = windowBytes * 8d / ThroughputWindowMs;
                long feedbackAgeMs = lastFeedbackAtMs < 0 ? -1 : Math.Max(0L, now - lastFeedbackAtMs);
                return new VoiceMediaTransportFeedbackSnapshot(
                    sentPackets,
                    sentBytes,
                    acknowledgedPackets,
                    acknowledgedBytes,
                    finalizedLostPackets,
                    sendFailures,
                    pending.Count,
                    latestRttMs,
                    smoothedRttMs,
                    smoothedWriteDurationMs,
                    throughputKbps,
                    estimatedIncomingLostPackets,
                    feedbackAgeMs);
            }
        }

        private void ApplyAcknowledgementLocked(uint ackSequence, uint ackMask, long now)
        {
            if (ackSequence == 0 || pending.Count == 0) return;
            List<uint> completed = null;
            List<uint> lost = null;

            foreach (KeyValuePair<uint, SentPacketRecord> item in pending)
            {
                uint sequence = item.Key;
                if (IsAcknowledged(sequence, ackSequence, ackMask))
                {
                    if (completed == null) completed = new List<uint>();
                    completed.Add(sequence);
                    SentPacketRecord record = item.Value;
                    double rtt = Math.Max(0L, now - record.SentAtMs);
                    latestRttMs = rtt;
                    smoothedRttMs = hasRtt ? smoothedRttMs + RttSmoothingFactor * (rtt - smoothedRttMs) : rtt;
                    hasRtt = true;
                    acknowledgedPackets++;
                    acknowledgedBytes += record.Bytes;
                    acknowledgedBytesWindow.Enqueue(new AcknowledgedBytesRecord(now, record.Bytes));
                    continue;
                }

                if (IsFinalizedLost(sequence, ackSequence))
                {
                    if (lost == null) lost = new List<uint>();
                    lost.Add(sequence);
                    finalizedLostPackets++;
                }
            }

            if (completed != null)
            {
                foreach (uint sequence in completed) pending.Remove(sequence);
            }
            if (lost != null)
            {
                foreach (uint sequence in lost) pending.Remove(sequence);
            }
            TrimAcknowledgedWindowLocked(now);
        }

        private void ObserveIncomingSequenceLocked(uint sequence)
        {
            if (sequence == 0) return;
            if (!hasIncomingTransportSequence)
            {
                hasIncomingTransportSequence = true;
                lastIncomingTransportSequence = sequence;
                return;
            }

            uint forward = sequence - lastIncomingTransportSequence;
            if (forward > 0 && forward < 0x80000000u)
            {
                if (forward > 1) estimatedIncomingLostPackets += forward - 1;
                lastIncomingTransportSequence = sequence;
            }
        }

        private void ObserveWriteDurationLocked(long writeDurationMs)
        {
            smoothedWriteDurationMs = hasWriteDuration
                ? smoothedWriteDurationMs + WriteSmoothingFactor * (writeDurationMs - smoothedWriteDurationMs)
                : writeDurationMs;
            hasWriteDuration = true;
        }

        private void TrimAcknowledgedWindowLocked(long now)
        {
            long minimum = now - ThroughputWindowMs;
            while (acknowledgedBytesWindow.Count > 0 && acknowledgedBytesWindow.Peek().AcknowledgedAtMs < minimum)
                acknowledgedBytesWindow.Dequeue();
        }

        private void TrimPendingLocked()
        {
            if (pending.Count <= MaximumTrackedPackets) return;
            uint oldestSequence = 0;
            long oldestTime = long.MaxValue;
            bool found = false;
            foreach (KeyValuePair<uint, SentPacketRecord> item in pending)
            {
                if (item.Value.SentAtMs >= oldestTime) continue;
                oldestTime = item.Value.SentAtMs;
                oldestSequence = item.Key;
                found = true;
            }
            if (found) pending.Remove(oldestSequence);
        }

        private static bool IsAcknowledged(uint sequence, uint ackSequence, uint ackMask)
        {
            if (sequence == ackSequence) return true;
            uint distance = ackSequence - sequence;
            if (distance == 0 || distance > 32 || distance >= 0x80000000u) return false;
            uint bit = 1u << ((int)distance - 1);
            return (ackMask & bit) != 0;
        }

        private static bool IsFinalizedLost(uint sequence, uint ackSequence)
        {
            uint distance = ackSequence - sequence;
            return distance > 32 && distance < 0x80000000u;
        }

        private readonly struct SentPacketRecord
        {
            public SentPacketRecord(long sentAtMs, int bytes)
            {
                SentAtMs = sentAtMs;
                Bytes = bytes;
            }

            public long SentAtMs { get; }
            public int Bytes { get; }
        }

        private readonly struct AcknowledgedBytesRecord
        {
            public AcknowledgedBytesRecord(long acknowledgedAtMs, int bytes)
            {
                AcknowledgedAtMs = acknowledgedAtMs;
                Bytes = bytes;
            }

            public long AcknowledgedAtMs { get; }
            public int Bytes { get; }
        }
    }
}
