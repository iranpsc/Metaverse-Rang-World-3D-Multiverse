using System.Collections.Generic;
using System.Text;
using UnityEngine.Networking;
using Assets.Scripts.Network.Core.Models;

namespace Assets.Scripts.Network.HTTP.Encoders
{
    public class UrlEncodedBodyEncoder : IRequestBodyEncoder
    {
        public bool ShouldSetContentTypeHeader => true;

        public UnityWebRequest Build(string url, string method, RequestModel request)
        {
            var uwr = new UnityWebRequest(url, method);

            if (request.Body is Dictionary<string, string> form &&
                (method == UnityWebRequest.kHttpVerbPOST ||
                 method == UnityWebRequest.kHttpVerbPUT ||
                 method == "PATCH"))
            {
                string payload = BuildUrlEncoded(form);
                byte[] bodyRaw = Encoding.UTF8.GetBytes(payload);
                uwr.uploadHandler = new UploadHandlerRaw(bodyRaw);
            }

            return uwr;
        }

        private static string BuildUrlEncoded(Dictionary<string, string> form)
        {
            var sb = new StringBuilder();
            bool first = true;

            foreach (var kv in form)
            {
                if (!first) sb.Append("&");
                first = false;

                sb.Append(UnityWebRequest.EscapeURL(kv.Key));
                sb.Append("=");
                sb.Append(UnityWebRequest.EscapeURL(kv.Value ?? ""));
            }

            return sb.ToString();
        }
    }
}
