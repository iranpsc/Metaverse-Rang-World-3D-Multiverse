using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Network_A.Auth
{
    public static class AuthProtoMapper
    {
        //* Builds Register/Login protobuf request bytes.
        public static byte[] EncodeLoginLikeRequest(string emailOrUsername, string password)
        {
            var w = new ProtoWriter();
            w.WriteString(1, emailOrUsername ?? string.Empty);
            w.WriteString(2, password ?? string.Empty);
            return w.ToArray();
        }

        //* Builds Refresh protobuf request bytes.
        public static byte[] EncodeRefreshRequest(string refreshToken)
        {
            var w = new ProtoWriter();
            w.WriteString(ServerConfig.RefreshRequestTokenFieldNumber, refreshToken ?? string.Empty);
            return w.ToArray();
        }

        //* Builds an empty protobuf request.
        public static byte[] EncodeEmptyRequest()
        {
            return new byte[0];
        }

        //* Wraps protobuf bytes inside a gRPC-Web unary data frame.
        public static byte[] EncodeGrpcWebUnaryRequest(byte[] message)
        {
            message = message ?? new byte[0];
            int len = message.Length;
            byte[] buf = new byte[5 + len];
            buf[0] = 0x00;
            buf[1] = (byte)((len >> 24) & 0xFF);
            buf[2] = (byte)((len >> 16) & 0xFF);
            buf[3] = (byte)((len >> 8) & 0xFF);
            buf[4] = (byte)(len & 0xFF);
            Buffer.BlockCopy(message, 0, buf, 5, len);
            return buf;
        }

        //* Extracts the protobuf data message from a gRPC-Web unary response.
        public static bool TryDecodeGrpcWebUnaryResponse(byte[] responseBytes, out byte[] messageOut, out Dictionary<string, string> trailersOut)
        {
            messageOut = null;
            trailersOut = new Dictionary<string, string>();

            if (responseBytes == null || responseBytes.Length < 5) return false;

            int i = 0;
            while (i + 5 <= responseBytes.Length)
            {
                byte flag = responseBytes[i];
                int len = (responseBytes[i + 1] << 24) | (responseBytes[i + 2] << 16) | (responseBytes[i + 3] << 8) | responseBytes[i + 4];
                i += 5;

                if (len < 0 || i + len > responseBytes.Length) return false;

                if ((flag & 0x80) == 0)
                {
                    if (messageOut == null)
                    {
                        messageOut = new byte[len];
                        Buffer.BlockCopy(responseBytes, i, messageOut, 0, len);
                    }
                }
                else
                {
                    string txt = Encoding.ASCII.GetString(responseBytes, i, len);
                    string[] lines = txt.Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                    for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                    {
                        string line = lines[lineIndex];
                        int idx = line.IndexOf(':');
                        if (idx <= 0) continue;
                        string key = line.Substring(0, idx).Trim().ToLowerInvariant();
                        string value = line.Substring(idx + 1).Trim();
                        trailersOut[key] = value;
                    }
                }

                i += len;
            }

            return messageOut != null;
        }

        //* Decodes Register/Login/Refresh protobuf response bytes to clean DTO.
        public static AuthResponseDto DecodeAuthResponse(byte[] bytes)
        {
            var dto = new AuthResponseDto
            {
                success = false,
                message = string.Empty,
                accessToken = string.Empty,
                refreshToken = string.Empty,
                expiresIn = 0,
                user = null
            };

            if (bytes == null || bytes.Length == 0) return dto;

            var r = new ProtoReader(bytes);
            while (r.TryReadTag(out int field, out int wire))
            {
                switch (field)
                {
                    case 1:
                        dto.success = r.ReadBool();
                        break;

                    case 2:
                        dto.message = r.ReadString();
                        break;

                    case 3:
                        dto.accessToken = r.ReadString();
                        break;

                    case 4:
                        dto.refreshToken = r.ReadString();
                        break;

                    case 5:
                        if (wire == 0) dto.expiresIn = r.ReadInt32();
                        else r.Skip(wire);
                        break;

                    case 6:
                        dto.user = DecodeUser(r.ReadBytes());
                        break;

                    default:
                        r.Skip(wire);
                        break;
                }
            }

            return dto;
        }

        //* Decodes GetUserDataReply protobuf bytes to clean DTO.
        public static GetUserDataResponseDto DecodeGetUserDataResponse(byte[] bytes)
        {
            var dto = new GetUserDataResponseDto { success = false, message = string.Empty, user = null };
            if (bytes == null || bytes.Length == 0) return dto;

            var r = new ProtoReader(bytes);
            while (r.TryReadTag(out int field, out int wire))
            {
                switch (field)
                {
                    case 1: dto.success = r.ReadBool(); break;
                    case 2: dto.message = r.ReadString(); break;
                    case 3: dto.user = DecodeUser(r.ReadBytes()); break;
                    default: r.Skip(wire); break;
                }
            }

            return dto;
        }


        public static GetMicroserviceUserDataResponseDto DecodeGetMicroserviceUserDataResponse(byte[] bytes)
        {
            var dto = new GetMicroserviceUserDataResponseDto
            {
                success = false,
                message = string.Empty,
                profile = null
            };

            if (bytes == null || bytes.Length == 0) return dto;

            var r = new ProtoReader(bytes);
            while (r.TryReadTag(out int field, out int wire))
            {
                switch (field)
                {
                    case 1:
                        dto.success = r.ReadBool();
                        break;

                    case 2:
                        dto.message = r.ReadString();
                        break;

                    case 3:
                        dto.profile = DecodeMicroserviceUserData(r.ReadBytes());
                        break;

                    default:
                        r.Skip(wire);
                        break;
                }
            }

            return dto;
        }

        static MicroserviceUserDataDto DecodeMicroserviceUserData(byte[] bytes)
        {
            var dto = new MicroserviceUserDataDto
            {
                microserviceId = string.Empty,
                name = string.Empty,
                code = string.Empty,
                avatar = string.Empty,
                microserviceUserName = string.Empty,
                lastSyncAtUnix = 0
            };

            if (bytes == null || bytes.Length == 0) return dto;

            var r = new ProtoReader(bytes);
            while (r.TryReadTag(out int field, out int wire))
            {
                switch (field)
                {
                    case 1:
                        dto.microserviceId = r.ReadString();
                        break;

                    case 2:
                        dto.name = r.ReadString();
                        break;

                    case 3:
                        dto.code = r.ReadString();
                        break;

                    case 4:
                        dto.avatar = r.ReadString();
                        break;

                    case 5:
                        dto.microserviceUserName = r.ReadString();
                        break;

                    case 6:
                        if (wire == 0) dto.lastSyncAtUnix = r.ReadInt64();
                        else r.Skip(wire);
                        break;

                    default:
                        r.Skip(wire);
                        break;
                }
            }

            return dto;
        }


        //* Decodes User protobuf bytes.
        static AuthUserDto DecodeUser(byte[] bytes)
        {
            var dto = new AuthUserDto { id = string.Empty, emailOrUsername = string.Empty, createdAtUnix = 0 };
            if (bytes == null || bytes.Length == 0) return dto;

            var r = new ProtoReader(bytes);
            while (r.TryReadTag(out int field, out int wire))
            {
                switch (field)
                {
                    case 1:
                        dto.id = r.ReadString();
                        break;
                    case 2:
                        dto.emailOrUsername = r.ReadString();
                        break;
                    case 3:
                        if (wire == 0) dto.createdAtUnix = r.ReadInt64();
                        else if (wire == 2)
                        {
                            string s = r.ReadString();
                            long v;
                            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) dto.createdAtUnix = v;
                        }
                        else r.Skip(wire);
                        break;
                    default:
                        r.Skip(wire);
                        break;
                }
            }

            return dto;
        }

        sealed class ProtoWriter
        {
            readonly List<byte> _buf = new List<byte>(128);

            //* Writes a protobuf string field.
            public void WriteString(int fieldNumber, string value)
            {
                value = value ?? string.Empty;
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                WriteTag(fieldNumber, 2);
                WriteVarint((uint)bytes.Length);
                _buf.AddRange(bytes);
            }

            //* Converts writer buffer to bytes.
            public byte[] ToArray()
            {
                return _buf.ToArray();
            }

            //* Writes protobuf field tag.
            void WriteTag(int fieldNumber, int wireType)
            {
                WriteVarint((uint)((fieldNumber << 3) | wireType));
            }

            //* Writes protobuf varint.
            void WriteVarint(uint value)
            {
                while (value > 127)
                {
                    _buf.Add((byte)((value & 0x7F) | 0x80));
                    value >>= 7;
                }
                _buf.Add((byte)value);
            }
        }

        //* Builds Logout protobuf request bytes.
        public static byte[] EncodeLogoutRequest(string refreshToken)
        {
            var w = new ProtoWriter();
            w.WriteString(ServerConfig.LogoutRequestRefreshTokenFieldNumber, refreshToken ?? string.Empty);
            return w.ToArray();
        }

        sealed class ProtoReader
        {
            readonly byte[] _data;
            int _i;

            public ProtoReader(byte[] data)
            {
                _data = data ?? new byte[0];
                _i = 0;
            }

            //* Reads the next protobuf field tag.
            public bool TryReadTag(out int fieldNumber, out int wireType)
            {
                fieldNumber = 0;
                wireType = 0;
                if (_i >= _data.Length) return false;
                uint tag = ReadVarint();
                if (tag == 0) return false;
                wireType = (int)(tag & 0x07);
                fieldNumber = (int)(tag >> 3);
                return true;
            }

            //* Reads a protobuf bool value.
            public bool ReadBool()
            {
                return ReadVarint() != 0;
            }

            //* Reads a protobuf int32 value.
            public int ReadInt32()
            {
                unchecked { return (int)ReadVarint(); }
            }

            //* Reads a protobuf int64 value.
            public long ReadInt64()
            {
                unchecked { return (long)ReadVarint64(); }
            }

            //* Reads a protobuf string value.
            public string ReadString()
            {
                int len = (int)ReadVarint();
                if (len <= 0) return string.Empty;
                if (_i + len > _data.Length) len = Math.Max(0, _data.Length - _i);
                string s = Encoding.UTF8.GetString(_data, _i, len);
                _i += len;
                return s;
            }

            //* Reads protobuf length-delimited bytes.
            public byte[] ReadBytes()
            {
                int len = (int)ReadVarint();
                if (len <= 0) return new byte[0];
                if (_i + len > _data.Length) len = Math.Max(0, _data.Length - _i);
                byte[] output = new byte[len];
                Buffer.BlockCopy(_data, _i, output, 0, len);
                _i += len;
                return output;
            }

            //* Skips unsupported protobuf fields.
            public void Skip(int wireType)
            {
                switch (wireType)
                {
                    case 0:
                        ReadVarint();
                        return;
                    case 2:
                        int len = (int)ReadVarint();
                        _i = Math.Min(_data.Length, _i + Math.Max(0, len));
                        return;
                    default:
                        _i = _data.Length;
                        return;
                }
            }

            //* Reads protobuf uint32 varint.
            uint ReadVarint()
            {
                uint result = 0;
                int shift = 0;
                while (_i < _data.Length)
                {
                    byte b = _data[_i++];
                    result |= (uint)(b & 0x7F) << shift;
                    if ((b & 0x80) == 0) break;
                    shift += 7;
                }
                return result;
            }

            //* Reads protobuf uint64 varint.
            ulong ReadVarint64()
            {
                ulong result = 0;
                int shift = 0;
                while (_i < _data.Length)
                {
                    byte b = _data[_i++];
                    result |= (ulong)(b & 0x7F) << shift;
                    if ((b & 0x80) == 0) break;
                    shift += 7;
                    if (shift >= 64) break;
                }
                return result;
            }
        }
    }
}
