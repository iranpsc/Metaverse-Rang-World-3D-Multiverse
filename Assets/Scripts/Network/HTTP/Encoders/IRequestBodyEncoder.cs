using UnityEngine.Networking;
using Assets.Scripts.Network.Core.Models;

namespace Assets.Scripts.Network.HTTP.Encoders
{
    public interface IRequestBodyEncoder
    {
        /// <summary>
        /// اگر false باشد یعنی نباید Content-Type را دستی SetRequestHeader کنیم
        /// (مثل multipart که Unity boundary می‌سازد)
        /// </summary>
        bool ShouldSetContentTypeHeader { get; }

        /// <summary>
        /// UnityWebRequest را با بدنه‌ی صحیح می‌سازد (بدون ست کردن هدرها)
        /// </summary>
        UnityWebRequest Build(string url, string method, RequestModel request);
    }
}
