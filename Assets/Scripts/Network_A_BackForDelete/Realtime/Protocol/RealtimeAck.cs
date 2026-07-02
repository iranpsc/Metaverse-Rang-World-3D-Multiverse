using System;

namespace Network_A.Realtime.Protocol
{
    [Serializable]
    public class RealtimeAck
    {
        public const string StatusProcessed = "processed";
        public const string StatusReceived = "received";
        public const string StatusFailed = "failed";

        public string originalMessageId = string.Empty;
        public string status = StatusProcessed;
        public string detailsJson = "{}";

        //* یک اَک خالی با مقدارهای امن می سازد.
        public RealtimeAck()
        {
        }

        //* یک اَک آماده برای یک پیام اصلی می سازد.
        public static RealtimeAck Create(string originalMessageId, string status = StatusProcessed, string detailsJson = "{}")
        {
            var ack = new RealtimeAck();
            ack.originalMessageId = originalMessageId ?? string.Empty;
            ack.status = string.IsNullOrWhiteSpace(status) ? StatusProcessed : status;
            ack.detailsJson = RealtimeJsonUtil.NormalizeRawJson(detailsJson);
            return ack;
        }

        //* پِیلود جیسون اَک را برای قرار گرفتن داخل اِنولوپ می سازد.
        public string ToPayloadJson()
        {
            detailsJson = RealtimeJsonUtil.NormalizeRawJson(detailsJson);

            return "{"
                + "\"originalMessageId\":\"" + RealtimeJsonUtil.Escape(originalMessageId) + "\","
                + "\"status\":\"" + RealtimeJsonUtil.Escape(status) + "\","
                + "\"details\":" + detailsJson
                + "}";
        }

        //* جیسون پِیلود اَک را به مدل قابل استفاده در یونیتی تبدیل می کند.
        public static RealtimeAck FromPayloadJson(string payloadJson)
        {
            if (string.IsNullOrWhiteSpace(payloadJson)) return null;

            var ack = new RealtimeAck();
            ack.originalMessageId = RealtimeJsonUtil.ReadString(payloadJson, "originalMessageId", string.Empty);
            ack.status = RealtimeJsonUtil.ReadString(payloadJson, "status", string.Empty);
            ack.detailsJson = RealtimeJsonUtil.ReadRawValue(payloadJson, "details", "{}");
            return ack;
        }

        //* اگر اِنولوپ از نوع اَک باشد، پِیلود آن را به مدل اَک تبدیل می کند.
        public static RealtimeAck FromEnvelope(RealtimeEnvelope envelope)
        {
            if (envelope == null || !envelope.IsAck()) return null;
            return FromPayloadJson(envelope.payloadJson);
        }

        //* بررسی می کند اَک مربوط به پیام مورد نظر هست یا نه.
        public bool Matches(string messageId)
        {
            return !string.IsNullOrWhiteSpace(messageId) && originalMessageId == messageId;
        }

        //* بررسی می کند اَک با وضعیت موفق پردازش شده یا نه.
        public bool IsProcessed()
        {
            return status == StatusProcessed || status == StatusReceived;
        }
    }
}

//* این فایل فرمت اَک ریل تایم را نگه می دارد.
//* اَک برای پیام های مهم و قابل پیگیری استفاده می شود.
