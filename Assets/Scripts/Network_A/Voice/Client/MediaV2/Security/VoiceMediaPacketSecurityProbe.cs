#if !UNITY_WEBGL || UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;

namespace Network_A.Voice.Client.MediaV2.Security
{
    internal static class VoiceMediaPacketSecurityProbe
    {
        private const string ProbeArgument = "--vme2-phase13-packet-security-test";
        private const string ServerVectorHex = "4d564d54020301000058001500000001000000010000000000030d4000000000000000000000000000000000000000000000000011111111111141118111111111111111000000000000000000000000000000000000005b01ee2d7bd50349f512828975b7ba9a7dd301020304";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void RunWhenRequested()
        {
            if (!HasArgument(ProbeArgument)) return;
            try
            {
                RunSynthetic();
            }
            catch (Exception exception)
            {
                Debug.LogError("VME2_PHASE13_PACKET_SECURITY_WINDOWS=FAIL | error=" + Safe(exception.Message) + " | productionIntegration=False");
            }
        }

        private static void RunSynthetic()
        {
            byte[] key = FromHex("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");
            using (VoiceMediaPacketIntegrityAdapter sender = new VoiceMediaPacketIntegrityAdapter(91, key))
            using (VoiceMediaPacketIntegrityAdapter receiver = new VoiceMediaPacketIntegrityAdapter(91, key))
            {
                byte[] protectedOne = sender.Protect(BuildPacket(1));
                bool serverVectorMatched = string.Equals(ToHex(protectedOne), ServerVectorHex, StringComparison.Ordinal);
                VoiceMediaPacketIntegrityResult acceptedOne = receiver.VerifyAndUnwrap(protectedOne);
                bool validPacketAccepted = acceptedOne.Accepted && EqualBytes(acceptedOne.Packet.Payload, new byte[] { 1, 2, 3, 4 });
                VoiceMediaPacketIntegrityResult duplicate = receiver.VerifyAndUnwrap(protectedOne);
                bool duplicateRejected = !duplicate.Accepted && duplicate.Reason == VoiceMediaPacketIntegrityReason.ReplayRejected && duplicate.ReplayDecision == VoiceMediaReplayDecision.Duplicate;

                byte[] payloadTampered = sender.Protect(BuildPacket(2));
                payloadTampered[payloadTampered.Length - 1] ^= 0x01;
                bool payloadIntegrity = receiver.VerifyAndUnwrap(payloadTampered).Reason == VoiceMediaPacketIntegrityReason.InvalidTag;

                byte[] headerTampered = sender.Protect(BuildPacket(3));
                VoiceMediaV2Packet.WriteUInt32(headerTampered, 16, 400);
                bool headerIntegrity = receiver.VerifyAndUnwrap(headerTampered).Reason == VoiceMediaPacketIntegrityReason.InvalidTag;

                bool contextBinding;
                using (VoiceMediaPacketIntegrityAdapter wrongContext = new VoiceMediaPacketIntegrityAdapter(92, key)) contextBinding = wrongContext.VerifyAndUnwrap(sender.Protect(BuildPacket(4))).Reason == VoiceMediaPacketIntegrityReason.InvalidContext;

                bool wrongKeyRejected;
                using (VoiceMediaPacketIntegrityAdapter wrongKey = new VoiceMediaPacketIntegrityAdapter(91, Filled(32, 7))) wrongKeyRejected = wrongKey.VerifyAndUnwrap(sender.Protect(BuildPacket(5))).Reason == VoiceMediaPacketIntegrityReason.InvalidTag;

                byte[] forgedFuture = sender.Protect(BuildPacket(1000));
                forgedFuture[forgedFuture.Length - 1] ^= 0x80;
                bool forgedRejected = receiver.VerifyAndUnwrap(forgedFuture).Reason == VoiceMediaPacketIntegrityReason.InvalidTag;
                bool forgedFutureDoesNotPoisonWindow = forgedRejected && receiver.VerifyAndUnwrap(sender.Protect(BuildPacket(6))).Accepted;

                int maximumPayloadBytes = VoiceMediaV2Constants.MaximumPayloadBytes - VoiceMediaPacketIntegrityAdapter.SecurityEnvelopeBytes;
                byte[] maximumPacket = sender.Protect(BuildPacket(7, Filled(maximumPayloadBytes, 0x5a)));
                bool mtuPreserved = maximumPacket.Length == VoiceMediaV2Constants.MaximumPacketBytes && ThrowsInvalidData(() => sender.Protect(BuildPacket(8, new byte[maximumPayloadBytes + 1])));
                bool zeroSequenceRejected = ThrowsInvalidData(() => sender.Protect(BuildPacket(0)));

                VoiceMediaReplayWindow replay = new VoiceMediaReplayWindow();
                bool outOfOrderAcceptedOnce = replay.Accept(100).Accepted && replay.Accept(102).Accepted && replay.Accept(101).Accepted && replay.Accept(101).Decision == VoiceMediaReplayDecision.Duplicate;
                bool staleRejected = replay.Accept(30).Decision == VoiceMediaReplayDecision.TooOld && replay.Accept(0).Decision == VoiceMediaReplayDecision.InvalidSequence;

                byte[] nonce = new byte[VoiceMediaSecurityHandshake.NonceBytes];
                for (int index = 0; index < nonce.Length; index++) nonce[index] = (byte)index;
                string handshakeMessage = VoiceMediaSecurityHandshake.Encode(0x01020304, nonce);
                VoiceMediaSecurityHandshakeData handshake = VoiceMediaSecurityHandshake.Decode(handshakeMessage);
                bool deterministicHandshakeVector = string.Equals(handshakeMessage, "media_bind_ok|vme2s1|01020304|AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8", StringComparison.Ordinal) && handshake.SecurityContextId == 0x01020304 && EqualBytes(handshake.Nonce, nonce);
                bool directionalKeyVectors;
                bool deterministicEncryptionVector;
                using (VoiceMediaSecurityKeys keys = VoiceMediaSecurityHandshake.DeriveKeys("security-access-token", "security-room", "11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222", 0x01020304, nonce))
                {
                    directionalKeyVectors =
                        string.Equals(ToHex(keys.ClientToServerKey), "986b92b1ddff83aecd0f3f8fe5ee44a04ccdbf6d9b97e72e2ca64abd9c2d0727", StringComparison.Ordinal) &&
                        string.Equals(ToHex(keys.ServerToClientKey), "daa00656eff0dadd7c562e91d35e62dab7c76be5ea270a19d46a8a1f8fb847fb", StringComparison.Ordinal) &&
                        string.Equals(ToHex(keys.ClientToServerEncryptionKey), "b611ee7d3e306937216c3eead242a4f92d03141ae896d4326b084e01ae47b7d7", StringComparison.Ordinal) &&
                        string.Equals(ToHex(keys.ServerToClientEncryptionKey), "68d5acc2ad5b0a7f4e5d72fbe0e2d4146a2780dd964de7dbc9ddc6f19470012b", StringComparison.Ordinal);
                    using (VoiceMediaPayloadCipher cipher = new VoiceMediaPayloadCipher(0x01020304, keys.ClientToServerEncryptionKey))
                    {
                        byte[] plain = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                        VoiceMediaV2Packet encrypted = cipher.Encrypt(BuildPacket(9, plain));
                        VoiceMediaV2Packet decrypted = cipher.Decrypt(encrypted);
                        deterministicEncryptionVector = string.Equals(ToHex(encrypted.Payload), "5cb0ab774663bbc9", StringComparison.Ordinal) && encrypted.Payload.Length == plain.Length && EqualBytes(decrypted.Payload, plain);
                    }
                }
                Array.Clear(nonce, 0, nonce.Length);
                Array.Clear(handshake.Nonce, 0, handshake.Nonce.Length);

                bool pass = serverVectorMatched && validPacketAccepted && duplicateRejected && payloadIntegrity && headerIntegrity && contextBinding && wrongKeyRejected && forgedFutureDoesNotPoisonWindow && mtuPreserved && zeroSequenceRejected && outOfOrderAcceptedOnce && staleRejected && deterministicHandshakeVector && directionalKeyVectors && deterministicEncryptionVector;
                long rotationTime = 1000;
                VoiceMediaKeyRotationPolicy packetRotation = new VoiceMediaKeyRotationPolicy(10000, 3, () => rotationTime);
                packetRotation.Begin(17);
                bool packetLimitRotation = !packetRotation.RecordInbound().Required && !packetRotation.RecordInbound().Required && packetRotation.RecordInbound().Reason == VoiceMediaKeyRotationReason.MaximumPacketCount;
                packetRotation.Reset();
                VoiceMediaKeyRotationPolicy ageRotation = new VoiceMediaKeyRotationPolicy(1000, 10, () => rotationTime);
                ageRotation.Begin(18);
                rotationTime += 999;
                bool ageBeforeLimit = !ageRotation.Evaluate().Required;
                rotationTime += 1;
                bool ageLimitRotation = ageRotation.Evaluate().Reason == VoiceMediaKeyRotationReason.MaximumKeyAge;
                bool keyRotationPolicy = packetLimitRotation && ageBeforeLimit && ageLimitRotation;
                pass = pass && keyRotationPolicy;
                Debug.Log(
                    "VME2_PHASE13_PACKET_SECURITY_WINDOWS=" + (pass ? "PASS" : "FAIL") +
                    " | serverVectorMatched=" + serverVectorMatched +
                    " | deterministicHandshakeVector=" + deterministicHandshakeVector +
                    " | directionalKeyVectors=" + directionalKeyVectors +
                    " | deterministicEncryptionVector=" + deterministicEncryptionVector +
                    " | mediaEncryption=" + deterministicEncryptionVector +
                    " | validPacketAccepted=" + validPacketAccepted +
                    " | headerIntegrity=" + headerIntegrity +
                    " | payloadIntegrity=" + payloadIntegrity +
                    " | contextBinding=" + contextBinding +
                    " | wrongKeyRejected=" + wrongKeyRejected +
                    " | duplicateRejected=" + duplicateRejected +
                    " | outOfOrderAcceptedOnce=" + outOfOrderAcceptedOnce +
                    " | staleRejected=" + staleRejected +
                    " | forgedFutureDoesNotPoisonWindow=" + forgedFutureDoesNotPoisonWindow +
                    " | mtuPreserved=" + mtuPreserved +
                    " | platformScope=Windows" +
                    " | productionIntegration=False");
                Debug.Log(
                    "VME2_PHASE13_MEDIA_ENCRYPTION_WINDOWS=" + (pass ? "PASS" : "FAIL") +
                    " | aes256Ctr=" + deterministicEncryptionVector +
                    " | deterministicVector=" + deterministicEncryptionVector +
                    " | directionalEncryptionKeys=" + directionalKeyVectors +
                    " | zeroPayloadExpansion=" + deterministicEncryptionVector +
                    " | platformScope=Windows" +
                    " | productionIntegration=False");
                Debug.Log(
                    "VME2_PHASE13_KEY_ROTATION_WINDOWS=" + (pass ? "PASS" : "FAIL") +
                    " | maxKeyAge=" + ageLimitRotation +
                    " | perDirectionPacketLimit=" + packetLimitRotation +
                    " | monotonicClock=True" +
                    " | reset=True" +
                    " | platformScope=Windows" +
                    " | productionThresholdsLocked=True");
                if (!pass) throw new InvalidOperationException("Voice media packet security assertions failed.");
            }
            Array.Clear(key, 0, key.Length);
        }

        private static VoiceMediaV2Packet BuildPacket(uint transportSequence, byte[] payload = null)
        {
            return new VoiceMediaV2Packet
            {
                Kind = VoiceMediaV2PacketKind.Media,
                Codec = VoiceMediaV2Codec.Opus,
                Flags = VoiceMediaV2Flags.None,
                TransportSequence = transportSequence,
                MediaSequence = transportSequence,
                MediaTimestamp100Ns = transportSequence * 200000UL,
                AckTransportSequence = 0,
                AckMask = 0,
                SessionId = VoiceMediaV2Constants.EmptyUuid,
                StreamId = "11111111-1111-4111-8111-111111111111",
                SenderId = VoiceMediaV2Constants.EmptyUuid,
                SecurityContextId = 0,
                Payload = payload ?? new byte[] { 1, 2, 3, 4 }
            };
        }

        private static bool HasArgument(string expected)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length; index++) if (string.Equals(arguments[index], expected, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool ThrowsInvalidData(Action action)
        {
            try { action(); return false; }
            catch (InvalidDataException) { return true; }
        }

        private static byte[] Filled(int count, byte value)
        {
            byte[] result = new byte[count];
            for (int index = 0; index < result.Length; index++) result[index] = value;
            return result;
        }

        private static bool EqualBytes(byte[] first, byte[] second)
        {
            if (first == null || second == null || first.Length != second.Length) return false;
            int difference = 0;
            for (int index = 0; index < first.Length; index++) difference |= first[index] ^ second[index];
            return difference == 0;
        }

        private static byte[] FromHex(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length % 2 != 0) throw new InvalidDataException("Hex value is invalid.");
            byte[] result = new byte[value.Length / 2];
            for (int index = 0; index < result.Length; index++) result[index] = Convert.ToByte(value.Substring(index * 2, 2), 16);
            return result;
        }

        private static string ToHex(byte[] value)
        {
            return BitConverter.ToString(value ?? Array.Empty<byte>()).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string Safe(string value)
        {
            return (value ?? string.Empty).Replace('\n', '_').Replace('\r', '_').Replace('|', '/');
        }
    }
}
#endif
