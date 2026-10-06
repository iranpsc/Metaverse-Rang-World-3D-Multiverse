#if !UNITY_WEBGL || UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Network_A.Voice.Client.MediaV2.Security
{
    public readonly struct VoiceMediaSecurityHandshakeData
    {
        public readonly uint SecurityContextId;
        public readonly byte[] Nonce;

        public VoiceMediaSecurityHandshakeData(uint securityContextId, byte[] nonce)
        {
            SecurityContextId = securityContextId;
            Nonce = nonce;
        }
    }

    public readonly struct VoiceMediaBindSecurityIdentity
    {
        public readonly string AccessToken;
        public readonly string RoomId;
        public readonly string ControlConnectionId;
        public readonly byte Platform;
        public readonly string ClientBuild;

        public VoiceMediaBindSecurityIdentity(string accessToken, string roomId, string controlConnectionId, byte platform, string clientBuild)
        {
            AccessToken = accessToken;
            RoomId = roomId;
            ControlConnectionId = controlConnectionId;
            Platform = platform;
            ClientBuild = clientBuild;
        }
    }

    public sealed class VoiceMediaSecurityKeys : IDisposable
    {
        public byte[] ClientToServerKey { get; private set; }
        public byte[] ServerToClientKey { get; private set; }
        public byte[] ClientToServerEncryptionKey { get; private set; }
        public byte[] ServerToClientEncryptionKey { get; private set; }

        public VoiceMediaSecurityKeys(byte[] clientToServerKey, byte[] serverToClientKey, byte[] clientToServerEncryptionKey, byte[] serverToClientEncryptionKey)
        {
            ClientToServerKey = clientToServerKey ?? throw new ArgumentNullException(nameof(clientToServerKey));
            ServerToClientKey = serverToClientKey ?? throw new ArgumentNullException(nameof(serverToClientKey));
            ClientToServerEncryptionKey = clientToServerEncryptionKey ?? throw new ArgumentNullException(nameof(clientToServerEncryptionKey));
            ServerToClientEncryptionKey = serverToClientEncryptionKey ?? throw new ArgumentNullException(nameof(serverToClientEncryptionKey));
        }

        public void Dispose()
        {
            if (ClientToServerKey != null) Array.Clear(ClientToServerKey, 0, ClientToServerKey.Length);
            if (ServerToClientKey != null) Array.Clear(ServerToClientKey, 0, ServerToClientKey.Length);
            if (ClientToServerEncryptionKey != null) Array.Clear(ClientToServerEncryptionKey, 0, ClientToServerEncryptionKey.Length);
            if (ServerToClientEncryptionKey != null) Array.Clear(ServerToClientEncryptionKey, 0, ServerToClientEncryptionKey.Length);
            ClientToServerKey = null;
            ServerToClientKey = null;
            ClientToServerEncryptionKey = null;
            ServerToClientEncryptionKey = null;
        }
    }

    public static class VoiceMediaSecurityHandshake
    {
        public const int NonceBytes = 32;
        public const string Capability = "vme2s1";
        private const string Prefix = "media_bind_ok";
        private const string Version = "vme2s1";
        private static readonly byte[] InfoPrefix = Encoding.ASCII.GetBytes("VME2-MEDIA-INTEGRITY-V1");

        public static string Encode(uint securityContextId, byte[] nonce)
        {
            ValidateContextId(securityContextId);
            ValidateNonce(nonce);
            return Prefix + "|" + Version + "|" + securityContextId.ToString("x8", CultureInfo.InvariantCulture) + "|" + ToBase64Url(nonce);
        }

        public static VoiceMediaSecurityHandshakeData Decode(string message)
        {
            string[] parts = (message ?? string.Empty).Trim().Split('|');
            if (parts.Length != 4 || parts[0] != Prefix || parts[1] != Version || parts[2].Length != 8) throw new InvalidDataException("Media security handshake is invalid.");
            if (!uint.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint contextId) || contextId == 0) throw new InvalidDataException("Media security context is invalid.");
            byte[] nonce = FromBase64Url(parts[3]);
            ValidateNonce(nonce);
            return new VoiceMediaSecurityHandshakeData(contextId, nonce);
        }

        public static VoiceMediaSecurityKeys DeriveKeys(string accessToken, string roomId, string controlConnectionId, string streamId, uint securityContextId, byte[] nonce)
        {
            ValidateContextId(securityContextId);
            ValidateNonce(nonce);
            byte[] token = RequiredUtf8(accessToken, 16384, nameof(accessToken));
            byte[] room = RequiredUtf8(roomId, 512, nameof(roomId));
            byte[] control = RequiredUtf8((controlConnectionId ?? string.Empty).Trim().ToLowerInvariant(), 64, nameof(controlConnectionId));
            byte[] stream = RequiredUtf8((streamId ?? string.Empty).Trim().ToLowerInvariant(), 64, nameof(streamId));
            byte[] prk;
            using (HMACSHA256 extract = new HMACSHA256(nonce)) prk = extract.ComputeHash(token);
            byte[] clientToServer = Expand(prk, BuildInfo(securityContextId, room, control, stream, Encoding.ASCII.GetBytes("c2s")));
            byte[] serverToClient = Expand(prk, BuildInfo(securityContextId, room, control, stream, Encoding.ASCII.GetBytes("s2c")));
            byte[] clientToServerEncryption = Expand(prk, BuildInfo(securityContextId, room, control, stream, Encoding.ASCII.GetBytes("c2e")));
            byte[] serverToClientEncryption = Expand(prk, BuildInfo(securityContextId, room, control, stream, Encoding.ASCII.GetBytes("s2e")));
            Array.Clear(token, 0, token.Length);
            Array.Clear(prk, 0, prk.Length);
            return new VoiceMediaSecurityKeys(clientToServer, serverToClient, clientToServerEncryption, serverToClientEncryption);
        }

        public static VoiceMediaBindSecurityIdentity DecodeBindIdentity(byte[] payload)
        {
            if (payload == null || payload.Length < 26 || payload[0] != 1) throw new InvalidDataException("Media bind request is invalid.");
            int tokenLength = VoiceMediaV2Packet.ReadUInt16(payload, 2);
            int roomLength = VoiceMediaV2Packet.ReadUInt16(payload, 4);
            int buildLength = VoiceMediaV2Packet.ReadUInt16(payload, 6);
            if (VoiceMediaV2Packet.ReadUInt16(payload, 8) != 0 || payload.Length != 26 + tokenLength + roomLength + buildLength) throw new InvalidDataException("Media bind request length is invalid.");
            int roomOffset = 26 + tokenLength;
            int buildOffset = roomOffset + roomLength;
            string accessToken = Encoding.UTF8.GetString(payload, 26, tokenLength).Trim();
            string roomId = Encoding.UTF8.GetString(payload, roomOffset, roomLength).Trim();
            string clientBuild = Encoding.UTF8.GetString(payload, buildOffset, buildLength).Trim();
            string controlConnectionId = VoiceMediaV2Packet.ReadUuid(payload, 10).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(roomId) || !Guid.TryParse(controlConnectionId, out _)) throw new InvalidDataException("Media bind identity is invalid.");
            return new VoiceMediaBindSecurityIdentity(accessToken, roomId, controlConnectionId, payload[1], clientBuild);
        }

        public static string AddCapability(string clientBuild)
        {
            string normalized = (clientBuild ?? string.Empty).Trim();
            string[] values = normalized.Split(';');
            for (int index = 0; index < values.Length; index++) if (string.Equals(values[index].Trim(), Capability, StringComparison.OrdinalIgnoreCase)) return normalized;
            string result = normalized.Length == 0 ? Capability : normalized + ";" + Capability;
            if (Encoding.UTF8.GetByteCount(result) > 128) throw new InvalidDataException("Media client build cannot carry the security capability.");
            return result;
        }

        private static byte[] BuildInfo(uint contextId, byte[] room, byte[] control, byte[] stream, byte[] direction)
        {
            using (MemoryStream output = new MemoryStream())
            {
                output.Write(InfoPrefix, 0, InfoPrefix.Length);
                byte[] context = new byte[4];
                VoiceMediaV2Packet.WriteUInt32(context, 0, contextId);
                output.Write(context, 0, context.Length);
                WriteLengthPrefixed(output, room);
                WriteLengthPrefixed(output, control);
                WriteLengthPrefixed(output, stream);
                WriteLengthPrefixed(output, direction);
                return output.ToArray();
            }
        }

        private static void WriteLengthPrefixed(Stream output, byte[] value)
        {
            if (value == null || value.Length > ushort.MaxValue) throw new InvalidDataException("Security context text is invalid.");
            byte[] length = new byte[2];
            VoiceMediaV2Packet.WriteUInt16(length, 0, value.Length);
            output.Write(length, 0, length.Length);
            output.Write(value, 0, value.Length);
        }

        private static byte[] Expand(byte[] prk, byte[] info)
        {
            byte[] input = new byte[info.Length + 1];
            Buffer.BlockCopy(info, 0, input, 0, info.Length);
            input[input.Length - 1] = 1;
            byte[] result;
            using (HMACSHA256 expand = new HMACSHA256(prk)) result = expand.ComputeHash(input);
            Array.Clear(input, 0, input.Length);
            return result;
        }

        private static byte[] RequiredUtf8(string value, int maximumBytes, string fieldName)
        {
            string normalized = (value ?? string.Empty).Trim();
            byte[] bytes = Encoding.UTF8.GetBytes(normalized);
            if (string.IsNullOrWhiteSpace(normalized) || bytes.Length > maximumBytes) throw new InvalidDataException(fieldName + " is invalid.");
            return bytes;
        }

        private static string ToBase64Url(byte[] value)
        {
            return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static byte[] FromBase64Url(string value)
        {
            string normalized = (value ?? string.Empty).Replace('-', '+').Replace('_', '/');
            int padding = (4 - normalized.Length % 4) % 4;
            if (padding > 0) normalized = normalized.PadRight(normalized.Length + padding, '=');
            try { return Convert.FromBase64String(normalized); }
            catch (FormatException exception) { throw new InvalidDataException("Media security nonce is invalid.", exception); }
        }

        private static void ValidateContextId(uint value)
        {
            if (value == 0) throw new InvalidDataException("Media security context is invalid.");
        }

        private static void ValidateNonce(byte[] value)
        {
            if (value == null || value.Length != NonceBytes) throw new InvalidDataException("Media security nonce is invalid.");
        }
    }
}
#endif
