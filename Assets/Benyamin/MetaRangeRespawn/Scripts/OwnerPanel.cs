using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;   // UTF8Encoding برای ذخیرهٔ فایل لینک
using TMPro;
using RTLTMPro;   // پکیج com.nosuchstudio.rtltmpro — نمایش صحیح فارسی
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace MetaRange.Avatar
{
    public class OwnerPanel : MonoBehaviour
    {
        [Header("Owner")]
        [SerializeField] private Toggle ownerToggle;
        [SerializeField] private Transform avatar;

        [Header("Live Position")]
        [SerializeField] private RTLTextMeshPro livePosText;

        [Header("Register")]
        [Tooltip("نام یونیک موقعیت — اختیاری؛ خالی = نام خودکار pos_xxxxxx")]
        [SerializeField] private TMP_InputField positionNameInput;
        [SerializeField] private Button registerButton;
        [SerializeField] private RTLTextMeshPro registerStatusText;

        [Header("Result")]
        [SerializeField] private GameObject sectionResult;
        [SerializeField] private RTLTextMeshPro linkText;
        [SerializeField] private RawImage qrImage;
        [SerializeField] private Button downloadQrButton;
        [SerializeField] private Button copyLinkButton;
        [SerializeField] private Button downloadLinkButton;
        [SerializeField] private RTLTextMeshPro linkStatusText;

        [Header("Position List")]
        [SerializeField] private GameObject sectionPositionList;
        [SerializeField] private RTLTextMeshPro emptyListText;
        [SerializeField] private Transform cardContainer;
        [SerializeField] private GameObject cardPrefab;

        [Header("Config")]
        [SerializeField] private string serverUrl = "http://localhost:3000";
        [SerializeField] private string playBaseUrl = "https://metarange.adfam.com/play";

        [Header("QR Service")]
        [Tooltip("منبع اول: QR روی سرور خودمان (پیشنهادی — بدون وابستگی به سرویس بیرونی)")]
        [SerializeField] private bool useLocalServerQr = true;
        [Tooltip("سرویس اصلی QR — {DATA} با لینک Escape-شده جایگزین می‌شود")]
        [SerializeField] private string qrPrimaryTemplate =
            "https://api.qrserver.com/v1/create-qr-code/?size=256x256&data={DATA}";
        [Tooltip("سرویس جایگزین در صورت خطای سرویس اصلی")]
        [SerializeField] private string qrFallbackTemplate =
            "https://quickchart.io/qr?size=256&margin=2&text={DATA}";

        private bool isOwner;
        private string currentLink;
        private string currentSpawnId = "";
        private readonly List<GameObject> spawnedCards = new List<GameObject>();
        private readonly HashSet<string> knownIds = new HashSet<string>();

        /// <summary>کارتِ در حال ویرایش — استاتیک تا بین نمونه‌های مختلف OwnerPanel هم مشترک باشد</summary>
        private static PositionCardUI editingCard;

        private void Start()
        {
            if (ownerToggle != null) ownerToggle.onValueChanged.AddListener(OnOwnerChanged);
            if (registerButton != null) registerButton.onClick.AddListener(OnRegister);
            if (downloadQrButton != null) downloadQrButton.onClick.AddListener(OnDownloadQr);
            if (copyLinkButton != null) copyLinkButton.onClick.AddListener(OnCopyLink);
            if (downloadLinkButton != null) downloadLinkButton.onClick.AddListener(OnDownloadLink);

            SetOwnerUI(false);
            UpdateLinkButtonsState(false);

            EnsureAvatar("Start");
        }

        /// <summary>
        /// اطمینان از وجود آواتار معتبر: اگر رفرنس ابزار (Tool) به آبجکتی اشاره می‌کند که
        /// در زمان اجرا حذف/تعویض شده، آواتار را دوباره پیدا می‌کنیم
        /// (تگ Player، سپس نام‌های رایج).
        /// </summary>
        void EnsureAvatar(string reason)
        {
            if (avatar != null) return;

            GameObject found = null;
            try { found = GameObject.FindWithTag("Player"); }
            catch { /* تگ Player تعریف نشده */ }

            if (found == null)
            {
                string[] names = { "Player", "MetaRangeAvatar", "Avatar", "LocalPlayer", "XR Origin", "XR Rig" };
                for (int i = 0; i < names.Length && found == null; i++)
                    found = GameObject.Find(names[i]);
            }

            if (found != null)
            {
                avatar = found.transform;
                Debug.LogWarning("[OwnerPanel] آواتار خودکار وصل شد (" + reason + "): " + found.name +
                                 "  |  موقعیت: " + avatar.position.ToString("F2"));
            }
            else
            {
                Debug.LogError("[OwnerPanel] آواتار پیدا نشد (" + reason + ") — هیچ آبجکتی با تگ Player " +
                               "یا نام رایج وجود ندارد. روی Player تگ Player بگذارید یا فیلد Avatar " +
                               "را در Inspector دستی ست کنید.");
                LogSceneRootNames();
            }
        }

        /// <summary>فهرست آبجکت‌های ریشهٔ صحنه — برای تشخیص سریع مشکل</summary>
        void LogSceneRootNames()
        {
            var roots = SceneManager.GetActiveScene().GetRootGameObjects();
            var names = new System.Text.StringBuilder();
            int n = 0;
            for (int i = 0; i < roots.Length && n < 8; i++)
            {
                names.Append(roots[i].name);
                names.Append(roots[i].CompareTag("Player") ? "(Player*)" : "");
                names.Append(", ");
                n++;
            }
            Debug.Log("[OwnerPanel] آبجکت‌های ریشهٔ صحنه: " + names);
        }

        /// <summary>اتصال دستی آواتار در زمان اجرا (یا از اسکریپت بازی)</summary>
        public void SetAvatar(Transform t)
        {
            avatar = t;
            Debug.Log("[OwnerPanel] آواتار دستی ست شد: " + (t != null ? t.name : "null"));
        }

        /// <summary>اتصال خودکار آواتار با تگ Player (منوی راست‌کلیک در Inspector)</summary>
        [UnityEngine.ContextMenu("MetaRange/اتصال آواتار با تگ Player")]
        public void RebindAvatarByTag()
        {
            avatar = null;
            EnsureAvatar("RebindByTag");
        }

        /// <summary>آواتار فعلی (برای کارت‌های ویرایش)</summary>
        public bool HasAvatar => avatar != null;
        public Vector3 AvatarPosition => avatar != null ? avatar.position : Vector3.zero;
        /// <summary>چرخش آواتار بر حسب درجه — برای پیش‌نمایش زندهٔ Rx/Ry/Rz در کارت ویرایش</summary>
        public Vector3 AvatarRotation => avatar != null ? avatar.eulerAngles : Vector3.zero;
        public string AvatarName => avatar != null ? avatar.name : "—";

        private void OnDestroy()
        {
            if (ownerToggle != null) ownerToggle.onValueChanged.RemoveListener(OnOwnerChanged);
            if (registerButton != null) registerButton.onClick.RemoveListener(OnRegister);
            if (downloadQrButton != null) downloadQrButton.onClick.RemoveListener(OnDownloadQr);
            if (copyLinkButton != null) copyLinkButton.onClick.RemoveListener(OnCopyLink);
            if (downloadLinkButton != null) downloadLinkButton.onClick.RemoveListener(OnDownloadLink);
        }

        private void Update()
        {
            if (livePosText == null) return;

            // وضعیت واقعی تیک (نه پروندهٔ داخلی) — تا با Inspector هم‌خوان باشد
            bool ownerOn = ownerToggle != null ? ownerToggle.isOn : isOwner;
            if (!ownerOn) return;

            // اگر آواتار در زمان اجرا نامعتبر شد (حذف/تعویض صحنه توسط بازی)، دوباره پیدایش کن
            if (avatar == null) EnsureAvatar("Update");

            if (avatar == null)
            {
                livePosText.text = "موقعیت زنده: آواتار پیدا نشد\n(تگ Player یا فیلد Avatar را تنظیم کنید)";
                return;
            }

            Vector3 p = avatar.position;
            Vector3 e = avatar.eulerAngles;

            // InvariantCulture ⇒ همیشه نقطهٔ اعشار و رقم لاتین (بدون به‌هم‌ریختگی RTL)
            livePosText.text = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "موقعیت زنده ({0})\nX: {1:F2}   Y: {2:F2}   Z: {3:F2}\nچرخش: {4:F1} / {5:F1} / {6:F1}",
                avatar.name, p.x, p.y, p.z, e.x, e.y, e.z);
        }

        private void OnOwnerChanged(bool on)
        {
            isOwner = on;
            SetOwnerUI(on);
            if (on)
            {
                EnsureAvatar("OwnerToggle");
                StartCoroutine(RefreshList());
            }
        }

        private void SetOwnerUI(bool on)
        {
            if (livePosText != null) livePosText.gameObject.SetActive(on);
            if (registerButton != null) registerButton.gameObject.SetActive(on);
            if (sectionPositionList != null) sectionPositionList.SetActive(on);
            if (!on && sectionResult != null) sectionResult.SetActive(false);
        }

        private void OnRegister()
        {
            if (avatar == null)
            {
                Debug.LogWarning("[OwnerPanel] آواتار پیدا نشد (tag=Player)");
                SetRegisterStatus("آواتار پیدا نشد (tag=Player)", new Color(1f, 0.4f, 0.4f));
                return;
            }

            StartCoroutine(RegisterRoutine());
        }

        /// <summary>
        /// منطق دکمهٔ «ثبت موقعیت» (Register Position):
        /// ۱) محیط باید قبلاً از **پنل چپ** («ساخت فایل محیط») ساخته شده باشد
        /// ۲) نام یونیک موقعیت از `positionNameInput` گرفته می‌شود (خالی ⇒ خودکار)
        /// ۳) سپس add-position با همان شناسه زده می‌شود
        /// </summary>
        private IEnumerator RegisterRoutine()
        {
            string env = EnvironmentCreator.StoredEnvironmentName;
            if (string.IsNullOrEmpty(env))
            {
                const string msg = "اول از پنل چپ «ساخت فایل محیط» را بزنید";
                Debug.LogWarning("[OwnerPanel] " + msg);
                SetRegisterStatus(msg, new Color(1f, 0.75f, 0.3f));
                yield break;
            }

            // نام یونیک کاربر (یا خودکار) — همان کلید JSON و بخش spawn لینک
            string typed = positionNameInput != null ? positionNameInput.text : null;
            string posId = SanitizePositionName(typed);
            bool auto = string.IsNullOrEmpty(posId);
            if (auto) posId = "pos_" + Guid.NewGuid().ToString("N").Substring(0, 6);

            if (!auto && knownIds.Contains(posId))
            {
                const string msg = "نام تکراری است";
                Debug.LogWarning("[OwnerPanel] " + msg + ": " + posId);
                SetRegisterStatus(msg + " — یک نام دیگر انتخاب کنید", new Color(1f, 0.4f, 0.4f));
                yield break;
            }

            SetRegisterStatus("در حال ثبت…", new Color(0.8f, 0.9f, 1f));
            Debug.Log("[OwnerPanel] ثبت موقعیت: env=" + env + "  posId=" + posId + (auto ? "  (خودکار)" : ""));

            var body = new AddPositionBody
            {
                environmentName = env,
                positionId = posId,
                position = Vec3Data.From(avatar.position),
                rotation = QuatData.From(avatar.rotation)
            };

            yield return SendAdd(env, posId, body);
        }

        /// <summary>نام Canvas پنل چپ — فقط برای پیام راهنما به کاربر</summary>
        const string CreateEnvCanvasName = "MetaRange_CreateEnvCanvas";

        /// <summary>
        /// نام موقعیت را برای سرور آماده می‌کند: فقط حروف (لاتین/فارسی)، عدد، «_» و «-».
        /// فاصله‌ها به «_» تبدیل و کاراکترهای نامعتبر حذف می‌شوند.
        /// </summary>
        public static string SanitizePositionName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
                else if (char.IsWhiteSpace(c)) sb.Append('_');
            }

            string s = sb.ToString();
            if (s.Length > 40) s = s.Substring(0, 40);
            return s;
        }

        /// <summary>پیام کوتاه وضعیت زیر دکمهٔ ثبت</summary>
        public void SetRegisterStatus(string msg, Color color)
        {
            if (registerStatusText == null) return;
            registerStatusText.text = msg;
            registerStatusText.color = color;
        }

        /// <summary>فقط یک کارت هم‌زمان در حالت ویرایش باشد (ایستا ⇒ بین نمونه‌ها هم مشترک)</summary>
        public void NotifyEditStarted(PositionCardUI card)
        {
            if (editingCard != null && editingCard != card) editingCard.ExitEditMode();
            editingCard = card;
        }

        public void NotifyEditEnded(PositionCardUI card)
        {
            if (editingCard == card) editingCard = null;
        }

        private IEnumerator SendAdd(string env, string posId, AddPositionBody body)
        {
            string json = JsonUtility.ToJson(body);
            var reply = new MetarangeNet.Reply();

            // POST + Content-Type: application/json (بدنه واقعی، نه query)
            yield return MetarangeNet.PostJson(serverUrl, "/api/add-position", json, reply);

            // اگر سرور محیط را ندارد (ری‌استارت/پاک شدن data) → یک‌بار ساخت و تلاش مجدد
            if (reply.code == 404 &&
                reply.body.IndexOf("Environment not found", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Debug.LogWarning("[OwnerPanel] محیط «" + env + "» روی سرور نبود — ساخت مجدد و تلاش دوباره ...");
                EnvironmentCreator.ClearStoredName();

                var ensure = new MetarangeNet.Reply();
                yield return EnvironmentCreator.EnsureEnvironment(serverUrl, ensure);

                if (ensure.ok && !string.IsNullOrEmpty(EnvironmentCreator.StoredEnvironmentName))
                {
                    env = EnvironmentCreator.StoredEnvironmentName;
                    body.environmentName = env;
                    json = JsonUtility.ToJson(body);
                    Debug.Log("[OwnerPanel] تلاش مجدد ثبت: env=" + env + "  posId=" + posId);
                    var retry = new MetarangeNet.Reply();
                    yield return MetarangeNet.PostJson(serverUrl, "/api/add-position", json, retry);
                    reply = retry;
                }
            }

            if (reply.ok)
            {
                ShowResult(env, posId);
                StartCoroutine(RefreshList());
            }
            else
            {
                Debug.LogError("[OwnerPanel] خطا در ثبت موقعیت — " + reply.Detail());
            }
        }

        private void ShowResult(string env, string posId)
        {
            currentSpawnId = posId;
            currentLink = playBaseUrl + "?env=" + env + "&spawn=" + posId;

            if (linkText != null) linkText.text = currentLink;
            if (sectionResult != null) sectionResult.SetActive(true);
            UpdateLinkButtonsState(true);
            SetLinkStatus("آماده — کپی یا دانلود کنید", new Color(0.85f, 0.9f, 1f));
            if (qrImage != null) StartCoroutine(LoadQr(currentLink));
        }

        /// <summary>
        /// بارگذاری QR با زنجیرهٔ fallback:
        /// ① سرور خودمان (`/api/qr`) — پیشنهادی: بدون وابستگی و بدون محدودیت سرویس بیرونی
        /// ② و ③ سرویس‌های عمومی (اگر سرور در دسترس نباشد)
        /// در صورت شکست همه، لینک متنی همچنان معتبر است.
        /// </summary>
        private IEnumerator LoadQr(string data)
        {
            // ① QR روی سرور خودمان
            if (useLocalServerQr)
            {
                string localUrl = serverUrl + "/api/qr?data=" + UnityWebRequest.EscapeURL(data);
                bool okLocal = false;

                using (UnityWebRequest req = UnityWebRequestTexture.GetTexture(localUrl))
                {
                    req.timeout = 12;
                    TrySetUserAgent(req);
                    yield return req.SendWebRequest();

                    okLocal = req.result == UnityWebRequest.Result.Success;
                    if (okLocal)
                    {
                        if (qrImage != null) qrImage.texture = DownloadHandlerTexture.GetContent(req);
                        Debug.Log("[OwnerPanel] QR بارگذاری شد (سرور محلی: /api/qr)");
                        yield break;
                    }
                    Debug.LogWarning("[OwnerPanel] QR محلی ناموفق — " + ErrorDetail(req));
                }
            }

            // ② ③ سرویس‌های عمومی
            string[] templates = { qrPrimaryTemplate, qrFallbackTemplate };

            for (int i = 0; i < templates.Length; i++)
            {
                if (string.IsNullOrEmpty(templates[i])) continue;

                string url = templates[i].Replace("{DATA}", UnityWebRequest.EscapeURL(data));

                using (UnityWebRequest req = UnityWebRequestTexture.GetTexture(url))
                {
                    req.timeout = 12;
                    TrySetUserAgent(req);

                    yield return req.SendWebRequest();

                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        if (qrImage != null) qrImage.texture = DownloadHandlerTexture.GetContent(req);
                        Debug.Log("[OwnerPanel] QR بارگذاری شد (سرویس بیرونی #" + (i + 1) + ")");
                        yield break;
                    }

                    Debug.LogWarning("[OwnerPanel] سرویس QR #" + (i + 1) + " ناموفق — " + ErrorDetail(req));
                }
            }

            Debug.LogError("[OwnerPanel] بارگذاری QR از همهٔ منابع ناموفق بود. " +
                           "لینک متنی همچنان معتبر است: " + currentLink);
        }

        static void TrySetUserAgent(UnityWebRequest req)
        {
            // برخی سرویس‌های QR درخواست‌های غیرمرورگری را رد می‌کنند
            try { req.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"); }
            catch { }
        }

        static string ErrorDetail(UnityWebRequest req)
        {
            string body = "";
            try
            {
                if (req.downloadHandler != null)
                {
                    string t = req.downloadHandler.text;
                    if (!string.IsNullOrEmpty(t) && t.Length < 400 && t.IndexOf('\0') < 0) body = t;
                }
            }
            catch { }
            if (string.IsNullOrEmpty(body)) body = "(بدنه خالی یا باینری)";

            return "responseCode=" + req.responseCode +
                   "  error=" + (string.IsNullOrEmpty(req.error) ? "(none)" : req.error) +
                   "  body=" + body;
        }

        /// <summary>وقتی لینک خالی است دکمه‌ها غیرفعال می‌مانند</summary>
        private void UpdateLinkButtonsState(bool hasLink)
        {
            if (copyLinkButton != null) copyLinkButton.interactable = hasLink;
            if (downloadLinkButton != null) downloadLinkButton.interactable = hasLink;
        }

        private void SetLinkStatus(string msg, Color color)
        {
            if (linkStatusText == null) return;
            linkStatusText.text = msg;
            linkStatusText.color = color;
        }

        /// <summary>کپی لینک کامل در کلیپ‌بورد سیستم (Editor/Standone/Build)</summary>
        private void OnCopyLink()
        {
            if (string.IsNullOrEmpty(currentLink))
            {
                SetLinkStatus("ابتدا یک موقعیت ثبت کنید", new Color(1f, 0.75f, 0.3f));
                return;
            }

            try
            {
                GUIUtility.systemCopyBuffer = currentLink;
                SetLinkStatus("لینک کپی شد", new Color(0.4f, 1f, 0.55f));
                Debug.Log("[OwnerPanel] لینک کپی شد: " + currentLink);
            }
            catch (Exception e)
            {
                // در WebGL یا مرورگرهای محدود، کلیپ‌بورد در دسترس نیست
                SetLinkStatus("کپی خودکار ممکن نشد — لینک را دستی انتخاب کنید", new Color(1f, 0.75f, 0.3f));
                Debug.LogWarning("[OwnerPanel] کپی کلیپ‌بورد ناموفق: " + e.Message + "  |  لینک: " + currentLink);
            }
        }

        /// <summary>ذخیرهٔ لینک در فایل متنی (Editor: SaveFilePanel، Build: persistentDataPath)</summary>
        private void OnDownloadLink()
        {
            if (string.IsNullOrEmpty(currentLink))
            {
                SetLinkStatus("ابتدا یک موقعیت ثبت کنید", new Color(1f, 0.75f, 0.3f));
                return;
            }

            string safeEnv = SanitizePositionName(EnvironmentCreator.StoredEnvironmentName);
            if (string.IsNullOrEmpty(safeEnv)) safeEnv = "env";
            string fileName = "spawn_" + safeEnv + "_" + SanitizePositionName(currentSpawnId) + ".txt";

            string path;
#if UNITY_EDITOR
            path = UnityEditor.EditorUtility.SaveFilePanel("ذخیره لینک ریسپان", "", fileName, "txt");
            if (string.IsNullOrEmpty(path)) { SetLinkStatus("ذخیره لغو شد", new Color(0.85f, 0.9f, 1f)); return; }
#else
            path = System.IO.Path.Combine(Application.persistentDataPath, fileName);
#endif
            try
            {
                System.IO.File.WriteAllText(path, currentLink, new UTF8Encoding(false));
                SetLinkStatus("فایل لینک ذخیره شد", new Color(0.4f, 1f, 0.55f));
                Debug.Log("[OwnerPanel] فایل لینک ذخیره شد: " + path);
            }
            catch (Exception e)
            {
                SetLinkStatus("خطا در ذخیره فایل", new Color(1f, 0.4f, 0.4f));
                Debug.LogError("[OwnerPanel] خطا در ذخیرهٔ لینک: " + e.Message);
            }
        }

        private void OnDownloadQr()
        {
            if (qrImage == null || !(qrImage.texture is Texture2D tex))
            {
                Debug.LogWarning("[OwnerPanel] QR هنوز دانلود نشده است");
                return;
            }

            byte[] png = tex.EncodeToPNG();

#if UNITY_EDITOR
            // در ادیتور: انتخاب مسیر ذخیره
            string path = UnityEditor.EditorUtility.SaveFilePanel(
                "ذخیره QR کد ریسپان", "", "qr_spawn", "png");
            if (string.IsNullOrEmpty(path)) return;   // انصراف کاربر
#else
            // در بیلد: پوشهٔ دائمی بازیکن
            string path = System.IO.Path.Combine(Application.persistentDataPath, "qr_spawn.png");
#endif
            try
            {
                System.IO.File.WriteAllBytes(path, png);
                Debug.Log("[OwnerPanel] QR ذخیره شد: " + path);
            }
            catch (Exception e)
            {
                Debug.LogError("[OwnerPanel] خطا در ذخیره QR: " + e.Message);
            }
        }

        public void UpdatePosition(string env, string posId, Vector3 newPos, Quaternion newRot, bool moveAvatar = false)
        {
            StartCoroutine(SendUpdate(env, posId, newPos, newRot, moveAvatar));
        }

        /// <summary>نسخهٔ قابل await برای کارت ویرایش (پس از پایان، کارت رفرش می‌شود)</summary>
        public IEnumerator UpdatePositionRoutine(string env, string posId, Vector3 newPos, Quaternion newRot, bool moveAvatar)
        {
            yield return SendUpdate(env, posId, newPos, newRot, moveAvatar);
        }

        /// <summary>
        /// تغییر نام یونیک موقعیت (Rename) — کلید JSON و بخش spawn لینک عوض می‌شود،
        /// مختصات دست‌نخورده می‌مانند.
        /// </summary>
        public IEnumerator RenamePosition(string env, string oldId, string newId, Action<bool> onDone)
        {
            bool ok = false;

            if (string.IsNullOrEmpty(newId) || newId == oldId)
            {
                onDone?.Invoke(true);
                yield break;
            }

            var body = new RenamePositionBody
            {
                environmentName = env,
                positionId = oldId,
                newPositionId = newId
            };

            var reply = new MetarangeNet.Reply();
            yield return MetarangeNet.PutJson(serverUrl, "/api/rename-position", JsonUtility.ToJson(body), reply);

            if (reply.ok)
            {
                ok = true;
                Debug.Log("[OwnerPanel] نام موقعیت تغییر کرد: " + oldId + " → " + newId);
                SetRegisterStatus("نام تغییر کرد: " + newId, new Color(0.4f, 1f, 0.55f));
            }
            else if (reply.code == 409)
            {
                Debug.LogWarning("[OwnerPanel] نام «" + newId + "» تکراری است");
                SetRegisterStatus("نام تکراری است", new Color(1f, 0.4f, 0.4f));
            }
            else
            {
                Debug.LogError("[OwnerPanel] تغییر نام ناموفق — " + reply.Detail());
                SetRegisterStatus("خطا در تغییر نام", new Color(1f, 0.4f, 0.4f));
            }

            StartCoroutine(RefreshList());
            onDone?.Invoke(ok);
        }

        private IEnumerator SendUpdate(string env, string posId, Vector3 newPos, Quaternion newRot, bool moveAvatar)
        {
            var body = new AddPositionBody
            {
                environmentName = env,
                positionId = posId,
                position = Vec3Data.From(newPos),
                rotation = QuatData.From(newRot)
            };

            string json = JsonUtility.ToJson(body);
            var reply = new MetarangeNet.Reply();

            yield return MetarangeNet.PutJson(serverUrl, "/api/update-position", json, reply);

            if (reply.ok)
            {
                // آواتار باید واقعاً به نقطهٔ ویرایش‌شده برود (مقاوم به CharacterController/Rigidbody)
                if (moveAvatar) MoveAvatarTo(newPos, newRot);

                Debug.Log("[OwnerPanel] موقعیت بروزرسانی شد: " + posId +
                          (moveAvatar ? "  → آواتار منتقل شد به " + newPos.ToString("F2") : ""));
                SetRegisterStatus("ویرایش ذخیره شد: " + posId, new Color(0.4f, 1f, 0.55f));
                StartCoroutine(RefreshList());
            }
            else
            {
                Debug.LogError("[OwnerPanel] خطا در بروزرسانی — " + reply.Detail());
                SetRegisterStatus("خطا در ویرایش", new Color(1f, 0.4f, 0.4f));
            }
        }

        /// <summary>
        /// جابه‌جایی مطمئن آواتار:
        /// CharacterController و Rigidbody بعد از SetPositionAndRotation معمولاً
        /// موقعیت را در فریم بعدی برمی‌گردانند، پس موقتاً غیرفعال و سپس دوباره فعال می‌شوند.
        /// </summary>
        public void MoveAvatarTo(Vector3 pos, Quaternion rot)
        {
            if (avatar == null)
            {
                Debug.LogWarning("[OwnerPanel] آواتار برای جابه‌جایی تعریف نشده است");
                return;
            }

            CharacterController cc = avatar.GetComponent<CharacterController>();
            if (cc != null && cc.enabled) cc.enabled = false;

            Rigidbody rb = avatar.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                if (rb.isKinematic) rb.position = pos;
                else rb.MovePosition(pos);
            }

            avatar.SetPositionAndRotation(pos, rot);

            if (cc != null && !cc.enabled) cc.enabled = true;

            Debug.Log("[OwnerPanel] آواتار منتقل شد → " + pos.ToString("F2") +
                      (cc != null ? "  (CharacterPlayer باز فعال شد)" : "") +
                      (rb != null ? "  (Rigidbody اعمال شد)" : ""));
        }

        /// <summary>تبدیل متن فیلد به عدد — مستقل از جداکنندهٔ اعشار سیستم (نقطه/ممیز/کاما)</summary>
        public static float ParseFloat(string text, float fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;

            // جداکننده‌های ممکن: نقطهٔ لاتین، ممیز عربی «٫» (U+066B)، کاما «،» (U+060C) و فاصله
            var sb = new StringBuilder(text.Trim().Length);
            foreach (char c in text.Trim())
            {
                if (char.IsDigit(c) || c == '-' || c == '+' || c == '.') sb.Append(c);
                else if (c == '\u066B' || c == '\u060C' || c == ',' || char.IsWhiteSpace(c) ||
                         c == '\u200C' || c == '\u200F') sb.Append('.');
                // سایر کاراکترها (مثل حروف) نادیده گرفته می‌شوند
            }

            string s = sb.ToString();
            if (s.Length > 0)
            {
                if (float.TryParse(s, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out float v)) return v;
            }

            if (float.TryParse(text.Trim(), System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.CurrentCulture, out float v2)) return v2;

            Debug.LogWarning("[OwnerPanel] تبدیل عدد ناموفق: \"" + text + "\"");
            return fallback;
        }

        public IEnumerator RefreshList()
        {
            string env = EnvironmentCreator.StoredEnvironmentName;
            if (string.IsNullOrEmpty(env)) yield break;

            var reply = new MetarangeNet.Reply();
            yield return MetarangeNet.Get(serverUrl,
                "/api/list-positions?env=" + UnityWebRequest.EscapeURL(env), reply);

            if (!reply.ok)
            {
                Debug.LogError("[OwnerPanel] خطا در دریافت لیست — " + reply.Detail());
                yield break;
            }

            var dict = ParseDict(reply.body);
            ClearCards();
            knownIds.Clear();
            foreach (string key in dict.Keys) knownIds.Add(key);

            if (dict.Count == 0)
            {
                if (emptyListText != null) emptyListText.gameObject.SetActive(true);
                yield break;
            }

            if (emptyListText != null) emptyListText.gameObject.SetActive(false);

            foreach (var kv in dict)
            {
                if (cardPrefab == null || cardContainer == null) continue;

                GameObject card = Instantiate(cardPrefab, cardContainer);
                card.SetActive(true);

                var cardUI = card.GetComponent<PositionCardUI>();
                if (cardUI != null)
                    cardUI.Setup(env, kv.Key, kv.Value, this);

                spawnedCards.Add(card);
            }
        }

        private void ClearCards()
        {
            foreach (var c in spawnedCards)
                if (c != null) Destroy(c);
            spawnedCards.Clear();
            editingCard = null;   // کارت مرجع دیگر وجود ندارد
        }

        /// <summary>
        /// پارسر پاسخ `GET /api/list-positions`.
        /// پاسخ سرور یک شیء ساده است: { "نام۱": {...}, "نام۲": {...} }
        /// نام‌ها دلخواه‌اند (فارسی یا لاتین)؛ بنابراین هر کلیدی پذیرفته می‌شود
        /// — نه فقط کلیدهایی که با "pos_" شروع شوند.
        /// اگر پاسخ به‌صورت {"positions": {...}} هم باشد، محتوای داخلی خوانده می‌شود.
        /// </summary>
        public static Dictionary<string, PositionEntry> ParseDict(string json)
        {
            var result = new Dictionary<string, PositionEntry>();
            if (string.IsNullOrEmpty(json)) return result;

            int root = json.IndexOf('{');
            if (root < 0) return result;

            ScanEntries(json, root, result, 0);
            return result;
        }

        /// <summary>اسکن جفت‌های کلید/مقدار در عمق مشخص (عمق ۰ = همان شیء)</summary>
        private static void ScanEntries(string json, int objStart, Dictionary<string, PositionEntry> result, int depth)
        {
            int i = objStart + 1;

            while (i < json.Length)
            {
                // پرش تا کلید بعدی
                while (i < json.Length && (char.IsWhiteSpace(json[i]) || json[i] == ',')) i++;
                if (i >= json.Length || json[i] == '}') break;
                if (json[i] != '"') break;   // ساختار نامعتبر

                if (!TryReadString(json, ref i, out string key)) break;

                // تا کلید ':'
                while (i < json.Length && json[i] != ':') i++;
                if (i >= json.Length) break;
                i++;
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i >= json.Length) break;

                // مقدار باید شیء باشد
                if (json[i] != '{') break;
                int valueStart = i;
                if (!TrySkipObject(json, ref i)) break;

                string value = json.Substring(valueStart, i - valueStart);

                // پشتیبانی از پاسخ پیچیده: {"positions": {...}}
                if (key == "positions" && depth == 0)
                {
                    ScanEntries(json, valueStart, result, depth + 1);
                    continue;
                }

                try
                {
                    var entry = JsonUtility.FromJson<PositionEntry>(value);
                    if (entry != null && entry.position != null && !result.ContainsKey(key))
                        result[key] = entry;
                }
                catch { }
            }
        }

        /// <summary>خواندن یک رشتهٔ JSON (با هندل کردن escape) و قرار دادن i بعد از رشته</summary>
        private static bool TryReadString(string json, ref int i, out string value)
        {
            value = string.Empty;
            int start = i + 1;
            int j = start;
            var sb = new StringBuilder();

            while (j < json.Length)
            {
                char c = json[j];
                if (c == '\\' && j + 1 < json.Length)
                {
                    sb.Append(json[j + 1]);   // \n \" \\ و …
                    j += 2;
                    continue;
                }
                if (c == '"')
                {
                    value = sb.Length > 0 ? sb.ToString() : json.Substring(start, j - start);
                    i = j + 1;
                    return true;
                }
                sb.Append(c);
                j++;
            }
            return false;
        }

        /// <summary>رد کردن یک شیءٔ JSON (با احتساب رشته‌ها) و قرار دادن i بعد از '}'</summary>
        private static bool TrySkipObject(string json, ref int i)
        {
            int depth = 0;
            bool inString = false;
            int j = i;

            while (j < json.Length)
            {
                char c = json[j];
                if (inString)
                {
                    if (c == '\\') j++;
                    else if (c == '"') inString = false;
                }
                else
                {
                    if (c == '"') inString = true;
                    else if (c == '{') depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0) { i = j + 1; return true; }
                    }
                }
                j++;
            }
            return false;
        }
    }
}
