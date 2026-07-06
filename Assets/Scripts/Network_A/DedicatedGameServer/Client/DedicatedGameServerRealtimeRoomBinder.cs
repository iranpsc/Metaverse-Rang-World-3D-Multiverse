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
        [SerializeField] private G7ThreeDModeController threeDModeController;

        [Header("UI")]
        [SerializeField] private Button connectGameServerButton;
        [SerializeField] private Button disconnectGameServerButton;
        [SerializeField] private TextMeshProUGUI statusText;
        [SerializeField] private TextMeshProUGUI gameServerStateText;

        [Header("Rules")]
        [SerializeField] private bool autoFindReferences = true;
        [SerializeField] private bool requireAccessToken = true;
        [SerializeField] private bool disconnectDedicatedOnRealtimeRoomLeft = false;
        [SerializeField] private bool disconnectDedicatedOnRealtimeDisconnected = true;
        [SerializeField] private bool refreshUiInUpdate = true;
        [SerializeField] private float uiRefreshIntervalSeconds = 0.25f;

        [Header("Reconnect UI State")]
        [SerializeField] private bool disableGameServerConnectButtonWhileRealtimeReconnects = true;
        [SerializeField] private bool keepInsideGameServerStatusDuringReconnect = true;
        [SerializeField] private string outsideGameServerStatusMessage = "Outside Game Server";
        [SerializeField] private string connectingGameServerStatusMessage = "Connecting to Game Server";
        [SerializeField] private string insideGameServerStatusMessage = "Inside Game Server";
        [SerializeField] private string reconnectingInsideGameServerStatusMessage = "Inside Game Server - reconnecting";

        [Header("Manual Exit World Cleanup")]
        [SerializeField] private GameObject sharedWorld3DRoot;
        [SerializeField] private Transform[] runtimeCloneRoots;
        [SerializeField] private bool cleanupSharedWorldOnlyOnUserExit = true;
        [SerializeField] private bool disableSharedWorldRootOnUserExit = true;
        [SerializeField] private bool destroyRuntimeCloneRootChildrenOnUserExit = true;
        [SerializeField] private bool neverDestroySharedWorld3DRoot = true;
        [SerializeField] private bool skipSharedWorldRootWhenUsedAsRuntimeCloneRoot = true;
        [SerializeField] private bool activateSharedWorldRootOnRoomEntry = false;
        [SerializeField] private bool activateSharedWorldRootOnDedicatedConnected = false;
        [SerializeField] private bool activateSharedWorldRootOnDedicatedAuthenticated = true;
        [SerializeField] private bool requireDedicatedConnectionBeforeSharedWorldRootActivation = true;
        [SerializeField] private bool activateThreeDModeAfterDedicatedAuthenticated = true;
        [SerializeField] private bool ensureLocalPlayerAfterDedicatedAuthenticated = true;
        [SerializeField] private bool useThreeDModeControllerWorldRootFallback = true;

        [Header("Manual Exit Camera Safety")]
        [SerializeField] private bool detachMainCameraBeforeRuntimeCloneCleanup = true;
        [SerializeField] private Camera mainCameraOverride;
        [SerializeField] private Transform mainCameraSafeParent;
        [SerializeField] private bool keepMainCameraWorldPoseOnDetach = true;

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
        private string lastGameServerStateStatus = string.Empty;

        private bool lastCanConnect;
        private bool lastCanDisconnect;
        private bool hasButtonStateCache;
        private bool manualExitWorldCleanupApplied;
        private bool realtimeReconnectInProgress;
        private bool wasInsideDedicatedGameServer;

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
                SetGameServerStateText(outsideGameServerStatusMessage, true);
                SetStatus("Game server and realtime room are already disconnected.", true);
                CleanupSharedWorldAfterUserExit("manual_game_server_disconnect_already_disconnected");
                ClearRoomSyncCache();
                RefreshUiState(true);
                return true;
            }

            isDisconnectClickRunning = true;
            realtimeReconnectInProgress = false;
            wasInsideDedicatedGameServer = false;
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
                        CleanupSharedWorldAfterUserExit(safeReason + ":room_left");
                        ClearRoomSyncCache();
                        SetGameServerStateText(outsideGameServerStatusMessage, true);
                        SetStatus("Game server disconnected and realtime room left.", true);
                    }
                    else
                    {
                        SetStatus("Game server disconnected, but realtime room leave failed.", true);
                    }

                    return leaveOk;
                }

                CleanupSharedWorldAfterUserExit(safeReason + ":dedicated_only");
                ClearRoomSyncCache();
                SetGameServerStateText(outsideGameServerStatusMessage, true);
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

                SetGameServerStateText(connectingGameServerStatusMessage, true);
                SetStatus("Connecting to game server...", true);
                LogCurrentContext("before_auto_flow");

                bool ok = await autoConnectController.RunAutoTicketConnectAndAuthAsync();

                if (ok)
                {
                    realtimeReconnectInProgress = false;
                    wasInsideDedicatedGameServer = true;
                    EnsureDedicatedWorldActiveAfterAuthenticated("connect_flow_ok");
                    SetGameServerStateText(insideGameServerStatusMessage, true);
                    SetStatus(insideGameServerStatusMessage, true);
                }
                else
                {
                    wasInsideDedicatedGameServer = false;
                    SetGameServerStateText(outsideGameServerStatusMessage, true);
                    SetStatus("Game server connect failed.", true);
                }

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

            if (threeDModeController == null) threeDModeController = FindObjectOfType<G7ThreeDModeController>(true);

            if (sharedWorld3DRoot == null && useThreeDModeControllerWorldRootFallback && threeDModeController != null && threeDModeController.World3DRoot != null)
            {
                sharedWorld3DRoot = threeDModeController.World3DRoot;
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
                realtimeLobbyController.OnRealtimeConnectionLostForReconnectFor3D += HandleRealtimeConnectionLostForReconnect;
                realtimeLobbyController.OnRealtimeReconnectFailedPermanentlyFor3D += HandleRealtimeReconnectFailedPermanently;
                boundWebSocketRealtimeLobbyController = realtimeLobbyController;
            }

            if (grpcStreamingRealtimeLobbyController != null && boundGrpcStreamingRealtimeLobbyController == null)
            {
                grpcStreamingRealtimeLobbyController.OnRoomJoinedFor3D += HandleRealtimeRoomJoined;
                grpcStreamingRealtimeLobbyController.OnRoomLeftFor3D += HandleRealtimeRoomLeft;
                grpcStreamingRealtimeLobbyController.OnRealtimeDisconnectedFor3D += HandleRealtimeDisconnected;
                grpcStreamingRealtimeLobbyController.OnRealtimeConnectionLostForReconnectFor3D += HandleRealtimeConnectionLostForReconnect;
                grpcStreamingRealtimeLobbyController.OnRealtimeReconnectFailedPermanentlyFor3D += HandleRealtimeReconnectFailedPermanently;
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
            boundWebSocketRealtimeLobbyController.OnRealtimeConnectionLostForReconnectFor3D -= HandleRealtimeConnectionLostForReconnect;
            boundWebSocketRealtimeLobbyController.OnRealtimeReconnectFailedPermanentlyFor3D -= HandleRealtimeReconnectFailedPermanently;
            boundWebSocketRealtimeLobbyController = null;
        }

        //* این تابع ایونت های کنترلر جی آر پی سی را قطع می کند.
        private void UnbindGrpcStreamingRealtimeEvents()
        {
            if (boundGrpcStreamingRealtimeLobbyController == null) return;

            boundGrpcStreamingRealtimeLobbyController.OnRoomJoinedFor3D -= HandleRealtimeRoomJoined;
            boundGrpcStreamingRealtimeLobbyController.OnRoomLeftFor3D -= HandleRealtimeRoomLeft;
            boundGrpcStreamingRealtimeLobbyController.OnRealtimeDisconnectedFor3D -= HandleRealtimeDisconnected;
            boundGrpcStreamingRealtimeLobbyController.OnRealtimeConnectionLostForReconnectFor3D -= HandleRealtimeConnectionLostForReconnect;
            boundGrpcStreamingRealtimeLobbyController.OnRealtimeReconnectFailedPermanentlyFor3D -= HandleRealtimeReconnectFailedPermanently;
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
            manualExitWorldCleanupApplied = false;
            realtimeReconnectInProgress = false;
            ActivateSharedWorldForRoomEntry("realtime_room_joined:" + Safe(roomId));
            SyncRoomContextFromRealtime("room_joined_event", true);
            if (!IsInsideGameServer()) SetGameServerStateText(outsideGameServerStatusMessage, true);
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

            realtimeReconnectInProgress = false;
            wasInsideDedicatedGameServer = false;
            CleanupSharedWorldAfterUserExit("manual_realtime_room_left:" + Safe(roomId));
            SetGameServerStateText(outsideGameServerStatusMessage, true);
            SetStatus("Realtime room left.", true);
            ClearRoomSyncCache();
            RefreshUiState(true);
        }

        //* این تابع بعد از قطع ریل تایم، اتصال ددیکیتد را هم تمیز قطع می کند.
        private void HandleRealtimeDisconnected(string reason)
        {
            bool permanentReconnectFailure = IsPermanentReconnectFailureReason(reason);
            bool manualExit = isDisconnectClickRunning || IsManualExitReason(reason);

            if (permanentReconnectFailure)
            {
                realtimeReconnectInProgress = false;
                wasInsideDedicatedGameServer = false;
                RefreshUiState(true);
                return;
            }

            if (manualExit && disconnectDedicatedOnRealtimeDisconnected && wsClient != null && wsClient.IsConnected)
            {
                wsClient.Disconnect("realtime_disconnected_manual_exit");
            }

            if (manualExit)
            {
                realtimeReconnectInProgress = false;
                wasInsideDedicatedGameServer = false;
                CleanupSharedWorldAfterUserExit("manual_realtime_disconnected:" + Safe(reason));
                ClearRoomSyncCache();
                SetGameServerStateText(outsideGameServerStatusMessage, true);
                SetStatus("Realtime disconnected after user exit: " + Safe(reason), true);
            }
            else
            {
                HandleRealtimeConnectionLostForReconnect(reason);
            }

            RefreshUiState(true);
        }

        private void HandleRealtimeConnectionLostForReconnect(string reason)
        {
            realtimeReconnectInProgress = true;
            if (IsInsideGameServer()) wasInsideDedicatedGameServer = true;

            if (keepInsideGameServerStatusDuringReconnect && wasInsideDedicatedGameServer)
            {
                SetGameServerStateText(reconnectingInsideGameServerStatusMessage, true);
                SetStatus(reconnectingInsideGameServerStatusMessage + ": " + Safe(reason), true);
            }
            else
            {
                SetStatus("Realtime connection lost. Reconnect is allowed: " + Safe(reason), true);
            }

            RefreshUiState(true);
        }

        private void HandleRealtimeReconnectFailedPermanently(string reason)
        {
            string safeReason = "permanent_reconnect_failure:" + Safe(reason);
            realtimeReconnectInProgress = false;
            wasInsideDedicatedGameServer = false;

            if (disconnectDedicatedOnRealtimeDisconnected && wsClient != null && wsClient.IsConnected)
            {
                wsClient.Disconnect("realtime_reconnect_failed_permanently");
            }

            CleanupSharedWorldAfterUserExit(safeReason);
            ClearRoomSyncCache();
            SetGameServerStateText(outsideGameServerStatusMessage, true);
            SetStatus("Reconnect failed permanently. Game server room was cleared locally: " + Safe(reason), true);
            RefreshUiState(true);
        }

        private bool IsManualExitReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;

            string value = reason.Trim().ToLowerInvariant();
            return value.Contains("manual")
                   || value.Contains("user_exit")
                   || value.Contains("leave_room")
                   || value.Contains("realtime_room_left")
                   || value.Contains("manual_game_server_disconnect");
        }

        private bool IsPermanentReconnectFailureReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;

            string value = reason.Trim().ToLowerInvariant();
            return value.Contains("permanent_reconnect_failure")
                   || value.Contains("reconnect_failed_permanently");
        }

        private bool IsInsideGameServer()
        {
            return wsClient != null && (wsClient.IsConnected || wsClient.IsAuthenticated);
        }

        private void ActivateSharedWorldForRoomEntry(string reason)
        {
            manualExitWorldCleanupApplied = false;

            string safeReason = Safe(reason);
            bool dedicatedActivation = IsDedicatedSharedWorldActivationReason(safeReason);

            if (requireDedicatedConnectionBeforeSharedWorldRootActivation && !dedicatedActivation)
            {
                Log("Shared world root activation skipped before dedicated game server connection. reason=" + safeReason, true);
                return;
            }

            if (dedicatedActivation)
            {
                if (IsDedicatedAuthenticatedSharedWorldActivationReason(safeReason))
                {
                    if (!activateSharedWorldRootOnDedicatedAuthenticated) return;
                }
                else
                {
                    if (!activateSharedWorldRootOnDedicatedConnected) return;
                }
            }
            else
            {
                if (!activateSharedWorldRootOnRoomEntry) return;
            }

            if (sharedWorld3DRoot == null)
            {
                Log("Shared world root activation skipped. Reference is missing. reason=" + safeReason, true);
                return;
            }

            if (!sharedWorld3DRoot.activeSelf)
            {
                sharedWorld3DRoot.SetActive(true);
                Log("Shared world root activated after dedicated game server connection. reason=" + safeReason, true);
                return;
            }

            Log("Shared world root already active after dedicated game server connection. reason=" + safeReason, true);
        }

        private bool IsDedicatedSharedWorldActivationReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;

            string value = reason.Trim().ToLowerInvariant();
            return value.Contains("dedicated_socket_connected")
                   || value.Contains("dedicated_connected")
                   || value.Contains("dedicated_authenticated")
                   || value.Contains("game_server_connected")
                   || value.Contains("game_server_authenticated");
        }

        private bool IsDedicatedAuthenticatedSharedWorldActivationReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;

            string value = reason.Trim().ToLowerInvariant();
            return value.Contains("dedicated_authenticated")
                   || value.Contains("game_server_authenticated");
        }

        private void CleanupSharedWorldAfterUserExit(string reason)
        {
            if (!cleanupSharedWorldOnlyOnUserExit) return;
            if (manualExitWorldCleanupApplied) return;

            manualExitWorldCleanupApplied = true;
            string safeReason = Safe(reason);

            DetachMainCameraBeforeRuntimeCloneCleanup(safeReason);

            if (destroyRuntimeCloneRootChildrenOnUserExit) DestroyRuntimeCloneRootChildren(safeReason);

            if (disableSharedWorldRootOnUserExit && sharedWorld3DRoot != null)
            {
                sharedWorld3DRoot.SetActive(false);
                Log("Shared world root disabled after user exit. reason=" + safeReason, true);
            }
        }

        private void DetachMainCameraBeforeRuntimeCloneCleanup(string reason)
        {
            if (!detachMainCameraBeforeRuntimeCloneCleanup) return;

            Camera mainCamera = mainCameraOverride != null ? mainCameraOverride : Camera.main;
            if (mainCamera == null) return;

            Transform cameraTransform = mainCamera.transform;
            if (cameraTransform == null || cameraTransform.parent == null) return;
            if (!ShouldDetachMainCameraForRuntimeCleanup(cameraTransform)) return;

            Vector3 worldPosition = cameraTransform.position;
            Quaternion worldRotation = cameraTransform.rotation;
            Vector3 worldScale = cameraTransform.lossyScale;

            cameraTransform.SetParent(mainCameraSafeParent, keepMainCameraWorldPoseOnDetach);
            cameraTransform.position = worldPosition;
            cameraTransform.rotation = worldRotation;

            if (!keepMainCameraWorldPoseOnDetach) cameraTransform.localScale = worldScale;

            Log("Main camera detached before runtime clone cleanup. reason=" + reason, true);
        }

        private bool ShouldDetachMainCameraForRuntimeCleanup(Transform cameraTransform)
        {
            if (cameraTransform == null) return false;

            if (runtimeCloneRoots != null)
            {
                for (int i = 0; i < runtimeCloneRoots.Length; i++)
                {
                    Transform root = runtimeCloneRoots[i];
                    if (root != null && cameraTransform.IsChildOf(root)) return true;
                }
            }

            return sharedWorld3DRoot != null && cameraTransform.IsChildOf(sharedWorld3DRoot.transform);
        }

        private bool IsSharedWorldRootTransform(Transform target)
        {
            if (target == null || sharedWorld3DRoot == null) return false;
            return target == sharedWorld3DRoot.transform;
        }

        private void DestroyRuntimeCloneRootChildren(string reason)
        {
            if (runtimeCloneRoots == null || runtimeCloneRoots.Length == 0) return;

            int destroyedCount = 0;
            int skippedProtectedRootCount = 0;

            for (int rootIndex = 0; rootIndex < runtimeCloneRoots.Length; rootIndex++)
            {
                Transform root = runtimeCloneRoots[rootIndex];
                if (root == null) continue;

                if (neverDestroySharedWorld3DRoot && skipSharedWorldRootWhenUsedAsRuntimeCloneRoot && IsSharedWorldRootTransform(root))
                {
                    skippedProtectedRootCount++;
                    Log("Shared_World_3D_Root was used as a runtime clone root and was skipped. Use a child Runtime_Clones_Root for cloned objects. reason=" + reason, true);
                    continue;
                }

                for (int childIndex = root.childCount - 1; childIndex >= 0; childIndex--)
                {
                    Transform child = root.GetChild(childIndex);
                    if (child == null) continue;
                    if (neverDestroySharedWorld3DRoot && IsSharedWorldRootTransform(child))
                    {
                        skippedProtectedRootCount++;
                        continue;
                    }

                    Destroy(child.gameObject);
                    destroyedCount++;
                }
            }

            if (destroyedCount > 0)
            {
                Log("Runtime clone children destroyed after user exit. count=" + destroyedCount + " | reason=" + reason, true);
            }

            if (skippedProtectedRootCount > 0)
            {
                Log("Protected Shared_World_3D_Root was not destroyed during runtime cleanup. skipped=" + skippedProtectedRootCount + " | reason=" + reason, true);
            }
        }

        private void EnsureDedicatedWorldActiveAfterAuthenticated(string source)
        {
            string safeSource = Safe(source);

            if (threeDModeController == null) threeDModeController = FindObjectOfType<G7ThreeDModeController>(true);

            if (sharedWorld3DRoot == null && useThreeDModeControllerWorldRootFallback && threeDModeController != null && threeDModeController.World3DRoot != null)
            {
                sharedWorld3DRoot = threeDModeController.World3DRoot;
            }

            ActivateSharedWorldForRoomEntry("dedicated_authenticated:" + safeSource);

            if (threeDModeController == null)
            {
                Log("3D mode activation skipped after dedicated auth. G7ThreeDModeController is missing. source=" + safeSource, true);
                return;
            }

            if (activateThreeDModeAfterDedicatedAuthenticated && !threeDModeController.IsThreeDModeActive)
            {
                threeDModeController.EnterThreeDMode();
                Log("3D mode entered after dedicated auth. source=" + safeSource, true);
                return;
            }

            if (ensureLocalPlayerAfterDedicatedAuthenticated)
            {
                threeDModeController.EnsureLocalPlayerSpawned();
                Log("Local player ensured after dedicated auth. source=" + safeSource, true);
            }
        }

        private void HandleDedicatedConnected()
        {
            if (activateSharedWorldRootOnDedicatedConnected) ActivateSharedWorldForRoomEntry("dedicated_socket_connected");
            SetGameServerStateText(connectingGameServerStatusMessage, true);
            SetStatus("Dedicated server socket connected.", true);
            RefreshUiState(true);
        }

        private void HandleDedicatedDisconnected(string reason)
        {
            bool manualExit = isDisconnectClickRunning || IsManualExitReason(reason);
            bool permanentReconnectFailure = IsPermanentReconnectFailureReason(reason);

            if (manualExit || permanentReconnectFailure)
            {
                wasInsideDedicatedGameServer = false;
                if (permanentReconnectFailure) realtimeReconnectInProgress = false;
                SetGameServerStateText(outsideGameServerStatusMessage, true);
                SetStatus("Dedicated server disconnected: " + Safe(reason), true);
            }
            else if (keepInsideGameServerStatusDuringReconnect && (realtimeReconnectInProgress || wasInsideDedicatedGameServer))
            {
                realtimeReconnectInProgress = true;
                SetGameServerStateText(reconnectingInsideGameServerStatusMessage, true);
                SetStatus(reconnectingInsideGameServerStatusMessage + ": " + Safe(reason), true);
            }
            else
            {
                SetGameServerStateText(outsideGameServerStatusMessage, true);
                SetStatus("Dedicated server disconnected: " + Safe(reason), true);
            }

            RefreshUiState(true);
        }

        private void HandleDedicatedAuthenticated()
        {
            realtimeReconnectInProgress = false;
            wasInsideDedicatedGameServer = true;
            EnsureDedicatedWorldActiveAfterAuthenticated("dedicated_authenticated_event");
            SetGameServerStateText(insideGameServerStatusMessage, true);
            SetStatus(insideGameServerStatusMessage, true);
            RefreshUiState(true);
        }

        private void HandleDedicatedAuthFailed(string reason)
        {
            wasInsideDedicatedGameServer = false;
            SetGameServerStateText(outsideGameServerStatusMessage, true);
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

            if (disableGameServerConnectButtonWhileRealtimeReconnects && realtimeReconnectInProgress)
            {
                reason = "realtime_reconnect_in_progress | controller=" + Safe(snapshot.controllerKind);
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

        private void SetGameServerStateText(string value, bool forceLog)
        {
            string safeValue = Safe(value);
            bool changed = safeValue != lastGameServerStateStatus;

            if (gameServerStateText != null) gameServerStateText.text = safeValue;

            if (changed || forceLog)
            {
                Log("GameServerState=" + safeValue, forceLog || changed);
            }

            lastGameServerStateStatus = safeValue;
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
