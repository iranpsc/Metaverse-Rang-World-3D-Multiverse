using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using UnityEngine;

namespace Network_A.DedicatedGameServer.Client
{
    public class DedicatedGameServerAutoConnectController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private DedicatedGameTicketClient ticketClient;
        [SerializeField] private DedicatedGameServerWsClient wsClient;

        [Header("Auto Run")]
        [SerializeField] private bool autoRunOnStart = false;
        [SerializeField] private float startDelaySeconds = 1f;
        [SerializeField] private bool waitForAccessToken = true;
        [SerializeField] private float waitForAccessTokenSeconds = 20f;

        [Header("Auth Refresh Gate")]
        [SerializeField] private int accessTokenRefreshSkewSeconds = 60;

        [Header("Connection Address")]
        [SerializeField] private bool useTicketConnectionAddress = true;
        [SerializeField] private bool overrideAddressInEditor = false;
        [SerializeField] private string editorHost = "127.0.0.1";
        [SerializeField] private int editorPort = 7777;
        [SerializeField] private bool editorSecureWebSocket = false;

        [Header("Auth")]
        [SerializeField] private string fallbackUserName = "unity_test_player";
        [SerializeField] private bool disconnectBeforeConnect = true;

        [Header("Transient WebSocket Connect Retry")]
        [SerializeField, Min(1)] private int websocketConnectMaxAttempts = 2;
        [SerializeField, Min(0.1f)] private float websocketConnectRetryDelaySeconds = 1f;

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;

        private CancellationTokenSource flowCts;
        private bool isRunning;

        public bool IsRunning => isRunning;
        public DedicatedGameTicketResponseDto LastTicketResponse { get; private set; }

        //* این تابع نام کاربر پیش فرض را برای مسیر auth_ticket تنظیم می کند.
        public void SetFallbackUserName(string newFallbackUserName)
        {
            if (string.IsNullOrWhiteSpace(newFallbackUserName)) return;
            fallbackUserName = newFallbackUserName.Trim();
        }

        //* این تابع رفرنس های لازم را در شروع آبجکت پیدا می کند.
        private void Awake()
        {
            EnsureReferences();
        }

        //* این تابع در صورت فعال بودن Auto Run، بعد از کمی تاخیر کل مسیر را اجرا می کند.
        private async void Start()
        {
            if (!autoRunOnStart) return;

            if (startDelaySeconds > 0f)
            {
                await Task.Delay(Mathf.RoundToInt(startDelaySeconds * 1000f));
            }

            await RunAutoTicketConnectAndAuthAsync();
        }

        //* این تابع رفرنس تیکت کلاینت و وب سوکت کلاینت را پیدا یا ایجاد می کند.
        private void EnsureReferences()
        {
            if (ticketClient == null)
            {
                ticketClient = GetComponent<DedicatedGameTicketClient>();
                if (ticketClient == null) ticketClient = gameObject.AddComponent<DedicatedGameTicketClient>();
            }

            if (wsClient == null)
            {
                wsClient = GetComponent<DedicatedGameServerWsClient>();
                if (wsClient == null) wsClient = DedicatedGameServerWsClient.Instance;
                if (wsClient == null) wsClient = gameObject.AddComponent<DedicatedGameServerWsClient>();
            }
        }

        //* این تابع از اینسپکتور کل مسیر گرفتن تیکت، اتصال و احراز را اجرا می کند.
        [ContextMenu("Auto Ticket Connect And Auth")]
        public async void Btn_AutoTicketConnectAndAuth()
        {
            await RunAutoTicketConnectAndAuthAsync();
        }

        //* این تابع از اینسپکتور جریان جاری را کنسل می کند.
        [ContextMenu("Cancel Auto Flow")]
        public void Btn_CancelAutoFlow()
        {
            CancelAutoFlow("manual_cancel");
        }

        //* این تابع مسیر خودکار را اجرا می کند: خواندن اکسس توکن، گرفتن تیکت، اتصال و auth_ticket.
        public async Task<bool> RunAutoTicketConnectAndAuthAsync()
        {
            if (isRunning)
            {
                Log("Auto flow is already running.");
                return false;
            }

            EnsureReferences();

            if (ticketClient == null)
            {
                Debug.LogError("[DedicatedGameServerAutoConnectController] DedicatedGameTicketClient is missing.");
                return false;
            }

            if (wsClient == null)
            {
                Debug.LogError("[DedicatedGameServerAutoConnectController] DedicatedGameServerWsClient is missing.");
                return false;
            }

            flowCts = new CancellationTokenSource();
            isRunning = true;

            try
            {
                if (waitForAccessToken)
                {
                    bool tokenReady = await WaitForAccessTokenAsync(flowCts.Token);

                    if (!tokenReady)
                    {
                        Debug.LogError("[DedicatedGameServerAutoConnectController] Access token was not ready.");
                        return false;
                    }
                }

                DedicatedGameTicketResponseDto ticketResponse = await ticketClient.RequestGameTicketAsync(flowCts.Token);
                LastTicketResponse = ticketResponse;

                if (ticketResponse == null || !ticketResponse.success)
                {
                    Debug.LogError("[DedicatedGameServerAutoConnectController] Ticket request failed.");
                    return false;
                }

                if (disconnectBeforeConnect && wsClient.IsConnected)
                {
                    wsClient.Disconnect("auto_reconnect");
                    await Task.Delay(250, flowCts.Token);
                }

                DedicatedGameTicketConnectionDto connection = ticketResponse.data.connection;
                DedicatedGameTicketDto ticket = ticketResponse.data.ticket;

                string host = ResolveHost(connection);
                int port = ResolvePort(connection);
                bool secure = ResolveSecure(connection);
                string path = ResolvePath(connection);

                Log("Connecting to dedicated server | host=" + host + " | port=" + port + " | secure=" + secure + " | path=" + path);

                bool connected = await ConnectDedicatedWebSocketWithRetryAsync(
                    host,
                    port,
                    secure,
                    path,
                    flowCts.Token
                );

                if (!connected)
                {
                    Debug.LogError("[DedicatedGameServerAutoConnectController] Dedicated websocket connect failed.");
                    return false;
                }

                string userId = string.IsNullOrWhiteSpace(ticketResponse.data.userId)
                    ? ticket.userId
                    : ticketResponse.data.userId;

                string userName = string.IsNullOrWhiteSpace(fallbackUserName)
                    ? userId
                    : fallbackUserName.Trim();

                bool authenticated = await wsClient.AuthenticateWithTicketAsync(
                    ticket.ticketId,
                    ticket.signature,
                    userId,
                    connection.roomId,
                    connection.serverId,
                    connection.sessionId,
                    userId,
                    userName,
                    flowCts.Token);

                if (!authenticated)
                {
                    Debug.LogError("[DedicatedGameServerAutoConnectController] Dedicated auth failed. reason=" + wsClient.LastAuthReason + " error=" + wsClient.LastError);
                    return false;
                }

                Debug.Log("[DedicatedGameServerAutoConnectController] Auto ticket connect auth ok | userId=" +
                          userId + " | roomId=" + connection.roomId + " | serverId=" + connection.serverId);

                return true;
            }
            catch (OperationCanceledException)
            {
                Debug.LogWarning("[DedicatedGameServerAutoConnectController] Auto flow cancelled.");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[DedicatedGameServerAutoConnectController] Auto flow exception | " + ex.Message);
                return false;
            }
            finally
            {
                isRunning = false;

                if (flowCts != null)
                {
                    flowCts.Dispose();
                    flowCts = null;
                }
            }
        }

        //* این تابع فقط مرحله اتصال وب سوکت را با همان تیکت و تعداد محدود دوباره تلاش می کند.
        //* در شکست اتصال، تیکت تازه نمی گیرد و فقط ترنسپورت قبلی توسط خود کلاینت پاک و دوباره ساخته می شود.
        private async Task<bool> ConnectDedicatedWebSocketWithRetryAsync(
            string host,
            int port,
            bool secure,
            string path,
            CancellationToken cancellationToken)
        {
            int maxAttempts = Mathf.Clamp(websocketConnectMaxAttempts, 1, 3);
            int retryDelayMilliseconds = Mathf.Max(100, Mathf.RoundToInt(websocketConnectRetryDelaySeconds * 1000f));

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Log(
                    "Dedicated websocket connect attempt started | attempt=" +
                    attempt +
                    "/" +
                    maxAttempts +
                    " | host=" +
                    host +
                    " | port=" +
                    port +
                    " | secure=" +
                    secure +
                    " | path=" +
                    path
                );

                bool connected = await wsClient.ConnectToDedicatedServerAsync(
                    host,
                    port,
                    secure,
                    path,
                    cancellationToken
                );

                if (connected)
                {
                    Log(
                        "Dedicated websocket connect attempt succeeded | attempt=" +
                        attempt +
                        "/" +
                        maxAttempts
                    );

                    return true;
                }

                cancellationToken.ThrowIfCancellationRequested();

                string lastError = wsClient != null && !string.IsNullOrWhiteSpace(wsClient.LastError)
                    ? wsClient.LastError.Trim()
                    : "unknown_connect_error";

                Log(
                    "Dedicated websocket connect attempt failed | attempt=" +
                    attempt +
                    "/" +
                    maxAttempts +
                    " | error=" +
                    lastError
                );

                if (attempt >= maxAttempts || !IsTransientDedicatedWebSocketConnectError(lastError)) return false;

                Log(
                    "Dedicated websocket transient failure detected. Retrying with the same ticket after " +
                    retryDelayMilliseconds +
                    " ms."
                );

                await Task.Delay(retryDelayMilliseconds, cancellationToken);
            }

            return false;
        }

        //* این تابع فقط خطاهای موقت اتصال وب سوکت را برای تلاش دوباره مجاز می داند.
        private static bool IsTransientDedicatedWebSocketConnectError(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return true;

            string value = error.Trim().ToLowerInvariant();

            return value.Contains("websocket_connect_failed")
                   || value.Contains("connect_cancelled_or_timeout")
                   || value.Contains("connect_exception")
                   || value.Contains("unable to connect")
                   || value.Contains("ssl")
                   || value.Contains("transport");
        }

        //* این تابع منتظر می ماند تا اکسس توکن آماده شود و اگر لازم بود قبل از تیکت رفرش می زند.
        private async Task<bool> WaitForAccessTokenAsync(CancellationToken cancellationToken)
        {
            float startedAt = Time.realtimeSinceStartup;
            bool refreshAttempted = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                string accessToken = SecureTokenStorage.GetAccessToken();

                if (!IsAccessTokenRefreshRequired(accessToken))
                {
                    Log("Access token is ready.");
                    return true;
                }

                if (!refreshAttempted && !string.IsNullOrWhiteSpace(SecureTokenStorage.GetRefreshToken()))
                {
                    refreshAttempted = true;
                    Log("Access token is missing, expired or near expiry. Refreshing before auto ticket flow.");

                    bool refreshed = await AuthRefreshManager.Refresh();

                    if (refreshed && !IsAccessTokenRefreshRequired(SecureTokenStorage.GetAccessToken()))
                    {
                        Log("Access token refresh before auto ticket flow succeeded.");
                        return true;
                    }

                    Log("Access token refresh before auto ticket flow failed or token is still not ready.");
                }

                if (Time.realtimeSinceStartup - startedAt >= Mathf.Max(1f, waitForAccessTokenSeconds))
                {
                    return false;
                }

                await Task.Delay(250, cancellationToken);
            }

            return false;
        }

        //* این تابع تشخیص می دهد اکسس توکن خالی، اکسپایر شده یا نزدیک اکسپایر است یا نه.
        private bool IsAccessTokenRefreshRequired(string accessToken)
        {
            if (string.IsNullOrWhiteSpace(accessToken)) return true;

            if (!TryReadJwtExpiryUnixSeconds(accessToken, out long expiresAtUnixSeconds))
            {
                return false;
            }

            long nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int safeSkewSeconds = Mathf.Clamp(accessTokenRefreshSkewSeconds, 0, 3600);

            return expiresAtUnixSeconds <= nowUnixSeconds + safeSkewSeconds;
        }

        //* این تابع زمان اکسپایر شدن توکن جی دبلیو تی را از کلیم exp می خواند.
        private static bool TryReadJwtExpiryUnixSeconds(string token, out long expiresAtUnixSeconds)
        {
            expiresAtUnixSeconds = 0;

            string payloadJson = ReadJwtPayloadJson(token);
            if (string.IsNullOrWhiteSpace(payloadJson)) return false;

            return TryExtractJsonLongValue(payloadJson, "exp", out expiresAtUnixSeconds);
        }

        //* این تابع پِیلود جی دبلیو تی را بدون وابستگی اضافه می خواند.
        private static string ReadJwtPayloadJson(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return string.Empty;

            string[] parts = token.Split('.');
            if (parts == null || parts.Length < 2) return string.Empty;

            return DecodeBase64UrlToString(parts[1]);
        }

        //* این تابع متن بیس شصت و چهار یو آر ال را دیکود می کند.
        private static string DecodeBase64UrlToString(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            string base64 = value.Replace('-', '+').Replace('_', '/');
            int padding = base64.Length % 4;
            if (padding == 2) base64 += "==";
            else if (padding == 3) base64 += "=";
            else if (padding != 0) return string.Empty;

            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        //* این تابع مقدار عددی یک کلید جیسون را بدون وابستگی اضافه می خواند.
        private static bool TryExtractJsonLongValue(string json, string key, out long value)
        {
            value = 0;

            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key)) return false;

            string pattern = "\"" + key + "\"";
            int keyIndex = json.IndexOf(pattern, StringComparison.Ordinal);
            if (keyIndex < 0) return false;

            int colonIndex = json.IndexOf(':', keyIndex + pattern.Length);
            if (colonIndex < 0) return false;

            int valueStart = colonIndex + 1;
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart])) valueStart++;

            bool quoted = valueStart < json.Length && json[valueStart] == '"';
            if (quoted) valueStart++;

            int valueEnd = valueStart;
            while (valueEnd < json.Length)
            {
                char c = json[valueEnd];

                if (quoted)
                {
                    if (c == '"') break;
                }
                else if (c == ',' || c == '}' || c == ']' || char.IsWhiteSpace(c))
                {
                    break;
                }

                valueEnd++;
            }

            if (valueEnd <= valueStart) return false;

            string rawValue = json.Substring(valueStart, valueEnd - valueStart).Trim();
            return long.TryParse(rawValue, out value);
        }

        //* این تابع هاست اتصال را از تیکت می گیرد و فقط اگر تیکت آدرس نداشت سراغ مقدار تست ادیتور می رود.
        private string ResolveHost(DedicatedGameTicketConnectionDto connection)
        {
            if (useTicketConnectionAddress && connection != null && !string.IsNullOrWhiteSpace(connection.host))
            {
                return connection.host.Trim();
            }

#if UNITY_EDITOR
            if (overrideAddressInEditor && !string.IsNullOrWhiteSpace(editorHost))
            {
                return editorHost.Trim();
            }
#endif

            return "127.0.0.1";
        }

        //* این تابع پورت اتصال را از تیکت می گیرد و فقط اگر تیکت پورت نداشت سراغ مقدار تست ادیتور می رود.
        private int ResolvePort(DedicatedGameTicketConnectionDto connection)
        {
            if (useTicketConnectionAddress && connection != null && connection.port > 0)
            {
                return connection.port;
            }

#if UNITY_EDITOR
            if (overrideAddressInEditor)
            {
                return Mathf.Max(1, editorPort);
            }
#endif

            return 7777;
        }

        //* این تابع امن بودن وب سوکت را از تیکت می خواند و فقط برای تست ادیتور سراغ مقدار دستی می رود.
        private bool ResolveSecure(DedicatedGameTicketConnectionDto connection)
        {
            if (useTicketConnectionAddress && connection != null)
            {
                return connection.secure;
            }

#if UNITY_EDITOR
            if (overrideAddressInEditor && !useTicketConnectionAddress)
            {
                return editorSecureWebSocket;
            }
#endif

            return false;
        }

        //* این تابع مسیر وب سوکت را از تیکت می خواند؛ برای نیتیو خالی و برای وب جی ال مثل game-server/7777 است.
        private string ResolvePath(DedicatedGameTicketConnectionDto connection)
        {
            if (useTicketConnectionAddress && connection != null && !string.IsNullOrWhiteSpace(connection.path))
            {
                return connection.path.Trim();
            }

            return string.Empty;
        }

        //* این تابع جریان خودکار را کنسل می کند.
        private void CancelAutoFlow(string reason)
        {
            if (flowCts != null)
            {
                flowCts.Cancel();
            }

            Log("Auto flow cancel requested | reason=" + reason);
        }

        //* این تابع هنگام حذف آبجکت، جریان جاری را کنسل می کند.
        private void OnDestroy()
        {
            CancelAutoFlow("destroyed");
        }

        //* این تابع لاگ معمولی کنترلر خودکار را چاپ می کند.
        private void Log(string message)
        {
            if (!verboseLogs) return;
            Debug.Log("[DedicatedGameServerAutoConnectController] " + message);
        }

        /*
        توضیح مکتوب فایل:
        این اسکریپت مرحله DS-7B را خودکار می کند.
        ابتدا منتظر آماده شدن اکسس توکن کاربر بعد از AuthManager می ماند.
        سپس با DedicatedGameTicketClient از نود جی اس گیم تیکت می گیرد.
        بعد DedicatedGameServerWsClient را به آدرس ددیکیتد سرور وصل می کند.
        اگر تیکت برای وب جی ال باشد، مسیر امن wss و path را از پاسخ سرور می گیرد.
        در پایان auth_ticket را ارسال می کند و منتظر auth_ok می ماند.
        این اسکریپت با GameServerClient قدیمی تداخل ندارد و فقط در مسیر DedicatedGameServer استفاده می شود.
        */
    }
}
