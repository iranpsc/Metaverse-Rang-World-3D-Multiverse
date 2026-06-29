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

        [Header("Connection Address")]
        [SerializeField] private bool useTicketConnectionAddress = true;
        [SerializeField] private bool overrideAddressInEditor = true;
        [SerializeField] private string editorHost = "127.0.0.1";
        [SerializeField] private int editorPort = 7777;
        [SerializeField] private bool editorSecureWebSocket = false;

        [Header("Auth")]
        [SerializeField] private string fallbackUserName = "unity_test_player";
        [SerializeField] private bool disconnectBeforeConnect = true;

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
                bool secure = ResolveSecure();

                Log("Connecting to dedicated server | host=" + host + " | port=" + port + " | secure=" + secure);

                bool connected = await wsClient.ConnectToDedicatedServerAsync(host, port, secure, flowCts.Token);

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

        //* این تابع منتظر می ماند تا اکسس توکن بعد از لاگین آماده شود.
        private async Task<bool> WaitForAccessTokenAsync(CancellationToken cancellationToken)
        {
            float startedAt = Time.realtimeSinceStartup;

            while (!cancellationToken.IsCancellationRequested)
            {
                string accessToken = SecureTokenStorage.GetAccessToken();

                if (!string.IsNullOrWhiteSpace(accessToken))
                {
                    Log("Access token is ready.");
                    return true;
                }

                if (Time.realtimeSinceStartup - startedAt >= Mathf.Max(1f, waitForAccessTokenSeconds))
                {
                    return false;
                }

                await Task.Delay(250, cancellationToken);
            }

            return false;
        }

        //* این تابع هاست اتصال را از تیکت یا مقدار تست ادیتور انتخاب می کند.
        private string ResolveHost(DedicatedGameTicketConnectionDto connection)
        {
#if UNITY_EDITOR
            if (overrideAddressInEditor && !string.IsNullOrWhiteSpace(editorHost))
            {
                return editorHost.Trim();
            }
#endif

            if (useTicketConnectionAddress && connection != null && !string.IsNullOrWhiteSpace(connection.host))
            {
                return connection.host.Trim();
            }

            return "127.0.0.1";
        }

        //* این تابع پورت اتصال را از تیکت یا مقدار تست ادیتور انتخاب می کند.
        private int ResolvePort(DedicatedGameTicketConnectionDto connection)
        {
#if UNITY_EDITOR
            if (overrideAddressInEditor)
            {
                return Mathf.Max(1, editorPort);
            }
#endif

            if (useTicketConnectionAddress && connection != null && connection.port > 0)
            {
                return connection.port;
            }

            return 7777;
        }

        //* این تابع امن بودن وب سوکت را برای تست ادیتور تعیین می کند.
        private bool ResolveSecure()
        {
#if UNITY_EDITOR
            if (overrideAddressInEditor)
            {
                return editorSecureWebSocket;
            }
#endif

            return false;
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
        در پایان auth_ticket را ارسال می کند و منتظر auth_ok می ماند.
        این اسکریپت با GameServerClient قدیمی تداخل ندارد و فقط در مسیر DedicatedGameServer استفاده می شود.
        */
    }
}
