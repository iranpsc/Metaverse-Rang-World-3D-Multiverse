using System;
using System.Text;
using Network_A.Realtime.Protocol;

namespace Network_A.GameIntegration.World
{
    //* دیتای استاندارد رویداد جهان را از اِنولوپ گیم‌سرور می‌سازد تا گیم‌پلی با جیسون خام کار نکند.
    [Serializable]
    public class RealtimeWorldEventData
    {
        public string roomId = string.Empty;
        public string eventType = string.Empty;
        public string senderPlayerId = string.Empty;
        public string objectId = string.Empty;
        public string stateKey = string.Empty;
        public bool boolValue;
        public float numberValue;
        public string stringValue = string.Empty;
        public long sequence;
        public long sentAtMs;
        public string eventJson = "{}";
        public string rawPayloadJson = "{}";

        //* بررسی می‌کند رویداد حداقل تایپ و آبجکت هدف داشته باشد.
        public bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(eventType) && !string.IsNullOrWhiteSpace(objectId);
        }

        //* رویداد دریافتی را از اِنولوپ ریل‌تایم به مدل قابل استفاده گیم‌پلی تبدیل می‌کند.
        public static RealtimeWorldEventData FromEnvelope(RealtimeEnvelope envelope)
        {
            if (envelope == null) return null;

            string payloadJson = string.IsNullOrWhiteSpace(envelope.payloadJson) ? "{}" : envelope.payloadJson;
            string eventJson = ReadRawValue(payloadJson, "event", "{}");
            string eventType = ReadString(payloadJson, "eventType", string.Empty);

            if (string.IsNullOrWhiteSpace(eventType)) eventType = ReadFirstString(eventJson, string.Empty, "eventType", "type");

            return new RealtimeWorldEventData
            {
                roomId = string.IsNullOrWhiteSpace(envelope.room) ? ReadString(payloadJson, "roomId", string.Empty) : envelope.room,
                eventType = eventType,
                senderPlayerId = ReadFirstString(eventJson, string.Empty, "senderPlayerId", "playerId", "userId"),
                objectId = ReadFirstString(eventJson, string.Empty, "objectId", "targetId", "doorId", "itemId"),
                stateKey = ReadFirstString(eventJson, string.Empty, "stateKey", "key", "state"),
                boolValue = ReadBool(eventJson, "boolValue", ReadBool(eventJson, "isOpen", ReadBool(eventJson, "active", false))),
                numberValue = ReadFloat(eventJson, "numberValue", 0f),
                stringValue = ReadString(eventJson, "stringValue", string.Empty),
                sequence = ReadLong(eventJson, "sequence", 0),
                sentAtMs = ReadLong(eventJson, "sentAtMs", 0),
                eventJson = eventJson,
                rawPayloadJson = payloadJson
            };
        }

        //* اولین فیلد متنی موجود را از جیسون می‌خواند.
        private static string ReadFirstString(string json, string fallback, params string[] keys)
        {
            if (keys == null) return fallback;

            for (int i = 0; i < keys.Length; i++)
            {
                string value = ReadString(json, keys[i], string.Empty);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }

            return fallback;
        }

        //* یک فیلد متنی ساده را از جیسون می‌خواند.
        private static string ReadString(string json, string key, string fallback)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key)) return fallback;

            string pattern = "\"" + key + "\"";
            int keyIndex = json.IndexOf(pattern, StringComparison.Ordinal);
            if (keyIndex < 0) return fallback;

            int colonIndex = json.IndexOf(':', keyIndex + pattern.Length);
            if (colonIndex < 0) return fallback;

            int valueStart = colonIndex + 1;
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart])) valueStart++;
            if (valueStart >= json.Length || json[valueStart] != '\"') return fallback;

            int textStart = valueStart + 1;
            var result = new StringBuilder();

            for (int i = textStart; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    i++;
                    result.Append(json[i]);
                    continue;
                }

                if (c == '\"') return result.ToString();
                result.Append(c);
            }

            return fallback;
        }

        //* مقدار بولین یک فیلد ساده را از جیسون می‌خواند.
        private static bool ReadBool(string json, string key, bool fallback)
        {
            string raw = ReadRawValue(json, key, string.Empty);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        //* مقدار عددی long را از جیسون می‌خواند.
        private static long ReadLong(string json, string key, long fallback)
        {
            string raw = ReadRawValue(json, key, string.Empty);
            if (long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long value)) return value;
            return fallback;
        }

        //* مقدار عددی float را از جیسون می‌خواند.
        private static float ReadFloat(string json, string key, float fallback)
        {
            string raw = ReadRawValue(json, key, string.Empty);
            if (float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value)) return value;
            return fallback;
        }

        //* مقدار خام یک فیلد را از جیسون بیرون می‌کشد.
        private static string ReadRawValue(string json, string key, string fallback)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key)) return fallback;

            string pattern = "\"" + key + "\"";
            int keyIndex = json.IndexOf(pattern, StringComparison.Ordinal);
            if (keyIndex < 0) return fallback;

            int colonIndex = json.IndexOf(':', keyIndex + pattern.Length);
            if (colonIndex < 0) return fallback;

            int valueStart = colonIndex + 1;
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart])) valueStart++;
            if (valueStart >= json.Length) return fallback;

            int valueEnd = FindRawValueEnd(json, valueStart);
            if (valueEnd <= valueStart) return fallback;

            return json.Substring(valueStart, valueEnd - valueStart).Trim();
        }

        //* انتهای مقدار خام جیسون را با درنظرگرفتن آبجکت، آرایه و متن پیدا می‌کند.
        private static int FindRawValueEnd(string json, int valueStart)
        {
            bool insideString = false;
            int objectDepth = 0;
            int arrayDepth = 0;

            for (int i = valueStart; i < json.Length; i++)
            {
                char c = json[i];

                if (c == '\\' && insideString)
                {
                    i++;
                    continue;
                }

                if (c == '\"')
                {
                    insideString = !insideString;
                    continue;
                }

                if (insideString) continue;
                if (c == '{') objectDepth++;
                else if (c == '}')
                {
                    if (objectDepth == 0) return i;
                    objectDepth--;
                    if (objectDepth == 0 && arrayDepth == 0) return i + 1;
                }
                else if (c == '[') arrayDepth++;
                else if (c == ']')
                {
                    if (arrayDepth == 0) return i;
                    arrayDepth--;
                    if (objectDepth == 0 && arrayDepth == 0) return i + 1;
                }
                else if ((c == ',' || c == '}') && objectDepth == 0 && arrayDepth == 0) return i;
            }

            return json.Length;
        }
    }
}
