using UnityEngine;

namespace MetaRange.Avatar
{
    /// <summary>
    /// تنظیم مرکزی متارنج.
    /// <para>
    /// آدرس سرور Node و آدرس لینک ورود به بازی، هر دو از همین‌جا خوانده می‌شوند؛
    /// دیگر لازم نیست در Inspector چند اسکریپت جدا عوض شوند.
    /// تنها جایی که در صحنه ویرایش می‌شود، آبجکت <see cref="MetaRangeConfigSource"/> است.
    /// </para>
    /// </summary>
    public static class MetaRangeConfig
    {
        /// <summary>پیش‌فرض ساخت (آخرین fallback اگر هیچ Config صحنه‌ای نبود)</summary>
        public const string DefaultServerUrl = "http://217.218.238.201:4000";

        /// <summary>پایهٔ لینک ورود به بازی</summary>
        public const string DefaultPlayBaseUrl = "https://dev-world-3d.metarang.com/game";

        static string serverUrl = DefaultServerUrl;
        static string playBaseUrl = DefaultPlayBaseUrl;
        static bool configuredFromScene;
        static bool warnedMissing;

        /// <summary>آدرس سرور متارنج — همهٔ اسکریپت‌ها از همین می‌خوانند</summary>
        public static string ServerUrl
        {
            get
            {
                WarnIfUnconfigured();
                return serverUrl;
            }
        }

        /// <summary>پایهٔ لینک — همهٔ اسکریپت‌ها از همین می‌خوانند</summary>
        public static string PlayBaseUrl
        {
            get
            {
                WarnIfUnconfigured();
                return playBaseUrl;
            }
        }

        /// <summary>آیا مقدارها واقعاً از آبجکت Config صحنه آمده‌اند؟</summary>
        public static bool ConfiguredFromScene => configuredFromScene;

        /// <summary>مقداردهی — فقط <see cref="MetaRangeConfigSource"/> این را صدا می‌زند</summary>
        public static void Apply(string newServerUrl, string newPlayBaseUrl)
        {
            string s = TrimTrailingSlash(newServerUrl);
            if (!string.IsNullOrEmpty(s)) serverUrl = s;

            string p = TrimTrailingSlash(newPlayBaseUrl);
            if (!string.IsNullOrEmpty(p)) playBaseUrl = p;

            configuredFromScene = true;
        }

        /// <summary>پایهٔ لینک با اسلش انتهایی حذف‌شده (برای ساخت «?env=…»)</summary>
        public static string TrimTrailingSlash(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            string v = url.Trim();
            while (v.EndsWith("/")) v = v.Substring(0, v.Length - 1);
            return v;
        }

        /// <summary>
        /// اگر هیچ Config صحنه‌ای پیدا نشود یک‌بار هشدار راهنما می‌دهیم —
        /// نه سکوت، نه 127.0.0.1 پنهان.
        /// </summary>
        static void WarnIfUnconfigured()
        {
            if (configuredFromScene || warnedMissing) return;
            warnedMissing = true;
            Debug.LogWarning(
                "[متارنج] MetaRangeConfigSource در صحنه پیدا نشد ⇒ از مقدار پیش‌فرض ساخت استفاده می‌شود:\n" +
                "  serverUrl    = " + serverUrl + "\n" +
                "  playBaseUrl  = " + playBaseUrl + "\n" +
                "  برای تغییر فقط یک‌بار: یک GameObject با MetaRangeConfigSource بسازید و مقادیرش را " +
                "در Inspector ست کنید (یا یک‌بار «Tools ▸ متارنج ▸ راه‌اندازی سیستم ریسپان آواتار» را اجرا کنید).");
        }

        /// <summary>خلاصهٔ وضعیت برای لاگ/منوی تشخیصی</summary>
        public static string Describe()
        {
            return "serverUrl=" + serverUrl +
                   "  |  playBaseUrl=" + playBaseUrl +
                   "  |  منبع=" + (configuredFromScene ? "Config صحنه" : "پیش‌فرض ساخت");
        }
    }

    /// <summary>
    /// تنها آبجکتی که در Inspector ویرایش می‌شود.
    /// مقادیرش در <c>Awake</c> به <see cref="MetaRangeConfig"/> منتقل می‌شود
    /// و چون روی ریشهٔ دائمی قرار می‌گیرد، بین صحنه‌ها گم نمی‌شود.
    /// </summary>
    [AddComponentMenu("MetaRange/Config (تنظیم مرکزی سرور)")]
    public class MetaRangeConfigSource : MonoBehaviour
    {
        [Header("تنظیم مرکزی متارنج — فقط همین‌جا ویرایش شود")]
        [Tooltip("آدرس سرور Node متارنج. همهٔ اسکریپت‌ها از همین می‌خوانند.")]
        [SerializeField] private string serverUrl = MetaRangeConfig.DefaultServerUrl;

        [Tooltip("پایهٔ لینک ورود به بازی (env/spawn به آن اضافه می‌شود).")]
        [SerializeField] private string playBaseUrl = MetaRangeConfig.DefaultPlayBaseUrl;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Push();

            Debug.Log("[متارنج] تنظیم مرکزی اعمال شد | " + MetaRangeConfig.Describe() +
                      "  (برگرفته از: " + name + ")");
        }

        private void OnValidate()
        {
            if (Application.isPlaying) Push();
        }

        void Push()
        {
            MetaRangeConfig.Apply(serverUrl, playBaseUrl);
        }

        /// <summary>آدرس سرورِ همین آبجکت (فقط برای نمایش در ادیتور)</summary>
        public string ServerUrl => serverUrl;

        /// <summary>پایهٔ لینکِ همین آبجکت (فقط برای نمایش در ادیتور)</summary>
        public string PlayBaseUrl => playBaseUrl;
    }
}
