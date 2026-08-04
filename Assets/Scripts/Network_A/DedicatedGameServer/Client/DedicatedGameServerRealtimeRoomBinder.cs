using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Bootstrap;
using Network_A.Realtime.Controllers;
using Network_A.Tests.Realtime;
using Network_A.UI;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Network_A.DedicatedGameServer.Client
{
    public class DedicatedGameServerRealtimeRoomBinder : MonoBehaviour
    {
        public static DedicatedGameServerRealtimeRoomBinder Instance { get; private set; }

        public event Action<string> StatusChanged;
        public event Action<string> GameServerStateChanged;
        public event Action<bool> DisconnectAvailabilityChanged;

        public string CurrentStatus => lastStatus;
        public string CurrentGameServerState => lastGameServerStateStatus;
        public bool IsConnectFlowRunning => isConnectClickRunning || (autoConnectController != null && autoConnectController.IsRunning);
        public bool IsDisconnectFlowRunning => isDisconnectClickRunning || isSafetyDedicatedCloseRunning;
        public bool HasActiveDedicatedConnection => wsClient != null && (wsClient.IsConnected || wsClient.IsAuthenticated);
        public bool HasUnclosedDedicatedSession => HasActiveDedicatedConnection || wasInsideDedicatedGameServer || dedicatedCloseConfirmationRequired;
        public bool CanDisconnect => !isDisconnectClickRunning && (IsConnectFlowRunning || HasUnclosedDedicatedSession || (realtimeRoomGameServerManager != null && realtimeRoomGameServerManager.IsJoinedRoom));

        [Header("References")]
        [SerializeField] private RealtimeRoomGameServerManager realtimeRoomGameServerManager;
        [SerializeField] private RealtimeWebSocketG7RoomLobbyTestController realtimeLobbyController;
        [SerializeField] private RealtimeGrpcStreamingG7RoomLobbyTestController grpcStreamingRealtimeLobbyController;
        [SerializeField] private DedicatedGameTicketClient ticketClient;
        [SerializeField] private DedicatedGameServerAutoConnectController autoConnectController;
        [SerializeField] private DedicatedGameServerWsClient wsClient;
        [SerializeField] private G7ThreeDModeController threeDModeController;
        [SerializeField] private DedicatedRemotePlayerViewController dedicatedRemotePlayerViewController;

        [Header("UI")]
        [SerializeField] private Button connectGameServerButton;
        [SerializeField] private Button disconnectGameServerButton;
        [SerializeField] private TextMeshProUGUI statusText;
        [SerializeField] private TextMeshProUGUI gameServerStateText;

        [Header("Server Debug Progress UI")]
        [SerializeField] private GameObject pnlServerDebug;
        [SerializeField] private TextMeshProUGUI txtServerDebugTitle;
        [SerializeField] private TextMeshProUGUI txtServerDebugMessage;
        [SerializeField] private TextMeshProUGUI txtServerDebugTechnical;
        [SerializeField] private Button btnServerDebugClose;
        [SerializeField] private Button btnServerDebugRetry;
        [SerializeField] private Button btnServerDebugRelogin;
        [SerializeField] private bool autoFindServerDebugUiByName = true;
        [SerializeField] private string serverDebugPanelObjectName = "Pnl_ServerDebug";
        [SerializeField] private bool openServerDebugPanelOnGameServerConnectProgress = true;
        [SerializeField] private bool closeServerDebugPanelOnGameServerConnectSuccess = false;
        [SerializeField] private bool hideServerDebugCloseWhileGameServerConnectRunning = true;
        [SerializeField] private string gameServerConnectProgressTitle = "اتصال به Game Server";
        [SerializeField] private string gameServerPreparingMessage = "در حال آماده‌سازی اتصال به Game Server...";
        [SerializeField] private string gameServerRoomSyncMessage = "در حال هماهنگ‌سازی اطلاعات روم برای Game Server...";
        [SerializeField] private string gameServerTicketAndSocketMessage = "در حال دریافت تیکت، اتصال و احراز هویت Game Server...";
        [SerializeField] private string gameServerConnectSuccessDebugMessage = "ورود به Game Server با موفقیت انجام شد.";
        [SerializeField] private string gameServerConnectFailureDebugMessage = "اتصال به Game Server انجام نشد. لطفاً دوباره تلاش کنید.";

        [Header("Dedicated Connect Timeout And Global Message")]
        [SerializeField, Min(5f)] private float dedicatedConnectFlowTimeoutSeconds = 25f;
        [SerializeField] private bool publishDedicatedConnectMessagesGlobally = true;
        [SerializeField] private string dedicatedTicketRequestProgressMessage = "در حال دریافت تیکت ورود به Game Server...";
        [SerializeField] private string dedicatedSocketConnectProgressMessage = "تیکت دریافت شد. در حال اتصال به Game Server...";
        [SerializeField] private string dedicatedAuthenticationProgressMessage = "اتصال برقرار شد. در حال تأیید تیکت و ورود به Game Server...";
        [SerializeField] private string dedicatedRemainingTimeTemplate = "زمان باقی‌مانده: {0} ثانیه";
        [SerializeField] private string dedicatedConnectTimeoutMessage = "اتصال به Game Server در مهلت تعیین‌شده انجام نشد. اتصال اینترنت یا فیلترشکن را بررسی کنید و دوباره تلاش کنید.";
        private const string DedicatedConnectProgressMessageId = "GLOBAL_DEDICATED_CONNECT_PROGRESS";
        private const string DedicatedConnectErrorMessageId = "GLOBAL_DEDICATED_CONNECT_ERROR";

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
        [SerializeField] private bool autoReconnectGameServerAfterRealtimeReconnect = true;
        [SerializeField] private bool autoConnectGameServerAfterInitialRoomJoin = true;
        [SerializeField] private string reconnectingGameServerAfterRealtimeMessage = "Realtime دوباره وصل شد. در حال اتصال دوباره به Game Server...";
        [SerializeField] private string outsideGameServerStatusMessage = "Outside Game Server";
        [SerializeField] private string connectingGameServerStatusMessage = "Connecting to Game Server";
        [SerializeField] private string insideGameServerStatusMessage = "Inside Game Server";
        [SerializeField] private string reconnectingInsideGameServerStatusMessage = "Inside Game Server - reconnecting";
        [SerializeField] private bool showServerDebugPanelImmediatelyOnDedicatedConnectionLost = true;
        [SerializeField] private bool showServerDebugCloseDuringDedicatedReconnect = true;
        [SerializeField] private string dedicatedConnectionLostDebugMessage = "اینترنت شما قطع شده است. لطفاً اتصال اینترنت را بررسی کنید و دوباره تلاش کنید.";
        [SerializeField] private string realtimeConnectionLostWhileInsideGameServerDebugMessage = "اینترنت شما قطع شده است. لطفاً اتصال اینترنت را بررسی کنید و دوباره تلاش کنید.";
        [SerializeField] private string networkLostReconnectMessage = "اینترنت شما قطع شده است. لطفاً اتصال اینترنت را بررسی کنید و دوباره تلاش کنید.";
        private const string FixedInternetLostDebugTitle = "اتصال اینترنت قطع است";
        private const string FixedInternetLostUserMessage = "اینترنت شما قطع شده است. لطفاً اتصال اینترنت را بررسی کنید و دوباره تلاش کنید.";
        private const string FixedRealtimeTransportDropDebugTitle = "بازیابی اتصال Realtime";
        private const string FixedRealtimeTransportDropUserMessage = "ارتباط Realtime موقتاً قطع شد. در حال بازیابی اتصال...";
        [SerializeField] private string gameServerReconnectRetryMessage = "در حال تلاش برای اتصال مجدد به Game Server...";
        [SerializeField] private string gameServerReconnectSuccessMessage = "اتصال دوباره به Game Server برقرار شد.";
        [SerializeField] private string gameServerReconnectFailedMessage = "اتصال Game Server هنوز کامل نشده است. لطفاً اینترنت را بررسی کنید و دوباره تلاش کنید.";
        [SerializeField] private string gameServerReconnectPendingMessage = "اتصال Game Server هنوز کامل نشده است. لطفاً اینترنت را بررسی کنید و دوباره تلاش کنید.";
        [SerializeField] private int gameServerReconnectAfterRealtimeMaxAttempts = 120;
        [SerializeField] private float gameServerReconnectAfterRealtimeMaxSeconds = 600f;
        [SerializeField] private float gameServerReconnectAfterRealtimeRetryDelaySeconds = 3f;
        [SerializeField] private bool suppressDuplicateReconnectDebugMessages = true;

        [Header("Immediate Network Loss UI")]
        [SerializeField] private bool showInternetLostImmediatelyFromLocalReachability = true;
        [SerializeField] private float immediateNetworkLossCheckIntervalSeconds = 0.15f;
        [SerializeField] private float immediateNetworkLossConfirmSeconds = 0.35f;

        [SerializeField] private bool enableDedicatedFastNetworkProbe = false;
        [SerializeField] private float dedicatedFastNetworkProbeIntervalSeconds = 0.25f;
        [SerializeField] private int dedicatedFastNetworkProbeTimeoutMs = 500;
        [SerializeField] private int dedicatedFastNetworkProbeFailuresBeforeInternetLost = 1;

        private bool dedicatedFastProbeRunning;
        private float nextDedicatedFastProbeAt;
        private int dedicatedFastProbeFailures;

        [Header("Dedicated Presence UI")]
        [SerializeField] private bool showDedicatedPresenceInStatusText = true;
        [SerializeField] private bool showDedicatedPresenceLeftInServerDebugPanel = true;
        [SerializeField] private bool logDedicatedPresenceUiEvents = true;
        [SerializeField] private string dedicatedRemoteJoinedStatusFormat = "{0} وارد Game Server شد.";
        [SerializeField] private string dedicatedRemoteLeftStatusFormat = "{0} از Game Server خارج شد.";
        [SerializeField] private string dedicatedRemoteLeftDebugTitle = "خروج بازیکن از Game Server";
        [SerializeField] private string dedicatedRemoteLeftDebugTechnical = "Dedicated presence player_left confirmed.";

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

        [Header("Gameplay Scene Entry")]
        [SerializeField] private bool loadGameplaySceneAfterDedicatedAuthenticated = true;
        [SerializeField] private string gameplaySceneName = "Grpc_Enviroment";
        [SerializeField] private string gameplaySceneLoadingMessage = "در حال ورود به محیط سه بعدی...";

        [Header("Lobby Scene Return")]
        [SerializeField] private bool loadLobbySceneAfterManualGameServerDisconnect = true;
        [SerializeField] private string lobbySceneName = "Lobby 1";

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
        private bool isSafetyDedicatedCloseRunning;
        private bool dedicatedCloseConfirmationRequired;
        private TaskCompletionSource<bool> dedicatedConnectFlowCompletionWaiter;
        private bool dedicatedConnectFlowOwnsNextAuthenticatedEvent;
        private float nextUiRefreshAt;

        private string lastSyncedRoomId = string.Empty;
        private string lastSyncedRoomName = string.Empty;

        #region لابی عمومی سه بعدی

        private int lastSyncedRoomMaxPlayers;

        #endregion
        private string lastButtonReason = string.Empty;
        private string lastStatus = string.Empty;
        private string lastGameServerStateStatus = string.Empty;

        private bool lastCanConnect;
        private bool lastCanDisconnect;
        private bool hasButtonStateCache;
        private bool manualExitWorldCleanupApplied;
        private bool realtimeReconnectInProgress;
        private bool wasInsideDedicatedGameServer;
        private bool isAutoReconnectGameServerAfterRealtimeRunning;
        private bool hasShownConnectionLostDebugForCurrentReconnect;
        private bool hasShownReconnectFailureForCurrentReconnect;
        private bool pendingLobbyReturnAfterPermanentReconnectFailure;
        private bool permanentFailureLobbyReturnRunning;
        private float nextImmediateNetworkLossCheckAt;
        private float immediateNetworkLossStartedAt = -1f;
        private bool hasShownImmediateNetworkLossForCurrentOutage;
        private Task<bool> gameplaySceneEntryTask;

        private RealtimeRoomGameServerManager boundRealtimeRoomGameServerManager;
        private RealtimeWebSocketG7RoomLobbyTestController boundWebSocketRealtimeLobbyController;
        private RealtimeGrpcStreamingG7RoomLobbyTestController boundGrpcStreamingRealtimeLobbyController;
        private DedicatedRemotePlayerViewController boundDedicatedRemotePlayerViewController;

        private struct RealtimeRoomSnapshot
        {
            public bool hasController;
            public string controllerKind;
            public bool ready;
            public bool joined;
            public string roomId;
            public string roomName;
            public int roomMaxPlayers;
            public string userId;
            public string userName;
        }

        //* این تابع رفرنس ها، دکمه ها و ایونت ها را در شروع آبجکت آماده می کند.
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[DedicatedGameServerRealtimeRoomBinder] Duplicate Binder destroyed. Only the Lobby 1 instance may persist.");
                Destroy(gameObject);
                return;
            }

            Instance = this;
            EnsureReferences();
            BindButtons();
            BindRealtimeEvents();
            BindWsEvents();
            BindDedicatedPresenceUiEvents();
            SyncRoomContextFromRealtime("awake", true);
            RefreshUiState(true);
        }

        //* این تابع هنگام فعال شدن آبجکت، اتصال های لازم را دوباره امن می کند.
        //* این تابع هنگام فعال شدن آبجکت، اتصال های لازم را دوباره امن می کند.
        private void OnEnable()
        {
            EnsureReferences();
            BindButtons();
            BindRealtimeEvents();
            BindWsEvents();
            BindDedicatedPresenceUiEvents();

            // Awake وضعیت اولیه را قبلاً ثبت کرده است.
            // حالت غیر اجباری فقط تغییر واقعی بعد از Re-enable را لاگ می کند.
            SyncRoomContextFromRealtime("enable", false);
            RefreshUiState(false);
        }

        //* این تابع وضعیت دکمه ها و کانتکست روم را با فاصله زمانی کنترل می کند.
        private void Update()
        {
            TryStartLobbyReturnAfterPermanentReconnectFailure();

            if (!refreshUiInUpdate) return;
            if (Time.realtimeSinceStartup < nextUiRefreshAt) return;

            nextUiRefreshAt = Time.realtimeSinceStartup + Mathf.Max(0.05f, uiRefreshIntervalSeconds);

            CheckImmediateLocalNetworkLoss();
            //  CheckImmediateDedicatedNetworkProbe();

            EnsureReferences();
            BindRealtimeEvents();
            BindWsEvents();
            BindDedicatedPresenceUiEvents();
            SyncRoomContextFromRealtime("update", false);
            ClearStaleRealtimeReconnectStateWhenLobbyReady();
            RefreshUiState(false);
        }

        //* این تابع فقط بعد از پایان واقعی حلقه Reconnect در حالت Lobby، فلگ قدیمی Binder را آزاد می کند.
        //* آماده بودن ظاهری Realtime به تنهایی کافی نیست، چون هنگام شروع قطعی ممکن است State قبلی برای چند فریم هنوز Ready باشد.
        private void ClearStaleRealtimeReconnectStateWhenLobbyReady()
        {
            if (!realtimeReconnectInProgress) return;
            if (isConnectClickRunning || isDisconnectClickRunning || isAutoReconnectGameServerAfterRealtimeRunning) return;
            if (IsInsideGameServer()) return;

            if (!TryReadRealtimeSnapshot(out RealtimeRoomSnapshot snapshot)) return;
            if (!snapshot.ready || snapshot.joined) return;
            if (realtimeRoomGameServerManager != null && realtimeRoomGameServerManager.IsRecoveryRunning) return;

            if (IsRealtimeControllerReconnectLoopRunning(snapshot))
            {
                return;
            }

            realtimeReconnectInProgress = false;
            wasInsideDedicatedGameServer = false;

            ResetReconnectDebugDebounce();
            ResetImmediateLocalNetworkLossDetector("realtime_lobby_ready_after_reconnect");
            SetGameServerStateText(outsideGameServerStatusMessage, true);

            Log(
                "Stale realtime reconnect state cleared after confirmed lobby-only reconnect completion | controller=" +
                Safe(snapshot.controllerKind) +
                " | realtimeReady=" +
                snapshot.ready +
                " | joined=" +
                snapshot.joined,
                true
            );
        }

        //* این تابع وضعیت حلقه Reconnect را از کنترلر فعال WebSocket یا gRPC به شکل مشترک می خواند.
        private bool IsRealtimeControllerReconnectLoopRunning(
            RealtimeRoomSnapshot snapshot
        )
        {
            if (
                string.Equals(
                    snapshot.controllerKind,
                    "realtime_room_game_server_manager",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return realtimeRoomGameServerManager != null && realtimeRoomGameServerManager.IsRealtimeRecoveryPhaseRunning;
            }

            if (
                string.Equals(
                    snapshot.controllerKind,
                    "websocket",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return
                    realtimeLobbyController != null &&
                    realtimeLobbyController.IsRealtimeReconnectRunningState;
            }

            if (
                string.Equals(
                    snapshot.controllerKind,
                    "grpc_streaming",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return
                    grpcStreamingRealtimeLobbyController != null &&
                    grpcStreamingRealtimeLobbyController
                        .IsRealtimeReconnectRunningState;
            }

            return false;
        }

        //* این تابع هنگام غیرفعال شدن آبجکت، ایونت ها و دکمه ها را آزاد می کند.
        private void OnDisable()
        {
            UnbindRealtimeEvents();
            UnbindWsEvents();
            UnbindDedicatedPresenceUiEvents();
            UnbindButtons();
        }

        //* این تابع هنگام نابودی نمونه اصلی، رفرنس سراسری بایندر را آزاد می کند.
        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
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

        //* این تابع خروج اتمیک را اجرا می‌کند: پایان جریان اتصال، قطع تأییدشده Game Server، خروج از Room و سپس بازگشت به Lobby.
        public async Task<bool> DisconnectGameServerAndLeaveRoomAsync(string reason, bool clearSelectedBuilding = false)
        {
            if (isDisconnectClickRunning)
            {
                Log("Disconnect skipped. Coordinated disconnect flow is already running.", true);
                return false;
            }

            EnsureReferences();
            BindRealtimeEvents();
            BindWsEvents();
            pendingLobbyReturnAfterPermanentReconnectFailure = false;

            string safeReason = string.IsNullOrWhiteSpace(reason) ? "manual_game_server_disconnect" : reason.Trim();
            string normalizedReason = safeReason.ToLowerInvariant();
            bool isWholeGameExit = normalizedReason.Contains("user_exit_whole_game") || normalizedReason.Contains("exit_whole_game") || normalizedReason.Contains("application_quit");
            bool suppressLobbyReturn = isWholeGameExit || normalizedReason.Contains("before_realtime_disconnect");

            isDisconnectClickRunning = true;
            ClearDedicatedConnectGlobalMessages();
            SetRealtimeDedicatedPresenceGuard(false, safeReason);
            realtimeReconnectInProgress = false;
            RefreshUiState(true);

            try
            {
                bool connectFlowFinished = await WaitForDedicatedConnectFlowBeforeDisconnectAsync(safeReason);
                if (!connectFlowFinished)
                {
                    SetStatus("خروج انجام نشد؛ جریان اتصال Game Server هنوز کامل نشده است.", true);
                    Log("Coordinated disconnect aborted because dedicated connect flow did not finish | reason=" + Safe(safeReason), true);
                    return false;
                }

                bool hasDedicatedConnection = HasActiveDedicatedConnection;
                bool hasUnclosedDedicatedSession = HasUnclosedDedicatedSession;
                RealtimeRoomSnapshot snapshot = default(RealtimeRoomSnapshot);
                bool hasRealtimeRoom = TryReadRealtimeSnapshot(out snapshot) && snapshot.joined && !string.IsNullOrWhiteSpace(snapshot.roomId);

                if (!hasDedicatedConnection && hasUnclosedDedicatedSession)
                {
                    dedicatedCloseConfirmationRequired = true;
                    SetStatus("Session قبلی Game Server هنوز بسته‌شدن تأییدشده ندارد؛ خروج از روم تا بازیابی اتصال و بستن Session متوقف شد.", true);
                    SetGameServerStateText(reconnectingInsideGameServerStatusMessage, true);
                    Log("Realtime room leave blocked because dedicated session context exists without an active socket | roomId=" + Safe(snapshot.roomId) + " | sessionId=" + (wsClient != null ? Safe(wsClient.SessionId) : string.Empty) + " | wasInside=" + wasInsideDedicatedGameServer, true);
                    return false;
                }

                if (!hasDedicatedConnection && !hasRealtimeRoom)
                {
                    dedicatedCloseConfirmationRequired = false;
                    wasInsideDedicatedGameServer = false;
                    SetGameServerStateText(outsideGameServerStatusMessage, true);
                    SetStatus("Game server and realtime room are already disconnected.", true);
                    CleanupSharedWorldAfterUserExit("manual_game_server_disconnect_already_disconnected");
                    ClearRoomSyncCache();

                    if (!suppressLobbyReturn) await ReturnToLobbyAfterManualGameServerDisconnectAsync("already_disconnected");
                    else Log("Room list refresh skipped because whole-game exit is in progress. source=already_disconnected", true);

                    return true;
                }

                if (hasDedicatedConnection)
                {
                    SetStatus("Disconnecting dedicated server socket...", true);

                    bool dedicatedCloseCompleted = await wsClient.DisconnectAsync(safeReason, CancellationToken.None);
                    Log("Dedicated socket graceful close completed | result=" + dedicatedCloseCompleted + " | reason=" + Safe(safeReason), true);

                    if (!dedicatedCloseCompleted)
                    {
                        dedicatedCloseConfirmationRequired = true;
                        wasInsideDedicatedGameServer = true;
                        SetStatus("خروج از Game Server تأیید نشد؛ برای جلوگیری از Session معلق، خروج از روم انجام نشد.", true);
                        SetGameServerStateText(reconnectingInsideGameServerStatusMessage, true);
                        Log("Realtime room leave blocked because dedicated close was not confirmed | roomId=" + Safe(snapshot.roomId) + " | sessionId=" + (wsClient != null ? Safe(wsClient.SessionId) : string.Empty), true);
                        return false;
                    }

                    dedicatedCloseConfirmationRequired = false;
                    wasInsideDedicatedGameServer = false;
                    CleanupSharedWorldAfterUserExit(safeReason + ":dedicated_socket_disconnected_by_user");
                }
                else
                {
                    Log("Dedicated socket is already disconnected before room leave.", true);
                    CleanupSharedWorldAfterUserExit(safeReason + ":dedicated_socket_already_disconnected_by_user");
                }

                hasRealtimeRoom = TryReadRealtimeSnapshot(out snapshot) && snapshot.joined && !string.IsNullOrWhiteSpace(snapshot.roomId);

                if (hasRealtimeRoom)
                {
                    SetStatus("Leaving realtime room...", true);
                    bool leaveOk = await LeaveRealtimeRoomAfterDedicatedDisconnectAsync(snapshot, clearSelectedBuilding || isWholeGameExit);

                    if (!leaveOk)
                    {
                        SetStatus("Game server disconnected, but realtime room leave failed.", true);
                        return false;
                    }

                    dedicatedCloseConfirmationRequired = false;
                    wasInsideDedicatedGameServer = false;
                    CleanupSharedWorldAfterUserExit(safeReason + ":room_left");
                    ClearRoomSyncCache();
                    SetGameServerStateText(outsideGameServerStatusMessage, true);
                    SetStatus("Game server disconnected and realtime room left.", true);

                    if (!suppressLobbyReturn) await ReturnToLobbyAfterManualGameServerDisconnectAsync("manual_disconnect_room_left");
                    else Log("Room list refresh skipped because whole-game exit is in progress. source=manual_disconnect_room_left", true);

                    return true;
                }

                dedicatedCloseConfirmationRequired = false;
                wasInsideDedicatedGameServer = false;
                CleanupSharedWorldAfterUserExit(safeReason + ":dedicated_only");
                ClearRoomSyncCache();
                SetGameServerStateText(outsideGameServerStatusMessage, true);
                SetStatus("Game server disconnected.", true);

                if (!suppressLobbyReturn) await ReturnToLobbyAfterManualGameServerDisconnectAsync("manual_disconnect_dedicated_only");
                else Log("Room list refresh skipped because whole-game exit is in progress. source=manual_disconnect_dedicated_only", true);

                return true;
            }
            finally
            {
                isDisconnectClickRunning = false;
                RefreshUiState(true);
            }
        }

        #region تعویض لابی عمومی با روم ساختمان

        //* این تابع فقط اتصال Dedicated لابی عمومی را می بندد و خروج از روم Realtime را به مدیر اصلی واگذار می کند.
        public async Task<bool> DisconnectDedicatedForRoomSwitchAsync(string reason)
        {
            if (isDisconnectClickRunning || isSafetyDedicatedCloseRunning)
            {
                Log("Public lobby room switch dedicated disconnect skipped because another disconnect flow is running.", true);
                return false;
            }

            EnsureReferences();
            BindWsEvents();

            if (realtimeRoomGameServerManager == null || !realtimeRoomGameServerManager.IsInsidePublicLobbyRoom)
            {
                Log("Public lobby room switch dedicated disconnect rejected because current realtime room is not the public lobby.", true);
                return false;
            }

            string safeReason = string.IsNullOrWhiteSpace(reason) ? "public_lobby_to_building_room_switch" : reason.Trim();
            isDisconnectClickRunning = true;
            ClearDedicatedConnectGlobalMessages();
            SetRealtimeDedicatedPresenceGuard(false, safeReason);
            RefreshUiState(true);

            try
            {
                bool connectFlowFinished = await WaitForDedicatedConnectFlowBeforeDisconnectAsync(safeReason);

                if (!connectFlowFinished)
                {
                    SetStatus("تعویض روم متوقف شد؛ جریان اتصال Game Server هنوز کامل نشده است.", true);
                    return false;
                }

                if (HasActiveDedicatedConnection)
                {
                    SetStatus("در حال بستن اتصال لابی عمومی...", true);

                    bool dedicatedCloseCompleted = await wsClient.DisconnectAsync(safeReason, CancellationToken.None);

                    if (!dedicatedCloseCompleted)
                    {
                        dedicatedCloseConfirmationRequired = true;
                        wasInsideDedicatedGameServer = true;
                        SetGameServerStateText(reconnectingInsideGameServerStatusMessage, true);
                        SetStatus("بسته شدن اتصال لابی عمومی تأیید نشد.", true);
                        Log("Public lobby dedicated close was not confirmed before building switch.", true);
                        return false;
                    }
                }
                else if (dedicatedCloseConfirmationRequired || wasInsideDedicatedGameServer)
                {
                    dedicatedCloseConfirmationRequired = true;
                    SetGameServerStateText(reconnectingInsideGameServerStatusMessage, true);
                    SetStatus("Session قبلی لابی عمومی هنوز بسته شدن تأیید شده ندارد.", true);
                    Log("Public lobby switch blocked because dedicated session state is unresolved.", true);
                    return false;
                }

                dedicatedCloseConfirmationRequired = false;
                wasInsideDedicatedGameServer = false;
                realtimeReconnectInProgress = false;
                ResetReconnectDebugDebounce();
                CleanupSharedWorldAfterUserExit(safeReason);
                ClearRoomSyncCache();
                SetGameServerStateText(outsideGameServerStatusMessage, true);
                SetStatus("اتصال لابی عمومی بسته شد. در حال ورود به روم ساختمان...", true);
                Log("Public lobby dedicated connection closed for building room switch.", true);
                return true;
            }
            finally
            {
                isDisconnectClickRunning = false;
                RefreshUiState(true);
            }
        }

        #endregion

        //* این تابع هنگام درخواست خروج، پایان جریان احتمالی تیکت و اتصال را می‌گیرد تا Session بعد از خروج دیرهنگام ساخته نشود.
        private async Task<bool> WaitForDedicatedConnectFlowBeforeDisconnectAsync(string reason)
        {
            if (!IsConnectFlowRunning) return true;

            Log("Disconnect is waiting for active dedicated connect flow | reason=" + Safe(reason), true);
            Task completionTask = dedicatedConnectFlowCompletionWaiter != null ? dedicatedConnectFlowCompletionWaiter.Task : null;
            float timeoutSeconds = Mathf.Max(30f, dedicatedConnectFlowTimeoutSeconds + 5f);
            float deadlineAt = Time.realtimeSinceStartup + timeoutSeconds;

            while (IsConnectFlowRunning && Time.realtimeSinceStartup < deadlineAt)
            {
                if (completionTask != null && completionTask.IsCompleted) break;
                await Task.Delay(50);
            }

            if (!IsConnectFlowRunning)
            {
                Log("Active dedicated connect flow finished before disconnect.", true);
                return true;
            }

            autoConnectController?.Btn_CancelAutoFlow();
            Log("Dedicated connect flow did not finish before coordinated disconnect deadline; room exit remains blocked.", true);
            return false;
        }

        //* این تابع خروج از روم ریل تایم را از کنترلر فعال وب سوکت یا جی آر پی سی انجام می دهد.
        private async Task<bool> LeaveRealtimeRoomAfterDedicatedDisconnectAsync(RealtimeRoomSnapshot snapshot, bool clearSelectedBuilding)
        {
            try
            {
                if (snapshot.controllerKind == "realtime_room_game_server_manager" &&
                    realtimeRoomGameServerManager != null &&
                    realtimeRoomGameServerManager.IsJoinedRoom)
                {
                    Log("Leaving realtime room after dedicated disconnect | controller=realtime_room_game_server_manager | roomId=" + Safe(snapshot.roomId), true);
                    return await realtimeRoomGameServerManager.LeaveCurrentRoomAfterDedicatedDisconnectAsync(clearSelectedBuilding);
                }

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
                Log(
                    "Connect skipped. Binder flow is already running.",
                    true
                );

                return false;
            }

            EnsureReferences();
            BindRealtimeEvents();
            BindWsEvents();

            if (!isAutoReconnectGameServerAfterRealtimeRunning)
            {
                ShowGameServerDebugProgress(
                    gameServerPreparingMessage,
                    "GAME_SERVER_CONNECT_BUTTON_CLICKED",
                    "Connect To Game Server button clicked.",
                    true
                );
            }

            if (!CanStartGameServerConnect(out string reason))
            {
                SetStatus(
                    "Game server connect disabled: " + reason,
                    true
                );

                ShowGameServerDebugResult(
                    false,
                    "GAME_SERVER_CONNECT_BLOCKED",
                    "reason=" + Safe(reason)
                );

                Log("Connect blocked | reason=" + reason, true);
                RefreshUiState(true);
                return false;
            }

            isConnectClickRunning = true;
            dedicatedConnectFlowCompletionWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            dedicatedConnectFlowOwnsNextAuthenticatedEvent = false;

            ShowGameServerDebugProgress(
                gameServerPreparingMessage,
                "GAME_SERVER_CONNECT_PREPARING",
                "Preparing dedicated connect flow.",
                true
            );

            RefreshUiState(true);

            try
            {
                ShowGameServerDebugProgress(
                    gameServerRoomSyncMessage,
                    "GAME_SERVER_ROOM_CONTEXT_SYNC",
                    "Reading selected realtime room context.",
                    true
                );

                bool roomSynced =
                    SyncRoomContextFromRealtime(
                        "before_connect",
                        true
                    );

                if (!roomSynced)
                {
                    SetStatus("Room context sync failed.", true);

                    ShowGameServerDebugResult(
                        false,
                        "GAME_SERVER_ROOM_CONTEXT_SYNC_FAILED",
                        "Realtime room context could not be synced."
                    );

                    Log(
                        "Room context sync failed before connect.",
                        true
                    );

                    return false;
                }

                ApplyRealtimeUserNameToAutoConnect();

                SetGameServerStateText(
                    connectingGameServerStatusMessage,
                    true
                );

                SetStatus("Connecting to game server...", true);
                LogCurrentContext("before_auto_flow");

                ShowGameServerDebugProgress(
                    gameServerTicketAndSocketMessage,
                    "GAME_SERVER_TICKET_SOCKET_AUTH",
                    BuildGameServerDebugContext(),
                    true
                );

                // این فلگ تا زمان رسیدن همان Authenticated Event فعال می ماند.
                // Event فقط State ضروری را ثبت می کند و اعمال نهایی را دوباره اجرا نمی کند.
                dedicatedConnectFlowOwnsNextAuthenticatedEvent = true;

                PublishDedicatedConnectProgress(
                    "GAME_SERVER_TICKET_REQUEST",
                    BuildDedicatedConnectProgressMessage(
                        ResolveText(
                            dedicatedTicketRequestProgressMessage,
                            "در حال دریافت تیکت ورود به Game Server..."
                        ),
                        Mathf.CeilToInt(Mathf.Max(5f, dedicatedConnectFlowTimeoutSeconds))
                    ),
                    BuildGameServerDebugContext()
                );

                bool ok = await RunDedicatedConnectFlowWithTimeoutAsync();

                if (ok && !IsDedicatedSocketReadyForGameplay())
                {
                    Log(
                        "Auto flow reported success but dedicated socket is not connected and authenticated. " +
                        BuildGameServerDebugContext(),
                        true
                    );

                    dedicatedConnectFlowOwnsNextAuthenticatedEvent = false;
                    ok = false;
                }

                if (ok && !IsDedicatedSocketBoundToCurrentRealtimeRoom(out string roomBindingError))
                {
                    Log("Dedicated room binding validation failed | " + roomBindingError, true);
                    dedicatedConnectFlowOwnsNextAuthenticatedEvent = false;
                    await wsClient.DisconnectAsync("dedicated_room_binding_mismatch", CancellationToken.None);
                    ok = false;
                }

                if (!ok)
                {
                    dedicatedConnectFlowOwnsNextAuthenticatedEvent = false;
                }

                if (ok && isDisconnectClickRunning)
                {
                    Log("Dedicated connect completed after disconnect request; gameplay scene entry is skipped and coordinated close will continue.", true);
                    return true;
                }

                if (ok)
                {
                    ClearDedicatedConnectGlobalMessages();
                    realtimeReconnectInProgress = false;
                    wasInsideDedicatedGameServer = true;

                    bool gameplaySceneReady =
                        await EnterGameplaySceneAfterDedicatedAuthenticatedAsync(
                            "connect_flow_ok"
                        );

                    SetGameServerStateText(
                        insideGameServerStatusMessage,
                        true
                    );

                    if (!gameplaySceneReady)
                    {
                        SetStatus(
                            "ورود به صحنه سه بعدی انجام نشد.",
                            true
                        );

                        ShowGameServerDebugResult(
                            false,
                            "GAMEPLAY_SCENE_LOAD_FAILED",
                            "scene=" + Safe(gameplaySceneName)
                        );
                    }
                    else if (isAutoReconnectGameServerAfterRealtimeRunning ||
                             (realtimeRoomGameServerManager != null && realtimeRoomGameServerManager.IsRecoveryRunning))
                    {
                        string successMessage = ResolveText(
                            gameServerReconnectSuccessMessage,
                            "اتصال دوباره به Game Server برقرار شد."
                        );

                        SetStatus(successMessage, true);

                        ShowGameServerDebugProgress(
                            successMessage,
                            "GAME_SERVER_RECONNECT_SUCCESS_AFTER_REALTIME",
                            BuildGameServerDebugContext(),
                            false
                        );

                        HideServerDebugPanelAfterGameServerReconnectSuccess(
                            "connect_flow_ok_after_realtime"
                        );

                        realtimeRoomGameServerManager?.CompleteUnifiedRecoveryAfterGameServerAuthenticated(
                            "connect_flow_ok_after_realtime"
                        );

                        ResetReconnectDebugDebounce();
                    }
                    else
                    {
                        SetStatus(
                            insideGameServerStatusMessage,
                            true
                        );

                        ShowGameServerDebugResult(
                            true,
                            "GAME_SERVER_CONNECT_SUCCESS",
                            BuildGameServerDebugContext()
                        );
                    }
                }
                else
                {
                    if (isAutoReconnectGameServerAfterRealtimeRunning)
                    {
                        string pendingMessage =
                            GetGameServerReconnectPendingMessage();

                        wasInsideDedicatedGameServer = true;

                        SetGameServerStateText(
                            reconnectingInsideGameServerStatusMessage,
                            true
                        );

                        SetStatus(pendingMessage, true);

                        ShowGameServerDebugProgress(
                            pendingMessage,
                            "GAME_SERVER_RECONNECT_AFTER_REALTIME_PENDING",
                            BuildGameServerDebugContext(),
                            true
                        );
                    }
                    else
                    {
                        wasInsideDedicatedGameServer = false;

                        SetGameServerStateText(
                            outsideGameServerStatusMessage,
                            true
                        );

                        SetStatus(
                            "Game server connect failed.",
                            true
                        );

                        ShowGameServerDebugResult(
                            false,
                            "GAME_SERVER_CONNECT_FAILED",
                            BuildGameServerDebugContext()
                        );

                        ShowDedicatedConnectFailureGlobal(
                            ResolveText(
                                gameServerConnectFailureDebugMessage,
                                "اتصال به Game Server انجام نشد. لطفاً دوباره تلاش کنید."
                            ),
                            "stage=GAME_SERVER_CONNECT_FAILED | " + BuildGameServerDebugContext()
                        );
                    }
                }

                Log("Auto flow result=" + ok, true);
                return ok;
            }
            catch (TimeoutException error)
            {
                dedicatedConnectFlowOwnsNextAuthenticatedEvent = false;

                string timeoutMessage = ResolveText(
                    dedicatedConnectTimeoutMessage,
                    "اتصال به Game Server در مهلت تعیین‌شده انجام نشد. دوباره تلاش کنید."
                );

                if (isAutoReconnectGameServerAfterRealtimeRunning)
                {
                    wasInsideDedicatedGameServer = true;
                    SetGameServerStateText(reconnectingInsideGameServerStatusMessage, true);
                    SetStatus(timeoutMessage, true);

                    ShowGameServerDebugProgress(
                        timeoutMessage,
                        "GAME_SERVER_CONNECT_TIMEOUT_DURING_RECOVERY",
                        "timeoutSeconds=" + Mathf.Max(5f, dedicatedConnectFlowTimeoutSeconds).ToString("F1") + " | " + BuildGameServerDebugContext(),
                        true
                    );

                    PublishDedicatedConnectProgress(
                        "GAME_SERVER_CONNECT_TIMEOUT_DURING_RECOVERY",
                        "timeoutSeconds=" + Mathf.Max(5f, dedicatedConnectFlowTimeoutSeconds).ToString("F1") + " | " + BuildGameServerDebugContext()
                    );
                }
                else
                {
                    wasInsideDedicatedGameServer = false;
                    SetGameServerStateText(outsideGameServerStatusMessage, true);
                    SetStatus(timeoutMessage, true);

                    ShowGameServerDebugProgress(
                        timeoutMessage,
                        "GAME_SERVER_CONNECT_TIMEOUT",
                        "timeoutSeconds=" + Mathf.Max(5f, dedicatedConnectFlowTimeoutSeconds).ToString("F1") + " | " + BuildGameServerDebugContext(),
                        false
                    );

                    ShowDedicatedConnectFailureGlobal(
                        timeoutMessage,
                        "stage=GAME_SERVER_CONNECT_TIMEOUT | timeoutSeconds=" + Mathf.Max(5f, dedicatedConnectFlowTimeoutSeconds).ToString("F1") + " | " + BuildGameServerDebugContext()
                    );
                }

                Log(
                    "Game server connect timeout | timeoutSeconds=" +
                    Mathf.Max(5f, dedicatedConnectFlowTimeoutSeconds).ToString("F1") +
                    " | error=" +
                    error.Message,
                    true
                );

                return false;
            }
            catch (Exception error)
            {
                dedicatedConnectFlowOwnsNextAuthenticatedEvent = false;

                if (isAutoReconnectGameServerAfterRealtimeRunning)
                {
                    string pendingMessage =
                        GetGameServerReconnectPendingMessage();

                    wasInsideDedicatedGameServer = true;

                    SetGameServerStateText(
                        reconnectingInsideGameServerStatusMessage,
                        true
                    );

                    SetStatus(pendingMessage, true);

                    ShowGameServerDebugProgress(
                        pendingMessage,
                        "GAME_SERVER_RECONNECT_AFTER_REALTIME_PENDING_EXCEPTION",
                        "error=" + Safe(error.Message),
                        true
                    );
                }
                else
                {
                    wasInsideDedicatedGameServer = false;

                    SetGameServerStateText(
                        outsideGameServerStatusMessage,
                        true
                    );

                    SetStatus(
                        "Game server connect failed.",
                        true
                    );

                    ShowGameServerDebugResult(
                        false,
                        "GAME_SERVER_CONNECT_EXCEPTION",
                        "error=" + Safe(error.Message)
                    );

                    ShowDedicatedConnectFailureGlobal(
                        ResolveText(
                            gameServerConnectFailureDebugMessage,
                            "اتصال به Game Server انجام نشد. لطفاً دوباره تلاش کنید."
                        ),
                        "stage=GAME_SERVER_CONNECT_EXCEPTION | error=" + Safe(error.Message)
                    );
                }

                Log(
                    "Game server connect exception | error=" +
                    error.Message,
                    true
                );

                return false;
            }
            finally
            {
                isConnectClickRunning = false;
                dedicatedConnectFlowCompletionWaiter?.TrySetResult(true);
                dedicatedConnectFlowCompletionWaiter = null;
                RefreshUiState(true);
            }
        }

        //* این تابع مسیر کامل دریافت تیکت، اتصال وب‌سوکت و تأیید هویت را همراه شمارش معکوس اجرا می‌کند.
        private async Task<bool> RunDedicatedConnectFlowWithTimeoutAsync()
        {
            float timeoutSeconds = Mathf.Max(5f, dedicatedConnectFlowTimeoutSeconds);
            int timeoutMilliseconds = Mathf.Max(5000, Mathf.RoundToInt(timeoutSeconds * 1000f));
            float startedAt = Time.realtimeSinceStartup;
            string lastStage = string.Empty;
            int lastRemainingSeconds = -1;

            Task<bool> connectTask = autoConnectController.RunAutoTicketConnectAndAuthAsync();

            while (!connectTask.IsCompleted)
            {
                float elapsedSeconds = Mathf.Max(0f, Time.realtimeSinceStartup - startedAt);
                int remainingSeconds = Mathf.Max(0, Mathf.CeilToInt(timeoutSeconds - elapsedSeconds));
                string stage = ResolveDedicatedConnectProgressStage();
                string stageMessage = ResolveDedicatedConnectProgressStageMessage(stage);

                if (!string.Equals(lastStage, stage, StringComparison.Ordinal) ||
                    lastRemainingSeconds != remainingSeconds)
                {
                    lastStage = stage;
                    lastRemainingSeconds = remainingSeconds;

                    PublishDedicatedConnectProgress(
                        stage,
                        BuildDedicatedConnectProgressMessage(stageMessage, remainingSeconds),
                        "elapsedSeconds=" + elapsedSeconds.ToString("F1") +
                        " | remainingSeconds=" + remainingSeconds +
                        " | " + BuildGameServerDebugContext()
                    );
                }

                if (elapsedSeconds >= timeoutSeconds) break;

                await Task.Delay(200);
            }

            if (connectTask.IsCompleted)
            {
                return await connectTask;
            }

            autoConnectController.Btn_CancelAutoFlow();

            if (wsClient != null)
            {
                wsClient.Disconnect("dedicated_connect_timeout");
            }

            Task cleanupDelayTask = Task.Delay(1000);
            Task cleanupCompletedTask = await Task.WhenAny(connectTask, cleanupDelayTask);

            if (cleanupCompletedTask == connectTask)
            {
                try
                {
                    await connectTask;
                }
                catch
                {
                }
            }

            throw new TimeoutException(
                "Dedicated Game Server connect flow timed out after " +
                timeoutMilliseconds +
                " ms."
            );
        }

        //* این تابع پیام عمومی اتصال Game Server را با متن پیش‌فرض منتشر می‌کند.
        private void PublishDedicatedConnectProgress(string stage, string technicalDetails)
        {
            PublishDedicatedConnectProgress(
                stage,
                ResolveText(
                    gameServerTicketAndSocketMessage,
                    "در حال دریافت تیکت، اتصال و احراز هویت Game Server..."
                ),
                technicalDetails
            );
        }

        //* این تابع پیام مرحله فعلی اتصال Game Server را روی پنل سراسری منتشر می‌کند.
        private void PublishDedicatedConnectProgress(string stage, string userMessage, string technicalDetails)
        {
            if (!publishDedicatedConnectMessagesGlobally) return;

            GlobalMessageManager.Clear(DedicatedConnectErrorMessageId);

            GlobalMessageManager.Publish(
                DedicatedConnectProgressMessageId,
                GlobalMessageManager.MessageSource.DedicatedServer,
                GlobalMessageManager.MessageType.Information,
                GlobalMessageManager.Priorities.Reconnecting,
                ResolveText(gameServerConnectProgressTitle, "اتصال به Game Server"),
                string.IsNullOrWhiteSpace(userMessage)
                    ? "در حال اتصال به Game Server..."
                    : userMessage.Trim(),
                Safe(stage) + " | " + Safe(technicalDetails),
                0f,
                true,
                false
            );
        }

        //* این تابع مرحله فعلی اتصال را از وضعیت تیکت، وب‌سوکت و احراز هویت تشخیص می‌دهد.
        private string ResolveDedicatedConnectProgressStage()
        {
            if (wsClient != null && wsClient.IsAuthenticated) return "GAME_SERVER_AUTHENTICATED";
            if (wsClient != null && wsClient.IsConnected) return "GAME_SERVER_AUTHENTICATING";

            string currentUrl = wsClient != null
                ? wsClient.CurrentUrl
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(currentUrl)) return "GAME_SERVER_SOCKET_CONNECTING";
            return "GAME_SERVER_TICKET_REQUEST";
        }

        //* این تابع متن قابل فهم مرحله فعلی اتصال را برمی‌گرداند.
        private string ResolveDedicatedConnectProgressStageMessage(string stage)
        {
            if (string.Equals(stage, "GAME_SERVER_AUTHENTICATED", StringComparison.Ordinal) ||
                string.Equals(stage, "GAME_SERVER_AUTHENTICATING", StringComparison.Ordinal))
            {
                return ResolveText(
                    dedicatedAuthenticationProgressMessage,
                    "اتصال برقرار شد. در حال تأیید تیکت و ورود به Game Server..."
                );
            }

            if (string.Equals(stage, "GAME_SERVER_SOCKET_CONNECTING", StringComparison.Ordinal))
            {
                return ResolveText(
                    dedicatedSocketConnectProgressMessage,
                    "تیکت دریافت شد. در حال اتصال به Game Server..."
                );
            }

            return ResolveText(
                dedicatedTicketRequestProgressMessage,
                "در حال دریافت تیکت ورود به Game Server..."
            );
        }

        //* این تابع متن مرحله اتصال را همراه زمان باقی‌مانده برای کاربر آماده می‌کند.
        private string BuildDedicatedConnectProgressMessage(string stageMessage, int remainingSeconds)
        {
            string safeStageMessage = string.IsNullOrWhiteSpace(stageMessage)
                ? "در حال اتصال به Game Server..."
                : stageMessage.Trim();

            string template = ResolveText(
                dedicatedRemainingTimeTemplate,
                "زمان باقی‌مانده: {0} ثانیه"
            );

            string remainingText;

            try
            {
                remainingText = string.Format(template, Mathf.Max(0, remainingSeconds));
            }
            catch
            {
                remainingText = "زمان باقی‌مانده: " +
                                Mathf.Max(0, remainingSeconds) +
                                " ثانیه";
            }

            return safeStageMessage + "\n" + remainingText;
        }

        //* این تابع پیام‌های سراسری اتصال Game Server را پس از موفقیت پاک می‌کند.
        private void ClearDedicatedConnectGlobalMessages()
        {
            if (!publishDedicatedConnectMessagesGlobally) return;

            GlobalMessageManager.Clear(DedicatedConnectProgressMessageId);
            GlobalMessageManager.Clear(DedicatedConnectErrorMessageId);
        }

        //* این تابع شکست یا Timeout اتصال Game Server را با دکمه تلاش دوباره نمایش می‌دهد.
        private void ShowDedicatedConnectFailureGlobal(string message, string technicalDetails)
        {
            if (!publishDedicatedConnectMessagesGlobally) return;

            GlobalMessageManager.Clear(DedicatedConnectProgressMessageId);

            GlobalMessageManager.ShowError(
                DedicatedConnectErrorMessageId,
                ResolveText(gameServerConnectProgressTitle, "اتصال به Game Server"),
                string.IsNullOrWhiteSpace(message)
                    ? "اتصال به Game Server انجام نشد. دوباره تلاش کنید."
                    : message.Trim(),
                technicalDetails ?? string.Empty,
                0f,
                true,
                GlobalMessageManager.MessageSource.DedicatedServer,
                true,
                RetryDedicatedConnectFromGlobalMessageAsync
            );
        }

        //* این تابع تلاش دوباره پنل سراسری را به همان مسیر اصلی اتصال Game Server وصل می‌کند.
        private async Task RetryDedicatedConnectFromGlobalMessageAsync()
        {
            GlobalMessageManager.Clear(DedicatedConnectErrorMessageId);

            if (autoConnectController != null && autoConnectController.IsRunning)
            {
                autoConnectController.Btn_CancelAutoFlow();

                if (wsClient != null)
                {
                    wsClient.Disconnect("dedicated_connect_retry_reset");
                }

                float waitDeadline = Time.realtimeSinceStartup + 2f;

                while (autoConnectController.IsRunning &&
                       Time.realtimeSinceStartup < waitDeadline)
                {
                    await Task.Delay(100);
                }
            }

            await ConnectGameServerAsync();
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
            int roomMaxPlayers = Mathf.Clamp(snapshot.roomMaxPlayers, 1, 1024);

            if (string.IsNullOrWhiteSpace(roomId)) return false;

            bool changed =
                roomId != lastSyncedRoomId ||
                roomName != lastSyncedRoomName ||
                roomMaxPlayers != lastSyncedRoomMaxPlayers;

            if (!changed && !forceLog) return true;

            ticketClient.SetRoomContext(roomId, roomName, roomMaxPlayers);

            lastSyncedRoomId = roomId;
            lastSyncedRoomName = roomName;
            lastSyncedRoomMaxPlayers = roomMaxPlayers;

            Log("Room context synced | source=" + Safe(source) + " | controller=" + Safe(snapshot.controllerKind) + " | roomId=" + roomId + " | roomName=" + roomName + " | roomMaxPlayers=" + roomMaxPlayers, forceLog || changed);

            return true;
        }

        //* این تابع رفرنس های مورد نیاز را از آبجکت فعلی یا صحنه پیدا می کند.
        private void EnsureReferences()
        {
            if (realtimeRoomGameServerManager == null) realtimeRoomGameServerManager = RealtimeRoomGameServerManager.Instance;
            if (!autoFindReferences) return;

            if (realtimeRoomGameServerManager == null)
            {
                if (realtimeLobbyController == null) realtimeLobbyController = FindObjectOfType<RealtimeWebSocketG7RoomLobbyTestController>();
                if (grpcStreamingRealtimeLobbyController == null) grpcStreamingRealtimeLobbyController = FindObjectOfType<RealtimeGrpcStreamingG7RoomLobbyTestController>();
            }

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
            if (dedicatedRemotePlayerViewController == null) dedicatedRemotePlayerViewController = FindObjectOfType<DedicatedRemotePlayerViewController>(true);

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

        //* این تابع ایونت های مدیر اصلی ریل تایم را وصل می کند و کنترلرهای قدیمی را فقط به عنوان مسیر پشتیبان نگه می دارد.
        private void BindRealtimeEvents()
        {
            if (boundRealtimeRoomGameServerManager != realtimeRoomGameServerManager)
            {
                UnbindRealtimeRoomGameServerManagerEvents();
            }

            if (realtimeRoomGameServerManager != null)
            {
                UnbindWebSocketRealtimeEvents();
                UnbindGrpcStreamingRealtimeEvents();

                if (boundRealtimeRoomGameServerManager == null)
                {
                    RealtimeRoomGameServerManager.OnRealtimeReady += HandleRealtimeManagerReady;
                    RealtimeRoomGameServerManager.OnRoomJoinedFor3D += HandleRealtimeRoomJoined;
                    RealtimeRoomGameServerManager.OnRoomLeftFor3D += HandleRealtimeRoomLeft;
                    RealtimeRoomGameServerManager.OnRealtimeDisconnected += HandleRealtimeDisconnected;
                    RealtimeRoomGameServerManager.OnRealtimeReconnectFailedPermanently += HandleRealtimeReconnectFailedPermanently;
                    boundRealtimeRoomGameServerManager = realtimeRoomGameServerManager;
                }

                return;
            }

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
            UnbindRealtimeRoomGameServerManagerEvents();
            UnbindWebSocketRealtimeEvents();
            UnbindGrpcStreamingRealtimeEvents();
        }

        //* این تابع ایونت های مدیر اصلی ریل تایم را قطع می کند.
        private void UnbindRealtimeRoomGameServerManagerEvents()
        {
            if (boundRealtimeRoomGameServerManager == null) return;

            RealtimeRoomGameServerManager.OnRealtimeReady -= HandleRealtimeManagerReady;
            RealtimeRoomGameServerManager.OnRoomJoinedFor3D -= HandleRealtimeRoomJoined;
            RealtimeRoomGameServerManager.OnRoomLeftFor3D -= HandleRealtimeRoomLeft;
            RealtimeRoomGameServerManager.OnRealtimeDisconnected -= HandleRealtimeDisconnected;
            RealtimeRoomGameServerManager.OnRealtimeReconnectFailedPermanently -= HandleRealtimeReconnectFailedPermanently;
            boundRealtimeRoomGameServerManager = null;
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

        //* این تابع ایونت های ورود و خروج واقعی ددیکیتد را به وضعیت رابط کاربری وصل می کند.
        private void BindDedicatedPresenceUiEvents()
        {
            if (boundDedicatedRemotePlayerViewController != dedicatedRemotePlayerViewController)
            {
                UnbindDedicatedPresenceUiEvents();
            }

            if (dedicatedRemotePlayerViewController == null || boundDedicatedRemotePlayerViewController != null) return;

            dedicatedRemotePlayerViewController.DedicatedRemotePlayerJoinedForUi += HandleDedicatedRemotePlayerJoinedForUi;
            dedicatedRemotePlayerViewController.DedicatedRemotePlayerLeftForUi += HandleDedicatedRemotePlayerLeftForUi;
            dedicatedRemotePlayerViewController.DedicatedRoomOnlineCountChangedForUi += HandleDedicatedRoomOnlineCountChangedForUi;
            boundDedicatedRemotePlayerViewController = dedicatedRemotePlayerViewController;
        }

        //* این تابع ایونت های ورود و خروج واقعی ددیکیتد را از رابط کاربری جدا می کند.
        private void UnbindDedicatedPresenceUiEvents()
        {
            if (boundDedicatedRemotePlayerViewController == null) return;

            boundDedicatedRemotePlayerViewController.DedicatedRemotePlayerJoinedForUi -= HandleDedicatedRemotePlayerJoinedForUi;
            boundDedicatedRemotePlayerViewController.DedicatedRemotePlayerLeftForUi -= HandleDedicatedRemotePlayerLeftForUi;
            boundDedicatedRemotePlayerViewController.DedicatedRoomOnlineCountChangedForUi -= HandleDedicatedRoomOnlineCountChangedForUi;
            boundDedicatedRemotePlayerViewController = null;
        }

        //* این تابع پیام ورود واقعی پلیر ریموت را فقط از منبع ددیکیتد نشان می دهد.
        private void HandleDedicatedRemotePlayerJoinedForUi(string playerId, string displayName)
        {
            string safeName = ResolveDedicatedPresenceDisplayName(playerId, displayName);
            string message = FormatDedicatedPresenceMessage(dedicatedRemoteJoinedStatusFormat, safeName, "joined Game Server");

            if (showDedicatedPresenceInStatusText)
            {
                SetStatus(message, true);
            }

            GlobalMessageManager.ShowInfo(
                BuildDedicatedPresenceMessageId("JOINED", playerId),
                "ورود کاربر",
                safeName + " وارد محیط شد.",
                "playerId=" + Safe(playerId),
                3f,
                GlobalMessageManager.MessageSource.DedicatedServer
            );

            if (logDedicatedPresenceUiEvents)
            {
                Log("Dedicated presence UI joined | playerId=" + Safe(playerId) + " | name=" + safeName, true);
            }

            Log("Dedicated presence joined does not change gRPC room users by delta. Waiting for authoritative dedicated onlineCount.", true);
        }

        //* این تابع پیام خروج واقعی پلیر ریموت را فقط بعد از تایید ددیکیتد نشان می دهد.
        private void HandleDedicatedRemotePlayerLeftForUi(string playerId, string displayName)
        {
            string safeName = ResolveDedicatedPresenceDisplayName(playerId, displayName);
            string message = FormatDedicatedPresenceMessage(dedicatedRemoteLeftStatusFormat, safeName, "left Game Server");

            if (showDedicatedPresenceInStatusText)
            {
                SetStatus(message, true);
            }

            if (showDedicatedPresenceLeftInServerDebugPanel)
            {
                ShowGameServerDebugProgress(message, "DEDICATED_PLAYER_LEFT", dedicatedRemoteLeftDebugTechnical + " | playerId=" + Safe(playerId), false);
                if (txtServerDebugTitle != null) txtServerDebugTitle.text = string.IsNullOrWhiteSpace(dedicatedRemoteLeftDebugTitle) ? "خروج بازیکن از Game Server" : dedicatedRemoteLeftDebugTitle.Trim();
            }

            if (logDedicatedPresenceUiEvents)
            {
                Log("Dedicated presence UI left | playerId=" + Safe(playerId) + " | name=" + safeName, true);
            }

            Log("Dedicated presence left does not change gRPC room users by delta. Waiting for authoritative dedicated onlineCount.", true);
        }

        //* این تابع عدد authoritative ددیکیتد را بدون delta دستی روی شمارنده gRPC اعمال می کند.
        private void HandleDedicatedRoomOnlineCountChangedForUi(int onlineCount)
        {
            if (grpcStreamingRealtimeLobbyController == null) return;

            grpcStreamingRealtimeLobbyController.ApplyDedicatedAuthoritativeOnlineCount(
                onlineCount,
                "dedicated_room_online_count"
            );

            Log(
                "Dedicated authoritative room users forwarded to gRPC realtime UI. online=" +
                onlineCount,
                true
            );
        }

        //* این تابع نام قابل نمایش پیام حضور ددیکیتد را از نام یا شناسه پلیر می سازد.
        private static string ResolveDedicatedPresenceDisplayName(string playerId, string displayName)
        {
            if (!string.IsNullOrWhiteSpace(displayName)) return displayName.Trim();
            if (!string.IsNullOrWhiteSpace(playerId)) return playerId.Trim();
            return "Remote Player";
        }

        //* این تابع متن نهایی پیام حضور ددیکیتد را امن و بدون خطای فرمت می سازد.
        private static string FormatDedicatedPresenceMessage(string format, string displayName, string fallback)
        {
            string safeName = string.IsNullOrWhiteSpace(displayName) ? "Remote Player" : displayName.Trim();
            string safeFormat = string.IsNullOrWhiteSpace(format) ? "{0} " + fallback : format.Trim();

            try
            {
                return string.Format(safeFormat, safeName);
            }
            catch (FormatException)
            {
                return safeName + " " + fallback;
            }
        }

        //* این تابع بعد از ورود به روم ریل تایم، کانتکست را برای تیکت آماده می کند.//* این تابع بعد از ورود به روم ریل تایم، کانتکست را برای تیکت آماده می کند.
        //* این تابع قطع کامل شبکه محلی را قبل از تایم اوت وب سوکت تشخیص می دهد و پیام قطع اینترنت را فوری نشان می دهد.
        private void CheckImmediateLocalNetworkLoss()
        {
            if (!showInternetLostImmediatelyFromLocalReachability) return;
            if (Time.unscaledTime < nextImmediateNetworkLossCheckAt) return;

            nextImmediateNetworkLossCheckAt = Time.unscaledTime + Mathf.Max(0.05f, immediateNetworkLossCheckIntervalSeconds);

            if (isDisconnectClickRunning)
            {
                ResetImmediateLocalNetworkLossDetector("manual_disconnect_running");
                return;
            }

            bool shouldWatch = IsInsideGameServer() || realtimeReconnectInProgress || wasInsideDedicatedGameServer;
            if (!shouldWatch)
            {
                ResetImmediateLocalNetworkLossDetector("not_inside_game_server");
                return;
            }

            if (!IsLocalNetworkUnavailableFast())
            {
                ResetImmediateLocalNetworkLossDetector("local_network_available");
                return;
            }

            if (immediateNetworkLossStartedAt < 0f) immediateNetworkLossStartedAt = Time.unscaledTime;

            float confirmSeconds = Mathf.Max(0f, immediateNetworkLossConfirmSeconds);
            if (Time.unscaledTime - immediateNetworkLossStartedAt < confirmSeconds) return;
            if (hasShownImmediateNetworkLossForCurrentOutage) return;

            hasShownImmediateNetworkLossForCurrentOutage = true;
            hasShownConnectionLostDebugForCurrentReconnect = true;
            realtimeReconnectInProgress = true;
            if (IsInsideGameServer()) wasInsideDedicatedGameServer = true;

            string lostMessage = GetFixedInternetLostUserMessage();
            SetRealtimeDedicatedPresenceGuard(true, "immediate_local_network_loss");
            SetGameServerStateText(reconnectingInsideGameServerStatusMessage, true);
            SetStatus(lostMessage, true);
            ShowConnectionIssueDebug(
                lostMessage,
                "GAME_SERVER_NETWORK_LOST_LOCAL_REACHABILITY",
                "Application.internetReachability=NotReachable | wsConnected=" + (wsClient != null && wsClient.IsConnected) + " | wsAuthenticated=" + (wsClient != null && wsClient.IsAuthenticated)
            );

            Log("Immediate network loss UI shown before WebSocket timeout.", true);
            RefreshUiState(true);
        }

        //* این تابع وضعیت تشخیص فوری قطع شبکه محلی را ریست می کند.
        private void ResetImmediateLocalNetworkLossDetector(string reason)
        {
            immediateNetworkLossStartedAt = -1f;
            hasShownImmediateNetworkLossForCurrentOutage = false;
        }

        //* این تابع فقط قطع واقعی کارت شبکه یا نبود مسیر شبکه را سریع تشخیص می دهد و به Probe سرور وابسته نیست.
        private static bool IsLocalNetworkUnavailableFast()
        {
            if (Application.internetReachability == NetworkReachability.NotReachable) return true;

#if !UNITY_WEBGL
            try
            {
                if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable()) return true;
            }
            catch
            {
            }
#endif

            return false;
        }

        //* این تابع تشخیص می دهد آیا علت قطع ریل تایم واقعاً قطع اینترنت است یا فقط افت ترنسپورت ریل تایم.
        //* این تابع تشخیص می دهد آیا علت قطع ریل تایم واقعاً قطع مسیر شبکه است یا فقط افت مستقل ترنسپورت ریل تایم.
        private static bool ShouldTreatRealtimeDisconnectAsActualInternetLost(
            string reason
        )
        {
            if (IsLocalNetworkUnavailableFast()) return true;
            if (string.IsNullOrWhiteSpace(reason)) return false;

            string value = reason.Trim().ToLowerInvariant();

            return value.Contains("local_internet_not_reachable")
                   || value.Contains("not_reachable")
                   || value.Contains("internet")
                   || value.Contains("checknet_fast_server_unreachable");
        }

        //* این تابع قطع ددیکیتد را برای UI طبقه بندی می کند.
        //* در Windows ممکن است Application.internetReachability هنوز LAN نشان بدهد، اما مسیر سرور واقعاً قطع شده باشد.
        private static bool ShouldTreatDedicatedDisconnectAsActualInternetLost(
            string reason
        )
        {
            if (IsLocalNetworkUnavailableFast()) return true;
            if (string.IsNullOrWhiteSpace(reason)) return true;

            string value = reason.Trim().ToLowerInvariant();

            return value.Contains("receive failed")
                   || value.Contains("websocket receive failed")
                   || value.Contains("transport error")
                   || value.Contains("transport_disconnected")
                   || value.Contains("closed the websocket")
                   || value.Contains("close handshake")
                   || value.Contains("stream removed")
                   || value.Contains("ssl")
                   || value.Contains("request timeout")
                   || value.Contains("timeout")
                   || value.Contains("cancelled")
                   || value.Contains("not_reachable")
                   || value.Contains("internet")
                   || value.Contains("checknet_fast_server_unreachable");
        }

        //* این تابع متن ثابت افت ترنسپورت ریل تایم را برمی گرداند.
        private static string GetFixedRealtimeTransportDropUserMessage()
        {
            return FixedRealtimeTransportDropUserMessage;
        }

        //* این تابع آماده‌شدن مدیر اصلی ریل تایم را دریافت و وضعیت رابط گیم سرور را تازه می‌کند.
        private void HandleRealtimeManagerReady()
        {
            SyncRoomContextFromRealtime("realtime_manager_ready", true);
            ClearStaleRealtimeReconnectStateWhenLobbyReady();
            RefreshUiState(true);
        }

        private void HandleRealtimeRoomJoined(string roomId)
        {
            manualExitWorldCleanupApplied = false;

            bool isAlreadyInsideGameServer = IsInsideGameServer();

            if (isAlreadyInsideGameServer && !IsDedicatedSocketBoundToRoom(roomId))
            {
                Log("Realtime room joined while dedicated socket belongs to another room | realtimeRoomId=" + Safe(roomId) + " | dedicatedRoomId=" + (wsClient != null ? Safe(wsClient.RoomId) : string.Empty), true);
                _ = DisconnectGameServerAndLeaveRoomAsync("dedicated_room_context_mismatch");
                return;
            }

            bool shouldReconnectDedicated = autoReconnectGameServerAfterRealtimeReconnect
                                            && wasInsideDedicatedGameServer
                                            && !isAlreadyInsideGameServer
                                            && !isAutoReconnectGameServerAfterRealtimeRunning;

            realtimeReconnectInProgress = false;
            ResetReconnectDebugDebounce();

            ActivateSharedWorldForRoomEntry("realtime_room_joined:" + Safe(roomId));
            SyncRoomContextFromRealtime("room_joined_event", true);

            if (shouldReconnectDedicated)
            {
                SetGameServerStateText(reconnectingInsideGameServerStatusMessage, true);
                SetStatus(reconnectingGameServerAfterRealtimeMessage, true);
                RefreshUiState(true);

                _ = ReconnectGameServerAfterRealtimeReconnectAsync(roomId);
                return;
            }

            if (isAlreadyInsideGameServer)
            {
                CompleteGameServerReconnectAfterRealtime(
                    roomId,
                    "realtime_room_joined_while_dedicated_already_connected"
                );

                return;
            }

            if (autoConnectGameServerAfterInitialRoomJoin &&
                !isConnectClickRunning &&
                !isDisconnectClickRunning &&
                !isAutoReconnectGameServerAfterRealtimeRunning)
            {
                SetGameServerStateText(connectingGameServerStatusMessage, true);
                SetStatus("Realtime room joined. Connecting to Game Server...", true);
                RefreshUiState(true);
                _ = ConnectGameServerAfterInitialRoomJoinAsync(roomId);
                return;
            }

            SetGameServerStateText(outsideGameServerStatusMessage, true);
            SetStatus("Realtime room joined. Game server is ready to connect.", true);
            RefreshUiState(true);
        }

        //* این تابع پس از نخستین ورود موفق روم، همان مسیر آزمایش‌شده تیکت، سوکت و احراز گیم سرور را خودکار اجرا می‌کند.
        private async Task ConnectGameServerAfterInitialRoomJoinAsync(string roomId)
        {
            try
            {
                await Task.Yield();
                bool connected = await ConnectGameServerAsync();
                Log("Initial room auto-connect result=" + connected + " | roomId=" + Safe(roomId), true);
            }
            catch (Exception ex)
            {
                Log("Initial room auto-connect exception | roomId=" + Safe(roomId) + " | error=" + ex.Message, true);
            }
        }

        //* این تابع بعد از ریکانکت موفق ریل تایم، اگر قبل از قطعی داخل گیم سرور بوده، اتصال ددیکیتد را دوباره برقرار می کند.
        private async Task ReconnectGameServerAfterRealtimeReconnectAsync(string roomId)
        {
            if (isAutoReconnectGameServerAfterRealtimeRunning) return;
            if (!autoReconnectGameServerAfterRealtimeReconnect) return;
            if (!wasInsideDedicatedGameServer) return;
            if (IsInsideGameServer()) return;

            isAutoReconnectGameServerAfterRealtimeRunning = true;

            try
            {
                int maxAttempts = Mathf.Max(1, gameServerReconnectAfterRealtimeMaxAttempts);
                float maxReconnectSeconds = ResolveGameServerReconnectBudgetSeconds();
                float reconnectStartedAt = Time.realtimeSinceStartup;
                float reconnectDeadlineAt = reconnectStartedAt + maxReconnectSeconds;
                float retryDelay = Mathf.Max(0.25f, gameServerReconnectAfterRealtimeRetryDelaySeconds);
                int realtimeReadyPollDelayMs = Mathf.RoundToInt(
                    Mathf.Clamp(uiRefreshIntervalSeconds, 0.1f, 0.5f) * 1000f
                );
                int attempt = 1;
                string lastPauseReason = string.Empty;

                while (attempt <= maxAttempts && Time.realtimeSinceStartup < reconnectDeadlineAt)
                {
                    if (!isActiveAndEnabled || isDisconnectClickRunning || !wasInsideDedicatedGameServer)
                    {
                        Log(
                            "Game server reconnect loop stopped before next real attempt | roomId=" +
                            Safe(roomId) +
                            " | active=" +
                            isActiveAndEnabled +
                            " | disconnectRunning=" +
                            isDisconnectClickRunning +
                            " | wasInsideDedicated=" +
                            wasInsideDedicatedGameServer,
                            true
                        );

                        return;
                    }

                    if (IsInsideGameServer())
                    {
                        CompleteGameServerReconnectAfterRealtime(roomId, "auto_reconnect_already_connected_attempt_" + attempt);
                        return;
                    }

                    bool hasRealtimeSnapshot =
                        TryReadRealtimeSnapshot(out RealtimeRoomSnapshot snapshot);
                    bool realtimeLoopRunning =
                        hasRealtimeSnapshot &&
                        IsRealtimeControllerReconnectLoopRunning(snapshot);
                    bool roomContextReady =
                        hasRealtimeSnapshot &&
                        snapshot.ready &&
                        snapshot.joined &&
                        !string.IsNullOrWhiteSpace(snapshot.roomId) &&
                        string.Equals(
                            Safe(snapshot.roomId),
                            Safe(roomId),
                            StringComparison.Ordinal
                        );

                    if (realtimeLoopRunning || !roomContextReady)
                    {
                        string pauseReason =
                            "realtimeLoopRunning=" +
                            realtimeLoopRunning +
                            " | hasSnapshot=" +
                            hasRealtimeSnapshot +
                            " | ready=" +
                            (hasRealtimeSnapshot && snapshot.ready) +
                            " | joined=" +
                            (hasRealtimeSnapshot && snapshot.joined) +
                            " | expectedRoomId=" +
                            Safe(roomId) +
                            " | currentRoomId=" +
                            (hasRealtimeSnapshot ? Safe(snapshot.roomId) : "missing");

                        if (!string.Equals(lastPauseReason, pauseReason, StringComparison.Ordinal))
                        {
                            Log(
                                "Game server reconnect paused without consuming attempt " +
                                attempt +
                                " | " +
                                pauseReason,
                                true
                            );

                            lastPauseReason = pauseReason;
                        }

                        await Task.Delay(
                            GetGameServerReconnectBoundedDelayMs(
                                realtimeReadyPollDelayMs,
                                reconnectDeadlineAt
                            )
                        );
                        continue;
                    }

                    realtimeReconnectInProgress = false;
                    lastPauseReason = string.Empty;

                    if (!CanStartGameServerConnect(out string readinessReason))
                    {
                        bool transientBlock =
                            readinessReason.StartsWith("realtime_reconnect_in_progress", StringComparison.Ordinal) ||
                            readinessReason.StartsWith("realtime_not_ready", StringComparison.Ordinal) ||
                            readinessReason.StartsWith("room_not_joined", StringComparison.Ordinal) ||
                            readinessReason.StartsWith("room_id_empty", StringComparison.Ordinal) ||
                            string.Equals(readinessReason, "flow_running", StringComparison.Ordinal);

                        if (transientBlock)
                        {
                            Log(
                                "Game server reconnect paused by transient readiness gate without consuming attempt " +
                                attempt +
                                " | reason=" +
                                Safe(readinessReason),
                                true
                            );

                            await Task.Delay(
                                GetGameServerReconnectBoundedDelayMs(
                                    realtimeReadyPollDelayMs,
                                    reconnectDeadlineAt
                                )
                            );
                            continue;
                        }

                        Log(
                            "Game server reconnect stopped by non-transient readiness gate before attempt " +
                            attempt +
                            " | reason=" +
                            Safe(readinessReason),
                            true
                        );

                        break;
                    }

                    float elapsedSeconds = Mathf.Max(0f, Time.realtimeSinceStartup - reconnectStartedAt);
                    string retryMessage =
                        ResolveText(gameServerReconnectRetryMessage, "در حال تلاش برای اتصال مجدد به Game Server...") +
                        " تلاش " +
                        attempt +
                        "/" +
                        maxAttempts;

                    SetGameServerStateText(reconnectingInsideGameServerStatusMessage, true);
                    SetStatus(retryMessage, true);
                    ShowGameServerDebugProgress(
                        retryMessage,
                        "GAME_SERVER_RECONNECT_AFTER_REALTIME_ATTEMPT_" + attempt,
                        "roomId=" + Safe(roomId) +
                        " | attempt=" + attempt + "/" + maxAttempts +
                        " | elapsedSeconds=" + elapsedSeconds.ToString("F1") +
                        " | maxSeconds=" + maxReconnectSeconds.ToString("F1"),
                        true
                    );

                    bool connected = await ConnectGameServerAsync();
                    if (connected) return;

                    attempt++;

                    if (attempt <= maxAttempts && Time.realtimeSinceStartup < reconnectDeadlineAt)
                    {
                        string pendingMessage = GetGameServerReconnectPendingMessage();
                        wasInsideDedicatedGameServer = true;
                        SetGameServerStateText(reconnectingInsideGameServerStatusMessage, true);
                        SetStatus(pendingMessage, true);
                        ShowGameServerDebugProgress(
                            pendingMessage,
                            "GAME_SERVER_RECONNECT_AFTER_REALTIME_WAIT_" + (attempt - 1),
                            "roomId=" + Safe(roomId) +
                            " | nextRetrySeconds=" + retryDelay.ToString("F1") +
                            " | elapsedSeconds=" +
                            Mathf.Max(0f, Time.realtimeSinceStartup - reconnectStartedAt).ToString("F1") +
                            " | maxSeconds=" + maxReconnectSeconds.ToString("F1"),
                            true
                        );

                        await Task.Delay(
                            GetGameServerReconnectBoundedDelayMs(
                                Mathf.RoundToInt(retryDelay * 1000f),
                                reconnectDeadlineAt
                            )
                        );
                    }
                }

                string finalPendingMessage = GetGameServerReconnectPendingMessage();
                wasInsideDedicatedGameServer = true;
                SetGameServerStateText(reconnectingInsideGameServerStatusMessage, true);
                SetStatus(finalPendingMessage, true);
                ShowGameServerDebugProgress(
                    finalPendingMessage,
                    "GAME_SERVER_RECONNECT_AFTER_REALTIME_STILL_PENDING",
                    "roomId=" + Safe(roomId) +
                    " | attempts=" + Mathf.Max(0, attempt - 1) +
                    "/" + maxAttempts +
                    " | elapsedSeconds=" +
                    Mathf.Max(0f, Time.realtimeSinceStartup - reconnectStartedAt).ToString("F1") +
                    " | maxSeconds=" + maxReconnectSeconds.ToString("F1"),
                    false
                );
            }
            finally
            {
                isAutoReconnectGameServerAfterRealtimeRunning = false;
                RefreshUiState(true);
            }
        }

        //* این تابع بودجه اتصال دوباره Game Server را از مهلت مشترک مدیر ریل‌تایم می‌گیرد و مهلت مستقل تازه نمی‌سازد.
        private float ResolveGameServerReconnectBudgetSeconds()
        {
            if (realtimeRoomGameServerManager != null && realtimeRoomGameServerManager.IsRecoveryRunning)
            {
                return Mathf.Max(0.1f, realtimeRoomGameServerManager.RecoveryRemainingSeconds);
            }

            return Mathf.Max(10f, gameServerReconnectAfterRealtimeMaxSeconds);
        }

        //* این تابع اجازه نمی دهد Delay داخلی ریکانکت از مهلت نهایی عبور کند.
        private int GetGameServerReconnectBoundedDelayMs(int requestedDelayMs, float deadlineAt)
        {
            int safeRequestedDelayMs = Mathf.Max(1, requestedDelayMs);
            float secondsLeft = deadlineAt - Time.realtimeSinceStartup;

            if (secondsLeft <= 0f)
            {
                return 1;
            }

            int deadlineDelayMs = Mathf.Max(
                1,
                Mathf.RoundToInt(secondsLeft * 1000f)
            );

            return Mathf.Min(safeRequestedDelayMs, deadlineDelayMs);
        }

        //* این تابع بعد از خروج روم، به عنوان گارد نهایی هر اتصال Dedicated باقی‌مانده را به شکل await شده می‌بندد.
        private async void HandleRealtimeRoomLeft(string roomId)
        {
            if (!isDisconnectClickRunning && HasActiveDedicatedConnection)
            {
                isSafetyDedicatedCloseRunning = true;

                try
                {
                    bool dedicatedCloseCompleted = await wsClient.DisconnectAsync("realtime_room_left_safety_close", CancellationToken.None);
                    dedicatedCloseConfirmationRequired = !dedicatedCloseCompleted;
                    wasInsideDedicatedGameServer = !dedicatedCloseCompleted;
                    Log("Unexpected realtime room-left forced dedicated safety close | result=" + dedicatedCloseCompleted + " | roomId=" + Safe(roomId) + " | inspectorRule=" + disconnectDedicatedOnRealtimeRoomLeft, true);
                }
                finally
                {
                    isSafetyDedicatedCloseRunning = false;
                }
            }

            realtimeReconnectInProgress = false;
            wasInsideDedicatedGameServer = false;
            ResetReconnectDebugDebounce();
            ClearRoomSyncCache();

            // هنگام Disconnect رسمی از Game Server، متد اصلی خروج مالک Cleanup و پیام نهایی است.
            // این Guard از اجرای دوباره Cleanup و ثبت دو وضعیت Outside جلوگیری می کند.
            if (isDisconnectClickRunning)
            {
                Log(
                    "Realtime room-left event acknowledged by active manual game-server disconnect flow | roomId=" +
                    Safe(roomId),
                    true
                );

                RefreshUiState(false);
                return;
            }

            CleanupSharedWorldAfterUserExit(
                "manual_realtime_room_left:" + Safe(roomId)
            );

            SetGameServerStateText(outsideGameServerStatusMessage, true);
            SetStatus("Realtime room left.", true);
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

        //* این تابع هنگام قطع غیرعمدی ریل تایم، پیام درست را بر اساس علت واقعی نشان می دهد.
        //* افت ترنسپورت ریل تایم نباید پیام قطع اینترنت بسازد.
        private void HandleRealtimeConnectionLostForReconnect(string reason)
        {
            bool alreadyShowingReconnect =
                suppressDuplicateReconnectDebugMessages &&
                realtimeReconnectInProgress &&
                hasShownConnectionLostDebugForCurrentReconnect;

            realtimeReconnectInProgress = true;
            if (IsInsideGameServer())
            {
                wasInsideDedicatedGameServer = true;
                SetRealtimeDedicatedPresenceGuard(true, "realtime_reconnect_started:" + Safe(reason));
            }

            bool actualInternetLost = ShouldTreatRealtimeDisconnectAsActualInternetLost(reason);
            string lostMessage = actualInternetLost ? GetFixedInternetLostUserMessage() : GetFixedRealtimeTransportDropUserMessage();

            if (keepInsideGameServerStatusDuringReconnect && wasInsideDedicatedGameServer)
            {
                SetGameServerStateText(reconnectingInsideGameServerStatusMessage, true);

                if (!alreadyShowingReconnect)
                {
                    hasShownConnectionLostDebugForCurrentReconnect = true;
                    SetStatus(lostMessage, true);
                    ShowConnectionIssueDebug(
                        lostMessage,
                        actualInternetLost ? "GAME_SERVER_NETWORK_LOST_RECONNECT_STARTED" : "GAME_SERVER_REALTIME_TRANSPORT_DROP_RECONNECT_STARTED",
                        "Reason=" + Safe(reason)
                    );
                }
                else
                {
                    SetStatus(lostMessage, false);
                }
            }
            else
            {
                if (!alreadyShowingReconnect)
                {
                    hasShownConnectionLostDebugForCurrentReconnect = true;
                    SetStatus(lostMessage, true);
                    ShowConnectionIssueDebug(
                        lostMessage,
                        actualInternetLost ? "REALTIME_NETWORK_LOST_RECONNECT_STARTED" : "REALTIME_TRANSPORT_DROP_RECONNECT_STARTED",
                        "Reason=" + Safe(reason)
                    );
                }
                else
                {
                    SetStatus(lostMessage, false);
                }
            }

            RefreshUiState(true);
        }

        //* این تابع هنگام شکست نهایی ریکانکت, فقط یک پیام نهایی نشان می دهد و خروج از روم را محلی تمیز می کند.
        private void HandleRealtimeReconnectFailedPermanently(string reason)
        {
            string safeReason = "permanent_reconnect_failure:" + Safe(reason);
            string failureMessage = GetGameServerReconnectPendingMessage();

            SetRealtimeDedicatedPresenceGuard(false, safeReason);
            realtimeReconnectInProgress = false;
            wasInsideDedicatedGameServer = false;
            pendingLobbyReturnAfterPermanentReconnectFailure = true;

            if (disconnectDedicatedOnRealtimeDisconnected && wsClient != null && wsClient.IsConnected)
            {
                wsClient.Disconnect("realtime_reconnect_failed_permanently");
            }

            CleanupSharedWorldAfterUserExit(safeReason);
            ClearRoomSyncCache();
            SetGameServerStateText(outsideGameServerStatusMessage, true);
            SetStatus(failureMessage, true);

            if (!hasShownReconnectFailureForCurrentReconnect)
            {
                hasShownReconnectFailureForCurrentReconnect = true;
                ShowGameServerDebugProgress(
                    failureMessage,
                    "GAME_SERVER_RECONNECT_FAILED_PERMANENTLY",
                    "Reason=" + Safe(reason),
                    false
                );
            }

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

        //* این تابع تأیید می‌کند Session احراز‌شده Dedicated دقیقاً به روم فعلی Realtime تعلق دارد.
        private bool IsDedicatedSocketBoundToCurrentRealtimeRoom(out string error)
        {
            error = string.Empty;

            if (!TryReadRealtimeSnapshot(out RealtimeRoomSnapshot snapshot) || !snapshot.joined || string.IsNullOrWhiteSpace(snapshot.roomId))
            {
                error = "realtime_room_context_missing";
                return false;
            }

            if (!IsDedicatedSocketBoundToRoom(snapshot.roomId))
            {
                error = "room_mismatch | realtimeRoomId=" + Safe(snapshot.roomId) + " | dedicatedRoomId=" + (wsClient != null ? Safe(wsClient.RoomId) : string.Empty) + " | sessionId=" + (wsClient != null ? Safe(wsClient.SessionId) : string.Empty);
                return false;
            }

            return true;
        }

        //* این تابع شناسه روم Dedicated را با شناسه روم مورد انتظار مقایسه می‌کند.
        private bool IsDedicatedSocketBoundToRoom(string expectedRoomId)
        {
            return wsClient != null &&
                   !string.IsNullOrWhiteSpace(expectedRoomId) &&
                   !string.IsNullOrWhiteSpace(wsClient.RoomId) &&
                   string.Equals(wsClient.RoomId.Trim(), expectedRoomId.Trim(), StringComparison.Ordinal);
        }

        private bool IsInsideGameServer()
        {
            return IsDedicatedSocketReadyForGameplay();
        }

        #region لابی عمومی سه بعدی

        //* این تابع مشخص می کند روم فعلی مدیر اصلی همان لابی عمومی ثابت است یا خیر.
        private bool IsCurrentRealtimeRoomPublicLobby()
        {
            return realtimeRoomGameServerManager != null &&
                   realtimeRoomGameServerManager.IsInsidePublicLobbyRoom;
        }

        #endregion

        //* این تابع فقط وقتی true است که سوکت ددیکیتد هم وصل و هم احراز هویت شده باشد.
        private bool IsDedicatedSocketReadyForGameplay()
        {
            return wsClient != null &&
                   wsClient.IsConnected &&
                   wsClient.IsAuthenticated;
        }

        //* این تابع به کنترلر ریل‌تایم خبر می‌دهد که هنگام فعال بودن ددیکیتد گیم‌سرور،
        //* رخدادهای player_left مسیر ریل‌تایم نباید منبع حذف پلیرهای سه‌بعدی باشند.
        private void SetRealtimeDedicatedPresenceGuard(bool active, string reason)
        {
            if (realtimeLobbyController != null)
            {
                realtimeLobbyController.SetDedicatedGameServerPresenceGuardActive(
                    active,
                    reason
                );
            }

            if (grpcStreamingRealtimeLobbyController != null)
            {
                grpcStreamingRealtimeLobbyController
                    .SetDedicatedGameServerPresenceGuardActive(active, reason);
            }
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

            CleanupThreeDModeRuntimeWorldAfterUserExit(safeReason);
            DetachMainCameraBeforeRuntimeCloneCleanup(safeReason);

            if (destroyRuntimeCloneRootChildrenOnUserExit) DestroyRuntimeCloneRootChildren(safeReason);

            if (disableSharedWorldRootOnUserExit && sharedWorld3DRoot != null)
            {
                sharedWorld3DRoot.SetActive(false);
                Log("Shared world root disabled after user exit. reason=" + safeReason, true);
            }
        }

        private void CleanupThreeDModeRuntimeWorldAfterUserExit(string reason)
        {
            if (threeDModeController == null) threeDModeController = FindObjectOfType<G7ThreeDModeController>(true);
            if (threeDModeController == null)
            {
                Log("3D mode runtime cleanup skipped. G7ThreeDModeController is missing. reason=" + Safe(reason), true);
                return;
            }

            threeDModeController.CleanupRuntimeWorldAfterConfirmedExit(reason);
            Log("3D mode runtime cleanup executed from dedicated binder. reason=" + Safe(reason), true);
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

        //* این تابع ورود به صحنه سه بعدی را تک اجرا می کند تا رویداد و جریان اتصال هم زمان دو بار صحنه را بارگذاری نکنند.
        private Task<bool> EnterGameplaySceneAfterDedicatedAuthenticatedAsync(string source)
        {
            if (gameplaySceneEntryTask != null && !gameplaySceneEntryTask.IsCompleted) return gameplaySceneEntryTask;
            gameplaySceneEntryTask = EnterGameplaySceneAfterDedicatedAuthenticatedInternalAsync(source);
            return gameplaySceneEntryTask;
        }

        //* این تابع بعد از احراز Game Server صحنه محیط را بارگذاری می کند و سپس پلیرهای لوکال و ریموت را در همان صحنه آماده می کند.
        private async Task<bool> EnterGameplaySceneAfterDedicatedAuthenticatedInternalAsync(string source)
        {
            string safeSource = Safe(source);

            try
            {
                if (!IsDedicatedSocketReadyForGameplay())
                {
                    Log("Gameplay scene entry blocked because dedicated socket is not ready. source=" + safeSource, true);
                    return false;
                }

                #region لابی عمومی سه بعدی

                bool publicLobbyRoom = IsCurrentRealtimeRoomPublicLobby();
                string targetSceneName = publicLobbyRoom
                    ? (string.IsNullOrWhiteSpace(lobbySceneName) ? "Lobby 1" : lobbySceneName.Trim())
                    : (string.IsNullOrWhiteSpace(gameplaySceneName) ? "Grpc_Enviroment" : gameplaySceneName.Trim());

                #endregion

                Scene activeScene = SceneManager.GetActiveScene();
                bool alreadyInsideTargetScene = activeScene.IsValid() && string.Equals(activeScene.name, targetSceneName, StringComparison.Ordinal);
                bool shouldLoadTargetScene = publicLobbyRoom ? !alreadyInsideTargetScene : loadGameplaySceneAfterDedicatedAuthenticated && !alreadyInsideTargetScene;

                if (shouldLoadTargetScene)
                {
                    ShowGameServerDebugProgress(
                        publicLobbyRoom ? "در حال آماده سازی لابی عمومی سه بعدی..." : gameplaySceneLoadingMessage,
                        publicLobbyRoom ? "PUBLIC_LOBBY_SCENE_LOAD_STARTED" : "GAMEPLAY_SCENE_LOAD_STARTED",
                        "scene=" + targetSceneName + " | source=" + safeSource,
                        true
                    );

                    AsyncOperation loadOperation = SceneManager.LoadSceneAsync(targetSceneName, LoadSceneMode.Single);

                    if (loadOperation == null)
                    {
                        Log("Target scene load operation was not created. scene=" + targetSceneName + " | source=" + safeSource, true);
                        return false;
                    }

                    while (!loadOperation.isDone) await Task.Yield();
                    await Task.Yield();

                    activeScene = SceneManager.GetActiveScene();

                    if (!activeScene.IsValid() || !string.Equals(activeScene.name, targetSceneName, StringComparison.Ordinal))
                    {
                        Log("Target scene did not become active. expected=" + targetSceneName + " | actual=" + Safe(activeScene.name), true);
                        return false;
                    }

                    threeDModeController = null;
                    sharedWorld3DRoot = null;
                    mainCameraOverride = null;
                    mainCameraSafeParent = null;
                    dedicatedRemotePlayerViewController = null;
                }

                EnsureReferences();

                if (dedicatedRemotePlayerViewController == null && wsClient != null)
                {
                    dedicatedRemotePlayerViewController = wsClient.GetComponent<DedicatedRemotePlayerViewController>();
                }

                if (dedicatedRemotePlayerViewController == null)
                {
                    Log("Dedicated remote player view controller is missing after target scene preparation. scene=" + targetSceneName + " | source=" + safeSource, true);
                    return false;
                }

                bool remotePlayerViewWasEnabled = dedicatedRemotePlayerViewController.enabled;

                EnsureDedicatedWorldActiveAfterAuthenticated(
                    safeSource + (publicLobbyRoom ? "_public_lobby_ready" : "_gameplay_scene_ready")
                );

                if (!remotePlayerViewWasEnabled)
                {
                    dedicatedRemotePlayerViewController.enabled = true;
                }
                else
                {
                    dedicatedRemotePlayerViewController.Btn_BeginDedicatedRemoteView();
                }

                Log(
                    (publicLobbyRoom ? "Public lobby 3D ready after dedicated authentication" : "Gameplay scene ready after dedicated authentication") +
                    " | scene=" + SceneManager.GetActiveScene().name +
                    " | source=" + safeSource,
                    true
                );

                return true;
            }
            catch (Exception error)
            {
                Log("Target scene preparation failed | source=" + safeSource + " | error=" + Safe(error.Message), true);
                return false;
            }
            finally
            {
                gameplaySceneEntryTask = null;
            }
        }

        //* این تابع بعد از احراز ددیکیتد، دنیای سه بعدی را فعال می کند و در ریکانکت ترنسفورم پلیر را حفظ می کند.
        private void EnsureDedicatedWorldActiveAfterAuthenticated(string source)
        {
            string safeSource = Safe(source);

            bool preserveLocalPlayerTransform =
                isAutoReconnectGameServerAfterRealtimeRunning;

            if (threeDModeController == null)
            {
                threeDModeController =
                    FindObjectOfType<G7ThreeDModeController>(true);
            }

            if (sharedWorld3DRoot == null &&
                useThreeDModeControllerWorldRootFallback &&
                threeDModeController != null &&
                threeDModeController.World3DRoot != null)
            {
                sharedWorld3DRoot = threeDModeController.World3DRoot;
            }

            ActivateSharedWorldForRoomEntry(
                "dedicated_authenticated:" + safeSource
            );

            if (threeDModeController == null)
            {
                Log(
                    "3D mode activation skipped after dedicated auth. " +
                    "G7ThreeDModeController is missing. source=" +
                    safeSource,
                    true
                );

                return;
            }

            if (activateThreeDModeAfterDedicatedAuthenticated &&
                !threeDModeController.IsThreeDModeActive)
            {
                if (preserveLocalPlayerTransform &&
                    threeDModeController.LocalPlayerInstance != null)
                {
                    threeDModeController.EnterThreeDModePreservingLocalPlayer();

                    Log(
                        "3D mode entered after dedicated reconnect without resetting local player. " +
                        "source=" + safeSource,
                        true
                    );
                }
                else
                {
                    threeDModeController.EnterThreeDMode();

                    Log(
                        "3D mode entered after dedicated auth. source=" +
                        safeSource,
                        true
                    );
                }

                return;
            }

            if (ensureLocalPlayerAfterDedicatedAuthenticated)
            {
                threeDModeController.EnsureLocalPlayerSpawned();

                Log(
                    "Local player ensured without resetting existing transform. " +
                    "source=" + safeSource +
                    " | reconnect=" + preserveLocalPlayerTransform,
                    true
                );
            }
        }

        private void HandleDedicatedConnected()
        {
            if (activateSharedWorldRootOnDedicatedConnected) ActivateSharedWorldForRoomEntry("dedicated_socket_connected");
            SetGameServerStateText(connectingGameServerStatusMessage, true);
            SetStatus("Dedicated server socket connected.", true);
            RefreshUiState(true);
        }

        //* این تابع قطع ددیکیتد را با پیام یکسان قطع اینترنت مدیریت می کند و جلوی تکرار پاپ آپ را می گیرد.
        private void HandleDedicatedDisconnected(string reason)
        {
            dedicatedConnectFlowOwnsNextAuthenticatedEvent = false;

            bool manualExit =
                isDisconnectClickRunning ||
                IsManualExitReason(reason);

            bool permanentReconnectFailure =
                IsPermanentReconnectFailureReason(reason);

            bool actualInternetLost =
                ShouldTreatDedicatedDisconnectAsActualInternetLost(reason);

            bool shouldStartDedicatedReconnect =
                !manualExit &&
                !permanentReconnectFailure &&
                autoReconnectGameServerAfterRealtimeReconnect &&
                wasInsideDedicatedGameServer &&
                !isConnectClickRunning &&
                !isDisconnectClickRunning &&
                !isAutoReconnectGameServerAfterRealtimeRunning;

            string disconnectMessage = actualInternetLost
                ? GetFixedInternetLostUserMessage()
                : "ارتباط Game Server موقتاً قطع شد. در حال بازیابی اتصال...";

            string failureMessage =
                GetGameServerReconnectPendingMessage();

            if (manualExit && isDisconnectClickRunning)
            {
                // Flow اصلی Disconnect مالک Cleanup، State نهایی و پیام نهایی است.
                SetRealtimeDedicatedPresenceGuard(
                    false,
                    "dedicated_disconnected_manual_flow:" +
                    Safe(reason)
                );

                wasInsideDedicatedGameServer = false;
                ResetReconnectDebugDebounce();

                Log(
                    "Dedicated disconnected event acknowledged by active manual disconnect flow | reason=" +
                    Safe(reason),
                    true
                );

                RefreshUiState(false);
                return;
            }

            if (manualExit)
            {
                SetRealtimeDedicatedPresenceGuard(
                    false,
                    "dedicated_disconnected_manual:" +
                    Safe(reason)
                );

                wasInsideDedicatedGameServer = false;
                ResetReconnectDebugDebounce();

                SetGameServerStateText(
                    outsideGameServerStatusMessage,
                    true
                );

                SetStatus(
                    "Dedicated server disconnected: " +
                    Safe(reason),
                    true
                );
            }
            else if (permanentReconnectFailure)
            {
                SetRealtimeDedicatedPresenceGuard(
                    false,
                    "dedicated_disconnected_permanent_failure:" +
                    Safe(reason)
                );

                realtimeReconnectInProgress = false;
                wasInsideDedicatedGameServer = false;

                SetGameServerStateText(
                    outsideGameServerStatusMessage,
                    true
                );

                SetStatus(failureMessage, true);

                if (!hasShownReconnectFailureForCurrentReconnect)
                {
                    hasShownReconnectFailureForCurrentReconnect = true;

                    ShowGameServerDebugProgress(
                        failureMessage,
                        "GAME_SERVER_DEDICATED_RECONNECT_FAILED_PERMANENTLY",
                        "Reason=" + Safe(reason),
                        false
                    );
                }
            }
            else
            {
                SetRealtimeDedicatedPresenceGuard(
                    true,
                    "dedicated_disconnected_transient:" +
                    Safe(reason)
                );

                bool alreadyShowingReconnect =
                    suppressDuplicateReconnectDebugMessages &&
                    realtimeReconnectInProgress &&
                    hasShownConnectionLostDebugForCurrentReconnect;

                realtimeReconnectInProgress = true;
                realtimeRoomGameServerManager?.BeginUnifiedRecoveryFromDedicatedDisconnect(
                    "dedicated_disconnected:" + Safe(reason)
                );

                if (IsInsideGameServer())
                {
                    wasInsideDedicatedGameServer = true;
                }

                string stage = actualInternetLost
                    ? "GAME_SERVER_NETWORK_LOST_DEDICATED_DISCONNECTED"
                    : "GAME_SERVER_RECONNECT_DEDICATED_DISCONNECTED";

                string technicalDetails =
                    "Reason=" +
                    Safe(reason) +
                    " | actualInternetLost=" +
                    actualInternetLost +
                    " | dedicatedDisconnectClassifiedBy=server_path_reachability" +
                    " | Application.internetReachability=" +
                    Application.internetReachability +
                    " | wsConnected=" +
                    (wsClient != null && wsClient.IsConnected) +
                    " | wsAuthenticated=" +
                    (wsClient != null && wsClient.IsAuthenticated);

                if (keepInsideGameServerStatusDuringReconnect &&
                    (realtimeReconnectInProgress ||
                     wasInsideDedicatedGameServer))
                {
                    SetGameServerStateText(
                        reconnectingInsideGameServerStatusMessage,
                        true
                    );

                    if (!alreadyShowingReconnect)
                    {
                        hasShownConnectionLostDebugForCurrentReconnect =
                            true;

                        SetStatus(disconnectMessage, true);

                        ShowConnectionIssueDebug(
                            disconnectMessage,
                            stage,
                            technicalDetails
                        );
                    }
                    else
                    {
                        SetStatus(disconnectMessage, false);
                    }
                }
                else
                {
                    SetGameServerStateText(
                        outsideGameServerStatusMessage,
                        true
                    );

                    if (!alreadyShowingReconnect)
                    {
                        hasShownConnectionLostDebugForCurrentReconnect =
                            true;

                        SetStatus(disconnectMessage, true);

                        ShowConnectionIssueDebug(
                            disconnectMessage,
                            stage,
                            technicalDetails
                        );
                    }
                    else
                    {
                        SetStatus(disconnectMessage, false);
                    }
                }
            }

            if (shouldStartDedicatedReconnect)
            {
                bool hasRealtimeSnapshot =
                    TryReadRealtimeSnapshot(
                        out RealtimeRoomSnapshot reconnectSnapshot
                    );

                bool hasJoinedRoomContext =
                    hasRealtimeSnapshot &&
                    reconnectSnapshot.joined &&
                    !string.IsNullOrWhiteSpace(
                        reconnectSnapshot.roomId
                    );

                if (hasJoinedRoomContext)
                {
                    Log(
                        "Dedicated-only disconnect detected. Starting automatic Game Server reconnect without waiting for another Realtime room-joined event. " +
                        "reason=" +
                        Safe(reason) +
                        " | controller=" +
                        Safe(reconnectSnapshot.controllerKind) +
                        " | realtimeReady=" +
                        reconnectSnapshot.ready +
                        " | realtimeReconnectLoopRunning=" +
                        IsRealtimeControllerReconnectLoopRunning(
                            reconnectSnapshot
                        ) +
                        " | roomId=" +
                        Safe(reconnectSnapshot.roomId),
                        true
                    );

                    _ = ReconnectGameServerAfterRealtimeReconnectAsync(
                        reconnectSnapshot.roomId
                    );
                }
                else
                {
                    Log(
                        "Automatic Game Server reconnect deferred until Realtime room context becomes available. " +
                        "reason=" +
                        Safe(reason) +
                        " | hasRealtimeSnapshot=" +
                        hasRealtimeSnapshot +
                        " | joined=" +
                        (hasRealtimeSnapshot &&
                         reconnectSnapshot.joined) +
                        " | roomId=" +
                        (hasRealtimeSnapshot
                            ? Safe(reconnectSnapshot.roomId)
                            : "missing"),
                        true
                    );
                }
            }

            RefreshUiState(true);
        }

        private void HandleDedicatedAuthenticated()
        {
            if (!IsDedicatedSocketReadyForGameplay())
            {
                string pendingMessage = GetGameServerReconnectPendingMessage();

                wasInsideDedicatedGameServer = true;
                SetGameServerStateText(reconnectingInsideGameServerStatusMessage, true);
                SetStatus(pendingMessage, true);

                ShowGameServerDebugProgress(
                    pendingMessage,
                    "GAME_SERVER_AUTHENTICATED_EVENT_IGNORED_SOCKET_NOT_READY",
                    BuildGameServerDebugContext(),
                    true
                );

                Log(
                    "Dedicated authenticated event ignored because socket is not connected and authenticated. " +
                    BuildGameServerDebugContext(),
                    true
                );

                return;
            }

            realtimeReconnectInProgress = false;
            wasInsideDedicatedGameServer = true;

            SetRealtimeDedicatedPresenceGuard(
                true,
                "dedicated_authenticated_event"
            );

            if (dedicatedConnectFlowOwnsNextAuthenticatedEvent ||
                isConnectClickRunning)
            {
                dedicatedConnectFlowOwnsNextAuthenticatedEvent = false;

                Log(
                    "Dedicated authenticated event acknowledged. Active connect flow owns final world and UI application.",
                    true
                );

                return;
            }

            _ = EnterGameplaySceneAfterDedicatedAuthenticatedAsync(
                "dedicated_authenticated_event"
            );

            SetGameServerStateText(
                insideGameServerStatusMessage,
                true
            );

            SetStatus(insideGameServerStatusMessage, true);

            if (realtimeRoomGameServerManager != null && realtimeRoomGameServerManager.IsRecoveryRunning)
            {
                HideServerDebugPanelAfterGameServerReconnectSuccess("dedicated_authenticated_event");
                realtimeRoomGameServerManager.CompleteUnifiedRecoveryAfterGameServerAuthenticated("dedicated_authenticated_event");
            }

            RefreshUiState(true);
        }

        private void HandleDedicatedAuthFailed(string reason)
        {
            dedicatedConnectFlowOwnsNextAuthenticatedEvent = false;
            wasInsideDedicatedGameServer = false;

            SetGameServerStateText(
                outsideGameServerStatusMessage,
                true
            );

            SetStatus(
                "Dedicated auth failed: " + Safe(reason),
                true
            );

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
            bool canDisconnect = CanDisconnect;
            bool disconnectAvailabilityChanged = !hasButtonStateCache || canDisconnect != lastCanDisconnect;

            if (connectGameServerButton != null) connectGameServerButton.interactable = canConnect;
            if (disconnectGameServerButton != null) disconnectGameServerButton.interactable = canDisconnect;
            if (disconnectAvailabilityChanged) DisconnectAvailabilityChanged?.Invoke(canDisconnect);

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
            if (realtimeRoomGameServerManager != null)
            {
                snapshot = CreateRealtimeRoomGameServerManagerSnapshot();
                return true;
            }

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

        //* این تابع وضعیت مدیر اصلی ریل تایم را بدون وابستگی به کنترلرهای آزمایشی آماده می‌کند.
        private RealtimeRoomSnapshot CreateRealtimeRoomGameServerManagerSnapshot()
        {
            AuthUserDto currentUser = GlobalAuthManager.Instance != null ? GlobalAuthManager.Instance.CurrentUser : null;

            return new RealtimeRoomSnapshot
            {
                hasController = realtimeRoomGameServerManager != null,
                controllerKind = "realtime_room_game_server_manager",
                ready = realtimeRoomGameServerManager != null && realtimeRoomGameServerManager.IsRealtimeReady,
                joined = realtimeRoomGameServerManager != null && realtimeRoomGameServerManager.IsJoinedRoom,
                roomId = realtimeRoomGameServerManager != null ? Safe(realtimeRoomGameServerManager.CurrentRoomId) : string.Empty,
                roomName = realtimeRoomGameServerManager != null ? Safe(realtimeRoomGameServerManager.CurrentRoomName) : string.Empty,
                roomMaxPlayers = realtimeRoomGameServerManager != null ? realtimeRoomGameServerManager.CurrentRoomMaxPlayers : 50,
                userId = realtimeRoomGameServerManager != null && !string.IsNullOrWhiteSpace(realtimeRoomGameServerManager.RealtimeUserId)
                    ? Safe(realtimeRoomGameServerManager.RealtimeUserId)
                    : currentUser != null ? Safe(currentUser.id) : string.Empty,
                userName = currentUser != null ? Safe(currentUser.emailOrUsername) : string.Empty
            };
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
                roomMaxPlayers = 50,
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
                roomMaxPlayers = 50,
                userId = grpcStreamingRealtimeLobbyController != null ? Safe(grpcStreamingRealtimeLobbyController.CurrentUserId) : string.Empty,
                userName = grpcStreamingRealtimeLobbyController != null ? Safe(grpcStreamingRealtimeLobbyController.CurrentUserName) : string.Empty
            };
        }

        private void ClearRoomSyncCache()
        {
            lastSyncedRoomId = string.Empty;
            lastSyncedRoomName = string.Empty;
            lastSyncedRoomMaxPlayers = 0;

            if (ticketClient != null)
            {
                ticketClient.ClearRoomContext();
            }
        }

        private void AutoResolveServerDebugReferences(string source)
        {
            if (!autoFindServerDebugUiByName) return;

            if (pnlServerDebug == null && !string.IsNullOrWhiteSpace(serverDebugPanelObjectName))
            {
                GameObject foundPanel = FindSceneGameObjectByName(serverDebugPanelObjectName.Trim());
                if (foundPanel != null) pnlServerDebug = foundPanel;
            }

            if (pnlServerDebug != null)
            {
                if (txtServerDebugTitle == null) txtServerDebugTitle = FindTextMeshChildByName(pnlServerDebug.transform, "Txt_ServerDebugTitle");
                if (txtServerDebugMessage == null) txtServerDebugMessage = FindTextMeshChildByName(pnlServerDebug.transform, "Txt_ServerDebugMessage");
                if (txtServerDebugTechnical == null) txtServerDebugTechnical = FindTextMeshChildByName(pnlServerDebug.transform, "Txt_ServerDebugTechnical");
                if (btnServerDebugClose == null) btnServerDebugClose = FindButtonChildByName(pnlServerDebug.transform, "Btn_Close");
                if (btnServerDebugRetry == null) btnServerDebugRetry = FindButtonChildByName(pnlServerDebug.transform, "Btn_Retry");
                if (btnServerDebugRelogin == null) btnServerDebugRelogin = FindButtonChildByName(pnlServerDebug.transform, "Btn_Relogin");
            }

            Log("ServerDebugRefs | source=" + Safe(source) + " | panel=" + (pnlServerDebug != null) + " | title=" + (txtServerDebugTitle != null) + " | message=" + (txtServerDebugMessage != null) + " | technical=" + (txtServerDebugTechnical != null), false);
        }

        private static GameObject FindSceneGameObjectByName(string objectName)
        {
            if (string.IsNullOrWhiteSpace(objectName)) return null;

            GameObject activeObject = GameObject.Find(objectName);
            if (activeObject != null) return activeObject;

            GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < allObjects.Length; i++)
            {
                GameObject candidate = allObjects[i];
                if (candidate == null) continue;
                if (!string.Equals(candidate.name, objectName, StringComparison.Ordinal)) continue;
                if (!candidate.scene.IsValid()) continue;
                return candidate;
            }

            return null;
        }

        private static TextMeshProUGUI FindTextMeshChildByName(Transform root, string childName)
        {
            if (root == null || string.IsNullOrWhiteSpace(childName)) return null;

            TextMeshProUGUI[] texts = root.GetComponentsInChildren<TextMeshProUGUI>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                TextMeshProUGUI candidate = texts[i];
                if (candidate == null) continue;
                if (string.Equals(candidate.name, childName, StringComparison.Ordinal)) return candidate;
            }

            return null;
        }

        private static Button FindButtonChildByName(Transform root, string childName)
        {
            if (root == null || string.IsNullOrWhiteSpace(childName)) return null;

            Button[] buttons = root.GetComponentsInChildren<Button>(true);
            for (int i = 0; i < buttons.Length; i++)
            {
                Button candidate = buttons[i];
                if (candidate == null) continue;
                if (string.Equals(candidate.name, childName, StringComparison.Ordinal)) return candidate;
            }

            return null;
        }

        private void ShowConnectionIssueDebug(
            string message,
            string stage,
            string technicalDetails
        )
        {
            if (!showServerDebugPanelImmediatelyOnDedicatedConnectionLost) return;

            ShowGameServerDebugProgress(
                message,
                stage,
                technicalDetails,
                true
            );

            // دکمه Close فقط پنل را مخفی می کند و نباید Reconnect را متوقف کند.
            if (showServerDebugCloseDuringDedicatedReconnect)
            {
                SetButtonGameObjectActive(btnServerDebugClose, true);
            }
        }

        private static bool IsGameServerConnectionRecoveryStage(string stage)
        {
            if (string.IsNullOrWhiteSpace(stage)) return false;
            return stage.Contains("CONNECTION_LOST")
                   || stage.Contains("NETWORK_LOST")
                   || stage.Contains("RECONNECT");
        }

        //* این تابع مشخص می کند مرحله فعلی فقط اعلام قطع اینترنت است، نه مرحله بازیابی.
        private static bool IsGameServerNetworkLostStage(string stage)
        {
            if (string.IsNullOrWhiteSpace(stage)) return false;
            return stage.Contains("CONNECTION_LOST") || stage.Contains("NETWORK_LOST");
        }

        //* این تابع مشخص می کند مرحله فعلی افت ترنسپورت ریل تایم است، نه قطع اینترنت.
        private static bool IsRealtimeTransportDropStage(string stage)
        {
            if (string.IsNullOrWhiteSpace(stage)) return false;
            return stage.Contains("TRANSPORT_DROP");
        }

        //* این تابع بعد از کامل شدن ریکانکت گیم سرور، پیام نهایی درست را روی پنل دیباگ نشان می دهد.
        private void CompleteGameServerReconnectAfterRealtime(string roomId, string source)
        {
            string safeRoomId = Safe(roomId);
            string safeSource = Safe(source);
            string successMessage = ResolveText(gameServerReconnectSuccessMessage, "اتصال دوباره به Game Server برقرار شد.");

            if (!IsDedicatedSocketReadyForGameplay())
            {
                string pendingMessage = GetGameServerReconnectPendingMessage();

                wasInsideDedicatedGameServer = true;
                SetGameServerStateText(reconnectingInsideGameServerStatusMessage, true);
                SetStatus(pendingMessage, true);

                ShowGameServerDebugProgress(
                    pendingMessage,
                    "GAME_SERVER_RECONNECT_SUCCESS_BLOCKED_NOT_AUTHENTICATED",
                    "roomId=" + safeRoomId + " | source=" + safeSource + " | " + BuildGameServerDebugContext(),
                    true
                );

                Log(
                    "Game server reconnect success blocked because socket is not connected and authenticated. source=" +
                    safeSource +
                    " | " +
                    BuildGameServerDebugContext(),
                    true
                );

                return;
            }

            ResetReconnectDebugDebounce();

            SetGameServerStateText(insideGameServerStatusMessage, true);
            SetStatus(successMessage, true);

            ShowGameServerDebugProgress(
                successMessage,
                "GAME_SERVER_RECONNECT_SUCCESS_AFTER_REALTIME",
                "roomId=" + safeRoomId + " | source=" + safeSource + " | " + BuildGameServerDebugContext(),
                false
            );

            HideServerDebugPanelAfterGameServerReconnectSuccess(
                "complete_game_server_reconnect_after_realtime:" + safeSource
            );

            realtimeRoomGameServerManager?.CompleteUnifiedRecoveryAfterGameServerAuthenticated(
                "complete_game_server_reconnect_after_realtime:" + safeSource
            );

            RefreshUiState(true);
        }

        //* این تابع بعد از موفقیت کامل ریکانکت Game Server، پنل دیباگ بازیابی را جمع می کند.
        private void HideServerDebugPanelAfterGameServerReconnectSuccess(string source)
        {
            bool unifiedRecoveryCompleted = realtimeRoomGameServerManager != null && realtimeRoomGameServerManager.IsRecoveryRunning;
            bool reconnectSuccessMustClose = unifiedRecoveryCompleted || realtimeReconnectInProgress || isAutoReconnectGameServerAfterRealtimeRunning;
            if (!closeServerDebugPanelOnGameServerConnectSuccess && !reconnectSuccessMustClose) return;

            AutoResolveServerDebugReferences(
                "game_server_reconnect_success_hide:" + Safe(source)
            );

            if (pnlServerDebug != null && pnlServerDebug.activeSelf)
            {
                pnlServerDebug.SetActive(false);
                Log(
                    "Game server reconnect success debug panel hidden. source=" +
                    Safe(source),
                    true
                );
            }
        }

        private void ShowGameServerDebugProgress(
            string message,
            string stage,
            string technicalDetails,
            bool isRunning
        )
        {
            if (!openServerDebugPanelOnGameServerConnectProgress) return;

            AutoResolveServerDebugReferences("game_server_progress:" + stage);

            string safeMessage = string.IsNullOrWhiteSpace(message)
                ? gameServerPreparingMessage
                : message.Trim();

            string safeStage = Safe(stage);
            bool isConnectionRecoveryStage =
                IsGameServerConnectionRecoveryStage(safeStage);

            bool isNetworkLostStage =
                IsGameServerNetworkLostStage(safeStage);

            bool isTransportDropStage =
                IsRealtimeTransportDropStage(safeStage);

            if (!isTransportDropStage &&
                (isNetworkLostStage ||
                 IsLegacyInternetLostReconnectMessage(safeMessage)))
            {
                safeMessage = FixedInternetLostUserMessage;
            }

            if (IsLegacyGameServerReconnectFailedMessage(safeMessage))
            {
                safeMessage = GetGameServerReconnectPendingMessage();
            }

            string safeTitle = isTransportDropStage
                ? FixedRealtimeTransportDropDebugTitle
                : (isNetworkLostStage ||
                   string.Equals(
                       safeMessage,
                       FixedInternetLostUserMessage,
                       StringComparison.Ordinal
                   )
                    ? FixedInternetLostDebugTitle
                    : (isConnectionRecoveryStage
                        ? "بازیابی اتصال Game Server"
                        : (string.IsNullOrWhiteSpace(
                               gameServerConnectProgressTitle
                           )
                            ? "اتصال به Game Server"
                            : gameServerConnectProgressTitle.Trim())));

            string safeTechnical =
                string.IsNullOrWhiteSpace(technicalDetails)
                    ? safeStage
                    : technicalDetails.Trim();

            if (pnlServerDebug != null && !pnlServerDebug.activeSelf)
            {
                pnlServerDebug.SetActive(true);
            }

            if (txtServerDebugTitle != null)
            {
                txtServerDebugTitle.text = safeTitle;
            }

            if (txtServerDebugMessage != null)
            {
                txtServerDebugMessage.text = safeMessage;
            }

            if (txtServerDebugTechnical != null)
            {
                txtServerDebugTechnical.text =
                    safeStage + "\n" + safeTechnical;
            }

            ApplyServerDebugButtonsForGameServerFlow(isRunning);

            if (isConnectionRecoveryStage &&
                showServerDebugCloseDuringDedicatedReconnect)
            {
                SetButtonGameObjectActive(btnServerDebugClose, true);
            }

            // خود Progress پایین یک لاگ مرحله دارد؛ Status فقط هنگام تغییر متن لاگ می شود.
            SetStatus(safeMessage, false);

            Log(
                "Game server debug progress | stage=" +
                safeStage +
                " | running=" +
                isRunning +
                " | message=" +
                Safe(safeMessage),
                true
            );
        }

        private void ShowGameServerDebugResult(bool success, string stage, string technicalDetails)
        {
            string message = success
                ? (string.IsNullOrWhiteSpace(gameServerConnectSuccessDebugMessage) ? "ورود به Game Server با موفقیت انجام شد." : gameServerConnectSuccessDebugMessage.Trim())
                : (string.IsNullOrWhiteSpace(gameServerConnectFailureDebugMessage) ? "اتصال به Game Server انجام نشد. لطفاً دوباره تلاش کنید." : gameServerConnectFailureDebugMessage.Trim());

            ShowGameServerDebugProgress(message, stage, technicalDetails, false);

            if (success && closeServerDebugPanelOnGameServerConnectSuccess)
            {
                if (pnlServerDebug != null && pnlServerDebug.activeSelf) pnlServerDebug.SetActive(false);
            }
        }

        private void ApplyServerDebugButtonsForGameServerFlow(bool isRunning)
        {
            bool showClose = !isRunning || !hideServerDebugCloseWhileGameServerConnectRunning;
            SetButtonGameObjectActive(btnServerDebugClose, showClose);
            SetButtonGameObjectActive(btnServerDebugRetry, false);
            SetButtonGameObjectActive(btnServerDebugRelogin, false);
        }

        private static void SetButtonGameObjectActive(Button button, bool active)
        {
            if (button == null) return;
            if (button.gameObject.activeSelf != active) button.gameObject.SetActive(active);
        }

        private void ResetReconnectDebugDebounce()
        {
            hasShownConnectionLostDebugForCurrentReconnect = false;
            hasShownReconnectFailureForCurrentReconnect = false;
            ResetImmediateLocalNetworkLossDetector("reconnect_debug_debounce_reset");
        }

        //* این تابع متن ثابت قطع اینترنت را برمی گرداند تا مقدار قدیمی اینسپکتور وارد یو آی نشود.
        private static string GetFixedInternetLostUserMessage()
        {
            return FixedInternetLostUserMessage;
        }

        //* این تابع پیام موقت اتصال دوباره گیم سرور را بدون اعلام خروج از روم برمی گرداند.
        private string GetGameServerReconnectPendingMessage()
        {
            string message = ResolveText(gameServerReconnectPendingMessage, "اتصال Game Server هنوز کامل نشده است. لطفاً اینترنت را بررسی کنید و دوباره تلاش کنید.");
            if (IsLegacyGameServerReconnectFailedMessage(message)) return "اتصال Game Server هنوز کامل نشده است. لطفاً اینترنت را بررسی کنید و دوباره تلاش کنید.";
            return message;
        }

        //* این تابع متن قدیمی شکست ریکانکت را که به اشتباه خروج از روم اعلام می کند تشخیص می دهد.
        private static bool IsLegacyGameServerReconnectFailedMessage(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string safeValue = value.Trim();
            return safeValue.Contains("خروج از Room") || safeValue.Contains("از روم خارج") || safeValue.Contains("از روم خارج شدید");
        }

        //* این تابع متن قدیمی قطع اینترنت را که از اینسپکتور مانده باشد تشخیص می دهد.
        private static bool IsLegacyInternetLostReconnectMessage(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string safeValue = value.Trim();
            bool mentionsInternetLoss = safeValue.Contains("اینترنت قطع شد") || safeValue.Contains("نت قطع") || safeValue.Contains("قطع اینترنت");
            bool mentionsReconnectAction = safeValue.Contains("تلاش برای اتصال") || safeValue.Contains("بازیابی اتصال") || safeValue.Contains("ریکانکت");
            return mentionsInternetLoss && mentionsReconnectAction;
        }

        private static string ResolveText(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private string BuildGameServerDebugContext()
        {
            string roomId = Safe(lastSyncedRoomId);
            string roomName = Safe(lastSyncedRoomName);
            string socket = wsClient != null ? ("wsConnected=" + wsClient.IsConnected + " | wsAuthenticated=" + wsClient.IsAuthenticated) : "wsClient=null";
            string ticket = ticketClient != null ? "ticketClient=ready" : "ticketClient=null";
            string autoFlow = autoConnectController != null ? ("autoConnectRunning=" + autoConnectController.IsRunning) : "autoConnectController=null";
            return "roomId=" + roomId + " | roomName=" + roomName + " | " + ticket + " | " + socket + " | " + autoFlow;
        }

        private void SetGameServerStateText(string value, bool forceLog)
        {
            string safeValue = Safe(value);
            bool changed = safeValue != lastGameServerStateStatus;

            if (gameServerStateText != null) gameServerStateText.text = safeValue;
            if (changed) GameServerStateChanged?.Invoke(safeValue);

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
            if (changed) StatusChanged?.Invoke(safeValue);

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

        //* این تابع پس از شکست نهایی بازیابی، فقط بعد از برگشت قطعی اینترنت و ورود موفق کاربر، بازگشت به لابی را آغاز می‌کند.
        private void TryStartLobbyReturnAfterPermanentReconnectFailure()
        {
            if (!pendingLobbyReturnAfterPermanentReconnectFailure || permanentFailureLobbyReturnRunning) return;
            if (StartupNetworkSceneRouter.Instance == null || !StartupNetworkSceneRouter.IsOnline) return;
            if (GlobalAuthManager.CurrentAuthState != GlobalAuthManager.AuthState.Authenticated) return;

            _ = ReturnToLobbyAfterPermanentReconnectFailureAsync();
        }

        //* این تابع پس از شکست نهایی بازیابی، اتصال های قدیمی را پاک می کند و صحنه لابی را حتی اگر از قبل فعال باشد دوباره بارگذاری می کند تا ورود لابی عمومی و سه بعدی از ابتدا اجرا شود.
        private async Task ReturnToLobbyAfterPermanentReconnectFailureAsync()
        {
            if (permanentFailureLobbyReturnRunning) return;

            permanentFailureLobbyReturnRunning = true;

            try
            {
                EnsureReferences();

                if (wsClient != null && (wsClient.IsConnected || wsClient.IsAuthenticated))
                {
                    bool dedicatedClosed = await wsClient.DisconnectAsync(
                        "permanent_reconnect_failure_network_restored",
                        CancellationToken.None
                    );

                    if (!dedicatedClosed)
                    {
                        Log(
                            "Fresh Lobby return postponed because the remaining dedicated socket close was not confirmed.",
                            true
                        );

                        return;
                    }
                }

                realtimeReconnectInProgress = false;
                isAutoReconnectGameServerAfterRealtimeRunning = false;
                wasInsideDedicatedGameServer = false;
                dedicatedConnectFlowOwnsNextAuthenticatedEvent = false;

                CleanupSharedWorldAfterUserExit(
                    "permanent_reconnect_failure_network_restored"
                );

                ClearRoomSyncCache();

                SetRealtimeDedicatedPresenceGuard(
                    false,
                    "permanent_reconnect_failure_network_restored"
                );

                SetGameServerStateText(
                    outsideGameServerStatusMessage,
                    true
                );

                SetStatus(
                    "اتصال قبلی پایان یافت. در حال راه‌اندازی دوباره Lobby 1...",
                    true
                );

                string safeSceneName = string.IsNullOrWhiteSpace(lobbySceneName)
                    ? "Lobby 1"
                    : lobbySceneName.Trim();

                bool wasAlreadyInsideLobbyScene = string.Equals(
                    SceneManager.GetActiveScene().name,
                    safeSceneName,
                    StringComparison.Ordinal
                );

                /*
                 * صحنه عمداً همیشه دوباره بارگذاری می شود.
                 * چون لابی عمومی سه بعدی و فهرست لابی هر دو داخل Lobby 1 هستند.
                 * بدون بارگذاری دوباره، Start کنترلر صحنه اجرا نمی شود و Public Lobby دوباره Join نمی شود.
                 */
                AsyncOperation loadOperation = SceneManager.LoadSceneAsync(
                    safeSceneName,
                    LoadSceneMode.Single
                );

                if (loadOperation == null)
                {
                    Log(
                        "Fresh Lobby scene reload could not start. scene=" +
                        Safe(safeSceneName),
                        true
                    );

                    return;
                }

                while (!loadOperation.isDone)
                    await Task.Yield();

                // فرصت برای اجرای Awake، OnEnable و Start آبجکت های صحنه جدید.
                await Task.Yield();
                await Task.Yield();

                pendingLobbyReturnAfterPermanentReconnectFailure = false;
                hasShownReconnectFailureForCurrentReconnect = false;

                ResetReconnectDebugDebounce();

                EnsureReferences();
                BindRealtimeEvents();
                BindWsEvents();
                BindDedicatedPresenceUiEvents();

                Log(
                    "Fresh Lobby 1 reloaded after permanent reconnect failure. " +
                    "Public lobby entry and Dedicated auto-connect can start again. " +
                    "scene=" + Safe(safeSceneName) +
                    " | wasAlreadyLobbyScene=" + wasAlreadyInsideLobbyScene,
                    true
                );
            }
            catch (Exception ex)
            {
                Log(
                    "Fresh Lobby return after permanent reconnect failure failed | error=" +
                    ex.Message,
                    true
                );
            }
            finally
            {
                permanentFailureLobbyReturnRunning = false;
            }
        }

        //* این تابع پس از خروج موفق از Game Server و روم، صحنه Lobby 1 را بارگذاری و فهرست ساختمان‌ها را تازه می‌کند.
        private async Task<bool> ReturnToLobbyAfterManualGameServerDisconnectAsync(string source)
        {
            string safeSceneName = string.IsNullOrWhiteSpace(lobbySceneName) ? "Lobby 1" : lobbySceneName.Trim();

            if (loadLobbySceneAfterManualGameServerDisconnect && !string.Equals(SceneManager.GetActiveScene().name, safeSceneName, StringComparison.Ordinal))
            {
                SetStatus("در حال بازگشت به فهرست روم‌ها...", true);
                AsyncOperation loadOperation = SceneManager.LoadSceneAsync(safeSceneName, LoadSceneMode.Single);

                if (loadOperation == null)
                {
                    Log("Lobby scene load could not start. scene=" + Safe(safeSceneName) + " | source=" + Safe(source), true);
                    return false;
                }

                while (!loadOperation.isDone) await Task.Yield();
                await Task.Yield();
                Log("Lobby scene loaded after game server disconnect. scene=" + Safe(safeSceneName) + " | source=" + Safe(source), true);
            }

            EnsureReferences();
            BindRealtimeEvents();

            // تازه‌سازی لابی فقط در اختیار Lobby1RealtimeSceneController است.
            // اجرای دوباره از Binder باعث دو درخواست پشت سرهم و چشمک فهرست می‌شد.
            Log("Lobby scene controller owns the building list refresh. Duplicate binder refresh skipped. source=" + Safe(source), true);
            return true;
        }

        //* این تابع برای هر پیام حضور یک شناسه پایدار می‌سازد تا پیام یک پلیر دوباره روی خودش به‌روزرسانی شود.
        private static string BuildDedicatedPresenceMessageId(string eventName, string playerId)
        {
            string safeEvent = string.IsNullOrWhiteSpace(eventName) ? "PRESENCE" : eventName.Trim().ToUpperInvariant();
            string safePlayerId = string.IsNullOrWhiteSpace(playerId) ? "UNKNOWN" : playerId.Trim();
            return "GLOBAL_DEDICATED_PLAYER_" + safeEvent + "_" + safePlayerId;
        }

        private async Task RefreshRealtimeRoomListAfterManualGameServerDisconnectAsync(string source)
        {
            await Task.Yield();

            try
            {
                if (realtimeRoomGameServerManager != null && realtimeRoomGameServerManager.IsRealtimeReady)
                {
                    Log("Building list refresh requested after game server disconnect. controller=realtime_room_game_server_manager | source=" + Safe(source), true);
                    await realtimeRoomGameServerManager.RefreshCompletedBuildingsAsync();
                    return;
                }

                if (grpcStreamingRealtimeLobbyController != null &&
                    grpcStreamingRealtimeLobbyController.IsRealtimeReadyState &&
                    !grpcStreamingRealtimeLobbyController.IsJoinedRoom)
                {
                    Log("Room list refresh requested after game server disconnect. controller=grpc_streaming | source=" + Safe(source), true);
                    await grpcStreamingRealtimeLobbyController.ListRoomsAsync();
                    return;
                }

                if (realtimeLobbyController != null &&
                    realtimeLobbyController.IsRealtimeReadyState &&
                    !realtimeLobbyController.IsJoinedRoom)
                {
                    Log("Room list refresh requested after game server disconnect. controller=websocket | source=" + Safe(source), true);
                    await realtimeLobbyController.ListRoomsAsync();
                    return;
                }

                Log("Room list refresh after game server disconnect skipped. source=" + Safe(source)
                    + " | grpcReady=" + (grpcStreamingRealtimeLobbyController != null && grpcStreamingRealtimeLobbyController.IsRealtimeReadyState)
                    + " | grpcJoined=" + (grpcStreamingRealtimeLobbyController != null && grpcStreamingRealtimeLobbyController.IsJoinedRoom)
                    + " | wsReady=" + (realtimeLobbyController != null && realtimeLobbyController.IsRealtimeReadyState)
                    + " | wsJoined=" + (realtimeLobbyController != null && realtimeLobbyController.IsJoinedRoom), true);
            }
            catch (Exception ex)
            {
                Log("Room list refresh after game server disconnect failed. source=" + Safe(source) + " | error=" + ex.Message, true);
            }
        }





        private void CheckImmediateDedicatedNetworkProbe()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
    return;
#else
            if (!enableDedicatedFastNetworkProbe) return;

            if (isDisconnectClickRunning)
            {
                dedicatedFastProbeFailures = 0;
                return;
            }

            bool shouldWatch = IsInsideGameServer() || realtimeReconnectInProgress || wasInsideDedicatedGameServer;
            if (!shouldWatch)
            {
                dedicatedFastProbeFailures = 0;
                return;
            }

            if (hasShownImmediateNetworkLossForCurrentOutage && realtimeReconnectInProgress) return;
            if (dedicatedFastProbeRunning) return;

            float now = Time.unscaledTime;
            float interval = Mathf.Clamp(dedicatedFastNetworkProbeIntervalSeconds, 0.15f, 0.75f);
            if (now < nextDedicatedFastProbeAt) return;

            nextDedicatedFastProbeAt = now + interval;
            _ = RunDedicatedFastNetworkProbeAsync();
#endif
        }

        private async Task RunDedicatedFastNetworkProbeAsync()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
    await Task.CompletedTask;
#else
            dedicatedFastProbeRunning = true;

            try
            {
                string host;
                int port;

                if (!TryResolveDedicatedTcpProbeTarget(out host, out port)) return;

                int timeoutMs = Mathf.Clamp(dedicatedFastNetworkProbeTimeoutMs, 250, 900);
                bool reachable = await TryConnectTcpProbeAsync(host, port, timeoutMs);

                if (reachable)
                {
                    dedicatedFastProbeFailures = 0;
                    return;
                }

                dedicatedFastProbeFailures++;

                Log("Dedicated fast network probe failed. target=" + Safe(host) + ":" + port +
                    " | failures=" + dedicatedFastProbeFailures +
                    " | localNetworkUnavailable=" + IsLocalNetworkUnavailableFast(), true);

                int failuresNeeded = Mathf.Clamp(dedicatedFastNetworkProbeFailuresBeforeInternetLost, 1, 2);
                if (dedicatedFastProbeFailures < failuresNeeded) return;

                ShowImmediateDedicatedInternetLost(
                    "fast_tcp_probe_failed:" + Safe(host) + ":" + port +
                    " | localNetworkUnavailable=" + IsLocalNetworkUnavailableFast()
                );
            }
            catch (Exception ex)
            {
                Log("Dedicated fast network probe warning: " + ex.Message, true);
            }
            finally
            {
                dedicatedFastProbeRunning = false;
            }
#endif
        }

        private bool TryResolveDedicatedTcpProbeTarget(out string host, out int port)
        {
            host = string.Empty;
            port = 0;

            string url = wsClient != null ? wsClient.CurrentUrl : string.Empty;
            if (string.IsNullOrWhiteSpace(url)) return false;

            Uri uri;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri)) return false;

            host = uri.Host;
            port = uri.Port > 0 ? uri.Port : ResolveDedicatedTcpProbeDefaultPort(uri.Scheme);

            return !string.IsNullOrWhiteSpace(host) && port > 0;
        }

        private static int ResolveDedicatedTcpProbeDefaultPort(string scheme)
        {
            string safeScheme = string.IsNullOrWhiteSpace(scheme) ? string.Empty : scheme.Trim().ToLowerInvariant();
            if (safeScheme == "wss" || safeScheme == "https") return 443;
            if (safeScheme == "ws" || safeScheme == "http") return 80;
            return 443;
        }

        private static async Task<bool> TryConnectTcpProbeAsync(string host, int port, int timeoutMs)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
    await Task.CompletedTask;
    return true;
#else
            if (string.IsNullOrWhiteSpace(host) || port <= 0) return false;

            try
            {
                using (System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient())
                {
                    Task connectTask = client.ConnectAsync(host, port);
                    Task timeoutTask = Task.Delay(Mathf.Max(200, timeoutMs));

                    Task completedTask = await Task.WhenAny(connectTask, timeoutTask);
                    if (completedTask != connectTask) return false;

                    await connectTask;
                    return client.Connected;
                }
            }
            catch
            {
                return false;
            }
#endif
        }

        private void ShowImmediateDedicatedInternetLost(string reason)
        {
            if (hasShownImmediateNetworkLossForCurrentOutage) return;

            hasShownImmediateNetworkLossForCurrentOutage = true;
            hasShownConnectionLostDebugForCurrentReconnect = true;
            realtimeReconnectInProgress = true;

            if (IsInsideGameServer()) wasInsideDedicatedGameServer = true;

            string lostMessage = GetFixedInternetLostUserMessage();

            SetRealtimeDedicatedPresenceGuard(true, "immediate_dedicated_fast_probe_loss");
            SetGameServerStateText(reconnectingInsideGameServerStatusMessage, true);
            SetStatus(lostMessage, true);

            ShowConnectionIssueDebug(
                lostMessage,
                "GAME_SERVER_NETWORK_LOST_FAST_TCP_PROBE",
                "Reason=" + Safe(reason) +
                " | Application.internetReachability=" + Application.internetReachability +
                " | wsConnected=" + (wsClient != null && wsClient.IsConnected) +
                " | wsAuthenticated=" + (wsClient != null && wsClient.IsAuthenticated) +
                " | wsUrl=" + Safe(wsClient != null ? wsClient.CurrentUrl : string.Empty)
            );

            Log("Immediate network loss UI shown by dedicated TCP probe. reason=" + Safe(reason), true);
            RefreshUiState(true);
        }
    }
}
