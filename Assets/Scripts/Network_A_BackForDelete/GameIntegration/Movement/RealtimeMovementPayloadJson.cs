using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Network_A.GameIntegration.Movement
{
    //* ابزار جیسون سبک برای پِیلود حرکت است تا برای پیام‌های پرتعداد وابستگی اضافه نسازیم.
    public static class RealtimeMovementPayloadJson
    {
        //* پِیلود حرکت را به جیسون خام قابل ارسال در اِنولوپ تبدیل می‌کند.
        public static string Build(string roomId, string playerId, Vector3 position, Quaternion rotation, Vector3 velocity, long sequence)
        {
            return "{"
                + "\"roomId\":\"" + Escape(roomId) + "\","
                + "\"playerId\":\"" + Escape(playerId) + "\","
                + "\"sequence\":" + sequence + ","
                + "\"sentAtMs\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ","
                + "\"position\":{\"x\":" + FormatFloat(position.x) + ",\"y\":" + FormatFloat(position.y) + ",\"z\":" + FormatFloat(position.z) + "},"
                + "\"rotation\":{\"x\":" + FormatFloat(rotation.x) + ",\"y\":" + FormatFloat(rotation.y) + ",\"z\":" + FormatFloat(rotation.z) + ",\"w\":" + FormatFloat(rotation.w) + "},"
                + "\"velocity\":{\"x\":" + FormatFloat(velocity.x) + ",\"y\":" + FormatFloat(velocity.y) + ",\"z\":" + FormatFloat(velocity.z) + "}"
                + "}";
        }

        //* پِیلود دریافتی از سرور را به مدل حرکت قابل استفاده در گیم‌پلی تبدیل می‌کند.
        public static bool TryParse(string json, out RealtimeMovementSnapshot snapshot)
        {
            snapshot = new RealtimeMovementSnapshot();
            if (string.IsNullOrWhiteSpace(json)) return false;

            snapshot.roomId = ReadString(json, "roomId", string.Empty);
            snapshot.playerId = ReadString(json, "playerId", string.Empty);
            if (string.IsNullOrWhiteSpace(snapshot.playerId)) snapshot.playerId = ReadString(json, "userId", string.Empty);
            if (string.IsNullOrWhiteSpace(snapshot.playerId)) snapshot.playerId = ReadString(json, "avatarId", string.Empty);

            string positionJson = ReadRawValue(json, "position", "{}");
            string rotationJson = ReadRawValue(json, "rotation", "{}");
            string velocityJson = ReadRawValue(json, "velocity", "{}");

            snapshot.position = new Vector3(ReadFloat(positionJson, "x", 0f), ReadFloat(positionJson, "y", 0f), ReadFloat(positionJson, "z", 0f));
            snapshot.rotation = new Quaternion(ReadFloat(rotationJson, "x", 0f), ReadFloat(rotationJson, "y", 0f), ReadFloat(rotationJson, "z", 0f), ReadFloat(rotationJson, "w", 1f));
            snapshot.velocity = new Vector3(ReadFloat(velocityJson, "x", 0f), ReadFloat(velocityJson, "y", 0f), ReadFloat(velocityJson, "z", 0f));
            snapshot.sequence = ReadLong(json, "sequence", 0L);
            snapshot.sentAtMs = ReadLong(json, "sentAtMs", 0L);
            snapshot.receivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            return snapshot.IsValid();
        }

        //* عدد float را با فرمت ثابت و مستقل از زبان سیستم می‌سازد.
        public static string FormatFloat(float value)
        {
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }

        //* متن را برای قرار گرفتن داخل جیسون escape می‌کند.
        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }

            return sb.ToString();
        }

        //* مقدار string یک فیلد ساده را از جیسون می‌خواند.
        private static string ReadString(string json, string key, string fallback)
        {
            int valueStart = FindValueStart(json, key);
            if (valueStart < 0 || valueStart >= json.Length || json[valueStart] != '"') return fallback;

            var sb = new StringBuilder();
            for (int i = valueStart + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    i++;
                    sb.Append(ReadEscapedChar(json[i]));
                    continue;
                }

                if (c == '"') return sb.ToString();
                sb.Append(c);
            }

            return fallback;
        }

        //* مقدار long یک فیلد را از جیسون می‌خواند.
        private static long ReadLong(string json, string key, long fallback)
        {
            string raw = ReadRawValue(json, key, string.Empty).Trim().Trim('"');
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : fallback;
        }

        //* مقدار float یک فیلد را از جیسون می‌خواند.
        private static float ReadFloat(string json, string key, float fallback)
        {
            string raw = ReadRawValue(json, key, string.Empty).Trim().Trim('"');
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : fallback;
        }

        //* مقدار خام یک فیلد را از جیسون استخراج می‌کند.
        private static string ReadRawValue(string json, string key, string fallback)
        {
            int valueStart = FindValueStart(json, key);
            if (valueStart < 0 || valueStart >= json.Length) return fallback;

            int valueEnd = FindValueEnd(json, valueStart);
            if (valueEnd <= valueStart) return fallback;

            return json.Substring(valueStart, valueEnd - valueStart).Trim();
        }

        //* شروع مقدار فیلد را در جیسون پیدا می‌کند.
        private static int FindValueStart(string json, string key)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key)) return -1;

            string pattern = "\"" + key + "\"";
            int keyIndex = json.IndexOf(pattern, StringComparison.Ordinal);
            if (keyIndex < 0) return -1;

            int colonIndex = json.IndexOf(':', keyIndex + pattern.Length);
            if (colonIndex < 0) return -1;

            int valueStart = colonIndex + 1;
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart])) valueStart++;
            return valueStart;
        }

        //* پایان مقدار فیلد را با پشتیبانی از object و array پیدا می‌کند.
        private static int FindValueEnd(string json, int valueStart)
        {
            if (valueStart >= json.Length) return valueStart;

            char first = json[valueStart];
            if (first == '"') return FindStringEnd(json, valueStart) + 1;
            if (first == '{') return FindScopeEnd(json, valueStart, '{', '}') + 1;
            if (first == '[') return FindScopeEnd(json, valueStart, '[', ']') + 1;

            int i = valueStart;
            while (i < json.Length && json[i] != ',' && json[i] != '}') i++;
            return i;
        }

        //* پایان string را در جیسون پیدا می‌کند.
        private static int FindStringEnd(string json, int quoteStart)
        {
            for (int i = quoteStart + 1; i < json.Length; i++)
            {
                if (json[i] == '\\')
                {
                    i++;
                    continue;
                }

                if (json[i] == '"') return i;
            }

            return json.Length - 1;
        }

        //* پایان محدوده object یا array را پیدا می‌کند.
        private static int FindScopeEnd(string json, int scopeStart, char open, char close)
        {
            int depth = 0;
            bool insideString = false;

            for (int i = scopeStart; i < json.Length; i++)
            {
                char c = json[i];

                if (insideString)
                {
                    if (c == '\\') i++;
                    else if (c == '"') insideString = false;
                    continue;
                }

                if (c == '"')
                {
                    insideString = true;
                    continue;
                }

                if (c == open) depth++;
                if (c == close) depth--;
                if (depth == 0) return i;
            }

            return json.Length - 1;
        }

        //* کاراکتر escape شده جیسون را به کاراکتر واقعی تبدیل می‌کند.
        private static char ReadEscapedChar(char escaped)
        {
            switch (escaped)
            {
                case 'n': return '\n';
                case 'r': return '\r';
                case 't': return '\t';
                case '"': return '"';
                case '\\': return '\\';
                default: return escaped;
            }
        }
    }
}
