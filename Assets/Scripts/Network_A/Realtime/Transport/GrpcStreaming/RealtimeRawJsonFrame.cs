using System;
using System.IO;
using System.Text;

namespace Network_A.Realtime.Transport
{
    //* مدل سبک پیام خام ریل‌تایم است و فقط فیلد rawJson قرارداد جی‌آر‌پی‌سی را نگه می‌دارد.
    public sealed class RealtimeRawJsonFrame
    {
        public string RawJson { get; set; }

        //* یک فریم خالی برای دی‌سریالایز و مقداردهی پیش‌فرض می‌سازد.
        public RealtimeRawJsonFrame()
        {
            RawJson = string.Empty;
        }

        //* یک فریم با متن جیسون خام می‌سازد.
        public RealtimeRawJsonFrame(string rawJson)
        {
            RawJson = rawJson ?? string.Empty;
        }

        //* بررسی می‌کند که فریم پیام قابل ارسال یا دریافت دارد یا نه.
        public bool HasPayload()
        {
            return !string.IsNullOrWhiteSpace(RawJson);
        }

        //* متن جیسون خام را به فریم قابل ارسال تبدیل می‌کند.
        public static RealtimeRawJsonFrame FromRawJson(string rawJson)
        {
            return new RealtimeRawJsonFrame(rawJson);
        }

        //* فریم را با قرارداد پروتوباف message RealtimeRawJson { string rawJson = 1; } به بایت تبدیل می‌کند.
        public static byte[] ToProtoBytes(RealtimeRawJsonFrame frame)
        {
            string rawJson = frame == null ? string.Empty : frame.RawJson ?? string.Empty;
            byte[] valueBytes = Encoding.UTF8.GetBytes(rawJson);

            using (MemoryStream stream = new MemoryStream())
            {
                WriteLengthDelimitedStringField(stream, 1, valueBytes);
                return stream.ToArray();
            }
        }

        //* بایت پروتوباف را از قرارداد RealtimeRawJson به فریم خام تبدیل می‌کند.
        public static RealtimeRawJsonFrame FromProtoBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return new RealtimeRawJsonFrame(string.Empty);

            int index = 0;
            string rawJson = string.Empty;

            while (index < bytes.Length)
            {
                ulong tag;
                if (!TryReadVarint(bytes, ref index, out tag)) break;

                int fieldNumber = (int)(tag >> 3);
                int wireType = (int)(tag & 7);

                if (fieldNumber == 1 && wireType == 2)
                {
                    rawJson = ReadLengthDelimitedString(bytes, ref index);
                    continue;
                }

                if (!SkipUnknownField(bytes, ref index, wireType)) break;
            }

            return new RealtimeRawJsonFrame(rawJson);
        }

        //* فیلد رشته‌ای پروتوباف را به صورت length-delimited داخل استریم می‌نویسد.
        private static void WriteLengthDelimitedStringField(Stream stream, int fieldNumber, byte[] valueBytes)
        {
            ulong tag = ((ulong)fieldNumber << 3) | 2UL;
            WriteVarint(stream, tag);
            WriteVarint(stream, (ulong)(valueBytes == null ? 0 : valueBytes.Length));
            if (valueBytes != null && valueBytes.Length > 0) stream.Write(valueBytes, 0, valueBytes.Length);
        }

        //* مقدار عددی را با فرمت varint پروتوباف داخل استریم می‌نویسد.
        private static void WriteVarint(Stream stream, ulong value)
        {
            while (value >= 0x80)
            {
                stream.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            stream.WriteByte((byte)value);
        }

        //* مقدار varint را از آرایه بایت می‌خواند و ایندکس را جلو می‌برد.
        private static bool TryReadVarint(byte[] bytes, ref int index, out ulong value)
        {
            value = 0UL;
            int shift = 0;

            while (index < bytes.Length && shift <= 63)
            {
                byte current = bytes[index++];
                value |= (ulong)(current & 0x7F) << shift;
                if ((current & 0x80) == 0) return true;
                shift += 7;
            }

            return false;
        }

        //* رشته length-delimited را از آرایه بایت می‌خواند.
        private static string ReadLengthDelimitedString(byte[] bytes, ref int index)
        {
            ulong lengthValue;
            if (!TryReadVarint(bytes, ref index, out lengthValue)) return string.Empty;

            int length = (int)lengthValue;
            if (length <= 0) return string.Empty;
            if (index < 0 || index + length > bytes.Length) return string.Empty;

            string result = Encoding.UTF8.GetString(bytes, index, length);
            index += length;
            return result;
        }

        //* فیلدهای ناشناخته پروتوباف را رد می‌کند تا تغییرات آینده قرارداد باعث شکست نشود.
        private static bool SkipUnknownField(byte[] bytes, ref int index, int wireType)
        {
            switch (wireType)
            {
                case 0:
                    ulong ignored;
                    return TryReadVarint(bytes, ref index, out ignored);

                case 1:
                    return SkipBytes(bytes, ref index, 8);

                case 2:
                    ulong length;
                    if (!TryReadVarint(bytes, ref index, out length)) return false;
                    return SkipBytes(bytes, ref index, (int)length);

                case 5:
                    return SkipBytes(bytes, ref index, 4);

                default:
                    return false;
            }
        }

        //* تعداد مشخصی بایت را با کنترل محدوده رد می‌کند.
        private static bool SkipBytes(byte[] bytes, ref int index, int count)
        {
            if (count < 0) return false;
            if (index < 0 || index + count > bytes.Length) return false;

            index += count;
            return true;
        }
    }
}

//* این فایل مدل حمل rawJson و کدک سبک پروتوباف را برای جی‌آر‌پی‌سی استریمینگ نگه می‌دارد.
//* این فایل اِنولوپ جدید نمی‌سازد و فقط جیسون خام اِنولوپ فعلی را داخل فیلد rawJson جابه‌جا می‌کند.
