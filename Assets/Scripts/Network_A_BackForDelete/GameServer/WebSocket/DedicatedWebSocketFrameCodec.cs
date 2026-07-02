using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Network_A.GameServer.WebSocket
{
    public enum DedicatedWebSocketOpcode
    {
        Continuation = 0,
        Text = 1,
        Binary = 2,
        Close = 8,
        Ping = 9,
        Pong = 10
    }

    public class DedicatedWebSocketFrame
    {
        public bool fin;
        public DedicatedWebSocketOpcode opcode;
        public byte[] payload;

        //* این تابع مشخص می کند که فریم دریافت شده پیام متنی است یا نه.
        public bool IsText()
        {
            return opcode == DedicatedWebSocketOpcode.Text;
        }

        //* این تابع مشخص می کند که فریم دریافت شده پیام کلوز است یا نه.
        public bool IsClose()
        {
            return opcode == DedicatedWebSocketOpcode.Close;
        }

        //* این تابع مشخص می کند که فریم دریافت شده پیام پینگ است یا نه.
        public bool IsPing()
        {
            return opcode == DedicatedWebSocketOpcode.Ping;
        }

        //* این تابع مشخص می کند که فریم دریافت شده پیام پانگ است یا نه.
        public bool IsPong()
        {
            return opcode == DedicatedWebSocketOpcode.Pong;
        }

        //* این تابع پِیلود فریم را به متن یو تی اف هشت تبدیل می کند.
        public string ReadText()
        {
            if (payload == null || payload.Length == 0) return string.Empty;
            return Encoding.UTF8.GetString(payload);
        }
    }

    public static class DedicatedWebSocketFrameCodec
    {
        private const ulong MaxPayloadBytes = 262144;

        //* این تابع یک فریم کامل وب سوکت را از استریم می خواند.
        public static async Task<DedicatedWebSocketFrame> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] header = await ReadExactAsync(stream, 2, cancellationToken);

            byte firstByte = header[0];
            byte secondByte = header[1];

            bool fin = (firstByte & 0x80) != 0;
            int opcodeValue = firstByte & 0x0F;
            bool masked = (secondByte & 0x80) != 0;
            ulong payloadLength = (ulong)(secondByte & 0x7F);

            if (!fin)
            {
                throw new InvalidOperationException("Fragmented websocket frames are not supported yet.");
            }

            if (payloadLength == 126)
            {
                byte[] extended = await ReadExactAsync(stream, 2, cancellationToken);
                payloadLength = ((ulong)extended[0] << 8) | extended[1];
            }
            else if (payloadLength == 127)
            {
                byte[] extended = await ReadExactAsync(stream, 8, cancellationToken);
                payloadLength =
                    ((ulong)extended[0] << 56) |
                    ((ulong)extended[1] << 48) |
                    ((ulong)extended[2] << 40) |
                    ((ulong)extended[3] << 32) |
                    ((ulong)extended[4] << 24) |
                    ((ulong)extended[5] << 16) |
                    ((ulong)extended[6] << 8) |
                    extended[7];
            }

            if (payloadLength > MaxPayloadBytes)
            {
                throw new InvalidOperationException("Websocket payload is too large.");
            }

            byte[] maskKey = null;

            if (masked)
            {
                maskKey = await ReadExactAsync(stream, 4, cancellationToken);
            }

            byte[] payload = payloadLength > 0
                ? await ReadExactAsync(stream, (int)payloadLength, cancellationToken)
                : new byte[0];

            if (masked && maskKey != null)
            {
                for (int i = 0; i < payload.Length; i++)
                {
                    payload[i] = (byte)(payload[i] ^ maskKey[i % 4]);
                }
            }

            return new DedicatedWebSocketFrame
            {
                fin = fin,
                opcode = (DedicatedWebSocketOpcode)opcodeValue,
                payload = payload
            };
        }

        //* این تابع یک پیام متنی را به شکل فریم وب سوکت برای ارسال از سرور می سازد.
        public static byte[] BuildTextFrame(string text)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text ?? string.Empty);
            return BuildFrame(DedicatedWebSocketOpcode.Text, payload);
        }

        //* این تابع یک فریم پانگ برای پاسخ به پینگ کلاینت می سازد.
        public static byte[] BuildPongFrame(byte[] payload)
        {
            return BuildFrame(DedicatedWebSocketOpcode.Pong, payload ?? new byte[0]);
        }

        //* این تابع یک فریم کلوز ساده برای بستن کانکشن می سازد.
        public static byte[] BuildCloseFrame()
        {
            return BuildFrame(DedicatedWebSocketOpcode.Close, new byte[0]);
        }

        //* این تابع فریم خروجی سرور را بدون ماسک و طبق قرارداد وب سوکت می سازد.
        private static byte[] BuildFrame(DedicatedWebSocketOpcode opcode, byte[] payload)
        {
            payload = payload ?? new byte[0];

            using (MemoryStream memory = new MemoryStream())
            {
                byte firstByte = (byte)(0x80 | ((byte)opcode & 0x0F));
                memory.WriteByte(firstByte);

                if (payload.Length <= 125)
                {
                    memory.WriteByte((byte)payload.Length);
                }
                else if (payload.Length <= ushort.MaxValue)
                {
                    memory.WriteByte(126);
                    memory.WriteByte((byte)((payload.Length >> 8) & 0xFF));
                    memory.WriteByte((byte)(payload.Length & 0xFF));
                }
                else
                {
                    memory.WriteByte(127);

                    ulong length = (ulong)payload.Length;

                    memory.WriteByte((byte)((length >> 56) & 0xFF));
                    memory.WriteByte((byte)((length >> 48) & 0xFF));
                    memory.WriteByte((byte)((length >> 40) & 0xFF));
                    memory.WriteByte((byte)((length >> 32) & 0xFF));
                    memory.WriteByte((byte)((length >> 24) & 0xFF));
                    memory.WriteByte((byte)((length >> 16) & 0xFF));
                    memory.WriteByte((byte)((length >> 8) & 0xFF));
                    memory.WriteByte((byte)(length & 0xFF));
                }

                if (payload.Length > 0)
                {
                    memory.Write(payload, 0, payload.Length);
                }

                return memory.ToArray();
            }
        }

        //* این تابع تعداد مشخصی بایت را از استریم می خواند و اگر ناقص باشد خطا می دهد.
        private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[count];
            int offset = 0;

            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset, cancellationToken);

                if (read <= 0)
                {
                    throw new IOException("Websocket stream closed while reading.");
                }

                offset += read;
            }

            return buffer;
        }

        /*
        توضیح مکتوب فایل:
        این فایل کدک ساده فریم های وب سوکت است.
        پیام های کلاینت را از حالت ماسک شده می خواند و پیام های سرور را بدون ماسک می فرستد.
        فعلاً فقط پیام متنی، پینگ، پانگ و کلوز پشتیبانی می شود.
        فریم های چند تکه هنوز پشتیبانی نمی شوند چون برای فاز اول نیاز نداریم.
        */
    }
}
