#if !UNITY_WEBGL || UNITY_EDITOR
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Network_A.Voice.Client.MediaV2.Security
{
    public readonly struct VoiceSecuredMediaTransportStats
    {
        public readonly bool SecurityReady;
        public readonly uint SecurityContextId;
        public readonly long NegotiatedContexts;
        public readonly long OutboundProtectedPackets;
        public readonly long InboundProtectedPackets;
        public readonly long EncryptedPackets;
        public readonly long DecryptedPackets;
        public readonly long RejectedPackets;
        public readonly bool KeyRotationRequired;
        public readonly VoiceMediaKeyRotationReason KeyRotationReason;
        public readonly long KeyAgeMs;

        public VoiceSecuredMediaTransportStats(bool securityReady, uint securityContextId, long negotiatedContexts, long outboundProtectedPackets, long inboundProtectedPackets, long encryptedPackets, long decryptedPackets, long rejectedPackets, bool keyRotationRequired, VoiceMediaKeyRotationReason keyRotationReason, long keyAgeMs)
        {
            SecurityReady = securityReady;
            SecurityContextId = securityContextId;
            NegotiatedContexts = negotiatedContexts;
            OutboundProtectedPackets = outboundProtectedPackets;
            InboundProtectedPackets = inboundProtectedPackets;
            EncryptedPackets = encryptedPackets;
            DecryptedPackets = decryptedPackets;
            RejectedPackets = rejectedPackets;
            KeyRotationRequired = keyRotationRequired;
            KeyRotationReason = keyRotationReason;
            KeyAgeMs = keyAgeMs;
        }
    }

    public sealed class VoiceSecuredMediaTransportV2 : IVoiceMediaTransportV2
    {
        private readonly IVoiceMediaTransportV2 inner;
        private readonly object sync = new object();
        private readonly VoiceMediaKeyRotationPolicy keyRotationPolicy;
        private VoiceMediaPacketIntegrityAdapter outboundPolicy;
        private VoiceMediaPacketIntegrityAdapter inboundPolicy;
        private VoiceMediaPayloadCipher outboundCipher;
        private VoiceMediaPayloadCipher inboundCipher;
        private VoiceMediaBindSecurityIdentity pendingIdentity;
        private string pendingStreamId = string.Empty;
        private bool hasPendingIdentity;
        private uint securityContextId;
        private long negotiatedContexts;
        private long outboundProtectedPackets;
        private long inboundProtectedPackets;
        private long encryptedPackets;
        private long decryptedPackets;
        private long rejectedPackets;
        private bool disposed;

        public event Action Connected;
        public event Action<byte[]> PacketReceived;
        public event Action<string> Failed;
        public event Action<string> Disconnected;

        public bool IsConnected => !disposed && inner.IsConnected;
        public bool IsSecurityReady { get { lock (sync) return outboundPolicy != null && inboundPolicy != null && outboundCipher != null && inboundCipher != null && securityContextId != 0; } }

        public VoiceSecuredMediaTransportV2(IVoiceMediaTransportV2 inner, VoiceMediaKeyRotationPolicy keyRotationPolicy = null)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.keyRotationPolicy = keyRotationPolicy ?? new VoiceMediaKeyRotationPolicy();
            inner.Connected += HandleConnected;
            inner.PacketReceived += HandlePacketReceived;
            inner.Failed += HandleFailed;
            inner.Disconnected += HandleDisconnected;
        }

        public async Task<bool> ConnectAsync(string endpoint, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            ResetSecurityState();
            return await inner.ConnectAsync(endpoint, cancellationToken);
        }

        public async Task<bool> SendAsync(byte[] packetBytes, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            try
            {
                VoiceMediaV2Packet packet = VoiceMediaV2Packet.Decode(packetBytes);
                if (packet.Kind == VoiceMediaV2PacketKind.BindRequest)
                {
                    VoiceMediaBindSecurityIdentity identity = VoiceMediaSecurityHandshake.DecodeBindIdentity(packet.Payload);
                    byte[] securedBindPayload = VoiceMediaV2BindPayload.EncodeRequest(identity.AccessToken, identity.RoomId, identity.ControlConnectionId, identity.Platform, VoiceMediaSecurityHandshake.AddCapability(identity.ClientBuild));
                    lock (sync)
                    {
                        ClearPoliciesLocked();
                        pendingIdentity = identity;
                        pendingStreamId = (packet.StreamId ?? string.Empty).Trim().ToLowerInvariant();
                        hasPendingIdentity = true;
                    }
                    return await inner.SendAsync(EncodeLegacyPacket(packet, securedBindPayload), cancellationToken);
                }

                byte[] protectedPacket = packetBytes;
                VoiceMediaKeyRotationDecision rotation = keyRotationPolicy.Evaluate();
                if (rotation.Required)
                {
                    Failed?.Invoke("Voice media key rotation is required: " + rotation.Reason);
                    return false;
                }
                lock (sync)
                {
                    if (outboundPolicy != null && outboundCipher != null && IsProtectedKind(packet.Kind))
                    {
                        protectedPacket = outboundPolicy.Protect(outboundCipher.Encrypt(packet));
                        outboundProtectedPackets += 1;
                        encryptedPackets += 1;
                        keyRotationPolicy.RecordOutbound();
                    }
                }
                return await inner.SendAsync(protectedPacket, cancellationToken);
            }
            catch (Exception exception)
            {
                Failed?.Invoke("Voice media packet security send failed: " + exception.Message);
                return false;
            }
        }

        public Task DisconnectAsync(string reason, CancellationToken cancellationToken)
        {
            if (disposed) return Task.CompletedTask;
            ResetSecurityState();
            return inner.DisconnectAsync(reason, cancellationToken);
        }

        public VoiceSecuredMediaTransportStats GetStats()
        {
            lock (sync)
            {
                VoiceMediaKeyRotationStats rotation = keyRotationPolicy.GetStats();
                return new VoiceSecuredMediaTransportStats(outboundPolicy != null && inboundPolicy != null && outboundCipher != null && inboundCipher != null && securityContextId != 0, securityContextId, negotiatedContexts, outboundProtectedPackets, inboundProtectedPackets, encryptedPackets, decryptedPackets, rejectedPackets, rotation.RotationRequired, rotation.RotationReason, rotation.AgeMs);
            }
        }

        private void HandlePacketReceived(byte[] packetBytes)
        {
            try
            {
                VoiceMediaV2Packet packet = VoiceMediaV2Packet.Decode(packetBytes);
                if (packet.Kind == VoiceMediaV2PacketKind.BindResult)
                {
                    VoiceMediaV2BindResult result = VoiceMediaV2BindPayload.DecodeResult(packet.Payload);
                    if (result.Success) NegotiateSecurity(result.Message);
                    PacketReceived?.Invoke(packetBytes);
                    return;
                }

                VoiceMediaKeyRotationDecision rotation = keyRotationPolicy.Evaluate();
                if (rotation.Required) throw new InvalidDataException("Voice media key rotation is required: " + rotation.Reason);
                VoiceMediaPacketIntegrityResult verified;
                lock (sync)
                {
                    if (inboundPolicy == null || !IsProtectedKind(packet.Kind))
                    {
                        rejectedPackets += 1;
                        throw new InvalidDataException("Protected media packet was required.");
                    }
                    verified = inboundPolicy.VerifyAndUnwrap(packetBytes);
                    if (!verified.Accepted)
                    {
                        rejectedPackets += 1;
                        if (verified.Reason == VoiceMediaPacketIntegrityReason.ReplayRejected) return;
                        throw new InvalidDataException("Protected media packet was rejected: " + verified.Reason);
                    }
                    inboundProtectedPackets += 1;
                }
                VoiceMediaV2Packet decrypted;
                lock (sync)
                {
                    if (inboundCipher == null) throw new InvalidDataException("Media payload cipher is missing.");
                    decrypted = inboundCipher.Decrypt(verified.Packet);
                    decryptedPackets += 1;
                    keyRotationPolicy.RecordInbound();
                }
                PacketReceived?.Invoke(EncodeLegacyPacket(decrypted, decrypted.Payload));
            }
            catch (Exception exception)
            {
                Failed?.Invoke("Voice media packet security receive failed: " + exception.Message);
            }
        }

        private void NegotiateSecurity(string message)
        {
            VoiceMediaBindSecurityIdentity identity;
            string streamId;
            lock (sync)
            {
                if (!hasPendingIdentity) throw new InvalidDataException("Media security bind identity is missing.");
                identity = pendingIdentity;
                streamId = pendingStreamId;
            }
            VoiceMediaSecurityHandshakeData handshake = VoiceMediaSecurityHandshake.Decode(message);
            using (VoiceMediaSecurityKeys keys = VoiceMediaSecurityHandshake.DeriveKeys(identity.AccessToken, identity.RoomId, identity.ControlConnectionId, streamId, handshake.SecurityContextId, handshake.Nonce))
            {
                lock (sync)
                {
                    ClearPoliciesLocked();
                    outboundPolicy = new VoiceMediaPacketIntegrityAdapter(handshake.SecurityContextId, keys.ClientToServerKey);
                    inboundPolicy = new VoiceMediaPacketIntegrityAdapter(handshake.SecurityContextId, keys.ServerToClientKey);
                    outboundCipher = new VoiceMediaPayloadCipher(handshake.SecurityContextId, keys.ClientToServerEncryptionKey);
                    inboundCipher = new VoiceMediaPayloadCipher(handshake.SecurityContextId, keys.ServerToClientEncryptionKey);
                    securityContextId = handshake.SecurityContextId;
                    keyRotationPolicy.Begin(handshake.SecurityContextId);
                    negotiatedContexts += 1;
                    pendingIdentity = default;
                    pendingStreamId = string.Empty;
                    hasPendingIdentity = false;
                }
            }
            Array.Clear(handshake.Nonce, 0, handshake.Nonce.Length);
        }

        private static byte[] EncodeLegacyPacket(VoiceMediaV2Packet packet, byte[] payload)
        {
            return new VoiceMediaV2Packet
            {
                Kind = packet.Kind,
                Codec = packet.Codec,
                Flags = packet.Flags,
                TransportSequence = packet.TransportSequence,
                MediaSequence = packet.MediaSequence,
                MediaTimestamp100Ns = packet.MediaTimestamp100Ns,
                AckTransportSequence = packet.AckTransportSequence,
                AckMask = packet.AckMask,
                SessionId = packet.SessionId,
                StreamId = packet.StreamId,
                SenderId = packet.SenderId,
                SecurityContextId = 0,
                Payload = payload ?? Array.Empty<byte>()
            }.Encode();
        }

        private static bool IsProtectedKind(VoiceMediaV2PacketKind kind)
        {
            return kind == VoiceMediaV2PacketKind.Media || kind == VoiceMediaV2PacketKind.Ping || kind == VoiceMediaV2PacketKind.Pong;
        }

        private void HandleConnected() { Connected?.Invoke(); }
        private void HandleFailed(string message) { Failed?.Invoke(message); }

        private void HandleDisconnected(string reason)
        {
            ResetSecurityState();
            Disconnected?.Invoke(reason);
        }

        private void ResetSecurityState()
        {
            lock (sync)
            {
                ClearPoliciesLocked();
                pendingIdentity = default;
                pendingStreamId = string.Empty;
                hasPendingIdentity = false;
            }
        }

        private void ClearPoliciesLocked()
        {
            outboundPolicy?.Dispose();
            inboundPolicy?.Dispose();
            outboundCipher?.Dispose();
            inboundCipher?.Dispose();
            outboundPolicy = null;
            inboundPolicy = null;
            outboundCipher = null;
            inboundCipher = null;
            keyRotationPolicy.Reset();
            securityContextId = 0;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(VoiceSecuredMediaTransportV2));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            inner.Connected -= HandleConnected;
            inner.PacketReceived -= HandlePacketReceived;
            inner.Failed -= HandleFailed;
            inner.Disconnected -= HandleDisconnected;
            ResetSecurityState();
            try { inner.Dispose(); } catch { }
            Connected = null;
            PacketReceived = null;
            Failed = null;
            Disconnected = null;
        }
    }
}
#endif
