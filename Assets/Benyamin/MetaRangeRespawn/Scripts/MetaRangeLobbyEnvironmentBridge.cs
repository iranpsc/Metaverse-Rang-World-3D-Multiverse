using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;

namespace MetaRange.Avatar
{
    /// <summary>
    /// پاسخ <c>GET /api/list-positions?env=…</c> یک شیء JSON است که کلید هر نقطه،
    /// بنابراین اینجا یک پارسر کوچک و مستقل (بدون وابستگی بیرونی) نوشته شده است.
    /// بنابراین اینجا یک پارسر کوچک و مستقل (بدون وابستگی بیرونی) نوشته شده است.
    /// فرمت مورد انتظار همان خروجی <c>JSON.stringify</c> در سرور Node است.
    /// </summary>
    public static class MetaRangeSpawnList
    {
        //* این تابع بدنهٔ پاسخ لیست موقعیت‌ها را به جفت‌های «شناسه ⇒ موقعیت» تبدیل می‌کند.
        public static bool TryParseEntries(string json, out List<KeyValuePair<string, PositionEntry>> entries)
        {
            entries = new List<KeyValuePair<string, PositionEntry>>();
            if (string.IsNullOrWhiteSpace(json)) return false;

            int index = 0;
            SkipWhitespace(json, ref index);
            if (index >= json.Length || json[index] != '{') return false;
            index++;

            SkipWhitespace(json, ref index);
            while (index < json.Length && json[index] != '}')
            {
                string key;
                if (!TryReadString(json, ref index, out key)) return false;

                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != ':') return false;
                index++;

                SkipWhitespace(json, ref index);
                PositionEntry entry;
                if (!TryReadEntry(json, ref index, out entry)) return false;

                entries.Add(new KeyValuePair<string, PositionEntry>(key, entry ?? new PositionEntry()));

                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ',')
                {
                    index++;
                    SkipWhitespace(json, ref index);
                }
            }

            return true;
        }

        //* این تابع فاصله‌های سفید را نادیده می‌گیرد تا پارسر به فاصله‌گذاری حساس نباشد.
        static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
        }

        //* این تابع رشتهٔ JSON را با پشتیبانی از escape می‌خواند و دو گیومهٔ پایانی را مصرف می‌کند.
        static bool TryReadString(string json, ref int index, out string value)
        {
            value = string.Empty;
            if (index >= json.Length || json[index] != '"') return false;
            index++;

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            while (index < json.Length)
            {
                char c = json[index++];
                if (c == '"')
                {
                    value = builder.ToString();
                    return true;
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (index >= json.Length) return false;
                char escaped = json[index++];
                switch (escaped)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (index + 4 > json.Length) return false;
                        int code;
                        if (!int.TryParse(json.Substring(index, 4), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture, out code)) return false;
                        builder.Append((char)code);
                        index += 4;
                        break;
                    default:
                        builder.Append(escaped);
                        break;
                }
            }

            return false;
        }

        //* این تابع عدد JSON را با جداکنندهٔ نقطه می‌خواند تا در همهٔ محلی‌ها یکسان تفسیر شود.
        static bool TryReadFloat(string json, ref int index, out float value)
        {
            value = 0f;
            int start = index;
            while (index < json.Length)
            {
                char c = json[index];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' ||
                    c == 'e' || c == 'E') index++;
                else break;
            }

            if (index == start) return false;
            return float.TryParse(json.Substring(start, index - start), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value);
        }

        //* این تابع یک شیء موقعیت را می‌خواند و فقط کلیدهای position و rotation را نگه می‌دارد.
        static bool TryReadEntry(string json, ref int index, out PositionEntry entry)
        {
            entry = null;
            SkipWhitespace(json, ref index);
            if (index >= json.Length || json[index] != '{') return false;
            index++;

            PositionEntry result = new PositionEntry();

            SkipWhitespace(json, ref index);
            while (index < json.Length && json[index] != '}')
            {
                string key;
                if (!TryReadString(json, ref index, out key)) return false;

                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != ':') return false;
                index++;
                SkipWhitespace(json, ref index);

                bool isPosition = string.Equals(key, "position", StringComparison.Ordinal);
                bool isRotation = string.Equals(key, "rotation", StringComparison.Ordinal);

                if (isPosition)
                {
                    Vec3Data value;
                    if (!TryReadVec3(json, ref index, out value)) return false;
                    result.position = value;
                }
                else if (isRotation)
                {
                    QuatData value;
                    if (!TryReadQuat(json, ref index, out value)) return false;
                    result.rotation = value;
                }
                else if (string.Equals(key, "createdAt", StringComparison.Ordinal))
                {
                    string value;
                    if (!TryReadString(json, ref index, out value)) return false;
                    result.createdAt = value;
                }
                else if (string.Equals(key, "updatedAt", StringComparison.Ordinal))
                {
                    string value;
                    if (!TryReadString(json, ref index, out value)) return false;
                    result.updatedAt = value;
                }
                else if (!SkipValue(json, ref index))
                {
                    return false;
                }

                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ',')
                {
                    index++;
                    SkipWhitespace(json, ref index);
                }
            }

            // کلید بستن شیء موقعیت باید مصرف شود، وگرنه فراخوانیِ فهرست
            // آن را به‌عنوان پایان شیء بیرونی اشتباه می‌گیرد و نقطه‌های بعدی خوانده نمی‌شوند.
            index++;

            entry = result;
            return true;
        }

        //* این تابع شیء سه‌مؤلفه‌ای را می‌خواند و کلیدهای ناشناخته را نادیده می‌گیرد.
        static bool TryReadVec3(string json, ref int index, out Vec3Data value)
        {
            value = null;
            SkipWhitespace(json, ref index);
            if (index >= json.Length || json[index] != '{') return false;
            index++;

            Vec3Data result = new Vec3Data();

            SkipWhitespace(json, ref index);
            while (index < json.Length && json[index] != '}')
            {
                string key;
                if (!TryReadString(json, ref index, out key)) return false;

                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != ':') return false;
                index++;
                SkipWhitespace(json, ref index);

                float number;
                if (!TryReadFloat(json, ref index, out number)) return false;

                switch (key)
                {
                    case "x": result.x = number; break;
                    case "y": result.y = number; break;
                    case "z": result.z = number; break;
                }

                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ',')
                {
                    index++;
                    SkipWhitespace(json, ref index);
                }
            }

            index++;

            value = result;
            return true;
        }

        //* این تابع شیء چهارمؤلفه‌ای چرخش را می‌خواند و در نبود w، چرخش را هویتی می‌گیرد.
        static bool TryReadQuat(string json, ref int index, out QuatData value)
        {
            value = null;
            SkipWhitespace(json, ref index);
            if (index >= json.Length || json[index] != '{') return false;
            index++;

            QuatData result = new QuatData { w = 1f };

            SkipWhitespace(json, ref index);
            while (index < json.Length && json[index] != '}')
            {
                string key;
                if (!TryReadString(json, ref index, out key)) return false;

                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != ':') return false;
                index++;
                SkipWhitespace(json, ref index);

                float number;
                if (!TryReadFloat(json, ref index, out number)) return false;

                switch (key)
                {
                    case "x": result.x = number; break;
                    case "y": result.y = number; break;
                    case "z": result.z = number; break;
                    case "w": result.w = number; break;
                }

                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ',')
                {
                    index++;
                    SkipWhitespace(json, ref index);
                }
            }

            index++;

            value = result;
            return true;
        }

        //* این تابع هر مقدار ناشناخته (شیء، آرایه، رشته، عدد، بولی، null) را کامل رد می‌کند.
        static bool SkipValue(string json, ref int index)
        {
            SkipWhitespace(json, ref index);
            if (index >= json.Length) return false;

            char c = json[index];
            if (c == '"')
            {
                string ignored;
                return TryReadString(json, ref index, out ignored);
            }

            if (c == '{' || c == '[')
            {
                char open = c;
                char close = open == '{' ? '}' : ']';
                int depth = 0;

                while (index < json.Length)
                {
                    char current = json[index];

                    if (current == '"')
                    {
                        string ignored;
                        if (!TryReadString(json, ref index, out ignored)) return false;
                        continue;
                    }

                    index++;
                    if (current == open) depth++;
                    else if (current == close)
                    {
                        depth--;
                        if (depth == 0) return true;
                    }
                }

                return false;
            }

            while (index < json.Length && json[index] != ',' && json[index] != '}' && json[index] != ']')
            {
                index++;
            }

            return true;
        }
    }

    /// <summary>
    /// دسترسی **اختیاری** (Reflection) به رویدادهای لابی Network_A روی برنج gRPC.
    ///
    /// هوک‌های واقعی که استفاده می‌شوند:
    ///   RealtimeRoomGameServerManager.OnRoomJoinedFor3D (static, Action&lt;string&gt;) ← کلیک روی دکمهٔ محیط
    ///   RealtimeRoomGameServerManager.OnRoomLeftFor3D   (static, Action&lt;string&gt;)
    ///   RealtimeRoomGameServerManager.Instance.CurrentRoomName ← کد ساختمان = نام محیط متارنج
    ///   MetaverseNetworkClient.userId ← انتخاب نقطهٔ اسپان بین بازیکنان
    ///
    /// اگر Network_A نبود، همه‌چیز بی‌صدا غیرفعال می‌شود و رفتار شبکهٔ فعلی دست‌نخورده می‌ماند.
    /// </summary>
    public static class LobbyNetworkHooks
    {
        const string ManagerTypeName = "Network_A.Realtime.Controllers.RealtimeRoomGameServerManager";
        const string ClientTypeName = "MetaverseNetworkClient";

        static bool resolved;
        static Type tManager;
        static Type tClient;
        static PropertyInfo piInstance;
        static PropertyInfo piCurrentRoomName;
        static PropertyInfo piCurrentRoomId;
        static PropertyInfo piIsJoinedRoom;
        static PropertyInfo piUserId;
        static EventInfo eiRoomJoined;
        static EventInfo eiRoomLeft;

        //* این تابع انواع لابی را یک‌بار پیدا و اعضای لازم را قفل می‌کند.
        static void Resolve()
        {
            if (resolved) return;
            resolved = true;

            for (int i = 0; i < AppDomain.CurrentDomain.GetAssemblies().Length; i++)
            {
                Assembly asm = AppDomain.CurrentDomain.GetAssemblies()[i];
                if (tManager == null) tManager = asm.GetType(ManagerTypeName);
                if (tClient == null) tClient = asm.GetType(ClientTypeName);
                if (tManager != null && tClient != null) break;
            }

            if (tManager != null)
            {
                piInstance = tManager.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                eiRoomJoined = tManager.GetEvent("OnRoomJoinedFor3D", BindingFlags.Public | BindingFlags.Static);
                eiRoomLeft = tManager.GetEvent("OnRoomLeftFor3D", BindingFlags.Public | BindingFlags.Static);
                piCurrentRoomName = tManager.GetProperty("CurrentRoomName", BindingFlags.Public | BindingFlags.Instance);
                piCurrentRoomId = tManager.GetProperty("CurrentRoomId", BindingFlags.Public | BindingFlags.Instance);
                piIsJoinedRoom = tManager.GetProperty("IsJoinedRoom", BindingFlags.Public | BindingFlags.Instance);
            }

            if (tClient != null)
                piUserId = tClient.GetProperty("userId", BindingFlags.Public | BindingFlags.Static);
        }

        /// <summary>آیا کد لابی Network_A در پروژه موجود است؟</summary>
        public static bool Available
        {
            get { Resolve(); return tManager != null && eiRoomJoined != null; }
        }

        /// <summary>نام اتاق جاری که همان کد ساختمان انتخاب‌شده است.</summary>
        public static string CurrentRoomName
        {
            get
            {
                Resolve();
                object manager = ReadInstance();
                if (manager == null || piCurrentRoomName == null) return string.Empty;

                try { return piCurrentRoomName.GetValue(manager, null) as string ?? string.Empty; }
                catch { return string.Empty; }
            }
        }

        /// <summary>شناسهٔ اتاق جاری.</summary>
        public static string CurrentRoomId
        {
            get
            {
                Resolve();
                object manager = ReadInstance();
                if (manager == null || piCurrentRoomId == null) return string.Empty;

                try { return piCurrentRoomId.GetValue(manager, null) as string ?? string.Empty; }
                catch { return string.Empty; }
            }
        }

        /// <summary>آیا بازیکن در یک اتاق ریل‌تایم هست؟</summary>
        public static bool IsJoinedRoom
        {
            get
            {
                Resolve();
                object manager = ReadInstance();
                if (manager == null || piIsJoinedRoom == null) return false;

                try { return piIsJoinedRoom.GetValue(manager, null) is bool && (bool)piIsJoinedRoom.GetValue(manager, null); }
                catch { return false; }
            }
        }

        /// <summary>شناسهٔ کاربر جاری (برای پخش نقطهٔ اسپان بین بازیکنان).</summary>
        public static string UserId
        {
            get
            {
                Resolve();
                if (tClient == null || piUserId == null) return string.Empty;

                try { return piUserId.GetValue(null, null) as string ?? string.Empty; }
                catch { return string.Empty; }
            }
        }

        //* این تابع نمونهٔ یکتای مدیر Realtime را برمی‌گرداند یا null اگر هنوز ساخته نشده باشد.
        static object ReadInstance()
        {
            Resolve();
            if (tManager == null || piInstance == null) return null;

            try { return piInstance.GetValue(null, null); }
            catch { return null; }
        }

        /// <summary>به رویدادهای ورود/خروج اتاق متصل می‌شود (idempotent).</summary>
        public static bool Subscribe(Action<string> roomJoined, Action<string> roomLeft)
        {
            Resolve();
            if (tManager == null || eiRoomJoined == null) return false;

            try
            {
                if (roomJoined != null && eiRoomJoined.AddMethod != null)
                {
                    Delegate handler = Delegate.CreateDelegate(eiRoomJoined.EventHandlerType,
                        roomJoined.Target, roomJoined.Method);
                    eiRoomJoined.AddMethod.Invoke(null, new object[] { handler });
                }

                if (roomLeft != null && eiRoomLeft != null && eiRoomLeft.AddMethod != null)
                {
                    Delegate handler = Delegate.CreateDelegate(eiRoomLeft.EventHandlerType,
                        roomLeft.Target, roomLeft.Method);
                    eiRoomLeft.AddMethod.Invoke(null, new object[] { handler });
                }

                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[متارنج] اتصال به رویدادهای لابی ناموفق بود: " + exception.Message);
                return false;
            }
        }

        /// <summary>اتصال رویدادهای لابی را برمی‌دارد.</summary>
        public static void Unsubscribe(Action<string> roomJoined, Action<string> roomLeft)
        {
            Resolve();
            if (tManager == null) return;

            try
            {
                if (roomJoined != null && eiRoomJoined != null && eiRoomJoined.RemoveMethod != null)
                {
                    Delegate handler = Delegate.CreateDelegate(eiRoomJoined.EventHandlerType,
                        roomJoined.Target, roomJoined.Method);
                    eiRoomJoined.RemoveMethod.Invoke(null, new object[] { handler });
                }

                if (roomLeft != null && eiRoomLeft != null && eiRoomLeft.RemoveMethod != null)
                {
                    Delegate handler = Delegate.CreateDelegate(eiRoomLeft.EventHandlerType,
                        roomLeft.Target, roomLeft.Method);
                    eiRoomLeft.RemoveMethod.Invoke(null, new object[] { handler });
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[متارنج] قطع اتصال از رویدادهای لابی ناموفق بود: " + exception.Message);
            }
        }

        /// <summary>خلاصهٔ وضعیت برای لاگ تشخیصی.</summary>
        public static string Describe()
        {
            Resolve();
            if (!Available) return "Network_A لابی یافت نشد ⇒ بریج لابی غیرفعال است";

            return "Network_A لابی موجود | roomName=" + CurrentRoomName +
                   " | roomId=" + CurrentRoomId +
                   " | joined=" + IsJoinedRoom +
                   " | userId=" + UserId;
        }
    }

    /// <summary>
    /// اتصال دکمهٔ محیط لابی (Lobby 1 WebGL) به پکیج متارنج:
    ///
    /// ۱) به <c>OnRoomJoinedFor3D</c> گوش می‌دهد (همان رویدادی که با کلیک روی دکمهٔ محیط
    ///    در <c>Lobby1RealtimeSceneController</c> منتشر می‌شود)
    /// ۲) نام اتاق = <b>کد ساختمان</b> را به‌عنوان <c>env</c> متارنج می‌گیرد
    /// ۳) <c>GET /api/list-positions?env=…</c> می‌خواند و یک نقطه را انتخاب می‌کند
    /// ۴) <c>GET /api/get-position?env=&amp;spawn=</c> می‌خواند و پوز را به
    ///    <see cref="MetaRangeNetworkSpawnBridge"/> می‌دهد تا روی local player شبکه اعمال شود
    ///
    /// ساخت یا جابه‌جایی آواتار هرگز در این کلاس انجام نمی‌شود؛ آن کار فقط با Network_A است.
    /// اگر محیط در سرور متارنج نبود (۴۰۴) هیچ جابه‌جایی انجام نمی‌شود و فقط لاگ ثبت می‌گردد.
    /// </summary>
    [AddComponentMenu("MetaRange/Lobby Environment Bridge")]
    public class MetaRangeLobbyEnvironmentBridge : MonoBehaviour
    {
        public enum SpawnSelectionMode
        {
            PerUserStableHash = 0,
            FirstAvailable = 1
        }

        const string RootName = "MetaRange_Lobby_Environment_Bridge";

        [Header("MetaRange (Node)")]
        [SerializeField] private string serverUrl = "http://localhost:3000";

        [Header("Bindings")]
        [SerializeField] private MetaRangeNetworkSpawnBridge networkSpawnBridge;

        [Header("Spawn Selection")]
        [Tooltip("پخش بازیکنان بین نقاط ثبت‌شدهٔ محیط بر اساس شناسهٔ کاربر")]
        [SerializeField] private SpawnSelectionMode spawnSelection = SpawnSelectionMode.PerUserStableHash;
        [Tooltip("نام نقطهٔ ثابت وقتی انتخاب بر اساس کاربر ممکن نیست (کاربر خالی)")]
        [SerializeField] private string fallbackPositionId = "ورودی_اصلی";

        [Header("Safety")]
        [Tooltip("اگر سرور متارنج این محیط را نداشت، هیچ جابه‌جایی انجام نشود")]
        [SerializeField] private bool skipWhenEnvironmentMissing = true;
        [SerializeField] private bool logVerbose = true;

        public string CurrentEnvironment { get; private set; }
        public string LastSpawnId { get; private set; }
        public bool HasResolvedPose { get; private set; }
        public int KnownSpawnCount { get; private set; }
        public string LastError { get; private set; }
        public event Action<string, string> EnvironmentEntered;

        // ------------------------------------------------------------------
        // نصب خودکار (لابی با LoadSceneMode.Single به صحنهٔ گیم‌پلی می‌رود،
        // پس نمونه باید DontDestroyOnLoad باشد)
        // ------------------------------------------------------------------

        //* این تابع اگر نمونه‌ای در صحنه نبود، یک ریشهٔ دائمی می‌سازد تا با تعویض صحنه از بین نرود.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void AutoBootstrap()
        {
            if (FindAnyObjectByType<MetaRangeLobbyEnvironmentBridge>(FindObjectsInactive.Include) != null) return;

            GameObject root = new GameObject(RootName);
            UnityEngine.Object.DontDestroyOnLoad(root);
            root.AddComponent<MetaRangeLobbyEnvironmentBridge>();
        }

        void Awake()
        {
            if (networkSpawnBridge == null)
                networkSpawnBridge = FindAnyObjectByType<MetaRangeNetworkSpawnBridge>();
        }

        void OnEnable()
        {
            if (!LobbyNetworkHooks.Available)
            {
                Log("[متارنج لابی] Network_A لابی موجود نیست ⇒ بریج غیرفعال است (رفتار شبکه دست‌نخورده)");
                return;
            }

            LobbyNetworkHooks.Subscribe(HandleRoomJoined, HandleRoomLeft);
            Log("[متارنج لابی] شروع | " + LobbyNetworkHooks.Describe());
        }

        void OnDisable()
        {
            LobbyNetworkHooks.Unsubscribe(HandleRoomJoined, HandleRoomLeft);
        }

        //* این تابع با خروج از اتاق، وضعیت محیط را پاک می‌کند تا ورود بعدی تازه محاسبه شود.
        private void HandleRoomLeft(string roomId)
        {
            HasResolvedPose = false;
            KnownSpawnCount = 0;
            CurrentEnvironment = string.Empty;
            LastSpawnId = string.Empty;
            Log("[متارنج لابی] خروج از اتاق | roomId=" + roomId);
        }

        //* این تابع با ورود به یک محیط، نام دکمه را به env متارنج تبدیل، آن را روی سرور لوکال تضمین و سپس پوز را می‌گیرد.
        private void HandleRoomJoined(string roomId)
        {
            string rawEnv = ResolveEnvironmentName(roomId);
            string env = EnvironmentCreator.NormalizeEnvironmentName(rawEnv);

            if (string.IsNullOrEmpty(env))
            {
                LastError = "environment_name_unresolved | roomId=" + roomId + " | raw=" + rawEnv;
                Debug.LogWarning("[متارنج لابی] نام محیط قابل تشخیص نبود ⇒ پوزی اعمال نشد | roomId=" + roomId);
                return;
            }

            if (!string.Equals(env, rawEnv, StringComparison.Ordinal))
                Log("[متارنج لابی] نام env نرمال شد | raw=" + rawEnv + " | normalized=" + env);

            CurrentEnvironment = env;
            HasResolvedPose = false;
            KnownSpawnCount = 0;
            LastError = string.Empty;

            Log("[متارنج لابی] ورود به محیط | env=" + env + " | roomId=" + roomId);
            StartCoroutine(EnsureAndResolve(env));
        }

        //* این تابع ابتدا محیط را روی سرور لوکال تضمین می‌کند (idempotent)، سپس قفل Context و پوز را ادامه می‌دهد.
        private IEnumerator EnsureAndResolve(string env)
        {
            // ① تضمین وجود محیط روی سرور Node (اگر بود 409 ⇒ موفقیت)
            var createReply = new MetarangeNet.Reply();
            yield return EnvironmentCreator.EnsureEnvironment(serverUrl, env, createReply);

            if (!createReply.ok)
            {
                LastError = "ensure_environment_failed | env=" + env + " | " + createReply.Detail();
                Debug.LogWarning("[متارنج لابی] ساخت/تضمین محیط «" + env + "» ناموفق بود ⇒ " +
                                 "فقط ادامهٔ جریان شبکه، بدون اسپان");
                yield break;
            }

            // ② قفل Context مشترک ⇒ OwnerPanel و لینک‌سازی همان env را می‌بینند
            EnvironmentCreator.LockStoredName(env);
            Log("[متارنج لابی] Context.env قفل شد | " + EnvironmentCreator.StoredEnvironmentName +
                " | " + (createReply.code == 409 ? "already_exists" : "created"));

            // ③ حالا فهرست نقاط و پوز
            yield return ResolvePoseAndApply(env);
        }

        //* این تابع نام اتاق جاری (کد ساختمان) را می‌گیرد و فقط در نبود آن به roomId پناه می‌برد.
        private string ResolveEnvironmentName(string roomId)
        {
            string name = LobbyNetworkHooks.CurrentRoomName;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            if (!string.IsNullOrWhiteSpace(roomId)) return roomId.Trim();
            return string.Empty;
        }

        //* این تابع ابتدا فهرست نقاط محیط را می‌خواند، یکی را انتخاب و سپس پوز نهایی را اعمال می‌کند.
        private IEnumerator ResolvePoseAndApply(string env)
        {
            string path = "/api/list-positions?env=" + UnityWebRequest.EscapeURL(env);
            var listReply = new MetarangeNet.Reply();
            yield return MetarangeNet.Get(serverUrl, path, listReply);

            List<KeyValuePair<string, PositionEntry>> entries;
            if (!listReply.ok || !MetaRangeSpawnList.TryParseEntries(listReply.body, out entries) || entries.Count == 0)
            {
                LastError = "list_positions_unavailable | env=" + env + " | " +
                            (listReply.ok ? "empty_or_unparsable" : listReply.Detail());

                if (skipWhenEnvironmentMissing)
                {
                    Debug.LogWarning("[متارنج لابی] محیط «" + env + "» در سرور متارنج پیدا نشد ⇒ " +
                                     "آواتار جابه‌جا نشد (رفتار شبکه حفظ شد)");
                    yield break;
                }
            }
            else
            {
                entries.Sort(delegate (KeyValuePair<string, PositionEntry> a,
                                       KeyValuePair<string, PositionEntry> b)
                {
                    return string.CompareOrdinal(a.Key, b.Key);
                });

                KnownSpawnCount = entries.Count;
                yield return FetchAndApply(env, entries);
            }
        }

        //* این تابع نقطهٔ انتخاب‌شده را از منبع حقیقت (/get-position) می‌خواند و به بریج شبکه می‌دهد.
        private IEnumerator FetchAndApply(string env, List<KeyValuePair<string, PositionEntry>> entries)
        {
            string spawnId = SelectSpawnId(entries);

            string path = "/api/get-position?env=" + UnityWebRequest.EscapeURL(env) +
                          "&spawn=" + UnityWebRequest.EscapeURL(spawnId);

            var reply = new MetarangeNet.Reply();
            yield return MetarangeNet.Get(serverUrl, path, reply);

            PositionEntry entry = null;
            if (reply.ok)
            {
                try { entry = JsonUtility.FromJson<PositionEntry>(reply.body); }
                catch (Exception exception)
                {
                    LastError = "get_position_parse_failed | " + exception.Message;
                }
            }
            else
            {
                LastError = "get_position_failed | " + reply.Detail();
            }

            if (entry == null || entry.position == null) entry = FindEntry(entries, spawnId);

            if (entry == null || entry.position == null)
            {
                LastError = "position_unavailable | env=" + env + " | spawn=" + spawnId;
                Debug.LogWarning("[متارنج لابی] موقعیت «" + spawnId + "» در محیط «" + env + "» در دسترس نبود ⇒ " +
                                 "آواتار جابه‌جا نشد");
                yield break;
            }

            Vector3 position = entry.position.ToVector3();
            Quaternion rotation = entry.rotation != null ? entry.rotation.ToQuaternion() : Quaternion.identity;

            LastSpawnId = spawnId;
            HasResolvedPose = true;

            if (networkSpawnBridge == null)
            {
                networkSpawnBridge = FindAnyObjectByType<MetaRangeNetworkSpawnBridge>();
                if (networkSpawnBridge == null)
                {
                    LastError = "network_spawn_bridge_missing";
                    Debug.LogWarning("[متارنج لابی] MetaRangeNetworkSpawnBridge پیدا نشد ⇒ پوز فقط خوانده شد و اعمال نشد");
                    yield break;
                }
            }

            networkSpawnBridge.ApplyPoseWhenPlayerReady(position, rotation, env, spawnId);

            // پنل مالک هم دقیقاً همین آواتار شبکه را بگیرد تا «موقعیت زنده» و «ثبت موقعیت»
            // روی همان Transform باشند (نه یک کپسول، نه آواتار دوم).
            networkSpawnBridge.AdoptNetworkPlayer("LobbyJoin:" + spawnId);

            Log("[متارنج لابی] پوز محیط ثبت شد | env=" + env + " | spawn=" + spawnId +
                " | points=" + entries.Count + " | pos=" + position +
                " rot=" + rotation.eulerAngles + " (اعمال روی local player شبکه در بریج)");

            Action<string, string> handler = EnvironmentEntered;
            if (handler == null) yield break;

            try { handler(env, spawnId); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }

        //* این تابع شناسهٔ نقطه را بر اساس حالت انتخاب مشخص می‌کند و لیست را مرتب‌شده فرض می‌کند.
        internal string SelectSpawnId(List<KeyValuePair<string, PositionEntry>> sortedEntries)
        {
            if (sortedEntries == null || sortedEntries.Count == 0) return string.Empty;

            if (spawnSelection == SpawnSelectionMode.FirstAvailable) return sortedEntries[0].Key;

            string userId = LobbyNetworkHooks.UserId;
            if (string.IsNullOrWhiteSpace(userId)) userId = SystemInfo.deviceUniqueIdentifier;
            if (string.IsNullOrWhiteSpace(userId))
                return string.IsNullOrEmpty(fallbackPositionId) ? sortedEntries[0].Key : fallbackPositionId;

            int index = StableHash(userId) % sortedEntries.Count;
            return sortedEntries[index].Key;
        }

        //* این تابع هش پایدار FNV-1a می‌دهد تا یک کاربر همیشه همان نقطه را بگیرد.
        internal static int StableHash(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;

            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= 16777619;
                }
                return (int)(hash & 0x7FFFFFFF);
            }
        }

        //* این تابع ورودیِ مرتب‌شده را با شناسهٔ داده‌شده پیدا می‌کند.
        static PositionEntry FindEntry(List<KeyValuePair<string, PositionEntry>> entries, string spawnId)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i].Key, spawnId, StringComparison.Ordinal)) return entries[i].Value;
            }

            return null;
        }

        private void Log(string message)
        {
            if (logVerbose) Debug.Log(message);
        }
    }
}