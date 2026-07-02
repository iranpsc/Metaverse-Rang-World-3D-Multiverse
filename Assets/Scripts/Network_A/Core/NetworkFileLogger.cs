using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Network_A.Core
{
    public class NetworkFileLogger : MonoBehaviour
    {
        public static NetworkFileLogger Instance { get; private set; }

        [Header("File Log Settings")]
        [SerializeField] bool enableFileLog = true;
        [SerializeField] bool enableMarkdownLog = true;
        [SerializeField] bool enableHtmlLog = true;
        [SerializeField] bool captureUnityLogs = true;
        [SerializeField] bool echoToConsole;
        [SerializeField] string folderName = "Network_A_Logs";

        readonly object lockObject = new object();
        readonly List<LogEntry> allEntries = new List<LogEntry>();
        readonly Dictionary<string, RequestGroup> requestGroups = new Dictionary<string, RequestGroup>();
        readonly List<LogEntry> nonRequestEntries = new List<LogEntry>();

        StreamWriter writer;
        string sessionId;
        string logFilePath;
        string markdownFilePath;
        string htmlFilePath;
        bool isInitialized;
        bool reportsGenerated;
        static bool isCreating;

        public static string CurrentLogFilePath => Instance != null ? Instance.logFilePath : string.Empty;
        public static string CurrentMarkdownFilePath => Instance != null ? Instance.markdownFilePath : string.Empty;
        public static string CurrentHtmlFilePath => Instance != null ? Instance.htmlFilePath : string.Empty;

        class LogEntry
        {
            public string Time;
            public string Level;
            public string Tag;
            public string Message;
            public Dictionary<string, string> Values;
        }

        class RequestGroup
        {
            public string RequestId;
            public string Url;
            public string Method;
            public int LastStatus;
            public List<LogEntry> Entries = new List<LogEntry>();
        }

        //* Creates logger automatically before the first scene starts.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void CreateLoggerOnLoad()
        {
            EnsureInstance();
        }

        //* Ensures that the logger exists in the scene.
        public static NetworkFileLogger EnsureInstance()
        {
            if (Instance != null) return Instance;
            if (isCreating) return null;

            isCreating = true;
            var go = new GameObject("Network_A_FileLogger");
            DontDestroyOnLoad(go);
            var logger = go.AddComponent<NetworkFileLogger>();
            isCreating = false;
            return logger;
        }

        //* Initializes logger singleton and creates a new log file for the current Play session.
        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            InitializeLogger();
        }

        //* Releases logger resources when the object is destroyed.
        void OnDestroy()
        {
            if (Instance == this) ShutdownLogger();
        }

        //* Releases logger resources when application quits.
        void OnApplicationQuit()
        {
            ShutdownLogger();
        }

        //* Creates log directory, creates session log file and starts capturing Unity logs.
        public void InitializeLogger()
        {
            if (isInitialized) return;

            sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string logDirectory = Path.Combine(Application.persistentDataPath, folderName);
            if (!Directory.Exists(logDirectory)) Directory.CreateDirectory(logDirectory);

            logFilePath = Path.Combine(logDirectory, "Network_A_Play_" + sessionId + ".log");
            markdownFilePath = Path.Combine(logDirectory, "Network_A_Play_" + sessionId + ".md");
            htmlFilePath = Path.Combine(logDirectory, "Network_A_Play_" + sessionId + ".html");

            if (enableFileLog) writer = new StreamWriter(logFilePath, false, Encoding.UTF8) { AutoFlush = true };

            isInitialized = true;
            reportsGenerated = false;

            if (captureUnityLogs) Application.logMessageReceivedThreaded += HandleUnityLog;

            WriteHeader();
            WriteInternal("INFO", "LOGGER", "Network file logger started. Path: " + logFilePath);
            WriteInternal("INFO", "LOGGER", "Markdown report path: " + markdownFilePath);
            WriteInternal("INFO", "LOGGER", "HTML report path: " + htmlFilePath);
        }

        //* Stops capturing Unity logs, generates grouped reports and closes the log file.
        public void ShutdownLogger()
        {
            if (!isInitialized) return;

            WriteInternal("INFO", "LOGGER", "Network file logger stopped.");
            if (captureUnityLogs) Application.logMessageReceivedThreaded -= HandleUnityLog;

            lock (lockObject)
            {
                if (!reportsGenerated)
                {
                    if (enableMarkdownLog) GenerateMarkdownReport();
                    if (enableHtmlLog) GenerateHtmlReport();
                    reportsGenerated = true;
                }

                writer?.Flush();
                writer?.Close();
                writer?.Dispose();
                writer = null;
            }

            isInitialized = false;
        }

        //* Logs an information entry.
        public static void Info(string tag, string message) { Write("INFO", tag, message); }

        //* Logs a warning entry.
        public static void Warning(string tag, string message) { Write("WARN", tag, message); }

        //* Logs an error entry.
        public static void Error(string tag, string message) { Write("ERROR", tag, message); }

        //* Logs an exception entry.
        public static void Exception(string tag, Exception exception)
        {
            if (exception == null)
            {
                Write("EXCEPTION", tag, "Exception is null.");
                return;
            }

            Write("EXCEPTION", tag, exception.GetType().Name + ": " + exception.Message + "\n" + exception.StackTrace);
        }

        //* Logs a request lifecycle entry.
        public static void Request(string requestId, string stage, string url, string method = "", int statusCode = 0, string extra = "")
        {
            string text = "requestId=" + Safe(requestId) + " stage=" + Safe(stage) + " method=" + Safe(method) + " status=" + statusCode + " url=" + Safe(url) + " extra=" + Safe(extra);
            Write("REQUEST", "NETWORK", text);
        }

        //* Logs an auth lifecycle entry.
        public static void Auth(string stage, bool ok, string message = "", string userId = "", bool hasAccess = false, bool hasRefresh = false)
        {
            string text = "stage=" + Safe(stage) + " ok=" + ok + " userId=" + Safe(userId) + " hasAccess=" + hasAccess + " hasRefresh=" + hasRefresh + " message=" + Safe(message);
            Write("AUTH", "AUTH", text);
        }

        //* Logs token state without printing the real token value.
        public static void TokenState(string stage, string accessToken, string refreshToken)
        {
            string text = "stage=" + Safe(stage) + " access=" + MaskToken(accessToken) + " refresh=" + MaskToken(refreshToken);
            Write("TOKEN", "AUTH", text);
        }

        //* Logs a key-value data line.
        public static void Data(string tag, string key, string value)
        {
            Write("DATA", tag, Safe(key) + "=" + Safe(value));
        }

        //* Writes a log entry to file and optionally to Unity console.
        static void Write(string level, string tag, string message)
        {
            EnsureInstance();
            if (Instance == null)
            {
                Debug.Log("[Network_A_FileLoggerMissing] [" + level + "] [" + tag + "] " + message);
                return;
            }

            Instance.WriteInternal(level, tag, message);
        }

        //* Writes one formatted line to the raw log file and stores it for grouped reports.
        void WriteInternal(string level, string tag, string message)
        {
            string time = DateTime.Now.ToString("HH:mm:ss.fff");
            string safeLevel = Safe(level);
            string safeTag = Safe(tag);
            string safeMessage = Safe(message);
            string line = time + " | " + safeLevel + " | " + safeTag + " | " + safeMessage;

            if (echoToConsole && level != "UNITY_LOG") Debug.Log(line);

            lock (lockObject)
            {
                var entry = new LogEntry
                {
                    Time = time,
                    Level = safeLevel,
                    Tag = safeTag,
                    Message = safeMessage,
                    Values = ParseKeyValues(safeMessage)
                };

                allEntries.Add(entry);
                AddEntryToGroup(entry);

                if (enableFileLog && writer != null) writer.WriteLine(line);
            }
        }

        //* Captures Unity Debug.Log, Debug.Warning and Debug.Error entries.
        void HandleUnityLog(string condition, string stackTrace, LogType type)
        {
            if (!isInitialized) return;

            string level = type == LogType.Error || type == LogType.Exception ? "UNITY_ERROR" : type == LogType.Warning ? "UNITY_WARN" : "UNITY_LOG";
            string message = Safe(condition);
            if (type == LogType.Exception || type == LogType.Error) message += "\\n" + Safe(stackTrace);
            WriteInternal(level, "UNITY", message);
        }

        //* Writes basic session information at the top of the log file.
        void WriteHeader()
        {
            WriteInternal("SESSION", "START", "==================================================");
            WriteInternal("SESSION", "START", "SessionId: " + sessionId);
            WriteInternal("SESSION", "START", "UnityVersion: " + Application.unityVersion);
            WriteInternal("SESSION", "START", "Platform: " + Application.platform);
            WriteInternal("SESSION", "START", "ProductName: " + Application.productName);
            WriteInternal("SESSION", "START", "AppVersion: " + Application.version);
            WriteInternal("SESSION", "START", "DeviceModel: " + SystemInfo.deviceModel);
            WriteInternal("SESSION", "START", "OperatingSystem: " + SystemInfo.operatingSystem);
            WriteInternal("SESSION", "START", "PersistentDataPath: " + Application.persistentDataPath);
            WriteInternal("SESSION", "START", "==================================================");
        }

        //* Adds an entry to a request group when it contains a requestId.
        void AddEntryToGroup(LogEntry entry)
        {
            if (entry == null || entry.Values == null)
            {
                nonRequestEntries.Add(entry);
                return;
            }

            if (entry.Level != "REQUEST" || !entry.Values.TryGetValue("requestId", out string requestId) || string.IsNullOrEmpty(requestId) || requestId == "<empty>")
            {
                nonRequestEntries.Add(entry);
                return;
            }

            if (!requestGroups.TryGetValue(requestId, out RequestGroup group))
            {
                group = new RequestGroup { RequestId = requestId };
                requestGroups.Add(requestId, group);
            }

            if (entry.Values.TryGetValue("url", out string url) && !string.IsNullOrEmpty(url) && url != "<empty>") group.Url = url;
            if (entry.Values.TryGetValue("method", out string method) && !string.IsNullOrEmpty(method) && method != "<empty>") group.Method = method;
            if (entry.Values.TryGetValue("status", out string statusText) && int.TryParse(statusText, out int status)) group.LastStatus = status;

            group.Entries.Add(entry);
        }

        //* Generates grouped markdown report at shutdown.
        void GenerateMarkdownReport()
        {
            using (var md = new StreamWriter(markdownFilePath, false, Encoding.UTF8))
            {
                md.WriteLine("# Network A Grouped Log");
                md.WriteLine();
                md.WriteLine("- Session: `" + EscapeMarkdown(sessionId) + "`");
                md.WriteLine("- Raw log: `" + EscapeMarkdown(logFilePath) + "`");
                md.WriteLine("- Created: `" + EscapeMarkdown(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")) + "`");
                md.WriteLine();
                md.WriteLine("---");
                md.WriteLine();

                md.WriteLine("# Request Groups");
                md.WriteLine();

                foreach (var pair in requestGroups)
                {
                    WriteMarkdownRequestGroup(md, pair.Value);
                }

                md.WriteLine("# Other Logs");
                md.WriteLine();

                for (int i = 0; i < nonRequestEntries.Count; i++)
                {
                    WriteMarkdownEntry(md, nonRequestEntries[i]);
                }
            }
        }

        //* Writes one request group into markdown report.
        void WriteMarkdownRequestGroup(StreamWriter md, RequestGroup group)
        {
            if (group == null) return;

            string title = group.Method + " " + group.Url;
            md.WriteLine("## REQUEST GROUP");
            md.WriteLine();
            md.WriteLine("- RequestId: `" + EscapeMarkdown(group.RequestId) + "`");
            md.WriteLine("- Method: `" + EscapeMarkdown(group.Method) + "`");
            md.WriteLine("- Url: `" + EscapeMarkdown(group.Url) + "`");
            md.WriteLine("- LastStatus: `" + group.LastStatus + "`");
            md.WriteLine("- Summary: `" + EscapeMarkdown(title) + "`");
            md.WriteLine();

            md.WriteLine("| Time | Stage | Status | Extra |");
            md.WriteLine("|---|---|---|---|");

            for (int i = 0; i < group.Entries.Count; i++)
            {
                LogEntry entry = group.Entries[i];
                string stage = ReadValue(entry, "stage");
                string status = ReadValue(entry, "status");
                string extra = ReadValue(entry, "extra");
                md.WriteLine("| `" + EscapeMarkdown(entry.Time) + "` | `" + EscapeMarkdown(stage) + "` | `" + EscapeMarkdown(status) + "` | `" + EscapeMarkdown(extra) + "` |");
            }

            md.WriteLine();
            md.WriteLine("<details>");
            md.WriteLine("<summary>Full request values</summary>");
            md.WriteLine();

            for (int i = 0; i < group.Entries.Count; i++)
            {
                WriteMarkdownEntry(md, group.Entries[i]);
            }

            md.WriteLine("</details>");
            md.WriteLine();
            md.WriteLine("---");
            md.WriteLine();
        }

        //* Writes one markdown entry.
        void WriteMarkdownEntry(StreamWriter md, LogEntry entry)
        {
            if (entry == null) return;

            md.WriteLine("### " + EscapeMarkdown(entry.Level) + " · " + EscapeMarkdown(entry.Tag) + " · " + EscapeMarkdown(entry.Time));
            md.WriteLine();

            if (entry.Values != null && entry.Values.Count > 0)
            {
                md.WriteLine("| Key | Value |");
                md.WriteLine("|---|---|");

                foreach (var pair in entry.Values)
                {
                    md.WriteLine("| `" + EscapeMarkdown(pair.Key) + "` | `" + EscapeMarkdown(pair.Value) + "` |");
                }
            }
            else
            {
                md.WriteLine(EscapeMarkdown(entry.Message));
            }

            md.WriteLine();
        }

        //* Generates grouped html report at shutdown.
        void GenerateHtmlReport()
        {
            using (var html = new StreamWriter(htmlFilePath, false, Encoding.UTF8))
            {
                WriteHtmlStart(html);

                html.WriteLine("<h2>Request Groups</h2>");

                foreach (var pair in requestGroups)
                {
                    WriteHtmlRequestGroup(html, pair.Value);
                }

                html.WriteLine("<h2>Other Logs</h2>");

                for (int i = 0; i < nonRequestEntries.Count; i++)
                {
                    WriteHtmlEntry(html, nonRequestEntries[i]);
                }

                html.WriteLine("</div>");
                html.WriteLine("</body>");
                html.WriteLine("</html>");
            }
        }

        //* Writes html document start and styles.
        void WriteHtmlStart(StreamWriter html)
        {
            html.WriteLine("<!doctype html>");
            html.WriteLine("<html lang=\"en\">");
            html.WriteLine("<head>");
            html.WriteLine("<meta charset=\"utf-8\">");
            html.WriteLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            html.WriteLine("<title>Network A Grouped Log " + Html(sessionId) + "</title>");
            html.WriteLine("<style>");
            html.WriteLine("body{margin:0;background:#101216;color:#d9e2ef;font-family:Consolas,Menlo,monospace;font-size:14px;line-height:1.45;}");
            html.WriteLine(".wrap{max-width:1500px;margin:0 auto;padding:24px;}");
            html.WriteLine("h1,h2{color:#e8f1ff;}.top,.group,.entry{border:1px solid #293241;border-radius:12px;background:#151922;margin:12px 0;padding:14px 16px;}");
            html.WriteLine(".summary{display:flex;gap:10px;align-items:center;flex-wrap:wrap;margin-bottom:10px;}.pill{border-radius:999px;padding:3px 9px;font-weight:700;background:#263747;color:#9cdcfe;}");
            html.WriteLine(".url{color:#79b8ff;word-break:break-all;}.requestId{color:#c79cff;}.time{color:#8ea0b8;}.tag{color:#79b8ff;}.msg{white-space:pre-wrap;word-break:break-word;}");
            html.WriteLine(".level{font-weight:700;border-radius:999px;padding:2px 8px;}.INFO{background:#183a2a;color:#62d486;}.WARN{background:#4a3515;color:#ffcc66;}.ERROR,.EXCEPTION,.UNITY_ERROR{background:#4a1b1b;color:#ff7777;}");
            html.WriteLine(".REQUEST{background:#18304a;color:#79b8ff;}.AUTH{background:#3a2459;color:#c79cff;}.TOKEN{background:#4a3418;color:#ffcc66;}.DATA{background:#263747;color:#9cdcfe;}.SESSION{background:#2d3440;color:#d6deeb;}.UNITY_LOG{background:#263238;color:#b0bec5;}.UNITY_WARN{background:#4a3515;color:#ffcc66;}");
            html.WriteLine("table{width:100%;border-collapse:collapse;margin-top:8px;}th,td{border-bottom:1px solid #273244;text-align:left;padding:6px 8px;vertical-align:top;}th{color:#9cdcfe;background:#101721;}");
            html.WriteLine(".kv{display:flex;flex-wrap:wrap;gap:8px;margin-top:6px;}.pair{background:#0f131a;border:1px solid #273244;border-radius:8px;padding:4px 7px;}.key{color:#9cdcfe;}.value{color:#dcdcaa;}.value.error{color:#ff7777;}.value.ok{color:#62d486;}.value.warn{color:#ffcc66;}");
            html.WriteLine("details{margin-top:10px;}summary{cursor:pointer;color:#ffcc66;}");
            html.WriteLine("</style>");
            html.WriteLine("</head>");
            html.WriteLine("<body>");
            html.WriteLine("<div class=\"wrap\">");
            html.WriteLine("<div class=\"top\"><h1>Network A Grouped Log</h1><div>Session: " + Html(sessionId) + "</div><div>Raw log: " + Html(logFilePath) + "</div></div>");
        }

        //* Writes one grouped request card into html report.
        void WriteHtmlRequestGroup(StreamWriter html, RequestGroup group)
        {
            if (group == null) return;

            html.WriteLine("<div class=\"group\">");
            html.WriteLine("<div class=\"summary\"><span class=\"pill\">" + Html(group.Method) + "</span><span class=\"url\">" + Html(group.Url) + "</span><span class=\"requestId\">requestId=" + Html(group.RequestId) + "</span><span class=\"pill\">lastStatus=" + group.LastStatus + "</span></div>");
            html.WriteLine("<table>");
            html.WriteLine("<tr><th>Time</th><th>Stage</th><th>Status</th><th>Extra</th></tr>");

            for (int i = 0; i < group.Entries.Count; i++)
            {
                LogEntry entry = group.Entries[i];
                html.WriteLine("<tr><td>" + Html(entry.Time) + "</td><td>" + Html(ReadValue(entry, "stage")) + "</td><td>" + Html(ReadValue(entry, "status")) + "</td><td>" + Html(ReadValue(entry, "extra")) + "</td></tr>");
            }

            html.WriteLine("</table>");
            html.WriteLine("<details><summary>Full request values</summary>");

            for (int i = 0; i < group.Entries.Count; i++)
            {
                WriteHtmlEntry(html, group.Entries[i]);
            }

            html.WriteLine("</details>");
            html.WriteLine("</div>");
        }

        //* Writes one html entry.
        void WriteHtmlEntry(StreamWriter html, LogEntry entry)
        {
            if (entry == null) return;

            string cssLevel = CssClass(entry.Level);
            html.WriteLine("<div class=\"entry\">");
            html.WriteLine("<div class=\"summary\"><span class=\"time\">" + Html(entry.Time) + "</span><span class=\"level " + cssLevel + "\">" + Html(entry.Level) + "</span><span class=\"tag\">" + Html(entry.Tag) + "</span></div>");

            if (entry.Values != null && entry.Values.Count > 0)
            {
                html.WriteLine("<div class=\"kv\">");

                foreach (var pair in entry.Values)
                {
                    html.WriteLine("<span class=\"pair\"><span class=\"key\">" + Html(pair.Key) + "</span><span>=</span><span class=\"value " + ValueClass(pair.Key, pair.Value) + "\">" + Html(pair.Value) + "</span></span>");
                }

                html.WriteLine("</div>");
            }
            else
            {
                html.WriteLine("<div class=\"msg\">" + Html(entry.Message) + "</div>");
            }

            html.WriteLine("</div>");
        }

        //* Reads a value from an entry dictionary.
        static string ReadValue(LogEntry entry, string key)
        {
            if (entry == null || entry.Values == null || string.IsNullOrEmpty(key)) return string.Empty;
            return entry.Values.TryGetValue(key, out string value) ? value : string.Empty;
        }

        //* Parses a simple key=value message into a dictionary.
        static Dictionary<string, string> ParseKeyValues(string message)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(message) || !message.Contains("=")) return result;

            string[] parts = message.Split(' ');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                int index = part.IndexOf('=');
                if (index <= 0) continue;

                string key = part.Substring(0, index);
                string value = part.Substring(index + 1);
                if (string.IsNullOrEmpty(key)) continue;

                if (result.ContainsKey(key)) result[key] = value;
                else result.Add(key, value);
            }

            return result;
        }

        //* Masks token value for safe logging.
        static string MaskToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return "<empty>";
            if (token.Length <= 12) return "<short-token>";
            return token.Substring(0, 6) + "..." + token.Substring(token.Length - 6);
        }

        //* Sanitizes null values for log output.
        static string Safe(string value)
        {
            return string.IsNullOrEmpty(value) ? "<empty>" : value.Replace("\n", "\\n").Replace("\r", "\\r");
        }

        //* Escapes html special characters.
        static string Html(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
        }

        //* Escapes markdown table breaking characters.
        static string EscapeMarkdown(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("|", "\\|").Replace("\n", " ").Replace("\r", " ");
        }

        //* Converts a level name to a safe css class.
        static string CssClass(string value)
        {
            if (string.IsNullOrEmpty(value)) return "INFO";
            return value.Replace(" ", "_").Replace("-", "_");
        }

        //* Returns css class for key-value values.
        static string ValueClass(string key, string value)
        {
            string lowerKey = string.IsNullOrEmpty(key) ? string.Empty : key.ToLowerInvariant();
            string lowerValue = string.IsNullOrEmpty(value) ? string.Empty : value.ToLowerInvariant();

            if (lowerKey.Contains("error")) return "error";
            if (lowerValue.Contains("error")) return "error";
            if (lowerValue.Contains("fail")) return "error";
            if (lowerValue == "false") return "error";
            if (lowerValue == "success") return "ok";
            if (lowerValue == "true") return "ok";
            if (lowerValue == "200") return "ok";
            if (lowerValue == "401") return "warn";
            if (lowerValue == "403") return "warn";
            if (lowerValue == "404") return "warn";
            if (lowerValue.StartsWith("5")) return "error";

            return string.Empty;
        }
    }
}