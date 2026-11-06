#if !UNITY_WEBGL || UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Network_A.Voice.Client.MediaV2.Security
{
    public readonly struct VoiceMediaPayloadCipherStats
    {
        public readonly uint SecurityContextId;
        public readonly long EncryptedPackets;
        public readonly long DecryptedPackets;

        public VoiceMediaPayloadCipherStats(uint securityContextId, long encryptedPackets, long decryptedPackets)
        {
            SecurityContextId = securityContextId;
            EncryptedPackets = encryptedPackets;
            DecryptedPackets = decryptedPackets;
        }
    }

    public sealed class VoiceMediaPayloadCipher : IDisposable
    {
        public const int EncryptionKeyBytes = 32;
        private const int CounterBytes = 16;
        private static readonly byte[] CounterDomain = Encoding.ASCII.GetBytes("VME2ENC1");
        private readonly object sync = new object();
        private readonly uint securityContextId;
        private readonly byte[] encryptionKey;
        private readonly Aes aes;
        private readonly ICryptoTransform blockEncryptor;
        private long encryptedPackets;
        private long decryptedPackets;
        private bool disposed;

        public VoiceMediaPayloadCipher(uint securityContextId, byte[] key)
        {
            if (securityContextId == 0) throw new ArgumentOutOfRangeException(nameof(securityContextId));
            if (key == null || key.Length != EncryptionKeyBytes) throw new ArgumentException("Media encryption key must contain exactly 32 bytes.", nameof(key));
            this.securityContextId = securityContextId;
            encryptionKey = new byte[key.Length];
            Buffer.BlockCopy(key, 0, encryptionKey, 0, key.Length);
            aes = Aes.Create();
            if (aes == null) throw new CryptographicException("AES provider is unavailable.");
            aes.KeySize = 256;
            aes.BlockSize = 128;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = encryptionKey;
            blockEncryptor = aes.CreateEncryptor();
        }

        public VoiceMediaV2Packet Encrypt(VoiceMediaV2Packet packet)
        {
            return Transform(packet, true);
        }

        public VoiceMediaV2Packet Decrypt(VoiceMediaV2Packet packet)
        {
            return Transform(packet, false);
        }

        public VoiceMediaPayloadCipherStats GetStats()
        {
            lock (sync) return new VoiceMediaPayloadCipherStats(securityContextId, encryptedPackets, decryptedPackets);
        }

        private VoiceMediaV2Packet Transform(VoiceMediaV2Packet packet, bool encrypting)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));
            if (!IsEncryptedKind(packet.Kind)) throw new InvalidDataException("Packet kind cannot be encrypted.");
            if (packet.TransportSequence == 0) throw new InvalidDataException("Encrypted packet transport sequence is invalid.");
            byte[] input = packet.Payload ?? Array.Empty<byte>();
            byte[] output = new byte[input.Length];
            byte[] counter = BuildCounter(packet.TransportSequence);
            byte[] keyStream = new byte[CounterBytes];
            try
            {
                lock (sync)
                {
                    ThrowIfDisposed();
                    for (int offset = 0; offset < input.Length; offset += CounterBytes)
                    {
                        if (blockEncryptor.TransformBlock(counter, 0, CounterBytes, keyStream, 0) != CounterBytes) throw new CryptographicException("AES counter block encryption failed.");
                        int count = Math.Min(CounterBytes, input.Length - offset);
                        for (int index = 0; index < count; index++) output[offset + index] = (byte)(input[offset + index] ^ keyStream[index]);
                        IncrementCounter(counter);
                    }
                    if (encrypting) encryptedPackets += 1; else decryptedPackets += 1;
                }
            }
            finally
            {
                Array.Clear(counter, 0, counter.Length);
                Array.Clear(keyStream, 0, keyStream.Length);
            }
            return CopyPacket(packet, output);
        }

        private byte[] BuildCounter(uint transportSequence)
        {
            byte[] counter = new byte[CounterBytes];
            VoiceMediaV2Packet.WriteUInt32(counter, 0, securityContextId);
            VoiceMediaV2Packet.WriteUInt32(counter, 4, transportSequence);
            Buffer.BlockCopy(CounterDomain, 0, counter, 8, CounterDomain.Length);
            return counter;
        }

        private static void IncrementCounter(byte[] counter)
        {
            for (int index = counter.Length - 1; index >= 0; index--)
            {
                counter[index] += 1;
                if (counter[index] != 0) return;
            }
            throw new CryptographicException("AES counter exhausted.");
        }

        private static VoiceMediaV2Packet CopyPacket(VoiceMediaV2Packet source, byte[] payload)
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
                SecurityContextId = source.SecurityContextId,
                Payload = payload ?? Array.Empty<byte>()
            };
        }

        private static bool IsEncryptedKind(VoiceMediaV2PacketKind kind)
        {
            return kind == VoiceMediaV2PacketKind.Media || kind == VoiceMediaV2PacketKind.Ping || kind == VoiceMediaV2PacketKind.Pong;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(VoiceMediaPayloadCipher));
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                try { blockEncryptor?.Dispose(); } catch { }
                try { aes?.Dispose(); } catch { }
                Array.Clear(encryptionKey, 0, encryptionKey.Length);
            }
        }
    }
}
#endif
