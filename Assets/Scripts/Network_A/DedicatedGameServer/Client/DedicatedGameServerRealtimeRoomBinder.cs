using System;
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
        [SerializeField] private RealtimeGrpcStreamingG7RoomLobbyTestController grpcStreamingRealtimeLobbyController;
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
        private bool isDisconnectClickRunning;
        private float nextUiRefreshAt;

        private string lastSyncedRoomId = string.Empty;
        private string lastSyncedRoomName = string.Empty;
        private string lastButtonReason = string.Empty;
        private string lastStatus = string.Empty;

        private bool lastCanConnect;
        private bool lastCanDisconnect;
        private bool hasButtonStateCache;

        private RealtimeWebSocketG7RoomLobbyTestController boundWebSocketRealtimeLobbyController;
        private RealtimeGrpcStreamingG7RoomLobbyTestController boundGrpcStreamingRealtimeLobbyController;

        private struct RealtimeRoomSnapshot
        {
            public bool hasController;
            public string controllerKind;
            public bool ready;
            public bool joined;
            public string roomId;
            public string roomName;
            public string userId;
            public string userName;
        }

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
            EnsureReferences();
            BindRealtimeEvents();
            BindWsEvents();
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

        //* این تابع کلیک دکمه خروج از گیم سرور و روم ریل تایم را اجرا می کند.
        public async void Btn_DisconnectGameServer()
        {
            await DisconnectGameServerAndLeaveRoomAsync("manual_game_server_disconnect");
        }

        //* این تابع گیم سرور را قطع می کند و از روم ریل تایم همان گیم سرور هم خارج می شود.
        public async Task<bool> DisconnectGameServerAndLeaveRoomAsync(string reason)
        {
            if (isDisconnectClickRunning)
            {
                Log("Disconnect skipped. Disconnect flow is already running.", true);
                return false;
            }

            EnsureReferences();
            BindRealtimeEvents();
            BindWsEvents();

            string safeReason = string.IsNullOrWhiteSpace(reason) ? "manual_game_server_disconnect" : reason.Trim();

            bool hasDedicatedConnection = wsClient != null && wsClient.IsConnected;
            bool hasRealtimeRoom = TryReadRealtimeSnapshot(out RealtimeRoomSnapshot snapshot) &&
                                   snapshot.joined &&
                                   !string.IsNullOrWhiteSpace(snapshot.roomId);

            if (!hasDedicatedConnection && !hasRealtimeRoom)
            {
                SetStatus("Game server and realtime room are already disconnected.", true);
                ClearRoomSyncCache();
                RefreshUiState(true);
                return true;
            }

            isDisconnectClickRunning = true;
            RefreshUiState(true);

            try
            {
                if (hasDedicatedConnection)
                {
                    SetStatus("Disconnecting dedicated server socket...", true);
                    wsClient.Disconnect(safeReason);
                }
                else
                {
                    Log("Dedicated socket is already disconnected before room leave.", true);
                }

                if (hasRealtimeRoom)
                {
                    SetStatus("Leaving realtime room...", true);
                    bool leaveOk = await LeaveRealtimeRoomAfterDedicatedDisconnectAsync(snapshot);

                    if (leaveOk)
                    {
                        ClearRoomSyncCache();
                        SetStatus("Game server disconnected and realtime room left.", true);
                    }
                    else
                    {
                        SetStatus("Game server disconnected, but realtime room leave failed.", true);
                    }

                    return leaveOk;
                }

                ClearRoomSyncCache();
                SetStatus("Game server disconnected.", true);
                return true;
            }
            finally
            {
                isDisconnectClickRunning = false;
                RefreshUiState(true);
            }
        }

        //* این تابع خروج از روم ریل تایم را از کنترلر فعال وب سوکت یا جی آر پی سی انجام می دهد.
        private async Task<bool> LeaveRealtimeRoomAfterDedicatedDisconnectAsync(RealtimeRoomSnapshot snapshot)
        {
            try
            {
                if (snapshot.controllerKind == "grpc_streaming" &&
                    grpcStreamingRealtimeLobbyController != null &&
                    grpcStreamingRealtimeLobbyController.IsJoinedRoom)
                {
                    Log("Leaving realtime room after dedicated disconnect | controller=grpc_streaming | roomId=" + Safe(snapshot.roomId), true);
                    return await grpcStreamingRealtimeLobbyController.LeaveRoomAsync();
                }

                if (snapshot.controllerKind == "websocket" &&
                    realtimeLobbyController != null &&
                    realtimeLobbyController.IsJoinedRoom)
                {
                    Log("Leaving realtime room after dedicated disconnect | controller=websocket | roomId=" + Safe(snapshot.roomId), true);
                    return await realtimeLobbyController.LeaveRoomAsync();
                }

                if (grpcStreamingRealtimeLobbyController != null && grpcStreamingRealtimeLobbyController.IsJoinedRoom)
                {
                    Log("Leaving realtime room after dedicated disconnect | controller=grpc_streaming_fallback | roomId=" + Safe(grpcStreamingRealtimeLobbyController.CurrentRoomId), true);
                    return await grpcStreamingRealtimeLobbyController.LeaveRoomAsync();
                }

                if (realtimeLobbyController != null && realtimeLobbyController.IsJoinedRoom)
                {
                    Log("Leaving realtime room after dedicated disconnect | controller=websocket_fallback | roomId=" + Safe(realtimeLobbyController.CurrentRoomId), true);
                    return await realtimeLobbyController.LeaveRoomAsync();
                }

                Log("Realtime room leave skipped. No joined realtime room was found.", true);
                return true;
            }
            catch (Exception error)
            {
                Log("Realtime room leave failed after dedicated disconnect | error=" + error.Message, true);
                return false;
            }
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
            BindRealtimeEvents();
            BindWsEvents();

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
            if (!TryReadRealtimeSnapshot(out RealtimeRoomSnapshot snapshot))
            {
                Log("Room sync skipped. Realtime controller is missing. source=" + Safe(source), forceLog);
                return false;
            }

            if (ticketClient == null)
            {
                Log("Room sync skipped. Ticket client is missing. source=" + Safe(source), forceLog);
                return false;
            }

            if (!snapshot.ready || !snapshot.joined)
            {
                ClearRoomSyncCache();
                return false;
            }

            string roomId = Safe(snapshot.roomId);
            string roomName = Safe(snapshot.roomName);

            if (string.IsNullOrWhiteSpace(roomId)) return false;

            bool changed = roomId != lastSyncedRoomId || roomName != lastSyncedRoomName;

            if (!changed && !forceLog) return true;

            ticketClient.SetRoomContext(roomId, roomName);

            lastSyncedRoomId = roomId;
            lastSyncedRoomName = roomName;

            Log("Room context synced | source=" + Safe(source) + " | controller=" + Safe(snapshot.controllerKind) + " | roomId=" + roomId + " | roomName=" + roomName, forceLog || changed);

            return true;
        }

        //* این تابع رفرنس های مورد نیاز را از آبجکت فعلی یا صحنه پیدا می کند.
        private void EnsureReferences()
        {
            if (!autoFindReferences) return;

            if (realtimeLobbyController == null) realtimeLobbyController = FindObjectOfType<RealtimeWebSocketG7RoomLobbyTestController>();
            if (grpcStreamingRealtimeLobbyController == null) grpcStreamingRealtimeLobbyController = FindObjectOfType<RealtimeGrpcStreamingG7RoomLobbyTestController>();

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

        //* این تابع ایونت های ریل تایم را برای وب سوکت و جی آر پی سی وصل می کند.
        private void BindRealtimeEvents()
        {
            if (boundWebSocketRealtimeLobbyController != realtimeLobbyController)
            {
                UnbindWebSocketRealtimeEvents();
            }

            if (boundGrpcStreamingRealtimeLobbyController != grpcStreamingRealtimeLobbyController)
            {
                UnbindGrpcStreamingRealtimeEvents();
            }

            if (realtimeLobbyController != null && boundWebSocketRealtimeLobbyController == null)
            {
                realtimeLobbyController.OnRoomJoinedFor3D += HandleRealtimeRoomJoined;
                realtimeLobbyController.OnRoomLeftFor3D += HandleRealtimeRoomLeft;
                realtimeLobbyController.OnRealtimeDisconnectedFor3D += HandleRealtimeDisconnected;
                boundWebSocketRealtimeLobbyController = realtimeLobbyController;
            }

            if (grpcStreamingRealtimeLobbyController != null && boundGrpcStreamingRealtimeLobbyController == null)
            {
                grpcStreamingRealtimeLobbyController.OnRoomJoinedFor3D += HandleRealtimeRoomJoined;
                grpcStreamingRealtimeLobbyController.OnRoomLeftFor3D += HandleRealtimeRoomLeft;
                grpcStreamingRealtimeLobbyController.OnRealtimeDisconnectedFor3D += HandleRealtimeDisconnected;
                boundGrpcStreamingRealtimeLobbyController = grpcStreamingRealtimeLobbyController;
            }
        }

        //* این تابع ایونت های ریل تایم را قطع می کند.
        private void UnbindRealtimeEvents()
        {
            UnbindWebSocketRealtimeEvents();
            UnbindGrpcStreamingRealtimeEvents();
        }

        //* این تابع ایونت های کنترلر وب سوکت را قطع می کند.
        private void UnbindWebSocketRealtimeEvents()
        {
            if (boundWebSocketRealtimeLobbyController == null) return;

            boundWebSocketRealtimeLobbyController.OnRoomJoinedFor3D -= HandleRealtimeRoomJoined;
            boundWebSocketRealtimeLobbyController.OnRoomLeftFor3D -= HandleRealtimeRoomLeft;
            boundWebSocketRealtimeLobbyController.OnRealtimeDisconnectedFor3D -= HandleRealtimeDisconnected;
            boundWebSocketRealtimeLobbyController = null;
        }

        //* این تابع ایونت های کنترلر جی آر پی سی را قطع می کند.
        private void UnbindGrpcStreamingRealtimeEvents()
        {
            if (boundGrpcStreamingRealtimeLobbyController == null) return;

            boundGrpcStreamingRealtimeLobbyController.OnRoomJoinedFor3D -= HandleRealtimeRoomJoined;
            boundGrpcStreamingRealtimeLobbyController.OnRoomLeftFor3D -= HandleRealtimeRoomLeft;
            boundGrpcStreamingRealtimeLobbyController.OnRealtimeDisconnectedFor3D -= HandleRealtimeDisconnected;
            boundGrpcStreamingRealtimeLobbyController = null;
        }

        //* این تابع ایونت های وب سوکت ددیکیتد را وصل می کند.
        private void BindWsEvents()
        {
            if (wsClient == null) return;

            wsClient.Connected -= HandleDedicatedConnected;
            wsClient.Disconnected -= HandleDedicatedDisconnected;
            wsClient.Authenticated -= HandleDedicatedAuthenticated;
            wsClient.AuthFailed -= HandleDedicatedAuthFailed;

            wsClient.Connected += HandleDedicatedConnected;
            wsClient.Disconnected += HandleDedicatedDisconnected;
            wsClient.Authenticated += HandleDedicatedAuthenticated;
            wsClient.AuthFailed += HandleDedicatedAuthFailed;
        }

        //* این تابع ایونت های وب سوکت ددیکیتد را قطع می کند.
        private void UnbindWsEvents()
        {
            if (wsClient == null) return;

            wsClient.Connected -= HandleDedicatedConnected;
            wsClient.Disconnected -= HandleDedicatedDisconnected;
            wsClient.Authenticated -= HandleDedicatedAuthenticated;
            wsClient.AuthFailed -= HandleDedicatedAuthFailed;
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

            if (!TryReadRealtimeSnapshot(out RealtimeRoomSnapshot snapshot))
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

            if (isConnectClickRunning || isDisconnectClickRunning || autoConnectController.IsRunning)
            {
                reason = "flow_running";
                return false;
            }

            if (!snapshot.ready)
            {
                reason = "realtime_not_ready | controller=" + Safe(snapshot.controllerKind);
                return false;
            }

            if (!snapshot.joined)
            {
                reason = "room_not_joined | controller=" + Safe(snapshot.controllerKind);
                return false;
            }

            if (string.IsNullOrWhiteSpace(snapshot.roomId))
            {
                reason = "room_id_empty | controller=" + Safe(snapshot.controllerKind);
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

            reason = "ready | controller=" + Safe(snapshot.controllerKind);
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
            bool hasJoinedRealtimeRoom = TryReadRealtimeSnapshot(out RealtimeRoomSnapshot disconnectSnapshot) &&
                                         disconnectSnapshot.joined &&
                                         !string.IsNullOrWhiteSpace(disconnectSnapshot.roomId);
            bool canDisconnect = isDisconnectClickRunning ||
                                 (wsClient != null && wsClient.IsConnected) ||
                                 hasJoinedRealtimeRoom;

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
            if (autoConnectController == null) return;
            if (!TryReadRealtimeSnapshot(out RealtimeRoomSnapshot snapshot)) return;

            string userName = Safe(snapshot.userName);
            if (string.IsNullOrWhiteSpace(userName)) userName = Safe(snapshot.userId);

            autoConnectController.SetFallbackUserName(userName);
        }

        //* این تابع کانتکست فعلی را برای دیباگ چاپ می کند.
        private void LogCurrentContext(string source)
        {
            if (!TryReadRealtimeSnapshot(out RealtimeRoomSnapshot snapshot)) return;

            Log("Context | source=" + Safe(source)
                + " | controller=" + Safe(snapshot.controllerKind)
                + " | roomId=" + Safe(snapshot.roomId)
                + " | roomName=" + Safe(snapshot.roomName)
                + " | userId=" + Safe(snapshot.userId)
                + " | userName=" + Safe(snapshot.userName)
                + " | realtimeReady=" + snapshot.ready
                + " | joined=" + snapshot.joined, true);
        }

        //* این تابع وضعیت فعلی کنترلر ریل تایم فعال را می خواند.
        private bool TryReadRealtimeSnapshot(out RealtimeRoomSnapshot snapshot)
        {
            if (realtimeLobbyController != null && realtimeLobbyController.IsJoinedRoom)
            {
                snapshot = CreateWebSocketSnapshot();
                return true;
            }

            if (grpcStreamingRealtimeLobbyController != null && grpcStreamingRealtimeLobbyController.IsJoinedRoom)
            {
                snapshot = CreateGrpcStreamingSnapshot();
                return true;
            }

            if (realtimeLobbyController != null && realtimeLobbyController.IsRealtimeReadyState)
            {
                snapshot = CreateWebSocketSnapshot();
                return true;
            }

            if (grpcStreamingRealtimeLobbyController != null && grpcStreamingRealtimeLobbyController.IsRealtimeReadyState)
            {
                snapshot = CreateGrpcStreamingSnapshot();
                return true;
            }

            if (realtimeLobbyController != null)
            {
                snapshot = CreateWebSocketSnapshot();
                return true;
            }

            if (grpcStreamingRealtimeLobbyController != null)
            {
                snapshot = CreateGrpcStreamingSnapshot();
                return true;
            }

            snapshot = new RealtimeRoomSnapshot();
            return false;
        }

        //* این تابع وضعیت کنترلر وب سوکت ریل تایم را به شکل مشترک آماده می کند.
        private RealtimeRoomSnapshot CreateWebSocketSnapshot()
        {
            return new RealtimeRoomSnapshot
            {
                hasController = realtimeLobbyController != null,
                controllerKind = "websocket",
                ready = realtimeLobbyController != null && realtimeLobbyController.IsRealtimeReadyState,
                joined = realtimeLobbyController != null && realtimeLobbyController.IsJoinedRoom,
                roomId = realtimeLobbyController != null ? Safe(realtimeLobbyController.CurrentRoomId) : string.Empty,
                roomName = realtimeLobbyController != null ? Safe(realtimeLobbyController.CurrentRoomName) : string.Empty,
                userId = realtimeLobbyController != null ? Safe(realtimeLobbyController.CurrentUserId) : string.Empty,
                userName = realtimeLobbyController != null ? Safe(realtimeLobbyController.CurrentUserName) : string.Empty
            };
        }

        //* این تابع وضعیت کنترلر جی آر پی سی ریل تایم را به شکل مشترک آماده می کند.
        private RealtimeRoomSnapshot CreateGrpcStreamingSnapshot()
        {
            return new RealtimeRoomSnapshot
            {
                hasController = grpcStreamingRealtimeLobbyController != null,
                controllerKind = "grpc_streaming",
                ready = grpcStreamingRealtimeLobbyController != null && grpcStreamingRealtimeLobbyController.IsRealtimeReadyState,
                joined = grpcStreamingRealtimeLobbyController != null && grpcStreamingRealtimeLobbyController.IsJoinedRoom,
                roomId = grpcStreamingRealtimeLobbyController != null ? Safe(grpcStreamingRealtimeLobbyController.CurrentRoomId) : string.Empty,
                roomName = grpcStreamingRealtimeLobbyController != null ? Safe(grpcStreamingRealtimeLobbyController.CurrentRoomName) : string.Empty,
                userId = grpcStreamingRealtimeLobbyController != null ? Safe(grpcStreamingRealtimeLobbyController.CurrentUserId) : string.Empty,
                userName = grpcStreamingRealtimeLobbyController != null ? Safe(grpcStreamingRealtimeLobbyController.CurrentUserName) : string.Empty
            };
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
