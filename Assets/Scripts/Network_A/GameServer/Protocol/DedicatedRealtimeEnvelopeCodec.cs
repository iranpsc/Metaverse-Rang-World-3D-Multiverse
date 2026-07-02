using System;
using Network_A.Realtime.Protocol;
using UnityEngine;

namespace Network_A.GameServer.Protocol
{
    public static class DedicatedRealtimeEnvelopeCodec
    {
        //* این تابع بررسی می کند متن ورودی اِنولوپ معتبر ریل تایم هست یا نه.
        public static bool TryParseEnvelope(string text, out RealtimeEnvelope envelope)
        {
            envelope = null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            RealtimeEnvelope parsed = RealtimeEnvelope.FromJson(text);
            if (parsed == null || !parsed.IsValidBasic()) return false;

            envelope = parsed;
            return true;
        }


        //* این تابع فرمت پیام ورودی را برای لاگ تشخیص می دهد.
        public static string ReadMessageFormat(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "empty";
            if (TryParseEnvelope(text, out RealtimeEnvelope _)) return "envelope";

            DedicatedMessageTypeDto typeDto = ReadLegacyTypeDto(text);
            if (typeDto != null && !string.IsNullOrWhiteSpace(typeDto.type)) return "legacy";

            return "invalid";
        }

        //* این تابع مسیر قابل خواندن پیام را برای لاگ می سازد.
        public static string ReadRouteForLog(string text)
        {
            if (TryParseEnvelope(text, out RealtimeEnvelope envelope))
            {
                string channel = string.IsNullOrWhiteSpace(envelope.ch) ? "unknown" : envelope.ch.Trim();
                string type = string.IsNullOrWhiteSpace(envelope.t) ? "unknown" : envelope.t.Trim();
                return channel + "/" + type;
            }

            DedicatedMessageTypeDto typeDto = ReadLegacyTypeDto(text);
            string legacyType = typeDto == null || string.IsNullOrWhiteSpace(typeDto.type) ? "unknown" : typeDto.type.Trim();
            return "legacy/" + legacyType;
        }

        //* این تابع تایپ پیام را از اِنولوپ یا پیام قدیمی خام می خواند.
        public static string ReadMessageType(string text)
        {
            if (TryParseEnvelope(text, out RealtimeEnvelope envelope)) return envelope.t;

            DedicatedMessageTypeDto typeDto = ReadLegacyTypeDto(text);
            return typeDto == null ? string.Empty : typeDto.type;
        }

        //* این تابع کانال پیام را از اِنولوپ می خواند و برای پیام خام قدیمی مقدار خالی برمی گرداند.
        public static string ReadChannel(string text)
        {
            return TryParseEnvelope(text, out RealtimeEnvelope envelope) ? envelope.ch : string.Empty;
        }

        //* این تابع پِیلود قابل پارس را از اِنولوپ یا پیام خام قدیمی برمی گرداند.
        public static string ReadPayloadOrRawJson(string text)
        {
            if (TryParseEnvelope(text, out RealtimeEnvelope envelope)) return envelope.payloadJson;
            return string.IsNullOrWhiteSpace(text) ? "{}" : text;
        }

        //* این تابع پیام خروجی را داخل اِنولوپ استاندارد پروژه قرار می دهد.
        public static string WrapPayload(string channel, string type, string payloadJson, string roomId = "", bool requiresAck = false, string replyTo = "")
        {
            RealtimeEnvelope envelope = RealtimeEnvelope.Create(channel, type, payloadJson, roomId, requiresAck);
            envelope.replyTo = string.IsNullOrWhiteSpace(replyTo) ? string.Empty : replyTo.Trim();
            envelope.EnsureDefaults();
            return envelope.ToJson();
        }

        //* این تابع پیام سیستم خروجی را داخل اِنولوپ استاندارد پروژه قرار می دهد.
        public static string WrapSystemPayload(string type, string payloadJson, string roomId = "", bool requiresAck = false, string replyTo = "")
        {
            return WrapPayload(RealtimeChannels.System, type, payloadJson, roomId, requiresAck, replyTo);
        }

        //* این تابع پیام پرزنس خروجی را داخل اِنولوپ استاندارد پروژه قرار می دهد.
        public static string WrapPresencePayload(string type, string payloadJson, string roomId = "", bool requiresAck = false, string replyTo = "")
        {
            return WrapPayload(RealtimeChannels.Presence, type, payloadJson, roomId, requiresAck, replyTo);
        }

        //* این تابع پیام گیم خروجی را داخل اِنولوپ استاندارد پروژه قرار می دهد.
        public static string WrapGamePayload(string type, string payloadJson, string roomId = "", bool requiresAck = false, string replyTo = "")
        {
            return WrapPayload(RealtimeChannels.Game, type, payloadJson, roomId, requiresAck, replyTo);
        }

        //* این تابع بررسی می کند اِنولوپ ورودی با کانال و تایپ مورد انتظار هماهنگ است یا نه.
        public static bool Matches(string text, string expectedChannel, string expectedType)
        {
            if (!TryParseEnvelope(text, out RealtimeEnvelope envelope)) return false;
            return string.Equals(envelope.ch, expectedChannel, StringComparison.Ordinal) &&
                   string.Equals(envelope.t, expectedType, StringComparison.Ordinal);
        }

        //* این تابع برای سازگاری با پیام های قدیمی، فیلد type خام را می خواند.
        private static DedicatedMessageTypeDto ReadLegacyTypeDto(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            try
            {
                return JsonUtility.FromJson<DedicatedMessageTypeDto>(text);
            }
            catch
            {
                return null;
            }
        }
    }
}

//* این فایل کدک مشترک بین ددیکیتد گیم سرور و قرارداد ریل تایم است.
//* این فایل پیام های جدید را با RealtimeEnvelope استاندارد می کند و برای گذار امن، پیام خام قدیمی را هم می خواند.
//* برای تست گذار، این فایل می تواند در لاگ مشخص کند پیام از مسیر envelope آمده یا legacy.
