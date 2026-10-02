using System;
using System.Collections;
using System.Text;
using TMPro;
using RTLTMPro;   
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace MetaRange.Avatar
{
    public class EnvironmentCreator : MonoBehaviour
    {
        [Header("UI")]
        [SerializeField] private TMP_InputField nameInput;
        [SerializeField] private Button createButton;
        [SerializeField] private RTLTextMeshPro resultText;

        // آدرس سرور از تنظیم مرکزی می‌آید: MetaRangeConfigSource در صحنه (Tools ▸ متارنج ▸ راه‌اندازی)

        public static string StoredEnvironmentName { get; private set; } = string.Empty;

        /// <summary>پاک کردن نام ذخیره‌شده (وقتی سرور محیط را ندارد)</summary>
        public static void ClearStoredName()
        {
            StoredEnvironmentName = string.Empty;
        }

        /// <summary>
        /// قفل کردن Context مشترک روی یک env مشخص.
        /// بریج لابی بعد از کلیک روی دکمهٔ محیط این را صدا می‌زند تا OwnerPanel،
        /// لیست کارت‌ها، ثبت موقعیت و لینک خروجی همگی روی همان env کار کنند.
        /// نام ورودی با قاعدهٔ سرور یکسان‌سازی می‌شود.
        /// </summary>
        public static string LockStoredName(string envName)
        {
            string name = NormalizeEnvironmentName(envName);
            if (string.IsNullOrEmpty(name)) return StoredEnvironmentName;

            StoredEnvironmentName = name;
            return name;
        }

        /// <summary>
        /// تضمین آماده بودن محیط — از OwnerPanel قابل فراخوانی (بدون UI).
        /// همیشه POST /api/create-env می‌زند (idempotent):
        ///   201 → ساخته شد  |  409 → از قبل بود (هر دو موفق)
        /// نام (اگر از قبل ذخیره شده یا خودکار ساخته شود) در StoredEnvironmentName می‌ماند.
        /// خطا: reply.ok=false با Detail() برای لاگ.
        /// </summary>
        public static IEnumerator EnsureEnvironment(string server, MetarangeNet.Reply reply)
        {
            // نام موجود را نگه می‌داریم؛ اگر نبود یک نام خودکار می‌سازیم
            string name = !string.IsNullOrEmpty(StoredEnvironmentName)
                ? StoredEnvironmentName
                : ("env_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));

            string json = JsonUtility.ToJson(new CreateEnvBody(name));
            yield return MetarangeNet.PostJson(server, "/api/create-env", json, reply);

            if (reply.code == 201 || reply.code == 409)
            {
                StoredEnvironmentName = name;
                reply.ok = true;
                Debug.Log("[EnvironmentCreator] محیط آماده: " + name + "  (code=" + reply.code + ")");
            }
            else
            {
                reply.ok = false;
                Debug.LogError("[EnvironmentCreator] create-env ناموفق — " + reply.Detail());
            }
        }

        /// <summary>
        /// تضمین آماده بودن محیط با یک نام مشخص (مثلاً کد ساختمانِ دکمهٔ لابی).
        /// 201 → ساخته شد | 409 → از قبل بود | هر دو موفق‌اند و idempotent هستند.
        /// نام روی StoredEnvironmentName قفل می‌شود تا OwnerPanel و لینک‌سازی همان را ببینند.
        /// نام با safeName سرور هم‌خوان است: حروف/رقم/ـ/_ و محدودهٔ فارسی (U+0600-U+06FF)،
        /// بدون فاصله و بدون نیم‌فاصله. اگر سرور نام را رد کند (400) شکست خورده و قفل نمی‌شود.
        /// </summary>
        public static IEnumerator EnsureEnvironment(string server, string envName, MetarangeNet.Reply reply)
        {
            string name = NormalizeEnvironmentName(envName);

            if (string.IsNullOrEmpty(name))
            {
                reply.ok = false;
                reply.error = "Environment name is empty.";
                Debug.LogError("[EnvironmentCreator] نام محیط خالی است — create-env صدا زده نشد.");
                yield break;
            }

            string json = JsonUtility.ToJson(new CreateEnvBody(name));
            yield return MetarangeNet.PostJson(server, "/api/create-env", json, reply);

            if (reply.code == 201 || reply.code == 409)
            {
                StoredEnvironmentName = name;
                reply.ok = true;
                Debug.Log(
                    "[EnvironmentCreator] محیط «" + name + "» آماده است" +
                    (reply.code == 409 ? " (از قبل وجود داشت)" : " (ساخته شد)"));
            }
            else
            {
                reply.ok = false;
                Debug.LogError("[EnvironmentCreator] create-env ناموفق — " + reply.Detail());
            }
        }

        /// <summary>نام محیط را برای سرور Node امن می‌کند (trim + حذف کاراکترهای غیرمجاز).</summary>
        public static string NormalizeEnvironmentName(string envName)
        {
            if (string.IsNullOrWhiteSpace(envName)) return string.Empty;

            var builder = new StringBuilder(envName.Trim().Length);
            foreach (char c in envName.Trim())
            {
                bool latin = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                bool persian = c >= '\u0600' && c <= '\u06FF';
                bool mark = c == '-' || c == '_';

                if (latin || persian || mark) builder.Append(c);
            }

            if (builder.Length == 0) return string.Empty;
            if (builder.Length > 64) builder.Length = 64;
            return builder.ToString();
        }

        private void Awake()
        {
            if (createButton != null)
                createButton.onClick.AddListener(OnCreateClicked);
        }

        private void OnDestroy()
        {
            if (createButton != null)
                createButton.onClick.RemoveListener(OnCreateClicked);
        }

        private void OnCreateClicked()
        {
            string name = nameInput != null && !string.IsNullOrWhiteSpace(nameInput.text)
                ? nameInput.text.Trim()
                : "env_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

            StartCoroutine(CreateRoutine(name));
        }

        private IEnumerator CreateRoutine(string envName)
        {
            if (createButton != null) createButton.interactable = false;

            string json = JsonUtility.ToJson(new CreateEnvBody(envName));
            var reply = new MetarangeNet.Reply();

            // POST با بدنه JSON واقعی + هدر application/json
            // (در صورت شکست localhost، توسط MetarangeNet یک بار با 127.0.0.1 تلاش می‌شود)
            yield return MetarangeNet.PostJson(MetaRangeConfig.ServerUrl, "/api/create-env", json, reply);

            if (createButton != null) createButton.interactable = true;

            if (reply.ok)
            {
                StoredEnvironmentName = envName;
                SetResult("محیط «" + envName + "» ساخته شد", Color.green);
            }
            else if (reply.code == 409)
            {
                StoredEnvironmentName = envName;
                SetResult("محیط از قبل وجود دارد", Color.yellow);
            }
            else
            {
                string msg = reply.body;
                try
                {
                    var eb = JsonUtility.FromJson<ErrorBody>(reply.body);
                    if (eb != null && !string.IsNullOrEmpty(eb.error))
                        msg = eb.error;
                }
                catch { }
                SetResult("خطا: " + msg, Color.red);
                Debug.LogError("[EnvironmentCreator] ساخت محیط ناموفق — " + reply.Detail());
            }
        }

        private void SetResult(string msg, Color color)
        {
            if (resultText != null)
            {
                resultText.text = msg;
                resultText.color = color;
            }
            Debug.Log("[EnvironmentCreator] " + msg);
        }
    }
}
