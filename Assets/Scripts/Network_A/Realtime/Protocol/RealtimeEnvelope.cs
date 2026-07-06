using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Network_A.Realtime.Protocol
{
    [Serializable]
    public class RealtimeEnvelope
    {
        public int v = 1;
        public string ch = string.Empty;
        public string t = string.Empty;
        public string id = string.Empty;
        public long ts;
        public string room = string.Empty;
        public string payloadJson = "{}";
        public bool requiresAck;
        public string replyTo = string.Empty;

        //* یک اِنولوپ خالی با مقدارهای امن می سازد.
        public RealtimeEnvelope()
        {
            ts = RealtimeJsonUtil.NowUnixMs();
            id = CreateMessageId("msg");
        }

        //* یک اِنولوپ آماده برای ارسال به سرور می سازد.
        public static RealtimeEnvelope Create(string channel, string type, string payloadJson = "{}", string roomId = "", bool requiresAck = false)
        {
            var envelope = new RealtimeEnvelope();
            envelope.ch = channel ?? string.Empty;
            envelope.t = type ?? string.Empty;
            envelope.room = roomId ?? string.Empty;
            envelope.payloadJson = RealtimeJsonUtil.NormalizeRawJson(payloadJson);
            envelope.requiresAck = requiresAck;
            envelope.EnsureDefaults();
            return envelope;
        }

        //* یک اِنولوپ آماده با آیدی مشخص می سازد.
        public static RealtimeEnvelope CreateWithId(string messageId, string channel, string type, string payloadJson = "{}", string roomId = "", bool requiresAck = false)
        {
            var envelope = Create(channel, type, payloadJson, roomId, requiresAck);
            envelope.id = string.IsNullOrWhiteSpace(messageId) ? CreateMessageId(type) : messageId;
            return envelope;
        }

        //* یک آیدی یکتا برای پیام ریل تایم می سازد.
        public static string CreateMessageId(string prefix)
        {
            string safePrefix = string.IsNullOrWhiteSpace(prefix) ? "msg" : prefix.Trim();
            return safePrefix + "_" + Guid.NewGuid().ToString();
        }

        //* مقدارهای ضروری اِنولوپ را قبل از ارسال کامل می کند.
        public void EnsureDefaults()
        {
            if (v <= 0) v = 1;
            if (string.IsNullOrWhiteSpace(id)) id = CreateMessageId(t);
            if (ts <= 0) ts = RealtimeJsonUtil.NowUnixMs();
            if (room == null) room = string.Empty;
            payloadJson = RealtimeJsonUtil.NormalizeRawJson(payloadJson);
            if (replyTo == null) replyTo = string.Empty;
        }

        //* پِیلود خام جیسون را روی اِنولوپ تنظیم می کند.
        public void SetPayloadJson(string rawPayloadJson)
        {
            payloadJson = RealtimeJsonUtil.NormalizeRawJson(rawPayloadJson);
        }

        //* اِنولوپ را به جیسون سازگار با سرور تبدیل می کند.
        public string ToJson()
        {
            EnsureDefaults();

            var sb = new StringBuilder(256);
            sb.Append("{");
            sb.Append("\"v\":").Append(v).Append(",");
            sb.Append("\"ch\":\"").Append(RealtimeJsonUtil.Escape(ch)).Append("\",");
            sb.Append("\"t\":\"").Append(RealtimeJsonUtil.Escape(t)).Append("\",");
            sb.Append("\"id\":\"").Append(RealtimeJsonUtil.Escape(id)).Append("\",");
            sb.Append("\"ts\":").Append(ts).Append(",");
            sb.Append("\"room\":\"").Append(RealtimeJsonUtil.Escape(room)).Append("\",");
            sb.Append("\"payload\":").Append(payloadJson).Append(",");
            sb.Append("\"requiresAck\":").Append(requiresAck ? "true" : "false").Append(",");
            sb.Append("\"replyTo\":\"").Append(RealtimeJsonUtil.Escape(replyTo)).Append("\"");
            sb.Append("}");
            return sb.ToString();
        }

        //* جیسون دریافتی از سرور را به اِنولوپ قابل استفاده در یونیتی تبدیل می کند.
        public static RealtimeEnvelope FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                var envelope = new RealtimeEnvelope();
                envelope.v = RealtimeJsonUtil.ReadInt(json, "v", 1);
                envelope.ch = RealtimeJsonUtil.ReadString(json, "ch", string.Empty);
                envelope.t = RealtimeJsonUtil.ReadString(json, "t", string.Empty);
                envelope.id = RealtimeJsonUtil.ReadString(json, "id", string.Empty);
                envelope.ts = RealtimeJsonUtil.ReadLong(json, "ts", 0);
                envelope.room = RealtimeJsonUtil.ReadString(json, "room", string.Empty);
                envelope.payloadJson = RealtimeJsonUtil.ReadRawValue(json, "payload", "{}");
                envelope.requiresAck = RealtimeJsonUtil.ReadBool(json, "requiresAck", false);
                envelope.replyTo = RealtimeJsonUtil.ReadString(json, "replyTo", string.Empty);
                envelope.EnsureDefaults();
                return envelope;
            }
            catch (Exception ex)
            {
                Debug.LogError("[RealtimeEnvelope] FromJson failed: " + ex.Message);
                return null;
            }
        }

        //* بررسی می کند اِنولوپ حداقل فیلدهای لازم برای رُت شدن را دارد.
        public bool IsValidBasic()
        {
            return v > 0 && !string.IsNullOrWhiteSpace(ch) && !string.IsNullOrWhiteSpace(t) && !string.IsNullOrWhiteSpace(id);
        }

        //* بررسی می کند پیام از نوع اَک است یا نه.
        public bool IsAck()
        {
            return ch == RealtimeChannels.System && t == RealtimeMessageTypes.Ack;
        }

        //* بررسی می کند پیام خطای سیستمی است یا نه.
        public bool IsError()
        {
            return ch == RealtimeChannels.System && t == RealtimeMessageTypes.Error;
        }

        //* بررسی می کند پیام پاسخ یک پیام قبلی است یا نه.
        public bool HasReplyTo()
        {
            return !string.IsNullOrWhiteSpace(replyTo);
        }
    }

    internal static class RealtimeJsonUtil
    {
        //* زمان فعلی را با فرمت یونیکس میلی ثانیه برمی گرداند.
        public static long NowUnixMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        //* جیسون خام را برای قرار گرفتن داخل پِیلود آماده می کند.
        public static string NormalizeRawJson(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson)) return "{}";
            string trimmed = rawJson.Trim();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("[") || trimmed == "null") return trimmed;
            return "\"" + Escape(trimmed) + "\"";
        }

        //* متن را برای قرار گرفتن داخل جیسون escape می کند.
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

        //* یک فیلد استرینگ را از جیسون ساده سرور می خواند.
        public static string ReadString(string json, string key, string fallback)
        {
            int valueStart = FindValueStart(json, key);
            if (valueStart < 0) return fallback;
            if (valueStart >= json.Length || json[valueStart] != '"') return fallback;

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

        //* یک فیلد اینت را از جیسون ساده سرور می خواند.
        public static int ReadInt(string json, string key, int fallback)
        {
            string raw = ReadPrimitive(json, key);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
        }

        //* یک فیلد لُنگ را از جیسون ساده سرور می خواند.
        public static long ReadLong(string json, string key, long fallback)
        {
            string raw = ReadPrimitive(json, key);
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : fallback;
        }

        //* یک فیلد bool را از جیسون ساده سرور می خواند.
        public static bool ReadBool(string json, string key, bool fallback)
        {
            string raw = ReadPrimitive(json, key);
            if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        //* مقدار خام یک فیلد را از جیسون می خواند.
        public static string ReadRawValue(string json, string key, string fallback)
        {
            int valueStart = FindValueStart(json, key);
            if (valueStart < 0 || valueStart >= json.Length) return fallback;

            int valueEnd = FindValueEnd(json, valueStart);
            if (valueEnd <= valueStart) return fallback;

            return json.Substring(valueStart, valueEnd - valueStart).Trim();
        }

        //* مقدار primitive مثل عدد یا بولین را از جیسون می خواند.
        private static string ReadPrimitive(string json, string key)
        {
            string raw = ReadRawValue(json, key, string.Empty);
            return raw.Trim().Trim('"');
        }

        //* شروع مقدار یک فیلد را پیدا می کند.
        private static int FindValueStart(string json, string key)
        {
            string pattern = "\"" + key + "\"";
            int keyIndex = json.IndexOf(pattern, StringComparison.Ordinal);
            if (keyIndex < 0) return -1;

            int colonIndex = json.IndexOf(':', keyIndex + pattern.Length);
            if (colonIndex < 0) return -1;

            int valueStart = colonIndex + 1;
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart])) valueStart++;
            return valueStart;
        }

        //* پایان مقدار یک فیلد را با رعایت object و array پیدا می کند.
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

        //* پایان string را در جیسون پیدا می کند.
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

        //* پایان object یا array را در جیسون پیدا می کند.
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

        //* کاراکتر escape شده جیسون را به متن واقعی تبدیل می کند.
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

//* این فایل مدل اصلی اِنولوپ ریل تایم را نگه می دارد.
//* این فایل مستقل از ترنسپورت است و برای وب سوکت و جی آر پی سی مشترک می ماند.
