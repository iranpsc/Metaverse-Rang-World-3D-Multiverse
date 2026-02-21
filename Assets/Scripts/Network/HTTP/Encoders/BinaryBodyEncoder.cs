using UnityEngine.Networking;
using Assets.Scripts.Network.Core.Models;

namespace Assets.Scripts.Network.HTTP.Encoders
{
    public class BinaryBodyEncoder : IRequestBodyEncoder
    {
        public bool ShouldSetContentTypeHeader => true;

        public UnityWebRequest Build(string url, string method, RequestModel request)
        {
            var uwr = new UnityWebRequest(url, method);

            if (request.Body is byte[] bytes &&
                (method == UnityWebRequest.kHttpVerbPOST ||
                 method == UnityWebRequest.kHttpVerbPUT ||
                 method == "PATCH"))
            {
                uwr.uploadHandler = new UploadHandlerRaw(bytes);
            }

            return uwr;
        }
    }
}
