using System;
using System.Collections;
using System.IO;
using System.Text;
using Network_A.GameServer.Gameplay;
using Network_A.GameServer.Players;
using UnityEngine;
using UnityEngine.Networking;

namespace Network_A.GameServer.Diagnostics
{
    public sealed class DedicatedReconnectGraceAuditLogger : MonoBehaviour
    {
        private const string AuditFileName = "Dedicated_Reconnect_Grace_Audit.log";
        private const string PreviousAuditFileName = "Dedicated_Reconnect_Grace_Audit.previous.log";
        private const long MaxAuditFileBytes = 20L * 1024L * 1024L;

        private static DedicatedReconnectGraceAuditLogger instance;

        private readonly object writerLock = new object();

        private StreamWriter writer;
        private string auditFilePath = string.Empty;
        private bool unityLogSubscribed;
        private bool registryEventsBound;
        private bool stateStoreEventsBound;
        private float nextReferenceScanAt;

        private DedicatedServerRuntime runtime;
        private DedicatedPlayerRegistry playerRegistry;
        private DedicatedPlayerStateStore playerStateStore;
        private MetaverseNetworkPlayerObjectServer playerObjectServer;

        //* این تابع لاگر گریس را قبل از لود صحنه و فقط در پروسه ددیکیتد سرور می سازد.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void CreateAuditLoggerBeforeSceneLoad()
        {
            if (!ShouldRunInCurrentProcess()) return;
            if (instance != null) return;

            GameObject loggerObject = new GameObject("Dedicated_Reconnect_Grace_Audit_Logger");
            DontDestroyOnLoad(loggerObject);
            instance = loggerObject.AddComponent<DedicatedReconnectGraceAuditLogger>();
        }

        //* این تابع فایل لاگ مستقل را آماده و دریافت لاگ های یونیتی را فعال می کند.
        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }

            instance = this;
            DontDestroyOnLoad(gameObject);

            InitializeAuditFile();
            SubscribeUnityLogs();

            WriteAudit(
                "AUDIT_SESSION_STARTED",
                "utc=" + DateTime.UtcNow.ToString("O") +
                " | platform=" + Application.platform +
                " | unityVersion=" + Application.unityVersion +
                " | product=" + Safe(Application.productName) +
                " | appVersion=" + Safe(Application.version) +
                " | baseDirectory=" + Safe(AppDomain.CurrentDomain.BaseDirectory) +
                " | persistentDataPath=" + Safe(Application.persistentDataPath) +
                " | commandLine=" + Safe(string.Join(" ", Environment.GetCommandLineArgs()))
            );

            Debug.Log(
                "[DedicatedReconnectGraceAuditLogger] Automatic grace audit enabled | path=" +
                auditFilePath
            );
        }

        //* این تابع رفرنس ها و رویدادهای رجیستری و استیت استور را در زمان اجرا متصل نگه می دارد.
        private void Update()
        {
            if (Time.unscaledTime < nextReferenceScanAt) return;

            nextReferenceScanAt = Time.unscaledTime + 1f;
            EnsureReferencesAndBindings();
        }

        //* این تابع هنگام خروج برنامه رویدادها را جدا و فایل را می بندد.
        private void OnApplicationQuit()
        {
            ShutdownAuditLogger("application_quit");
        }

        //* این تابع هنگام نابودی آبجکت، منابع لاگر را آزاد می کند.
        private void OnDestroy()
        {
            if (instance != this) return;

            ShutdownAuditLogger("logger_destroyed");
            instance = null;
        }

        //* این تابع فایل ثابت لاگ را مستقیم کنار فایل اجرایی سرور می سازد و فقط در صورت خطا از پرسیستنت دیتا استفاده می کند.
        private void InitializeAuditFile()
        {
            string preferredFolder = ResolveExecutableDirectory();

            try
            {
                OpenAuditWriter(preferredFolder);
                return;
            }
            catch (Exception preferredError)
            {
                string fallbackFolder = Application.persistentDataPath;

                try
                {
                    OpenAuditWriter(fallbackFolder);
                    WriteAudit(
                        "AUDIT_PATH_FALLBACK",
                        "preferredFolder=" + Safe(preferredFolder) +
                        " | fallbackFolder=" + Safe(fallbackFolder) +
                        " | error=" + Safe(preferredError.Message)
                    );

                    Debug.LogWarning(
                        "[DedicatedReconnectGraceAuditLogger] Preferred audit path failed; fallback path is active | path=" +
                        auditFilePath + " | error=" + preferredError.Message
                    );
                }
                catch (Exception fallbackError)
                {
                    auditFilePath = string.Empty;
                    writer = null;

                    Debug.LogError(
                        "[DedicatedReconnectGraceAuditLogger] Audit file creation failed | preferredFolder=" +
                        preferredFolder + " | preferredError=" + preferredError.Message +
                        " | fallbackFolder=" + fallbackFolder +
                        " | fallbackError=" + fallbackError.Message
                    );
                }
            }
        }

        //* این تابع مسیر واقعی پوشه بیلد را از پوشه دیتای یونیتی به دست می آورد تا فایل دقیقاً کنار فایل اجرایی ساخته شود.
        private static string ResolveExecutableDirectory()
        {
            try
            {
                string dataPath = Application.dataPath;
                if (!string.IsNullOrWhiteSpace(dataPath))
                {
                    DirectoryInfo dataDirectory = new DirectoryInfo(dataPath);
                    if (dataDirectory.Parent != null &&
                        !string.IsNullOrWhiteSpace(dataDirectory.Parent.FullName))
                    {
                        return dataDirectory.Parent.FullName;
                    }
                }
            }
            catch
            {
                // در صورت نامعتبر بودن دیتا پث، مسیر بیس دامین استفاده می شود.
            }

            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(baseDirectory))
            {
                return baseDirectory;
            }

            return Directory.GetCurrentDirectory();
        }

        //* این تابع پوشه لاگ را می سازد، فایل قبلی بزرگ را روتیت می کند و رایتر را باز می کند.
        private void OpenAuditWriter(string folderPath)
        {
            Directory.CreateDirectory(folderPath);

            auditFilePath = Path.Combine(folderPath, AuditFileName);
            string previousPath = Path.Combine(folderPath, PreviousAuditFileName);

            if (File.Exists(auditFilePath))
            {
                FileInfo info = new FileInfo(auditFilePath);
                if (info.Length >= MaxAuditFileBytes)
                {
                    if (File.Exists(previousPath)) File.Delete(previousPath);
                    File.Move(auditFilePath, previousPath);
                }
            }

            writer = new StreamWriter(
                auditFilePath,
                true,
                new UTF8Encoding(false)
            )
            {
                AutoFlush = true
            };
        }

        //* این تابع دریافت لاگ های یونیتی را فقط یک بار فعال می کند.
        private void SubscribeUnityLogs()
        {
            if (unityLogSubscribed) return;

            Application.logMessageReceivedThreaded += HandleUnityLogReceived;
            unityLogSubscribed = true;
        }

        //* این تابع دریافت لاگ های یونیتی را متوقف می کند.
        private void UnsubscribeUnityLogs()
        {
            if (!unityLogSubscribed) return;

            Application.logMessageReceivedThreaded -= HandleUnityLogReceived;
            unityLogSubscribed = false;
        }

        //* این تابع فقط لاگ های مرتبط با گریس، حذف، استیت، لفت و دیسپاون را داخل فایل آدیت نگه می دارد.
        private void HandleUnityLogReceived(
            string condition,
            string stackTrace,
            LogType logType)
        {
            if (!ShouldCaptureUnityLine(condition)) return;

            string level = logType == LogType.Error || logType == LogType.Exception
                ? "ERROR"
                : logType == LogType.Warning
                    ? "WARN"
                    : "INFO";

            WriteAudit(
                "UNITY_" + level,
                Safe(condition)
            );

            if ((logType == LogType.Error || logType == LogType.Exception) &&
                !string.IsNullOrWhiteSpace(stackTrace))
            {
                WriteAudit(
                    "UNITY_STACK",
                    Safe(stackTrace)
                );
            }
        }

        //* این تابع تعیین می کند کدام لاگ های عمومی یونیتی برای تست گریس لازم هستند.
        private static bool ShouldCaptureUnityLine(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;
            if (message.Contains("[DedicatedReconnectGraceAuditLogger]")) return false;

            if (message.Contains("[DedicatedTicketHandshakeHandler] Reconnect grace")) return true;
            if (message.Contains("[DedicatedTicketHandshakeHandler] Pending reconnect grace")) return true;
            if (message.Contains("[DedicatedTicketHandshakeHandler] All reconnect grace")) return true;
            if (message.Contains("[DedicatedTicketHandshakeHandler] Player resume state")) return true;
            if (message.Contains("[DedicatedPlayerRegistry] Player registered")) return true;
            if (message.Contains("[DedicatedPlayerRegistry] Player removed")) return true;
            if (message.Contains("[DedicatedPlayerRegistry] Duplicate user rebound")) return true;
            if (message.Contains("[DedicatedPlayerStateStore] State removed")) return true;
            if (message.Contains("[DedicatedPlayerStateStore] State rebound")) return true;
            if (message.Contains("[DedicatedGameMessageRouter] Player joined broadcast")) return true;
            if (message.Contains("[DedicatedGameMessageRouter] Player left broadcast")) return true;
            if (message.Contains("[MetaverseNetworkPlayerObjectServer] Player object")) return true;
            if (message.Contains("[DedicatedHeartbeatLoop]") &&
                message.Contains("player")) return true;
            if (message.Contains("[GameServerControlDedicatedClient]") &&
                (message.Contains("player-left") || message.Contains("player_left"))) return true;

            return false;
        }

        //* این تابع رفرنس های مورد نیاز را پیدا و رویدادهای آدیت را متصل می کند.
        private void EnsureReferencesAndBindings()
        {
            if (runtime == null)
            {
                runtime = DedicatedServerRuntime.Instance;
#if UNITY_2023_1_OR_NEWER
                if (runtime == null) runtime = FindFirstObjectByType<DedicatedServerRuntime>();
#else
                if (runtime == null) runtime = FindObjectOfType<DedicatedServerRuntime>();
#endif
            }

            if (playerRegistry == null)
            {
#if UNITY_2023_1_OR_NEWER
                playerRegistry = FindFirstObjectByType<DedicatedPlayerRegistry>();
#else
                playerRegistry = FindObjectOfType<DedicatedPlayerRegistry>();
#endif
            }

            if (playerStateStore == null)
            {
#if UNITY_2023_1_OR_NEWER
                playerStateStore = FindFirstObjectByType<DedicatedPlayerStateStore>();
#else
                playerStateStore = FindObjectOfType<DedicatedPlayerStateStore>();
#endif
            }

            if (playerObjectServer == null)
            {
#if UNITY_2023_1_OR_NEWER
                playerObjectServer = FindFirstObjectByType<MetaverseNetworkPlayerObjectServer>();
#else
                playerObjectServer = FindObjectOfType<MetaverseNetworkPlayerObjectServer>();
#endif
            }

            BindRegistryEvents();
            BindStateStoreEvents();
        }

        //* این تابع رویدادهای ثبت و حذف پلیر را فقط یک بار متصل می کند.
        private void BindRegistryEvents()
        {
            if (registryEventsBound || playerRegistry == null) return;

            playerRegistry.PlayerRegistered -= HandlePlayerRegistered;
            playerRegistry.PlayerRemoved -= HandlePlayerRemoved;
            playerRegistry.PlayerRegistered += HandlePlayerRegistered;
            playerRegistry.PlayerRemoved += HandlePlayerRemoved;
            registryEventsBound = true;

            WriteAudit(
                "REGISTRY_EVENTS_BOUND",
                "currentPlayers=" + playerRegistry.CurrentPlayerCount +
                " | uniqueUsers=" + playerRegistry.UniqueUserCount
            );
        }

        //* این تابع رویداد حذف استیت را فقط یک بار متصل می کند.
        private void BindStateStoreEvents()
        {
            if (stateStoreEventsBound || playerStateStore == null) return;

            playerStateStore.PlayerStateRemoved -= HandlePlayerStateRemoved;
            playerStateStore.PlayerStateRemoved += HandlePlayerStateRemoved;
            stateStoreEventsBound = true;

            WriteAudit(
                "STATE_STORE_EVENTS_BOUND",
                "stateCount=" + playerStateStore.StateCount +
                " | userStateCount=" + playerStateStore.GetUserStateCount()
            );
        }

        //* این تابع رویدادهای رجیستری را جدا می کند.
        private void UnbindRegistryEvents()
        {
            if (!registryEventsBound || playerRegistry == null) return;

            playerRegistry.PlayerRegistered -= HandlePlayerRegistered;
            playerRegistry.PlayerRemoved -= HandlePlayerRemoved;
            registryEventsBound = false;
        }

        //* این تابع رویداد استیت استور را جدا می کند.
        private void UnbindStateStoreEvents()
        {
            if (!stateStoreEventsBound || playerStateStore == null) return;

            playerStateStore.PlayerStateRemoved -= HandlePlayerStateRemoved;
            stateStoreEventsBound = false;
        }

        //* این تابع ثبت یا ریبایند پلیر را با شمارنده های فعلی داخل آدیت ثبت می کند.
        private void HandlePlayerRegistered(DedicatedPlayerSession session)
        {
            if (session == null) return;

            WriteAudit(
                "PLAYER_REGISTERED_EVENT",
                BuildSessionText(session) +
                " | registryCount=" + (playerRegistry != null ? playerRegistry.CurrentPlayerCount : -1) +
                " | stateExists=" + HasStateForSession(session)
            );

            StartCoroutine(WritePostRegisterSnapshotCoroutine(session));
        }

        //* این تابع حذف نهایی پلیر را ثبت و یک اسنپ شات تأخیری از رجیستری، استیت، آبجکت و نود می گیرد.
        private void HandlePlayerRemoved(
            DedicatedPlayerSession session,
            string reason)
        {
            if (session == null) return;

            WriteAudit(
                "PLAYER_REMOVED_EVENT",
                BuildSessionText(session) +
                " | reason=" + Safe(reason) +
                " | registryCount=" + (playerRegistry != null ? playerRegistry.CurrentPlayerCount : -1)
            );

            StartCoroutine(
                WritePostRemovalSnapshotCoroutine(
                    session,
                    reason
                )
            );
        }

        //* این تابع حذف واقعی استیت را به صورت رویدادی ثبت می کند.
        private void HandlePlayerStateRemoved(
            DedicatedPlayerStateRecord record,
            string reason)
        {
            if (record == null) return;

            WriteAudit(
                "PLAYER_STATE_REMOVED_EVENT",
                "userId=" + Safe(record.userId) +
                " | playerId=" + Safe(record.playerId) +
                " | roomId=" + Safe(record.roomId) +
                " | connectionId=" + Safe(record.connectionId) +
                " | sequence=" + record.sequence +
                " | position=" + record.Position +
                " | reason=" + Safe(reason) +
                " | stateCount=" + (playerStateStore != null ? playerStateStore.StateCount : -1)
            );
        }

        //* این تابع کمی بعد از ثبت پلیر، نتیجه ریبایند استیت و آبجکت را ثبت می کند.
        private IEnumerator WritePostRegisterSnapshotCoroutine(
            DedicatedPlayerSession session)
        {
            yield return new WaitForSecondsRealtime(1f);

            EnsureReferencesAndBindings();

            bool registryHasUser = playerRegistry != null &&
                                   playerRegistry.GetByUserIdInRoom(
                                       session.roomId,
                                       session.userId
                                   ) != null;

            DedicatedPlayerStateRecord stateRecord = playerStateStore != null
                ? playerStateStore.GetByUserIdInRoom(session.roomId, session.userId)
                : null;

            bool playerObjectExists = false;
            if (playerObjectServer != null)
            {
                playerObjectExists = playerObjectServer.TryGetPlayerObject(
                    session,
                    out MetaverseNetworkIdentity _
                );
            }

            WriteAudit(
                "POST_REGISTER_SNAPSHOT",
                BuildSessionText(session) +
                " | registryHasUser=" + registryHasUser +
                " | stateExists=" + (stateRecord != null) +
                " | stateSequence=" + (stateRecord != null ? stateRecord.sequence : 0) +
                " | playerObjectExists=" + playerObjectExists +
                " | playerObjectCount=" + (playerObjectServer != null ? playerObjectServer.PlayerObjectCount : -1)
            );
        }

        //* این تابع کمی بعد از حذف پلیر بررسی می کند که رجیستری، استیت و آبجکت واقعاً پاک شده باشند.
        private IEnumerator WritePostRemovalSnapshotCoroutine(
            DedicatedPlayerSession session,
            string reason)
        {
            yield return new WaitForSecondsRealtime(1.5f);

            EnsureReferencesAndBindings();

            bool registryHasUser = playerRegistry != null &&
                                   playerRegistry.GetByUserIdInRoom(
                                       session.roomId,
                                       session.userId
                                   ) != null;

            DedicatedPlayerStateRecord stateRecord = playerStateStore != null
                ? playerStateStore.GetByUserIdInRoom(session.roomId, session.userId)
                : null;

            bool playerObjectExists = false;
            if (playerObjectServer != null)
            {
                playerObjectExists = playerObjectServer.TryGetPlayerObject(
                    session,
                    out MetaverseNetworkIdentity _
                );
            }

            WriteAudit(
                "POST_REMOVAL_SNAPSHOT",
                BuildSessionText(session) +
                " | reason=" + Safe(reason) +
                " | registryHasUser=" + registryHasUser +
                " | registryCount=" + (playerRegistry != null ? playerRegistry.CurrentPlayerCount : -1) +
                " | stateExists=" + (stateRecord != null) +
                " | stateCount=" + (playerStateStore != null ? playerStateStore.StateCount : -1) +
                " | playerObjectExists=" + playerObjectExists +
                " | playerObjectCount=" + (playerObjectServer != null ? playerObjectServer.PlayerObjectCount : -1)
            );

            yield return FetchAndWriteNodeStatusCoroutine(
                session,
                reason
            );
        }

        //* این تابع بعد از حذف پلیر، استاتوس نود را خودکار می گیرد و وجود یوزر حذف شده را در پاسخ ثبت می کند.
        private IEnumerator FetchAndWriteNodeStatusCoroutine(
            DedicatedPlayerSession session,
            string reason)
        {
            string controlBaseUrl = ResolveControlBaseUrl();
            if (string.IsNullOrWhiteSpace(controlBaseUrl))
            {
                WriteAudit(
                    "NODE_STATUS_SKIPPED",
                    BuildSessionText(session) +
                    " | reason=" + Safe(reason) +
                    " | error=control_base_url_missing"
                );

                yield break;
            }

            string statusUrl = controlBaseUrl.TrimEnd('/') +
                               "/game-server-control/status";

            using (UnityWebRequest request = UnityWebRequest.Get(statusUrl))
            {
                request.timeout = 10;
                yield return request.SendWebRequest();

                string body = request.downloadHandler != null
                    ? request.downloadHandler.text
                    : string.Empty;

                bool containsUserId = !string.IsNullOrWhiteSpace(session.userId) &&
                                      !string.IsNullOrWhiteSpace(body) &&
                                      body.Contains(session.userId);

                bool containsConnectionId = !string.IsNullOrWhiteSpace(session.connectionId) &&
                                            !string.IsNullOrWhiteSpace(body) &&
                                            body.Contains(session.connectionId);

                WriteAudit(
                    "NODE_STATUS_AFTER_REMOVAL",
                    BuildSessionText(session) +
                    " | reason=" + Safe(reason) +
                    " | url=" + Safe(statusUrl) +
                    " | httpStatus=" + request.responseCode +
                    " | requestResult=" + request.result +
                    " | containsUserId=" + containsUserId +
                    " | containsOldConnectionId=" + containsConnectionId +
                    " | body=" + Safe(LimitLength(body, 30000))
                );
            }
        }

        //* این تابع کنترل بیس یو آر ال را از کانفیگ ران تایم سرور برمی گرداند.
        private string ResolveControlBaseUrl()
        {
            if (runtime == null) runtime = DedicatedServerRuntime.Instance;

            if (runtime != null &&
                runtime.CurrentConfig != null &&
                !string.IsNullOrWhiteSpace(runtime.CurrentConfig.controlBaseUrl))
            {
                return runtime.CurrentConfig.controlBaseUrl.Trim();
            }

            return string.Empty;
        }

        //* این تابع وجود استیت آخر پلیر را بررسی می کند.
        private bool HasStateForSession(DedicatedPlayerSession session)
        {
            if (session == null || playerStateStore == null) return false;

            return playerStateStore.GetByUserIdInRoom(
                session.roomId,
                session.userId
            ) != null;
        }

        //* این تابع اطلاعات ثابت سشن را به متن قابل مقایسه برای آدیت تبدیل می کند.
        private static string BuildSessionText(DedicatedPlayerSession session)
        {
            if (session == null) return "session=<null>";

            return "userId=" + Safe(session.userId) +
                   " | playerId=" + Safe(session.playerId) +
                   " | userName=" + Safe(session.userName) +
                   " | roomId=" + Safe(session.roomId) +
                   " | serverId=" + Safe(session.serverId) +
                   " | sessionId=" + Safe(session.sessionId) +
                   " | connectionId=" + Safe(session.connectionId);
        }

        //* این تابع هر خط آدیت را با زمان یو تی سی داخل فایل ثابت ثبت می کند.
        private void WriteAudit(string stage, string message)
        {
            string line = DateTime.UtcNow.ToString("O") +
                          " | " + Safe(stage) +
                          " | " + Safe(message);

            lock (writerLock)
            {
                if (writer == null) return;

                try
                {
                    writer.WriteLine(line);
                }
                catch
                {
                    // در این مسیر عمداً لاگ یونیتی نوشته نمی شود تا حلقه بازگشتی ایجاد نشود.
                }
            }
        }

        //* این تابع رویدادها و فایل آدیت را به شکل امن می بندد.
        private void ShutdownAuditLogger(string reason)
        {
            UnsubscribeUnityLogs();
            UnbindRegistryEvents();
            UnbindStateStoreEvents();

            WriteAudit(
                "AUDIT_SESSION_STOPPED",
                "utc=" + DateTime.UtcNow.ToString("O") +
                " | reason=" + Safe(reason)
            );

            lock (writerLock)
            {
                if (writer == null) return;

                try
                {
                    writer.Flush();
                    writer.Close();
                    writer.Dispose();
                }
                catch
                {
                    // هنگام خروج برنامه، خطای بستن فایل نادیده گرفته می شود.
                }

                writer = null;
            }
        }

        //* این تابع تشخیص می دهد پروسه فعلی ددیکیتد سرور است یا کلاینت بازی.
        private static bool ShouldRunInCurrentProcess()
        {
            if (Application.isBatchMode) return true;
            if (Application.platform == RuntimePlatform.LinuxPlayer) return true;

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string value = Safe(args[i]).ToLowerInvariant();
                if (value == "-server") return true;
                if (value == "-dedicatedserver") return true;
                if (value == "-batchmode") return true;
                if (value == "-runmode" && i + 1 < args.Length)
                {
                    string runMode = Safe(args[i + 1]);
                    if (runMode == "2") return true;
                }
            }

            return false;
        }

        //* این تابع طول پاسخ استاتوس را برای جلوگیری از رشد کنترل نشده فایل محدود می کند.
        private static string LimitLength(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (maxLength <= 0 || value.Length <= maxLength) return value;

            return value.Substring(0, maxLength) +
                   "...<truncated:" + (value.Length - maxLength) + ">";
        }

        //* این تابع متن را برای ثبت در یک خط لاگ امن می کند.
        private static string Safe(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            return value
                .Trim()
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }
    }
}
