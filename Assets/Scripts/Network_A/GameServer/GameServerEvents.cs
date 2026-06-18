using System;
using Network_A.Realtime.Protocol;
using UnityEngine;

namespace Network_A.GameServer
{
    //* رویدادهای سطح گیم‌سرور را نگه می‌دارد تا گیم‌پلی مستقیم با اِنولوپ خام کار نکند.
    public class GameServerEvents
    {
        public event Action<string> LogReceived;
        public event Action<GameServerAckResult> AckReceived;
        public event Action<RealtimeEnvelope> WorldEventReceived;
        public event Action<RealtimeEnvelope> PlayerStateReceived;
        public event Action<GameServerPresenceEvent> PlayerJoinedReceived;
        public event Action<GameServerPresenceEvent> PlayerLeftReceived;
        public event Action<RealtimeError> ErrorReceived;

        //* لاگ سطح گیم‌سرور را برای یوآی یا تست بیرون می‌فرستد.
        public void RaiseLog(string message)
        {
            LogReceived?.Invoke(message ?? string.Empty);
        }

        //* نتیجه اَک سرور را برای لایه گیم‌پلی بیرون می‌فرستد.
        public void RaiseAck(GameServerAckResult ack)
        {
            AckReceived?.Invoke(ack);
        }

        //* رویدادهای جهان را بعد از دریافت از ریل‌تایم به گیم‌پلی اعلام می‌کند.
        public void RaiseWorldEvent(RealtimeEnvelope envelope)
        {
            WorldEventReceived?.Invoke(envelope);
        }

        //* وضعیت پلیرهای دیگر را بعد از دریافت از ریل‌تایم به گیم‌پلی اعلام می‌کند.
        public void RaisePlayerState(RealtimeEnvelope envelope)
        {
            PlayerStateReceived?.Invoke(envelope);
        }

        //* ورود پلیر به روم را بعد از دریافت از سرور به لایه گیم‌پلی اعلام می‌کند.
        public void RaisePlayerJoined(GameServerPresenceEvent presenceEvent)
        {
            PlayerJoinedReceived?.Invoke(presenceEvent);
        }

        //* خروج پلیر از روم را بعد از دریافت از سرور به لایه گیم‌پلی اعلام می‌کند.
        public void RaisePlayerLeft(GameServerPresenceEvent presenceEvent)
        {
            PlayerLeftReceived?.Invoke(presenceEvent);
        }

        //* خطای ریل‌تایم یا گیم‌سرور را به لایه بیرونی اعلام می‌کند.
        public void RaiseError(RealtimeError error)
        {
            ErrorReceived?.Invoke(error);
        }
    }

    [Serializable]
    public class GameServerPresenceEvent
    {
        public string eventType = string.Empty;
        public string roomId = string.Empty;
        public string userId = string.Empty;
        public string connectionId = string.Empty;
        public string playerId = string.Empty;
        public string rawPayloadJson = "{}";

        //* شناسه مناسب برای پلیر ریموت را با اولویت playerId، سپس connectionId و سپس userId برمی‌گرداند.
        public string ResolveNetworkPlayerId()
        {
            if (!string.IsNullOrWhiteSpace(playerId)) return playerId;
            if (!string.IsNullOrWhiteSpace(connectionId)) return connectionId;
            if (!string.IsNullOrWhiteSpace(userId)) return userId;
            return string.Empty;
        }

        //* بررسی می‌کند رویداد پرزنس حداقل یک شناسه قابل استفاده داشته باشد.
        public bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(ResolveNetworkPlayerId());
        }

        //* رویداد پرزنس را از اِنولوپ دریافتی سرور می‌سازد.
        public static GameServerPresenceEvent FromEnvelope(RealtimeEnvelope envelope)
        {
            if (envelope == null) return null;

            string payloadJson = envelope.payloadJson ?? "{}";
            return new GameServerPresenceEvent
            {
                eventType = envelope.t ?? string.Empty,
                roomId = string.IsNullOrWhiteSpace(envelope.room) ? ReadString(payloadJson, "roomId", string.Empty) : envelope.room,
                userId = ReadString(payloadJson, "userId", string.Empty),
                connectionId = ReadString(payloadJson, "connectionId", string.Empty),
                playerId = ReadFirstString(payloadJson, string.Empty, "playerId", "networkPlayerId", "avatarId"),
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

        //* یک فیلد متنی ساده را از جیسون پِیلود می‌خواند.
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
            var result = new System.Text.StringBuilder();

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
    }

    [Serializable]
    public class GameServerAckResult
    {
        public string originalMessageId = string.Empty;
        public string status = string.Empty;
        public string detailsJson = "{}";
        public string replyTo = string.Empty;

        //* بررسی می‌کند اَک دریافتی از سمت سرور پردازش موفق داشته است یا نه.
        public bool IsProcessed()
        {
            return string.Equals(status, "processed", StringComparison.OrdinalIgnoreCase);
        }

        //* اَک گیم‌سرور را از اِنولوپ سیستم می‌سازد.
        public static GameServerAckResult FromEnvelope(RealtimeEnvelope envelope)
        {
            if (envelope == null) return null;

            return new GameServerAckResult
            {
                originalMessageId = ReadString(envelope.payloadJson, "originalMessageId", string.Empty),
                status = ReadString(envelope.payloadJson, "status", string.Empty),
                detailsJson = ReadRawValue(envelope.payloadJson, "details", "{}"),
                replyTo = envelope.replyTo ?? string.Empty
            };
        }

        //* یک فیلد متنی ساده را از جیسون پِیلود می‌خواند.
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
            var result = new System.Text.StringBuilder();

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

        //* مقدار خام یک فیلد را از جیسون پِیلود بیرون می‌کشد.
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

        //* انتهای مقدار خام جیسون را با درنظرگرفتن آبجکت و آرایه پیدا می‌کند.
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
                if (c == '}')
                {
                    if (objectDepth == 0) return i;
                    objectDepth--;
                    if (objectDepth == 0 && arrayDepth == 0) return i + 1;
                }

                if (c == '[') arrayDepth++;
                if (c == ']')
                {
                    if (arrayDepth == 0) return i;
                    arrayDepth--;
                    if (objectDepth == 0 && arrayDepth == 0) return i + 1;
                }

                if ((c == ',' || c == '}') && objectDepth == 0 && arrayDepth == 0) return i;
            }

            return json.Length;
        }
    }
}

//* این فایل رویدادهای سطح گیم‌سرور را برای یونیتی نگه می‌دارد.
//* هدف این فایل جدا کردن گیم‌پلی از اِنولوپ خام ریل‌تایم است.
