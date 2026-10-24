#if !UNITY_WEBGL || UNITY_EDITOR
using System;
using System.Diagnostics;

namespace Network_A.Voice.Client.MediaV2.Security
{
    public enum VoiceMediaKeyRotationReason
    {
        None,
        MaximumKeyAge,
        MaximumPacketCount
    }

    public readonly struct VoiceMediaKeyRotationDecision
    {
        public readonly bool Required;
        public readonly VoiceMediaKeyRotationReason Reason;

        public VoiceMediaKeyRotationDecision(bool required, VoiceMediaKeyRotationReason reason)
        {
            Required = required;
            Reason = reason;
        }
    }

    public readonly struct VoiceMediaKeyRotationStats
    {
        public readonly bool Active;
        public readonly uint SecurityContextId;
        public readonly long AgeMs;
        public readonly long InboundPackets;
        public readonly long OutboundPackets;
        public readonly bool RotationRequired;
        public readonly VoiceMediaKeyRotationReason RotationReason;

        public VoiceMediaKeyRotationStats(bool active, uint securityContextId, long ageMs, long inboundPackets, long outboundPackets, bool rotationRequired, VoiceMediaKeyRotationReason rotationReason)
        {
            Active = active;
            SecurityContextId = securityContextId;
            AgeMs = ageMs;
            InboundPackets = inboundPackets;
            OutboundPackets = outboundPackets;
            RotationRequired = rotationRequired;
            RotationReason = rotationReason;
        }
    }

    public sealed class VoiceMediaKeyRotationPolicy
    {
        public const long DefaultMaximumKeyAgeMs = 15 * 60 * 1000;
        public const long DefaultMaximumPacketsPerDirection = 45000;
        private readonly object sync = new object();
        private readonly long maximumKeyAgeMs;
        private readonly long maximumPacketsPerDirection;
        private readonly Func<long> monotonicMilliseconds;
        private bool active;
        private uint securityContextId;
        private long startedAtMs;
        private long inboundPackets;
        private long outboundPackets;
        private bool rotationRequired;
        private VoiceMediaKeyRotationReason rotationReason;

        public VoiceMediaKeyRotationPolicy(long maximumKeyAgeMs = DefaultMaximumKeyAgeMs, long maximumPacketsPerDirection = DefaultMaximumPacketsPerDirection, Func<long> monotonicMilliseconds = null)
        {
            if (maximumKeyAgeMs < 1000 || maximumKeyAgeMs > 24 * 60 * 60 * 1000) throw new ArgumentOutOfRangeException(nameof(maximumKeyAgeMs));
            if (maximumPacketsPerDirection < 2 || maximumPacketsPerDirection >= uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(maximumPacketsPerDirection));
            this.maximumKeyAgeMs = maximumKeyAgeMs;
            this.maximumPacketsPerDirection = maximumPacketsPerDirection;
            this.monotonicMilliseconds = monotonicMilliseconds ?? ReadMonotonicMilliseconds;
        }

        public void Begin(uint contextId)
        {
            if (contextId == 0) throw new ArgumentOutOfRangeException(nameof(contextId));
            lock (sync)
            {
                active = true;
                securityContextId = contextId;
                startedAtMs = ReadCurrentTime();
                inboundPackets = 0;
                outboundPackets = 0;
                rotationRequired = false;
                rotationReason = VoiceMediaKeyRotationReason.None;
            }
        }

        public VoiceMediaKeyRotationDecision Evaluate()
        {
            lock (sync) return EvaluateLocked();
        }

        public VoiceMediaKeyRotationDecision RecordInbound()
        {
            lock (sync)
            {
                if (!active) throw new InvalidOperationException("Key rotation context is inactive.");
                inboundPackets += 1;
                return EvaluateLocked();
            }
        }

        public VoiceMediaKeyRotationDecision RecordOutbound()
        {
            lock (sync)
            {
                if (!active) throw new InvalidOperationException("Key rotation context is inactive.");
                outboundPackets += 1;
                return EvaluateLocked();
            }
        }

        public VoiceMediaKeyRotationStats GetStats()
        {
            lock (sync)
            {
                VoiceMediaKeyRotationDecision decision = EvaluateLocked();
                long ageMs = active ? Math.Max(0, ReadCurrentTime() - startedAtMs) : 0;
                return new VoiceMediaKeyRotationStats(active, securityContextId, ageMs, inboundPackets, outboundPackets, decision.Required, decision.Reason);
            }
        }

        public void Reset()
        {
            lock (sync)
            {
                active = false;
                securityContextId = 0;
                startedAtMs = 0;
                inboundPackets = 0;
                outboundPackets = 0;
                rotationRequired = false;
                rotationReason = VoiceMediaKeyRotationReason.None;
            }
        }

        private VoiceMediaKeyRotationDecision EvaluateLocked()
        {
            if (!active) return new VoiceMediaKeyRotationDecision(false, VoiceMediaKeyRotationReason.None);
            if (!rotationRequired)
            {
                long ageMs = Math.Max(0, ReadCurrentTime() - startedAtMs);
                if (ageMs >= maximumKeyAgeMs)
                {
                    rotationRequired = true;
                    rotationReason = VoiceMediaKeyRotationReason.MaximumKeyAge;
                }
                else if (inboundPackets >= maximumPacketsPerDirection || outboundPackets >= maximumPacketsPerDirection)
                {
                    rotationRequired = true;
                    rotationReason = VoiceMediaKeyRotationReason.MaximumPacketCount;
                }
            }
            return new VoiceMediaKeyRotationDecision(rotationRequired, rotationReason);
        }

        private long ReadCurrentTime()
        {
            long value = monotonicMilliseconds();
            if (value < 0) throw new InvalidOperationException("Monotonic time is invalid.");
            return value;
        }

        private static long ReadMonotonicMilliseconds()
        {
            return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));
        }
    }
}
#endif
