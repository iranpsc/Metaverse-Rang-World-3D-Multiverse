using System;
using System.Collections;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.GameServer;
using Network_A.Realtime.Auth;
using Network_A.Realtime.Core;
using Network_A.Realtime.Lobby;
using Network_A.Realtime.Protocol;
using Network_A.Realtime.Stability;
using Network_A.Realtime.Transport;
using Project.UI.MainMenu;
using RTLTMPro;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;
namespace Network_A.Tests.Realtime
{
    public class RealtimeWebSocketG7RoomLobbyTestController : MonoBehaviour
    {
        [Header("Connection")]
        [SerializeField] private string serverUrl = "wss://dev-world-3d.metarang.com/ws";
        [SerializeField] private bool useServerConfigUrl = true;
        [SerializeField] private bool forceDedicatedServerConfig = true;
        [SerializeField] private RealtimeTransportKind transportKind = RealtimeTransportKind.WebSocket;
        [SerializeField] private bool disableCoreConnectTimeoutAfterConnect = true;
        [SerializeField] private bool preventAutoRealtimeConnectBeforeStartButton = true;

        [Header("Room")]
        [SerializeField] private string roomNamePrefix = "WebGL G7 Lobby Room";
        [SerializeField] private string roomDescription = "Room created by Unity WebGL G7 Room Lobby test.";
        [SerializeField] private string roomVisibility = "public";
        [SerializeField] private int maxPlayers = 20;
        [SerializeField] private string chatActionType = "webgl_g7_lobby_chat";
        [SerializeField] private string clientLabel = "User";
        [SerializeField] private bool blockCreateRoomWhenCurrentUserAlreadyOwnsRoom = true;
        [SerializeField] private string currentOwnerNameForCreateRoomCheck = string.Empty;

        [Header("Timing")]
        [SerializeField] private int connectTimeoutMs = 10000;
        [SerializeField] private int sendTimeoutMs = 10000;
        [SerializeField] private int waitTimeoutMs = 15000;
        [SerializeField] private int reliableAckTimeoutMs = 5000;

        [Header("Auth Refresh Gate")]
        [SerializeField] private int accessTokenRefreshSkewSeconds = 60;

        [Header("Keep Alive")]
        [SerializeField] private bool enableTestKeepAlive = true;
        [SerializeField] private int keepAliveIntervalMs = 3000;
        [SerializeField] private int keepAlivePingTimeoutMs = 3000;
        [SerializeField] private bool monitorRealtimeConnectionDropInUpdate = true;

        [Header("UI")]
        [SerializeField] private TMP_InputField roomNameInput;
        [SerializeField] private TMP_InputField messageInput;
        [SerializeField] private TextMeshProUGUI roomText;
        [SerializeField] private TextMeshProUGUI statusText;
        [SerializeField] private Button connectButton;
        [SerializeField] private Button disconnectButton;
        [SerializeField] private Button createRoomButton;
        [SerializeField] private Button listRoomsButton;
        [SerializeField] private Button leaveRoomButton;
        [SerializeField] private Button sendMessageButton;
        [SerializeField] private TextMeshProUGUI logText;
        [SerializeField] private bool mirrorLogToStatusWhenLogTextMissing = true;
        [SerializeField] private bool forceTextMeshRefreshAfterUiApply = true;
        [SerializeField] private bool disableSendButtonWhenMessageInputEmpty = true;
        [SerializeField] private int minimumRoomNameCharactersToEnableCreateButton = 8;

        [Header("Server Debug Progress UI")]
        [SerializeField] private GameObject pnlServerDebug;
        [SerializeField] private TextMeshProUGUI txtServerDebugTitle;
        [SerializeField] private TextMeshProUGUI txtServerDebugMessage;
        [SerializeField] private TextMeshProUGUI txtServerDebugTechnical;
        [SerializeField] private Button btnServerDebugClose;
        [SerializeField] private Button btnServerDebugRetry;
        [SerializeField] private Button btnServerDebugRelogin;
        private bool serverDebugButtonHandlersBound;
        private string lastRealtimeServerDebugStage = string.Empty;
        [SerializeField] private bool autoFindServerDebugPanelByName = true;
        [SerializeField] private string serverDebugPanelObjectName = "Pnl_ServerDebug";
        [SerializeField] private bool openServerDebugPanelOnRealtimeConnectProgress = true;
        [SerializeField] private bool openServerDebugPanelOnRealtimeConnectFailure = true;
        [SerializeField] private bool closeServerDebugPanelOnRealtimeConnectSuccess = false;
        [SerializeField] private bool hideServerDebugCloseWhileRealtimeConnectRunning = true;
        [SerializeField] private string realtimeConnectProgressTitle = "اتصال به Realtime";
        [SerializeField] private string gameServerReconnectProgressTitle = "اتصال به گیم سرور";
        [SerializeField] private string realtimeConnectPreparingMessage = "در حال آماده‌سازی اتصال به Realtime...";
        [SerializeField] private string realtimeTokenCheckingMessage = "در حال بررسی نشست کاربر برای اتصال Realtime...";
        [SerializeField] private string realtimeTokenRefreshingMessage = "نشست کاربر برای اتصال Realtime در حال تمدید است...";
        [SerializeField] private string realtimeSocketConnectingMessage = "در حال باز کردن اتصال شبکه Realtime...";
        [SerializeField] private string realtimeAuthenticatingMessage = "اتصال شبکه برقرار شد. در حال احراز هویت Realtime...";
        [SerializeField] private string realtimeRoomSyncMessage = "در حال هماهنگ‌سازی وضعیت روم‌های Realtime...";
        [SerializeField] private string realtimeConnectSuccessDebugMessage = "اتصال به Realtime با موفقیت انجام شد.";
        [SerializeField] private string realtimeConnectFailureDebugMessage = "اتصال به Realtime انجام نشد. لطفاً دوباره تلاش کنید.";
        [Header("Room List UI")]
        [SerializeField] private Transform roomListContent;
        [SerializeField] private RealtimeRoomListItemView roomListItemPrefab;
        [SerializeField] private bool disableRoomListWhileJoining = true;
        [SerializeField] private bool clearRoomListOnJoinSuccess = false;

        [Header("Manual Exit World Cleanup")]
        [SerializeField] private GameObject sharedWorld3DRoot;
        [SerializeField] private Transform[] runtimeCloneRoots;
        [SerializeField] private bool cleanupSharedWorldOnlyOnUserExit = true;
        [SerializeField] private bool disableSharedWorldRootOnUserExit = true;
        [SerializeField] private bool destroyRuntimeCloneRootChildrenOnUserExit = true;
        [SerializeField] private bool activateSharedWorldRootOnRoomEntry = false;
        [SerializeField] private bool allowSharedWorldRootActivationFromRealtimeRoom = false;

        [Header("Reconnect Failure Cleanup")]
        [SerializeField] private bool cleanupSharedWorldAfterPermanentReconnectFailure = true;
        [SerializeField] private float permanentReconnectFailureTimeoutSeconds = 180f;
        [SerializeField] private bool invokeDisconnectedFor3DAfterPermanentReconnectFailure = true;
        [Header("CheckNet Fast Reconnect Watch")]
        [SerializeField] private bool enableCheckNetFastReconnectWatch = true;
        [SerializeField] private float checkNetFastWatchIntervalSeconds = 0.75f;
        [SerializeField] private int checkNetFastTimeoutMs = 2000;
        [SerializeField] private int checkNetFastFailuresBeforeDisconnect = 2;
        [SerializeField] private float dedicatedGameServerInboundAliveProofSeconds = 2.25f;
        [SerializeField] private bool useSingleCheckNetFailureInsideDedicatedGameServer = true;

        private bool checkNetFastWatchRunning;
        private float nextCheckNetFastWatchAt;
        private int checkNetFastConsecutiveFailures;
        private bool checkNetFastOutageActive;
        private bool checkNetFastReconnectKickRequested;
        private bool checkNetFastWarningOnlyPanelActive;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private const string SingleCheckNetFailureTestArgument =
            "-networkTestSingleCheckNetFailure";
        private const int SingleCheckNetFailureValidationWindowMs = 2500;
        private bool networkValidationCommandLineChecked;
        private bool singleCheckNetFailureTestArmed;
        private bool singleCheckNetFailureTestRunning;
        private bool forceNextCheckNetFailureForValidation;
#endif
        [Header("Realtime Reconnect Loop")]
        [SerializeField] private bool enableAutomaticRealtimeReconnect = true;
        [SerializeField] private bool rejoinLastRoomAfterRealtimeReconnect = true;
        [SerializeField] private float realtimeReconnectInitialDelaySeconds = 1f;
        [SerializeField] private float realtimeReconnectMaxDelaySeconds = 8f;
        [SerializeField] private string realtimeReconnectStartingMessage = "اتصال Realtime قطع شد. در حال تلاش برای اتصال دوباره...";
        [SerializeField] private string realtimeReconnectAttemptMessage = "در حال تلاش برای اتصال دوباره به Realtime...";
        [SerializeField] private string realtimeReconnectRejoinRoomMessage = "اتصال Realtime برگشت. در حال ورود دوباره به روم...";
        [SerializeField] private string realtimeReconnectSuccessMessage = "اتصال دوباره به Realtime انجام شد.";
        [SerializeField] private string realtimeReconnectPrepareGameServerMessage = "اتصال بلادرنگ برگشت. در حال بازیابی روم و آماده‌سازی اتصال دوباره به گیم سرور...";
        [SerializeField] private string realtimeReconnectWaitingForGameServerMessage = "روم بازیابی شد. در حال اتصال دوباره به گیم سرور...";
        [SerializeField] private string realtimeReconnectFailedMessage = "اتصال دوباره به Realtime انجام نشد. از روم خارج شدید. لطفاً دوباره تلاش کنید.";
        [SerializeField] private bool refreshRoomListAfterRealtimeReconnectWithoutRejoin = true;
        [Header("Immediate Internet Lost UI")]
        [SerializeField] private bool showServerDebugPanelImmediatelyOnInternetLost = true;
        [SerializeField] private string internetLostDebugTitle = "اتصال اینترنت قطع است";
        [SerializeField] private string internetLostDebugMessage = "اینترنت شما قطع شده است. لطفاً اتصال اینترنت را بررسی کنید و دوباره تلاش کنید.";
        [SerializeField] private string realtimeInternetLostImmediateMessage = "اینترنت شما قطع شده است. لطفاً اتصال اینترنت را بررسی کنید و دوباره تلاش کنید.";
        private const string FixedInternetLostDebugTitle = "اتصال اینترنت قطع است";
        private const string FixedInternetLostUserMessage = "اینترنت شما قطع شده است. لطفاً اتصال اینترنت را بررسی کنید و دوباره تلاش کنید.";
        private const string FixedRealtimeTransportDropDebugTitle = "بازیابی اتصال Realtime";
        private const string FixedRealtimeTransportDropUserMessage = "ارتباط Realtime موقتاً قطع شد. در حال بازیابی اتصال...";
        [SerializeField] private bool suppressRealtimeReconnectProgressUiDuringNetworkIssue = true;
        [SerializeField] private bool suppressPlayerLeftUiDuringRealtimeReconnect = true;
        [SerializeField] private float playerLeftSuppressSecondsAfterRealtimeReconnect = 30f;
        [SerializeField] private bool suppressRealtimePlayerLeftUiWhileDedicatedGameServerActive = true;
        [SerializeField] private bool useAuthoritativeLobbyRoomUpdatedForPresenceCount = true;
        [SerializeField] private float dedicatedGameServerPresenceGuardSecondsAfterDisconnect = 60f;
        [SerializeField] private float realtimeNetworkIssueUiLockSeconds = 45f;
        [SerializeField] private bool keepInternetLostStatusWhileNetworkIssueUiLocked = true;
        [SerializeField] private bool enableFastRealtimeTcpConnectivityProbe = false;
        [SerializeField] private float fastRealtimeConnectivityProbeIntervalSeconds = 0.35f;
        [SerializeField] private int fastRealtimeConnectivityProbeTimeoutMs = 500;
        [SerializeField] private bool allowFastRealtimeTcpProbeToStartReconnect = false;
        [SerializeField] private int fastRealtimeConnectivityProbeFailuresBeforeReconnect = 1;
        [Header("Manual Exit Camera Safety")]
        [SerializeField] private bool detachMainCameraBeforeRuntimeCloneCleanup = true;
        [SerializeField] private Camera mainCameraOverride;
        [SerializeField] private Transform mainCameraSafeParent;
        [SerializeField] private bool keepMainCameraWorldPoseOnDetach = true;

        [Header("Auth Login Ready Realtime Start")]
        [SerializeField] private bool autoConnectRealtimeAfterAuthLogin = false;
        [SerializeField] private bool autoListRoomsAfterAuthRealtimeConnect = true;
        [SerializeField] private bool refreshRoomListAfterLeaveRoom = true;
        [SerializeField] private bool autoConnectRealtimeOnlyWhenDisconnected = true;
        [SerializeField] private float autoConnectRealtimeAfterAuthDelaySeconds = 0.1f;
        private bool immediateInternetLostHandled;
        private bool internetLostPanelShownForCurrentOutage;
        private bool fastRealtimeProbeRunning;
        private float nextFastRealtimeProbeAt;
        private int consecutiveFastRealtimeProbeFailures;
        private bool realtimeNetworkIssueUiLocked;
        private float realtimeNetworkIssueUiLockedUntil;
        private float suppressPlayerLeftUiUntil;
        private bool dedicatedGameServerPresenceGuardActive;
        private float dedicatedGameServerPresenceGuardUntil;
        private bool dedicatedRecoveryFreshRealtimeDelivered;
        private readonly Dictionary<string, bool> realtimePresenceStateByPlayerId =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        //* این شمارنده هر بار که یک اتصال ریل‌تایم واقعاً و تازه احراز هویت می‌شود، افزایش پیدا می‌کند.
        //* هدف این است که نتیجه‌ی دیرهنگام و بی‌اعتبار پروب‌های async (که برای اتصال قدیمی شروع شده بودند)
        //* بعد از برقراری یک اتصال جدید و سالم، اشتباهاً یک قطعی جعلی/فیک left نسازد.
        private int connectionGenerationId;
        private float lastRealtimeInboundRealtimeSeconds = -9999f;

        private RealtimeClient realtimeClient;
        private RealtimeAuthClient realtimeAuthClient;
        private RealtimeLobbyClient realtimeLobbyClient;
        private GameServerClient gameServerClient;
        private CancellationTokenSource lifecycleCts;
        private CancellationTokenSource keepAliveCts;

        private TaskCompletionSource<bool> authWaiter;
        private TaskCompletionSource<bool> leaveAckWaiter;

        private string lastHandledGameAckMessageId = string.Empty;
        private float lastHandledGameAckTime = -1f;

        private string activeServerUrl = string.Empty;
        private string activeRoomId = string.Empty;
        private string activeRoomName = string.Empty;

        private bool isConnected;
        private bool isAuthenticated;
        private bool isJoined;
        private bool eventsBound;
        private bool isCleaningUp;
        private bool isAutoConnectRealtimeAfterAuthRunning;
        private bool isConnectAndAuthRunning;
        private bool isCreateRoomRunning;
        private bool hasCreateRoomButtonState;
        private bool lastCreateRoomButtonInteractable;
        private string lastCreateRoomButtonStateReason = string.Empty;
        private string lastRoomNameInputTextForButtonSync = string.Empty;
        private bool lastRealtimeReadyForCreateButtonSync;
        private bool lastCreateRoomRunningForButtonSync;
        private bool lastCleaningUpForButtonSync;
        private bool transportDropAlreadyHandled;
        private bool isUserRequestedExitFlow;
        private bool manualExitWorldCleanupApplied;
        private bool permanentReconnectFailureCleanupApplied;
        private Coroutine permanentReconnectFailureCleanupCoroutine;
        private string activePermanentReconnectFailureReason = string.Empty;
        private CancellationTokenSource realtimeReconnectCts;
        private bool isRealtimeReconnectRunning;
        private int realtimeReconnectAttemptCount;

        private RealtimeRoomDto[] lastListedRooms = Array.Empty<RealtimeRoomDto>();
        private readonly StringBuilder logBuffer = new StringBuilder(4096);

        private string pendingStatusTextValue = string.Empty;
        private string pendingLogTextValue = string.Empty;
        private string pendingRoomTextValue = string.Empty;
        private bool hasPendingStatusTextRefresh;
        private bool hasPendingLogTextRefresh;
        private bool hasPendingRoomTextRefresh;

        private bool isJoiningFromRoomList;
        private bool isJoinRoomRunning;
        private bool isLeaveRoomRunning;
        private bool hasLeaveRoomButtonState;
        private bool lastLeaveRoomButtonInteractable;
        private string lastLeaveRoomButtonStateReason = string.Empty;
        private RealtimeRoomDto selectedListedRoom;
        private readonly List<RealtimeRoomListItemView> roomListItems = new List<RealtimeRoomListItemView>();
        private string lastCreatedRoomId = string.Empty;
        private RealtimeRoomDto joinedRoom;

        private string currentRealtimeUserId = string.Empty;
        private string currentRealtimeUserName = string.Empty;
        private bool currentUserHasCreatedRoom;
        private bool isCreateRoomAvailabilityChecking;
        private string currentUserCreatedRoomId = string.Empty;
        private bool lastCurrentUserHasCreatedRoomForButtonSync;
        private bool lastCreateRoomAvailabilityCheckingForButtonSync;

        private bool isSendMessageRunning;
        private bool hasSendMessageButtonState;

        private const string PresenceChannelName = RealtimeChannels.Presence;
        private const string PresencePlayerStateTypeName = RealtimeMessageTypes.PlayerState;
        private const string PresenceRoomMembersSnapshotTypeName = RealtimeMessageTypes.RoomMembersSnapshot;

        public string CurrentRoomId => activeRoomId;
        public string CurrentRoomName => activeRoomName;
        public string CurrentUserId => currentRealtimeUserId;
        public string CurrentUserName => currentRealtimeUserName;
        public bool IsJoinedRoom => isJoined;
        public bool IsRealtimeReadyState => IsRealtimeReady();
        public bool IsRealtimeReconnectRunningState => isRealtimeReconnectRunning;
        public float LastRealtimeInboundRealtimeSeconds =>
            lastRealtimeInboundRealtimeSeconds;

        //* این تابع از بایندر گیم‌سرور صدا زده می‌شود تا وقتی کلاینت واقعاً داخل ددیکیتد گیم‌سرور است،
        //* رخدادهای player_left قدیمی ریل‌تایم باعث حذف اشتباه پلیرها و پیام left نشوند.
        public void SetDedicatedGameServerPresenceGuardActive(bool active, string reason)
        {
            if (!suppressRealtimePlayerLeftUiWhileDedicatedGameServerActive) return;

            dedicatedGameServerPresenceGuardActive = active;
            string safeReason = SafeText(reason);

            if (!active ||
                safeReason.IndexOf("dedicated_disconnected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                safeReason.IndexOf("dedicated_authenticated", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                dedicatedRecoveryFreshRealtimeDelivered = false;
            }

            if (active)
            {
                dedicatedGameServerPresenceGuardUntil = Time.realtimeSinceStartup + Mathf.Max(1f, dedicatedGameServerPresenceGuardSecondsAfterDisconnect);
            }
            else
            {
                dedicatedGameServerPresenceGuardUntil = Time.realtimeSinceStartup + Mathf.Max(1f, dedicatedGameServerPresenceGuardSecondsAfterDisconnect);
            }

            Log("Dedicated game server presence guard updated. active=" + active + " | reason=" + safeReason + " | until=" + dedicatedGameServerPresenceGuardUntil.ToString("F1"));
        }

        public event Action<string> OnRoomJoinedFor3D;
        public event Action<string> OnRoomLeftFor3D;
        public event Action<string> OnRealtimeDisconnectedFor3D;
        public event Action<string, string> OnPlayerJoinedFor3D;
        public event Action<string, string> OnPlayerLeftFor3D;
        public event Action<RealtimeEnvelope> OnPlayerStateReceivedFor3D;
        public event Action<RealtimeEnvelope> OnRoomMembersSnapshotReceivedFor3D;
        public event Action<string> OnRealtimeConnectionLostForReconnectFor3D;
        public event Action<string> OnRealtimeReconnectFailedPermanentlyFor3D;
        public event Action<string> OnManualWorldCleanupFor3D;

        private bool lastSendMessageButtonInteractable;
        private string lastSendMessageButtonStateReason = string.Empty;
        private string lastMessageInputTextForButtonSync = string.Empty;
        private bool lastRealtimeReadyForSendButtonSync;
        private bool lastJoinedForSendButtonSync;
        private bool lastJoinRoomRunningForSendButtonSync;
        private bool lastLeaveRoomRunningForSendButtonSync;
        private bool lastCleaningUpForSendButtonSync;
        private bool lastSendMessageRunningForButtonSync;

#if UNITY_EDITOR
        //* این تابع مقدار قدیمی اکشن تایپ چت را در ادیتور به مقدار مشترک وب‌جی‌ال و نیتیو تبدیل می کند تا پیام ها بین دو ترنسپورت فیلتر نشوند.
        private void OnValidate()
        {
            if (string.Equals(chatActionType, "grpc_g7_lobby_chat", StringComparison.Ordinal))
            {
                chatActionType = "webgl_g7_lobby_chat";
            }
        }
#endif

        private void Awake()
        {
            EnsureLifecycleToken();
            activeServerUrl = ResolveRealtimeServerUrl();
            activeRoomName = BuildRoomName();
            AutoResolveServerDebugReferences("Awake");
            BindServerDebugButtonHandlers("Awake");
            LogUiReferences("Awake");
            UpdateRoomDisplay();
            SetStatus("Ready");
            Log("G7 WebSocket controller ready. url=" + activeServerUrl);
            Log("[WEBGL_RECONNECT_FIX_VERSION] v4.3.7_reconnect_success_panel_cleanup_fix");
            ApplyPendingUiRefresh();
            BindMessageInputEvents();
            BindRoomNameInputEvents();
            SyncCreateRoomButtonFromRoomInput(true);
            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            SyncSendMessageButtonFromMessageInput(true);
        }

        private void OnEnable()
        {
            LogUiReferences("OnEnable");
            BindAuthLoginReadyEvent("OnEnable");

            BindMessageInputEvents();
            BindRoomNameInputEvents();
            SyncCreateRoomButtonFromRoomInput(true);
            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            SyncSendMessageButtonFromMessageInput(true);
        }

        private void Update()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            ProcessSingleCheckNetFailureValidationTest();
#endif
            DetectImmediateInternetLostByLocalNetwork();
            CheckNetFastReconnectWatch();
            DetectImmediateInternetLostByFastProbe();
            DetectRealtimeConnectionDrop();
            SyncCreateRoomButtonFromRoomInput(false);
            SyncSendMessageButtonFromMessageInput(false);
            ClearStaleNetworkStatusIfFullyRecovered("update_full_recovery");
            ApplyPendingUiRefresh();
        }

        //* این تابع هنگام حذف آبجکت فقط پاکسازی سبک انجام می دهد و هیچ عملیات شبکه ای را await نمی کند تا ادیتور در ریلود دامین گیر نکند.
        private void OnDestroy()
        {
            try
            {
                UnbindAuthLoginReadyEvent();
                ReleaseForDestroyWithoutNetworkAwait();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[G7-WebSocket-RoomLobby] Destroy cleanup warning: " + ex.Message);
            }
        }

        public async void ConnectAndAuthButton()
        {
            if (isConnectAndAuthRunning) return;
            if (!CanStartNormalRealtimeConnectNow("connect_and_auth_button")) return;

            isUserRequestedExitFlow = false;
            manualExitWorldCleanupApplied = false;
            permanentReconnectFailureCleanupApplied = false;
            transportDropAlreadyHandled = false;

            isConnectAndAuthRunning = true;
            ShowServerDebugPanelForRealtimeProgress(
                realtimeConnectPreparingMessage,
                "REALTIME_CONNECT_BUTTON_CLICKED",
                "Connect To Realtime button clicked.",
                true
            );

            UpdateConnectionButtons();
            UpdateCreateRoomButton();

            try
            {
                bool connectedAndAuthenticated = await LoginCheckConnectAndAuthAsync();

                // نمایش موفقیت داخل LoginCheckConnectAndAuthAsync انجام می شود.
                // اینجا فقط شکست نهایی دکمه مدیریت می شود تا پنل موفقیت دو بار ثبت نشود.
                if (!connectedAndAuthenticated)
                {
                    ShowServerDebugPanelForRealtimeConnectFailure("connect_button_result_false");
                }
            }
            catch (Exception ex)
            {
                ShowServerDebugPanelForRealtimeConnectFailure("connect_button_exception: " + ex.Message);
                throw;
            }
            finally
            {
                isConnectAndAuthRunning = false;
                UpdateConnectionButtons();
                UpdateCreateRoomButton();
                UpdateSendMessageButton();
            }
        }

        public async void CreateRoomButton()
        {
            if (isCreateRoomRunning) return;

            Log("Create room button clicked. ready=" + IsRealtimeReady() + " | roomNameLength=" + GetRoomNameInputLength());

            if (!IsRealtimeReady())
            {
                ShowRealtimeWarningMessage("Connect/Auth first. Create Room is disabled while disconnected.");
                UpdateCreateRoomButton();
                return;
            }

            if (!IsRoomNameInputValidForCreateRoom())
            {
                ShowRealtimeWarningMessage("Room name must be more than 7 characters.");
                UpdateCreateRoomButton();
                return;
            }

            isCreateRoomRunning = true;
            UpdateCreateRoomButton();

            try
            {
                await CreateRoomAsync();
            }
            finally
            {
                isCreateRoomRunning = false;
                UpdateCreateRoomButton();
            }
        }

        //* این تابع کلیک دکمه لیست روم را فقط وقتی قبول می کند که کانکشن آماده باشد و یوزر داخل روم نباشد.
        public async void ListRoomsButton()
        {
            if (!CanUseListRoomsButton())
            {
                Log("List rooms ignored. " + BuildListRoomsButtonStateReason());
                ShowRealtimeWarningMessage(IsRealtimeReady() ? "Leave current room first." : "Connect/Auth first.");
                UpdateListRoomsButton();
                UpdateSendMessageButton();
                return;
            }

            await ListRoomsAsync();
        }

        public async void JoinCreatedRoomButton()
        {
            await JoinFirstListedRoomAsync();
        }

        public async void JoinFirstListedRoomButton()
        {
            await JoinFirstListedRoomAsync();
        }

        public async void SendMessageButton()
        {
            if (isSendMessageRunning) return;

            if (!CanUseSendMessageButton())
            {
                Log("Send message ignored. " + BuildSendMessageButtonStateReason());
                ShowRealtimeErrorMessage(IsRealtimeReady() && isJoined ? "Message is empty. Please type a message first." : "Join a room first.");
                SyncSendMessageButtonFromMessageInput(true);
                return;
            }

            string text = GetMessageInputText().Trim();
            isSendMessageRunning = true;
            UpdateSendMessageButton();

            try
            {
                await SendChatMessageAsync(text);
            }
            finally
            {
                isSendMessageRunning = false;
                SyncSendMessageButtonFromMessageInput(true);
            }
        }

        //* این تابع کلیک دکمه خروج از روم را فقط وقتی قبول می کند که یوزر واقعاً داخل روم باشد.
        public async void LeaveRoomButton()
        {
            if (!CanUseLeaveRoomButton())
            {
                Log("Leave button ignored. " + BuildLeaveRoomButtonStateReason());
                ShowRealtimeWarningMessage("You are not inside a room.");
                UpdateLeaveRoomButton();
                return;
            }

            await LeaveRoomAsync();
        }

        public async void DisconnectButton()
        {
            isUserRequestedExitFlow = true;
            await CleanupAsync("Manual G7 disconnect", false, true);
        }

        public async void RunFullLobbyTestButton()
        {
            await RunFullLobbyTestAsync();
        }

        public async Task<bool> RunFullLobbyTestAsync()
        {
            Log("G7 WebSocket full lobby test started.");

            if (!await LoginCheckConnectAndAuthAsync()) return false;
            if (!await CreateRoomAsync()) return false;
            if (!await ListRoomsAsync()) return false;
            if (!await JoinFirstListedRoomAsync()) return false;
            if (!await SendChatMessageAsync("G7 lobby test message")) return false;
            if (!await LeaveRoomAsync()) return false;

            Log("G7 WebSocket full lobby test completed.");
            SetStatus("G7 WebSocket PASSED");
            return true;
        }

        private bool CanStartNormalRealtimeConnectNow(string source)
        {
            bool authManagerExists = AuthManager.Instance != null;
            bool authLoginCompleted = authManagerExists && AuthManager.Instance.isLogin && AuthManager.Instance.CurrentUser != null;

            if (authLoginCompleted) return true;

            string details =
                "source=" + SafeText(source) +
                " | authManagerExists=" + authManagerExists +
                " | authManagerIsLogin=" + (authManagerExists ? AuthManager.Instance.isLogin.ToString() : "False") +
                " | hasCurrentUser=" + (authManagerExists && AuthManager.Instance.CurrentUser != null) +
                " | hasAccessToken=" + !string.IsNullOrWhiteSpace(SecureTokenStorage.GetAccessToken()) +
                " | hasRefreshToken=" + !string.IsNullOrWhiteSpace(SecureTokenStorage.GetRefreshToken());

            Log("Realtime normal connect blocked because AuthManager login is not completed. " + details);

            ShowServerDebugPanelForRealtimeProgress(
                "ابتدا باید ورود کاربر کامل شود. اتصال Realtime قبل از ورود موفق مجاز نیست.",
                "REALTIME_CONNECT_BLOCKED_AUTH_NOT_READY",
                details,
                false
            );

            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();
            return false;
        }

        public async Task<bool> LoginCheckConnectAndAuthAsync()
        {
            return await LoginCheckConnectAndAuthInternalAsync(
                SecureTokenStorage.GetAccessToken(),
                "stored_token"
            );
        }

        public async Task<bool> LoginCheckConnectAndAuthWithAccessTokenAsync(
            string accessToken
        )
        {
            return await LoginCheckConnectAndAuthInternalAsync(
                accessToken,
                "explicit_token"
            );
        }

        //* این تابع مسیر مشترک اتصال WebSocket را برای توکن ذخیره شده و توکن صریح اجرا می کند.
        private async Task<bool> LoginCheckConnectAndAuthInternalAsync(
            string accessToken,
            string tokenSource
        )
        {
            EnsureLifecycleToken();
            string safeTokenSource = SafeTokenSource(tokenSource);

            if (IsRealtimeReady())
            {
                // این مسیر فقط یک Gate است. وقتی Realtime از قبل آماده است،
                // نباید دوباره ListRooms یا پیام موفقیت اتصال اجرا شود.
                UpdateConnectionButtons();
                UpdateCreateRoomButton();
                UpdateSendMessageButton();

                Log(
                    "Realtime connect/auth gate reused the existing authenticated connection. tokenSource=" +
                    safeTokenSource
                );
                return true;
            }

            if (realtimeClient != null && !realtimeClient.IsConnected)
            {
                Log("Realtime client state is stale. Recreating client objects.");
                CleanupClientObjectsOnly();
                isConnected = false;
                isAuthenticated = false;
                isJoined = false;
                isJoinRoomRunning = false;
                isLeaveRoomRunning = false;
            }

            accessToken = await EnsureFreshAccessTokenBeforeRealtimeAuthAsync(
                accessToken,
                safeTokenSource
            );

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return Fail(
                    "Realtime access token is empty after refresh gate. tokenSource=" +
                    safeTokenSource
                );
            }

            UpdateCurrentUserIdentityFromAccessToken(accessToken);

            if (realtimeClient == null)
            {
                CreateClientObjects();
            }

            bool connected = await ConnectAsync();
            if (!connected)
            {
                return Fail("Realtime connect failed.");
            }

            bool authenticated = await AuthenticateWithAccessTokenAsync(
                accessToken
            );
            if (!authenticated)
            {
                return Fail(
                    "Realtime auth failed. tokenSource=" + safeTokenSource
                );
            }

            StopRealtimeReconnectLoop("realtime_authenticated_by_normal_flow");
            StopPermanentReconnectFailureCleanupWatch("realtime_reconnect_authenticated");
            permanentReconnectFailureCleanupApplied = false;

            ShowServerDebugPanelForRealtimeProgress(
                realtimeRoomSyncMessage,
                "REALTIME_ROOM_STATE_SYNC",
                "Refreshing room ownership state after auth.",
                true
            );

            await RefreshCurrentUserCreatedRoomStateAsync();

            StartKeepAliveLoop();

            ShowRealtimeSuccessMessage("Realtime connected and authenticated.");
            Log(
                "Realtime connection and auth completed. tokenSource=" +
                safeTokenSource
            );

            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();

            ShowServerDebugPanelForRealtimeConnectSuccess(
                "login_check_connect_and_auth_success"
            );

            return true;
        }
//

        //* این تابع قبل از آث ریل تایم، اکسس توکن را تازه می کند تا توکن اکسپایر شده ارسال نشود.
        private async Task<string> EnsureFreshAccessTokenBeforeRealtimeAuthAsync(string accessToken, string tokenSource)
        {
            string safeTokenSource = string.IsNullOrWhiteSpace(tokenSource) ? "unknown" : tokenSource.Trim();

            if (!IsAccessTokenRefreshRequired(accessToken))
            {
                return string.IsNullOrWhiteSpace(accessToken) ? string.Empty : accessToken.Trim();
            }

            if (string.IsNullOrWhiteSpace(SecureTokenStorage.GetRefreshToken()))
            {
                Log("Access token refresh is required before realtime auth, but refresh token is empty. tokenSource=" + safeTokenSource);
                return string.Empty;
            }

            Log("Access token is expired or near expiry. Refreshing before realtime auth. tokenSource=" + safeTokenSource);
            ShowServerDebugPanelForRealtimeProgress(realtimeTokenRefreshingMessage, "REALTIME_TOKEN_REFRESH", "tokenSource=" + safeTokenSource, true);

            bool refreshed = await AuthRefreshManager.Refresh();

            if (!refreshed)
            {
                Log("Refresh before realtime auth failed. tokenSource=" + safeTokenSource);
                return string.Empty;
            }

            string refreshedToken = SecureTokenStorage.GetAccessToken();

            if (string.IsNullOrWhiteSpace(refreshedToken))
            {
                Log("Refresh before realtime auth returned empty access token. tokenSource=" + safeTokenSource);
                return string.Empty;
            }

            Log("Refresh before realtime auth succeeded. tokenSource=" + safeTokenSource);
            ShowServerDebugPanelForRealtimeProgress(realtimeTokenCheckingMessage, "REALTIME_TOKEN_REFRESH_SUCCESS", "tokenSource=" + safeTokenSource, true);
            return refreshedToken.Trim();
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

        private async Task<bool> ConnectAsync()
        {
            EnsureLifecycleToken();
            activeServerUrl = ResolveRealtimeServerUrl();
            Log("Connecting to " + activeServerUrl + " | uiTimeoutMs=" + connectTimeoutMs);
            ShowServerDebugPanelForRealtimeProgress(realtimeSocketConnectingMessage, "REALTIME_SOCKET_CONNECTING", "url=" + SafeText(activeServerUrl) + " | timeoutMs=" + connectTimeoutMs, true);

            Task<bool> connectTask = realtimeClient.ConnectAsync(null, lifecycleCts.Token);

            if (connectTimeoutMs > 0)
            {
                Task timeoutTask = Task.Delay(Mathf.Max(1000, connectTimeoutMs), lifecycleCts.Token);
                Task completedTask = await Task.WhenAny(connectTask, timeoutTask);

                if (completedTask != connectTask)
                {
                    isConnected = false;
                    isAuthenticated = false;
                    transportDropAlreadyHandled = true;
                    Log("Connect timeout before realtime client reported connected. timeoutMs=" + connectTimeoutMs);
                    ShowServerDebugPanelForRealtimeConnectFailure("connect_timeout_ms_" + connectTimeoutMs);
                    UpdateConnectionButtons();
                    UpdateCreateRoomButton();
                    UpdateSendMessageButton();
                    return false;
                }
            }

            bool connected = await connectTask;
            isConnected = connected && realtimeClient.IsConnected;
            transportDropAlreadyHandled = !isConnected;
            Log("Connect result: " + isConnected + " | lifetimeTokenUsed=True");
            if (isConnected)
            {
                if (isRealtimeReconnectRunning)
                {
                    Log("Reconnect transport connected internally. Socket connected UI message is suppressed until realtime auth result.");
                }
                else
                {
                    ShowServerDebugPanelForRealtimeProgress(realtimeAuthenticatingMessage, "REALTIME_SOCKET_CONNECTED", "url=" + SafeText(activeServerUrl), true);
                }
            }
            else
            {
                ShowServerDebugPanelForRealtimeConnectFailure("connect_result_false");
            }
            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();
            return isConnected;
        }

        //* این تابع برای ارسال پیام احراز هویت و انتظار پاسخ، دو تایم اوت مستقل می سازد تا زمان صرف شده در Send، مهلت انتظار auth_ok را مصرف نکند.
        private async Task<bool> AuthenticateWithStoredTokenAsync()
        {
            EnsureLifecycleToken();
            authWaiter = CreateBoolWaiter();

            int safeSendTimeoutMs = Mathf.Max(1000, sendTimeoutMs);

            try
            {
                using (CancellationTokenSource authSendCts = CreateLinkedTimeoutToken(safeSendTimeoutMs))
                {
                    bool sent = await realtimeAuthClient.AuthenticateWithStoredTokenAsync(authSendCts.Token);
                    if (!sent) return Fail("Realtime auth message was not sent.");
                }
            }
            catch (OperationCanceledException)
            {
                Log("Realtime auth send cancelled or timed out. sendTimeoutMs=" + safeSendTimeoutMs);
                return Fail("Realtime auth message send timed out.");
            }

            Log(
                "Realtime auth message send completed. Fresh auth acknowledgement timeout started. sendTimeoutMs=" +
                safeSendTimeoutMs +
                " | waitTimeoutMs=" +
                waitTimeoutMs
            );

            using (CancellationTokenSource authWaitCts = CreateLinkedTimeoutToken(waitTimeoutMs))
            {
                bool ok = await WaitForBoolAsync(authWaiter, waitTimeoutMs, authWaitCts.Token);
                isAuthenticated = ok && realtimeAuthClient.IsAuthenticated;

                Log("Auth result: " + isAuthenticated);
                if (isAuthenticated) ShowServerDebugPanelForRealtimeProgress(realtimeRoomSyncMessage, "REALTIME_AUTH_OK", "Realtime auth acknowledged by server.", true);
                else ShowServerDebugPanelForRealtimeConnectFailure("auth_wait_result_false");
                UpdateConnectionButtons();
                UpdateCreateRoomButton();
                return isAuthenticated;
            }
        }

        //* این تابع همان تفکیک تایم اوت Send و auth_ok را برای توکن صریح WebGL حفظ می کند.
        private async Task<bool> AuthenticateWithAccessTokenAsync(
            string accessToken
        )
        {
            EnsureLifecycleToken();
            authWaiter = CreateBoolWaiter();

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return Fail("Realtime auth token is empty before send.");
            }

            int safeSendTimeoutMs = Mathf.Max(1000, sendTimeoutMs);

            try
            {
                using (
                    CancellationTokenSource authSendCts =
                        CreateLinkedTimeoutToken(safeSendTimeoutMs)
                )
                {
                    bool sent =
                        await realtimeAuthClient.AuthenticateWithAccessTokenAsync(
                            accessToken.Trim(),
                            authSendCts.Token
                        );

                    if (!sent)
                    {
                        return Fail("Realtime auth message was not sent.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Log(
                    "Realtime auth send cancelled or timed out. sendTimeoutMs=" +
                    safeSendTimeoutMs
                );

                return Fail("Realtime auth message send timed out.");
            }

            Log(
                "Realtime auth message send completed. Fresh auth acknowledgement timeout started. sendTimeoutMs=" +
                safeSendTimeoutMs +
                " | waitTimeoutMs=" +
                waitTimeoutMs
            );

            using (
                CancellationTokenSource authWaitCts =
                    CreateLinkedTimeoutToken(waitTimeoutMs)
            )
            {
                bool ok = await WaitForBoolAsync(
                    authWaiter,
                    waitTimeoutMs,
                    authWaitCts.Token
                );

                isAuthenticated =
                    ok &&
                    realtimeAuthClient != null &&
                    realtimeAuthClient.IsAuthenticated;

                Log("Auth result: " + isAuthenticated);

                if (isAuthenticated)
                {
                    ShowServerDebugPanelForRealtimeProgress(
                        realtimeRoomSyncMessage,
                        "REALTIME_AUTH_OK",
                        "Realtime auth acknowledged by server.",
                        true
                    );
                }
                else
                {
                    ShowServerDebugPanelForRealtimeConnectFailure(
                        "auth_wait_result_false"
                    );
                }

                UpdateConnectionButtons();
                UpdateCreateRoomButton();
                return isAuthenticated;
            }
        }

        public async Task<bool> CreateRoomAsync()
        {
            if (!IsRealtimeReady()) return Fail("Client is not connected/authenticated. Click Connect/Auth first.");
            if (!IsRoomNameInputValidForCreateRoom()) return Fail("Room name must be more than 7 characters.");
            if (blockCreateRoomWhenCurrentUserAlreadyOwnsRoom && !await CheckCurrentUserCanCreateRoomAsync()) return false;

            activeRoomName = BuildRoomName();
            Log("Create room request started. name=" + activeRoomName);

            var request = new RealtimeCreateRoomRequestDto(
                activeRoomName,
                roomDescription,
                roomVisibility,
                maxPlayers
            );

            RealtimeLobbyCreateRoomResult result = await realtimeLobbyClient.CreateRoomAsync(
                request,
                CreateReliableOptions(),
                lifecycleCts.Token
            );

            if (result == null) return Fail("Create room result is null.");
            if (!result.isSuccess) return Fail("Create room failed: " + result.errorMessage);
            if (result.room == null || !result.room.HasValidRoomId()) return Fail("Create room returned invalid room.");

            result.room.Normalize();

            lastCreatedRoomId = result.room.roomId;
            currentUserCreatedRoomId = result.room.roomId;
            currentUserHasCreatedRoom = true;

            if (!string.IsNullOrWhiteSpace(result.room.ownerUserName))
            {
                currentRealtimeUserName = result.room.ownerUserName.Trim();
            }

            selectedListedRoom = result.room;
            activeRoomId = string.Empty;
            activeRoomName = string.Empty;

            UpdateRoomDisplay(result.room, false);
            ShowRealtimeSuccessMessage("Room created. Select it from the room list to join.");
            Log("Room created by server: " + result.room.roomId + " | " + result.room.roomName + " | owner=" + result.room.ownerUserName);
            UpdateCreateRoomButton();
            UpdateSendMessageButton();

            await ListRoomsAsync();
            return true;
        }

        public async Task<bool> ListRoomsAsync()
        {
            if (isJoined)
            {
                SetListRoomsButtonInteractable(false);
                Log("List rooms skipped. Client is already joined to a room.");
                ShowRealtimeWarningMessage("You are already inside a room. Leave current room first.");
                UpdateConnectionButtons();
                UpdateCreateRoomButton();
                UpdateSendMessageButton();
                return false;
            }

            if (!await EnsureRealtimeReadyForUtilityMethodAsync("List rooms"))
            {
                return false;
            }

            if (!IsRealtimeReady())
            {
                return Fail("List rooms requires an authenticated Realtime connection.");
            }

            RealtimeLobbyListRoomsResult result = await realtimeLobbyClient.ListRoomsAsync(
                CreateReliableOptions(),
                lifecycleCts.Token
            );

            if (result == null) return Fail("List rooms result is null.");
            if (!result.isSuccess) return Fail("List rooms failed: " + result.errorMessage);

            lastListedRooms = result.Rooms ?? Array.Empty<RealtimeRoomDto>();

            RenderRooms(lastListedRooms);
            RenderRoomListButtons(lastListedRooms);
            SetRoomListInteractable(true);
            SetListRoomsButtonInteractable(true);

            UpdateCurrentUserIdentityFromStoredToken();
            int ownedRoomCount = CountRoomsOwnedByCurrentUser(
                ResolveCurrentOwnerNameForCreateRoomCheck(),
                out RealtimeRoomDto firstOwnedRoom
            );

            currentUserHasCreatedRoom = ownedRoomCount > 0;

            if (firstOwnedRoom != null)
            {
                firstOwnedRoom.Normalize();
                lastCreatedRoomId = firstOwnedRoom.roomId;
                currentUserCreatedRoomId = firstOwnedRoom.roomId;
            }
            else
            {
                currentUserCreatedRoomId = string.Empty;
            }

            ShowRealtimeInfoMessage("Rooms refreshed. Count: " + result.Count);
            Log(
                "List rooms result: count=" +
                result.Count +
                " | ownedRoomCount=" +
                ownedRoomCount
            );

            if (!string.IsNullOrWhiteSpace(lastCreatedRoomId))
            {
                RealtimeRoomDto listedRoom = result.response == null ? null : result.response.FindRoomById(lastCreatedRoomId);
                Log("Created room exists in list: " + (listedRoom != null));
            }

            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();
            return true;
        }

        private async Task<bool> CheckCurrentUserCanCreateRoomAsync()
        {
            UpdateCurrentUserIdentityFromStoredToken();

            if (!HasCurrentUserIdentityForCreateRoomCheck())
            {
                UpdateCreateRoomButton();
                return Fail("Current user identity for create room check is empty.");
            }

            Log("Create room owner check started. userId=" + currentRealtimeUserId + " | userName=" + ResolveCurrentOwnerNameForCreateRoomCheck());

            bool listed = await RefreshCurrentUserCreatedRoomStateAsync();
            if (!listed) return false;

            if (!currentUserHasCreatedRoom) return true;

            ShowRealtimeWarningMessage("You already created a room. Select your room from the list to join.");
            return false;
        }

        private async Task<bool> RefreshCurrentUserCreatedRoomStateAsync()
        {
            if (!IsRealtimeReady())
            {
                Log("Create room availability check skipped. realtime is not ready.");
                return false;
            }

            if (realtimeLobbyClient == null)
            {
                Log("Create room availability check skipped. lobby client is null.");
                return false;
            }

            isCreateRoomAvailabilityChecking = true;
            UpdateCreateRoomButton();

            try
            {
                bool listed = await RefreshRoomsForCreateRoomCheckAsync();
                if (!listed) return false;

                int ownedRoomCount = CountRoomsOwnedByCurrentUser(ResolveCurrentOwnerNameForCreateRoomCheck(), out RealtimeRoomDto firstOwnedRoom);
                currentUserHasCreatedRoom = ownedRoomCount > 0;

                if (firstOwnedRoom != null)
                {
                    firstOwnedRoom.Normalize();
                    lastCreatedRoomId = firstOwnedRoom.roomId;
                    currentUserCreatedRoomId = firstOwnedRoom.roomId;
                    Log("Create room ownership state updated without changing selected room. ownedRoomId=" + currentUserCreatedRoomId);
                }
                else
                {
                    currentUserCreatedRoomId = string.Empty;
                }

                Log("Create room availability check completed. hasCreatedRoom=" + currentUserHasCreatedRoom
                    + " | ownedRoomCount=" + ownedRoomCount
                    + " | userId=" + currentRealtimeUserId
                    + " | userName=" + ResolveCurrentOwnerNameForCreateRoomCheck()
                    + " | roomId=" + currentUserCreatedRoomId);

                return true;
            }
            finally
            {
                isCreateRoomAvailabilityChecking = false;
                UpdateCreateRoomButton();
                UpdateSendMessageButton();
            }
        }

        private async Task<bool> RefreshRoomsForCreateRoomCheckAsync()
        {
            if (!IsRealtimeReady()) return Fail("Create room owner check needs active Realtime connection.");

            RealtimeLobbyListRoomsResult result = await realtimeLobbyClient.ListRoomsAsync(
                CreateReliableOptions(),
                lifecycleCts.Token
            );

            if (result == null) return Fail("Create room owner check failed: list rooms result is null.");
            if (!result.isSuccess) return Fail("Create room owner check failed: " + result.errorMessage);

            lastListedRooms = result.Rooms ?? Array.Empty<RealtimeRoomDto>();

            RenderRooms(lastListedRooms);
            RenderRoomListButtons(lastListedRooms);
            SetListRoomsButtonInteractable(!isJoined);
            Log("Create room pre-check list rooms result: count=" + result.Count);
            UpdateCreateRoomButton();
            UpdateSendMessageButton();

            return true;
        }

        private int CountRoomsOwnedByCurrentUser(string ownerName, out RealtimeRoomDto firstOwnedRoom)
        {
            firstOwnedRoom = null;
            if (lastListedRooms == null) return 0;

            int count = 0;

            for (int i = 0; i < lastListedRooms.Length; i++)
            {
                RealtimeRoomDto room = lastListedRooms[i];
                if (room == null) continue;

                room.Normalize();
                if (!room.HasValidRoomId()) continue;
                if (room.IsClosed()) continue;
                if (!IsRoomOwnedByCurrentUser(room, ownerName)) continue;

                count++;
                if (firstOwnedRoom == null) firstOwnedRoom = room;
            }

            return count;
        }

        private bool IsRoomOwnedByCurrentUser(RealtimeRoomDto room, string ownerName)
        {
            if (room == null) return false;

            string roomOwnerName = room.ownerUserName;
            string roomOwnerUserId = ReadRoomStringMember(room, "ownerUserId");
            string roomCreatorUserId = ReadRoomStringMember(room, "creatorUserId");
            string roomUserId = ReadRoomStringMember(room, "userId");

            bool idMatched = IsSameText(roomOwnerUserId, currentRealtimeUserId)
                             || IsSameText(roomCreatorUserId, currentRealtimeUserId)
                             || IsSameText(roomUserId, currentRealtimeUserId);

            bool nameMatched = IsSameText(roomOwnerName, ownerName)
                               || IsSameText(roomOwnerName, currentRealtimeUserName)
                               || IsSameText(roomOwnerName, currentOwnerNameForCreateRoomCheck);

            if (idMatched || nameMatched)
            {
                Log("Owned room matched. roomId=" + room.roomId
                    + " | ownerName=" + roomOwnerName
                    + " | ownerUserId=" + roomOwnerUserId
                    + " | creatorUserId=" + roomCreatorUserId
                    + " | currentUserId=" + currentRealtimeUserId
                    + " | currentUserName=" + currentRealtimeUserName);
            }

            return idMatched || nameMatched;
        }

        private string ResolveCurrentOwnerNameForCreateRoomCheck()
        {
            if (!string.IsNullOrWhiteSpace(currentOwnerNameForCreateRoomCheck)) return currentOwnerNameForCreateRoomCheck.Trim();
            if (!string.IsNullOrWhiteSpace(currentRealtimeUserName)) return currentRealtimeUserName.Trim();
            return string.Empty;
        }

        private bool HasCurrentUserIdentityForCreateRoomCheck()
        {
            return !string.IsNullOrWhiteSpace(currentRealtimeUserId) || !string.IsNullOrWhiteSpace(ResolveCurrentOwnerNameForCreateRoomCheck());
        }

        private static string ReadRoomStringMember(RealtimeRoomDto room, string memberName)
        {
            if (room == null || string.IsNullOrWhiteSpace(memberName)) return string.Empty;

            Type type = room.GetType();

            var field = type.GetField(memberName);
            if (field != null)
            {
                object value = field.GetValue(room);
                return value == null ? string.Empty : value.ToString();
            }

            var property = type.GetProperty(memberName);
            if (property != null)
            {
                object value = property.GetValue(room, null);
                return value == null ? string.Empty : value.ToString();
            }

            return string.Empty;
        }

        private static bool IsSameText(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private bool IsCurrentRealtimeUserPresence(string playerId, string displayName)
        {
            if (IsSameText(playerId, currentRealtimeUserId)) return true;
            if (IsSameText(playerId, currentRealtimeUserName)) return true;
            if (IsSameText(displayName, currentRealtimeUserId)) return true;
            if (IsSameText(displayName, currentRealtimeUserName)) return true;

            string safeDisplayName = string.IsNullOrWhiteSpace(displayName) ? string.Empty : displayName.Trim();
            string safeUserId = string.IsNullOrWhiteSpace(currentRealtimeUserId) ? string.Empty : currentRealtimeUserId.Trim();

            if (!string.IsNullOrWhiteSpace(safeDisplayName) &&
                !string.IsNullOrWhiteSpace(safeUserId) &&
                safeDisplayName.IndexOf(safeUserId, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        //* این تابع درخواست جوین روم را ارسال می کند و تا پایان جوین، دکمه خروج از روم را غیرفعال نگه می دارد.
        public async Task<bool> JoinRoomAsync()
        {
            if (isJoinRoomRunning)
            {
                Log("Join skipped. Join is already running.");
                UpdateLeaveRoomButton();
                return false;
            }

            isJoinRoomRunning = true;
            UpdateConnectionButtons();
            UpdateSendMessageButton();

            try
            {
                if (!await EnsureRealtimeReadyForUtilityMethodAsync("Join room"))
                {
                    return false;
                }
                if (string.IsNullOrWhiteSpace(activeRoomId)) return Fail("Room id is empty. Create room first or join a listed room.");
                if (isJoined && gameServerClient != null && gameServerClient.HasRoom) return true;

                RealtimeReliableSendResult result = await gameServerClient.JoinRoomReliableAsync(
                    activeRoomId,
                    CreateReliableOptions(),
                    lifecycleCts.Token
                );

                bool ok = result != null && result.isSuccess;
                isJoined = ok;

                Log("Join room result: " + ok + " | room=" + activeRoomId + " | attempts=" + (result == null ? 0 : result.attempts) + " | error=" + (result == null ? "null" : result.errorMessage));

                if (!ok)
                {
                    ShowRealtimeErrorMessage("Join failed.");
                    UpdateConnectionButtons();
                    UpdateSendMessageButton();
                    return false;
                }

                joinedRoom = CloneRoomDto(
                    selectedListedRoom ?? FindLastListedRoom(activeRoomId)
                );
                ResetRealtimePresenceCountTrackingForJoinedRoom();
                if (joinedRoom != null)
                {
                    joinedRoom.Normalize();
                    if (!string.IsNullOrWhiteSpace(joinedRoom.roomName)) activeRoomName = joinedRoom.roomName.Trim();

                    UpdateRoomDisplay(joinedRoom, true);
                    Log(
                        "Room users count displayed from the authoritative server snapshot without a local +1. " +
                        "Authoritative room_updated owns later onlineCount changes. roomId=" +
                        SafeText(joinedRoom.roomId) +
                        " | serverOnline=" +
                        joinedRoom.onlineCount
                    );
                    ShowRealtimeSuccessMessage("You joined to " + joinedRoom.roomName + ". Start chat.");
                }
                else
                {
                    UpdateRoomDisplay();
                    ShowRealtimeSuccessMessage("You joined to room. Start chat.");
                }

                SetRoomListInteractable(false);
                SetListRoomsButtonInteractable(false);
                UpdateConnectionButtons();
                UpdateSendMessageButton();
                if (string.IsNullOrWhiteSpace(activeRoomName) && joinedRoom != null) activeRoomName = joinedRoom.roomName;
                manualExitWorldCleanupApplied = false;
                isUserRequestedExitFlow = false;
                ActivateSharedWorldForRoomEntry("room_joined:" + SafeText(activeRoomId));
                OnRoomJoinedFor3D?.Invoke(activeRoomId);
                return true;
            }
            finally
            {
                isJoinRoomRunning = false;
                UpdateConnectionButtons();
                UpdateSendMessageButton();
            }
        }

        public async Task<bool> JoinFirstListedRoomAsync()
        {
            if (
                !await EnsureRealtimeReadyForUtilityMethodAsync(
                    "Join first listed room"
                )
            )
            {
                return false;
            }

            if (lastListedRooms == null || lastListedRooms.Length == 0)
            {
                bool listed = await ListRoomsAsync();
                if (!listed) return false;
            }

            RealtimeRoomDto room = FindFirstJoinableListedRoom();
            if (room == null) return Fail("No joinable room found in the latest room list.");

            selectedListedRoom = room;
            activeRoomId = room.roomId;
            activeRoomName = room.roomName;

            UpdateRoomDisplay(room, false);
            Log("Selected listed room: " + activeRoomId + " | " + activeRoomName);

            return await JoinRoomAsync();
        }

        public async Task<bool> SendChatMessageAsync(string text)
        {
            if (!EnsureReadyForRoomMessage())
            {
                UpdateSendMessageButton();
                return false;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                ShowRealtimeErrorMessage("Message is empty. Please type a message first.");
                UpdateSendMessageButton();
                return false;
            }

            string trimmedText = text.Trim();
            string payloadJson = BuildChatPayload(trimmedText);

            RealtimeReliableSendResult result = await gameServerClient.SendPlayerActionReliableAsync(
                chatActionType,
                payloadJson,
                CreateReliableOptions(),
                lifecycleCts.Token
            );

            bool ok = result != null && result.isSuccess;

            if (ok)
            {
                Log(ResolveLocalChatSenderName() + ": " + trimmedText);
                SetStatus("Message sent");
                if (messageInput != null) messageInput.text = string.Empty;
            }
            else
            {
                ShowRealtimeErrorMessage("Message send failed.");
            }

            Log("Chat send result: " + ok + " | attempts=" + (result == null ? 0 : result.attempts) + " | error=" + (result == null ? "null" : result.errorMessage));
            UpdateSendMessageButton();
            return ok;
        }

        //* این تابع اِنولوپ ریل تایم را بدون وابسته کردن منطق سه بعدی به کلاینت داخلی ارسال می کند.
        public async Task<bool> SendRealtimeEnvelopeAsync(RealtimeEnvelope envelope, RealtimeDeliveryPolicy deliveryPolicy, bool isPriority = false, CancellationToken cancellationToken = default)
        {
            if (envelope == null) return false;
            if (!IsRealtimeReady() || realtimeClient == null) return false;

            return await realtimeClient.SendEnvelopeWithPolicyAsync(envelope, deliveryPolicy, isPriority, cancellationToken);
        }

        //* این تابع خروج از روم را مدیریت می کند و وضعیت دکمه خروج را بعد از اَک یا خطا به روز می کند.
        public async Task<bool> LeaveRoomAsync()
        {
            if (isLeaveRoomRunning)
            {
                Log("Leave skipped. Leave is already running.");
                UpdateLeaveRoomButton();
                return false;
            }

            if (gameServerClient == null || !isJoined || string.IsNullOrWhiteSpace(activeRoomId))
            {
                Log("Leave skipped. Client is not joined.");
                isJoined = false;
                joinedRoom = null;
                selectedListedRoom = null;
                activeRoomId = string.Empty;
                activeRoomName = string.Empty;
                isUserRequestedExitFlow = false;
                SetRoomListInteractable(true);
                UpdateRoomDisplay();
                UpdateConnectionButtons();
                UpdateCreateRoomButton();
                UpdateSendMessageButton();
                return true;
            }

            bool leaveAcknowledged = false;
            isLeaveRoomRunning = true;
            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();

            try
            {
                leaveAckWaiter = CreateBoolWaiter();

                bool sent = await gameServerClient.LeaveRoomAsync(activeRoomId, lifecycleCts.Token);
                if (!sent) return Fail("Leave room message was not sent.");

                bool ack = await WaitForBoolAsync(leaveAckWaiter, waitTimeoutMs, lifecycleCts.Token);
                isJoined = !ack;
                leaveAcknowledged = ack;

                if (ack)
                {
                    string leftRoomIdFor3D = activeRoomId;

                    // این فلگ فقط هنگام اجرای خروج رسمی از روم فعال است تا افت هم‌زمان ترنسپورت،
                    // به‌اشتباه یک حلقه Reconnect جدید نسازد. بعد از پایان Leave دوباره آزاد می‌شود.
                    isUserRequestedExitFlow = true;

                    OnRoomLeftFor3D?.Invoke(leftRoomIdFor3D);
                    CleanupSharedWorldAfterUserExit("manual_leave_room:" + leftRoomIdFor3D);

                    joinedRoom = null;
                    selectedListedRoom = null;
                    activeRoomId = string.Empty;
                    activeRoomName = string.Empty;
                    SetRoomListInteractable(true);
                    UpdateRoomDisplay();

                    if (refreshRoomListAfterLeaveRoom)
                    {
                        Log("Refreshing room list after leave ack. leftRoomId=" + SafeText(leftRoomIdFor3D));
                        bool refreshed = await ListRoomsAsync();
                        Log("Room list refresh after leave completed. refreshed=" + refreshed);
                    }
                }

                Log("Leave room ack result: " + ack);
                if (ack) ShowRealtimeWarningMessage("Left room. Select another room if needed.");
                else ShowRealtimeErrorMessage("Leave timeout.");

                UpdateConnectionButtons();
                UpdateCreateRoomButton();
                UpdateSendMessageButton();
                return ack;
            }
            finally
            {
                isLeaveRoomRunning = false;

                if (leaveAcknowledged)
                {
                    isUserRequestedExitFlow = false;
                    transportDropAlreadyHandled = false;
                    Log("Room-only exit completed. Realtime lobby monitoring remains active.");
                }

                SetRoomListInteractable(!isJoined);
                UpdateListRoomsButton();
                UpdateConnectionButtons();
                UpdateCreateRoomButton();
                UpdateSendMessageButton();
            }
        }

        private void CreateClientObjects()
        {
            CleanupClientObjectsOnly();

            int coreConnectTimeoutMs = disableCoreConnectTimeoutAfterConnect ? 0 : connectTimeoutMs;

            var config = new RealtimeConfig
            {
                serverUrl = activeServerUrl,
                transportKind = transportKind,
                connectTimeoutMs = coreConnectTimeoutMs,
                sendTimeoutMs = sendTimeoutMs,
                autoAuthenticateAfterConnect = false,
                logIncomingMessages = false,
                logOutgoingMessages = false
            };

            realtimeClient = new RealtimeClient(config);
            realtimeAuthClient = new RealtimeAuthClient(realtimeClient);
            realtimeLobbyClient = new RealtimeLobbyClient(realtimeClient);
            gameServerClient = new GameServerClient(realtimeClient);

            BindEvents();
            Log("Realtime clients created. coreConnectTimeoutMs=" + coreConnectTimeoutMs);
        }

        private void BindEvents()
        {
            if (eventsBound) return;
            eventsBound = true;

            realtimeClient.StateChanged += HandleStateChanged;
            realtimeClient.EnvelopeReceived += HandleEnvelopeReceived;
            realtimeClient.TransportErrorReceived += HandleTransportError;
            realtimeClient.Disconnected += HandleDisconnected;
            realtimeClient.ReliableLogReceived += HandleReliableLog;
            realtimeClient.ReliableAckTimeout += HandleReliableAckTimeout;

            realtimeAuthClient.Authenticated += HandleAuthenticated;
            realtimeAuthClient.AuthenticationFailed += HandleAuthenticationFailed;
            realtimeAuthClient.AuthLogReceived += HandleAuthLog;

            realtimeLobbyClient.LogReceived += HandleLobbyLog;
            realtimeLobbyClient.AckReceived += HandleLobbyAckReceived;
            realtimeLobbyClient.ErrorReceived += HandleLobbyError;
            realtimeLobbyClient.RoomCreatedReceived += HandleLobbyRoomCreated;
            realtimeLobbyClient.RoomUpdatedReceived += HandleLobbyRoomUpdated;
            realtimeLobbyClient.RoomClosedReceived += HandleLobbyRoomClosed;

            gameServerClient.Events.LogReceived += HandleGameLog;
            gameServerClient.Events.AckReceived += HandleGameAckReceived;
            gameServerClient.Events.ErrorReceived += HandleGameError;
            gameServerClient.Events.PlayerJoinedReceived += HandlePlayerJoinedReceived;
            gameServerClient.Events.PlayerLeftReceived += HandlePlayerLeftReceived;
        }

        private void UnbindEvents()
        {
            if (!eventsBound) return;
            eventsBound = false;

            if (realtimeClient != null)
            {
                realtimeClient.StateChanged -= HandleStateChanged;
                realtimeClient.EnvelopeReceived -= HandleEnvelopeReceived;
                realtimeClient.TransportErrorReceived -= HandleTransportError;
                realtimeClient.Disconnected -= HandleDisconnected;
                realtimeClient.ReliableLogReceived -= HandleReliableLog;
                realtimeClient.ReliableAckTimeout -= HandleReliableAckTimeout;
            }

            if (realtimeAuthClient != null)
            {
                realtimeAuthClient.Authenticated -= HandleAuthenticated;
                realtimeAuthClient.AuthenticationFailed -= HandleAuthenticationFailed;
                realtimeAuthClient.AuthLogReceived -= HandleAuthLog;
            }

            if (realtimeLobbyClient != null)
            {
                realtimeLobbyClient.LogReceived -= HandleLobbyLog;
                realtimeLobbyClient.AckReceived -= HandleLobbyAckReceived;
                realtimeLobbyClient.ErrorReceived -= HandleLobbyError;
                realtimeLobbyClient.RoomCreatedReceived -= HandleLobbyRoomCreated;
                realtimeLobbyClient.RoomUpdatedReceived -= HandleLobbyRoomUpdated;
                realtimeLobbyClient.RoomClosedReceived -= HandleLobbyRoomClosed;
            }

            if (gameServerClient != null)
            {
                gameServerClient.Events.LogReceived -= HandleGameLog;
                gameServerClient.Events.AckReceived -= HandleGameAckReceived;
                gameServerClient.Events.ErrorReceived -= HandleGameError;
                gameServerClient.Events.PlayerJoinedReceived -= HandlePlayerJoinedReceived;
                gameServerClient.Events.PlayerLeftReceived -= HandlePlayerLeftReceived;
            }
        }

        private RealtimeReliableSendOptions CreateReliableOptions()
        {
            return new RealtimeReliableSendOptions
            {
                ackTimeoutMs = reliableAckTimeoutMs,
                maxSendAttempts = 3,
                retryDelayMs = 300,
                retryOnAckTimeout = true,
                retryOnTransportSendFailed = true
            };
        }

        //* این تابع آدرس WebSocket ریل تایم را از کانفیگ مرکزی مخصوص WebGL می سازد.
        private string ResolveRealtimeServerUrl()
        {
            if (useServerConfigUrl)
            {
                if (forceDedicatedServerConfig)
                {
                    ServerConfig.UseDedicatedGrpcWeb();
                }

                ServerConfig.UseRealtimeWebSocketPath("/ws");
                return ServerConfig.RealtimeWebSocketUrl;
            }

            if (!string.IsNullOrWhiteSpace(serverUrl)) return serverUrl.Trim();
            return ServerConfig.RealtimeWebSocketUrl;
        }

        private void HandleStateChanged(RealtimeConnectionState state)
        {
            isConnected = realtimeClient != null && realtimeClient.IsConnected;
            if (isConnected) transportDropAlreadyHandled = false;
            Log("State changed: " + state);
            UpdateConnectionButtons();
            UpdateCreateRoomButton();
        }

        //* این تابع اِنولوپ های دریافتی را بین چت لابی و بخش سه بعدی تقسیم می کند.
        private void HandleEnvelopeReceived(RealtimeEnvelope envelope)
        {
            if (envelope == null) return;

            lastRealtimeInboundRealtimeSeconds = Time.realtimeSinceStartup;

            if (envelope.ch == RealtimeChannels.Game && envelope.t == RealtimeMessageTypes.PlayerAction)
            {
                HandleIncomingPlayerActionEnvelope(envelope);
                return;
            }

            if (IsRealtimeEnvelopeType(envelope, PresenceChannelName, PresencePlayerStateTypeName))
            {
                OnPlayerStateReceivedFor3D?.Invoke(envelope);
                return;
            }

            if (IsRealtimeEnvelopeType(envelope, PresenceChannelName, PresenceRoomMembersSnapshotTypeName))
            {
                OnRoomMembersSnapshotReceivedFor3D?.Invoke(envelope);
            }
        }

        //* این تابع کانال و تایپ اِنولوپ را با مقدار متنی بررسی می کند.
        private bool IsRealtimeEnvelopeType(RealtimeEnvelope envelope, string channel, string type)
        {
            if (envelope == null) return false;

            return string.Equals(envelope.ch, channel, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(envelope.t, type, StringComparison.OrdinalIgnoreCase);
        }

        private void HandleTransportError(string error)
        {
            Log("Transport error: " + error);

            if (ShouldTreatReasonAsActualInternetLost(error) ||
                IsRealtimeNetworkIssueReason(error) ||
                ShouldSuppressRealtimePopupBecauseInternetIsDown(error))
            {
                // نمایش پنل به مسیر واحد Disconnect/Reconnect سپرده می شود.
                // این Guard از نمایش دوباره پنل برای Error و سپس Disconnected جلوگیری می کند.
                Log(
                    "Transport error UI deferred to the single reconnect classification flow. reason=" +
                    SafeText(error)
                );

                UpdateConnectionButtons();
                UpdateSendMessageButton();
                return;
            }

            ShowRealtimeErrorMessage("Transport error: " + error);
            UpdateConnectionButtons();
            UpdateSendMessageButton();
        }

        private void HandleDisconnected(string reason)
        {
            StopKeepAliveLoop();

            bool userRequestedExit =
                isUserRequestedExitFlow ||
                isCleaningUp ||
                IsUserRequestedExitReason(reason);

            isConnected = false;
            isAuthenticated = false;
            isJoinRoomRunning = false;
            isLeaveRoomRunning = false;
            isSendMessageRunning = false;

            if (userRequestedExit)
            {
                isJoined = false;
                joinedRoom = null;
            }

            SetRoomListInteractable(false);
            SetListRoomsButtonInteractable(false);
            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();

            Log(
                "Disconnected: " +
                reason +
                " | userRequestedExit=" +
                userRequestedExit
            );

            if (userRequestedExit)
            {
                transportDropAlreadyHandled = true;
                ShowRealtimeWarningMessage(
                    "Realtime disconnected. You left all rooms."
                );

                CleanupSharedWorldAfterUserExit(
                    "manual_realtime_disconnect:" + SafeText(reason)
                );

                OnRealtimeDisconnectedFor3D?.Invoke(reason);
                return;
            }

            // پنل و Event قطع فقط در مسیر واحد Reconnect و بعد از نتیجه CheckNet اعمال می شوند.
            transportDropAlreadyHandled = true;
            StartRealtimeReconnectFlowAfterConnectionLoss(reason);
        }
        private void HandleReliableLog(string message)
        {
            Log("Reliable: " + message);
        }

        private void HandleReliableAckTimeout(string messageId)
        {
            Log("Reliable ack timeout: " + messageId);
        }

        private void HandleAuthenticated(string connectionId, string userId)
        {
            if (!isRealtimeReconnectRunning) StopRealtimeReconnectLoop("realtime_authenticated_event");
            StopPermanentReconnectFailureCleanupWatch("realtime_authenticated_event");
            permanentReconnectFailureCleanupApplied = false;

            //* یک اتصال تازه و سالم برقرار شد؛ نسل اتصال بالا می‌رود تا نتیجه‌ی پروب‌های قدیمی و دیرهنگام نادیده گرفته شود.
            connectionGenerationId++;
            immediateInternetLostHandled = false;
            internetLostPanelShownForCurrentOutage = false;

            isAuthenticated = true;
            currentRealtimeUserId = string.IsNullOrWhiteSpace(userId) ? currentRealtimeUserId : userId.Trim();
            UpdateCurrentUserIdentityFromStoredToken();
            Log("Authenticated. connectionId=" + connectionId + " userId=" + currentRealtimeUserId + " userName=" + currentRealtimeUserName);
            if (isRealtimeReconnectRunning) ReleaseRealtimeNetworkIssueUiLock("realtime_authenticated_during_reconnect");
            if (isJoined && !string.IsNullOrWhiteSpace(activeRoomId)) ActivateSharedWorldForRoomEntry("realtime_reauthenticated:" + SafeText(activeRoomId));
            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();
            CompleteBoolWaiter(authWaiter, true);
        }

        private void HandleAuthenticationFailed(RealtimeError error)
        {
            isAuthenticated = false;
            Log("Authentication failed: " + FormatError(error));
            ShowRealtimeErrorMessage("Realtime authentication failed: " + FormatError(error));
            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();
            CompleteBoolWaiter(authWaiter, false);
        }

        private void HandleAuthLog(string message)
        {
            Log("Auth: " + message);
        }

        private void HandleLobbyLog(string message)
        {
            Log("Lobby: " + message);
        }

        private void HandleLobbyAckReceived(RealtimeAck ack)
        {
            if (ack == null) return;
            Log("Lobby ack: " + ack.originalMessageId + " | status=" + ack.status);
        }

        private void HandleLobbyError(RealtimeError error)
        {
            Log("Lobby error: " + FormatError(error));
            ShowRealtimeErrorMessage("Lobby error: " + FormatError(error));
        }

        private void HandleLobbyRoomCreated(RealtimeRoomDto room)
        {
            if (room == null) return;
            Log("Lobby broadcast room_created: " + room.roomId);
        }

        private void HandleLobbyRoomUpdated(RealtimeRoomDto room)
        {
            if (room == null) return;

            Log("Lobby broadcast room_updated: " + room.roomId + " | online=" + room.onlineCount);
            ApplyRoomUpdateToCurrentRoom(room, "lobby_room_updated");
        }

        private void HandleLobbyRoomClosed(RealtimeRoomDto room)
        {
            if (room == null) return;
            Log("Lobby broadcast room_closed: " + room.roomId);
        }

        private void HandleGameLog(string message)
        {
            Log("Game: " + message);
        }

        private void HandleGameAckReceived(GameServerAckResult ack)
        {
            if (ack == null) return;

            string messageId = string.IsNullOrWhiteSpace(ack.originalMessageId)
                ? string.Empty
                : ack.originalMessageId.Trim();

            float now = Time.realtimeSinceStartup;
            bool duplicateAck =
                !string.IsNullOrWhiteSpace(messageId) &&
                string.Equals(messageId, lastHandledGameAckMessageId, StringComparison.Ordinal) &&
                lastHandledGameAckTime >= 0f &&
                now - lastHandledGameAckTime <= 0.5f;

            if (duplicateAck)
            {
                return;
            }

            lastHandledGameAckMessageId = messageId;
            lastHandledGameAckTime = now;

            Log("Game ack: " + messageId + " | processed=" + ack.IsProcessed());

            if (messageId.StartsWith("leave_room_", StringComparison.OrdinalIgnoreCase))
            {
                CompleteBoolWaiter(leaveAckWaiter, ack.IsProcessed());
            }
        }

        private void HandleGameError(RealtimeError error)
        {
            Log("Game error: " + FormatError(error));
            ShowRealtimeErrorMessage("Game error: " + FormatError(error));
        }

        private void HandlePlayerJoinedReceived(GameServerPresenceEvent presence)
        {
            if (presence == null) return;

            string playerId = ResolvePresencePlayerIdFor3D(presence);
            string displayName = ResolvePresenceDisplayName(presence, playerId);

            if (IsCurrentRealtimeUserPresence(playerId, displayName))
            {
                Log("Self player_joined ignored. playerId=" + SafeText(playerId) + " | displayName=" + SafeText(displayName));
                return;
            }

            OnPlayerJoinedFor3D?.Invoke(playerId, displayName);

            Log("Player joined: " + displayName);
            ShowRealtimeInfoMessage(displayName + " joined");
            ApplyRealtimePresenceOnlineCountTransition(
                playerId,
                true,
                "player_joined",
                displayName
            );
        }

        private void HandlePlayerLeftReceived(GameServerPresenceEvent presence)
        {
            if (presence == null) return;

            string playerId = ResolvePresencePlayerIdFor3D(presence);
            string displayName = ResolvePresenceDisplayName(presence, playerId);

            if (IsCurrentRealtimeUserPresence(playerId, displayName))
            {
                Log("Self player_left ignored. playerId=" + SafeText(playerId) + " | displayName=" + SafeText(displayName));
                return;
            }

            if (ShouldSuppressPlayerLeftUiBecauseDedicatedGameServerIsSourceOfTruth(playerId, displayName))
            {
                Log("Realtime player_left suppressed because Dedicated Game Server is source of truth. playerId=" + SafeText(playerId) + " | displayName=" + SafeText(displayName));
                return;
            }

            if (ShouldSuppressPlayerLeftUiBecauseRealtimeReconnect(playerId, displayName))
            {
                Log("Player left suppressed during reconnect grace. playerId=" + SafeText(playerId) + " | displayName=" + SafeText(displayName));
                return;
            }

            OnPlayerLeftFor3D?.Invoke(playerId, displayName);

            Log("Player left: " + displayName);
            ShowRealtimeWarningMessage(displayName + " left");
            ApplyRealtimePresenceOnlineCountTransition(
                playerId,
                false,
                "player_left",
                displayName
            );
        }

        private void HandleIncomingPlayerActionEnvelope(RealtimeEnvelope envelope)
        {
            string payload = envelope.payloadJson ?? string.Empty;
            if (!payload.Contains("\"kind\":\"chat\"")) return;
            if (!payload.Contains("\"actionType\":\"" + EscapeJson(chatActionType) + "\"")) return;

            string sender = ReadJsonString(payload, "senderLabel", "Remote");
            string text = ReadJsonString(payload, "text", payload);
            Log(sender + ": " + text);
            ShowRealtimeInfoMessage(sender + ": " + text);
        }

        //* این تابع آپدیت رسمی روم را روی روم انتخاب شده یا روم جوین شده اعمال می کند تا تعداد کاربران در تکست روم تازه شود.
        private void ApplyRoomUpdateToCurrentRoom(RealtimeRoomDto room, string source)
        {
            if (room == null) return;

            room.Normalize();
            if (!room.HasValidRoomId()) return;

            bool matchesActiveRoom = !string.IsNullOrWhiteSpace(activeRoomId) && IsSameText(room.roomId, activeRoomId);
            bool matchesJoinedRoom = joinedRoom != null && IsSameText(room.roomId, joinedRoom.roomId);
            bool matchesSelectedRoom = selectedListedRoom != null && IsSameText(room.roomId, selectedListedRoom.roomId);

            if (!matchesActiveRoom && !matchesJoinedRoom && !matchesSelectedRoom) return;

            if (matchesJoinedRoom || (isJoined && matchesActiveRoom))
            {
                joinedRoom = room;
                activeRoomId = room.roomId;
                activeRoomName = room.roomName;
                UpdateRoomDisplay(joinedRoom, true);
                Log("Room display updated from " + source + ". roomId=" + room.roomId + " | online=" + room.onlineCount);
                return;
            }

            selectedListedRoom = room;
            activeRoomId = room.roomId;
            activeRoomName = room.roomName;
            UpdateRoomDisplay(selectedListedRoom, false);
            Log("Selected room display updated from " + source + ". roomId=" + room.roomId + " | online=" + room.onlineCount);
        }

        //* این تابع وقتی فقط ایونت حضور داریم و آپدیت کامل روم نداریم، تعداد کاربران روم فعلی را کم یا زیاد می کند.
        private void ApplyPresenceOnlineCountDelta(int delta, string source, string displayName)
        {
            if (!isJoined || joinedRoom == null || string.IsNullOrWhiteSpace(activeRoomId)) return;
            if (!IsSameText(joinedRoom.roomId, activeRoomId)) return;

            if (ShouldIgnorePresenceOnlineCountDelta(source))
            {
                int maxPlayersForIgnoredDelta = Mathf.Max(1, joinedRoom.maxPlayers);
                Log(
                    "Room users delta ignored from " + source +
                    " because lobby_room_updated is authoritative. player=" +
                    displayName +
                    " | users=" +
                    joinedRoom.onlineCount +
                    "/" +
                    maxPlayersForIgnoredDelta
                );
                return;
            }

            int maxPlayersSafe = Mathf.Max(1, joinedRoom.maxPlayers);
            int minUsersSafe = isJoined ? 1 : 0;
            int currentOnlineCount = Mathf.Max(minUsersSafe, joinedRoom.onlineCount);

            joinedRoom.onlineCount = Mathf.Clamp(currentOnlineCount + delta, minUsersSafe, maxPlayersSafe);
            UpdateRoomDisplay(joinedRoom, true);
            Log("Room users updated from " + source + ". player=" + displayName + " | users=" + joinedRoom.onlineCount + "/" + maxPlayersSafe);
        }

        //* این تابع جلوی دوباره شمردن player_joined/player_left را می گیرد، چون عدد رسمی از lobby_room_updated می آید.
        private bool ShouldIgnorePresenceOnlineCountDelta(string source)
        {
            if (!useAuthoritativeLobbyRoomUpdatedForPresenceCount) return false;
            if (string.IsNullOrWhiteSpace(source)) return false;

            string safeSource = source.Trim();
            return
                IsSameText(safeSource, "player_joined") ||
                IsSameText(safeSource, "player_left");
        }

        //* این تابع وضعیت شناخته شده حضور را هنگام جوین تازه بازنشانی می کند تا ایونت های همان نشست فقط یک بار شمارش شوند.
        private void ResetRealtimePresenceCountTrackingForJoinedRoom()
        {
            realtimePresenceStateByPlayerId.Clear();

            if (!string.IsNullOrWhiteSpace(currentRealtimeUserId))
            {
                realtimePresenceStateByPlayerId[currentRealtimeUserId.Trim()] = true;
            }
        }

        //* این تابع فقط پیش از فعال شدن منبع authoritative ددیکیتد، تعداد را از transition واقعی حضور ریل تایم تازه می کند.
        private void ApplyRealtimePresenceOnlineCountTransition(
            string playerId,
            bool isPresent,
            string source,
            string displayName
        )
        {
            if (!isJoined || joinedRoom == null) return;
            if (string.IsNullOrWhiteSpace(playerId)) return;

            if (isRealtimeReconnectRunning ||
                dedicatedGameServerPresenceGuardActive ||
                IsDedicatedGameServerConnectedAndAuthenticated() ||
                Time.realtimeSinceStartup < suppressPlayerLeftUiUntil)
            {
                Log(
                    "Room users realtime presence transition ignored while reconnect/Dedicated owns count. source=" +
                    SafeText(source) +
                    " | player=" +
                    SafeText(displayName)
                );
                return;
            }

            string safePlayerId = playerId.Trim();
            if (realtimePresenceStateByPlayerId.TryGetValue(safePlayerId, out bool previousState) &&
                previousState == isPresent)
            {
                Log(
                    "Duplicate realtime presence transition ignored for room users. source=" +
                    SafeText(source) +
                    " | playerId=" +
                    SafeText(safePlayerId)
                );
                return;
            }

            realtimePresenceStateByPlayerId[safePlayerId] = isPresent;
            ApplyPresenceOnlineCountDelta(
                isPresent ? 1 : -1,
                source,
                displayName
            );
        }

        //* این تابع تعداد authoritative رجیستری Dedicated Server را بدون delta روی Users اعمال می کند.
        public void ApplyDedicatedAuthoritativeOnlineCount(int onlineCount, string source)
        {
            if (!isJoined || joinedRoom == null || string.IsNullOrWhiteSpace(activeRoomId)) return;
            if (!IsSameText(joinedRoom.roomId, activeRoomId)) return;

            int maxPlayersSafe = Mathf.Max(1, joinedRoom.maxPlayers);
            int safeOnlineCount = Mathf.Clamp(onlineCount, 1, maxPlayersSafe);

            if (joinedRoom.onlineCount == safeOnlineCount)
            {
                Log("Dedicated authoritative room users unchanged from " + source + ". users=" + joinedRoom.onlineCount + "/" + maxPlayersSafe);
                return;
            }

            joinedRoom.onlineCount = safeOnlineCount;
            UpdateRoomDisplay(joinedRoom, true);
            Log("Dedicated authoritative room users applied from " + source + ". users=" + joinedRoom.onlineCount + "/" + maxPlayersSafe);
        }


        //* این تابع برای حذف و ساخت کلون سه بعدی، آی دی یوزر را از ایونت حضور می خواند و روی کانکشن آی دی تکیه نمی کند.
        private string ResolvePresencePlayerIdFor3D(GameServerPresenceEvent presence)
        {
            if (presence == null) return string.Empty;

            string userId = ReadObjectStringMember(presence, "userId");
            if (!string.IsNullOrWhiteSpace(userId)) return userId.Trim();

            string playerId = ReadObjectStringMember(presence, "playerId");
            if (!string.IsNullOrWhiteSpace(playerId)) return playerId.Trim();

            string networkPlayerId = ReadObjectStringMember(presence, "networkPlayerId");
            if (!string.IsNullOrWhiteSpace(networkPlayerId)) return networkPlayerId.Trim();

            string id = ReadObjectStringMember(presence, "id");
            if (!string.IsNullOrWhiteSpace(id)) return id.Trim();

            string resolvedId = presence.ResolveNetworkPlayerId();
            return string.IsNullOrWhiteSpace(resolvedId) ? string.Empty : resolvedId.Trim();
        }

        //* این تابع نام نمایشی پلیر را از ایونت حضور می خواند و اگر نام نبود از آی دی استفاده می کند.
        private string ResolvePresenceDisplayName(GameServerPresenceEvent presence, string fallbackPlayerId)
        {
            if (presence == null) return string.IsNullOrWhiteSpace(fallbackPlayerId) ? "Player" : fallbackPlayerId;

            string userName = ReadObjectStringMember(presence, "userName");
            if (!string.IsNullOrWhiteSpace(userName)) return userName.Trim();

            string username = ReadObjectStringMember(presence, "username");
            if (!string.IsNullOrWhiteSpace(username)) return username.Trim();

            string playerName = ReadObjectStringMember(presence, "playerName");
            if (!string.IsNullOrWhiteSpace(playerName)) return playerName.Trim();

            string displayName = ReadObjectStringMember(presence, "displayName");
            if (!string.IsNullOrWhiteSpace(displayName)) return displayName.Trim();

            return string.IsNullOrWhiteSpace(fallbackPlayerId) ? "Player" : fallbackPlayerId;
        }

        //* این تابع نام ارسال کننده پیام لوکال را برای پِیلود چت و لاگ پیام می سازد.
        private string ResolveLocalChatSenderName()
        {
            if (!string.IsNullOrWhiteSpace(currentRealtimeUserName)) return currentRealtimeUserName.Trim();
            if (!string.IsNullOrWhiteSpace(clientLabel)) return clientLabel.Trim();
            if (!string.IsNullOrWhiteSpace(currentRealtimeUserId)) return currentRealtimeUserId.Trim();
            return "User";
        }

        //* این تابع با رفلکشن امن، مقدار رشته ای یک فیلد یا پراپرتی را از آبجکت می خواند.
        private static string ReadObjectStringMember(object target, string memberName)
        {
            if (target == null || string.IsNullOrWhiteSpace(memberName)) return string.Empty;

            Type type = target.GetType();

            var field = type.GetField(memberName);
            if (field != null)
            {
                object value = field.GetValue(target);
                return value == null ? string.Empty : value.ToString();
            }

            var property = type.GetProperty(memberName);
            if (property != null)
            {
                object value = property.GetValue(target, null);
                return value == null ? string.Empty : value.ToString();
            }

            return string.Empty;
        }

        private void UpdateCurrentUserIdentityFromStoredToken()
        {
            string token = SecureTokenStorage.GetAccessToken();
            UpdateCurrentUserIdentityFromAccessToken(token);
        }

        private void UpdateCurrentUserIdentityFromAccessToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return;

            string payloadJson = ReadJwtPayloadJson(token);
            if (string.IsNullOrWhiteSpace(payloadJson)) return;

            string tokenUserId = ExtractJsonStringValue(payloadJson, "sub");
            string tokenUserName = ExtractJsonStringValue(payloadJson, "userName");
            if (string.IsNullOrWhiteSpace(tokenUserName)) tokenUserName = ExtractJsonStringValue(payloadJson, "username");
            if (string.IsNullOrWhiteSpace(tokenUserName)) tokenUserName = ExtractJsonStringValue(payloadJson, "displayName");
            if (string.IsNullOrWhiteSpace(tokenUserName)) tokenUserName = ExtractJsonStringValue(payloadJson, "name");

            if (!string.IsNullOrWhiteSpace(tokenUserId)) currentRealtimeUserId = tokenUserId.Trim();
            if (!string.IsNullOrWhiteSpace(tokenUserName)) currentRealtimeUserName = tokenUserName.Trim();
        }

        private static string SafeText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        }

        private static string SafeTokenSource(string tokenSource)
        {
            return string.IsNullOrWhiteSpace(tokenSource)
                ? "unknown"
                : tokenSource.Trim();
        }

        private static string ReadJwtPayloadJson(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return string.Empty;

            string[] parts = token.Split('.');
            if (parts == null || parts.Length < 2) return string.Empty;

            return DecodeBase64UrlToString(parts[1]);
        }

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
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ExtractJsonStringValue(string json, string key)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key)) return string.Empty;

            string pattern = "\"" + key + "\"";
            int keyIndex = json.IndexOf(pattern, StringComparison.Ordinal);
            if (keyIndex < 0) return string.Empty;

            int colonIndex = json.IndexOf(':', keyIndex + pattern.Length);
            if (colonIndex < 0) return string.Empty;

            int valueStart = json.IndexOf('"', colonIndex + 1);
            if (valueStart < 0) return string.Empty;

            int valueEnd = valueStart + 1;
            bool escaped = false;

            while (valueEnd < json.Length)
            {
                char c = json[valueEnd];

                if (c == '\\' && !escaped)
                {
                    escaped = true;
                    valueEnd++;
                    continue;
                }

                if (c == '"' && !escaped) break;

                escaped = false;
                valueEnd++;
            }

            if (valueEnd >= json.Length) return string.Empty;
            return json.Substring(valueStart + 1, valueEnd - valueStart - 1);
        }

        private bool IsRealtimeReady()
        {
            return realtimeClient != null
                   && realtimeClient.IsConnected
                   && realtimeAuthClient != null
                   && realtimeAuthClient.IsAuthenticated;
        }

        //* این تابع از ساخته شدن خودکار اتصال توسط عملیات کمکی قبل از شروع صریح Realtime جلوگیری می کند.
        private async Task<bool> EnsureRealtimeReadyForUtilityMethodAsync(
            string actionName
        )
        {
            if (IsRealtimeReady()) return true;

            if (!preventAutoRealtimeConnectBeforeStartButton)
            {
                return await LoginCheckConnectAndAuthAsync();
            }

            string safeActionName = string.IsNullOrWhiteSpace(actionName)
                ? "Realtime action"
                : actionName.Trim();

            return Fail(
                safeActionName +
                " blocked. Click Connect/Auth or Server Start button first. Auto connection is disabled."
            );
        }

        private void BindMessageInputEvents()
        {
            if (messageInput == null) return;
            messageInput.onValueChanged.RemoveListener(HandleMessageInputChanged);
            messageInput.onValueChanged.AddListener(HandleMessageInputChanged);
        }

        private void UnbindMessageInputEvents()
        {
            if (messageInput == null) return;
            messageInput.onValueChanged.RemoveListener(HandleMessageInputChanged);
        }

        private void BindRoomNameInputEvents()
        {
            if (roomNameInput == null) return;
            roomNameInput.onValueChanged.RemoveListener(HandleRoomNameInputChanged);
            roomNameInput.onValueChanged.AddListener(HandleRoomNameInputChanged);
        }

        private void UnbindRoomNameInputEvents()
        {
            if (roomNameInput == null) return;
            roomNameInput.onValueChanged.RemoveListener(HandleRoomNameInputChanged);
        }

        private void HandleMessageInputChanged(string value)
        {
            SyncSendMessageButtonFromMessageInput(true);
        }

        private void HandleRoomNameInputChanged(string value)
        {
            Log("Room input changed. length=" + GetRoomNameInputLength() + " | valid=" + IsRoomNameInputValidForCreateRoom() + " | ready=" + IsRealtimeReady());
            SyncCreateRoomButtonFromRoomInput(true);
        }

        private void SyncCreateRoomButtonFromRoomInput(bool forceUpdate)
        {
            string currentRoomNameText = roomNameInput == null || roomNameInput.text == null ? string.Empty : roomNameInput.text;
            bool currentReady = IsRealtimeReady();
            bool changed = forceUpdate
                           || !string.Equals(lastRoomNameInputTextForButtonSync, currentRoomNameText, StringComparison.Ordinal)
                           || lastRealtimeReadyForCreateButtonSync != currentReady
                           || lastCreateRoomRunningForButtonSync != isCreateRoomRunning
                           || lastCleaningUpForButtonSync != isCleaningUp
                           || lastCurrentUserHasCreatedRoomForButtonSync != currentUserHasCreatedRoom
                           || lastCreateRoomAvailabilityCheckingForButtonSync != isCreateRoomAvailabilityChecking;

            if (!changed) return;

            lastRoomNameInputTextForButtonSync = currentRoomNameText;
            lastRealtimeReadyForCreateButtonSync = currentReady;
            lastCreateRoomRunningForButtonSync = isCreateRoomRunning;
            lastCleaningUpForButtonSync = isCleaningUp;
            lastCurrentUserHasCreatedRoomForButtonSync = currentUserHasCreatedRoom;
            lastCreateRoomAvailabilityCheckingForButtonSync = isCreateRoomAvailabilityChecking;

            UpdateCreateRoomButton();
        }

        //* این تابع وضعیت دکمه ارسال پیام را با متن اینپوت پیام و وضعیت روم همگام می کند.
        private void SyncSendMessageButtonFromMessageInput(bool forceUpdate)
        {
            string currentMessageText = GetMessageInputText();
            bool currentReady = IsRealtimeReady();
            bool changed = forceUpdate
                           || !string.Equals(lastMessageInputTextForButtonSync, currentMessageText, StringComparison.Ordinal)
                           || lastRealtimeReadyForSendButtonSync != currentReady
                           || lastJoinedForSendButtonSync != isJoined
                           || lastJoinRoomRunningForSendButtonSync != isJoinRoomRunning
                           || lastLeaveRoomRunningForSendButtonSync != isLeaveRoomRunning
                           || lastCleaningUpForSendButtonSync != isCleaningUp
                           || lastSendMessageRunningForButtonSync != isSendMessageRunning;

            if (!changed) return;

            lastMessageInputTextForButtonSync = currentMessageText;
            lastRealtimeReadyForSendButtonSync = currentReady;
            lastJoinedForSendButtonSync = isJoined;
            lastJoinRoomRunningForSendButtonSync = isJoinRoomRunning;
            lastLeaveRoomRunningForSendButtonSync = isLeaveRoomRunning;
            lastCleaningUpForSendButtonSync = isCleaningUp;
            lastSendMessageRunningForButtonSync = isSendMessageRunning;

            UpdateSendMessageButton();
        }

        //* این تابع متن خام اینپوت پیام را امن می خواند.
        private string GetMessageInputText()
        {
            return messageInput == null || messageInput.text == null ? string.Empty : messageInput.text;
        }

        //* این تابع طول متن پیام را بعد از حذف فاصله های ابتدا و انتها برمی گرداند.
        private int GetMessageInputLength()
        {
            return GetMessageInputText().Trim().Length;
        }

        private bool IsMessageInputValid()
        {
            return messageInput != null && GetMessageInputLength() > 0;
        }

        private bool IsRoomNameInputValidForCreateRoom()
        {
            if (roomNameInput == null) return false;
            string value = roomNameInput.text == null ? string.Empty : roomNameInput.text.Trim();
            return value.Length >= Mathf.Max(8, minimumRoomNameCharactersToEnableCreateButton);
        }

        private int GetRoomNameInputLength()
        {
            if (roomNameInput == null || roomNameInput.text == null) return 0;
            return roomNameInput.text.Trim().Length;
        }

        private void UpdateConnectionButtons()
        {
            bool ready = IsRealtimeReady();

            if (connectButton != null) connectButton.interactable = !isConnectAndAuthRunning && !isRealtimeReconnectRunning && !isCleaningUp && !ready;
            if (disconnectButton != null) disconnectButton.interactable = !isConnectAndAuthRunning && !isRealtimeReconnectRunning && !isCleaningUp && ready;
            UpdateListRoomsButton();
            UpdateLeaveRoomButton();
            UpdateCreateRoomButton();
        }

        //* این تابع وضعیت دکمه لیست روم را بدون دست زدن به آیتم های اسکرول روم فقط از وضعیت اتصال و جوین محاسبه می کند.
        private void UpdateListRoomsButton()
        {
            if (listRoomsButton == null) return;
            listRoomsButton.interactable = CanUseListRoomsButton();
        }

        //* این تابع مشخص می کند دکمه لیست روم در این لحظه اجازه فعال بودن دارد یا نه.
        private bool CanUseListRoomsButton()
        {
            return IsRealtimeReady()
                   && !isJoined
                   && !isJoinRoomRunning
                   && !isJoiningFromRoomList
                   && !isLeaveRoomRunning
                   && !isConnectAndAuthRunning
                   && !isRealtimeReconnectRunning
                   && !isCleaningUp;
        }

        //* این تابع دلیل فعال یا غیرفعال بودن دکمه لیست روم را برای لاگ می سازد.
        private string BuildListRoomsButtonStateReason()
        {
            string buttonState = listRoomsButton == null ? "button=missing | " : "button=assigned | ";
            if (!IsRealtimeReady()) return buttonState + "reason=realtime_not_ready | connected=" + isConnected + " | authenticated=" + isAuthenticated;
            if (isJoined) return buttonState + "reason=user_inside_room | roomId=" + activeRoomId;
            if (isJoinRoomRunning) return buttonState + "reason=join_running";
            if (isJoiningFromRoomList) return buttonState + "reason=joining_from_room_list";
            if (isLeaveRoomRunning) return buttonState + "reason=leave_running";
            if (isConnectAndAuthRunning) return buttonState + "reason=connect_auth_running";
            if (isRealtimeReconnectRunning) return buttonState + "reason=realtime_reconnect_running";
            if (isCleaningUp) return buttonState + "reason=cleanup_running";
            return buttonState + "reason=ready_to_list_rooms";
        }

        //* این تابع وضعیت فعال بودن دکمه خروج از روم را فقط از وضعیت واقعی اتصال و جوین محاسبه می کند.
        private void UpdateLeaveRoomButton()
        {
            bool canLeave = CanUseLeaveRoomButton();
            string reason = BuildLeaveRoomButtonStateReason();

            if (leaveRoomButton != null) leaveRoomButton.interactable = canLeave;

            if (hasLeaveRoomButtonState && lastLeaveRoomButtonInteractable == canLeave && string.Equals(lastLeaveRoomButtonStateReason, reason, StringComparison.Ordinal)) return;

            hasLeaveRoomButtonState = true;
            lastLeaveRoomButtonInteractable = canLeave;
            lastLeaveRoomButtonStateReason = reason;
            Log("Leave room button state: interactable=" + canLeave + " | " + reason);
        }

        //* این تابع مشخص می کند دکمه خروج از روم در این لحظه اجازه فعال بودن دارد یا نه.
        private bool CanUseLeaveRoomButton()
        {
            return IsRealtimeReady()
                   && isJoined
                   && !string.IsNullOrWhiteSpace(activeRoomId)
                   && !isJoinRoomRunning
                   && !isJoiningFromRoomList
                   && !isLeaveRoomRunning
                   && !isRealtimeReconnectRunning
                   && !isCleaningUp;
        }

        //* این تابع دلیل فعال یا غیرفعال بودن دکمه خروج از روم را برای لاگ و دیباگ می سازد.
        private string BuildLeaveRoomButtonStateReason()
        {
            string buttonState = leaveRoomButton == null ? "button=missing | " : "button=assigned | ";
            if (!IsRealtimeReady()) return buttonState + "reason=realtime_not_ready | connected=" + isConnected + " | authenticated=" + isAuthenticated;
            if (!isJoined) return buttonState + "reason=user_not_joined";
            if (string.IsNullOrWhiteSpace(activeRoomId)) return buttonState + "reason=active_room_id_empty";
            if (isJoinRoomRunning) return buttonState + "reason=join_running";
            if (isJoiningFromRoomList) return buttonState + "reason=joining_from_room_list";
            if (isLeaveRoomRunning) return buttonState + "reason=leave_running";
            if (isRealtimeReconnectRunning) return buttonState + "reason=realtime_reconnect_running";
            if (isCleaningUp) return buttonState + "reason=cleanup_running";
            return buttonState + "reason=user_joined | roomId=" + activeRoomId;
        }

        private void UpdateCreateRoomButton()
        {
            bool ready = IsRealtimeReady();
            bool validRoomName = IsRoomNameInputValidForCreateRoom();
            bool hasIdentity = HasCurrentUserIdentityForCreateRoomCheck();
            bool canCreate = ready
                             && validRoomName
                             && hasIdentity
                             && !currentUserHasCreatedRoom
                             && !isCreateRoomAvailabilityChecking
                             && !isCreateRoomRunning
                             && !isCleaningUp;

            string reason = BuildCreateRoomButtonStateReason(ready, validRoomName, hasIdentity);

            if (createRoomButton != null) createRoomButton.interactable = canCreate;

            if (hasCreateRoomButtonState && lastCreateRoomButtonInteractable == canCreate && string.Equals(lastCreateRoomButtonStateReason, reason, StringComparison.Ordinal)) return;

            hasCreateRoomButtonState = true;
            lastCreateRoomButtonInteractable = canCreate;
            lastCreateRoomButtonStateReason = reason;
            Log("Create room button state: interactable=" + canCreate + " | " + reason);
        }

        private string BuildCreateRoomButtonStateReason(bool ready, bool validRoomName, bool hasIdentity)
        {
            if (createRoomButton == null) return "createButton=missing | ready=" + ready + " | roomNameInput=" + (roomNameInput == null ? "missing" : "ok") + " | roomNameLength=" + GetRoomNameInputLength();
            if (roomNameInput == null) return "reason=room_name_input_not_connected | ready=" + ready + " | roomNameLength=0";
            if (!ready) return "reason=realtime_not_ready | connected=" + isConnected + " | authenticated=" + isAuthenticated + " | clientConnected=" + (realtimeClient != null && realtimeClient.IsConnected) + " | authClientAuthenticated=" + (realtimeAuthClient != null && realtimeAuthClient.IsAuthenticated) + " | roomNameLength=" + GetRoomNameInputLength();
            if (!hasIdentity) return "reason=current_user_identity_missing | userId=" + currentRealtimeUserId + " | userName=" + currentRealtimeUserName + " | roomNameLength=" + GetRoomNameInputLength();
            if (!validRoomName) return "reason=room_name_too_short | roomNameLength=" + GetRoomNameInputLength() + " | min=" + Mathf.Max(8, minimumRoomNameCharactersToEnableCreateButton);
            if (currentUserHasCreatedRoom) return "reason=user_already_created_room | roomId=" + currentUserCreatedRoomId + " | userId=" + currentRealtimeUserId + " | userName=" + currentRealtimeUserName;
            if (isCreateRoomAvailabilityChecking) return "reason=checking_existing_created_room | roomNameLength=" + GetRoomNameInputLength();
            if (isCreateRoomRunning) return "reason=create_room_running | roomNameLength=" + GetRoomNameInputLength();
            if (isCleaningUp) return "reason=cleanup_running | roomNameLength=" + GetRoomNameInputLength();
            return "reason=ready_to_create | roomNameLength=" + GetRoomNameInputLength() + " | userId=" + currentRealtimeUserId + " | userName=" + currentRealtimeUserName;
        }

        //* این تابع دکمه ارسال پیام را فقط وقتی فعال می کند که یوزر داخل روم باشد و متن پیام خالی نباشد.
        private void UpdateSendMessageButton()
        {
            bool canSend = CanUseSendMessageButton();
            string reason = BuildSendMessageButtonStateReason();

            if (sendMessageButton != null) sendMessageButton.interactable = canSend;

            if (hasSendMessageButtonState && lastSendMessageButtonInteractable == canSend && string.Equals(lastSendMessageButtonStateReason, reason, StringComparison.Ordinal)) return;

            hasSendMessageButtonState = true;
            lastSendMessageButtonInteractable = canSend;
            lastSendMessageButtonStateReason = reason;
            Log("Send message button state: interactable=" + canSend + " | " + reason);
        }

        //* این تابع مشخص می کند دکمه ارسال پیام در این لحظه اجازه فعال بودن دارد یا نه.
        private bool CanUseSendMessageButton()
        {
            bool messageRulePassed = !disableSendButtonWhenMessageInputEmpty || IsMessageInputValid();
            return IsRealtimeReady()
                   && isJoined
                   && messageRulePassed
                   && !isJoinRoomRunning
                   && !isJoiningFromRoomList
                   && !isLeaveRoomRunning
                   && !isSendMessageRunning
                   && !isCleaningUp;
        }

        //* این تابع دلیل فعال یا غیرفعال بودن دکمه ارسال پیام را برای لاگ و دیباگ می سازد.
        private string BuildSendMessageButtonStateReason()
        {
            string buttonState = sendMessageButton == null ? "button=missing | " : "button=assigned | ";
            if (!IsRealtimeReady()) return buttonState + "reason=realtime_not_ready | connected=" + isConnected + " | authenticated=" + isAuthenticated;
            if (!isJoined) return buttonState + "reason=user_not_joined | roomId=" + activeRoomId;
            if (disableSendButtonWhenMessageInputEmpty && !IsMessageInputValid()) return buttonState + "reason=message_empty | messageLength=" + GetMessageInputLength();
            if (isJoinRoomRunning) return buttonState + "reason=join_running";
            if (isJoiningFromRoomList) return buttonState + "reason=joining_from_room_list";
            if (isLeaveRoomRunning) return buttonState + "reason=leave_running";
            if (isSendMessageRunning) return buttonState + "reason=send_running";
            if (isCleaningUp) return buttonState + "reason=cleanup_running";
            return buttonState + "reason=ready_to_send | roomId=" + activeRoomId + " | messageLength=" + GetMessageInputLength();
        }

        private void DetectRealtimeConnectionDrop()
        {
            if (!monitorRealtimeConnectionDropInUpdate) return;
            if (isCleaningUp || isUserRequestedExitFlow) return;
            if (isRealtimeReconnectRunning) return;
            if (!isConnected && !isAuthenticated && !isJoined) return;

            bool clientConnected = realtimeClient != null && realtimeClient.IsConnected;
            if (clientConnected)
            {
                transportDropAlreadyHandled = false;
                return;
            }

            MarkRealtimeDisconnectedByTransport("Realtime transport drop detected by controller monitor.");
        }

        private void MarkRealtimeDisconnectedByTransport(string reason)
        {
            if (isCleaningUp ||
                isUserRequestedExitFlow ||
                IsUserRequestedExitReason(reason))
            {
                Log(
                    "Realtime transport drop ignored because user exit or cleanup is active. reason=" +
                    SafeText(reason)
                );

                return;
            }

            if (transportDropAlreadyHandled || isRealtimeReconnectRunning)
            {
                Log(
                    "Realtime transport drop ignored because reconnect is already handled/running. reason=" +
                    SafeText(reason)
                );

                return;
            }

            transportDropAlreadyHandled = true;
            StopKeepAliveLoop();

            Log(
                reason +
                " | connected=" +
                isConnected +
                " | authenticated=" +
                isAuthenticated +
                " | joined=" +
                isJoined +
                " | clientConnected=" +
                (realtimeClient != null && realtimeClient.IsConnected) +
                " | authClientAuthenticated=" +
                (realtimeAuthClient != null &&
                 realtimeAuthClient.IsAuthenticated)
            );

            isConnected = false;
            isAuthenticated = false;

            SetRoomListInteractable(false);
            SetListRoomsButtonInteractable(false);
            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();

            // مسیر Reconnect مالک یکتای تشخیص نوع قطعی و نمایش پنل است.
            StartRealtimeReconnectFlowAfterConnectionLoss(reason);
        }

        private void StartRealtimeReconnectFlowAfterConnectionLoss(string reason)
        {
            if (isUserRequestedExitFlow ||
                isCleaningUp ||
                IsUserRequestedExitReason(reason))
            {
                return;
            }

            if (!enableAutomaticRealtimeReconnect)
            {
                OnRealtimeConnectionLostForReconnectFor3D?.Invoke(reason);

                if (ShouldTreatReasonAsActualInternetLost(reason))
                {
                    ShowServerDebugPanelForInternetLost(reason);
                }
                else
                {
                    ShowServerDebugPanelForRealtimeTransportDrop(reason);
                }

                StartPermanentReconnectFailureCleanupWatch(reason);
                return;
            }

            // در حالت Auto Reconnect، خود حلقه بعد از CheckNet فقط یک بار
            // نوع قطعی را مشخص می کند و سپس Event و پنل مناسب را اعمال می کند.
            StartRealtimeReconnectLoop(reason);
        }

        //* این تابع حلقه واقعی ریکانکت را شروع می کند و فقط تایمر خاموشی نیست.
        private void StartRealtimeReconnectLoop(string reason)
        {
            if (isRealtimeReconnectRunning)
            {
                Log(
                    "Realtime reconnect loop already running. reason=" +
                    SafeText(reason)
                );

                return;
            }

            StopPermanentReconnectFailureCleanupWatch(
                "automatic_reconnect_loop_started"
            );

            string targetRoomId = string.IsNullOrWhiteSpace(activeRoomId)
                ? string.Empty
                : activeRoomId.Trim();

            string targetRoomName = string.IsNullOrWhiteSpace(activeRoomName)
                ? string.Empty
                : activeRoomName.Trim();

            bool shouldRejoinRoom =
                rejoinLastRoomAfterRealtimeReconnect &&
                !string.IsNullOrWhiteSpace(targetRoomId) &&
                (isJoined ||
                 joinedRoom != null ||
                 selectedListedRoom != null);

            realtimeReconnectCts?.Cancel();
            realtimeReconnectCts?.Dispose();
            realtimeReconnectCts = new CancellationTokenSource();

            isRealtimeReconnectRunning = true;
            realtimeReconnectAttemptCount = 0;
            permanentReconnectFailureCleanupApplied = false;

            Log(
                "Realtime reconnect loop started. CheckNet owns outage classification before UI and transport creation. reason=" +
                SafeText(reason) +
                " | roomId=" +
                SafeText(targetRoomId) +
                " | rejoin=" +
                shouldRejoinRoom
            );

            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();

            _ = RunRealtimeReconnectLoopAsync(
                reason,
                targetRoomId,
                targetRoomName,
                shouldRejoinRoom,
                realtimeReconnectCts.Token
            );
        }

        //* این تابع حلقه واقعی ریکانکت را اجرا می کند و اگر تا زمان مشخص موفق نشود، نتیجه نهایی شکست را نشان می دهد.
        //* این تابع حلقه واقعی ریکانکت را اجرا می کند و اگر تا زمان مشخص موفق نشود، نتیجه نهایی شکست را نشان می دهد.
        //* نتیجه موفق CheckNet همین دور به Attempt منتقل می شود تا Preflight دوباره همان درخواست را تکرار نکند.
        //* این تابع حلقه واقعی ریکانکت را اجرا می کند و نتیجه موفق CheckNet همان دور را به Attempt بعدی منتقل می کند.
        //* این تابع حلقه واقعی Reconnect را اجرا می کند.
        //* CheckNet در این حلقه مالک یکتا است و تا قبل از تایید دسترسی سرور، Attempt ساخته نمی شود.
        private async Task RunRealtimeReconnectLoopAsync(
            string reason,
            string targetRoomId,
            string targetRoomName,
            bool shouldRejoinRoom,
            CancellationToken cancellationToken
        )
        {
            float timeoutSeconds = GetPermanentReconnectFailureTimeoutSeconds();
            float startedAt = Time.realtimeSinceStartup;
            float nextDelaySeconds = 0f;
            string safeReason = SafeText(reason);

            bool connectionLossEventRaised = false;
            bool reconnectStartUiShown = false;
            bool webGlOutageDelayCompletionLogged = false;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (isCleaningUp || isUserRequestedExitFlow)
                    {
                        Log(
                            "Realtime reconnect loop stopped because cleanup/user exit started. reason=" +
                            safeReason
                        );

                        return;
                    }

                    if (Time.realtimeSinceStartup - startedAt >= timeoutSeconds)
                    {
                        break;
                    }

                    if (nextDelaySeconds > 0f)
                    {
                        int delayMs = Mathf.RoundToInt(
                            nextDelaySeconds * 1000f
                        );

                        ShowServerDebugPanelForRealtimeProgress(
                            (string.IsNullOrWhiteSpace(
                                realtimeReconnectAttemptMessage
                            )
                                ? "در حال تلاش برای اتصال دوباره..."
                                : realtimeReconnectAttemptMessage) +
                            " تلاش بعدی تا " +
                            nextDelaySeconds.ToString("F0") +
                            " ثانیه دیگر.",
                            "REALTIME_RECONNECT_WAIT",
                            "Reason=" +
                            safeReason +
                            " | nextDelaySeconds=" +
                            nextDelaySeconds.ToString("F1"),
                            true
                        );

                        await WebGLCheckNetFastWrapper.DelayRealtimeAsync(
                            delayMs,
                            cancellationToken
                        );
                        nextDelaySeconds = 0f;
                    }

                    bool serverReachabilityAlreadyConfirmed = false;
                    bool checkNetEnabled =
                        enableCheckNetFastReconnectWatch &&
                        AuthManager.Instance != null;

                    if (checkNetEnabled)
                    {
                        // اگر Watch قبلی هنوز در حال پایان است، حلقه درخواست همزمان جدید نمی سازد.
                        while (checkNetFastWatchRunning &&
                               !cancellationToken.IsCancellationRequested)
                        {
                            await WebGLCheckNetFastWrapper.DelayRealtimeAsync(
                                25,
                                cancellationToken
                            );
                        }

                        float probeIntervalSeconds = Mathf.Clamp(
                            checkNetFastWatchIntervalSeconds,
                            0.5f,
                            1.5f
                        );

                        bool localNetworkUnavailable =
                            IsLocalNetworkUnavailableFast();

                        bool knownOutageStillInsideInterval =
                            checkNetFastOutageActive &&
                            Time.realtimeSinceStartup <
                            nextCheckNetFastWatchAt;

                        bool serverReachable = false;

                        if (checkNetFastReconnectKickRequested)
                        {
                            checkNetFastReconnectKickRequested = false;
                            serverReachable = true;
                        }
                        else if (localNetworkUnavailable ||
                                 knownOutageStillInsideInterval)
                        {
                            serverReachable = false;

                            if (localNetworkUnavailable &&
                                nextCheckNetFastWatchAt <=
                                Time.realtimeSinceStartup)
                            {
                                nextCheckNetFastWatchAt =
                                    Time.realtimeSinceStartup +
                                    probeIntervalSeconds;
                            }
                        }
                        else
                        {
                            serverReachable =
                                await WebGLCheckNetFastWrapper.CheckNetFastSilentAsync(
                                    AuthManager.Instance,
                                    checkNetFastTimeoutMs,
                                    cancellationToken
                                );

                            nextCheckNetFastWatchAt =
                                Time.realtimeSinceStartup +
                                probeIntervalSeconds;
                        }

                        if (!serverReachable)
                        {
                            checkNetFastOutageActive = true;
                            immediateInternetLostHandled = true;

                            checkNetFastConsecutiveFailures = Mathf.Max(
                                checkNetFastConsecutiveFailures,
                                Mathf.Max(
                                    1,
                                    checkNetFastFailuresBeforeDisconnect
                                )
                            );

                            if (!connectionLossEventRaised)
                            {
                                connectionLossEventRaised = true;

                                OnRealtimeConnectionLostForReconnectFor3D?.Invoke(
                                    "checknet_fast_server_unreachable"
                                );
                            }

                            ShowServerDebugPanelForInternetLost(
                                "checknet_fast_server_unreachable"
                            );

                            float waitSeconds = Mathf.Max(
                                0.05f,
                                nextCheckNetFastWatchAt -
                                Time.realtimeSinceStartup
                            );

                            await WebGLCheckNetFastWrapper.DelayRealtimeAsync(
                                Mathf.Max(
                                    1,
                                    Mathf.RoundToInt(
                                        waitSeconds * 1000f
                                    )
                                ),
                                cancellationToken
                            );

                            if (!webGlOutageDelayCompletionLogged)
                            {
                                webGlOutageDelayCompletionLogged = true;
                                Log(
                                    "Reconnect outage delay completed. Network state will be checked again."
                                );
                            }

                            continue;
                        }

                        bool recoveredFromOutage =
                            checkNetFastOutageActive ||
                            immediateInternetLostHandled;

                        checkNetFastOutageActive = false;
                        checkNetFastConsecutiveFailures = 0;
                        immediateInternetLostHandled = false;
                        serverReachabilityAlreadyConfirmed = true;

                        if (recoveredFromOutage)
                        {
                            ReleaseRealtimeNetworkIssueUiLock(
                                "checknet_fast_recovered_before_reconnect_attempt"
                            );

                            ShowServerDebugPanelForRealtimeProgress(
                                "اتصال شبکه برگشت. در حال اتصال دوباره به Realtime...",
                                "REALTIME_RECONNECT_NETWORK_RECOVERED",
                                "Reason=" +
                                safeReason +
                                " | roomId=" +
                                SafeText(targetRoomId) +
                                " | rejoin=" +
                                shouldRejoinRoom,
                                true
                            );

                            reconnectStartUiShown = true;

                            Log(
                                "CheckNet fast recovered inside reconnect loop. Reconnect attempt starts now. reason=" +
                                safeReason
                            );
                        }
                    }

                    if (!connectionLossEventRaised)
                    {
                        connectionLossEventRaised = true;
                        OnRealtimeConnectionLostForReconnectFor3D?.Invoke(
                            reason
                        );
                    }

                    if (!reconnectStartUiShown)
                    {
                        reconnectStartUiShown = true;

                        ShowServerDebugPanelForRealtimeProgress(
                            GetRealtimeReconnectStartMessageForReason(reason),
                            "REALTIME_RECONNECT_STARTED",
                            "Reason=" +
                            safeReason +
                            " | roomId=" +
                            SafeText(targetRoomId) +
                            " | rejoin=" +
                            shouldRejoinRoom,
                            true
                        );
                    }

                    realtimeReconnectAttemptCount++;

                    ShowServerDebugPanelForRealtimeProgress(
                        (string.IsNullOrWhiteSpace(
                            realtimeReconnectAttemptMessage
                        )
                            ? "در حال تلاش برای اتصال دوباره..."
                            : realtimeReconnectAttemptMessage) +
                        " تلاش " +
                        realtimeReconnectAttemptCount,
                        "REALTIME_RECONNECT_ATTEMPT_" +
                        realtimeReconnectAttemptCount,
                        "Reason=" +
                        safeReason +
                        " | roomId=" +
                        SafeText(targetRoomId) +
                        " | elapsedSeconds=" +
                        (Time.realtimeSinceStartup - startedAt).ToString("F1"),
                        true
                    );

                    bool reconnected = await TryRealtimeReconnectOnceAsync(
                        targetRoomId,
                        targetRoomName,
                        shouldRejoinRoom,
                        realtimeReconnectAttemptCount,
                        serverReachabilityAlreadyConfirmed,
                        cancellationToken
                    );

                    if (reconnected)
                    {
                        CompleteRealtimeReconnectLoop(
                            targetRoomId,
                            shouldRejoinRoom,
                            realtimeReconnectAttemptCount
                        );

                        return;
                    }

                    nextDelaySeconds = CalculateNextRealtimeReconnectDelay(
                        nextDelaySeconds
                    );
                }

                Log(
                    "Realtime reconnect loop timeout reached. timeoutSeconds=" +
                    timeoutSeconds.ToString("F1") +
                    " | reason=" +
                    safeReason
                );

                isRealtimeReconnectRunning = false;

                ShowRealtimeReconnectFinalFailurePanel(
                    "automatic_reconnect_loop_timeout:" + safeReason,
                    "REALTIME_RECONNECT_TIMEOUT_KEEP_RETRYING",
                    realtimeReconnectAttemptCount
                );

                StartRealtimeReconnectLoop(
                    "automatic_reconnect_loop_timeout_keep_retrying:" +
                    safeReason
                );
            }
            catch (OperationCanceledException)
            {
                Log(
                    "Realtime reconnect loop cancelled. reason=" +
                    safeReason
                );
            }
            catch (ObjectDisposedException)
            {
                Log(
                    "Realtime reconnect loop stopped because client objects were disposed. reason=" +
                    safeReason
                );
            }
            catch (Exception ex)
            {
                Log(
                    "Realtime reconnect loop exception: " +
                    ex.Message +
                    " | reason=" +
                    safeReason
                );

                isRealtimeReconnectRunning = false;

                ShowRealtimeReconnectFinalFailurePanel(
                    "automatic_reconnect_loop_exception:" +
                    safeReason +
                    " | " +
                    ex.Message,
                    "REALTIME_RECONNECT_EXCEPTION_KEEP_RETRYING",
                    realtimeReconnectAttemptCount
                );

                StartRealtimeReconnectLoop(
                    "automatic_reconnect_loop_exception_keep_retrying:" +
                    safeReason
                );
            }
            finally
            {
                if (!IsRealtimeReady())
                {
                    if (realtimeReconnectCts == null ||
                        realtimeReconnectCts.IsCancellationRequested)
                    {
                        isRealtimeReconnectRunning = false;
                    }

                    UpdateConnectionButtons();
                    UpdateCreateRoomButton();
                    UpdateSendMessageButton();
                }
            }
        }
        //* این تابع یک تلاش کامل برای کانکت، آث و جوین دوباره همان روم انجام می دهد.
        //* اگر حلقه Reconnect در همین دور دسترسی سرور را تایید کرده باشد، CheckNet دوم اجرا نمی شود.
        //* این تابع یک تلاش کامل برای Connect، Auth و Join دوباره همان Room انجام می دهد.
        //* اگر حلقه Reconnect دسترسی سرور را تایید کرده باشد، CheckNet دوم اجرا نمی شود.
        private async Task<bool> TryRealtimeReconnectOnceAsync(
            string targetRoomId,
            string targetRoomName,
            bool shouldRejoinRoom,
            int attempt,
            bool serverReachabilityAlreadyConfirmed,
            CancellationToken cancellationToken
        )
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            EnsureLifecycleToken();

            if (serverReachabilityAlreadyConfirmed)
            {
                Log(
                    "Realtime reconnect preflight reused CheckNet success from current reconnect loop. attempt=" +
                    attempt
                );
            }
            else if (enableCheckNetFastReconnectWatch &&
                     AuthManager.Instance != null)
            {
                bool serverReachable;

                try
                {
                    serverReachable =
                        await WebGLCheckNetFastWrapper.CheckNetFastSilentAsync(
                            AuthManager.Instance,
                            checkNetFastTimeoutMs,
                            cancellationToken
                        );
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                float probeIntervalSeconds = Mathf.Clamp(
                    checkNetFastWatchIntervalSeconds,
                    0.5f,
                    1.5f
                );

                nextCheckNetFastWatchAt =
                    Time.realtimeSinceStartup + probeIntervalSeconds;

                if (!serverReachable)
                {
                    checkNetFastOutageActive = true;

                    checkNetFastConsecutiveFailures = Mathf.Max(
                        checkNetFastConsecutiveFailures,
                        Mathf.Max(
                            1,
                            checkNetFastFailuresBeforeDisconnect
                        )
                    );

                    Log(
                        "Realtime reconnect attempt blocked before transport creation because CheckNet is unreachable. attempt=" +
                        attempt +
                        " | timeoutMs=" +
                        checkNetFastTimeoutMs
                    );

                    return false;
                }

                checkNetFastOutageActive = false;
                checkNetFastConsecutiveFailures = 0;

                Log(
                    "Realtime reconnect preflight CheckNet passed. attempt=" +
                    attempt +
                    " | timeoutMs=" +
                    checkNetFastTimeoutMs
                );
            }

            CleanupTransportObjectsForRealtimeReconnect(
                "reconnect_attempt_" + attempt
            );

            if (!string.IsNullOrWhiteSpace(targetRoomId))
            {
                activeRoomId = targetRoomId.Trim();
            }

            if (!string.IsNullOrWhiteSpace(targetRoomName))
            {
                activeRoomName = targetRoomName.Trim();
            }

            CreateClientObjects();

            bool connected = await ConnectAsync();
            if (!connected)
            {
                Log(
                    "Realtime reconnect attempt failed at connect. attempt=" +
                    attempt
                );

                return false;
            }

            string refreshedAccessToken =
                await EnsureFreshAccessTokenBeforeRealtimeAuthAsync(
                    SecureTokenStorage.GetAccessToken(),
                    "reconnect_attempt_" + attempt
                );

            if (string.IsNullOrWhiteSpace(refreshedAccessToken))
            {
                Log(
                    "Realtime reconnect attempt failed because access token is empty after refresh gate. attempt=" +
                    attempt
                );

                return false;
            }

            UpdateCurrentUserIdentityFromStoredToken();

            bool authenticated = await AuthenticateWithStoredTokenAsync();
            if (!authenticated)
            {
                Log(
                    "Realtime reconnect attempt failed at auth. attempt=" +
                    attempt
                );

                return false;
            }

            if (!shouldRejoinRoom)
            {
                isJoined = false;
                StartKeepAliveLoop();

                Log(
                    "Realtime reconnect succeeded without room rejoin. attempt=" +
                    attempt
                );

                return true;
            }

            if (string.IsNullOrWhiteSpace(targetRoomId))
            {
                Log(
                    "Realtime reconnect cannot rejoin because target room id is empty. attempt=" +
                    attempt
                );

                return false;
            }

            ShowServerDebugPanelForRealtimeProgress(
                GetRealtimeReconnectPrepareGameServerMessage(),
                "REALTIME_RECONNECT_PREPARE_GAME_SERVER",
                "roomId=" +
                SafeText(targetRoomId) +
                " | attempt=" +
                attempt,
                true
            );

            RealtimeReliableSendResult joinResult =
                await gameServerClient.JoinRoomReliableAsync(
                    targetRoomId,
                    CreateReliableOptions(),
                    lifecycleCts.Token
                );

            bool joined = joinResult != null && joinResult.isSuccess;

            if (!joined)
            {
                Log(
                    "Realtime reconnect rejoin failed. attempt=" +
                    attempt +
                    " | room=" +
                    targetRoomId +
                    " | error=" +
                    (joinResult == null
                        ? "null"
                        : joinResult.errorMessage)
                );

                isJoined = false;
                return false;
            }

            isJoined = true;
            activeRoomId = targetRoomId;
            activeRoomName = targetRoomName;
            manualExitWorldCleanupApplied = false;
            isUserRequestedExitFlow = false;

            UpdateRoomDisplay();
            SetRoomListInteractable(false);
            SetListRoomsButtonInteractable(false);
            StartKeepAliveLoop();

            Log(
                "Realtime reconnect rejoin succeeded. attempt=" +
                attempt +
                " | room=" +
                targetRoomId
            );

            OnRoomJoinedFor3D?.Invoke(targetRoomId);
            return true;
        }

        //* این تابع نتیجه موفق ریکانکت را ثبت می کند.
        //* این تابع نتیجه موفق ریکانکت را ثبت می کند.
        //* این تابع نتیجه موفق ریکانکت را ثبت می کند و اجرای CheckNet بعدی را تا یک بازه کامل عقب می اندازد.
        private void CompleteRealtimeReconnectLoop(
            string targetRoomId,
            bool rejoinedRoom,
            int attempt
        )
        {
            isRealtimeReconnectRunning = false;
            transportDropAlreadyHandled = false;
            permanentReconnectFailureCleanupApplied = false;

            float nextProbeDelaySeconds = Mathf.Clamp(
                checkNetFastWatchIntervalSeconds,
                0.5f,
                3f
            );

            nextCheckNetFastWatchAt =
                Time.realtimeSinceStartup + nextProbeDelaySeconds;

            ReleaseRealtimeNetworkIssueUiLock("realtime_reconnect_loop_success");
            StopPermanentReconnectFailureCleanupWatch(
                "realtime_reconnect_loop_success"
            );

            if (rejoinedRoom &&
                dedicatedGameServerPresenceGuardActive &&
                !IsDedicatedGameServerConnectedAndAuthenticated())
            {
                dedicatedRecoveryFreshRealtimeDelivered = true;
                Log(
                    "Fresh Realtime recovery delivered for current Dedicated disconnect. " +
                    "Waiting for Dedicated binder without starting a second Realtime reconnect."
                );
            }

            if (!rejoinedRoom)
            {
                string successMessage =
                    string.IsNullOrWhiteSpace(realtimeReconnectSuccessMessage)
                        ? "اتصال دوباره به بلادرنگ انجام شد."
                        : realtimeReconnectSuccessMessage.Trim();

                ShowServerDebugPanelForRealtimeProgress(
                    successMessage,
                    "REALTIME_RECONNECT_SUCCESS",
                    "roomId=" +
                    SafeText(targetRoomId) +
                    " | rejoined=" +
                    rejoinedRoom +
                    " | attempts=" +
                    attempt,
                    false
                );

                ShowRealtimeSuccessMessage("Realtime reconnected.");
            }
            else
            {
                Log(
                    "Realtime reconnect room restore completed. Game Server final panel message is owned by Dedicated binder. roomId=" +
                    SafeText(targetRoomId) +
                    " | attempts=" +
                    attempt
                );
            }

            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();

            if (!rejoinedRoom &&
                refreshRoomListAfterRealtimeReconnectWithoutRejoin)
            {
                _ = RefreshRoomListAfterRealtimeReconnectAsync(attempt);
            }
        }

        //* این تابع بعد از ریکانکت موفق بدون جوین روم، لیست روم را دوباره از سرور می گیرد تا لابی خالی نماند.
        private async Task RefreshRoomListAfterRealtimeReconnectAsync(int attempt)
        {
            await Task.Yield();

            if (!refreshRoomListAfterRealtimeReconnectWithoutRejoin) return;

            if (!IsRealtimeReady())
            {
                Log("Reconnect room list refresh skipped because realtime is not ready. attempt=" + attempt);
                ShowServerDebugPanelForRealtimeProgress(
                    "اتصال Realtime برگشت، اما لیست روم‌ها هنوز قابل دریافت نیست.",
                    "REALTIME_RECONNECT_LIST_ROOMS_SKIPPED",
                    "Reason=realtime_not_ready | attempt=" + attempt,
                    false
                );
                return;
            }

            if (isJoined)
            {
                Log("Reconnect room list refresh skipped because user is already inside a room. attempt=" + attempt + " | roomId=" + SafeText(activeRoomId));
                HideServerDebugPanelAfterRealtimeConnectSuccess();
                return;
            }

            if (realtimeLobbyClient == null)
            {
                Log("Reconnect room list refresh skipped because lobby client is null. attempt=" + attempt);
                ShowServerDebugPanelForRealtimeProgress(
                    "اتصال Realtime برگشت، اما کلاینت لابی آماده نیست.",
                    "REALTIME_RECONNECT_LIST_ROOMS_SKIPPED",
                    "Reason=lobby_client_null | attempt=" + attempt,
                    false
                );
                return;
            }

            try
            {
                ShowServerDebugPanelForRealtimeProgress(
                    realtimeRoomSyncMessage,
                    "REALTIME_RECONNECT_LIST_ROOMS",
                    "Refreshing lobby rooms after reconnect. attempt=" + attempt,
                    true
                );

                RealtimeLobbyListRoomsResult result = await realtimeLobbyClient.ListRoomsAsync(
                    CreateReliableOptions(),
                    lifecycleCts.Token
                );

                if (result == null)
                {
                    Log("Reconnect room list refresh failed. result=null | attempt=" + attempt);
                    UpdateListRoomsButton();

                    ShowServerDebugPanelForRealtimeProgress(
                        "اتصال Realtime برگشت، اما دریافت لیست روم‌ها ناموفق بود.",
                        "REALTIME_RECONNECT_LIST_ROOMS_FAILED",
                        "Reason=result_null | attempt=" + attempt,
                        false
                    );

                    return;
                }

                if (!result.isSuccess)
                {
                    Log("Reconnect room list refresh failed. error=" + SafeText(result.errorMessage) + " | attempt=" + attempt);
                    UpdateListRoomsButton();

                    ShowServerDebugPanelForRealtimeProgress(
                        "اتصال Realtime برگشت، اما دریافت لیست روم‌ها ناموفق بود.",
                        "REALTIME_RECONNECT_LIST_ROOMS_FAILED",
                        "Reason=" + SafeText(result.errorMessage) + " | attempt=" + attempt,
                        false
                    );

                    return;
                }

                lastListedRooms = result.Rooms ?? Array.Empty<RealtimeRoomDto>();
                RenderRooms(lastListedRooms);
                RenderRoomListButtons(lastListedRooms);
                SetListRoomsButtonInteractable(!isJoined);
                UpdateCreateRoomButton();
                UpdateSendMessageButton();

                ShowRealtimeInfoMessage("Rooms refreshed after reconnect. Count: " + result.Count);
                Log("Reconnect room list refresh completed. count=" + result.Count + " | attempt=" + attempt);

                ShowServerDebugPanelForRealtimeProgress(
                    "اتصال Realtime برگشت و لیست روم‌ها به‌روزرسانی شد.",
                    "REALTIME_RECONNECT_LIST_ROOMS_SUCCESS",
                    "count=" + result.Count + " | attempt=" + attempt,
                    false
                );

                HideServerDebugPanelAfterRealtimeConnectSuccess();
            }
            catch (Exception ex)
            {
                Log("Reconnect room list refresh exception: " + ex.Message + " | attempt=" + attempt);
                UpdateListRoomsButton();

                ShowServerDebugPanelForRealtimeProgress(
                    "اتصال Realtime برگشت، اما دریافت لیست روم‌ها خطا داد.",
                    "REALTIME_RECONNECT_LIST_ROOMS_EXCEPTION",
                    "Exception=" + SafeText(ex.Message) + " | attempt=" + attempt,
                    false
                );
            }
        }
        //* این تابع منابع ترنسپورت قبلی را برای ساخت استریم جدید پاک می کند اما کانتکست روم را نگه می دارد.
        private void CleanupTransportObjectsForRealtimeReconnect(string reason)
        {
            StopKeepAliveLoop();
            UnbindEvents();

            try
            {
                gameServerClient?.Dispose();
                realtimeLobbyClient?.Dispose();
                realtimeAuthClient?.Dispose();
                realtimeClient?.Dispose();
            }
            catch (Exception ex)
            {
                Log("Reconnect transport cleanup warning: " + ex.Message + " | reason=" + SafeText(reason));
            }

            gameServerClient = null;
            realtimeLobbyClient = null;
            realtimeAuthClient = null;
            realtimeClient = null;

            authWaiter = null;
            leaveAckWaiter = null;

            isConnected = false;
            isAuthenticated = false;
            isConnectAndAuthRunning = false;
            isCreateRoomRunning = false;
            isJoinRoomRunning = false;
            isLeaveRoomRunning = false;
            isSendMessageRunning = false;
            transportDropAlreadyHandled = true;

            lifecycleCts?.Dispose();
            lifecycleCts = null;
        }

        private float CalculateNextRealtimeReconnectDelay(float currentDelaySeconds)
        {
            float maxDelay = Mathf.Max(1f, realtimeReconnectMaxDelaySeconds);
            if (currentDelaySeconds <= 0f) return Mathf.Min(1f, maxDelay);
            return Mathf.Min(currentDelaySeconds * 2f, maxDelay);
        }

        private void StopRealtimeReconnectLoop(string reason)
        {
            if (!isRealtimeReconnectRunning && realtimeReconnectCts == null) return;

            try
            {
                realtimeReconnectCts?.Cancel();
            }
            catch
            {
                // لغو ریکانکت فقط برای توقف تسک پس زمینه است.
            }

            realtimeReconnectCts?.Dispose();
            realtimeReconnectCts = null;
            isRealtimeReconnectRunning = false;
            realtimeReconnectAttemptCount = 0;
            Log("Realtime reconnect loop stopped. reason=" + SafeText(reason));
        }

        private void StartPermanentReconnectFailureCleanupWatch(string reason)
        {
            if (!cleanupSharedWorldAfterPermanentReconnectFailure) return;
            if (isUserRequestedExitFlow || isCleaningUp || IsUserRequestedExitReason(reason)) return;

            StopPermanentReconnectFailureCleanupWatch("restart_reconnect_failure_watch");

            activePermanentReconnectFailureReason = SafeText(reason);
            permanentReconnectFailureCleanupCoroutine = StartCoroutine(PermanentReconnectFailureCleanupRoutine(activePermanentReconnectFailureReason));
            Log("Permanent reconnect failure watch started. timeoutSeconds=" + GetPermanentReconnectFailureTimeoutSeconds().ToString("F1") + " | reason=" + activePermanentReconnectFailureReason);
        }

        private void StopPermanentReconnectFailureCleanupWatch(string reason)
        {
            if (permanentReconnectFailureCleanupCoroutine == null) return;

            StopCoroutine(permanentReconnectFailureCleanupCoroutine);
            permanentReconnectFailureCleanupCoroutine = null;
            activePermanentReconnectFailureReason = string.Empty;
            Log("Permanent reconnect failure watch stopped. reason=" + SafeText(reason));
        }

        private IEnumerator PermanentReconnectFailureCleanupRoutine(string reason)
        {
            float timeoutSeconds = GetPermanentReconnectFailureTimeoutSeconds();
            float startTime = Time.realtimeSinceStartup;

            while (Time.realtimeSinceStartup - startTime < timeoutSeconds)
            {
                if (ShouldCancelPermanentReconnectFailureCleanupWatch())
                {
                    permanentReconnectFailureCleanupCoroutine = null;
                    activePermanentReconnectFailureReason = string.Empty;
                    yield break;
                }

                yield return null;
            }

            permanentReconnectFailureCleanupCoroutine = null;
            ShowRealtimeReconnectFinalFailurePanel(
                "permanent_reconnect_failure_watch_timeout:" + SafeText(reason),
                "REALTIME_RECONNECT_WATCH_TIMEOUT_KEEP_RETRYING",
                realtimeReconnectAttemptCount
            );
            Log("Permanent reconnect failure watch timeout reached without forced exit. reason=" + SafeText(reason));
        }

        private float GetPermanentReconnectFailureTimeoutSeconds()
        {
            return Mathf.Clamp(permanentReconnectFailureTimeoutSeconds, 5f, 1800f);
        }

        private bool ShouldCancelPermanentReconnectFailureCleanupWatch()
        {
            if (isCleaningUp || isUserRequestedExitFlow) return true;
            if (IsRealtimeReady()) return true;
            if (realtimeClient != null && realtimeClient.IsConnected && isConnected && isAuthenticated) return true;
            return false;
        }

        //* این تابع بعد از شکست قطعی یا لغو ریکانکت، خروج محلی امن از روم را انجام می دهد.
        private void ForceLocalExitAfterPermanentReconnectFailure(string reason)
        {
            StopRealtimeReconnectLoop("permanent_reconnect_failure_local_exit");
            if (!cleanupSharedWorldAfterPermanentReconnectFailure) return;
            if (permanentReconnectFailureCleanupApplied) return;

            permanentReconnectFailureCleanupApplied = true;
            string safeReason = "permanent_reconnect_failure:" + SafeText(reason);

            Log("Permanent reconnect failure reached. Running local forced exit. reason=" + safeReason);

            StopKeepAliveLoop();
            CleanupClientObjectsOnly();

            isConnected = false;
            isAuthenticated = false;
            isJoined = false;
            isConnectAndAuthRunning = false;
            isCreateRoomRunning = false;
            isJoinRoomRunning = false;
            isLeaveRoomRunning = false;
            isSendMessageRunning = false;
            isRealtimeReconnectRunning = false;
            transportDropAlreadyHandled = true;
            isUserRequestedExitFlow = false;

            SetRoomListInteractable(false);
            SetListRoomsButtonInteractable(false);
            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();

            CleanupSharedWorldAfterUserExit(safeReason);

            OnRealtimeReconnectFailedPermanentlyFor3D?.Invoke(safeReason);
            if (invokeDisconnectedFor3DAfterPermanentReconnectFailure) OnRealtimeDisconnectedFor3D?.Invoke(safeReason);
        }

        private bool IsUserRequestedExitReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;

            string value = reason.Trim().ToLowerInvariant();
            return value.Contains("manual")
                   || value.Contains("user_exit")
                   || value.Contains("leave_room")
                   || value.Contains("client disconnect")
                   || value.Contains("client disposed")
                   || value.Contains("manual_game_server_disconnect");
        }

        private void ActivateSharedWorldForRoomEntry(string reason)
        {
            StopPermanentReconnectFailureCleanupWatch("shared_world_entry:" + SafeText(reason));
            manualExitWorldCleanupApplied = false;
            permanentReconnectFailureCleanupApplied = false;

            string safeReason = SafeText(reason);

            if (!allowSharedWorldRootActivationFromRealtimeRoom)
            {
                Log("Shared world root activation skipped on realtime room entry. Dedicated game server must activate it. reason=" + safeReason);
                return;
            }

            if (!activateSharedWorldRootOnRoomEntry) return;

            if (sharedWorld3DRoot == null)
            {
                Log("Shared world root activation skipped. Reference is missing. reason=" + safeReason);
                return;
            }

            if (!sharedWorld3DRoot.activeSelf)
            {
                sharedWorld3DRoot.SetActive(true);
                Log("Shared world root activated for realtime room entry. reason=" + safeReason);
                return;
            }

            Log("Shared world root already active for realtime room entry. reason=" + safeReason);
        }

        private void CleanupSharedWorldAfterUserExit(string reason)
        {
            if (!cleanupSharedWorldOnlyOnUserExit) return;
            if (manualExitWorldCleanupApplied) return;

            manualExitWorldCleanupApplied = true;
            string safeReason = SafeText(reason);

            DetachMainCameraBeforeRuntimeCloneCleanup(safeReason);

            if (destroyRuntimeCloneRootChildrenOnUserExit)
            {
                DestroyRuntimeCloneRootChildren(safeReason);
            }

            if (disableSharedWorldRootOnUserExit && sharedWorld3DRoot != null)
            {
                sharedWorld3DRoot.SetActive(false);
                Log("Shared world root disabled after user exit. reason=" + safeReason);
            }

            OnManualWorldCleanupFor3D?.Invoke(safeReason);
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

            Log("Main camera detached before runtime clone cleanup. reason=" + reason);
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

        private void DestroyRuntimeCloneRootChildren(string reason)
        {
            if (runtimeCloneRoots == null || runtimeCloneRoots.Length == 0) return;

            int destroyedCount = 0;

            for (int rootIndex = 0; rootIndex < runtimeCloneRoots.Length; rootIndex++)
            {
                Transform root = runtimeCloneRoots[rootIndex];
                if (root == null) continue;

                for (int childIndex = root.childCount - 1; childIndex >= 0; childIndex--)
                {
                    Transform child = root.GetChild(childIndex);
                    if (child == null) continue;

                    Destroy(child.gameObject);
                    destroyedCount++;
                }
            }

            if (destroyedCount > 0)
            {
                Log("Runtime clone children destroyed after user exit. count=" + destroyedCount + " | reason=" + reason);
            }
        }

        private void StartKeepAliveLoop()
        {
            if (!enableTestKeepAlive)
            {
                enableTestKeepAlive = true;
                Log("KeepAlive forced on. Realtime WebSocket must not stay idle.");
            }

            if (!IsRealtimeReady()) return;

            StopKeepAliveLoop();
            keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(lifecycleCts.Token);
            _ = RunKeepAliveLoopAsync(keepAliveCts.Token);
            Log("KeepAlive started. intervalMs=" + keepAliveIntervalMs);
        }

        private async Task RunKeepAliveLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(Mathf.Max(1000, keepAliveIntervalMs), cancellationToken);

                    if (realtimeClient == null || !realtimeClient.IsConnected)
                    {
                        MarkRealtimeDisconnectedByTransport("KeepAlive detected realtime client disconnected.");
                        continue;
                    }

                    using (CancellationTokenSource pingCts = CreateLinkedTimeoutToken(Mathf.Max(1000, keepAlivePingTimeoutMs)))
                    {
                        bool sent = await realtimeClient.SendPingAsync(pingCts.Token);
                        if (!sent)
                        {
                            MarkRealtimeDisconnectedByTransport("KeepAlive ping failed.");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log("KeepAlive warning: " + ex.Message);
                }
            }
        }

        private void StopKeepAliveLoop()
        {
            if (keepAliveCts == null) return;
            keepAliveCts.Cancel();
            keepAliveCts.Dispose();
            keepAliveCts = null;
        }

        //* این تابع منابع تست جی‌آر‌پی‌سی را هنگام حذف آبجکت بدون دیسکانکت شبکه ای آزاد می کند تا ادیتور قفل نشود.
        private void ReleaseForDestroyWithoutNetworkAwait()
        {
            StopRealtimeReconnectLoop("object_destroy_release");
            StopKeepAliveLoop();
            ClearRoomListButtons();
            UnbindEvents();
            UnbindMessageInputEvents();
            UnbindRoomNameInputEvents();

            try
            {
                lifecycleCts?.Cancel();
            }
            catch
            {
                // لغو توکن فقط برای توقف سریع Taskها است و خطای آن مهم نیست.
            }

            try
            {
                gameServerClient?.Dispose();
                realtimeLobbyClient?.Dispose();
                realtimeAuthClient?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[G7-WebSocket-RoomLobby] Lightweight destroy dispose warning: " + ex.Message);
            }

            gameServerClient = null;
            realtimeLobbyClient = null;
            realtimeAuthClient = null;
            realtimeClient = null;

            authWaiter = null;
            leaveAckWaiter = null;

            isConnected = false;
            isAuthenticated = false;
            isJoined = false;
            isConnectAndAuthRunning = false;
            isCreateRoomRunning = false;
            isJoinRoomRunning = false;
            isLeaveRoomRunning = false;
            transportDropAlreadyHandled = true;

            lifecycleCts?.Dispose();
            lifecycleCts = null;
        }

        private async Task CleanupAsync(string reason, bool objectDestroy = false, bool userRequestedExit = false)
        {
            if (isCleaningUp) return;

            isCleaningUp = true;

            if (userRequestedExit)
            {
                isUserRequestedExitFlow = true;
                transportDropAlreadyHandled = true;
                isRealtimeReconnectRunning = false;
            }

            StopRealtimeReconnectLoop("cleanup_started:" + SafeText(reason));
            StopPermanentReconnectFailureCleanupWatch("cleanup_started:" + SafeText(reason));

            Log("Cleanup started: " + reason + " | objectDestroy=" + objectDestroy + " | userRequestedExit=" + userRequestedExit);
            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();

            try
            {
                StopKeepAliveLoop();

                try
                {
                    if (gameServerClient != null && isJoined && !string.IsNullOrWhiteSpace(activeRoomId))
                    {
                        Log("Cleanup leave room started. room=" + activeRoomId);
                        await gameServerClient.LeaveRoomAsync(activeRoomId, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    Log("Leave cleanup warning: " + ex.Message);
                }

                try
                {
                    if (realtimeClient != null)
                    {
                        Log("Cleanup disconnect started. reason=" + reason);
                        await realtimeClient.DisconnectAsync(reason, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    Log("Disconnect cleanup warning: " + ex.Message);
                }

                CleanupClientObjectsOnly(!userRequestedExit);

                if (userRequestedExit && !objectDestroy)
                {
                    CleanupSharedWorldAfterUserExit("manual_disconnect:" + SafeText(reason));
                }

                isConnected = false;
                isAuthenticated = false;
                isJoined = false;
                isConnectAndAuthRunning = false;
                isCreateRoomRunning = false;
                isJoinRoomRunning = false;
                isLeaveRoomRunning = false;
                isSendMessageRunning = false;
                isRealtimeReconnectRunning = false;
                transportDropAlreadyHandled = true;

                joinedRoom = null;
                selectedListedRoom = null;
                activeRoomId = string.Empty;
                activeRoomName = string.Empty;

                SetRoomListInteractable(false);
                SetListRoomsButtonInteractable(false);
                UpdateRoomDisplay();

                if (!objectDestroy)
                {
                    if (userRequestedExit)
                    {
                        ShowServerDebugPanelForManualRealtimeDisconnectSuccess(reason);
                    }
                    else
                    {
                        ShowRealtimeWarningMessage("Disconnected. You left all rooms.");
                    }
                }
            }
            finally
            {
                isCleaningUp = false;

                if (userRequestedExit)
                {
                    isUserRequestedExitFlow = false;
                }

                Log("Cleanup completed: " + reason);
                UpdateConnectionButtons();
                UpdateCreateRoomButton();
                UpdateSendMessageButton();
            }
        }

        private void CleanupClientObjectsOnly(bool clearRoomList = true)
        {
            StopKeepAliveLoop();

            if (clearRoomList)
            {
                ClearRoomListButtons();
                lastListedRooms = Array.Empty<RealtimeRoomDto>();
                lastCreatedRoomId = string.Empty;
            }
            else
            {
                SetRoomListInteractable(false);
            }

            selectedListedRoom = null;
            joinedRoom = null;
            isJoiningFromRoomList = false;
            isJoinRoomRunning = false;
            isLeaveRoomRunning = false;
            isCreateRoomRunning = false;

            UnbindEvents();

            gameServerClient?.Dispose();
            realtimeLobbyClient?.Dispose();
            realtimeAuthClient?.Dispose();
            realtimeClient?.Dispose();

            gameServerClient = null;
            realtimeLobbyClient = null;
            realtimeAuthClient = null;
            realtimeClient = null;

            authWaiter = null;
            leaveAckWaiter = null;

            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();
        }
        private bool EnsureReadyForRoomMessage()
        {
            if (!IsRealtimeReady()) return Fail("Client is not connected/authenticated.");
            if (!isJoined || gameServerClient == null || !gameServerClient.HasRoom) return Fail("Client is not joined to a room.");
            return true;
        }

        private RealtimeRoomDto FindFirstJoinableListedRoom()
        {
            if (lastListedRooms == null || lastListedRooms.Length == 0) return null;

            RealtimeRoomDto createdRoom = FindLastListedRoom(lastCreatedRoomId);
            if (createdRoom != null)
            {
                createdRoom.Normalize();
                if (createdRoom.CanJoin()) return createdRoom;
                if (createdRoom.HasValidRoomId() && !createdRoom.IsClosed() && !createdRoom.IsFull()) return createdRoom;
            }

            for (int i = 0; i < lastListedRooms.Length; i++)
            {
                RealtimeRoomDto room = lastListedRooms[i];
                if (room == null) continue;
                room.Normalize();
                if (room.CanJoin()) return room;
            }

            for (int i = 0; i < lastListedRooms.Length; i++)
            {
                RealtimeRoomDto room = lastListedRooms[i];
                if (room == null) continue;
                room.Normalize();
                if (room.HasValidRoomId() && !room.IsClosed() && !room.IsFull()) return room;
            }

            return null;
        }

        private string BuildRoomName()
        {
            if (roomNameInput != null && !string.IsNullOrWhiteSpace(roomNameInput.text))
            {
                return roomNameInput.text.Trim();
            }

            string prefix = string.IsNullOrWhiteSpace(roomNamePrefix) ? "WebGL G7 Lobby Room" : roomNamePrefix.Trim();
            return prefix + " " + DateTime.Now.ToString("HHmmss");
        }

        private string BuildChatPayload(string text)
        {
            return "{"
                   + "\"kind\":\"chat\","
                   + "\"actionType\":\"" + EscapeJson(chatActionType) + "\","
                   + "\"senderLabel\":\"" + EscapeJson(ResolveLocalChatSenderName()) + "\","
                   + "\"roomId\":\"" + EscapeJson(activeRoomId) + "\","
                   + "\"text\":\"" + EscapeJson(text) + "\","
                   + "\"ts\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                   + "}";
        }
        private void RenderRooms(RealtimeRoomDto[] rooms)
        {
            int count = rooms == null ? 0 : rooms.Length;
            Log("Rooms data received. count=" + count);
        }

        private void UpdateRoomDisplay()
        {
            if (joinedRoom != null)
            {
                UpdateRoomDisplay(joinedRoom, isJoined);
                return;
            }

            if (selectedListedRoom != null)
            {
                UpdateRoomDisplay(selectedListedRoom, false);
                return;
            }

            string roomNameText = string.IsNullOrWhiteSpace(activeRoomName) ? "-" : activeRoomName;
            QueueRoomText("Room: " + roomNameText + "\nOwner: -\nUsers: -");
        }

        private void EnsureLifecycleToken()
        {
            if (lifecycleCts != null && !lifecycleCts.IsCancellationRequested) return;

            lifecycleCts?.Dispose();
            lifecycleCts = new CancellationTokenSource();
        }

        private CancellationTokenSource CreateLinkedTimeoutToken(int timeoutMs)
        {
            EnsureLifecycleToken();
            CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(lifecycleCts.Token);
            if (timeoutMs > 0) cts.CancelAfter(timeoutMs);
            return cts;
        }

        private static TaskCompletionSource<bool> CreateBoolWaiter()
        {
            return new TaskCompletionSource<bool>();
        }

        private async Task<bool> WaitForBoolAsync(TaskCompletionSource<bool> waiter, int timeoutMs, CancellationToken cancellationToken)
        {
            if (waiter == null) return false;

            Task timeoutTask = Task.Delay(Mathf.Max(500, timeoutMs), cancellationToken);
            Task completed = await Task.WhenAny(waiter.Task, timeoutTask);
            if (completed != waiter.Task) return false;

            return waiter.Task.Result;
        }

        private static void CompleteBoolWaiter(TaskCompletionSource<bool> waiter, bool value)
        {
            if (waiter == null || waiter.Task.IsCompleted) return;
            waiter.TrySetResult(value);
        }

        private static string ReadJsonString(string json, string key, string fallback)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key)) return fallback;

            string pattern = "\"" + key + "\":\"";
            int start = json.IndexOf(pattern, StringComparison.Ordinal);
            if (start < 0) return fallback;

            start += pattern.Length;
            StringBuilder value = new StringBuilder();
            bool escaped = false;

            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];

                if (escaped)
                {
                    if (c == 'n') value.Append('\n');
                    else if (c == 'r') value.Append('\r');
                    else if (c == 't') value.Append('\t');
                    else value.Append(c);

                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '"') break;
                value.Append(c);
            }

            return value.Length == 0 ? fallback : value.ToString();
        }

        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }

        private static string FormatError(RealtimeError error)
        {
            return error == null ? "unknown" : error.code + " | " + error.message;
        }

        private bool Fail(string message)
        {
            Log("FAILED: " + message);
            ShowRealtimeErrorMessage(message);
            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();
            return false;
        }

        private void AutoResolveServerDebugReferences(string source)
        {
            if (!autoFindServerDebugPanelByName) return;

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

            BindServerDebugButtonHandlers(source);

            if (pnlServerDebug != null || txtServerDebugMessage != null)
            {
                Log("Server debug UI refs resolved | source=" + SafeText(source) + " | panel=" + (pnlServerDebug != null) + " | title=" + (txtServerDebugTitle != null) + " | message=" + (txtServerDebugMessage != null) + " | technical=" + (txtServerDebugTechnical != null));
            }
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
        //* این تابع عنوان پنل دیباگ را بر اساس مرحله فعلی انتخاب می کند.
        private string GetServerDebugProgressTitleForStage(string stage)
        {
            string safeStage = SafeText(stage);
            lastRealtimeServerDebugStage = safeStage;
            string upperStage = safeStage.ToUpperInvariant();

            if (IsRealtimeInternetLostStage(upperStage)) return FixedInternetLostDebugTitle;

            bool isGameServerStage =
                upperStage.Contains("GAME_SERVER") ||
                upperStage.Contains("DEDICATED");

            if (isGameServerStage)
            {
                return string.IsNullOrWhiteSpace(gameServerReconnectProgressTitle)
                    ? "اتصال به گیم سرور"
                    : gameServerReconnectProgressTitle.Trim();
            }

            return string.IsNullOrWhiteSpace(realtimeConnectProgressTitle)
                ? "اتصال به Realtime"
                : realtimeConnectProgressTitle.Trim();
        }

        //* این تابع مشخص می کند آیا مرحله فعلی فقط اعلام قطع اینترنت است یا نه.
        private static bool IsRealtimeInternetLostStage(string upperStage)
        {
            if (string.IsNullOrWhiteSpace(upperStage)) return false;
            return upperStage.Contains("INTERNET_CONNECTION_LOST")
                   || upperStage.Contains("NETWORK_LOST")
                   || upperStage.Contains("LOCAL_INTERNET_NOT_REACHABLE");
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

        //* این تابع برای قطع اینترنت، متن نهایی کاربر را مستقل از مقدار ذخیره شده در اینسپکتور برمی گرداند.
        private static string NormalizeInternetLostMessageForUi(string message)
        {
            return IsLegacyInternetLostReconnectMessage(message) ? FixedInternetLostUserMessage : message;
        }
        //* این تابع پیام پیشرفت اتصال را روی پنل دیباگ سرور نشان می دهد.
        private void ShowServerDebugPanelForRealtimeProgress(string message, string stage, string technicalDetails, bool isRunning)
        {
            if (!openServerDebugPanelOnRealtimeConnectProgress) return;

            AutoResolveServerDebugReferences("realtime_progress:" + stage);

            string safeStage = SafeText(stage);
            string upperStage = safeStage.ToUpperInvariant();
            if (ShouldSuppressRealtimeProgressUiForNetworkIssue(upperStage))
            {
                if (keepInternetLostStatusWhileNetworkIssueUiLocked) SetStatus(GetRealtimeInternetLostImmediateMessage());
                Log("Realtime progress UI suppressed while network issue is active | stage=" + safeStage);
                return;
            }

            bool shouldKeepTransportDropPanelStable =
                isRealtimeReconnectRunning &&
                IsHighFrequencyAutomaticReconnectStage(upperStage) &&
                !checkNetFastOutageActive &&
                !IsLocalNetworkUnavailableFast();

            if (shouldKeepTransportDropPanelStable)
            {
                if (!string.Equals(
                        lastRealtimeServerDebugStage,
                        "REALTIME_TRANSPORT_DROP",
                        StringComparison.Ordinal
                    ))
                {
                    ShowServerDebugPanelForRealtimeTransportDrop(
                        string.IsNullOrWhiteSpace(technicalDetails)
                            ? safeStage
                            : technicalDetails.Trim()
                    );
                }
                else
                {
                    SetStatus(FixedRealtimeTransportDropUserMessage);
                }

                Log(
                    "Realtime reconnect progress kept in log only to prevent panel flicker | stage=" +
                    safeStage
                );

                return;
            }

            string safeMessage = string.IsNullOrWhiteSpace(message) ? realtimeConnectPreparingMessage : message.Trim();
            if (IsRealtimeInternetLostStage(upperStage) || IsLegacyInternetLostReconnectMessage(safeMessage)) safeMessage = FixedInternetLostUserMessage;
            string safeTitle = GetServerDebugProgressTitleForStage(safeStage);
            if (string.Equals(safeMessage, FixedInternetLostUserMessage, StringComparison.Ordinal)) safeTitle = FixedInternetLostDebugTitle;
            string safeTechnical = string.IsNullOrWhiteSpace(technicalDetails) ? safeStage : technicalDetails.Trim();

            if (pnlServerDebug != null && !pnlServerDebug.activeSelf) pnlServerDebug.SetActive(true);
            if (txtServerDebugTitle != null) ApplyTextMeshValue(txtServerDebugTitle, safeTitle);
            if (txtServerDebugMessage != null) ApplyTextMeshValue(txtServerDebugMessage, safeMessage);
            if (txtServerDebugTechnical != null) ApplyTextMeshValue(txtServerDebugTechnical, safeStage + "\n" + safeTechnical);

            ApplyServerDebugButtonsForRealtimeFlow(isRunning, safeStage);
            SetStatus(safeMessage);
            Log("Realtime debug progress | stage=" + safeStage + " | running=" + isRunning + " | message=" + SafeText(safeMessage));
        }

        //* این تابع Stateهای پرتکرار Auto Reconnect را مشخص می کند تا روی پنل به یک پیام ثابت تبدیل شوند و باعث Flicker نشوند.
        private static bool IsHighFrequencyAutomaticReconnectStage(string upperStage)
        {
            if (string.IsNullOrWhiteSpace(upperStage)) return false;

            return upperStage.Contains("REALTIME_RECONNECT_WAIT")
                   || upperStage.Contains("REALTIME_RECONNECT_ATTEMPT")
                   || upperStage.Contains("REALTIME_SOCKET_CONNECTING")
                   || upperStage.Contains("REALTIME_CONNECT_FAILED");
        }

        private void ShowServerDebugPanelForRealtimeConnectFailure(string reason)
        {
            if (!openServerDebugPanelOnRealtimeConnectFailure) return;

            string message = string.IsNullOrWhiteSpace(realtimeConnectFailureDebugMessage)
                ? "اتصال به Realtime انجام نشد. لطفاً دوباره تلاش کنید."
                : realtimeConnectFailureDebugMessage.Trim();

            ShowServerDebugPanelForRealtimeProgress(message, "REALTIME_CONNECT_FAILED", "Reason=" + SafeText(reason), false);
        }

        private void ShowServerDebugPanelForRealtimeConnectSuccess(string reason)
        {
            string message = string.IsNullOrWhiteSpace(realtimeConnectSuccessDebugMessage)
                ? "اتصال به Realtime با موفقیت انجام شد."
                : realtimeConnectSuccessDebugMessage.Trim();

            ShowServerDebugPanelForRealtimeProgress(message, "REALTIME_CONNECT_SUCCESS", "Reason=" + SafeText(reason), false);
            HideServerDebugPanelAfterRealtimeConnectSuccess();
        }

        //* این تابع وضعیت دکمه های پنل دیباگ را بر اساس مرحله ریکانکت تنظیم می کند.
        private void ApplyServerDebugButtonsForRealtimeFlow(bool isRunning, string stage)
        {
            string safeStage = SafeText(stage);
            string upperStage = safeStage.ToUpperInvariant();

            bool isFailureStage = IsRealtimeDebugFailureStage(safeStage);

            bool isRecoveringConnection =
                upperStage.Contains("RECONNECT") ||
                upperStage.Contains("CONNECTION_LOST") ||
                upperStage.Contains("INTERNET_CONNECTION_LOST");

            if (isRunning)
            {
                SetButtonGameObjectActive(btnServerDebugClose, true);
                SetButtonInteractable(btnServerDebugClose, true);

                SetButtonGameObjectActive(btnServerDebugRetry, false);
                SetButtonGameObjectActive(btnServerDebugRelogin, false);
                return;
            }

            SetButtonGameObjectActive(btnServerDebugClose, true);
            SetButtonInteractable(btnServerDebugClose, true);

            SetButtonGameObjectActive(btnServerDebugRetry, isFailureStage);
            SetButtonInteractable(btnServerDebugRetry, isFailureStage);

            SetButtonGameObjectActive(btnServerDebugRelogin, false);
        }

        //* این تابع مشخص می کند مرحله فعلی یک نتیجه شکست نهایی یا قابل تلاش دوباره است یا نه.
        private bool IsRealtimeDebugFailureStage(string stage)
        {
            if (string.IsNullOrWhiteSpace(stage)) return false;

            string value = stage.Trim().ToUpperInvariant();
            return value.Contains("FAILED")
                   || value.Contains("FAILURE")
                   || value.Contains("EXCEPTION")
                   || value.Contains("TIMEOUT");
        }

        private static void SetButtonGameObjectActive(Button button, bool active)
        {
            if (button == null) return;
            if (button.gameObject.activeSelf != active) button.gameObject.SetActive(active);
        }

        private static void SetButtonInteractable(Button button, bool interactable)
        {
            if (button == null) return;
            if (button.interactable != interactable) button.interactable = interactable;
        }

        //* این تابع دکمه های پنل دیباگ را به هندلرهای امن وصل می کند.
        private void BindServerDebugButtonHandlers(string source)
        {
            if (serverDebugButtonHandlersBound) return;
            if (btnServerDebugClose == null && btnServerDebugRetry == null && btnServerDebugRelogin == null) return;

            if (btnServerDebugClose != null) btnServerDebugClose.onClick.AddListener(HideServerDebugPanelFromDebugButton);
            if (btnServerDebugRetry != null) btnServerDebugRetry.onClick.AddListener(RetryRealtimeFromServerDebugButton);

            serverDebugButtonHandlersBound = true;
            Log("Server debug button handlers bound. source=" + SafeText(source));
        }

        //* این تابع فقط پنل پیام را می بندد و هیچ نقشی در توقف ریکانکت، خروج از روم، یا پاکسازی گیم سرور ندارد.
        public void HideServerDebugPanelFromDebugButton()
        {
            AutoResolveServerDebugReferences("debug_close_button_hide_message_only");
            if (pnlServerDebug != null && pnlServerDebug.activeSelf) pnlServerDebug.SetActive(false);
            Log("Server debug close hidden only. Reconnect and room/game-server context were not changed.");
        }

        //* این تابع بعد از شکست ریکانکت، تلاش دوباره را از همان پنل دیباگ شروع می کند.
        public void RetryRealtimeFromServerDebugButton()
        {
            if (!IsRealtimeRetryButtonOwnedByRealtimePanel())
            {
                Log("Server debug retry ignored because current panel is not owned by realtime. stage=" + SafeText(lastRealtimeServerDebugStage));
                return;
            }

            if (isConnectAndAuthRunning || isRealtimeReconnectRunning)
            {
                Log("Server debug retry ignored because realtime flow is already running.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(activeRoomId))
            {
                StartRealtimeReconnectLoop("server_debug_retry_button");
                return;
            }

            if (!CanStartNormalRealtimeConnectNow("server_debug_retry_button")) return;

            ConnectAndAuthButton();
        }

        private bool IsRealtimeRetryButtonOwnedByRealtimePanel()
        {
            if (string.IsNullOrWhiteSpace(lastRealtimeServerDebugStage)) return false;

            string upperStage = lastRealtimeServerDebugStage.Trim().ToUpperInvariant();

            if (!upperStage.Contains("REALTIME") && !upperStage.Contains("RECONNECT")) return false;
            if (upperStage.Contains("AUTH_NOT_READY")) return false;

            return IsRealtimeDebugFailureStage(upperStage);
        }

        private void HideServerDebugPanelAfterRealtimeConnectSuccess()
        {
            bool dedicatedRecoveryPending =
                dedicatedGameServerPresenceGuardActive &&
                !IsDedicatedGameServerConnectedAndAuthenticated();

            if (isRealtimeReconnectRunning || dedicatedRecoveryPending)
            {
                Log(
                    "Server debug panel auto-hide deferred until full recovery. " +
                    "realtimeReconnectRunning=" +
                    isRealtimeReconnectRunning +
                    " | dedicatedRecoveryPending=" +
                    dedicatedRecoveryPending
                );

                return;
            }

            AutoResolveServerDebugReferences("connect_success");
            if (pnlServerDebug != null && pnlServerDebug.activeSelf) pnlServerDebug.SetActive(false);
        }

        private void ShowRealtimeInfoMessage(string message)
        {
            SetStatus(message);
            MainMenuMessageManager.Info(message);
        }

        private void ShowRealtimeSuccessMessage(string message)
        {
            SetStatus(message);
            MainMenuMessageManager.Success(message);
        }

        private void ShowRealtimeWarningMessage(string message)
        {
            SetStatus(message);
            MainMenuMessageManager.Warning(message);
        }

        private void ShowRealtimeErrorMessage(string message)
        {
            SetStatus(message);
            MainMenuMessageManager.Error(message);
        }

        private void SetStatus(string value)
        {
            QueueStatusText(value);
        }

        private void Log(string message)
        {
            string safeMessage = message ?? string.Empty;
            string line = "[G7-WebSocket-RoomLobby] " + safeMessage;
            Debug.Log(line);

            logBuffer.AppendLine(line);
            if (logBuffer.Length > 8000) logBuffer.Remove(0, logBuffer.Length - 8000);

            QueueLogText(logBuffer.ToString());

            if (mirrorLogToStatusWhenLogTextMissing && logText == null)
            {
                QueueStatusText(safeMessage);
            }
        }

        //* این تابع متن استاتوس را صف می کند تا در آپدیت اصلی یونیتی روی تکست اعمال شود.
        private void QueueStatusText(string value)
        {
            pendingStatusTextValue = value ?? string.Empty;
            hasPendingStatusTextRefresh = true;
        }

        //* این تابع متن لاگ را صف می کند تا در وب جی ال هم از مسیر آپدیت اصلی یونیتی اعمال شود.
        private void QueueLogText(string value)
        {
            pendingLogTextValue = value ?? string.Empty;
            hasPendingLogTextRefresh = true;
        }

        //* این تابع متن روم را صف می کند تا مقداردهی مستقیم تکست در کالبک های وب جی ال مشکل ایجاد نکند.
        private void QueueRoomText(string value)
        {
            pendingRoomTextValue = value ?? string.Empty;
            hasPendingRoomTextRefresh = true;
        }

        //* این تابع تغییرات صف شده تکست ها را فقط از مسیر آپدیت اصلی یونیتی روی یو آی اعمال می کند.
        private void ApplyPendingUiRefresh()
        {
            if (hasPendingStatusTextRefresh)
            {
                hasPendingStatusTextRefresh = false;
                if (statusText != null) ApplyTextMeshValue(statusText, pendingStatusTextValue);
            }

            if (hasPendingLogTextRefresh)
            {
                hasPendingLogTextRefresh = false;
                if (logText != null) ApplyTextMeshValue(logText, pendingLogTextValue);
            }

            if (hasPendingRoomTextRefresh)
            {
                hasPendingRoomTextRefresh = false;
                if (roomText != null) ApplyTextMeshValue(roomText, pendingRoomTextValue);
            }
        }

        //* این تابع مقدار تکست را با پشتیبانی از آر تی ال تکست مش پرو اعمال می کند.
        private void ApplyTextMeshValue(TextMeshProUGUI targetText, string value)
        {
            if (targetText == null) return;

            string safeValue = value ?? string.Empty;
            RTLTextMeshPro rtlTargetText = targetText as RTLTextMeshPro;

            if (rtlTargetText == null)
            {
                rtlTargetText = targetText.GetComponent<RTLTextMeshPro>();
            }

            if (rtlTargetText != null)
            {
                bool containsRtlCharacters = false;

                for (int i = 0; i < safeValue.Length; i++)
                {
                    char character = safeValue[i];

                    if ((character >= '\u0600' && character <= '\u06FF') ||
                        (character >= '\u0750' && character <= '\u077F') ||
                        (character >= '\u08A0' && character <= '\u08FF'))
                    {
                        containsRtlCharacters = true;
                        break;
                    }
                }

                rtlTargetText.Farsi = true;
                rtlTargetText.ForceFix = containsRtlCharacters;
                rtlTargetText.text = safeValue;
                rtlTargetText.UpdateText();
            }
            else if (!TryApplyRtlTextMeshProValue(targetText, safeValue))
            {
                targetText.text = safeValue;
            }

            if (!forceTextMeshRefreshAfterUiApply) return;

            targetText.SetVerticesDirty();
            targetText.SetLayoutDirty();
            targetText.ForceMeshUpdate(true, true);
        }
        //* این تابع اگر روی آبجکت تکست، کامپوننت آر تی ال تکست مش پرو وجود داشته باشد، متن را از همان مسیر اعمال می کند.
        private bool TryApplyRtlTextMeshProValue(TextMeshProUGUI targetText, string value)
        {
            if (targetText == null) return false;

            Component[] components = targetText.GetComponents<Component>();

            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null) continue;

                Type componentType = component.GetType();
                string typeName = componentType.Name;

                bool isRtlTextMeshPro =
                    typeName.IndexOf("RtlTextMeshpro", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("RtlTextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    typeName.IndexOf("RTLTextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!isRtlTextMeshPro) continue;

                try
                {
                    System.Reflection.PropertyInfo textProperty = componentType.GetProperty("text");
                    if (textProperty != null && textProperty.CanWrite && textProperty.PropertyType == typeof(string))
                    {
                        textProperty.SetValue(component, value);
                        return true;
                    }

                    System.Reflection.PropertyInfo originalTextProperty = componentType.GetProperty("OriginalText");
                    if (originalTextProperty != null && originalTextProperty.CanWrite && originalTextProperty.PropertyType == typeof(string))
                    {
                        originalTextProperty.SetValue(component, value);
                        return true;
                    }

                    System.Reflection.MethodInfo setTextMethod = componentType.GetMethod("SetText", new Type[] { typeof(string) });
                    if (setTextMethod != null)
                    {
                        setTextMethod.Invoke(component, new object[] { value });
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[G7-WebSocket-RoomLobby] RTL text apply failed | object=" +
                                     targetText.name + " | component=" + typeName + " | error=" + ex.Message);
                }
            }

            return false;
        }
        //* این تابع وضعیت وصل بودن رفرنس های یو آی را مستقیم در کنسول چاپ می کند.
        private void LogUiReferences(string source)
        {
            Debug.Log("[G7-WebSocket-RoomLobby] UI refs " + source
                + " | roomText=" + FormatUiReference(roomText)
                + " | statusText=" + FormatUiReference(statusText)
                + " | logText=" + FormatUiReference(logText)
                + " | roomNameInput=" + FormatUiReference(roomNameInput)
                + " | messageInput=" + FormatUiReference(messageInput)
                + " | listRoomsButton=" + FormatUiReference(listRoomsButton)
                + " | leaveRoomButton=" + FormatUiReference(leaveRoomButton)
                + " | sendMessageButton=" + FormatUiReference(sendMessageButton));
        }

        //* این تابع نام آبجکت رفرنس یو آی را برای دیباگ امن می سازد.
        private string FormatUiReference(UnityEngine.Object reference)
        {
            return reference == null ? "MISSING" : "OK:" + reference.name;
        }





        private void RenderRoomListButtons(RealtimeRoomDto[] rooms)
        {
            ClearRoomListButtons();

            if (roomListContent == null)
            {
                Log("Room list content is not assigned.");
                return;
            }

            if (roomListItemPrefab == null)
            {
                Log("Room list item prefab is not assigned.");
                return;
            }

            if (rooms == null || rooms.Length == 0)
            {
                Log("Room list UI is empty.");
                return;
            }

            for (int i = 0; i < rooms.Length; i++)
            {
                RealtimeRoomDto room = rooms[i];
                if (room == null) continue;

                room.Normalize();

                RealtimeRoomListItemView item = Instantiate(roomListItemPrefab, roomListContent);
                item.Setup(room, HandleRoomListItemClicked);
                item.SetInteractable(!isJoined && !isJoiningFromRoomList);

                roomListItems.Add(item);
            }

            Log("Room list UI rendered. items=" + roomListItems.Count);
        }

        private void ClearRoomListButtons()
        {
            for (int i = 0; i < roomListItems.Count; i++)
            {
                if (roomListItems[i] != null) Destroy(roomListItems[i].gameObject);
            }

            roomListItems.Clear();

            if (roomListContent == null) return;

            for (int i = roomListContent.childCount - 1; i >= 0; i--)
            {
                Destroy(roomListContent.GetChild(i).gameObject);
            }
        }

        private void SetRoomListInteractable(bool value)
        {
            for (int i = 0; i < roomListItems.Count; i++)
            {
                if (roomListItems[i] == null) continue;
                roomListItems[i].SetInteractable(value);
            }
        }

        private void SetListRoomsButtonInteractable(bool value)
        {
            if (listRoomsButton == null) return;
            listRoomsButton.interactable = value;
        }

        private async void HandleRoomListItemClicked(RealtimeRoomDto room)
        {
            if (room == null)
            {
                Fail("Selected room is null.");
                return;
            }

            if (isJoined)
            {
                Log("Room click ignored. You are already inside a room.");
                return;
            }

            if (isJoiningFromRoomList)
            {
                Log("Room click ignored. Join is already running.");
                return;
            }

            room.Normalize();

            if (!room.CanJoin())
            {
                Fail("Selected room is not joinable: " + room.roomName);
                return;
            }

            selectedListedRoom = room;
            activeRoomId = room.roomId;
            activeRoomName = room.roomName;

            isJoiningFromRoomList = true;
            SetRoomListInteractable(false);
            UpdateSendMessageButton();

            UpdateRoomDisplay(room, false);
            SetStatus("Joining " + room.roomName + "...");

            bool joined = await JoinRoomAsync();

            isJoiningFromRoomList = false;
            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();

            if (joined)
            {
                SetRoomListInteractable(false);
                SetListRoomsButtonInteractable(false);
                if (clearRoomListOnJoinSuccess) ClearRoomListButtons();

                Log("Joined selected room from list: " + activeRoomId + " | " + activeRoomName);
                UpdateConnectionButtons();
                UpdateCreateRoomButton();
                UpdateSendMessageButton();
                return;
            }

            selectedListedRoom = null;
            SetRoomListInteractable(true);
            ShowRealtimeErrorMessage("Join failed. Select another room.");
            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();
        }

        private RealtimeRoomDto FindLastListedRoom(string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId) || lastListedRooms == null) return null;

            for (int i = 0; i < lastListedRooms.Length; i++)
            {
                if (lastListedRooms[i] == null) continue;
                if (string.Equals(lastListedRooms[i].roomId, roomId, StringComparison.OrdinalIgnoreCase)) return lastListedRooms[i];
            }

            return null;
        }

        //* این تابع اسنپ شات روم جوین شده را از DTO مشترک لیست جدا می کند تا تغییرات حضور دوباره روی لیست انباشته نشوند.
        private static RealtimeRoomDto CloneRoomDto(RealtimeRoomDto room)
        {
            if (room == null) return null;

            return new RealtimeRoomDto
            {
                roomId = room.roomId,
                roomName = room.roomName,
                description = room.description,
                ownerUserId = room.ownerUserId,
                ownerUserName = room.ownerUserName,
                visibility = room.visibility,
                status = room.status,
                maxPlayers = room.maxPlayers,
                onlineCount = room.onlineCount,
                createdAtUnix = room.createdAtUnix,
                updatedAtUnix = room.updatedAtUnix,
                lastActiveAtUnix = room.lastActiveAtUnix,
                closedAtUnix = room.closedAtUnix,
                canJoin = room.canJoin
            };
        }

        private void UpdateRoomDisplay(
            RealtimeRoomDto room,
            bool joined,
            int? onlineCountOverride = null
        )
        {
            if (room == null)
            {
                QueueRoomText("Room: -\nOwner: -\nUsers: -");
                return;
            }

            room.Normalize();

            int onlineCount = onlineCountOverride.HasValue
                ? Mathf.Clamp(
                    onlineCountOverride.Value,
                    joined ? 1 : 0,
                    Mathf.Max(1, room.maxPlayers)
                )
                : joined
                    ? Mathf.Max(1, room.onlineCount)
                    : room.onlineCount;
            string ownerName = string.IsNullOrWhiteSpace(room.ownerUserName) ? "-" : room.ownerUserName;
            string roomName = string.IsNullOrWhiteSpace(room.roomName) ? "-" : room.roomName;

            QueueRoomText(
                "Room: " + roomName +
                "\nOwner: " + ownerName +
                "\nUsers: " + onlineCount + "/" + room.maxPlayers
            );
        }

        //* این تابع اجازه می دهد اسکریپت های دیگر، پیام تستی را داخل لاگ تکست ریل تایم چاپ کنند.
        public void AppendExternalLogTextLine(string source, string message)
        {
            string safeSource = string.IsNullOrWhiteSpace(source) ? "External" : source.Trim();
            string safeMessage = string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim();

            if (string.Equals(safeSource, "DedicatedMove", StringComparison.OrdinalIgnoreCase) ||
                safeMessage.StartsWith("GAME_SERVER_MOVE_SENT", StringComparison.OrdinalIgnoreCase))
            {
                AppendDedicatedMoveOnlyLogText(safeSource, safeMessage);
                return;
            }

            Log("[" + safeSource + "] " + safeMessage);
        }

        //* این تابع برای تست حرکت، لاگ تکست را فقط با دیتای ارسال حرکت گیم سرور پر می کند.
        private void AppendDedicatedMoveOnlyLogText(string source, string message)
        {
            string safeSource = string.IsNullOrWhiteSpace(source) ? "DedicatedMove" : source.Trim();
            string safeMessage = string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim();

            string line = "[G7-WebSocket-RoomLobby] [" + safeSource + "] " + safeMessage;

            Debug.Log(line);

            List<string> moveLines = new List<string>();

            string current = logBuffer.ToString();
            string[] lines = current.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < lines.Length; i++)
            {
                string item = lines[i];
                if (string.IsNullOrWhiteSpace(item)) continue;

                if (item.Contains("[DedicatedMove] GAME_SERVER_MOVE_SENT"))
                {
                    moveLines.Add(item);
                }
            }

            moveLines.Add(line);

            while (moveLines.Count > 12)
            {
                moveLines.RemoveAt(0);
            }

            logBuffer.Length = 0;
            logBuffer.AppendLine("=== GAME SERVER MOVE SEND LOG ===");

            for (int i = 0; i < moveLines.Count; i++)
            {
                logBuffer.AppendLine(moveLines[i]);
            }

            QueueLogText(logBuffer.ToString());
        }


        //* این تابع قطع شدن اینترنت سیستم را قبل از خطای دیرهنگام ترنسپورت تشخیص می دهد.
        private void DetectImmediateInternetLostByLocalNetwork()
        {
            if (!showServerDebugPanelImmediatelyOnInternetLost) return;
            if (isUserRequestedExitFlow || isCleaningUp) return;
            if (!HasActiveRealtimeNetworkContextForInternetLostWatch()) return;

            if (IsLocalNetworkUnavailableFast())
            {
                if (immediateInternetLostHandled && isRealtimeReconnectRunning) return;

                immediateInternetLostHandled = true;
                MarkRealtimeDisconnectedByTransport("local_internet_not_reachable");
                return;
            }

            immediateInternetLostHandled = false;
            consecutiveFastRealtimeProbeFailures = 0;
        }

        //* این تابع مسیر واقعی تی سی پی به سرور ریل تایم را جدا از WebSocket بررسی می کند تا یو آی منتظر تایم اوت ترنسپورت نماند.
        private void DetectImmediateInternetLostByFastProbe()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
    return;
#else
            if (!enableFastRealtimeTcpConnectivityProbe) return;
            if (isUserRequestedExitFlow || isCleaningUp) return;
            if (!HasActiveRealtimeNetworkContextForInternetLostWatch()) return;
            if (immediateInternetLostHandled && isRealtimeReconnectRunning) return;
            if (fastRealtimeProbeRunning) return;

            float now = Time.realtimeSinceStartup;
            float interval = Mathf.Clamp(fastRealtimeConnectivityProbeIntervalSeconds, 0.15f, 0.75f);

            if (now < nextFastRealtimeProbeAt) return;

            nextFastRealtimeProbeAt = now + interval;
            _ = RunFastRealtimeConnectivityProbeAsync();
#endif
        }

        //* این تابع نتیجه پروب سریع سرور ریل تایم را می گیرد و نتیجه دیررس یا شکست تکی را بدون ساختن ریکانکت جعلی کنترل می کند.
        private async Task RunFastRealtimeConnectivityProbeAsync()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            await Task.CompletedTask;
#else
            fastRealtimeProbeRunning = true;

            //* نسل اتصال را همین لحظه‌ی شروع پروب ثبت می کنیم تا اگر تا لحظه‌ی برگشت پروب یک اتصال جدید و سالم
            //* برقرار شده باشد (مثلاً ریکانکت اصلی سریع‌تر موفق شده)، نتیجه‌ی این پروبِ کهنه نادیده گرفته شود.
            int probeGenerationId = connectionGenerationId;

            try
            {
                string host;
                int port;

                if (!TryResolveRealtimeTcpProbeTarget(out host, out port)) return;

                bool reachable = await TryConnectTcpProbeAsync(host, port, fastRealtimeConnectivityProbeTimeoutMs);

                //* در فاصله‌ی زمانی awaitِ بالا ممکن است یک اتصال کاملاً جدید authenticate شده باشد؛
                //* در این صورت نتیجه‌ی این پروب دیگر معتبر نیست و نباید هیچ اقدامی (نه ریست، نه شمارش شکست) انجام دهد.
                if (probeGenerationId != connectionGenerationId)
                {
                    Log("Fast realtime connectivity probe result discarded as stale. probeGenerationId=" + probeGenerationId + " | currentGenerationId=" + connectionGenerationId);
                    return;
                }

                if (reachable)
                {
                    consecutiveFastRealtimeProbeFailures = 0;
                    if (!isRealtimeReconnectRunning) immediateInternetLostHandled = false;
                    return;
                }

                consecutiveFastRealtimeProbeFailures++;
                Log("Fast realtime connectivity probe failed but reconnect is not started by probe alone. target=" + SafeText(host) + ":" + port
                    + " | failures=" + consecutiveFastRealtimeProbeFailures
                    + " | allowProbeReconnect=" + allowFastRealtimeTcpProbeToStartReconnect
                    + " | localNetworkUnavailable=" + IsLocalNetworkUnavailableFast());

                int failuresNeeded = Mathf.Clamp(fastRealtimeConnectivityProbeFailuresBeforeReconnect, 1, 2);
                if (consecutiveFastRealtimeProbeFailures < failuresNeeded) return;
                if (isUserRequestedExitFlow || isCleaningUp) return;
                if (!HasActiveRealtimeNetworkContextForInternetLostWatch()) return;
                if (immediateInternetLostHandled && isRealtimeReconnectRunning) return;

                immediateInternetLostHandled = true;

                MarkRealtimeDisconnectedByTransport(
                    "local_internet_not_reachable_fast_probe:" + SafeText(host) + ":" + port
                );
            }
            catch (Exception ex)
            {
                Log("Fast realtime connectivity probe warning: " + ex.Message);
            }
            finally
            {
                fastRealtimeProbeRunning = false;
            }
#endif
        }

        //* این تابع هاست و پورت ریل تایم را برای پروب سریع از همان آدرس فعال کنترلر استخراج می کند.
        private bool TryResolveRealtimeTcpProbeTarget(out string host, out int port)
        {
            host = string.Empty;
            port = 0;

            string source = !string.IsNullOrWhiteSpace(activeServerUrl) ? activeServerUrl.Trim() : ResolveRealtimeServerUrl();
            if (string.IsNullOrWhiteSpace(source)) return false;

            Uri uri;
            if (Uri.TryCreate(source, UriKind.Absolute, out uri))
            {
                host = uri.Host;
                port = uri.Port > 0 ? uri.Port : ResolveRealtimeTcpProbeDefaultPort(uri.Scheme);
                return !string.IsNullOrWhiteSpace(host) && port > 0;
            }

            string value = source.Trim();
            int slashIndex = value.IndexOf('/');
            if (slashIndex >= 0) value = value.Substring(0, slashIndex);

            int colonIndex = value.LastIndexOf(':');
            if (colonIndex > 0 && colonIndex < value.Length - 1 && int.TryParse(value.Substring(colonIndex + 1), out int parsedPort))
            {
                host = value.Substring(0, colonIndex);
                port = parsedPort;
                return !string.IsNullOrWhiteSpace(host) && port > 0;
            }

            host = value;
            port = 443;
            return !string.IsNullOrWhiteSpace(host) && port > 0;
        }

        //* این تابع پورت پیش فرض پروب ریل تایم را بر اساس اسکیم آدرس مشخص می کند.
        private int ResolveRealtimeTcpProbeDefaultPort(string scheme)
        {
            string safeScheme = string.IsNullOrWhiteSpace(scheme) ? string.Empty : scheme.Trim().ToLowerInvariant();
            if (safeScheme == "https" || safeScheme == "wss") return 443;
            if (safeScheme == "http" || safeScheme == "ws") return 80;
            return 443;
        }

        //* این تابع اتصال تی سی پی کوتاه به سرور ریل تایم را با تایم اوت محدود تست می کند.
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
                    Task timeoutTask = Task.Delay(Mathf.Max(250, timeoutMs));
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

        //* این تابع پیام فوری قطع اینترنت را برای پنل ریل تایم برمی گرداند و مقدار قدیمی اینسپکتور را نادیده می گیرد.
        private string GetRealtimeInternetLostImmediateMessage()
        {
            return FixedInternetLostUserMessage;
        }

        //* این تابع مشخص می کند آیا دلیل خطا مربوط به قطع اینترنت کاربر است یا نه.
        private bool IsUserInternetUnavailableReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;

            string value = reason.Trim().ToLowerInvariant();
            return value.Contains("local_internet_not_reachable")
                   || value.Contains("not_reachable")
                   || value.Contains("internet");
        }

        //* این تابع تشخیص می دهد آیا علت فعلی واقعاً قطع اینترنت کاربر است یا فقط افت ترنسپورت ریل تایم.
        private bool ShouldTreatReasonAsActualInternetLost(string reason)
        {
            if (IsLocalNetworkUnavailableFast())
            {
                return true;
            }

            if (checkNetFastOutageActive)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(reason))
            {
                string value = reason.Trim().ToLowerInvariant();

                if (value.Contains("checknet_fast_server_unreachable"))
                {
                    return true;
                }
            }

            return IsUserInternetUnavailableReason(reason);
        }

        //* این تابع خطاهای ترنسپورت را که در قطعی شبکه رخ می دهند برای کنترل یو آی تشخیص می دهد.
        private bool IsRealtimeNetworkIssueReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;

            string value = reason.Trim().ToLowerInvariant();
            return IsUserInternetUnavailableReason(value)
                   || value.Contains("dns")
                   || value.Contains("unavailable")
                   || value.Contains("stream removed")
                   || value.Contains("receive failed")
                   || value.Contains("send failed")
                   || value.Contains("closed by remote")
                   || value.Contains("socket")
                   || value.Contains("transport")
                   || value.Contains("network")
                   || value.Contains("ssl")
                   || value.Contains("timeout");
        }

        //* این تابع قفل یو آی قطع اینترنت واقعی را فعال می کند.
        //* افت ترنسپورت ریل تایم مثل End of TCP stream نباید پیام «اینترنت قطع است» بسازد.
        //* این تابع قفل UI قطع اینترنت واقعی را فعال می کند.
        //* افت مستقل Transport نباید پیام قطع اینترنت بسازد.
        private void MarkRealtimeNetworkIssueUiLocked(string reason)
        {
            bool actualInternetLost =
                checkNetFastOutageActive ||
                ShouldTreatReasonAsActualInternetLost(reason);

            if (!actualInternetLost)
            {
                Log(
                    "Realtime network issue lock skipped because reason is transport drop, not actual internet loss. reason=" +
                    SafeText(reason)
                );

                ShowServerDebugPanelForRealtimeTransportDrop(reason);
                return;
            }

            if (!suppressRealtimeReconnectProgressUiDuringNetworkIssue)
            {
                return;
            }

            realtimeNetworkIssueUiLocked = true;

            realtimeNetworkIssueUiLockedUntil =
                Time.realtimeSinceStartup +
                Mathf.Max(3f, realtimeNetworkIssueUiLockSeconds);

            suppressPlayerLeftUiUntil = Mathf.Max(
                suppressPlayerLeftUiUntil,
                realtimeNetworkIssueUiLockedUntil
            );

            ShowServerDebugPanelForInternetLost(reason);
        }

        //* این تابع بعد از احراز موفق ریل تایم، قفل پیام قطع شبکه را آزاد می کند ولی برای player_left قدیمی مهلت محافظتی نگه می دارد.
        private void ReleaseRealtimeNetworkIssueUiLock(string reason)
        {
            bool wasLocked = realtimeNetworkIssueUiLocked;
            realtimeNetworkIssueUiLocked = false;
            realtimeNetworkIssueUiLockedUntil = 0f;
            internetLostPanelShownForCurrentOutage = false;

            if (suppressPlayerLeftUiDuringRealtimeReconnect)
            {
                suppressPlayerLeftUiUntil = Mathf.Max(
                    suppressPlayerLeftUiUntil,
                    Time.realtimeSinceStartup + Mathf.Max(1f, playerLeftSuppressSecondsAfterRealtimeReconnect)
                );
            }

            if (wasLocked) Log("Realtime network issue UI lock released. reason=" + SafeText(reason));

            // Release can run while Dedicated is still reconnecting. The cleanup method
            // therefore performs its own full Realtime + Dedicated recovery checks.
            ClearStaleNetworkStatusIfFullyRecovered(
                "network_issue_ui_lock_released:" + SafeText(reason)
            );
        }

        //* این تابع وضعیت فعال بودن قفل پیام قطع شبکه را برمی گرداند.
        private bool IsRealtimeNetworkIssueUiLockActive()
        {
            if (!realtimeNetworkIssueUiLocked) return false;
            if (Time.realtimeSinceStartup <= realtimeNetworkIssueUiLockedUntil) return true;

            realtimeNetworkIssueUiLocked = false;
            realtimeNetworkIssueUiLockedUntil = 0f;
            return false;
        }

        //* این تابع مشخص می کند کدام مراحل ریکانکت نباید هنگام قطعی شبکه روی یو آی نوشته شوند.
        private bool ShouldSuppressRealtimeProgressUiForNetworkIssue(string upperStage)
        {
            if (!suppressRealtimeReconnectProgressUiDuringNetworkIssue) return false;
            if (!IsRealtimeNetworkIssueUiLockActive()) return false;
            if (string.IsNullOrWhiteSpace(upperStage)) return false;

            return upperStage.Contains("REALTIME_RECONNECT_STARTED")
                   || upperStage.Contains("REALTIME_RECONNECT_WAIT")
                   || upperStage.Contains("REALTIME_RECONNECT_ATTEMPT")
                   || upperStage.Contains("REALTIME_SOCKET_CONNECTING")
                   || upperStage.Contains("REALTIME_CONNECT_FAILED");
        }

        //* این تابع پیام مناسب برای شروع ریکانکت را بر اساس علت واقعی انتخاب می کند.
        private string GetRealtimeReconnectTransportDropMessage()
        {
            return FixedRealtimeTransportDropUserMessage;
        }

        //* این تابع وقتی ددیکیتد گیم‌سرور فعال است، player_left های مسیر ریل‌تایم را از یو آی و مسیر سه‌بعدی حذف می کند.
        //* در این حالت منبع درست حضور پلیرها، پیام های خود ددیکیتد گیم‌سرور است نه presence قدیمی ریل‌تایم.
        private bool ShouldSuppressPlayerLeftUiBecauseDedicatedGameServerIsSourceOfTruth(string playerId, string displayName)
        {
            if (!suppressRealtimePlayerLeftUiWhileDedicatedGameServerActive) return false;
            if (dedicatedGameServerPresenceGuardActive) return true;
            if (Time.realtimeSinceStartup <= dedicatedGameServerPresenceGuardUntil) return true;
            return false;
        }

        //* این تابع player_left های قدیمی بعد از ریکانکت را که مربوط به نشست قبلی هستند از یو آی حذف می کند.
        private bool ShouldSuppressPlayerLeftUiBecauseRealtimeReconnect(string playerId, string displayName)
        {
            if (!suppressPlayerLeftUiDuringRealtimeReconnect) return false;
            if (isRealtimeReconnectRunning) return true;
            if (IsRealtimeNetworkIssueUiLockActive()) return true;
            if (Time.realtimeSinceStartup <= suppressPlayerLeftUiUntil) return true;
            return false;
        }

        //* این تابع جلوی نمایش پاپ آپ های فنی را وقتی اینترنت کاربر قطع است می گیرد.
        private bool ShouldSuppressRealtimePopupBecauseInternetIsDown(string reason)
        {
            if (isRealtimeReconnectRunning || immediateInternetLostHandled) return true;
            if (IsUserInternetUnavailableReason(reason)) return true;
            return IsLocalNetworkUnavailableFast();
        }

        //* این تابع فقط وقتی اینترنت واقعی قطع است، پیام های تلاش برای ریکانکت را روی یو آی مخفی می کند.
        //* افت ترنسپورت ریل تایم نباید با اینترنت قطع شده یکی شود.
        private bool ShouldSuppressReconnectProgressBecauseInternetIsDown(string reason)
        {
            bool actualInternetLost = ShouldTreatReasonAsActualInternetLost(reason);
            if (!actualInternetLost && !immediateInternetLostHandled) return false;
            if (IsRealtimeNetworkIssueUiLockActive() && actualInternetLost) return true;
            if (immediateInternetLostHandled && IsLocalNetworkUnavailableFast()) return true;
            return false;
        }

        //* این تابع مشخص می کند آیا کلاینت در وضعیتی هست که باید قطع اینترنت برای او نمایش داده شود یا نه.
        private bool HasActiveRealtimeNetworkContextForInternetLostWatch()
        {
            if (isConnectAndAuthRunning || isRealtimeReconnectRunning) return true;
            if (isConnected || isAuthenticated || isJoined) return true;
            if (realtimeClient != null && realtimeClient.IsConnected) return true;

            return false;
        }

        //* این تابع قطع بودن شبکه محلی را بدون انتظار برای خطای دیرهنگام WebSocket بررسی می کند.
        private bool IsLocalNetworkUnavailableFast()
        {
            if (Application.internetReachability == NetworkReachability.NotReachable) return true;

#if UNITY_WEBGL && !UNITY_EDITOR
            if (!WebGLWebSocketRealtimeTransport.IsBrowserOnline) return true;
#endif

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
        //* این تابع نتیجه خروج دستی از ریل تایم را روی پنل دیباگ نشان می دهد و پیام های قدیمی ریکانکت را پاک می کند.
        private void ShowServerDebugPanelForManualRealtimeDisconnectSuccess(string reason)
        {
            AutoResolveServerDebugReferences("manual_realtime_disconnect_complete");

            string safeTitle = "خروج از ریل تایم";
            string safeMessage = "اتصال ریل تایم قطع شد. از روم و گیم سرور خارج شدید.";
            string safeStage = "MANUAL_REALTIME_DISCONNECT_COMPLETE";
            string safeReason = SafeText(reason);

            if (pnlServerDebug != null && !pnlServerDebug.activeSelf) pnlServerDebug.SetActive(true);
            if (txtServerDebugTitle != null) ApplyTextMeshValue(txtServerDebugTitle, safeTitle);
            if (txtServerDebugMessage != null) ApplyTextMeshValue(txtServerDebugMessage, safeMessage);
            if (txtServerDebugTechnical != null) ApplyTextMeshValue(txtServerDebugTechnical, safeStage + "\nReason=" + safeReason);

            SetButtonGameObjectActive(btnServerDebugClose, true);
            SetButtonInteractable(btnServerDebugClose, true);

            SetButtonGameObjectActive(btnServerDebugRetry, false);
            SetButtonGameObjectActive(btnServerDebugRelogin, false);

            SetStatus(safeMessage);
            Log("Manual realtime disconnect panel shown. reason=" + safeReason);
        }
        //* این تابع افت موقت ترنسپورت ریل تایم را جدا از قطع واقعی اینترنت نشان می دهد.
        private void ShowServerDebugPanelForRealtimeTransportDrop(string reason)
        {
            AutoResolveServerDebugReferences("realtime_transport_drop");

            string safeTitle = FixedRealtimeTransportDropDebugTitle;
            string safeMessage = FixedRealtimeTransportDropUserMessage;
            string safeReason = SafeText(reason);
            string safeStage = "REALTIME_TRANSPORT_DROP";

            lastRealtimeServerDebugStage = safeStage;

            if (pnlServerDebug != null && !pnlServerDebug.activeSelf) pnlServerDebug.SetActive(true);
            if (txtServerDebugTitle != null) ApplyTextMeshValue(txtServerDebugTitle, safeTitle);
            if (txtServerDebugMessage != null) ApplyTextMeshValue(txtServerDebugMessage, safeMessage);
            if (txtServerDebugTechnical != null) ApplyTextMeshValue(txtServerDebugTechnical, safeStage + "\nReason=" + safeReason);

            ApplyServerDebugButtonsForRealtimeFlow(true, safeStage);

            SetStatus(safeMessage);
            Log("Realtime transport drop panel shown. reason=" + safeReason);
        }

        //* این تابع پیام قطع اینترنت را در هر دوره قطعی فقط یک بار روی پنل دیباگ نشان می دهد و پاپ آپ جدا باز نمی کند.
        private void ShowServerDebugPanelForInternetLost(string reason)
        {
            if (!showServerDebugPanelImmediatelyOnInternetLost) return;
            if (internetLostPanelShownForCurrentOutage) return;

            internetLostPanelShownForCurrentOutage = true;

            AutoResolveServerDebugReferences("internet_connection_lost");

            string safeTitle = FixedInternetLostDebugTitle;
            string safeMessage = GetRealtimeInternetLostImmediateMessage();
            string safeReason = SafeText(reason);
            string safeStage = "INTERNET_CONNECTION_LOST";

            lastRealtimeServerDebugStage = safeStage;

            if (pnlServerDebug != null && !pnlServerDebug.activeSelf) pnlServerDebug.SetActive(true);
            if (txtServerDebugTitle != null) ApplyTextMeshValue(txtServerDebugTitle, safeTitle);
            if (txtServerDebugMessage != null) ApplyTextMeshValue(txtServerDebugMessage, safeMessage);
            if (txtServerDebugTechnical != null) ApplyTextMeshValue(txtServerDebugTechnical, safeStage + "\nReason=" + safeReason);

            ApplyServerDebugButtonsForRealtimeFlow(true, safeStage);

            SetStatus(safeMessage);
            Log("Internet connection lost panel shown once for current outage. reason=" + safeReason);
        }

        private void ShowCheckNetFastWarningOnlyPanel(string reason)
        {
            if (!showServerDebugPanelImmediatelyOnInternetLost) return;

            checkNetFastWarningOnlyPanelActive = true;

            if (internetLostPanelShownForCurrentOutage) return;

            ShowServerDebugPanelForInternetLost(reason);

            Log("CheckNet fast warning-only panel shown without starting reconnect. reason=" + SafeText(reason));
        }

        private void ClearCheckNetFastWarningOnlyPanelIfActive(string reason)
        {
            if (!checkNetFastWarningOnlyPanelActive) return;

            bool dedicatedRecoveryRequired =
                dedicatedGameServerPresenceGuardActive;
            bool dedicatedRecoveryComplete =
                !dedicatedRecoveryRequired ||
                IsDedicatedGameServerConnectedAndAuthenticated();

            bool canHideWarningPanel =
                !isRealtimeReconnectRunning &&
                !checkNetFastOutageActive &&
                !immediateInternetLostHandled &&
                IsRealtimeReady() &&
                dedicatedRecoveryComplete &&
                string.Equals(
                    lastRealtimeServerDebugStage,
                    "INTERNET_CONNECTION_LOST",
                    StringComparison.Ordinal
                );

            if (!canHideWarningPanel) return;

            checkNetFastWarningOnlyPanelActive = false;
            internetLostPanelShownForCurrentOutage = false;

            AutoResolveServerDebugReferences("checknet_fast_warning_clear");
            if (pnlServerDebug != null && pnlServerDebug.activeSelf) pnlServerDebug.SetActive(false);

            Log("CheckNet fast warning-only panel cleared. reason=" + SafeText(reason));
            ClearStaleNetworkStatusIfFullyRecovered(
                "checknet_fast_warning_cleared:" + SafeText(reason)
            );
        }

        //* این تابع فقط متن قدیمی قطعی شبکه را بعد از تأیید بازیابی کامل Realtime و Dedicated پاک می کند.
        private void ClearStaleNetworkStatusIfFullyRecovered(string source)
        {
            if (isConnectAndAuthRunning || isRealtimeReconnectRunning || isCleaningUp) return;
            if (checkNetFastOutageActive || immediateInternetLostHandled) return;
            if (Application.internetReachability == NetworkReachability.NotReachable) return;
            if (IsRealtimeNetworkIssueUiLockActive()) return;
            if (!IsRealtimeReady() || !isJoined) return;
            if (!IsDedicatedGameServerConnectedAndAuthenticated()) return;
            string currentStatus = hasPendingStatusTextRefresh
                ? pendingStatusTextValue
                : statusText == null
                    ? string.Empty
                    : statusText.text;

            if (!IsStaleNetworkStatusMessage(currentStatus)) return;

            Log(
                "Stale network status cleared after full recovery. source=" +
                SafeText(source)
            );
            SetStatus("اتصال برقرار است.");
        }

        //* این تابع فقط پیام های باقی مانده از قطعی یا بازیابی شبکه را شناسایی می کند.
        private static bool IsStaleNetworkStatusMessage(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            string safeValue = value.Trim();
            if (string.Equals(safeValue, FixedInternetLostUserMessage, StringComparison.Ordinal)) return true;
            if (string.Equals(safeValue, FixedRealtimeTransportDropUserMessage, StringComparison.Ordinal)) return true;

            bool mentionsDisconnect =
                safeValue.Contains("قطع") ||
                safeValue.Contains("از دست رفت");
            bool mentionsNetwork =
                safeValue.Contains("اینترنت") ||
                safeValue.Contains("شبکه") ||
                safeValue.Contains("Realtime") ||
                safeValue.Contains("ریلتایم") ||
                safeValue.Contains("بلادرنگ");
            bool mentionsRecovery =
                safeValue.Contains("بازیابی") ||
                safeValue.Contains("اتصال دوباره") ||
                safeValue.Contains("تلاش برای اتصال") ||
                safeValue.Contains("ریکانکت");

            return (mentionsDisconnect && mentionsNetwork) ||
                   (mentionsRecovery && mentionsNetwork);
        }

        //* این تابع متن مرحله بازیابی روم و آماده سازی اتصال دوباره به گیم سرور را برمی گرداند.
        private string GetRealtimeReconnectPrepareGameServerMessage()
        {
            return "اتصال بلادرنگ برگشت. در حال بازیابی روم و آماده سازی اتصال دوباره به گیم سرور...";
        }

        //* این تابع متن انتظار برای نتیجه نهایی اتصال دوباره به گیم سرور را برمی گرداند.
        private string GetRealtimeReconnectWaitingForGameServerMessage()
        {
            return "روم بازیابی شد. در حال اتصال دوباره به گیم سرور...";
        }
        //* این تابع پیام شروع ریکانکت را بر اساس دلیل قطع اتصال انتخاب می کند.
        private string GetRealtimeReconnectStartMessageForReason(string reason)
        {
            if (!string.IsNullOrWhiteSpace(reason))
            {
                string value = reason.Trim().ToLowerInvariant();

                if (ShouldTreatReasonAsActualInternetLost(value))
                {
                    return GetRealtimeInternetLostImmediateMessage();
                }

                if (IsRealtimeNetworkIssueReason(value))
                {
                    return GetRealtimeReconnectTransportDropMessage();
                }
            }

            return string.IsNullOrWhiteSpace(realtimeReconnectStartingMessage)
                ? "اتصال Realtime قطع شد. در حال تلاش برای اتصال دوباره..."
                : realtimeReconnectStartingMessage.Trim();
        }



        //* این تابع وقتی ریکانکت به نتیجه نرسد، پیام نهایی شکست را روی پنل دیباگ نشان می دهد.
        private void ShowRealtimeReconnectFinalFailurePanel(string reason, string stage, int attempts)
        {
            string safeReason = SafeText(reason);
            string safeStage = string.IsNullOrWhiteSpace(stage) ? "REALTIME_RECONNECT_FAILED_PERMANENTLY" : stage.Trim();

            ShowServerDebugPanelForRealtimeProgress(
                "اتصال دوباره انجام نشد. لطفاً اینترنت خود را بررسی کنید و دوباره تلاش کنید.",
                safeStage,
                "Reason=" + safeReason + " | attempts=" + attempts,
                false
            );

            SetStatus("اتصال دوباره انجام نشد. لطفاً اینترنت خود را بررسی کنید و دوباره تلاش کنید.");
        }

        private void BindAuthLoginReadyEvent(string source)
        {
            AuthManager.OnLoginReady -= HandleAuthLoginReadyForRealtime;
            AuthManager.OnLoginReady += HandleAuthLoginReadyForRealtime;
            Log("Auth login ready event bound. source=" + SafeText(source));
        }

        private void UnbindAuthLoginReadyEvent()
        {
            AuthManager.OnLoginReady -= HandleAuthLoginReadyForRealtime;
        }

        private void HandleAuthLoginReadyForRealtime(AuthUserDto user)
        {
            Log("Auth login ready received. Realtime auto connect is disabled. User must press Connect Realtime.");
            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();
        }

        private async void TryStartAutoConnectRealtimeAfterAuth(string source, AuthUserDto user)
        {
            if (!autoConnectRealtimeAfterAuthLogin) return;
            if (!isActiveAndEnabled) return;
            if (isAutoConnectRealtimeAfterAuthRunning) return;
            if (isConnectAndAuthRunning || isRealtimeReconnectRunning || isCleaningUp) return;

            AuthManager authManager = AuthManager.Instance;
            if (authManager == null || !authManager.isLogin || authManager.CurrentUser == null) return;

            if (autoConnectRealtimeOnlyWhenDisconnected && IsRealtimeReady())
            {
                if (autoListRoomsAfterAuthRealtimeConnect && !isJoined)
                {
                    Log("Realtime already ready after auth login. Refreshing room list. source=" + SafeText(source));
                    await ListRoomsAsync();
                }

                return;
            }

            isAutoConnectRealtimeAfterAuthRunning = true;

            try
            {
                float delay = Mathf.Max(0f, autoConnectRealtimeAfterAuthDelaySeconds);
                if (delay > 0f) await Task.Delay(Mathf.RoundToInt(delay * 1000f));
                if (!isActiveAndEnabled) return;

                Log("Auto realtime connect started after auth login. source=" + SafeText(source) + " | user=" + (user != null ? SafeText(user.emailOrUsername) : "empty"));

                ShowServerDebugPanelForRealtimeProgress(
                    realtimeConnectPreparingMessage,
                    "REALTIME_AUTO_CONNECT_AFTER_AUTH_LOGIN",
                    "source=" + SafeText(source),
                    true
                );

                bool ok = await LoginCheckConnectAndAuthAsync();

                if (!ok)
                {
                    Log("Auto realtime connect after auth login failed. source=" + SafeText(source));
                    ShowServerDebugPanelForRealtimeConnectFailure("auto_connect_after_auth_login_failed:" + SafeText(source));
                    return;
                }

                Log("Auto realtime connect after auth login succeeded. source=" + SafeText(source));

                if (autoListRoomsAfterAuthRealtimeConnect && !isJoined)
                {
                    Log("Room list refresh requested after auth login realtime connect. source=" + SafeText(source));
                    await ListRoomsAsync();
                }
            }
            catch (Exception ex)
            {
                Log("Auto realtime connect after auth login exception: " + ex.Message);
                ShowServerDebugPanelForRealtimeConnectFailure("auto_connect_after_auth_login_exception:" + ex.Message);
            }
            finally
            {
                isAutoConnectRealtimeAfterAuthRunning = false;
                UpdateConnectionButtons();
                UpdateCreateRoomButton();
                UpdateSendMessageButton();
            }
        }


        //* این تابع تنها زمانی CheckNet را اجرا می کند که حلقه Reconnect مالک بررسی شبکه نباشد.
        private void CheckNetFastReconnectWatch()
        {
            if (!enableCheckNetFastReconnectWatch) return;
            if (AuthManager.Instance == null) return;
            if (isCleaningUp || isUserRequestedExitFlow) return;
            if (checkNetFastWatchRunning) return;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (singleCheckNetFailureTestRunning) return;
#endif

            // هنگام اجرای Reconnect، فقط خود حلقه Reconnect مالک CheckNet است.
            if (isRealtimeReconnectRunning) return;

            bool clientConnected = realtimeClient != null && realtimeClient.IsConnected;
            bool authReady = realtimeAuthClient != null && realtimeAuthClient.IsAuthenticated;

            bool normalConnectOrAuthIsRunning =
                isConnectAndAuthRunning ||
                isAutoConnectRealtimeAfterAuthRunning ||
                (clientConnected && !authReady && !checkNetFastOutageActive);

            if (normalConnectOrAuthIsRunning)
            {
                checkNetFastConsecutiveFailures = 0;
                checkNetFastOutageActive = false;
                checkNetFastReconnectKickRequested = false;
                return;
            }

            bool realtimeLobbyOnlyReady =
                IsRealtimeReady() &&
                !isJoined &&
                !IsDedicatedGameServerConnectedAndAuthenticated() &&
                Application.internetReachability !=
                NetworkReachability.NotReachable;

            if (realtimeLobbyOnlyReady)
            {
                checkNetFastConsecutiveFailures = 0;
                checkNetFastOutageActive = false;
                immediateInternetLostHandled = false;
                checkNetFastReconnectKickRequested = false;
                return;
            }

            bool shouldWatch =
                IsRealtimeReady() ||
                isJoined ||
                checkNetFastOutageActive;

            if (!shouldWatch)
            {
                checkNetFastConsecutiveFailures = 0;
                checkNetFastOutageActive = false;
                checkNetFastReconnectKickRequested = false;
                return;
            }

            float now = Time.realtimeSinceStartup;
            float interval = Mathf.Clamp(checkNetFastWatchIntervalSeconds, 0.5f, 3f);

            if (now < nextCheckNetFastWatchAt) return;

            nextCheckNetFastWatchAt = now + interval;

            if (!checkNetFastOutageActive && IsDedicatedGameServerProvingNetworkIsAlive(false))
            {
                checkNetFastConsecutiveFailures = 0;
                checkNetFastReconnectKickRequested = false;
                return;
            }

            _ = RunCheckNetFastReconnectWatchAsync();
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        //* این تابع آرگومان تست را فقط یک بار می خواند و بعد از آماده شدن کامل Realtime و Dedicated تست را شروع می کند.
        private void ProcessSingleCheckNetFailureValidationTest()
        {
            if (!networkValidationCommandLineChecked)
            {
                networkValidationCommandLineChecked = true;

                string[] commandLineArgs = Environment.GetCommandLineArgs();
                for (int i = 0; i < commandLineArgs.Length; i++)
                {
                    if (!string.Equals(
                            commandLineArgs[i],
                            SingleCheckNetFailureTestArgument,
                            StringComparison.OrdinalIgnoreCase
                        ))
                    {
                        continue;
                    }

                    singleCheckNetFailureTestArmed = true;

                    Log(
                        "[NETWORK_VALIDATION] SINGLE_CHECKNET_FAILURE_ARMED | " +
                        "waitingForRealtimeRoomAndDedicated=True"
                    );

                    break;
                }

#if UNITY_WEBGL && !UNITY_EDITOR
                if (!singleCheckNetFailureTestArmed)
                {
                    string absoluteUrl = Application.absoluteURL;
                    bool requestedByUrl =
                        !string.IsNullOrWhiteSpace(absoluteUrl) &&
                        absoluteUrl.IndexOf(
                            "networkTestSingleCheckNetFailure=1",
                            StringComparison.OrdinalIgnoreCase
                        ) >= 0;

                    if (requestedByUrl)
                    {
                        singleCheckNetFailureTestArmed = true;

                        Log(
                            "[NETWORK_VALIDATION] SINGLE_CHECKNET_FAILURE_ARMED | " +
                            "waitingForRealtimeRoomAndDedicated=True | source=webgl_url"
                        );
                    }
                }
#endif
            }

            if (!singleCheckNetFailureTestArmed) return;
            if (singleCheckNetFailureTestRunning) return;
            if (checkNetFastWatchRunning || isRealtimeReconnectRunning) return;
            if (!IsRealtimeReady() || !isJoined) return;
            if (!IsDedicatedGameServerConnectedAndAuthenticated()) return;
            if (!IsDedicatedGameServerProvingNetworkIsAlive(false)) return;
            if (Application.internetReachability == NetworkReachability.NotReachable) return;

            singleCheckNetFailureTestArmed = false;
            singleCheckNetFailureTestRunning = true;
            _ = RunSingleCheckNetFailureValidationAsync();
        }

        //* این تابع یک نتیجه ناموفق CheckNet را بدون قطع Transport تزریق می کند و نتیجه را با وضعیت واقعی اتصال می سنجد.
        private async Task RunSingleCheckNetFailureValidationAsync()
        {
            int generationBeforeTest = connectionGenerationId;
            int reconnectAttemptBeforeTest = realtimeReconnectAttemptCount;
            string roomIdBeforeTest = activeRoomId;
            CancellationToken cancellationToken =
                lifecycleCts != null
                    ? lifecycleCts.Token
                    : CancellationToken.None;

            checkNetFastConsecutiveFailures = 0;
            forceNextCheckNetFailureForValidation = true;

            Log(
                "[NETWORK_VALIDATION] SINGLE_CHECKNET_FAILURE_START | " +
                "realtimeReady=" +
                IsRealtimeReady() +
                " | joined=" +
                isJoined +
                " | roomId=" +
                SafeText(roomIdBeforeTest) +
                " | dedicatedConnected=" +
                IsDedicatedGameServerConnectedAndAuthenticated() +
                " | localNetworkUnavailable=" +
                (Application.internetReachability == NetworkReachability.NotReachable) +
                " | generation=" +
                generationBeforeTest
            );

            try
            {
                await RunCheckNetFastReconnectWatchAsync();

                int failuresAfterInjection = checkNetFastConsecutiveFailures;

                await Task.Delay(
                    SingleCheckNetFailureValidationWindowMs,
                    cancellationToken
                );

                bool realtimeStillReady = IsRealtimeReady();
                bool dedicatedStillReady =
                    IsDedicatedGameServerConnectedAndAuthenticated();
                bool sameRoom =
                    isJoined &&
                    string.Equals(
                        activeRoomId,
                        roomIdBeforeTest,
                        StringComparison.Ordinal
                    );
                bool reconnectDidNotStart =
                    !isRealtimeReconnectRunning &&
                    realtimeReconnectAttemptCount == reconnectAttemptBeforeTest &&
                    connectionGenerationId == generationBeforeTest;
                bool noOutageStateWasCreated =
                    !checkNetFastOutageActive &&
                    !immediateInternetLostHandled &&
                    !checkNetFastReconnectKickRequested;
                bool passed =
                    failuresAfterInjection == 1 &&
                    realtimeStillReady &&
                    dedicatedStillReady &&
                    sameRoom &&
                    reconnectDidNotStart &&
                    noOutageStateWasCreated;

                Log(
                    "[NETWORK_VALIDATION] SINGLE_CHECKNET_FAILURE_RESULT=" +
                    (passed ? "PASS" : "FAIL") +
                    " | failuresAfterInjection=" +
                    failuresAfterInjection +
                    " | realtimeReady=" +
                    realtimeStillReady +
                    " | dedicatedConnected=" +
                    dedicatedStillReady +
                    " | sameRoom=" +
                    sameRoom +
                    " | reconnectDidNotStart=" +
                    reconnectDidNotStart +
                    " | noOutageState=" +
                    noOutageStateWasCreated +
                    " | generationBefore=" +
                    generationBeforeTest +
                    " | generationAfter=" +
                    connectionGenerationId +
                    " | reconnectAttemptBefore=" +
                    reconnectAttemptBeforeTest +
                    " | reconnectAttemptAfter=" +
                    realtimeReconnectAttemptCount
                );
            }
            catch (OperationCanceledException)
            {
                Log(
                    "[NETWORK_VALIDATION] SINGLE_CHECKNET_FAILURE_RESULT=CANCELLED | " +
                    "reason=lifecycle_cancelled"
                );
            }
            catch (Exception ex)
            {
                Log(
                    "[NETWORK_VALIDATION] SINGLE_CHECKNET_FAILURE_RESULT=FAIL | " +
                    "exception=" +
                    SafeText(ex.Message)
                );
            }
            finally
            {
                forceNextCheckNetFailureForValidation = false;
                checkNetFastConsecutiveFailures = 0;
                singleCheckNetFailureTestRunning = false;
            }
        }
#endif

        private bool IsDedicatedGameServerConnectedAndAuthenticated()
        {
            Network_A.DedicatedGameServer.Client.DedicatedGameServerWsClient dedicatedClient =
                Network_A.DedicatedGameServer.Client.DedicatedGameServerWsClient.Instance;

            return dedicatedClient != null && dedicatedClient.IsConnected && dedicatedClient.IsAuthenticated;
        }

        private bool IsDedicatedGameServerProvingNetworkIsAlive()
        {
            return IsDedicatedGameServerProvingNetworkIsAlive(true);
        }

        private bool IsDedicatedGameServerProvingNetworkIsAlive(bool writeLog)
        {
            Network_A.DedicatedGameServer.Client.DedicatedGameServerWsClient dedicatedClient =
                Network_A.DedicatedGameServer.Client.DedicatedGameServerWsClient.Instance;

            if (dedicatedClient == null) return false;
            if (!dedicatedClient.IsConnected || !dedicatedClient.IsAuthenticated) return false;

            float proofSeconds = Mathf.Clamp(dedicatedGameServerInboundAliveProofSeconds, 1.25f, 6f);
            bool hasRecentInbound = dedicatedClient.HasRecentInboundMessage(proofSeconds);
            if (!hasRecentInbound) return false;

            if (writeLog)
            {
                Log("Dedicated Game Server proves network is alive. CheckNet fast failure ignored. lastRoute=" +
                    SafeText(dedicatedClient.LastInboundRoute) + " | proofSeconds=" + proofSeconds.ToString("F2"));
            }

            return true;
        }

        private async Task RunCheckNetFastReconnectWatchAsync()
        {
            checkNetFastWatchRunning = true;

            int watchGenerationId = connectionGenerationId;

            bool wasNormalConnectRunningAtStart =
                isConnectAndAuthRunning ||
                isAutoConnectRealtimeAfterAuthRunning ||
                (realtimeClient != null &&
                 realtimeClient.IsConnected &&
                 realtimeAuthClient != null &&
                 !realtimeAuthClient.IsAuthenticated &&
                 !isRealtimeReconnectRunning);

            try
            {
                bool serverReachable = AuthManager.Instance != null &&
                                       await WebGLCheckNetFastWrapper.CheckNetFastSilentAsync(
                                           AuthManager.Instance,
                                           checkNetFastTimeoutMs
                                       );
                bool forcedSingleFailureForValidation = false;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (forceNextCheckNetFailureForValidation)
                {
                    forceNextCheckNetFailureForValidation = false;
                    forcedSingleFailureForValidation = true;
                    serverReachable = false;

                    Log(
                        "[NETWORK_VALIDATION] FORCED_SINGLE_CHECKNET_FAILURE_INJECTED | " +
                        "realtimeReady=" +
                        IsRealtimeReady() +
                        " | joined=" +
                        isJoined +
                        " | dedicatedConnected=" +
                        IsDedicatedGameServerConnectedAndAuthenticated() +
                        " | localNetworkUnavailable=" +
                        (Application.internetReachability == NetworkReachability.NotReachable)
                    );
                }
#endif

                bool generationChangedAfterCheck = watchGenerationId != connectionGenerationId;

                if (serverReachable)
                {
                    bool dedicatedRecoveryMissingAfterShortOutage =
                        dedicatedGameServerPresenceGuardActive &&
                        isJoined &&
                        !IsDedicatedGameServerConnectedAndAuthenticated();

                    if (checkNetFastOutageActive || immediateInternetLostHandled)
                    {
                        Log("CheckNet fast says server is reachable again. Reconnect kick requested.");

                        checkNetFastOutageActive = false;
                        checkNetFastConsecutiveFailures = 0;
                        immediateInternetLostHandled = false;
                        checkNetFastReconnectKickRequested = true;

                        if (!isRealtimeReconnectRunning &&
                            (!IsRealtimeReady() ||
                             (dedicatedRecoveryMissingAfterShortOutage &&
                              !dedicatedRecoveryFreshRealtimeDelivered)))
                        {
                            StartRealtimeReconnectLoop(
                                dedicatedRecoveryMissingAfterShortOutage
                                    ? "checknet_fast_recovered_dedicated_not_authenticated"
                                    : "checknet_fast_recovered_without_running_loop"
                            );
                        }
                    }
                    else
                    {
                        checkNetFastConsecutiveFailures = 0;
                        ClearCheckNetFastWarningOnlyPanelIfActive("server_reachable_without_reconnect");
                    }

                    // A short outage can close only the Dedicated socket while the
                    // Realtime client still reports Ready. The Dedicated binder then
                    // waits for a fresh Realtime inbound signal and cannot reconnect by
                    // itself. Force the Realtime recovery loop in that exact state even
                    // when CheckNet has already recovered and no outage flag remains.
                    if (!isRealtimeReconnectRunning &&
                        dedicatedRecoveryMissingAfterShortOutage &&
                        !dedicatedRecoveryFreshRealtimeDelivered)
                    {
                        Log(
                            "Dedicated recovery is waiting for fresh Realtime inbound while server is reachable. " +
                            "Forcing Realtime reconnect. joined=" +
                            isJoined +
                            " | realtimeReady=" +
                            IsRealtimeReady()
                        );

                        StartRealtimeReconnectLoop(
                            "server_reachable_dedicated_waiting_fresh_realtime"
                        );
                    }

                    return;
                }

                bool localNetworkUnavailableAfterCheck =
                    Application.internetReachability ==
                    NetworkReachability.NotReachable;
                bool realtimeLobbyOnlyReadyAfterCheck =
                    IsRealtimeReady() &&
                    !isJoined &&
                    !IsDedicatedGameServerConnectedAndAuthenticated();

                if (realtimeLobbyOnlyReadyAfterCheck &&
                    !localNetworkUnavailableAfterCheck)
                {
                    Log(
                        "CheckNet fast failed result ignored because realtime lobby transport is still ready and no room or dedicated session is active. " +
                        "watchGenerationId=" +
                        watchGenerationId +
                        " | currentGenerationId=" +
                        connectionGenerationId +
                        " | realtimeReady=" +
                        IsRealtimeReady() +
                        " | joined=" +
                        isJoined +
                        " | dedicatedConnected=" +
                        IsDedicatedGameServerConnectedAndAuthenticated() +
                        " | localNetworkUnavailable=" +
                        localNetworkUnavailableAfterCheck
                    );

                    checkNetFastConsecutiveFailures = 0;
                    checkNetFastOutageActive = false;
                    immediateInternetLostHandled = false;
                    checkNetFastReconnectKickRequested = false;
                    ClearCheckNetFastWarningOnlyPanelIfActive("lobby_only_ready");
                    return;
                }

                if (wasNormalConnectRunningAtStart || (generationChangedAfterCheck && IsRealtimeReady()))
                {
                    Log("CheckNet fast failed result ignored because realtime connect/auth completed or connection generation changed. watchGenerationId="
                        + watchGenerationId + " | currentGenerationId=" + connectionGenerationId
                        + " | ready=" + IsRealtimeReady());

                    checkNetFastConsecutiveFailures = 0;
                    checkNetFastOutageActive = false;
                    checkNetFastReconnectKickRequested = false;
                    ClearCheckNetFastWarningOnlyPanelIfActive("connect_generation_changed");
                    return;
                }

                if (isConnectAndAuthRunning && !isRealtimeReconnectRunning)
                {
                    Log("CheckNet fast failed result ignored because normal Connect/Auth is running.");
                    checkNetFastConsecutiveFailures = 0;
                    ClearCheckNetFastWarningOnlyPanelIfActive("normal_connect_running");
                    return;
                }

                if (!forcedSingleFailureForValidation &&
                    IsDedicatedGameServerProvingNetworkIsAlive())
                {
                    checkNetFastConsecutiveFailures = 0;
                    checkNetFastOutageActive = false;
                    immediateInternetLostHandled = false;
                    checkNetFastReconnectKickRequested = false;
                    ClearCheckNetFastWarningOnlyPanelIfActive("dedicated_inbound_alive");
                    return;
                }

                checkNetFastConsecutiveFailures++;

                bool realtimeReady = IsRealtimeReady();
                bool dedicatedGameServerConnected =
                    IsDedicatedGameServerConnectedAndAuthenticated();
                bool localNetworkUnavailable =
                    Application.internetReachability ==
                    NetworkReachability.NotReachable;
                bool bothLiveTransportsUnavailable =
                    !realtimeReady &&
                    !dedicatedGameServerConnected;
                bool singleFailureHasIndependentConfirmation =
                    useSingleCheckNetFailureInsideDedicatedGameServer &&
                    (localNetworkUnavailable || bothLiveTransportsUnavailable);

                Log("CheckNet fast failed. failures=" + checkNetFastConsecutiveFailures +
                    " | realtimeReady=" + realtimeReady +
                    " | reconnectRunning=" + isRealtimeReconnectRunning +
                    " | joined=" + isJoined +
                    " | dedicatedConnected=" + dedicatedGameServerConnected +
                    " | localNetworkUnavailable=" + localNetworkUnavailable +
                    " | bothLiveTransportsUnavailable=" + bothLiveTransportsUnavailable +
                    " | forcedSingleFailureTest=" + forcedSingleFailureForValidation +
                    " | singleFailureConfirmed=" + singleFailureHasIndependentConfirmation);

                int failuresNeeded = singleFailureHasIndependentConfirmation
                    ? 1
                    : Mathf.Clamp(checkNetFastFailuresBeforeDisconnect, 2, 4);

                if (checkNetFastConsecutiveFailures < failuresNeeded) return;

                bool realtimeRoomStillJoinedWithoutOutageProof =
                    realtimeReady &&
                    isJoined &&
                    !localNetworkUnavailable &&
                    !bothLiveTransportsUnavailable &&
                    !singleFailureHasIndependentConfirmation &&
                    !(
                        dedicatedGameServerPresenceGuardActive &&
                        !dedicatedGameServerConnected
                    );

                if (!forcedSingleFailureForValidation &&
                    realtimeRoomStillJoinedWithoutOutageProof)
                {
                    ShowCheckNetFastWarningOnlyPanel(
                        "checknet_fast_warning_without_transport_drop"
                    );

                    Log("CheckNet fast failure threshold ignored because realtime room is still joined and no independent outage confirmation exists. failures="
                        + checkNetFastConsecutiveFailures +
                        " | realtimeReady=" + realtimeReady +
                        " | joined=" + isJoined +
                        " | dedicatedConnected=" + dedicatedGameServerConnected +
                        " | localNetworkUnavailable=" + localNetworkUnavailable +
                        " | bothLiveTransportsUnavailable=" + bothLiveTransportsUnavailable);

                    checkNetFastConsecutiveFailures = 0;
                    checkNetFastOutageActive = false;
                    immediateInternetLostHandled = false;
                    checkNetFastReconnectKickRequested = false;
                    return;
                }

                if (!forcedSingleFailureForValidation &&
                    IsDedicatedGameServerProvingNetworkIsAlive())
                {
                    checkNetFastConsecutiveFailures = 0;
                    checkNetFastOutageActive = false;
                    immediateInternetLostHandled = false;
                    checkNetFastReconnectKickRequested = false;
                    ClearCheckNetFastWarningOnlyPanelIfActive("dedicated_inbound_alive_after_threshold");
                    return;
                }

                bool wasOutageActive = checkNetFastOutageActive;

                checkNetFastOutageActive = true;
                immediateInternetLostHandled = true;
                checkNetFastReconnectKickRequested = false;
                checkNetFastWarningOnlyPanelActive = false;

                if (!isRealtimeReconnectRunning)
                {
                    MarkRealtimeDisconnectedByTransport("checknet_fast_server_unreachable");
                    return;
                }

                ShowServerDebugPanelForInternetLost("checknet_fast_server_unreachable_while_reconnecting");

                if (!wasOutageActive)
                {
                    OnRealtimeConnectionLostForReconnectFor3D?.Invoke("checknet_fast_server_unreachable_while_reconnecting");
                }
            }
            catch (Exception ex)
            {
                Log("CheckNet fast reconnect watch warning: " + ex.Message);
            }
            finally
            {
                checkNetFastWatchRunning = false;
            }
        }

        private void RestartRealtimeReconnectLoopImmediately(string reason)
        {
            string safeReason = string.IsNullOrWhiteSpace(reason) ? "checknet_fast_recovered" : reason.Trim();

            Log("Restarting realtime reconnect loop immediately. reason=" + SafeText(safeReason));

            StopRealtimeReconnectLoop(safeReason + ":restart_before_immediate_attempt");

            StartRealtimeReconnectLoop(safeReason);
        }

    }
}
