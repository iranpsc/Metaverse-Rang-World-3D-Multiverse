using System;
using System.IO;
using System.Text;

namespace Network_A.Voice.Client.MediaV2
{
    public static class VoiceMediaV2Constants
    {
        public const int Version = 2;
        public const int HeaderBytes = 88;
        public const int MaximumPacketBytes = 1200;
        public const int MaximumPayloadBytes = 1112;
        public const string EmptyUuid = "00000000-0000-0000-0000-000000000000";
    }

    public enum VoiceMediaV2PacketKind : byte
    {
        BindRequest = 1,
        BindResult = 2,
        Media = 3,
        Ping = 4,
        Pong = 5
    }

    public enum VoiceMediaV2Codec : byte
    {
        None = 0,
        Opus = 1
    }

    [Flags]
    public enum VoiceMediaV2Flags : byte
    {
        None = 0,
        EndOfStream = 1,
        Discontinuity = 2,
        Recovery = 4
    }

    public sealed class VoiceMediaV2Packet
    {
        public VoiceMediaV2PacketKind Kind;
        public VoiceMediaV2Codec Codec;
        public VoiceMediaV2Flags Flags;
        public uint TransportSequence;
        public uint MediaSequence;
        public ulong MediaTimestamp100Ns;
        public uint AckTransportSequence;
        public uint AckMask;
        public string SessionId = VoiceMediaV2Constants.EmptyUuid;
        public string StreamId = VoiceMediaV2Constants.EmptyUuid;
        public string SenderId = VoiceMediaV2Constants.EmptyUuid;
        public uint SecurityContextId;
        public byte[] Payload = Array.Empty<byte>();

        //* این تابع بسته رسانه نسل دوم را با سربرگ ثابت و محدودیت اندازه به آرایه بایت تبدیل می کند.
        public byte[] Encode()
        {
            byte[] payload = Payload ?? Array.Empty<byte>();
            if (payload.Length > VoiceMediaV2Constants.MaximumPayloadBytes) throw new InvalidDataException("Voice media payload exceeds MTU policy.");
            byte[] result = new byte[VoiceMediaV2Constants.HeaderBytes + payload.Length];
            result[0] = (byte)'M'; result[1] = (byte)'V'; result[2] = (byte)'M'; result[3] = (byte)'T';
            result[4] = VoiceMediaV2Constants.Version;
            result[5] = (byte)Kind;
            result[6] = (byte)Codec;
            result[7] = (byte)Flags;
            WriteUInt16(result, 8, VoiceMediaV2Constants.HeaderBytes);
            WriteUInt16(result, 10, payload.Length);
            WriteUInt32(result, 12, TransportSequence);
            WriteUInt32(result, 16, MediaSequence);
            WriteUInt64(result, 20, MediaTimestamp100Ns);
            WriteUInt32(result, 28, AckTransportSequence);
            WriteUInt32(result, 32, AckMask);
            WriteUuid(result, 36, SessionId);
            WriteUuid(result, 52, StreamId);
            WriteUuid(result, 68, SenderId);
            WriteUInt32(result, 84, SecurityContextId);
            if (payload.Length > 0) Buffer.BlockCopy(payload, 0, result, VoiceMediaV2Constants.HeaderBytes, payload.Length);
            return result;
        }

        //* این تابع آرایه بایت را بررسی می کند و یک بسته رسانه نسل دوم معتبر می سازد.
        public static VoiceMediaV2Packet Decode(byte[] input)
        {
            if (input == null || input.Length < VoiceMediaV2Constants.HeaderBytes) throw new InvalidDataException("Voice media packet is too short.");
            if (input[0] != (byte)'M' || input[1] != (byte)'V' || input[2] != (byte)'M' || input[3] != (byte)'T') throw new InvalidDataException("Voice media magic is invalid.");
            if (input[4] != VoiceMediaV2Constants.Version) throw new InvalidDataException("Voice media version is invalid.");
            int headerBytes = ReadUInt16(input, 8);
            int payloadBytes = ReadUInt16(input, 10);
            if (headerBytes != VoiceMediaV2Constants.HeaderBytes || payloadBytes > VoiceMediaV2Constants.MaximumPayloadBytes || input.Length != headerBytes + payloadBytes) throw new InvalidDataException("Voice media packet length is invalid.");
            VoiceMediaV2Packet packet = new VoiceMediaV2Packet
            {
                Kind = (VoiceMediaV2PacketKind)input[5],
                Codec = (VoiceMediaV2Codec)input[6],
                Flags = (VoiceMediaV2Flags)input[7],
                TransportSequence = ReadUInt32(input, 12),
                MediaSequence = ReadUInt32(input, 16),
                MediaTimestamp100Ns = ReadUInt64(input, 20),
                AckTransportSequence = ReadUInt32(input, 28),
                AckMask = ReadUInt32(input, 32),
                SessionId = ReadUuid(input, 36),
                StreamId = ReadUuid(input, 52),
                SenderId = ReadUuid(input, 68),
                SecurityContextId = ReadUInt32(input, 84),
                Payload = new byte[payloadBytes]
            };
            if (!Enum.IsDefined(typeof(VoiceMediaV2PacketKind), packet.Kind) || !Enum.IsDefined(typeof(VoiceMediaV2Codec), packet.Codec)) throw new InvalidDataException("Voice media packet kind or codec is invalid.");
            if (payloadBytes > 0) Buffer.BlockCopy(input, headerBytes, packet.Payload, 0, payloadBytes);
            return packet;
        }

        //* این تابع یک عدد دو بایتی را با ترتیب شبکه در آرایه می نویسد.
        internal static void WriteUInt16(byte[] target, int offset, int value)
        {
            target[offset] = (byte)(value >> 8); target[offset + 1] = (byte)value;
        }

        //* این تابع یک عدد چهار بایتی را با ترتیب شبکه در آرایه می نویسد.
        internal static void WriteUInt32(byte[] target, int offset, uint value)
        {
            target[offset] = (byte)(value >> 24); target[offset + 1] = (byte)(value >> 16); target[offset + 2] = (byte)(value >> 8); target[offset + 3] = (byte)value;
        }

        //* این تابع یک عدد هشت بایتی را با ترتیب شبکه در آرایه می نویسد.
        internal static void WriteUInt64(byte[] target, int offset, ulong value)
        {
            for (int index = 7; index >= 0; index--) { target[offset + index] = (byte)value; value >>= 8; }
        }

        //* این تابع یک عدد دو بایتی را با ترتیب شبکه از آرایه می خواند.
        internal static ushort ReadUInt16(byte[] source, int offset)
        {
            return (ushort)((source[offset] << 8) | source[offset + 1]);
        }

        //* این تابع یک عدد چهار بایتی را با ترتیب شبکه از آرایه می خواند.
        internal static uint ReadUInt32(byte[] source, int offset)
        {
            return ((uint)source[offset] << 24) | ((uint)source[offset + 1] << 16) | ((uint)source[offset + 2] << 8) | source[offset + 3];
        }

        //* این تابع یک عدد هشت بایتی را با ترتیب شبکه از آرایه می خواند.
        internal static ulong ReadUInt64(byte[] source, int offset)
        {
            ulong value = 0; for (int index = 0; index < 8; index++) value = (value << 8) | source[offset + index]; return value;
        }

        //* این تابع شناسه یکتا را به شانزده بایت با ترتیب نوشتاری استاندارد تبدیل می کند.
        internal static void WriteUuid(byte[] target, int offset, string value)
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? VoiceMediaV2Constants.EmptyUuid : value.Trim().ToLowerInvariant();
            if (!Guid.TryParse(normalized, out _)) throw new InvalidDataException("Voice media UUID is invalid.");
            string hex = normalized.Replace("-", string.Empty);
            for (int index = 0; index < 16; index++) target[offset + index] = Convert.ToByte(hex.Substring(index * 2, 2), 16);
        }

        //* این تابع شانزده بایت شناسه یکتا را به متن استاندارد تبدیل می کند.
        internal static string ReadUuid(byte[] source, int offset)
        {
            byte[] bytes = new byte[16]; Buffer.BlockCopy(source, offset, bytes, 0, 16);
            string hex = BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            return hex.Substring(0, 8) + "-" + hex.Substring(8, 4) + "-" + hex.Substring(12, 4) + "-" + hex.Substring(16, 4) + "-" + hex.Substring(20, 12);
        }
    }

    public static class VoiceMediaV2BindPayload
    {
        //* این تابع درخواست اتصال مسیر رسانه را با توکن و شناسه اتصال مسیر کنترل می سازد.
        public static byte[] EncodeRequest(string accessToken, string roomId, string controlConnectionId, byte platform, string clientBuild)
        {
            byte[] token = Encoding.UTF8.GetBytes((accessToken ?? string.Empty).Trim());
            byte[] room = Encoding.UTF8.GetBytes((roomId ?? string.Empty).Trim());
            byte[] build = Encoding.UTF8.GetBytes((clientBuild ?? string.Empty).Trim());
            if (token.Length == 0 || token.Length > 16384 || room.Length == 0 || room.Length > 512 || build.Length > 128) throw new InvalidDataException("Voice media bind request field length is invalid.");
            byte[] result = new byte[26 + token.Length + room.Length + build.Length];
            result[0] = 1; result[1] = platform;
            VoiceMediaV2Packet.WriteUInt16(result, 2, token.Length); VoiceMediaV2Packet.WriteUInt16(result, 4, room.Length); VoiceMediaV2Packet.WriteUInt16(result, 6, build.Length); VoiceMediaV2Packet.WriteUInt16(result, 8, 0);
            VoiceMediaV2Packet.WriteUuid(result, 10, controlConnectionId);
            int cursor = 26; Buffer.BlockCopy(token, 0, result, cursor, token.Length); cursor += token.Length; Buffer.BlockCopy(room, 0, result, cursor, room.Length); cursor += room.Length; Buffer.BlockCopy(build, 0, result, cursor, build.Length);
            return result;
        }

        //* این تابع نتیجه اتصال مسیر رسانه را از داده باینری می خواند.
        public static VoiceMediaV2BindResult DecodeResult(byte[] payload)
        {
            if (payload == null || payload.Length < 22) throw new InvalidDataException("Voice media bind result is invalid.");
            int messageLength = VoiceMediaV2Packet.ReadUInt16(payload, 20);
            if (payload.Length != 22 + messageLength) throw new InvalidDataException("Voice media bind result length is invalid.");
            return new VoiceMediaV2BindResult
            {
                Success = payload[0] == 1,
                Retryable = payload[1] == 1,
                Code = VoiceMediaV2Packet.ReadUInt16(payload, 2),
                ControlConnectionId = VoiceMediaV2Packet.ReadUuid(payload, 4),
                Message = Encoding.UTF8.GetString(payload, 22, messageLength).Trim()
            };
        }
    }

    public sealed class VoiceMediaV2BindResult
    {
        public bool Success;
        public bool Retryable;
        public ushort Code;
        public string ControlConnectionId = string.Empty;
        public string Message = string.Empty;
    }
}
