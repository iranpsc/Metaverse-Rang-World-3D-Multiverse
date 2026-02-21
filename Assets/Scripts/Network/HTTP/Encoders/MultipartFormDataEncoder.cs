using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using Assets.Scripts.Network.Core.Models;

namespace Assets.Scripts.Network.HTTP.Encoders
{
    public class MultipartFormDataEncoder : IRequestBodyEncoder
    {
        // Unity خودش Content-Type با boundary می‌سازد، پس دستی ست نکن
        public bool ShouldSetContentTypeHeader => false;

        public UnityWebRequest Build(string url, string method, RequestModel request)
        {
            var form = new WWWForm();

            if (request.Body is Dictionary<string, string> mp)
            {
                foreach (var kv in mp)
                    form.AddField(kv.Key, kv.Value);
            }

            var uwr = UnityWebRequest.Post(url, form);

            // برای token معمولاً POST است، ولی اگر خواستی عمومی باشد:
            if (method != UnityWebRequest.kHttpVerbPOST)
                uwr.method = method;

            return uwr;
        }
    }
}
