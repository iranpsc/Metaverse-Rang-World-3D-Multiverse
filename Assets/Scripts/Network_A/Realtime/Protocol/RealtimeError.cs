using System;

namespace Network_A.Realtime.Protocol
{
    [Serializable]
    public class RealtimeError
    {
        public string code = string.Empty;
        public string message = string.Empty;
        public string detailsJson = "null";

        //* یک خطای خالی با مقدارهای امن می سازد.
        public RealtimeError()
        {
        }

        //* یک خطای ریل تایم آماده می سازد.
        public static RealtimeError Create(string code, string message, string detailsJson = "null")
        {
            var error = new RealtimeError();
            error.code = code ?? string.Empty;
            error.message = message ?? string.Empty;
            error.detailsJson = RealtimeJsonUtil.NormalizeRawJson(detailsJson);
            return error;
        }

        //* پِیلود جیسون خطا را برای قرار گرفتن داخل اِنولوپ می سازد.
        public string ToPayloadJson()
        {
            detailsJson = RealtimeJsonUtil.NormalizeRawJson(detailsJson);

            return "{"
                + "\"code\":\"" + RealtimeJsonUtil.Escape(code) + "\","
                + "\"message\":\"" + RealtimeJsonUtil.Escape(message) + "\","
                + "\"details\":" + detailsJson
                + "}";
        }

        //* جیسون پِیلود خطا را به مدل قابل استفاده در یونیتی تبدیل می کند.
        public static RealtimeError FromPayloadJson(string payloadJson)
        {
            if (string.IsNullOrWhiteSpace(payloadJson)) return null;

            var error = new RealtimeError();
            error.code = RealtimeJsonUtil.ReadString(payloadJson, "code", string.Empty);
            error.message = RealtimeJsonUtil.ReadString(payloadJson, "message", string.Empty);
            error.detailsJson = RealtimeJsonUtil.ReadRawValue(payloadJson, "details", "null");
            return error;
        }

        //* اگر اِنولوپ از نوع خطا باشد، پِیلود آن را به مدل خطا تبدیل می کند.
        public static RealtimeError FromEnvelope(RealtimeEnvelope envelope)
        {
            if (envelope == null || !envelope.IsError()) return null;
            return FromPayloadJson(envelope.payloadJson);
        }

        //* بررسی می کند خطا از نوع انقضای توکن است یا نه.
        public bool IsTokenExpired()
        {
            return code == RealtimeErrorCodes.TokenExpired;
        }
    }

    public static class RealtimeErrorCodes
    {
        public const string InternalError = "internal_error";
        public const string InvalidEnvelope = "invalid_envelope";
        public const string InvalidMessage = "invalid_message";
        public const string InvalidMessageType = "invalid_message_type";
        public const string AuthRequired = "auth_required";
        public const string AuthFailed = "auth_failed";
        public const string TokenExpired = "token_expired";
        public const string Forbidden = "forbidden";
        public const string RateLimited = "rate_limited";
        public const string RoomNotFound = "room_not_found";
        public const string UnknownRoute = "unknown_route";
    }
}

//* این فایل فرمت خطای ریل تایم و کدهای رسمی خطا را نگه می دارد.
//* این خطاها باید با کدهای برگشتی سرور هم نام بمانند.