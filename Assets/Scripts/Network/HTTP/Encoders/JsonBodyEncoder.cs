using System.Text;
using UnityEngine.Networking;
using Assets.Scripts.Network.Core.Models;
using Assets.Scripts.Network.Core.Utils;

namespace Assets.Scripts.Network.HTTP.Encoders
{
    public class JsonBodyEncoder : IRequestBodyEncoder
    {
        public bool ShouldSetContentTypeHeader => true;

        public UnityWebRequest Build(string url, string method, RequestModel request)
        {
            var uwr = new UnityWebRequest(url, method);

            if (request.Body != null &&
                (method == UnityWebRequest.kHttpVerbPOST ||
                 method == UnityWebRequest.kHttpVerbPUT ||
                 method == "PATCH"))
            {
                string json = request.Body as string ?? JSONSerializer.Serialize(request.Body);
                byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
                uwr.uploadHandler = new UploadHandlerRaw(bodyRaw);
            }

            return uwr;
        }
    }
}
