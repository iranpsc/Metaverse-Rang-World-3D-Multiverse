using System;

namespace Assets.Scripts.Network.WebSocket
{
    public enum SystemMessageType
    {
        None = 0,
        Pong = 1,
        Ping = 2,
        AuthOk = 3,
        AuthFail = 4,
        Unauthorized = 5,
        Ack = 6,
        ServerError = 7
    }

    public static class SystemMessageParser
    {
        /// <summary>
        /// تلاش برای تشخیص پیام‌های سیستمی:
        /// - Non-JSON: pong, auth_ok, auth_fail
        /// - JSON: {"type":"pong"} / {"action":"auth_ok"} / {"event":"ack", "ackId":"..."} ...
        /// </summary>
        public static bool TryParse(string raw, out SystemMessageType type, out string payload)
        {
            type = SystemMessageType.None;
            payload = null;

            if (string.IsNullOrEmpty(raw))
                return false;

            // ---------- Non-JSON fast path ----------
            // trim کم‌هزینه (بدون ساخت string جدید)
            string s = raw.Trim();

            if (StringEqualsIgnoreCase(s, "pong"))
            {
                type = SystemMessageType.Pong;
                return true;
            }
            if (StringEqualsIgnoreCase(s, "ping"))
            {
                type = SystemMessageType.Ping;
                return true;
            }
            if (StringEqualsIgnoreCase(s, "auth_ok"))
            {
                type = SystemMessageType.AuthOk;
                return true;
            }
            if (StringEqualsIgnoreCase(s, "auth_fail"))
            {
                type = SystemMessageType.AuthFail;
                payload = raw;
                return true;
            }

            // ---------- JSON-ish path ----------
            // اگر شبیه JSON نیست، بیخیال
            if (!(s.Length >= 2 && s[0] == '{' && s[s.Length - 1] == '}'))
                return false;

            // استخراج نوع از فیلدهای رایج
            string msgType =
                TryExtractJsonStringField(s, "type") ??
                TryExtractJsonStringField(s, "action") ??
                TryExtractJsonStringField(s, "event");

            if (string.IsNullOrEmpty(msgType))
                return false;

            msgType = msgType.Trim();

            // mapping
            if (StringEqualsIgnoreCase(msgType, "pong"))
            {
                type = SystemMessageType.Pong;
                return true;
            }
            if (StringEqualsIgnoreCase(msgType, "ping"))
            {
                type = SystemMessageType.Ping;
                return true;
            }
            if (StringEqualsIgnoreCase(msgType, "auth_ok"))
            {
                type = SystemMessageType.AuthOk;
                return true;
            }
            if (StringEqualsIgnoreCase(msgType, "auth_fail"))
            {
                type = SystemMessageType.AuthFail;
                payload = raw;
                return true;
            }
            if (StringEqualsIgnoreCase(msgType, "unauthorized"))
            {
                type = SystemMessageType.Unauthorized;
                payload = raw;
                return true;
            }
            if (StringEqualsIgnoreCase(msgType, "ack"))
            {
                type = SystemMessageType.Ack;

                payload =
                    TryExtractJsonStringField(s, "ackId") ??
                    TryExtractJsonStringField(s, "id") ??
                    TryExtractJsonStringField(s, "messageId");

                return true;
            }
            if (StringEqualsIgnoreCase(msgType, "server_error") || StringEqualsIgnoreCase(msgType, "error"))
            {
                type = SystemMessageType.ServerError;
                payload = raw;
                return true;
            }

            return false;
        }

        // -------------------------
        // Helpers
        // -------------------------

        private static bool StringEqualsIgnoreCase(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// استخراج سریع مقدار یک فیلد string در JSON ساده:
        /// "field":"value"
        /// توجه: این parser کامل JSON نیست، ولی برای پیام‌های سیستمی کافی و سریع است.
        /// </summary>
        private static string TryExtractJsonStringField(string json, string fieldName)
        {
            // دنبال "fieldName"
            // pattern: "fieldName" : "value"
            string needle = $"\"{fieldName}\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return null;

            i += needle.Length;

            // skip spaces and colon
            i = SkipSpaces(json, i);
            if (i >= json.Length || json[i] != ':') return null;
            i++;
            i = SkipSpaces(json, i);

            // must start with quote
            if (i >= json.Length || json[i] != '"') return null;
            i++;

            // read until next unescaped quote
            int start = i;
            bool esc = false;

            for (; i < json.Length; i++)
            {
                char c = json[i];
                if (esc)
                {
                    esc = false;
                    continue;
                }
                if (c == '\\')
                {
                    esc = true;
                    continue;
                }
                if (c == '"')
                {
                    // value is json.Substring(start, i-start) but handle minimal unescape
                    return UnescapeJsonString(json, start, i - start);
                }
            }

            return null;
        }

        private static int SkipSpaces(string s, int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                    break;
                i++;
            }
            return i;
        }

        private static string UnescapeJsonString(string src, int start, int len)
        {
            // اگر هیچ backslash ندارد، سریع substring
            int bs = src.IndexOf('\\', start, len);
            if (bs < 0)
                return src.Substring(start, len);

            // حداقل unescape برای \" و \\ و \/ و \n \r \t
            var sb = new System.Text.StringBuilder(len);
            int end = start + len;

            for (int i = start; i < end; i++)
            {
                char c = src[i];
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (i + 1 >= end)
                    break;

                char n = src[++i];
                switch (n)
                {
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case '/': sb.Append('/'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    default:
                        // برای سادگی، همین n را اضافه می‌کنیم (و unicode را فعلاً ignore)
                        sb.Append(n);
                        break;
                }
            }

            return sb.ToString();
        }
    }
}
