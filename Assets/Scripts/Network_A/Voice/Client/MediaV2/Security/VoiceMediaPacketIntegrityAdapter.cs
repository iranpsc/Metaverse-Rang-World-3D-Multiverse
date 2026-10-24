#if !UNITY_WEBGL || UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;

namespace Network_A.Voice.Client.MediaV2.Security
{
    public enum VoiceMediaPacketIntegrityReason
    {
        Accepted,
        InvalidPacket,
        UnsupportedKind,
        InvalidContext,
        InvalidTag,
        ReplayRejected
    }

    public readonly struct VoiceMediaPacketIntegrityResult
    {
        public readonly bool Accepted;
        public readonly VoiceMediaPacketIntegrityReason Reason;
        public readonly VoiceMediaReplayDecision ReplayDecision;
        public readonly VoiceMediaV2Packet Packet;

        public VoiceMediaPacketIntegrityResult(bool accepted, VoiceMediaPacketIntegrityReason reason, VoiceMediaReplayDecision replayDecision, VoiceMediaV2Packet packet)
        {
            Accepted = accepted;
            Reason = reason;
            ReplayDecision = replayDecision;
            Packet = packet;
        }
    }

    public readonly struct VoiceMediaPacketIntegrityStats
    {
        public readonly uint SecurityContextId;
        public readonly long AuthenticatedPackets;
        public readonly long InvalidPackets;
        public readonly long InvalidContextPackets;
        public readonly long InvalidTagPackets;
        public readonly long ReplayRejectedPackets;

        public VoiceMediaPacketIntegrityStats(uint securityContextId, long authenticatedPackets, long invalidPackets, long invalidContextPackets, long invalidTagPackets, long replayRejectedPackets)
        {
            SecurityContextId = securityContextId;
            AuthenticatedPackets = authenticatedPackets;
            InvalidPackets = invalidPackets;
            InvalidContextPackets = invalidContextPackets;
            InvalidTagPackets = invalidTagPackets;
            ReplayRejectedPackets = replayRejectedPackets;
        }
    }

    public sealed class VoiceMediaPacketIntegrityAdapter : IDisposable
    {
        public const byte SecurityEnvelopeVersion = 1;
        public const int AuthenticationTagBytes = 16;
        public const int SecurityEnvelopeBytes = 1 + AuthenticationTagBytes;
        private readonly object sync = new object();
        private readonly uint securityContextId;
        private readonly byte[] integrityKey;
        private readonly VoiceMediaReplayWindow replayWindow;
        private long authenticatedPackets;
        private long invalidPackets;
        private long invalidContextPackets;
        private long invalidTagPackets;
        private long replayRejectedPackets;
        private bool disposed;

        public VoiceMediaPacketIntegrityAdapter(uint securityContextId, byte[] key, VoiceMediaReplayWindow replayWindow = null)
        {
            if (securityContextId == 0) throw new ArgumentOutOfRangeException(nameof(securityContextId));
            if (key == null || key.Length < 32) throw new ArgumentException("Integrity key must contain at least 32 bytes.", nameof(key));
            this.securityContextId = securityContextId;
            integrityKey = new byte[key.Length];
            Buffer.BlockCopy(key, 0, integrityKey, 0, key.Length);
            this.replayWindow = replayWindow ?? new VoiceMediaReplayWindow();
        }

        public byte[] Protect(VoiceMediaV2Packet packet)
        {
            ThrowIfDisposed();
            if (packet == null) throw new ArgumentNullException(nameof(packet));
            if (!IsProtectedKind(packet.Kind)) throw new InvalidDataException("Packet kind cannot be integrity protected.");
            if (packet.TransportSequence == 0) throw new InvalidDataException("Protected packet transport sequence is invalid.");
            byte[] originalPayload = packet.Payload ?? Array.Empty<byte>();
            int maximumOriginalPayloadBytes = VoiceMediaV2Constants.MaximumPayloadBytes - SecurityEnvelopeBytes;
            if (originalPayload.Length > maximumOriginalPayloadBytes) throw new InvalidDataException("Protected media payload exceeds MTU policy.");

            byte[] envelope = new byte[SecurityEnvelopeBytes + originalPayload.Length];
            envelope[0] = SecurityEnvelopeVersion;
            if (originalPayload.Length > 0) Buffer.BlockCopy(originalPayload, 0, envelope, SecurityEnvelopeBytes, originalPayload.Length);
            VoiceMediaV2Packet protectedPacket = CopyPacket(packet, securityContextId, envelope);
            byte[] encoded = protectedPacket.Encode();
            byte[] tag = CalculateTag(encoded);
            Buffer.BlockCopy(tag, 0, encoded, VoiceMediaV2Constants.HeaderBytes + 1, AuthenticationTagBytes);
            Array.Clear(tag, 0, tag.Length);
            return encoded;
        }

        public VoiceMediaPacketIntegrityResult VerifyAndUnwrap(byte[] input)
        {
            ThrowIfDisposed();
            VoiceMediaV2Packet packet;
            try
            {
                packet = VoiceMediaV2Packet.Decode(input);
            }
            catch
            {
                lock (sync) invalidPackets += 1;
                return Rejected(VoiceMediaPacketIntegrityReason.InvalidPacket);
            }

            if (!IsProtectedKind(packet.Kind) || packet.Payload == null || packet.Payload.Length < SecurityEnvelopeBytes || packet.Payload[0] != SecurityEnvelopeVersion)
            {
                lock (sync) invalidPackets += 1;
                return Rejected(VoiceMediaPacketIntegrityReason.UnsupportedKind);
            }
            if (packet.SecurityContextId != securityContextId)
            {
                lock (sync) invalidContextPackets += 1;
                return Rejected(VoiceMediaPacketIntegrityReason.InvalidContext);
            }

            byte[] unsigned = new byte[input.Length];
            Buffer.BlockCopy(input, 0, unsigned, 0, input.Length);
            Array.Clear(unsigned, VoiceMediaV2Constants.HeaderBytes + 1, AuthenticationTagBytes);
            byte[] expectedTag = CalculateTag(unsigned);
            bool validTag = ConstantTimeEquals(packet.Payload, 1, expectedTag, 0, AuthenticationTagBytes);
            Array.Clear(unsigned, 0, unsigned.Length);
            Array.Clear(expectedTag, 0, expectedTag.Length);
            if (!validTag)
            {
                lock (sync) invalidTagPackets += 1;
                return Rejected(VoiceMediaPacketIntegrityReason.InvalidTag);
            }

            VoiceMediaReplayResult replay = replayWindow.Accept(packet.TransportSequence);
            if (!replay.Accepted)
            {
                lock (sync) replayRejectedPackets += 1;
                return new VoiceMediaPacketIntegrityResult(false, VoiceMediaPacketIntegrityReason.ReplayRejected, replay.Decision, null);
            }

            byte[] payload = new byte[packet.Payload.Length - SecurityEnvelopeBytes];
            if (payload.Length > 0) Buffer.BlockCopy(packet.Payload, SecurityEnvelopeBytes, payload, 0, payload.Length);
            VoiceMediaV2Packet unwrapped = CopyPacket(packet, packet.SecurityContextId, payload);
            lock (sync) authenticatedPackets += 1;
            return new VoiceMediaPacketIntegrityResult(true, VoiceMediaPacketIntegrityReason.Accepted, VoiceMediaReplayDecision.Accepted, unwrapped);
        }

        public VoiceMediaPacketIntegrityStats GetStats()
        {
            lock (sync)
            {
                return new VoiceMediaPacketIntegrityStats(securityContextId, authenticatedPackets, invalidPackets, invalidContextPackets, invalidTagPackets, replayRejectedPackets);
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                Array.Clear(integrityKey, 0, integrityKey.Length);
                replayWindow.Reset();
            }
        }

        private byte[] CalculateTag(byte[] input)
        {
            lock (sync)
            {
                using (HMACSHA256 hmac = new HMACSHA256(integrityKey)) return hmac.ComputeHash(input);
            }
        }

        private static VoiceMediaV2Packet CopyPacket(VoiceMediaV2Packet source, uint contextId, byte[] payload)
        {
            return new VoiceMediaV2Packet
            {
                Kind = source.Kind,
                Codec = source.Codec,
                Flags = source.Flags,
                TransportSequence = source.TransportSequence,
                MediaSequence = source.MediaSequence,
                MediaTimestamp100Ns = source.MediaTimestamp100Ns,
                AckTransportSequence = source.AckTransportSequence,
                AckMask = source.AckMask,
                SessionId = source.SessionId,
                StreamId = source.StreamId,
                SenderId = source.SenderId,
                SecurityContextId = contextId,
                Payload = payload ?? Array.Empty<byte>()
            };
        }

        private static VoiceMediaPacketIntegrityResult Rejected(VoiceMediaPacketIntegrityReason reason)
        {
            return new VoiceMediaPacketIntegrityResult(false, reason, VoiceMediaReplayDecision.InvalidSequence, null);
        }

        private static bool IsProtectedKind(VoiceMediaV2PacketKind kind)
        {
            return kind == VoiceMediaV2PacketKind.Media || kind == VoiceMediaV2PacketKind.Ping || kind == VoiceMediaV2PacketKind.Pong;
        }

        private static bool ConstantTimeEquals(byte[] first, int firstOffset, byte[] second, int secondOffset, int count)
        {
            int difference = 0;
            for (int index = 0; index < count; index++) difference |= first[firstOffset + index] ^ second[secondOffset + index];
            return difference == 0;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(VoiceMediaPacketIntegrityAdapter));
        }
    }
}
#endif
