using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace MetaRange.Avatar
{
    public class SpawnFromURL : MonoBehaviour
    {
        // آدرس سرور از تنظیم مرکزی: MetaRangeConfigSource
        [SerializeField] private Transform avatarTransform;
        [SerializeField] private Vector3 defaultPosition = new Vector3(0f, 1f, 0f);
        [SerializeField] private Quaternion defaultRotation = Quaternion.identity;

        public Transform AvatarTransform => avatarTransform;

        public void SetAvatar(Transform t) { avatarTransform = t; }
        public void SetDefaults(Vector3 pos, Quaternion rot)
        {
            defaultPosition = pos;
            defaultRotation = rot;
        }

        // =============================================================
        // API عمومی برای برنامهٔ اصلی (Integration Hooks)
        // اگر بازی خودش Scene Loader دارد، بعد از لود صحنه این را صدا بزند.
        // =============================================================

        /// <summary>نام محیط و شناسهٔ موقعیت خوانده‌شده از URL جاری (اگر وجود داشته باشد)</summary>
        public static bool TryGetUrlParams(out string env, out string spawn)
        {
            env = string.Empty;
            spawn = string.Empty;
            string url = GetCurrentUrl();
            if (string.IsNullOrEmpty(url)) return false;

            env = GetParam(url, "env");
            spawn = GetParam(url, "spawn");
            return !string.IsNullOrEmpty(env) && !string.IsNullOrEmpty(spawn);
        }

        /// <summary>
        /// اعمال اسپان از پارامترهای URL جاری.
        /// برای بازی‌هایی که خودشان صحنه را لود می‌کنند: بعد از لود، این را صدا بزنید.
        /// </summary>
        public bool ApplySpawnFromUrl()
        {
            if (TryGetUrlParams(out string env, out string spawn))
            {
                Debug.Log("[SpawnFromURL] ApplySpawnFromUrl: env=" + env + " spawn=" + spawn);
                StartCoroutine(FetchSpawn(env, spawn));
                return true;
            }

            Debug.LogWarning("[SpawnFromURL] پارامتر env/spawn در URL یافت نشد — اسپان در موقعیت پیش‌فرض.");
            Spawn(defaultPosition, defaultRotation);
            return false;
        }

        /// <summary>اسپان مستقیم با مقادیر مشخص (وقتی برنامهٔ اصلی خودش env/spawn را از URL خوانده)</summary>
        public Coroutine ApplySpawn(string env, string spawn)
        {
            return StartCoroutine(FetchSpawn(env, spawn));
        }

        private void Start()
        {
            if (avatarTransform == null)
            {
                var p = GameObject.FindWithTag("Player");
                if (p != null) avatarTransform = p.transform;
            }
            ParseAndSpawn();
        }

        private void ParseAndSpawn()
        {
            ApplySpawnFromUrl();
        }

        /// <summary>URL جاری: WebGL از مرورگر، و در ادیتور/بیلد از آرگومان خط فرمان url=...</summary>
        public static string GetCurrentUrl()
        {
            string url = Application.absoluteURL;

#if !UNITY_WEBGL || UNITY_EDITOR
            if (string.IsNullOrEmpty(url))
            {
                foreach (string arg in Environment.GetCommandLineArgs())
                {
                    if (arg.StartsWith("url="))
                    {
                        url = arg.Substring(4);
                        break;
                    }
                }
            }
#endif
            return url ?? string.Empty;
        }

        private static string GetParam(string url, string key)
        {
            int i = url.IndexOf(key + "=", StringComparison.Ordinal);
            if (i < 0) return string.Empty;

            i += key.Length + 1;
            int j = i;
            while (j < url.Length && url[j] != '&' && url[j] != '#') j++;

            return UnityWebRequest.UnEscapeURL(url.Substring(i, j - i));
        }

        private IEnumerator FetchSpawn(string env, string spawn)
        {
            // در پروژه‌های شبکه (Network_A / برنچ gRPC)، بریج متارنج مسئول جای‌گذاری
            // local player است. اینجا نباید آواتار آفلاین/کپسول را جابه‌جا کنیم.
            if (MetaRangeNetworkSpawnBridge.IsActive)
            {
                Debug.Log("[SpawnFromURL] بریج شبکه فعال است ⇒ ادغام با هوک Network_A انجام می‌شود.");
                yield break;
            }

            string path = "/api/get-position?env="
                          + UnityWebRequest.EscapeURL(env) + "&spawn="
                          + UnityWebRequest.EscapeURL(spawn);

            var reply = new MetarangeNet.Reply();
            yield return MetarangeNet.Get(MetaRangeConfig.ServerUrl, path, reply);

            if (reply.ok)
            {
                try
                {
                    var entry = JsonUtility.FromJson<PositionEntry>(reply.body);
                    if (entry != null && entry.position != null)
                    {
                        Quaternion rot = (entry.rotation != null)
                            ? entry.rotation.ToQuaternion()
                            : Quaternion.identity;
                        Spawn(entry.position.ToVector3(), rot);
                        Debug.Log("[SpawnFromURL] اسپان در " + entry.position.ToVector3());
                        yield break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError("[SpawnFromURL] خطا در پارس: " + ex.Message);
                }
            }
            else
            {
                Debug.LogError("[SpawnFromURL] خطا در API — " + reply.Detail());
            }

            Spawn(defaultPosition, defaultRotation);
        }

        private void Spawn(Vector3 pos, Quaternion rot)
        {
            if (avatarTransform != null)
            {
                avatarTransform.SetPositionAndRotation(pos, rot);
            }
            else
            {
                Debug.Log("[SpawnFromURL] آواتار پیدا نشد. هدف: " + pos);
            }
        }
    }
}
