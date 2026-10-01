using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace MetaRange.Avatar
{
    [Serializable]
    public class Vec3Data
    {
        public float x, y, z;

        public Vector3 ToVector3() => new Vector3(x, y, z);
        public static Vec3Data From(Vector3 v) => new Vec3Data { x = v.x, y = v.y, z = v.z };
    }

    [Serializable]
    public class QuatData
    {
        public float x, y, z, w = 1f;

        public Quaternion ToQuaternion() => new Quaternion(x, y, z, w);
        public static QuatData From(Quaternion q) => new QuatData { x = q.x, y = q.y, z = q.z, w = q.w };
    }

    [Serializable]
    public class PositionEntry
    {
        public Vec3Data position;
        public QuatData rotation;
        public string createdAt;
        public string updatedAt;
    }

    [Serializable]
    public class CreateEnvBody
    {
        public string environmentName;

        public CreateEnvBody() { }
        public CreateEnvBody(string name) { environmentName = name; }
    }

    [Serializable]
    public class AddPositionBody
    {
        public string environmentName;
        public string positionId;
        public Vec3Data position;
        public QuatData rotation;
    }

    [Serializable]
    public class RenamePositionBody
    {
        public string environmentName;
        public string positionId;        // نام فعلی
        public string newPositionId;     // نام یونیک جدید
    }

    [Serializable]
    public class ErrorBody
    {
        public string error;
    }

    /// <summary>
    /// درخواست‌های HTTP مشترک متارنج:
    /// - Content-Type: application/json برای POST/PUT (بدنه JSON واقعی، نه query)
    /// - اگر localhost وصل نشد (IPv6/IPv4) یک بار با 127.0.0.1 تلاش می‌شود
    /// - خطاها با responseCode و متن پاسخ گزارش می‌شوند
    /// </summary>
    public static class MetarangeNet
    {
        public sealed class Reply
        {
            public bool ok;
            public long code;
            public string body = "";
            public string error = "";
            public string url = "";

            /// <summary>متن کامل خطا برای Debug.LogError</summary>
            public string Detail()
                => "url=" + url + "  responseCode=" + code +
                   "  error=" + (string.IsNullOrEmpty(error) ? "(none)" : error) +
                   "  body=" + (string.IsNullOrEmpty(body) ? "(empty)" : body);
        }

        public static string Alternate(string url)
            => url.Replace("://localhost", "://127.0.0.1");

        public static IEnumerator PostJson(string serverUrl, string path, string json, Reply reply)
            => Send(UnityWebRequest.kHttpVerbPOST, serverUrl + path, json, reply);

        public static IEnumerator PutJson(string serverUrl, string path, string json, Reply reply)
            => Send(UnityWebRequest.kHttpVerbPUT, serverUrl + path, json, reply);

        public static IEnumerator Get(string serverUrl, string pathWithQuery, Reply reply)
            => Send(null, serverUrl + pathWithQuery, null, reply);

        static IEnumerator Send(string method, string url, string json, Reply reply)
        {
            string alt = Alternate(url);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string target = attempt == 0 ? url : alt;
                using (UnityWebRequest req = Build(method, target, json))
                {
                    yield return req.SendWebRequest();

                    reply.ok = req.result == UnityWebRequest.Result.Success;
                    reply.code = req.responseCode;
                    reply.url = target;
                    reply.error = req.error ?? "";
                    try { reply.body = req.downloadHandler != null ? req.downloadHandler.text ?? "" : ""; }
                    catch { reply.body = ""; }

                    bool canRetry = attempt == 0 && target != alt &&
                                    req.result == UnityWebRequest.Result.ConnectionError;
                    if (!canRetry) yield break;

                    Debug.LogWarning("[متارنج] اتصال به " + target + " ناموفق (" + req.error +
                                     ") — تلاش مجدد با 127.0.0.1 ...");
                }
            }
        }

        static UnityWebRequest Build(string method, string url, string json)
        {
            if (json == null)
            {
                UnityWebRequest get = UnityWebRequest.Get(url);
                get.timeout = 10;
                return get;
            }

            UnityWebRequest req = string.IsNullOrEmpty(method)
                ? new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
                : new UnityWebRequest(url, method);
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 10;
            return req;
        }
    }
}
