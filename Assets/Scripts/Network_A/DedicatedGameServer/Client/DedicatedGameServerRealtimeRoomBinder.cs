using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Tests.Realtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Network_A.DedicatedGameServer.Client
{
    public class DedicatedGameServerRealtimeRoomBinder : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private RealtimeWebSocketG7RoomLobbyTestController realtimeLobbyController;
        [SerializeField] private DedicatedGameTicketClient ticketClient;
        [SerializeField] private DedicatedGameServerAutoConnectController autoConnectController;
        [SerializeField] private DedicatedGameServerWsClient wsClient;

        [Header("UI")]
        [SerializeField] private Button connectGameServerButton;
        [SerializeField] private Button disconnectGameServerButton;
        [SerializeField] private TextMeshProUGUI statusText;

        [Header("Rules")]
        [SerializeField] private bool autoFindReferences = true;
        [SerializeField] private bool requireAccessToken = true;
        [SerializeField] private bool disconnectDedicatedOnRealtimeRoomLeft = false;
        [SerializeField] private bool disconnectDedicatedOnRealtimeDisconnected = true;
        [SerializeField] private bool refreshUiInUpdate = true;
        [SerializeField] private float uiRefreshIntervalSeconds = 0.25f;

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;
        [SerializeField] private bool logOnlyOnStateChange = true;

        private bool isConnectClickRunning;
        private bool realtimeEventsBound;
        private bool wsEventsBound;
        private float nextUiRefreshAt;

        private string lastSyncedRoomId = string.Empty;
        private string lastSyncedRoomName = string.Empty;
        private string lastButtonReason = string.Empty;
        private string lastStatus = string.Empty;

        private bool lastCanConnect;
        private bool lastCanDisconnect;
        private bool hasButtonStateCache;

        //* این تابع رفرنس ها، دکمه ها و ایونت ها را در شروع آبجکت آماده می کند.
        private void Awake()
        {
            EnsureReferences();
            BindButtons();
            BindRealtimeEvents();
            BindWsEvents();
            SyncRoomContextFromRealtime("awake", true);
            RefreshUiState(true);
        }

        //* این تابع هنگام فعال شدن آبجکت، اتصال های لازم را دوباره امن می کند.
        private void OnEnable()
        {
            EnsureReferences();
            BindButtons();
            BindRealtimeEvents();
            BindWsEvents();
            SyncRoomContextFromRealtime("enable", true);
            RefreshUiState(true);
        }

        //* این تابع وضعیت دکمه ها و کانتکست روم را با فاصله زمانی کنترل می کند.
        private void Update()
        {
            if (!refreshUiInUpdate) return;
            if (Time.realtimeSinceStartup < nextUiRefreshAt) return;

            nextUiRefreshAt = Time.realtimeSinceStartup + Mathf.Max(0.05f, uiRefreshIntervalSeconds);
            SyncRoomContextFromRealtime("update", false);
            RefreshUiState(false);
        }

        //* این تابع هنگام غیرفعال شدن آبجکت، ایونت ها و دکمه ها را آزاد می کند.
        private void OnDisable()
        {
            UnbindRealtimeEvents();
            UnbindWsEvents();
            UnbindButtons();
        }

        //* این تابع کلیک دکمه اتصال گیم سرور را اجرا می کند.
        public async void Btn_ConnectGameServer()
        {
            await ConnectGameServerAsync();
        }

        //* این تابع کلیک دکمه قطع گیم سرور را اجرا می کند.
        public void Btn_DisconnectGameServer()
        {
            if (wsClient == null)
            {
                SetStatus("Dedicated WS client is missing.", true);
                RefreshUiState(true);
                return;
            }

            if (!wsClient.IsConnected)
            {
                SetStatus("Game server is already disconnected.", true);
                RefreshUiState(true);
                return;
            }

            wsClient.Disconnect("manual_game_server_disconnect");
            RefreshUiState(true);
        }

        //* این تابع مسیر اتصال تیکت، وب سوکت و احراز ددیکیتد را اجرا می کند.
        public async Task<bool> ConnectGameServerAsync()
        {
            if (isConnectClickRunning)
            {
                Log("Connect skipped. Binder flow is already running.", true);
                return false;
            }

            EnsureReferences();

            if (!CanStartGameServerConnect(out string reason))
            {
                SetStatus("Game server connect disabled: " + reason, true);
                Log("Connect blocked | reason=" + reason, true);
                RefreshUiState(true);
                return false;
            }

            isConnectClickRunning = true;
            RefreshUiState(true);

            try
            {
                bool roomSynced = SyncRoomContextFromRealtime("before_connect", true);

                if (!roomSynced)
                {
                    SetStatus("Room context sync failed.", true);
                    Log("Room context sync failed before connect.", true);
                    return false;
                }

                ApplyRealtimeUserNameToAutoConnect();

                SetStatus("Connecting to game server...", true);
                LogCurrentContext("before_auto_flow");

                bool ok = await autoConnectController.RunAutoTicketConnectAndAuthAsync();

                SetStatus(ok ? "Game server connected." : "Game server connect failed.", true);
                Log("Auto flow result=" + ok, true);

                return ok;
            }
            finally
            {
                isConnectClickRunning = false;
                RefreshUiState(true);
            }
        }

        //* این تابع کانتکست روم ریل تایم را با تیکت کلاینت همسان می کند.
        public bool SyncRoomContextFromRealtime(string source)
        {
            return SyncRoomContextFromRealtime(source, false);
        }

        //* این تابع کانتکست روم ریل تایم را با کنترل لاگ تکراری همسان می کند.
        public bool SyncRoomContextFromRealtime(string source, bool forceLog)
        {
            if (realtimeLobbyController == null)
            {
                Log("Room sync skipped. Realtime controller is missing. source=" + Safe(source), forceLog);
                return false;
            }

            if (ticketClient == null)
            {
                Log("Room sync skipped. Ticket client is missing. source=" + Safe(source), forceLog);
                return false;
            }

            if (!realtimeLobbyController.IsRealtimeReadyState || !realtimeLobbyController.IsJoinedRoom)
            {
                ClearRoomSyncCache();
                return false;
            }

            string roomId = Safe(realtimeLobbyController.CurrentRoomId);
            string roomName = Safe(realtimeLobbyController.CurrentRoomName);

            if (string.IsNullOrWhiteSpace(roomId)) return false;

            bool changed = roomId != lastSyncedRoomId || roomName != lastSyncedRoomName;

            if (!changed && !forceLog) return true;

            ticketClient.SetRoomContext(roomId, roomName);

            lastSyncedRoomId = roomId;
            lastSyncedRoomName = roomName;

            Log("Room context synced | source=" + Safe(source) + " | roomId=" + roomId + " | roomName=" + roomName, forceLog || changed);

            return true;
        }

        //* این تابع رفرنس های مورد نیاز را از آبجکت فعلی یا صحنه پیدا می کند.
        private void EnsureReferences()
        {
            if (!autoFindReferences) return;

            if (realtimeLobbyController == null) realtimeLobbyController = FindObjectOfType<RealtimeWebSocketG7RoomLobbyTestController>();

            if (ticketClient == null)
            {
                ticketClient = GetComponent<DedicatedGameTicketClient>();
                if (ticketClient == null) ticketClient = FindObjectOfType<DedicatedGameTicketClient>();
            }

            if (autoConnectController == null)
            {
                autoConnectController = GetComponent<DedicatedGameServerAutoConnectController>();
                if (autoConnectController == null) autoConnectController = FindObjectOfType<DedicatedGameServerAutoConnectController>();
            }

            if (wsClient == null)
            {
                wsClient = GetComponent<DedicatedGameServerWsClient>();
                if (wsClient == null) wsClient = DedicatedGameServerWsClient.Instance;
                if (wsClient == null) wsClient = FindObjectOfType<DedicatedGameServerWsClient>();
            }
        }

        //* این تابع دکمه ها را به تابع های داخلی وصل می کند.
        private void BindButtons()
        {
            if (connectGameServerButton != null)
            {
                connectGameServerButton.onClick.RemoveListener(Btn_ConnectGameServer);
                connectGameServerButton.onClick.AddListener(Btn_ConnectGameServer);
            }

            if (disconnectGameServerButton != null)
            {
                disconnectGameServerButton.onClick.RemoveListener(Btn_DisconnectGameServer);
                disconnectGameServerButton.onClick.AddListener(Btn_DisconnectGameServer);
            }
        }

        //* این تابع اتصال دکمه ها را پاک می کند.
        private void UnbindButtons()
        {
            if (connectGameServerButton != null) connectGameServerButton.onClick.RemoveListener(Btn_ConnectGameServer);
            if (disconnectGameServerButton != null) disconnectGameServerButton.onClick.RemoveListener(Btn_DisconnectGameServer);
        }

        //* این تابع ایونت های ریل تایم را وصل می کند.
        private void BindRealtimeEvents()
        {
            if (realtimeEventsBound || realtimeLobbyController == null) return;

            realtimeLobbyController.OnRoomJoinedFor3D += HandleRealtimeRoomJoined;
            realtimeLobbyController.OnRoomLeftFor3D += HandleRealtimeRoomLeft;
            realtimeLobbyController.OnRealtimeDisconnectedFor3D += HandleRealtimeDisconnected;

            realtimeEventsBound = true;
        }

        //* این تابع ایونت های ریل تایم را قطع می کند.
        private void UnbindRealtimeEvents()
        {
            if (!realtimeEventsBound || realtimeLobbyController == null) return;

            realtimeLobbyController.OnRoomJoinedFor3D -= HandleRealtimeRoomJoined;
            realtimeLobbyController.OnRoomLeftFor3D -= HandleRealtimeRoomLeft;
            realtimeLobbyController.OnRealtimeDisconnectedFor3D -= HandleRealtimeDisconnected;

            realtimeEventsBound = false;
        }

        //* این تابع ایونت های وب سوکت ددیکیتد را وصل می کند.
        private void BindWsEvents()
        {
            if (wsEventsBound || wsClient == null) return;

            wsClient.Connected += HandleDedicatedConnected;
            wsClient.Disconnected += HandleDedicatedDisconnected;
            wsClient.Authenticated += HandleDedicatedAuthenticated;
            wsClient.AuthFailed += HandleDedicatedAuthFailed;

            wsEventsBound = true;
        }

        //* این تابع ایونت های وب سوکت ددیکیتد را قطع می کند.
        private void UnbindWsEvents()
        {
            if (!wsEventsBound || wsClient == null) return;

            wsClient.Connected -= HandleDedicatedConnected;
            wsClient.Disconnected -= HandleDedicatedDisconnected;
            wsClient.Authenticated -= HandleDedicatedAuthenticated;
            wsClient.AuthFailed -= HandleDedicatedAuthFailed;

            wsEventsBound = false;
        }

        //* این تابع بعد از ورود به روم ریل تایم، کانتکست را برای تیکت آماده می کند.
        private void HandleRealtimeRoomJoined(string roomId)
        {
            SyncRoomContextFromRealtime("room_joined_event", true);
            SetStatus("Realtime room joined. Game server is ready to connect.", true);
            RefreshUiState(true);
        }

        //* این تابع بعد از خروج از روم ریل تایم، وضعیت اتصال گیم سرور را کنترل می کند.
        private void HandleRealtimeRoomLeft(string roomId)
        {
            if (disconnectDedicatedOnRealtimeRoomLeft && wsClient != null && wsClient.IsConnected)
            {
                wsClient.Disconnect("realtime_room_left");
            }

            SetStatus("Realtime room left.", true);
            ClearRoomSyncCache();
            RefreshUiState(true);
        }

        //* این تابع بعد از قطع ریل تایم، اتصال ددیکیتد را هم تمیز قطع می کند.
        private void HandleRealtimeDisconnected(string reason)
        {
            if (disconnectDedicatedOnRealtimeDisconnected && wsClient != null && wsClient.IsConnected)
            {
                wsClient.Disconnect("realtime_disconnected");
            }

            SetStatus("Realtime disconnected: " + Safe(reason), true);
            ClearRoomSyncCache();
            RefreshUiState(true);
        }

        private void HandleDedicatedConnected()
        {
            SetStatus("Dedicated server socket connected.", true);
            RefreshUiState(true);
        }

        private void HandleDedicatedDisconnected(string reason)
        {
            SetStatus("Dedicated server disconnected: " + Safe(reason), true);
            RefreshUiState(true);
        }

        private void HandleDedicatedAuthenticated()
        {
            SetStatus("Dedicated server authenticated.", true);
            RefreshUiState(true);
        }

        private void HandleDedicatedAuthFailed(string reason)
        {
            SetStatus("Dedicated auth failed: " + Safe(reason), true);
            RefreshUiState(true);
        }

        //* این تابع شرط های فعال شدن دکمه اتصال به گیم سرور را بررسی می کند.
        private bool CanStartGameServerConnect(out string reason)
        {
            reason = string.Empty;

            if (realtimeLobbyController == null)
            {
                reason = "realtime_controller_missing";
                return false;
            }

            if (ticketClient == null)
            {
                reason = "ticket_client_missing";
                return false;
            }

            if (autoConnectController == null)
            {
                reason = "auto_connect_controller_missing";
                return false;
            }

            if (wsClient == null)
            {
                reason = "ws_client_missing";
                return false;
            }

            if (isConnectClickRunning || autoConnectController.IsRunning)
            {
                reason = "flow_running";
                return false;
            }

            if (!realtimeLobbyController.IsRealtimeReadyState)
            {
                reason = "realtime_not_ready";
                return false;
            }

            if (!realtimeLobbyController.IsJoinedRoom)
            {
                reason = "room_not_joined";
                return false;
            }

            if (string.IsNullOrWhiteSpace(realtimeLobbyController.CurrentRoomId))
            {
                reason = "room_id_empty";
                return false;
            }

            if (requireAccessToken && string.IsNullOrWhiteSpace(SecureTokenStorage.GetAccessToken()))
            {
                reason = "access_token_missing";
                return false;
            }

            if (wsClient.IsAuthenticated)
            {
                reason = "already_authenticated";
                return false;
            }

            reason = "ready";
            return true;
        }

        private void RefreshUiState()
        {
            RefreshUiState(false);
        }

        //* این تابع وضعیت دکمه ها را فقط هنگام تغییر لاگ می کند.
        private void RefreshUiState(bool forceLog)
        {
            bool canConnect = CanStartGameServerConnect(out string reason);
            bool canDisconnect = wsClient != null && wsClient.IsConnected;

            if (connectGameServerButton != null) connectGameServerButton.interactable = canConnect;
            if (disconnectGameServerButton != null) disconnectGameServerButton.interactable = canDisconnect;

            bool changed = !hasButtonStateCache ||
                           canConnect != lastCanConnect ||
                           canDisconnect != lastCanDisconnect ||
                           reason != lastButtonReason;

            if (changed || forceLog)
            {
                Log("Button state | connect=" + canConnect + " | disconnect=" + canDisconnect + " | reason=" + reason, forceLog || changed);
            }

            lastCanConnect = canConnect;
            lastCanDisconnect = canDisconnect;
            lastButtonReason = reason;
            hasButtonStateCache = true;
        }

        //* این تابع نام یوزر ریل تایم را به مسیر auth_ticket می دهد.
        private void ApplyRealtimeUserNameToAutoConnect()
        {
            if (autoConnectController == null || realtimeLobbyController == null) return;

            string userName = Safe(realtimeLobbyController.CurrentUserName);
            if (string.IsNullOrWhiteSpace(userName)) userName = Safe(realtimeLobbyController.CurrentUserId);

            autoConnectController.SetFallbackUserName(userName);
        }

        //* این تابع کانتکست فعلی را برای دیباگ چاپ می کند.
        private void LogCurrentContext(string source)
        {
            if (realtimeLobbyController == null) return;

            Log("Context | source=" + Safe(source)
                + " | roomId=" + Safe(realtimeLobbyController.CurrentRoomId)
                + " | roomName=" + Safe(realtimeLobbyController.CurrentRoomName)
                + " | userId=" + Safe(realtimeLobbyController.CurrentUserId)
                + " | userName=" + Safe(realtimeLobbyController.CurrentUserName)
                + " | realtimeReady=" + realtimeLobbyController.IsRealtimeReadyState
                + " | joined=" + realtimeLobbyController.IsJoinedRoom, true);
        }

        private void ClearRoomSyncCache()
        {
            lastSyncedRoomId = string.Empty;
            lastSyncedRoomName = string.Empty;

            if (ticketClient != null)
            {
                ticketClient.ClearRoomContext();
            }
        }

        private void SetStatus(string value, bool forceLog)
        {
            string safeValue = Safe(value);
            bool changed = safeValue != lastStatus;

            if (statusText != null) statusText.text = safeValue;

            if (changed || forceLog)
            {
                Log("Status=" + safeValue, forceLog || changed);
            }

            lastStatus = safeValue;
        }

        private void Log(string message, bool force)
        {
            if (!verboseLogs) return;
            if (logOnlyOnStateChange && !force) return;
            Debug.Log("[DedicatedGameServerRealtimeRoomBinder] " + message);
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
    }
}
