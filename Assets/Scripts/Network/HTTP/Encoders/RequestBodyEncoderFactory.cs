using Assets.Scripts.Network.Core.Models;

namespace Assets.Scripts.Network.HTTP.Encoders
{
    public static class RequestBodyEncoderFactory
    {
        private static readonly IRequestBodyEncoder Json = new JsonBodyEncoder();
        private static readonly IRequestBodyEncoder UrlEncoded = new UrlEncodedBodyEncoder();
        private static readonly IRequestBodyEncoder Multipart = new MultipartFormDataEncoder();
        private static readonly IRequestBodyEncoder Binary = new BinaryBodyEncoder();

        public static IRequestBodyEncoder Get(RequestModel request)
        {
            switch (request.BodyFormat)
            {
                case BodyFormat.MultipartFormData: return Multipart;
                case BodyFormat.UrlEncoded: return UrlEncoded;
                case BodyFormat.Binary: return Binary;

                // پیش‌فرض
                case BodyFormat.Json:
                default:
                    return Json;
            }
        }
    }
}
