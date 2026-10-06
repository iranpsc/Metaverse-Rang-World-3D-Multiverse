#if !UNITY_WEBGL || UNITY_EDITOR
using System;

namespace Network_A.Voice.Client.MediaV2.Security
{
    public enum VoiceMediaReplayDecision
    {
        Accepted,
        InvalidSequence,
        Duplicate,
        TooOld,
        Ambiguous
    }

    public readonly struct VoiceMediaReplayResult
    {
        public readonly bool Accepted;
        public readonly VoiceMediaReplayDecision Decision;

        public VoiceMediaReplayResult(bool accepted, VoiceMediaReplayDecision decision)
        {
            Accepted = accepted;
            Decision = decision;
        }
    }

    public readonly struct VoiceMediaReplayStats
    {
        public readonly uint HighestSequence;
        public readonly bool HasHighestSequence;
        public readonly long AcceptedPackets;
        public readonly long DuplicatePackets;
        public readonly long StalePackets;
        public readonly long InvalidPackets;

        public VoiceMediaReplayStats(uint highestSequence, bool hasHighestSequence, long acceptedPackets, long duplicatePackets, long stalePackets, long invalidPackets)
        {
            HighestSequence = highestSequence;
            HasHighestSequence = hasHighestSequence;
            AcceptedPackets = acceptedPackets;
            DuplicatePackets = duplicatePackets;
            StalePackets = stalePackets;
            InvalidPackets = invalidPackets;
        }
    }

    public sealed class VoiceMediaReplayWindow
    {
        public const int WindowSize = 64;
        private readonly object sync = new object();
        private uint highestSequence;
        private ulong receivedBitmap;
        private bool hasHighestSequence;
        private long acceptedPackets;
        private long duplicatePackets;
        private long stalePackets;
        private long invalidPackets;

        public VoiceMediaReplayResult Accept(uint sequence)
        {
            lock (sync)
            {
                if (sequence == 0)
                {
                    invalidPackets += 1;
                    return new VoiceMediaReplayResult(false, VoiceMediaReplayDecision.InvalidSequence);
                }

                if (!hasHighestSequence)
                {
                    hasHighestSequence = true;
                    highestSequence = sequence;
                    receivedBitmap = 1UL;
                    acceptedPackets += 1;
                    return new VoiceMediaReplayResult(true, VoiceMediaReplayDecision.Accepted);
                }

                uint forwardDistance = unchecked(sequence - highestSequence);
                if (forwardDistance == 0)
                {
                    duplicatePackets += 1;
                    return new VoiceMediaReplayResult(false, VoiceMediaReplayDecision.Duplicate);
                }

                if (forwardDistance < 0x80000000u)
                {
                    receivedBitmap = forwardDistance >= WindowSize ? 1UL : (receivedBitmap << (int)forwardDistance) | 1UL;
                    highestSequence = sequence;
                    acceptedPackets += 1;
                    return new VoiceMediaReplayResult(true, VoiceMediaReplayDecision.Accepted);
                }

                uint backwardDistance = unchecked(highestSequence - sequence);
                if (backwardDistance >= 0x80000000u)
                {
                    stalePackets += 1;
                    return new VoiceMediaReplayResult(false, VoiceMediaReplayDecision.Ambiguous);
                }
                if (backwardDistance >= WindowSize)
                {
                    stalePackets += 1;
                    return new VoiceMediaReplayResult(false, VoiceMediaReplayDecision.TooOld);
                }

                ulong bit = 1UL << (int)backwardDistance;
                if ((receivedBitmap & bit) != 0)
                {
                    duplicatePackets += 1;
                    return new VoiceMediaReplayResult(false, VoiceMediaReplayDecision.Duplicate);
                }

                receivedBitmap |= bit;
                acceptedPackets += 1;
                return new VoiceMediaReplayResult(true, VoiceMediaReplayDecision.Accepted);
            }
        }

        public void Reset()
        {
            lock (sync)
            {
                highestSequence = 0;
                receivedBitmap = 0;
                hasHighestSequence = false;
            }
        }

        public VoiceMediaReplayStats GetStats()
        {
            lock (sync)
            {
                return new VoiceMediaReplayStats(highestSequence, hasHighestSequence, acceptedPackets, duplicatePackets, stalePackets, invalidPackets);
            }
        }
    }
}
#endif
