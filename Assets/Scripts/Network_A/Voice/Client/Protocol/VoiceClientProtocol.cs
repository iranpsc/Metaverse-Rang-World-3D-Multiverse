using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Network_A.Voice.Client.Protocol
{
    public enum VoiceClientMessageType : byte
    {
        AuthRequest = 1,
        AuthResult = 2,
        Heartbeat = 3,
        HeartbeatAck = 4,
        Disconnect = 5,
        Ack = 6,
        SessionSnapshot = 16,
        SessionJoined = 17,
        SessionLeft = 18,
        SessionClosed = 19,
        PublishStart = 32,
        VoiceFrame = 33,
        PublishStop = 34,
        ListenerMuteChanged = 48,
        RecordingConsentChanged = 49,
        RecordingStateChanged = 50,
        ReconnectRequest = 64,
        ReconnectResult = 65,
        Error = 255
    }

    [Flags]
    public enum VoiceClientMessageFlags : ushort
    {
        None = 0,
        AckRequired = 1,
        Dtx = 2,
        EndOfStream = 4,
        Discontinuity = 8
    }

    public enum VoiceClientPlatform : byte
    {
        WebGl = 1,
        Windows = 2,
        Quest = 3
    }

    public enum VoiceClientMuteKind : byte
    {
        SpeakerOff = 1,
        MuteAll = 2,
        PerUser = 3
    }

    public sealed class VoiceClientAuthResult
    {
        public bool Success;
        public bool Retryable;
        public ushort Code;
        public string VoiceConnectionId;
        public string UserId;
        public string Message;
    }

    public sealed class VoiceClientSessionDescriptor
    {
        public string SessionId;
        public byte State;
        public byte Reason;
        public float DistanceMeters;
        public ulong EffectiveAtMs;
        public string PeerUserId;
        public string PeerConnectionId;
    }

    public sealed class VoiceClientReconnectResult
    {
        public bool Success;
        public bool Retryable;
        public ushort Code;
        public string VoiceConnectionId;
        public uint ResumeFromSequence;
        public ulong ServerTimeMs;
        public ushort RetainedSessionCount;
        public string Message;
    }

    public sealed class VoiceClientEnvelope
    {
        public const int FixedHeaderBytes = 60;
        public const string EmptyUuid = "00000000-0000-0000-0000-000000000000";

        public VoiceClientMessageType MessageType;
        public VoiceClientMessageFlags Flags;
        public uint Sequence;
        public ulong TimestampMs;
        public string SessionId;
        public string SenderId;
        public byte[] Payload;

        //* این تابع Envelope نسخه یک را با Header شصت‌بایتی و ترتیب Big Endian می‌سازد.
        public byte[] Encode()
        {
            byte[] payload = Payload ?? Array.Empty<byte>();
            byte[] packet = new byte[FixedHeaderBytes + payload.Length];
            packet[0] = (byte)'M';
            packet[1] = (byte)'V';
            packet[2] = (byte)'V';
            packet[3] = (byte)'C';
            packet[4] = 1;
            packet[5] = (byte)MessageType;
            WriteUInt16(packet, 6, (ushort)Flags);
            WriteUInt16(packet, 8, FixedHeaderBytes);
            WriteUInt16(packet, 10, 0);
            WriteUInt32(packet, 12, (uint)payload.Length);
            WriteUInt32(packet, 16, Sequence);
            WriteUInt64(packet, 20, TimestampMs);
            WriteUuid(packet, 28, SessionId);
            WriteUuid(packet, 44, SenderId);
            Buffer.BlockCopy(payload, 0, packet, FixedHeaderBytes, payload.Length);
            return packet;
        }

        //* این تابع Envelope دریافتی را کامل اعتبارسنجی و رمزگشایی می‌کند.
        public static VoiceClientEnvelope Decode(byte[] packet)
        {
            if (packet == null || packet.Length < FixedHeaderBytes)
                throw new InvalidDataException("Voice envelope is shorter than sixty bytes.");

            if (packet[0] != 'M' || packet[1] != 'V' || packet[2] != 'V' || packet[3] != 'C')
                throw new InvalidDataException("Voice envelope magic is invalid.");

            if (packet[4] != 1)
                throw new InvalidDataException("Voice protocol version is unsupported.");

            ushort headerLength = ReadUInt16(packet, 8);
            uint payloadLength = ReadUInt32(packet, 12);

            if (headerLength != FixedHeaderBytes || packet.Length != headerLength + payloadLength)
                throw new InvalidDataException("Voice envelope length is invalid.");

            byte[] payload = new byte[checked((int)payloadLength)];
            Buffer.BlockCopy(packet, headerLength, payload, 0, payload.Length);

            return new VoiceClientEnvelope
            {
                MessageType = (VoiceClientMessageType)packet[5],
                Flags = (VoiceClientMessageFlags)ReadUInt16(packet, 6),
                Sequence = ReadUInt32(packet, 16),
                TimestampMs = ReadUInt64(packet, 20),
                SessionId = ReadUuid(packet, 28),
                SenderId = ReadUuid(packet, 44),
                Payload = payload
            };
        }

        public static void WriteUInt16(byte[] target, int offset, int value)
        {
            target[offset] = (byte)((value >> 8) & 0xff);
            target[offset + 1] = (byte)(value & 0xff);
        }

        public static void WriteUInt32(byte[] target, int offset, uint value)
        {
            target[offset] = (byte)(value >> 24);
            target[offset + 1] = (byte)(value >> 16);
            target[offset + 2] = (byte)(value >> 8);
            target[offset + 3] = (byte)value;
        }

        public static void WriteUInt64(byte[] target, int offset, ulong value)
        {
            for (int index = 7; index >= 0; index--)
            {
                target[offset + index] = (byte)value;
                value >>= 8;
            }
        }

        public static ushort ReadUInt16(byte[] source, int offset)
        {
            return (ushort)((source[offset] << 8) | source[offset + 1]);
        }

        public static uint ReadUInt32(byte[] source, int offset)
        {
            return ((uint)source[offset] << 24) |
                   ((uint)source[offset + 1] << 16) |
                   ((uint)source[offset + 2] << 8) |
                   source[offset + 3];
        }

        public static ulong ReadUInt64(byte[] source, int offset)
        {
            ulong value = 0;
            for (int index = 0; index < 8; index++) value = (value << 8) | source[offset + index];
            return value;
        }

        public static void WriteUuid(byte[] target, int offset, string value)
        {
            Guid guid;
            if (!Guid.TryParse(string.IsNullOrWhiteSpace(value) ? EmptyUuid : value.Trim(), out guid))
                throw new InvalidDataException("Voice UUID is invalid.");

            byte[] bytes = ParseUuidBytes(guid.ToString("D"));
            Buffer.BlockCopy(bytes, 0, target, offset, 16);
        }

        public static string ReadUuid(byte[] source, int offset)
        {
            byte[] bytes = new byte[16];
            Buffer.BlockCopy(source, offset, bytes, 0, 16);
            string hex = BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            return hex.Substring(0, 8) + "-" + hex.Substring(8, 4) + "-" + hex.Substring(12, 4) + "-" +
                   hex.Substring(16, 4) + "-" + hex.Substring(20, 12);
        }

        private static byte[] ParseUuidBytes(string value)
        {
            string hex = value.Replace("-", string.Empty);
            byte[] result = new byte[16];
            for (int index = 0; index < result.Length; index++)
                result[index] = Convert.ToByte(hex.Substring(index * 2, 2), 16);
            return result;
        }
    }

    public static class VoiceClientControlPayload
    {
        //* این تابع Payload احراز را با userId در فیلد Alias قفل‌شده قدیمی می‌سازد.
        public static byte[] EncodeAuthRequest(
            VoiceClientPlatform platform,
            string accessToken,
            string roomId,
            string userId,
            string clientInstanceId,
            string clientBuild)
        {
            byte[] token = RequiredUtf8(accessToken, "accessToken", 16384);
            byte[] room = RequiredUtf8(roomId, "roomId", 512);
            byte[] user = RequiredUtf8(userId, "userId", 512);
            byte[] build = OptionalUtf8(clientBuild, 128);
            byte[] result = new byte[26 + token.Length + room.Length + user.Length + build.Length];
            result[0] = (byte)platform;
            result[1] = 0;
            VoiceClientEnvelope.WriteUInt16(result, 2, token.Length);
            VoiceClientEnvelope.WriteUInt16(result, 4, room.Length);
            VoiceClientEnvelope.WriteUInt16(result, 6, user.Length);
            VoiceClientEnvelope.WriteUInt16(result, 8, build.Length);
            VoiceClientEnvelope.WriteUuid(result, 10, clientInstanceId);
            int cursor = 26;
            Copy(token, result, ref cursor);
            Copy(room, result, ref cursor);
            Copy(user, result, ref cursor);
            Copy(build, result, ref cursor);
            return result;
        }

        //* این تابع تنظیمات قطعی Opus آغاز انتشار را می‌سازد.
        public static byte[] EncodePublishStart(int bitrateKbps)
        {
            if (bitrateKbps != 28 && bitrateKbps != 32 && bitrateKbps != 40)
                throw new ArgumentOutOfRangeException("bitrateKbps");

            byte[] payload = new byte[12];
            payload[0] = 1;
            payload[1] = 1;
            payload[2] = 1;
            payload[3] = 3;
            VoiceClientEnvelope.WriteUInt32(payload, 4, 48000);
            VoiceClientEnvelope.WriteUInt16(payload, 8, 20);
            VoiceClientEnvelope.WriteUInt16(payload, 10, bitrateKbps);
            return payload;
        }

        //* این تابع توقف انتشار را با علت مشخص می‌سازد.
        public static byte[] EncodePublishStop(byte reason)
        {
            if (reason < 1 || reason > 4) throw new ArgumentOutOfRangeException("reason");
            return new[] { (byte)1, reason, (byte)0, (byte)0 };
        }

        //* این تابع وضعیت Mute شنونده را همراه connectionId هدف اختیاری می‌سازد.
        public static byte[] EncodeMute(VoiceClientMuteKind kind, bool muted, string targetConnectionId)
        {
            byte[] payload = new byte[20];
            payload[0] = 1;
            payload[1] = (byte)kind;
            payload[2] = muted ? (byte)1 : (byte)0;
            VoiceClientEnvelope.WriteUuid(
                payload,
                4,
                kind == VoiceClientMuteKind.PerUser ? targetConnectionId : VoiceClientEnvelope.EmptyUuid);
            return payload;
        }

        //* این تابع رضایت ضبط را بدون دریافت شناسه هویتی از کلاینت می‌سازد.
        public static byte[] EncodeRecordingConsent(bool consented)
        {
            return new[] { (byte)1, consented ? (byte)1 : (byte)0, (byte)0, (byte)0 };
        }

        //* این تابع ACK چهار بایتی Heartbeat را می‌سازد.
        public static byte[] EncodeHeartbeatAck(uint sequence)
        {
            byte[] payload = new byte[4];
            VoiceClientEnvelope.WriteUInt32(payload, 0, sequence);
            return payload;
        }

        //* این تابع نتیجه Auth سرور را از قرارداد ثابت و دو متن UTF-8 می‌خواند.
        public static VoiceClientAuthResult DecodeAuthResult(byte[] payload)
        {
            if (payload == null || payload.Length < 24)
                throw new InvalidDataException("Voice auth result is too short.");

            int userLength = VoiceClientEnvelope.ReadUInt16(payload, 20);
            int messageLength = VoiceClientEnvelope.ReadUInt16(payload, 22);
            if (payload.Length != 24 + userLength + messageLength)
                throw new InvalidDataException("Voice auth result length is invalid.");

            return new VoiceClientAuthResult
            {
                Success = payload[0] == 1,
                Retryable = payload[1] == 1,
                Code = VoiceClientEnvelope.ReadUInt16(payload, 2),
                VoiceConnectionId = VoiceClientEnvelope.ReadUuid(payload, 4),
                UserId = Encoding.UTF8.GetString(payload, 24, userLength).Trim(),
                Message = Encoding.UTF8.GetString(payload, 24 + userLength, messageLength).Trim()
            };
        }

        //* این تابع Descriptor عضویت Session را با peerUserId قطعی می‌خواند.
        public static VoiceClientSessionDescriptor DecodeSessionDescriptor(byte[] payload)
        {
            if (payload == null || payload.Length < 48)
                throw new InvalidDataException("Voice session descriptor is too short.");

            int legacyAliasLength = VoiceClientEnvelope.ReadUInt16(payload, 46);
            int legacyLength = 48 + legacyAliasLength;
            const int groupExtensionLength = 20;
            bool hasGroupExtension = payload.Length == legacyLength + groupExtensionLength;

            if (payload.Length != legacyLength && !hasGroupExtension)
                throw new InvalidDataException("Voice session descriptor length is invalid.");

            string peerConnectionId = string.Empty;
            if (hasGroupExtension)
            {
                if (payload[legacyLength] != (byte)'G' ||
                    payload[legacyLength + 1] != (byte)'5' ||
                    payload[legacyLength + 2] != 1 ||
                    payload[legacyLength + 3] != 0)
                {
                    throw new InvalidDataException(
                        "Voice group session descriptor extension is invalid.");
                }

                peerConnectionId = VoiceClientEnvelope.ReadUuid(
                    payload,
                    legacyLength + 4);
            }

            uint distanceMillimeters = VoiceClientEnvelope.ReadUInt32(payload, 18);
            return new VoiceClientSessionDescriptor
            {
                SessionId = VoiceClientEnvelope.ReadUuid(payload, 0),
                State = payload[16],
                Reason = payload[17],
                DistanceMeters = distanceMillimeters == uint.MaxValue ? -1f : distanceMillimeters / 1000f,
                EffectiveAtMs = VoiceClientEnvelope.ReadUInt64(payload, 22),
                PeerUserId = VoiceClientEnvelope.ReadUuid(payload, 30),
                PeerConnectionId = peerConnectionId
            };
        }

        //* این تابع نتیجه بازیابی اتصال را از قرارداد ثابت سی‌وشش‌بایتی می‌خواند.
        public static VoiceClientReconnectResult DecodeReconnectResult(byte[] payload)
        {
            if (payload == null || payload.Length < 36)
                throw new InvalidDataException("Voice reconnect result is too short.");

            int messageLength = VoiceClientEnvelope.ReadUInt16(payload, 34);
            if (payload.Length != 36 + messageLength)
                throw new InvalidDataException("Voice reconnect result length is invalid.");

            return new VoiceClientReconnectResult
            {
                Success = payload[0] == 1,
                Retryable = payload[1] == 1,
                Code = VoiceClientEnvelope.ReadUInt16(payload, 2),
                VoiceConnectionId = VoiceClientEnvelope.ReadUuid(payload, 4),
                ResumeFromSequence = VoiceClientEnvelope.ReadUInt32(payload, 20),
                ServerTimeMs = VoiceClientEnvelope.ReadUInt64(payload, 24),
                RetainedSessionCount = VoiceClientEnvelope.ReadUInt16(payload, 32),
                Message = Encoding.UTF8.GetString(payload, 36, messageLength).Trim()
            };
        }

        //* این تابع Snapshot کامل Sessionهای بازیابی‌شده را می‌خواند.
        public static List<VoiceClientSessionDescriptor> DecodeSessionSnapshot(byte[] payload)
        {
            if (payload == null || payload.Length < 4)
                throw new InvalidDataException("Voice session snapshot is too short.");

            int count = VoiceClientEnvelope.ReadUInt16(payload, 0);
            if (VoiceClientEnvelope.ReadUInt16(payload, 2) != 0)
                throw new InvalidDataException("Voice session snapshot reserved field is invalid.");

            int cursor = 4;
            List<VoiceClientSessionDescriptor> result = new List<VoiceClientSessionDescriptor>(count);
            for (int index = 0; index < count; index++)
            {
                if (cursor + 2 > payload.Length)
                    throw new InvalidDataException("Voice session snapshot entry header is truncated.");
                int length = VoiceClientEnvelope.ReadUInt16(payload, cursor);
                cursor += 2;
                if (length <= 0 || cursor + length > payload.Length)
                    throw new InvalidDataException("Voice session snapshot entry is invalid.");

                byte[] descriptorPayload = new byte[length];
                Buffer.BlockCopy(payload, cursor, descriptorPayload, 0, length);
                cursor += length;
                result.Add(DecodeSessionDescriptor(descriptorPayload));
            }

            if (cursor != payload.Length)
                throw new InvalidDataException("Voice session snapshot length is invalid.");
            return result;
        }

        //* این تابع درخواست Snapshot بازیابی را با شناسه اتصال قبلی و Alias برابر userId می‌سازد.
        public static byte[] EncodeReconnectRequest(
            string previousVoiceConnectionId,
            string clientInstanceId,
            uint lastReceivedSequence,
            uint lastPublishedSequence,
            ulong disconnectedAtMs,
            string accessToken,
            string roomId,
            string userId)
        {
            byte[] token = RequiredUtf8(accessToken, "accessToken", 16384);
            byte[] room = RequiredUtf8(roomId, "roomId", 512);
            byte[] user = RequiredUtf8(userId, "userId", 512);
            byte[] payload = new byte[56 + token.Length + room.Length + user.Length];
            VoiceClientEnvelope.WriteUuid(payload, 0, previousVoiceConnectionId);
            VoiceClientEnvelope.WriteUuid(payload, 16, clientInstanceId);
            VoiceClientEnvelope.WriteUInt32(payload, 32, lastReceivedSequence);
            VoiceClientEnvelope.WriteUInt32(payload, 36, lastPublishedSequence);
            VoiceClientEnvelope.WriteUInt64(payload, 40, disconnectedAtMs);
            VoiceClientEnvelope.WriteUInt16(payload, 48, token.Length);
            VoiceClientEnvelope.WriteUInt16(payload, 50, room.Length);
            VoiceClientEnvelope.WriteUInt16(payload, 52, user.Length);
            VoiceClientEnvelope.WriteUInt16(payload, 54, 0);
            int cursor = 56;
            Copy(token, payload, ref cursor);
            Copy(room, payload, ref cursor);
            Copy(user, payload, ref cursor);
            return payload;
        }

        private static byte[] RequiredUtf8(string value, string fieldName, int maximumBytes)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(fieldName + " is required.");
            byte[] bytes = Encoding.UTF8.GetBytes(value.Trim());
            if (bytes.Length > maximumBytes) throw new ArgumentOutOfRangeException(fieldName);
            return bytes;
        }

        private static byte[] OptionalUtf8(string value, int maximumBytes)
        {
            byte[] bytes = Encoding.UTF8.GetBytes((value ?? string.Empty).Trim());
            if (bytes.Length > maximumBytes) throw new ArgumentOutOfRangeException("value");
            return bytes;
        }

        private static void Copy(byte[] source, byte[] target, ref int cursor)
        {
            Buffer.BlockCopy(source, 0, target, cursor, source.Length);
            cursor += source.Length;
        }
    }
}

/*
توضیح فایل:
این فایل قرارداد باینری V1 موردنیاز کلاینت Unity را با Header شصت‌بایتی، Auth، Opus Start/Stop، Mute و رضایت ضبط پیاده می‌کند. فیلد قدیمی قرارداد Auth فقط Alias برابر userId است و هویت مستقل دیگری ایجاد نمی‌کند.
*/
