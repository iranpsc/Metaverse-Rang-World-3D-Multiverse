using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;

namespace MetaRange.Avatar
{
    /// <summary>
    /// دسترسی **اختیاری** به هوک‌های واقعی Network_A / Dedicated (برنچ gRPC) از راه Reflection.
    ///
    /// چرا Reflection و نه ارجاع مستقیم؟ چون `Network_A` فقط در برنج شبکه وجود دارد و
    /// این پکیج باید هم روی آن برنچ و هم بدون آن کامپایل شود. اگر کلاس‌های
    /// <c>MetaverseNetworkClient</c> / <c>MetaverseSpawnManager</c> / <c>MetaverseNetworkIdentity</c>
    /// در اسمبلی بارگذاری‌شده نباشند، همهٔ متدها <c>false</c> برمی‌گردانند و
    /// سیستم به حالت آفلاین قبلی برمی‌گردد (بدون هیچ خطایی).
    ///
    /// هوک‌های واقعی که استفاده می‌شوند (برنچ gRPC):
    ///   MetaverseNetworkClient.TryGetLocalPlayer(out MetaverseNetworkIdentity)  → «local player آماده است»
    ///   MetaverseNetworkClient.isReady / userId / playerId
    ///   MetaverseSpawnManager.Instance.GetSpawnedObjects()                      → fallback
    ///   MetaverseNetworkIdentity.IsLocalPlayer / IsLocalOwner / HasAuthority / NetId
    /// </summary>
    public static class MetaverseNetworkHooks
    {
        static bool typesResolved;
        static Type tNetworkClient;      // MetaverseNetworkClient (global namespace)
        static Type tSpawnManager;       // MetaverseSpawnManager
        static Type tIdentity;           // MetaverseNetworkIdentity
        static MethodInfo miTryGetLocalPlayer;
        static MethodInfo miGetSpawnedObjects;
        static PropertyInfo piSpawnManagerInstance;
        static FieldInfo fiSpawnManagerInstance;

        const string ClientTypeName = "MetaverseNetworkClient";
        const string ManagerTypeName = "MetaverseSpawnManager";
        const string IdentityTypeName = "MetaverseNetworkIdentity";

        static void ResolveTypes()
        {
            if (typesResolved) return;
            typesResolved = true;

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (tNetworkClient == null) tNetworkClient = asm.GetType(ClientTypeName);
                if (tSpawnManager == null) tSpawnManager = asm.GetType(ManagerTypeName);
                if (tIdentity == null) tIdentity = asm.GetType(IdentityTypeName);
            }

            if (tNetworkClient != null)
                miTryGetLocalPlayer = tNetworkClient.GetMethod("TryGetLocalPlayer",
                    BindingFlags.Public | BindingFlags.Static);
            if (tSpawnManager != null)
            {
                piSpawnManagerInstance = tSpawnManager.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.Static);
                fiSpawnManagerInstance = tSpawnManager.GetField("Instance",
                    BindingFlags.Public | BindingFlags.Static);
                miGetSpawnedObjects = tSpawnManager.GetMethod("GetSpawnedObjects",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            }
        }

        static object ReadManagerInstance()
        {
            ResolveTypes();
            if (tSpawnManager == null) return null;
            try
            {
                if (piSpawnManagerInstance != null) return piSpawnManagerInstance.GetValue(null);
                if (fiSpawnManagerInstance != null) return fiSpawnManagerInstance.GetValue(null);
            }
            catch { }
            return null;
        }

        /// <summary>آیا کد شبکه (Network_A) در پروژه موجود است؟</summary>
        public static bool Available
        {
            get { ResolveTypes(); return tNetworkClient != null || tSpawnManager != null; }
        }

        /// <summary>کلاینت به Dedicated وصل و احراز هویت شده است؟</summary>
        public static bool NetworkClientReady
        {
            get
            {
                ResolveTypes();
                if (tNetworkClient == null) return false;
                try
                {
                    PropertyInfo p = tNetworkClient.GetProperty("isReady", BindingFlags.Public | BindingFlags.Static);
                    return p != null && p.GetValue(null) is bool && (bool)p.GetValue(null);
                }
                catch { return false; }
            }
        }

        /// <summary>آیا همین حالا local player شبکه وجود دارد؟</summary>
        public static bool LocalPlayerReady => TryGetLocalPlayer(out Transform _);

        /// <summary>
        /// پیدا کردن local player شبکه. اول از API رسمی
        /// <c>MetaverseNetworkClient.TryGetLocalPlayer</c> استفاده می‌شود و اگر نبود،
        /// از <c>MetaverseSpawnManager.GetSpawnedObjects()</c> با تست IsLocalPlayer/IsLocalOwner.
        /// </summary>
        public static bool TryGetLocalPlayer(out Transform playerTransform)
        {
            playerTransform = null;
            object identity = TryGetLocalPlayerIdentity();
            if (identity == null) return false;

            Transform t = GetTransform(identity);
            if (t == null) return false;

            playerTransform = t;
            return true;
        }

        /// <summary>شیء identity محلی (برای خواندن NetId / authority)</summary>
        public static object TryGetLocalPlayerIdentity()
        {
            ResolveTypes();
            if (tNetworkClient == null) return null;

            // ① API رسمی
            if (miTryGetLocalPlayer != null)
            {
                try
                {
                    object[] args = { null };
                    object result = miTryGetLocalPlayer.Invoke(null, args);
                    if (result is bool && (bool)result && args[0] != null) return args[0];
                }
                catch { /* ادامه با fallback */ }
            }

            // ② fallback: پیمایش spawned objects
            return FindLocalInSpawnedObjects();
        }

        static object FindLocalInSpawnedObjects()
        {
            ResolveTypes();
            if (tSpawnManager == null || miGetSpawnedObjects == null) return null;

            object manager = ReadManagerInstance();
            if (manager == null) return null;

            object listObj = null;
            try { listObj = miGetSpawnedObjects.Invoke(manager, null); } catch { }
            if (!(listObj is System.Collections.IEnumerable)) return null;

            object firstOwned = null;
            foreach (object identity in (System.Collections.IEnumerable)listObj)
            {
                if (identity == null) continue;
                if (ReadBool(identity, "IsLocalPlayer")) return identity;
                if (firstOwned == null && ReadBool(identity, "IsLocalOwner")) firstOwned = identity;
            }
            return firstOwned;
        }

        static bool ReadBool(object target, string propertyName)
        {
            if (target == null) return false;
            try
            {
                PropertyInfo p = target.GetType().GetProperty(propertyName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (p == null) return false;
                object v = p.GetValue(target, null);
                return v is bool && (bool)v;
            }
            catch { return false; }
        }

        static Transform GetTransform(object identity)
        {
            if (identity == null) return null;
            try
            {
                if (identity is Component c) return c != null ? c.transform : null;
                PropertyInfo p = identity.GetType().GetProperty("transform",
                    BindingFlags.Public | BindingFlags.Instance);
                return p != null ? p.GetValue(identity, null) as Transform : null;
            }
            catch { return null; }
        }

        /// <summary>توضیح وضعیت برای لاگ</summary>
        public static string Describe()
        {
            ResolveTypes();
            if (!Available) return "Network_A (MetaverseNetworkClient/SpawnManager) یافت نشد ⇒ حالت آفلاین";
            return "Network_A موجود | isReady=" + NetworkClientReady +
                   " | localPlayer=" + LocalPlayerReady;
        }
    }

    /// <summary>
    /// بریج «متارنج ⇄ Network_A»:
    ///
    /// 1) پارامترهای <c>env</c>/<c>spawn</c> را از URL می‌گیرد (همان <see cref="SpawnFromURL"/>)
    /// 2) از سرور Node فقط **موقعیت و چرخش** را می‌خواند
    /// 3) صبر می‌کند تا local player شبکه (Network_A) ساخته شود
    /// 4) همان Transform موجود را جابه‌جا می‌کند — **هیچ آواتار دومی ساخته نمی‌شود**
    ///
    /// محدودیت مهم (صادقانه): روی برنج gRPC سرور برای حرکت **authority** دارد
    /// (<c>identity.transform.position += move * speed * dt</c> در
    /// <c>MetaverseNetworkPlayerMovementBridge.HandleServerOwnerInput</c>) و هر
    /// transform broadcast سرور، مقدار محلی را بازنویسی می‌کند
    /// (<c>MetaverseNetworkStateSyncBridge.ApplyNetworkTransform</c>). بنابراین این بریج
    /// جای‌گذاری را **به‌صورت best-effort** انجام می‌دهد و برای چند ثانیه (قابل تنظیم)
    /// پوز را در برابر snap-back دوباره assert می‌کند. جای‌گذاری **دائمی و سمت سرور**
    /// نیازمند تغییر سمت سرور در <c>MetaverseNetworkPlayerObjectServer.SpawnPlayerObject</c> است
    /// که خارج از محدودهٔ این پکیج است.
    /// </summary>
    [AddComponentMenu("MetaRange/Network Spawn Bridge")]
    public class MetaRangeNetworkSpawnBridge : MonoBehaviour
    {
        [Header("MetaRange (Node)")]
        [SerializeField] private string serverUrl = "http://localhost:3000";
        [Tooltip("اگر سرور پاسخ نداد، اصلاً آواتار را جابه‌جا نکن (برای شبکه بهتر است)")]
        [SerializeField] private bool applyDefaultOnFailure = false;
        [SerializeField] private Vector3 defaultPosition = new Vector3(0f, 1f, 0f);
        [SerializeField] private Quaternion defaultRotation = Quaternion.identity;

        [Header("Network_A (gRPC)")]
        [Tooltip("فاصلهٔ بین بررسی‌های local player (ثانیه)")]
        [SerializeField] private float pollInterval = 0.25f;
        [Tooltip("حداکثر انتظار برای ساخته شدن local player (ثانیه)")]
        [SerializeField] private float waitTimeoutSeconds = 30f;
        [Tooltip("اگر شبکه موجود نبود، به تگ Player / نام‌های رایج هم اجازه بده (حالت آفلاین)")]
        [SerializeField] private bool allowOfflineFallback = true;

        [Header("Authority Guard")]
        [Tooltip("چند ثانیه پوز دوباره assert شود تا snap-back سرور خنثی شود؛ ۰ = فقط یک‌بار")]
        [SerializeField] private float reassertSeconds = 2.5f;
        [Tooltip("اگر بازیکن از این فاصله دور شد، یعنی خودش حرکت کرده ⇒ دست از assert بکش")]
        [SerializeField] private float giveUpDriftMeters = 5f;

        [Header("Bindings")]
        [SerializeField] private OwnerPanel ownerPanel;
        [SerializeField] private bool logVerbose = true;

        // ---------------- وضعیت ----------------
        static bool bridgeActive;   // در Awake ست می‌شود (قبل از Start همهٔ کامپوننت‌ها)
        Vector3 posePosition;
        Quaternion poseRotation;
        bool hasPose;
        bool applied;
        bool applyRoutineRunning;
        string lastEnv = "";
        string lastSpawn = "";

        /// <summary>بریج شبکه در حال اجراست؟ (برای جلوگیری از تداخل با SpawnFromURL)</summary>
        public static bool IsActive => bridgeActive;

        public bool HasPose => hasPose;
        public bool IsApplied => applied;
        public bool IsApplyRoutineRunning => applyRoutineRunning;
        public Vector3 PosePosition => posePosition;
        public Quaternion PoseRotation => poseRotation;
        public string LastEnvironment => lastEnv;
        public string LastPositionId => lastSpawn;

        // ------------------------------------------------------------------
        // API برای سیستم‌های بالادستی (مثلاً بریج لابی گِرَپ‌سی)
        // پوز از بیرون می‌آید، ولی اعمال آن همچنان منتظر local player شبکه می‌ماند.
        // ------------------------------------------------------------------

        /// <summary>
        /// پوز را از بیرون ثبت می‌کند و همان انتظارِ «local player آماده شود» را اجرا می‌کند.
        /// اگر حلقهٔ اعمال از قبل در حال اجرا باشد، پوز جدید جایگزین می‌شود و همان حلقه ادامه می‌یابد.
        /// </summary>
        public Coroutine ApplyPoseWhenPlayerReady(Vector3 position, Quaternion rotation,
                                                   string environment = "", string positionId = "")
        {
            SetPose(position, rotation);

            if (!string.IsNullOrWhiteSpace(environment)) lastEnv = environment.Trim();
            if (!string.IsNullOrWhiteSpace(positionId)) lastSpawn = positionId.Trim();

            if (applyRoutineRunning)
            {
                Log("[MetaRangeNetworkSpawnBridge] پوز تازه ثبت شد و حلقهٔ اعمال قبلی ادامه می‌یابد | env=" +
                    lastEnv + " spawn=" + lastSpawn);
                return null;
            }

            applied = false;
            return StartCoroutine(WaitForLocalPlayerAndApply());
        }

        /// <summary>پوز را ثبت می‌کند و اگر local player همین حالا آماده است بی‌درنگ اعمالش می‌کند.</summary>
        public bool ApplyPoseNowIfPossible(Vector3 position, Quaternion rotation,
                                          string environment = "", string positionId = "")
        {
            ApplyPoseWhenPlayerReady(position, rotation, environment, positionId);

            if (applyRoutineRunning) return false;
            return ReapplyNow();
        }

        void Awake()
        {
            bridgeActive = true;
            if (ownerPanel == null) ownerPanel = FindAnyObjectByType<OwnerPanel>();
        }

        void OnDestroy()
        {
            if (bridgeActive) bridgeActive = false;
        }

        void Start()
        {
            Log("[MetaRangeNetworkSpawnBridge] شروع | " + MetaverseNetworkHooks.Describe());

            if (SpawnFromURL.TryGetUrlParams(out string env, out string spawn))
            {
                lastEnv = env;
                lastSpawn = spawn;
                StartCoroutine(ResolvePoseAndApply(env, spawn));
                return;
            }

            Log("[MetaRangeNetworkSpawnBridge] پارامتر env/spawn در URL نیست ⇒ پوزی اعمال نمی‌شود " +
                "(رفتار شبکهٔ قبلی دست‌نخورده می‌ماند)");
        }

        // ------------------------------------------------------------------
        // گام ۱: گرفتن پوز از Node (فقط مختصات — ساخت آواتار با شبکه است)
        // ------------------------------------------------------------------
        IEnumerator ResolvePoseAndApply(string env, string spawn)
        {
            bool fetched = false;
            yield return FetchPose(env, spawn, ok => fetched = ok);

            if (!fetched)
            {
                if (applyDefaultOnFailure)
                {
                    Log("[MetaRangeNetworkSpawnBridge] پوز ناموفق ⇒ پوز پیش‌فرض | " +
                        defaultPosition.ToString());
                    SetPose(defaultPosition, defaultRotation);
                }
                else
                {
                    Debug.LogWarning("[MetaRangeNetworkSpawnBridge] دریافت پوز ناموفق ⇒ " +
                                     "آواتار جابه‌جا نشد (applyDefaultOnFailure=false)");
                    yield break;
                }
            }

            yield return WaitForLocalPlayerAndApply();
        }

        IEnumerator FetchPose(string env, string spawn, Action<bool> onDone)
        {
            string path = "/api/get-position?env="
                          + UnityWebRequest.EscapeURL(env) + "&spawn="
                          + UnityWebRequest.EscapeURL(spawn);

            var reply = new MetarangeNet.Reply();
            yield return MetarangeNet.Get(serverUrl, path, reply);

            bool ok = false;

            if (reply.ok)
            {
                try
                {
                    var entry = JsonUtility.FromJson<PositionEntry>(reply.body);
                    if (entry != null && entry.position != null)
                    {
                        SetPose(entry.position.ToVector3(),
                            entry.rotation != null ? entry.rotation.ToQuaternion() : Quaternion.identity);
                        Log("[MetaRangeNetworkSpawnBridge] پوز دریافت شد | env=" + env + " spawn=" + spawn +
                            " | pos=" + posePosition + " rot=" + poseRotation.eulerAngles);
                        ok = true;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError("[MetaRangeNetworkSpawnBridge] خطا در پارس پاسخ: " + e.Message);
                }
            }
            else
            {
                Debug.LogWarning("[MetaRangeNetworkSpawnBridge] خطای API — " + reply.Detail());
            }

            onDone(ok);
        }

        void SetPose(Vector3 pos, Quaternion rot)
        {
            posePosition = pos;
            poseRotation = rot;
            hasPose = true;
        }

        // ------------------------------------------------------------------
        // گام ۲: انتظار برای local player شبکه
        // ------------------------------------------------------------------
        IEnumerator WaitForLocalPlayerAndApply()
        {
            applyRoutineRunning = true;

            try
            {
                float deadline = Time.realtimeSinceStartup + Mathf.Max(0.5f, waitTimeoutSeconds);
                int attempts = 0;

                while (Time.realtimeSinceStartup < deadline)
                {
                    attempts++;
                    if (TryApplyToLocalPlayer(out Transform player))
                    {
                        Log("[MetaRangeNetworkSpawnBridge] پوز اعمال شد | تلاش=" + attempts +
                            " | player=" + player.name + " | pos=" + posePosition +
                            " rot=" + poseRotation.eulerAngles);
                        yield return ReassertPose(player);
                        yield break;
                    }

                    yield return new WaitForSeconds(Mathf.Max(0.05f, pollInterval));
                }

                // شبکه حاضر نشد
                if (allowOfflineFallback)
                {
                    Transform fallback = FindOfflineAvatar();
                    if (fallback != null && TryApplyPose(fallback, false))
                        Log("[MetaRangeNetworkSpawnBridge] شبکه آماده نشد ⇒ پوز روی آواتار آفلاین اعمال شد | " +
                            fallback.name);
                    else
                        Debug.LogWarning("[MetaRangeNetworkSpawnBridge] local player شبکه ظرف " +
                                         waitTimeoutSeconds + "s ساخته نشد ⇒ پوز اعمال نشد");
                }
                else
                {
                    Debug.LogWarning("[MetaRangeNetworkSpawnBridge] local player شبکه ظرف " +
                                     waitTimeoutSeconds + "s ساخته نشد ⇒ پوز اعمال نشد");
                }
            }
            finally
            {
                applyRoutineRunning = false;
            }
        }

        /// <summary>تلاش برای اعمال پوز روی local player شبکه (یا آواتار آفلاین در حالت مجاز)</summary>
        bool TryApplyToLocalPlayer(out Transform player)
        {
            player = null;

            if (MetaverseNetworkHooks.TryGetLocalPlayer(out Transform netPlayer))
            {
                player = netPlayer;
                return TryApplyPose(netPlayer, true);
            }

            if (allowOfflineFallback)
            {
                Transform fallback = FindOfflineAvatar();
                if (fallback != null)
                {
                    player = fallback;
                    return TryApplyPose(fallback, false);
                }
            }
            return false;
        }

        static Transform FindOfflineAvatar()
        {
            GameObject found = null;
            try { found = GameObject.FindWithTag("Player"); } catch { }
            if (found == null)
            {
                string[] names = { "Player", "MetaRangeAvatar", "Avatar", "LocalPlayer", "XR Origin", "XR Rig" };
                for (int i = 0; i < names.Length && found == null; i++) found = GameObject.Find(names[i]);
            }
            return found != null ? found.transform : null;
        }

        /// <summary>
        /// اعمال پوز روی یک Transform موجود (بدون ساخت آواتار جدید)، مقاوم در برابر
        /// CharacterController/Rigidbody — همان الگوی <see cref="OwnerPanel.MoveAvatarTo"/>.
        /// </summary>
        public bool TryApplyPose(Transform target, bool isNetworkPlayer)
        {
            if (target == null || !hasPose) return false;

            CharacterController cc = target.GetComponent<CharacterController>();
            bool ccWasEnabled = cc != null && cc.enabled;
            if (cc != null && cc.enabled) cc.enabled = false;

            Rigidbody rb = target.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                if (rb.isKinematic) rb.position = posePosition;
                else rb.MovePosition(posePosition);
            }

            target.SetPositionAndRotation(posePosition, poseRotation);

            if (cc != null && ccWasEnabled) cc.enabled = true;

            applied = true;
            if (ownerPanel != null && isNetworkPlayer) ownerPanel.SetAvatar(target);

            if (isNetworkPlayer)
                Log("[MetaRangeNetworkSpawnBridge] هشدار authority: سرور gRPC مالک حرکت است؛ " +
                    "این جای‌گذاری محلی است و ممکن است با اولین snapshot سرور بازنویسی شود.");
            return true;
        }

        // ------------------------------------------------------------------
        // گام ۳: دوباره assert کردن پوز برای خنثی کردن snap-back سرور
        // ------------------------------------------------------------------
        IEnumerator ReassertPose(Transform player)
        {
            if (reassertSeconds <= 0f || player == null) yield break;

            float until = Time.realtimeSinceStartup + reassertSeconds;
            int reasserts = 0;

            while (Time.realtimeSinceStartup < until && player != null)
            {
                yield return new WaitForSeconds(0.2f);
                if (player == null) yield break;

                float drift = Vector3.Distance(player.position, posePosition);
                if (drift > giveUpDriftMeters)
                {
                    Log("[MetaRangeNetworkSpawnBridge] بازیکن از پوز دور شد (drift=" +
                        drift.ToString("F1") + "m) ⇒ دست از assert برداشته شد (حرکت کاربر)");
                    yield break;
                }

                if (drift > 0.05f)
                {
                    TryApplyPose(player, true);
                    reasserts++;
                }
            }

            if (logVerbose)
                Log("[MetaRangeNetworkSpawnBridge] پایان پنجرهٔ assert | تعداد=" + reasserts);
        }

        /// <summary>اعمال دستی دوبارهٔ پوز (مثلاً دکمهٔ «برو به نقطهٔ ثبت‌شده»)</summary>
        public bool ReapplyNow()
        {
            if (!hasPose) return false;
            if (MetaverseNetworkHooks.TryGetLocalPlayer(out Transform t) && TryApplyPose(t, true)) return true;
            if (allowOfflineFallback)
            {
                Transform f = FindOfflineAvatar();
                if (f != null) return TryApplyPose(f, false);
            }
            return false;
        }

        /// <summary>ثبت دستی پوز (وقتی مختصات از جای دیگری می‌آید، مثلاً سرور شبکه)</summary>
        public void SetPoseExternal(Vector3 position, Quaternion rotation)
        {
            SetPose(position, rotation);
        }

        void Log(string msg)
        {
            if (logVerbose) Debug.Log(msg);
        }
    }
}