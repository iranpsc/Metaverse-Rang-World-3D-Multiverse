using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Bootstrap;
using Network_A.Core;
using Network_A.DedicatedGameServer.Client;
using Network_A.GameServer;
using Network_A.Lobby.Buildings;
using Network_A.Realtime.Auth;
using Network_A.Realtime.Core;
using Network_A.Realtime.Lobby;
using Network_A.Realtime.Protocol;
using Network_A.Realtime.Stability;
using Network_A.Realtime.Transport;
using Network_A.UI;
using UnityEngine;

namespace Network_A.Realtime.Controllers
{
    [DefaultExecutionOrder(-7500)]
    public sealed class RealtimeRoomGameServerManager : MonoBehaviour
    {
        #region وضعیت‌ها و رویدادها

        public enum FlowState
        {
            Idle,
            WaitingForNetwork,
            WaitingForAuthentication,
            Connecting,
            Connected,
            RefreshingToken,
            Authenticating,
            Ready,
            LoadingBuildings,
            LobbyReady,
            ResolvingRoom,
            JoiningRoom,
            RoomJoined,
            Reconnecting,
            Disconnecting,
            Failed
        }

        private const string RealtimeProgressMessageId = "GLOBAL_REALTIME_PROGRESS";
        private const string RealtimeErrorMessageId = "GLOBAL_REALTIME_ERROR";
        private const string BuildingsProgressMessageId = "GLOBAL_BUILDINGS_PROGRESS";
        private const string BuildingsErrorMessageId = "GLOBAL_BUILDINGS_ERROR";
        private const string RoomProgressMessageId = "GLOBAL_BUILDING_ROOM_PROGRESS";
        private const string RoomErrorMessageId = "GLOBAL_BUILDING_ROOM_ERROR";

        #region لابی عمومی سه بعدی

        private const string PublicLobbyProgressMessageId = "GLOBAL_PUBLIC_LOBBY_PROGRESS";
        private const string PublicLobbyErrorMessageId = "GLOBAL_PUBLIC_LOBBY_ERROR";
        private const string PublicLobbyRoomIdValue = "room_public_lobby_main";
        private const string PublicLobbyRoomNameValue = "Main Public Lobby";

        #endregion

        public static RealtimeRoomGameServerManager Instance { get; private set; }
        public static FlowState CurrentState { get; private set; } = FlowState.Idle;
        public static event Action<FlowState> OnStateChanged;
        public static event Action OnRealtimeReady;
        public static event Action<IReadOnlyList<CompletedBuildingDto>> OnBuildingsUpdated;
        public static event Action<string> OnRoomJoinedFor3D;
        public static event Action<string> OnRoomLeftFor3D;
        public static event Action<string> OnRealtimeDisconnected;
        public static event Action<string> OnRealtimeReconnectFailedPermanently;
        public event Action<RealtimeEnvelope> RealtimeEnvelopeReceived;

        public bool IsRealtimeConnected => realtimeClient != null && realtimeClient.IsConnected;
        public bool IsRealtimeAuthenticated => realtimeAuthClient != null && realtimeAuthClient.IsAuthenticated;
        public bool IsRealtimeReady => IsRealtimeConnected && IsRealtimeAuthenticated;
        public bool HasLobbyEntryRequested => hasLobbyEntryRequested;
        public IReadOnlyList<CompletedBuildingDto> CompletedBuildings => completedBuildings;
        public string RealtimeConnectionId => realtimeAuthClient != null ? realtimeAuthClient.ConnectionId : string.Empty;
        public string RealtimeUserId => realtimeAuthClient != null ? realtimeAuthClient.UserId : string.Empty;
        public bool IsJoinedRoom => isJoinedRoom;

        #region لابی عمومی سه بعدی

        public string PublicLobbyRoomId => PublicLobbyRoomIdValue;
        public string PublicLobbyRoomName => PublicLobbyRoomNameValue;
        public bool IsInsidePublicLobbyRoom => isJoinedRoom && string.Equals(currentRoomId, PublicLobbyRoomIdValue, StringComparison.Ordinal);
        public bool IsSwitchingFromPublicLobbyToBuildingRoom => isSwitchingFromPublicLobbyToBuildingRoom;
        public int CurrentRoomMaxPlayers => IsInsidePublicLobbyRoom ? Mathf.Clamp(publicLobbyRoomMaxPlayers, 1, 100) : Mathf.Clamp(buildingRoomMaxPlayers, 1, 100);

        #endregion
        public bool IsRoomExitInProgress => isRoomExitInProgress;
        public string CurrentRoomId => currentRoomId;
        public string CurrentRoomName => currentRoomName;
        public string SelectedBuildingWidth => selectedBuildingWidth;
        public string SelectedBuildingLength => selectedBuildingLength;
        public string SelectedBuildingDensity => selectedBuildingDensity;
        public bool CanSendRoomPlayerAction => IsRealtimeReady && IsJoinedRoom && gameServerClient != null && gameServerClient.HasRoom;
        public bool IsRecoveryRunning => unifiedRecoveryRunning;
        public bool IsRealtimeRecoveryPhaseRunning => unifiedRecoveryRunning && (!IsRealtimeReady || (recoveryRequiresDedicatedGameServer && !isJoinedRoom));
        public bool RecoveryRequiresDedicatedGameServer => recoveryRequiresDedicatedGameServer;
        public bool IsPermanentRecoveryFailureWaitingForLobby => permanentRecoveryFailureWaitingForLobby;
        public float RecoveryElapsedSeconds => unifiedRecoveryRunning ? Mathf.Max(0f, Time.realtimeSinceStartup - unifiedRecoveryStartedAt) : 0f;
        public float RecoveryRemainingSeconds => unifiedRecoveryRunning ? Mathf.Max(0f, unifiedRecoveryDeadlineAt - Time.realtimeSinceStartup) : 0f;
        public float ServerSessionReconnectGraceSeconds => Mathf.Max(30f, serverSessionReconnectGraceSeconds);
        public float ClientReconnectAttemptTimeoutSeconds => GetPermanentReconnectFailureTimeoutSeconds();

        #endregion

        #region تنظیمات

        [Header("Realtime Connection")]
        [SerializeField, Min(1000)] private int connectTimeoutMs = 10000;
        [SerializeField, Min(1000)] private int sendTimeoutMs = 10000;
        [SerializeField, Min(1000)] private int authAckTimeoutMs = 15000;
        [SerializeField, Range(0, 3600)] private int accessTokenRefreshSkewSeconds = 60;
        [SerializeField] private bool logIncomingRealtimeMessages;
        [SerializeField] private bool logOutgoingRealtimeMessages;

        [Header("Heartbeat")]
        [SerializeField] private bool enableHeartbeat = true;
        [SerializeField, Min(500)] private int heartbeatPingIntervalMs = 3000;
        [SerializeField, Min(500)] private int heartbeatPongTimeoutMs = 3000;
        [SerializeField, Min(1)] private int heartbeatMaximumMissedPongs = 3;

        [Header("Reconnect")]
        [SerializeField] private bool enableAutomaticReconnect = true;
        [SerializeField, Min(1)] private int reconnectMaximumAttempts = 10;
        [SerializeField, Min(0)] private int reconnectInitialDelayMs = 1000;
        [SerializeField, Min(0)] private int reconnectMaximumDelayMs = 8000;
        [SerializeField, Min(1000)] private int reconnectTotalTimeoutMs = 180000;
        [SerializeField, Min(1f)] private float reconnectDelayMultiplier = 2f;

        [Header("210 Second Recovery Contract")]
        [SerializeField, Min(30f)] private float serverSessionReconnectGraceSeconds = 210f;
        [SerializeField, Min(5f)] private float permanentReconnectFailureTimeoutSeconds = 180f;

        [Header("Completed Buildings")]
        [SerializeField, Min(1000)] private int buildingsRequestTimeoutMs = 15000;
        [SerializeField, Min(1)] private int buildingsMaximumPages = 100;
        [SerializeField] private bool loadBuildingsAfterRealtimeAuthentication = true;

        #region لابی عمومی سه بعدی

        [Header("Public 3D Lobby")]
        [SerializeField] private bool joinPublicLobbyAutomatically = true;
        [SerializeField, Range(1, 100)] private int publicLobbyRoomMaxPlayers = 100;

        #endregion

        [Header("Building Room")]
        [SerializeField, Range(1, 100)] private int buildingRoomMaxPlayers = 50;
        [SerializeField, Min(1000)] private int reliableAckTimeoutMs = 5000;
        [SerializeField] private bool autoRejoinRoomAfterRealtimeReconnect = true;

        #endregion

        #region منابع داخلی

        private readonly SemaphoreSlim connectionGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim buildingsGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim roomGate = new SemaphoreSlim(1, 1);
        private Task<bool> activeBuildingsRefreshTask;
        private readonly List<CompletedBuildingDto> completedBuildings = new List<CompletedBuildingDto>();

        private CancellationTokenSource lifecycleCts;
        private RealtimeClient realtimeClient;
        private RealtimeAuthClient realtimeAuthClient;
        private RealtimeLobbyClient realtimeLobbyClient;
        private GameServerClient gameServerClient;
        private RealtimeHeartbeat heartbeat;
        private RealtimeReconnect reconnect;
        private TaskCompletionSource<bool> authWaiter;
        private RealtimeError lastAuthenticationError;
        private bool eventsBound;
        private bool hasLobbyEntryRequested;
        private bool hasReachedRealtimeReady;
        private bool suppressDisconnectHandling;
        private bool applicationIsQuitting;
        private bool isJoinedRoom;
        private bool isRoomExitInProgress;

        #region لابی عمومی سه بعدی

        private bool isSwitchingFromPublicLobbyToBuildingRoom;

        #endregion
        private bool shouldRejoinRoomAfterReconnect;
        private bool unifiedRecoveryRunning;
        private bool recoveryRequiresDedicatedGameServer;
        private bool permanentRecoveryFailureWaitingForLobby;
        private float unifiedRecoveryStartedAt = -1f;
        private float unifiedRecoveryDeadlineAt = -1f;
        private string unifiedRecoveryReason = string.Empty;
        private Coroutine unifiedRecoveryTimeoutCoroutine;
        private CompletedBuildingDto selectedBuilding;
        private string selectedBuildingWidth = string.Empty;
        private string selectedBuildingLength = string.Empty;
        private string selectedBuildingDensity = string.Empty;
        private string currentRoomId = string.Empty;
        private string currentRoomName = string.Empty;
        private string lastRoomFlowError = string.Empty;

        #endregion

        #region چرخه حیات

        //* این تابع مقدارهای سراسری باقی‌مانده از اجرای قبلی را پیش از بارگذاری صحنه پاک می‌کند.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance = null;
            CurrentState = FlowState.Idle;
            OnStateChanged = null;
            OnRealtimeReady = null;
            OnBuildingsUpdated = null;
            OnRoomJoinedFor3D = null;
            OnRoomLeftFor3D = null;
            OnRealtimeDisconnected = null;
            OnRealtimeReconnectFailedPermanently = null;
        }

        //* این تابع تنها نمونه مدیر ریل‌تایم را آماده می‌کند و آن را هنگام تغییر صحنه نگه می‌دارد.
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                NetworkFileLogger.Warning("REALTIME_MANAGER", "نمونه تکراری مدیر ریل‌تایم حذف شد.");
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            lifecycleCts = new CancellationTokenSource();
            ConfigureReconnectController();
            BindGlobalEvents();
            SetState(FlowState.Idle, "initialized");
            NetworkFileLogger.Info("REALTIME_MANAGER", "مدیر ریل‌تایم، روم و گیم سرور آماده شد.");
        }

        //* این تابع بسته‌شدن برنامه را ثبت می‌کند تا رویداد قطع اتصال باعث شروع بازاتصال نشود.
        private void OnApplicationQuit()
        {
            applicationIsQuitting = true;
            CancelUnifiedRecovery("application_quit", false);
        }

        //* این تابع هنگام نابودی نمونه اصلی، همه رویدادها، اتصال‌ها و منابع لغو را آزاد می‌کند.
        private void OnDestroy()
        {
            if (Instance != this) return;

            applicationIsQuitting = true;
            CancelUnifiedRecovery("manager_destroyed", false);
            UnbindGlobalEvents();
            reconnect?.Dispose();
            reconnect = null;
            heartbeat?.Dispose();
            heartbeat = null;
            CleanupClientObjects();

            if (lifecycleCts != null)
            {
                if (!lifecycleCts.IsCancellationRequested) lifecycleCts.Cancel();
                lifecycleCts.Dispose();
                lifecycleCts = null;
            }

            RealtimeEnvelopeReceived = null;
            Instance = null;
        }

        #endregion

        #region ورود به لابی

        //* این تابع جریان کامل ورود به لابی شامل اتصال، احراز هویت و دریافت ساختمان‌ها را اجرا می‌کند.
        public async Task<bool> EnterLobbyAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            permanentRecoveryFailureWaitingForLobby = false;
            hasLobbyEntryRequested = true;
            NetworkFileLogger.Info("REALTIME_PERMANENT_RECOVERY", "Fresh Lobby 1 entry accepted; permanent recovery block cleared.");

            bool ready = await EnsureRealtimeReadyAsync(false, cancellationToken);
            if (!ready) return false;

            #region لابی عمومی سه بعدی

            if (joinPublicLobbyAutomatically)
            {
                bool publicLobbyJoined = await EnsurePublicLobbyJoinedAsync(cancellationToken);
                if (!publicLobbyJoined) return false;
            }

            #endregion

            if (!loadBuildingsAfterRealtimeAuthentication) return true;
            return await RefreshCompletedBuildingsAsync(cancellationToken);
        }

        #region لابی عمومی سه بعدی

        //* این تابع کاربر را با همان اتصال ریل تایم موجود وارد روم ثابت لابی عمومی می کند.
        public async Task<bool> EnsurePublicLobbyJoinedAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (IsInsidePublicLobbyRoom) return true;

            using (CancellationTokenSource linkedCts = CreateLinkedLifecycleToken(cancellationToken))
            {
                bool gateEntered = false;

                try
                {
                    await roomGate.WaitAsync(linkedCts.Token);
                    gateEntered = true;

                    if (IsInsidePublicLobbyRoom) return true;

                    if (isJoinedRoom)
                    {
                        ShowPublicLobbyFailure(
                            "ورود به لابی عمومی انجام نشد، چون کاربر هنوز داخل روم دیگری است.",
                            "public_lobby_join_blocked_by_existing_room | currentRoomId=" + currentRoomId,
                            false
                        );

                        return false;
                    }

                    if (!IsRealtimeReady)
                    {
                        bool ready = await EnsureRealtimeReadyAsync(false, linkedCts.Token);
                        if (!ready) return false;
                    }

                    if (gameServerClient == null)
                    {
                        ShowPublicLobbyFailure("کلاینت روم ریل تایم آماده نیست.", "game_server_client_missing", true);
                        return false;
                    }

                    currentRoomId = PublicLobbyRoomIdValue;
                    currentRoomName = PublicLobbyRoomNameValue;
                    selectedBuildingWidth = string.Empty;
                    selectedBuildingLength = string.Empty;
                    lastRoomFlowError = string.Empty;

                    SetState(FlowState.JoiningRoom, "public_lobby_join_started");
                    PublishPublicLobbyProgress(
                        "در حال ورود به لابی عمومی...",
                        "roomId=" + currentRoomId + " | roomName=" + currentRoomName + " | maxPlayers=" + Mathf.Clamp(publicLobbyRoomMaxPlayers, 1, 100)
                    );

                    RealtimeReliableSendResult joinResult = await gameServerClient.JoinRoomReliableAsync(currentRoomId, CreateReliableOptions(), linkedCts.Token);

                    if (joinResult == null || !joinResult.isSuccess)
                    {
                        lastRoomFlowError = "public_lobby_join_failed | roomId=" + currentRoomId + " | attempts=" + (joinResult != null ? joinResult.attempts : 0) + " | error=" + (joinResult != null ? joinResult.errorMessage : "result_null");
                        ClearFailedPublicLobbyJoinContext();
                        RestoreStateAfterRoomFailure("public_lobby_join_failed");
                        ShowPublicLobbyFailure("ورود به لابی عمومی انجام نشد. دوباره تلاش کنید.", lastRoomFlowError, true);
                        return false;
                    }

                    selectedBuilding = null;
                    isJoinedRoom = true;
                    shouldRejoinRoomAfterReconnect = true;
                    GlobalMessageManager.Clear(PublicLobbyProgressMessageId);
                    GlobalMessageManager.Clear(PublicLobbyErrorMessageId);
                    SetState(FlowState.RoomJoined, "public_lobby_join_succeeded");
                    NetworkFileLogger.Info("PUBLIC_LOBBY_JOINED", "roomId=" + currentRoomId + " | roomName=" + currentRoomName + " | maxPlayers=" + CurrentRoomMaxPlayers);
                    NotifyRoomJoined(currentRoomId);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    GlobalMessageManager.Clear(PublicLobbyProgressMessageId);
                    ClearFailedPublicLobbyJoinContext();
                    RestoreStateAfterRoomFailure("public_lobby_join_cancelled");
                    NetworkFileLogger.Warning("PUBLIC_LOBBY_FLOW", "ورود به لابی عمومی لغو شد.");
                    return false;
                }
                catch (Exception ex)
                {
                    GlobalMessageManager.Clear(PublicLobbyProgressMessageId);
                    ClearFailedPublicLobbyJoinContext();
                    RestoreStateAfterRoomFailure("public_lobby_join_exception");
                    NetworkFileLogger.Exception("PUBLIC_LOBBY_FLOW", ex);
                    ShowPublicLobbyFailure("ورود به لابی عمومی انجام نشد.", ex.ToString(), true);
                    return false;
                }
                finally
                {
                    if (gateEntered) roomGate.Release();
                }
            }
        }

        //* این تابع اتصال Dedicated لابی را می بندد و سپس فقط از روم عمومی خارج می شود تا ورود ساختمان ادامه پیدا کند.
        public async Task<bool> LeavePublicLobbyForBuildingSwitchAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!IsInsidePublicLobbyRoom) return true;

            if (isSwitchingFromPublicLobbyToBuildingRoom)
            {
                NetworkFileLogger.Warning("PUBLIC_LOBBY_SWITCH", "تعویض قبلی لابی عمومی به روم ساختمان هنوز فعال است.");
                return false;
            }

            isSwitchingFromPublicLobbyToBuildingRoom = true;

            try
            {
                DedicatedGameServerRealtimeRoomBinder dedicatedBinder = DedicatedGameServerRealtimeRoomBinder.Instance;

                if (dedicatedBinder != null)
                {
                    bool dedicatedDisconnected = await dedicatedBinder.DisconnectDedicatedForRoomSwitchAsync("public_lobby_to_building_room_switch");

                    if (!dedicatedDisconnected)
                    {
                        ShowPublicLobbyFailure(
                            "اتصال لابی عمومی هنوز کامل بسته نشده است. دوباره تلاش کنید.",
                            "public_lobby_dedicated_disconnect_failed_before_building_switch",
                            false
                        );

                        return false;
                    }
                }

                bool left = await LeaveCurrentRoomAfterDedicatedDisconnectAsync(false, cancellationToken);

                if (!left)
                {
                    ShowPublicLobbyFailure(
                        "خروج از لابی عمومی انجام نشد. دوباره تلاش کنید.",
                        "public_lobby_realtime_leave_failed_before_building_switch",
                        false
                    );

                    return false;
                }

                GlobalMessageManager.Clear(PublicLobbyProgressMessageId);
                GlobalMessageManager.Clear(PublicLobbyErrorMessageId);
                NetworkFileLogger.Info("PUBLIC_LOBBY_SWITCH", "خروج از لابی عمومی برای ورود به روم ساختمان کامل شد.");
                return true;
            }
            catch (OperationCanceledException)
            {
                NetworkFileLogger.Warning("PUBLIC_LOBBY_SWITCH", "تعویض لابی عمومی به روم ساختمان لغو شد.");
                return false;
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("PUBLIC_LOBBY_SWITCH", ex);
                ShowPublicLobbyFailure("تعویض لابی عمومی با روم ساختمان انجام نشد.", ex.ToString(), false);
                return false;
            }
            finally
            {
                isSwitchingFromPublicLobbyToBuildingRoom = false;
            }
        }

        //* این تابع پیام پیشرفت ورود به لابی عمومی را جدا از پیام روم ساختمان نمایش می دهد.
        private void PublishPublicLobbyProgress(string message, string technicalDetails)
        {
            if (!CanShowRealtimeOrRequestMessage()) return;

            GlobalMessageManager.Publish(
                PublicLobbyProgressMessageId,
                GlobalMessageManager.MessageSource.Realtime,
                GlobalMessageManager.MessageType.Information,
                GlobalMessageManager.Priorities.Reconnecting,
                "ورود به لابی عمومی",
                message,
                technicalDetails ?? string.Empty,
                0f,
                true,
                false
            );
        }

        //* این تابع خطای ورود یا خروج لابی عمومی را با شناسه مستقل نمایش می دهد.
        private void ShowPublicLobbyFailure(string userMessage, string technicalDetails, bool allowRetry)
        {
            GlobalMessageManager.Clear(PublicLobbyProgressMessageId);
            if (!CanShowRealtimeOrRequestMessage()) return;

            GlobalMessageManager.ShowError(
                PublicLobbyErrorMessageId,
                "لابی عمومی",
                userMessage,
                technicalDetails ?? string.Empty,
                0f,
                false,
                GlobalMessageManager.MessageSource.Realtime,
                allowRetry,
                allowRetry ? RetryPublicLobbyJoinAsync : null
            );
        }

        //* این تابع تلاش دوباره پیام لابی عمومی را به همان جریان Join ثابت متصل می کند.
        private async Task RetryPublicLobbyJoinAsync()
        {
            await EnsurePublicLobbyJoinedAsync();
        }

        //* این تابع فقط اطلاعات موقت Join ناموفق لابی عمومی را پاک می کند.
        private void ClearFailedPublicLobbyJoinContext()
        {
            isJoinedRoom = false;
            shouldRejoinRoomAfterReconnect = false;

            if (string.Equals(currentRoomId, PublicLobbyRoomIdValue, StringComparison.Ordinal))
            {
                currentRoomId = string.Empty;
                currentRoomName = string.Empty;
            }
        }

        #endregion

        //* این تابع اتصال و احراز هویت ریل‌تایم را در صورت نیاز آماده می‌کند و اتصال آماده قبلی را دوباره نمی‌سازد.
        public async Task<bool> EnsureRealtimeReadyAsync(bool reconnecting, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (IsRealtimeReady)
            {
                SetState(ResolveReadyState(), "existing_realtime_connection_reused");
                return true;
            }

            if (reconnecting && unifiedRecoveryRunning && !HasUnifiedRecoveryTimeRemaining())
            {
                FailUnifiedRecovery("recovery_deadline_reached_before_realtime_ready");
                return false;
            }

            using (CancellationTokenSource linkedCts = CreateLinkedLifecycleToken(cancellationToken, reconnecting))
            {
                bool gateEntered = false;

                try
                {
                    await connectionGate.WaitAsync(linkedCts.Token);
                    gateEntered = true;

                    if (IsRealtimeReady) return true;
                    if (!CanStartRealtimeFlow(out string blockedReason))
                    {
                        NetworkFileLogger.Warning("REALTIME_FLOW_BLOCKED", blockedReason);
                        return false;
                    }

                    return await ConnectAndAuthenticateInternalAsync(reconnecting, linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    if (reconnecting && unifiedRecoveryRunning && !HasUnifiedRecoveryTimeRemaining()) FailUnifiedRecovery("recovery_deadline_reached_during_realtime_flow");
                    else NetworkFileLogger.Warning("REALTIME_RECOVERY", "جریان اتصال ریل‌تایم لغو شد.");
                    return false;
                }
                finally
                {
                    if (gateEntered) connectionGate.Release();
                }
            }
        }

        //* این تابع همه درخواست‌های هم‌زمان دریافت ساختمان‌ها را روی یک تسک مشترک قرار می‌دهد تا درخواست و رندر تکراری ساخته نشود.
        public Task<bool> RefreshCompletedBuildingsAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (activeBuildingsRefreshTask != null && !activeBuildingsRefreshTask.IsCompleted)
            {
                NetworkFileLogger.Info("REALTIME_LOBBY_BUILDINGS", "Duplicate buildings refresh joined the active request.");
                return activeBuildingsRefreshTask;
            }

            activeBuildingsRefreshTask = RefreshCompletedBuildingsInternalAsync(cancellationToken);
            return activeBuildingsRefreshTask;
        }

        //* این تابع درخواست واقعی فهرست ساختمان‌ها را اجرا و فقط پس از موفقیت کامل داده‌ها را جایگزین می‌کند.
        private async Task<bool> RefreshCompletedBuildingsInternalAsync(CancellationToken cancellationToken)
        {
            using (CancellationTokenSource linkedCts = CreateLinkedLifecycleToken(cancellationToken))
            {
                await buildingsGate.WaitAsync(linkedCts.Token);

                try
                {
                    if (!IsRealtimeReady)
                    {
                        bool ready = await EnsureRealtimeReadyAsync(false, linkedCts.Token);
                        if (!ready) return false;
                    }

                    SetState(FlowState.LoadingBuildings, "completed_buildings_request_started");
                    PublishBuildingsProgress("در حال دریافت فهرست ساختمان‌ها...", "url=" + ServerConfig.CompletedBuildingsUrl);

                    var apiClient = new CompletedBuildingsApiClient(ServerConfig.CompletedBuildingsUrl, buildingsRequestTimeoutMs, buildingsMaximumPages);
                    CompletedBuildingsLoadResult result = await apiClient.GetAllAsync(linkedCts.Token);

                    if (!result.IsSuccess)
                    {
                        GlobalMessageManager.Clear(BuildingsProgressMessageId);
                        RestoreStateAfterBuildingsFailure("completed_buildings_request_failed");
                        await ShowBuildingsFailureIfNeededAsync(result);
                        return false;
                    }

                    if (!IsRealtimeReady || StartupNetworkSceneRouter.Instance == null || !StartupNetworkSceneRouter.IsOnline)
                    {
                        GlobalMessageManager.Clear(BuildingsProgressMessageId);
                        RestoreStateAfterBuildingsFailure("completed_buildings_result_arrived_after_connection_lost");
                        return false;
                    }

                    completedBuildings.Clear();
                    completedBuildings.AddRange(result.Buildings);
                    GlobalMessageManager.Clear(BuildingsProgressMessageId);
                    GlobalMessageManager.Clear(BuildingsErrorMessageId);
                    SetState(isJoinedRoom ? FlowState.RoomJoined : FlowState.LobbyReady, "completed_buildings_loaded");
                    NotifyBuildingsUpdated();
                    NetworkFileLogger.Info("REALTIME_LOBBY_BUILDINGS", "pages=" + result.LoadedPages + " | items=" + completedBuildings.Count + " | expected=" + result.TotalExpectedItems);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    GlobalMessageManager.Clear(BuildingsProgressMessageId);
                    RestoreStateAfterBuildingsFailure("completed_buildings_request_cancelled");
                    NetworkFileLogger.Warning("REALTIME_LOBBY_BUILDINGS", "دریافت ساختمان‌ها لغو شد.");
                    return false;
                }
                catch (Exception ex)
                {
                    GlobalMessageManager.Clear(BuildingsProgressMessageId);
                    RestoreStateAfterBuildingsFailure("completed_buildings_exception");
                    NetworkFileLogger.Exception("REALTIME_LOBBY_BUILDINGS", ex);

                    if (CanShowRealtimeOrRequestMessage())
                    {
                        GlobalMessageManager.ShowError(BuildingsErrorMessageId, "دریافت ساختمان‌ها", "فهرست ساختمان‌ها دریافت نشد.", ex.ToString(), 0f, false, GlobalMessageManager.MessageSource.Request, true, RetryBuildingsAsync);
                    }

                    return false;
                }
                finally
                {
                    buildingsGate.Release();
                }
            }
        }

        #region ورود به روم ساختمان

        //* این تابع ساختمان کلیک‌شده را با روم ریل‌تایم همنام پیدا یا ایجاد می‌کند، سپس ورود قابل اطمینان به همان روم را انجام می‌دهد.
        public async Task<bool> EnterBuildingRoomAsync(CompletedBuildingDto building, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (building == null || !building.HasValidFeatureId()) return ShowRoomValidationFailure("اطلاعات ساختمان انتخاب‌شده معتبر نیست.", "building_is_null_or_feature_id_invalid");

            building.Normalize();
            string buildingCode = (building.feature_properties_id ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(buildingCode)) return ShowRoomValidationFailure("کد ساختمان انتخاب‌شده خالی است.", "building_code_empty");
            if (buildingCode.Length < 2 || buildingCode.Length > 64) return ShowRoomValidationFailure("کد ساختمان برای ساخت روم معتبر نیست.", "building_code_length=" + buildingCode.Length);

            #region لابی عمومی سه بعدی

            if (IsInsidePublicLobbyRoom)
            {
                bool publicLobbyLeft = await LeavePublicLobbyForBuildingSwitchAsync(cancellationToken);
                if (!publicLobbyLeft) return false;
            }

            #endregion

            using (CancellationTokenSource linkedCts = CreateLinkedLifecycleToken(cancellationToken))
            {
                await roomGate.WaitAsync(linkedCts.Token);

                try
                {
                    if (!IsRealtimeReady)
                    {
                        bool ready = await EnsureRealtimeReadyAsync(false, linkedCts.Token);
                        if (!ready) return false;
                    }

                    if (isRoomExitInProgress || (DedicatedGameServerRealtimeRoomBinder.Instance != null && DedicatedGameServerRealtimeRoomBinder.Instance.IsDisconnectFlowRunning))
                    {
                        return ShowRoomValidationFailure("خروج از روم قبلی هنوز کامل نشده است.", "room_exit_in_progress");
                    }

                    if (isJoinedRoom)
                    {
                        bool sameBuilding = !string.IsNullOrWhiteSpace(currentRoomId) && string.Equals(currentRoomName, buildingCode, StringComparison.OrdinalIgnoreCase);
                        if (sameBuilding) return true;

                        return ShowRoomValidationFailure(
                            "ابتدا با دکمه خروج از Game Server از روم فعلی خارج شوید.",
                            "join_blocked_until_current_room_exit | currentRoomId=" + currentRoomId + " | requestedFeatureId=" + building.feature_id
                        );
                    }

                    DedicatedGameServerRealtimeRoomBinder dedicatedBinder = DedicatedGameServerRealtimeRoomBinder.Instance;
                    if (dedicatedBinder != null && (dedicatedBinder.IsConnectFlowRunning || dedicatedBinder.HasUnclosedDedicatedSession))
                    {
                        return ShowRoomValidationFailure(
                            "اتصال Game Server قبلی هنوز بسته نشده است.",
                            "join_blocked_by_active_dedicated_flow | connectRunning=" + dedicatedBinder.IsConnectFlowRunning + " | unclosedDedicatedSession=" + dedicatedBinder.HasUnclosedDedicatedSession
                        );
                    }

                    selectedBuilding = CloneBuilding(building);
                    lastRoomFlowError = string.Empty;
                    SetState(FlowState.ResolvingRoom, "building_room_resolve_started");
                    PublishRoomProgress("در حال آماده‌سازی روم ساختمان...", BuildSelectedBuildingTechnicalDetails());

                    RealtimeRoomDto room = await ResolveBuildingRoomAsync(selectedBuilding, linkedCts.Token);
                    if (room == null || !room.HasValidRoomId())
                    {
                        RestoreStateAfterRoomFailure("building_room_resolve_failed");
                        ShowRoomFailure("روم ساختمان آماده نشد. دوباره تلاش کنید.", lastRoomFlowError, true);
                        return false;
                    }

                    room.Normalize();
                    currentRoomId = room.roomId.Trim();
                    currentRoomName = string.IsNullOrWhiteSpace(room.roomName) ? buildingCode : room.roomName.Trim();
                    SetState(FlowState.JoiningRoom, "building_room_join_started");
                    PublishRoomProgress("در حال ورود به روم ساختمان...", "roomId=" + currentRoomId + " | roomName=" + currentRoomName);

                    RealtimeReliableSendResult joinResult = await gameServerClient.JoinRoomReliableAsync(currentRoomId, CreateReliableOptions(), linkedCts.Token);
                    if (joinResult == null || !joinResult.isSuccess)
                    {
                        lastRoomFlowError = "join_room_failed | roomId=" + currentRoomId + " | attempts=" + (joinResult != null ? joinResult.attempts : 0) + " | error=" + (joinResult != null ? joinResult.errorMessage : "result_null");
                        isJoinedRoom = false;
                        shouldRejoinRoomAfterReconnect = false;
                        RestoreStateAfterRoomFailure("building_room_join_failed");
                        ShowRoomFailure("ورود به روم ساختمان انجام نشد. دوباره تلاش کنید.", lastRoomFlowError, true);
                        return false;
                    }

                    int joinedBuildingFeatureId = selectedBuilding.feature_id;
                    selectedBuildingWidth = selectedBuilding.width ?? string.Empty;
                    selectedBuildingLength = selectedBuilding.length ?? string.Empty;
                    selectedBuildingDensity = selectedBuilding.density ?? string.Empty;
                    selectedBuilding = null;
                    isJoinedRoom = true;
                    shouldRejoinRoomAfterReconnect = true;
                    GlobalMessageManager.Clear(RoomProgressMessageId);
                    GlobalMessageManager.Clear(RoomErrorMessageId);
                    SetState(FlowState.RoomJoined, "building_room_join_succeeded");
                    NetworkFileLogger.Info("BUILDING_ROOM_JOINED", "roomId=" + currentRoomId + " | roomName=" + currentRoomName + " | featureId=" + joinedBuildingFeatureId + " | width=" + SelectedBuildingWidth + " | length=" + SelectedBuildingLength);
                    NotifyRoomJoined(currentRoomId);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    GlobalMessageManager.Clear(RoomProgressMessageId);
                    RestoreStateAfterRoomFailure("building_room_operation_cancelled");
                    NetworkFileLogger.Warning("BUILDING_ROOM_FLOW", "عملیات ورود به روم ساختمان لغو شد.");
                    return false;
                }
                catch (Exception ex)
                {
                    GlobalMessageManager.Clear(RoomProgressMessageId);
                    lastRoomFlowError = ex.ToString();
                    RestoreStateAfterRoomFailure("building_room_exception");
                    NetworkFileLogger.Exception("BUILDING_ROOM_FLOW", ex);
                    ShowRoomFailure("ورود به روم ساختمان انجام نشد.", ex.ToString(), true);
                    return false;
                }
                finally
                {
                    roomGate.Release();
                }
            }
        }

        //* این تابع خروج رسمی از روم را همیشه به Binder می‌سپارد تا ابتدا Session گیم سرور بسته شود و سپس روم ریل‌تایم ترک شود.
        public async Task<bool> LeaveCurrentRoomAsync(bool clearSelectedBuilding = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            DedicatedGameServerRealtimeRoomBinder dedicatedBinder = DedicatedGameServerRealtimeRoomBinder.Instance;

            if (dedicatedBinder != null)
            {
                if (dedicatedBinder.IsDisconnectFlowRunning)
                {
                    NetworkFileLogger.Warning("BUILDING_ROOM_LEFT", "خروج دوباره رد شد چون جریان خروج هماهنگ‌شده هنوز فعال است | roomId=" + currentRoomId);
                    return false;
                }

                return await dedicatedBinder.DisconnectGameServerAndLeaveRoomAsync(
                    clearSelectedBuilding ? "realtime_room_leave_and_clear_building" : "realtime_room_leave_requested",
                    clearSelectedBuilding
                );
            }

            return await LeaveCurrentRoomAfterDedicatedDisconnectAsync(clearSelectedBuilding, cancellationToken);
        }

        //* این تابع فقط توسط Binder و پس از پایان تأییدشده قطع Game Server اجرا می‌شود تا خروج روم بدون بازگشت دوباره به Binder انجام شود.
        public async Task<bool> LeaveCurrentRoomAfterDedicatedDisconnectAsync(bool clearSelectedBuilding = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            using (CancellationTokenSource linkedCts = CreateLinkedLifecycleToken(cancellationToken))
            {
                await roomGate.WaitAsync(linkedCts.Token);
                isRoomExitInProgress = true;

                try
                {
                    return await LeaveCurrentRoomInternalAsync(clearSelectedBuilding, linkedCts.Token);
                }
                finally
                {
                    isRoomExitInProgress = false;
                    roomGate.Release();
                }
            }
        }

        //* این تابع یک اکشن عمومی روم را از همان اتصال ریل تایم موجود و با ارسال مطمئن ارسال می کند.
        public async Task<bool> SendRoomPlayerActionReliableAsync(string actionType, string payloadJson, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!CanSendRoomPlayerAction) return false;
            if (string.IsNullOrWhiteSpace(actionType) || string.IsNullOrWhiteSpace(payloadJson)) return false;

            using (CancellationTokenSource linkedCts = CreateLinkedLifecycleToken(cancellationToken))
            {
                try
                {
                    RealtimeReliableSendResult result = await gameServerClient.SendPlayerActionReliableAsync(
                        actionType.Trim(),
                        payloadJson,
                        CreateReliableOptions(),
                        linkedCts.Token
                    );

                    bool sent = result != null && result.isSuccess;
                    NetworkFileLogger.Info(
                        "REALTIME_ROOM_PLAYER_ACTION",
                        "actionType=" + actionType.Trim()
                        + " | roomId=" + currentRoomId
                        + " | sent=" + sent
                        + " | attempts=" + (result != null ? result.attempts : 0)
                        + " | error=" + (result != null ? result.errorMessage : "result_null")
                    );
                    return sent;
                }
                catch (OperationCanceledException)
                {
                    NetworkFileLogger.Warning("REALTIME_ROOM_PLAYER_ACTION", "ارسال اکشن روم لغو شد | actionType=" + actionType.Trim() + " | roomId=" + currentRoomId);
                    return false;
                }
                catch (Exception ex)
                {
                    NetworkFileLogger.Exception("REALTIME_ROOM_PLAYER_ACTION", ex);
                    return false;
                }
            }
        }

        //* این تابع مقدارهای عرض و طول ساختمان انتخاب‌شده را برای مرحله ساخت زمین سه‌بعدی به عدد تبدیل می‌کند.
        public bool TryGetSelectedBuildingDimensions(out float width, out float length, out float density)
        {
            width = 0f;
            length = 0f;
            density = 0;
            return TryParsePositiveDimension(selectedBuildingWidth, out width) && TryParsePositiveDimension(selectedBuildingLength, out length) && TryParsePositiveDimension(selectedBuildingLength, out density);
        }

        //* این تابع روم همنام با کد ساختمان را از فهرست ریل‌تایم پیدا می‌کند و فقط در صورت نبودن آن، یک روم تازه می‌سازد.
        private async Task<RealtimeRoomDto> ResolveBuildingRoomAsync(CompletedBuildingDto building, CancellationToken cancellationToken)
        {
            if (realtimeLobbyClient == null)
            {
                lastRoomFlowError = "realtime_lobby_client_missing";
                return null;
            }

            string buildingCode = (building.feature_properties_id ?? string.Empty).Trim();
            RealtimeLobbyListRoomsResult listResult = await realtimeLobbyClient.ListRoomsAsync(CreateReliableOptions(), cancellationToken);
            if (listResult == null || !listResult.isSuccess)
            {
                lastRoomFlowError = "list_rooms_failed_before_resolve | error=" + (listResult != null ? listResult.errorMessage : "result_null");
                return null;
            }

            RealtimeRoomDto listedRoom = FindJoinableBuildingRoom(listResult.Rooms, buildingCode, out bool matchingRoomExists);
            if (listedRoom != null) return listedRoom;
            if (matchingRoomExists)
            {
                lastRoomFlowError = "matching_building_room_is_not_joinable | buildingCode=" + buildingCode;
                return null;
            }

            var createRequest = new RealtimeCreateRoomRequestDto(buildingCode, string.Empty, "public", Mathf.Clamp(buildingRoomMaxPlayers, 1, 100));
            RealtimeLobbyCreateRoomResult createResult = await realtimeLobbyClient.CreateRoomAsync(createRequest, CreateReliableOptions(), cancellationToken);
            if (createResult != null && createResult.isSuccess && createResult.room != null && createResult.room.HasValidRoomId()) return createResult.room;

            RealtimeLobbyListRoomsResult retryListResult = await realtimeLobbyClient.ListRoomsAsync(CreateReliableOptions(), cancellationToken);
            RealtimeRoomDto roomCreatedByAnotherRequest = retryListResult != null && retryListResult.isSuccess ? FindJoinableBuildingRoom(retryListResult.Rooms, buildingCode, out _) : null;
            if (roomCreatedByAnotherRequest != null) return roomCreatedByAnotherRequest;

            lastRoomFlowError = "create_room_failed | buildingCode=" + buildingCode + " | error=" + (createResult != null ? createResult.errorMessage : "result_null");
            return null;
        }

        //* این تابع از میان روم‌های همنام، نخستین روم باز و قابل ورود را برمی‌گرداند.
        private static RealtimeRoomDto FindJoinableBuildingRoom(RealtimeRoomDto[] rooms, string buildingCode, out bool matchingRoomExists)
        {
            matchingRoomExists = false;
            if (rooms == null || string.IsNullOrWhiteSpace(buildingCode)) return null;

            for (int i = 0; i < rooms.Length; i++)
            {
                RealtimeRoomDto room = rooms[i];
                if (room == null) continue;
                room.Normalize();
                if (!string.Equals(room.roomName, buildingCode, StringComparison.OrdinalIgnoreCase)) continue;

                matchingRoomExists = true;
                if (room.CanJoin()) return room;
            }

            return null;
        }

        //* این تابع بعد از بازیابی ریل‌تایم، عضویت روم قبلی را پیش از اتصال دوباره گیم سرور بازیابی می‌کند.
        private async Task<bool> RejoinCurrentRoomAfterReconnectAsync(CancellationToken cancellationToken)
        {
            if (!autoRejoinRoomAfterRealtimeReconnect || !shouldRejoinRoomAfterReconnect || string.IsNullOrWhiteSpace(currentRoomId)) return true;
            if (gameServerClient == null) return false;

            SetState(FlowState.JoiningRoom, "room_rejoin_after_realtime_started");
            PublishRoomProgress("اتصال Realtime برگشت. در حال ورود دوباره به روم...", "roomId=" + currentRoomId);
            RealtimeReliableSendResult result = await gameServerClient.JoinRoomReliableAsync(currentRoomId, CreateReliableOptions(), cancellationToken);

            if (result == null || !result.isSuccess)
            {
                lastRoomFlowError = "room_rejoin_failed | roomId=" + currentRoomId + " | error=" + (result != null ? result.errorMessage : "result_null");
                isJoinedRoom = false;
                return false;
            }

            isJoinedRoom = true;
            shouldRejoinRoomAfterReconnect = true;
            GlobalMessageManager.Clear(RoomProgressMessageId);
            SetState(FlowState.RoomJoined, "room_rejoin_after_realtime_succeeded");
            NetworkFileLogger.Info("BUILDING_ROOM_REJOINED", "roomId=" + currentRoomId + " | roomName=" + currentRoomName);
            NotifyRoomJoined(currentRoomId);
            return true;
        }

        //* این تابع خروج واقعی از روم را بدون گرفتن دوباره قفل عملیات انجام می‌دهد.
        private async Task<bool> LeaveCurrentRoomInternalAsync(bool clearSelectedBuilding, CancellationToken cancellationToken)
        {
            string leavingRoomId = currentRoomId;

            if (isJoinedRoom && gameServerClient != null && !string.IsNullOrWhiteSpace(leavingRoomId))
            {
                RealtimeReliableSendResult leaveResult = await gameServerClient.LeaveRoomReliableAsync(leavingRoomId, CreateReliableOptions(), cancellationToken);
                if (leaveResult == null || !leaveResult.isSuccess)
                {
                    NetworkFileLogger.Warning("BUILDING_ROOM_LEFT", "خروج از روم انجام نشد | roomId=" + leavingRoomId + " | error=" + (leaveResult != null ? leaveResult.errorMessage : "result_null"));
                    return false;
                }
            }

            isJoinedRoom = false;
            shouldRejoinRoomAfterReconnect = false;
            currentRoomId = string.Empty;
            currentRoomName = string.Empty;
            selectedBuildingWidth = string.Empty;
            selectedBuildingLength = string.Empty;
            selectedBuildingDensity = string.Empty;
            if (clearSelectedBuilding) selectedBuilding = null;
            GlobalMessageManager.Clear(RoomProgressMessageId);
            GlobalMessageManager.Clear(RoomErrorMessageId);
            GlobalMessageManager.Clear(PublicLobbyProgressMessageId);
            GlobalMessageManager.Clear(PublicLobbyErrorMessageId);
            SetState(completedBuildings.Count > 0 ? FlowState.LobbyReady : FlowState.Ready, "room_left");
            if (!string.IsNullOrWhiteSpace(leavingRoomId)) NotifyRoomLeft(leavingRoomId);
            NetworkFileLogger.Info("BUILDING_ROOM_LEFT", "roomId=" + leavingRoomId + " | clearSelectedBuilding=" + clearSelectedBuilding);
            return true;
        }

        //* این تابع اطلاعات روم را در قطع موقت نگه می‌دارد تا پس از بازگشت شبکه همان روم دوباره جوین شود.
        private void RememberRoomForReconnect()
        {
            if (isJoinedRoom && !string.IsNullOrWhiteSpace(currentRoomId)) shouldRejoinRoomAfterReconnect = true;
            isJoinedRoom = false;
        }

        //* این تابع همه اطلاعات روم و ساختمان انتخاب‌شده را هنگام خروج رسمی پاک می‌کند.
        private void ClearRoomContext(bool clearSelectedBuilding)
        {
            isRoomExitInProgress = false;
            isSwitchingFromPublicLobbyToBuildingRoom = false;
            isJoinedRoom = false;
            shouldRejoinRoomAfterReconnect = false;
            currentRoomId = string.Empty;
            currentRoomName = string.Empty;
            lastRoomFlowError = string.Empty;
            selectedBuildingWidth = string.Empty;
            selectedBuildingLength = string.Empty;
            selectedBuildingDensity = string.Empty;
            if (clearSelectedBuilding) selectedBuilding = null;
        }

        //* این تابع وضعیت مناسب بعد از موفقیت اتصال را با توجه به عضویت روم و وجود فهرست ساختمان‌ها تعیین می‌کند.
        private FlowState ResolveReadyState()
        {
            if (isJoinedRoom) return FlowState.RoomJoined;
            return completedBuildings.Count > 0 ? FlowState.LobbyReady : FlowState.Ready;
        }

        //* این تابع شکست اعتبارسنجی روم را ثبت می‌کند و پیام مناسب رابط را نمایش می‌دهد.
        private bool ShowRoomValidationFailure(string userMessage, string technicalDetails)
        {
            lastRoomFlowError = technicalDetails ?? string.Empty;
            ShowRoomFailure(userMessage, lastRoomFlowError, false);
            return false;
        }

        //* این تابع پیام پیشرفت ورود به روم را بدون امکان بستن نمایش می‌دهد.
        private void PublishRoomProgress(string message, string technicalDetails)
        {
            if (!CanShowRealtimeOrRequestMessage()) return;
            GlobalMessageManager.Publish(RoomProgressMessageId, GlobalMessageManager.MessageSource.Realtime, GlobalMessageManager.MessageType.Information, GlobalMessageManager.Priorities.Reconnecting, "ورود به روم", message, technicalDetails ?? string.Empty, 0f, true, false);
        }

        //* این تابع خطای ورود به روم را با امکان تلاش دوباره برای ساختمان انتخاب‌شده نمایش می‌دهد.
        private void ShowRoomFailure(string userMessage, string technicalDetails, bool allowRetry)
        {
            GlobalMessageManager.Clear(RoomProgressMessageId);
            if (!CanShowRealtimeOrRequestMessage()) return;
            GlobalMessageManager.ShowError(RoomErrorMessageId, "ورود به روم", userMessage, technicalDetails ?? string.Empty, 0f, false, GlobalMessageManager.MessageSource.Realtime, allowRetry, allowRetry ? RetrySelectedBuildingRoomAsync : null);
        }

        //* این تابع تلاش دوباره پیام خطای روم را با ساختمان انتخاب‌شده فعلی اجرا می‌کند.
        private async Task RetrySelectedBuildingRoomAsync()
        {
            if (selectedBuilding != null) await EnterBuildingRoomAsync(selectedBuilding);
        }

        //* این تابع وضعیت مدیر را پس از شکست روم به وضعیت آماده لابی یا آماده ریل‌تایم بازمی‌گرداند.
        private void RestoreStateAfterRoomFailure(string details)
        {
            if (StartupNetworkSceneRouter.Instance == null || !StartupNetworkSceneRouter.IsOnline) SetState(FlowState.WaitingForNetwork, details);
            else if (!IsGlobalAuthenticationReady()) SetState(FlowState.WaitingForAuthentication, details);
            else SetState(completedBuildings.Count > 0 ? FlowState.LobbyReady : FlowState.Ready, details);
        }

        //* این تابع تنظیمات ارسال قابل اطمینان آزمایش‌شده روم را می‌سازد.
        private RealtimeReliableSendOptions CreateReliableOptions()
        {
            return new RealtimeReliableSendOptions
            {
                ackTimeoutMs = Mathf.Max(1000, reliableAckTimeoutMs),
                maxSendAttempts = 3,
                retryDelayMs = 300,
                retryOnAckTimeout = true,
                retryOnTransportSendFailed = true
            };
        }

        //* این تابع یک نسخه مستقل از اطلاعات ساختمان را برای عبور امن میان صحنه‌ها نگه می‌دارد.
        private static CompletedBuildingDto CloneBuilding(CompletedBuildingDto source)
        {
            if (source == null) return null;
            source.Normalize();

            return new CompletedBuildingDto
            {
                id = source.id,
                feature_id = source.feature_id,
                feature_properties_id = source.feature_properties_id,
                karbari = source.karbari,
                density = source.density,
                length = source.length,
                width = source.width
            };
        }

        //* این تابع مقدار مثبت ابعاد ساختمان را با قالب عددی ثابت یا قالب جاری سیستم می‌خواند.
        private static bool TryParsePositiveDimension(string value, out float result)
        {
            result = 0f;
            if (string.IsNullOrWhiteSpace(value)) return false;
            string safeValue = value.Trim();
            bool parsed = float.TryParse(safeValue, NumberStyles.Float, CultureInfo.InvariantCulture, out result) || float.TryParse(safeValue, NumberStyles.Float, CultureInfo.CurrentCulture, out result);
            return parsed && result > 0f;
        }

        //* این تابع جزئیات ساختمان انتخاب‌شده را بدون تغییر داده‌های اصلی برای گزارش آماده می‌کند.
        private string BuildSelectedBuildingTechnicalDetails()
        {
            return selectedBuilding == null ? "selected_building_null" : "featureId=" + selectedBuilding.feature_id + " | buildingCode=" + selectedBuilding.feature_properties_id + " | width=" + selectedBuilding.width + " | length=" + selectedBuilding.length;
        }

        //* این تابع رویداد ورود موفق روم را برای Binder گیم سرور و مصرف‌کننده‌های سه‌بعدی اجرا می‌کند.
        private void NotifyRoomJoined(string roomId)
        {
            Action<string> handler = OnRoomJoinedFor3D;
            if (handler == null) return;

            try
            {
                handler(roomId ?? string.Empty);
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("BUILDING_ROOM_JOINED_EVENT", ex);
            }
        }

        //* این تابع رویداد خروج روم را برای Binder گیم سرور و مصرف‌کننده‌های سه‌بعدی اجرا می‌کند.
        private void NotifyRoomLeft(string roomId)
        {
            Action<string> handler = OnRoomLeftFor3D;
            if (handler == null) return;

            try
            {
                handler(roomId ?? string.Empty);
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("BUILDING_ROOM_LEFT_EVENT", ex);
            }
        }

        #endregion

        //* این تابع اتصال ریل‌تایم را به شکل رسمی می‌بندد و بازاتصال خودکار را متوقف می‌کند.
        public async Task DisconnectAsync(string reason = "user_requested_disconnect", CancellationToken cancellationToken = default(CancellationToken))
        {
            DedicatedGameServerRealtimeRoomBinder dedicatedBinder = DedicatedGameServerRealtimeRoomBinder.Instance;
            bool needsCoordinatedExit = isJoinedRoom || (dedicatedBinder != null && (dedicatedBinder.IsConnectFlowRunning || dedicatedBinder.HasUnclosedDedicatedSession));

            if (dedicatedBinder != null && needsCoordinatedExit)
            {
                bool coordinatedExitCompleted = await dedicatedBinder.DisconnectGameServerAndLeaveRoomAsync(
                    string.IsNullOrWhiteSpace(reason) ? "realtime_disconnect_before_room_exit" : reason.Trim() + ":before_realtime_disconnect",
                    true
                );

                if (!coordinatedExitCompleted)
                {
                    NetworkFileLogger.Warning("REALTIME_DISCONNECT", "قطع ریل‌تایم متوقف شد چون خروج Game Server و Room کامل نشد | reason=" + (reason ?? string.Empty) + " | roomId=" + currentRoomId);
                    return;
                }
            }

            hasLobbyEntryRequested = false;
            permanentRecoveryFailureWaitingForLobby = false;
            CancelUnifiedRecovery(reason, false);
            reconnect?.Stop();
            StopHeartbeat();
            SetState(FlowState.Disconnecting, reason);
            suppressDisconnectHandling = true;

            try
            {
                if (realtimeClient != null) await realtimeClient.DisconnectAsync(reason, cancellationToken);
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("REALTIME_DISCONNECT", ex);
            }
            finally
            {
                suppressDisconnectHandling = false;
                CleanupClientObjects();
                completedBuildings.Clear();
                ClearRoomContext(true);
                hasReachedRealtimeReady = false;
                GlobalMessageManager.Clear(RealtimeProgressMessageId);
                GlobalMessageManager.Clear(RealtimeErrorMessageId);
                GlobalMessageManager.Clear(BuildingsProgressMessageId);
                GlobalMessageManager.Clear(BuildingsErrorMessageId);
                GlobalMessageManager.Clear(PublicLobbyProgressMessageId);
                GlobalMessageManager.Clear(PublicLobbyErrorMessageId);
                SetState(FlowState.Idle, reason);
            }
        }

        #endregion

        #region اتصال و احراز هویت

        //* این تابع اتصال ترنسپورت، تازه‌سازی احتمالی توکن و دریافت پاسخ احراز هویت را به ترتیب اجرا می‌کند.
        private async Task<bool> ConnectAndAuthenticateInternalAsync(bool reconnecting, CancellationToken cancellationToken)
        {
            SetState(reconnecting ? FlowState.Reconnecting : FlowState.Connecting, reconnecting ? "reconnect_started" : "connect_started");
            PublishRealtimeProgress(reconnecting ? "در حال اتصال دوباره به فضای آنلاین..." : "در حال اتصال به فضای آنلاین...", "transport=" + ResolveTransportKind() + " | endpoint=" + ResolveRealtimeServerUrl());

            CleanupClientObjects();
            CreateClientObjects();

            bool connected = await ConnectTransportAsync(cancellationToken);
            if (!connected)
            {
                await HandleRealtimeFlowFailureAsync("اتصال به Realtime انجام نشد.", "transport_connect_failed", reconnecting);
                return false;
            }

            SetState(FlowState.Connected, "transport_connected");
            PublishRealtimeProgress("اتصال شبکه برقرار شد. در حال احراز هویت Realtime...", "transport_connected");

            string accessToken = await EnsureFreshAccessTokenAsync(reconnecting, cancellationToken);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                await HandleRealtimeFlowFailureAsync("نشست کاربر برای اتصال Realtime معتبر نیست.", "access_token_unavailable", reconnecting);
                return false;
            }

            bool authenticated = await AuthenticateAndWaitAsync(accessToken, cancellationToken);

            if (!authenticated && IsAuthenticationTokenFailure(lastAuthenticationError))
            {
                SetState(FlowState.RefreshingToken, "realtime_auth_token_rejected");
                PublishRealtimeProgress("نشست کاربر در حال تمدید است...", FormatRealtimeError(lastAuthenticationError));
                bool refreshed = await AuthRefreshManager.Refresh(!reconnecting);

                if (refreshed)
                {
                    string refreshedToken = SecureTokenStorage.GetAccessToken();
                    authenticated = !string.IsNullOrWhiteSpace(refreshedToken) && await AuthenticateAndWaitAsync(refreshedToken, cancellationToken);
                }
            }

            if (!authenticated)
            {
                await HandleRealtimeFlowFailureAsync("احراز هویت Realtime انجام نشد.", FormatRealtimeError(lastAuthenticationError), reconnecting);
                return false;
            }

            GlobalMessageManager.Clear(RealtimeProgressMessageId);
            GlobalMessageManager.Clear(RealtimeErrorMessageId);
            hasReachedRealtimeReady = true;
            StartHeartbeat();

            if (reconnecting && shouldRejoinRoomAfterReconnect && autoRejoinRoomAfterRealtimeReconnect)
            {
                bool rejoined = await RejoinCurrentRoomAfterReconnectAsync(cancellationToken);
                if (!rejoined)
                {
                    await HandleRealtimeFlowFailureAsync("ورود دوباره به روم انجام نشد.", lastRoomFlowError, true);
                    return false;
                }
            }
            else
            {
                SetState(FlowState.Ready, "realtime_authenticated");
            }

            NotifyRealtimeReady();

            if (unifiedRecoveryRunning && !recoveryRequiresDedicatedGameServer) CompleteUnifiedRecovery("realtime_recovered_without_dedicated_session");

            NetworkFileLogger.Info("REALTIME_READY", "connectionId=" + RealtimeConnectionId + " | userId=" + RealtimeUserId + " | transport=" + ResolveTransportKind() + " | roomJoined=" + isJoinedRoom + " | roomId=" + currentRoomId + " | recoveryRunning=" + unifiedRecoveryRunning + " | recoveryRemainingSeconds=" + RecoveryRemainingSeconds.ToString("F1"));
            return true;
        }

        //* این تابع ترنسپورت انتخاب‌شده را با مهلت مستقل باز می‌کند.
        private async Task<bool> ConnectTransportAsync(CancellationToken cancellationToken)
        {
            if (realtimeClient == null) return false;

            using (CancellationTokenSource timeoutCts = new CancellationTokenSource(Mathf.Max(1000, connectTimeoutMs)))
            using (CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
            {
                try
                {
                    bool connected = await realtimeClient.ConnectAsync(null, linkedCts.Token);
                    return connected && realtimeClient.IsConnected;
                }
                catch (OperationCanceledException)
                {
                    NetworkFileLogger.Warning("REALTIME_CONNECT", "اتصال ریل‌تایم لغو شد یا زمان آن پایان یافت.");
                    return false;
                }
                catch (Exception ex)
                {
                    NetworkFileLogger.Exception("REALTIME_CONNECT", ex);
                    return false;
                }
            }
        }

        //* این تابع در صورت خالی، منقضی یا نزدیک انقضا بودن اکسس توکن، پیش از احراز هویت آن را تمدید می‌کند.
        private async Task<string> EnsureFreshAccessTokenAsync(bool reconnecting, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string accessToken = SecureTokenStorage.GetAccessToken();
            if (!IsAccessTokenRefreshRequired(accessToken)) return accessToken.Trim();

            SetState(FlowState.RefreshingToken, "access_token_refresh_required");
            PublishRealtimeProgress("نشست کاربر برای اتصال Realtime در حال تمدید است...", "reconnecting=" + reconnecting);
            bool refreshed = await AuthRefreshManager.Refresh(!reconnecting);
            if (!refreshed) return string.Empty;

            accessToken = SecureTokenStorage.GetAccessToken();
            return string.IsNullOrWhiteSpace(accessToken) ? string.Empty : accessToken.Trim();
        }

        //* این تابع پیام احراز هویت را ارسال می‌کند و با مهلت جداگانه منتظر پاسخ سرور می‌ماند.
        private async Task<bool> AuthenticateAndWaitAsync(string accessToken, CancellationToken cancellationToken)
        {
            if (realtimeAuthClient == null || string.IsNullOrWhiteSpace(accessToken)) return false;

            lastAuthenticationError = null;
            authWaiter = CreateBoolWaiter();
            SetState(FlowState.Authenticating, "realtime_auth_started");

            using (CancellationTokenSource sendTimeoutCts = new CancellationTokenSource(Mathf.Max(1000, sendTimeoutMs)))
            using (CancellationTokenSource sendLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sendTimeoutCts.Token))
            {
                try
                {
                    bool sent = await realtimeAuthClient.AuthenticateWithAccessTokenAsync(accessToken, sendLinkedCts.Token);
                    if (!sent) return false;
                }
                catch (OperationCanceledException)
                {
                    lastAuthenticationError = RealtimeError.Create(RealtimeErrorCodes.AuthFailed, "Realtime auth send timed out.");
                    return false;
                }
            }

            return await WaitForAuthResultAsync(authWaiter, cancellationToken);
        }

        //* این تابع تا دریافت پاسخ احراز هویت، پایان مهلت یا لغو عملیات منتظر می‌ماند.
        private async Task<bool> WaitForAuthResultAsync(TaskCompletionSource<bool> waiter, CancellationToken cancellationToken)
        {
            if (waiter == null) return false;

            try
            {
                Task timeoutTask = Task.Delay(Mathf.Max(1000, authAckTimeoutMs), cancellationToken);
                Task completedTask = await Task.WhenAny(waiter.Task, timeoutTask);

                if (completedTask != waiter.Task)
                {
                    if (lastAuthenticationError == null) lastAuthenticationError = RealtimeError.Create(RealtimeErrorCodes.AuthFailed, "Realtime auth acknowledgement timed out.");
                    return false;
                }

                return waiter.Task.Result && realtimeAuthClient != null && realtimeAuthClient.IsAuthenticated;
            }
            finally
            {
                if (ReferenceEquals(authWaiter, waiter)) authWaiter = null;
            }
        }

        //* این تابع از وضعیت مدیر شبکه و مدیر ورود اطمینان می‌گیرد و علت مسدودشدن جریان را برمی‌گرداند.
        private bool CanStartRealtimeFlow(out string blockedReason)
        {
            if (permanentRecoveryFailureWaitingForLobby)
            {
                SetState(FlowState.Failed, "permanent_recovery_waiting_for_lobby");
                blockedReason = "Permanent recovery failed; a fresh Lobby 1 entry is required before realtime can reconnect.";
                return false;
            }

            if (StartupNetworkSceneRouter.Instance == null || !StartupNetworkSceneRouter.IsOnline)
            {
                SetState(FlowState.WaitingForNetwork, "network_not_ready");
                blockedReason = "Network is not online.";
                return false;
            }

            GlobalAuthManager authManager = GlobalAuthManager.Instance;

            if (authManager == null || !authManager.isLogin || authManager.CurrentUser == null || GlobalAuthManager.CurrentAuthState != GlobalAuthManager.AuthState.Authenticated)
            {
                SetState(FlowState.WaitingForAuthentication, "authentication_not_ready");
                blockedReason = "Global authentication is not ready.";
                return false;
            }

            blockedReason = string.Empty;
            return true;
        }

        //* این تابع تنظیمات واقعی ترنسپورت را با همان قراردادهای آزمایش‌شده پروژه می‌سازد.
        private RealtimeConfig BuildRealtimeConfig()
        {
            return new RealtimeConfig
            {
                serverUrl = ResolveRealtimeServerUrl(),
                transportKind = ResolveTransportKind(),
                connectTimeoutMs = 0,
                sendTimeoutMs = Mathf.Max(1000, sendTimeoutMs),
                autoAuthenticateAfterConnect = false,
                logIncomingMessages = logIncomingRealtimeMessages,
                logOutgoingMessages = logOutgoingRealtimeMessages
            };
        }

        //* این تابع نوع روش ارتباط نهایی را برای وب‌جی‌ال و پلتفرم‌های بومی مشخص می‌کند.
        private RealtimeTransportKind ResolveTransportKind()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return RealtimeTransportKind.WebSocket;
#else
            return RealtimeTransportKind.GrpcStreaming;
#endif
        }

        //* این تابع نشانی ریل‌تایم را از تنظیمات مرکزی و محیط انتخاب‌شده می‌سازد.
        private string ResolveRealtimeServerUrl()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            ServerConfig.UseRealtimeWebSocketPath("/ws");
            return ServerConfig.RealtimeWebSocketUrl;
#else
            if (ServerConfigBootstrap.HasAppliedConfiguration && ServerConfigBootstrap.AppliedEnvironment == ServerConfigBootstrap.ServerEnvironment.Local) ServerConfig.UseLocalRealtimeGrpcStreaming();
            else ServerConfig.UseDedicatedRealtimeGrpcStreaming();
            return ServerConfig.RealtimeGrpcStreamingAddress;
#endif
        }

        //* این تابع کلاینت اصلی، کلاینت احراز هویت و هارت‌بیت را برای یک اتصال تازه می‌سازد.
        private void CreateClientObjects()
        {
            CleanupClientObjects();
            realtimeClient = new RealtimeClient(BuildRealtimeConfig());
            realtimeAuthClient = new RealtimeAuthClient(realtimeClient);
            realtimeLobbyClient = new RealtimeLobbyClient(realtimeClient);
            gameServerClient = new GameServerClient(realtimeClient);
            heartbeat = new RealtimeHeartbeat(realtimeClient)
            {
                pingIntervalMs = Mathf.Max(500, heartbeatPingIntervalMs),
                pongTimeoutMs = Mathf.Max(500, heartbeatPongTimeoutMs),
                maxMissedPongs = Mathf.Max(1, heartbeatMaximumMissedPongs),
                logHeartbeat = false
            };
            BindClientEvents();
        }

        //* این تابع رویدادهای کلاینت، احراز هویت و هارت‌بیت را فقط یک بار ثبت می‌کند.
        private void BindClientEvents()
        {
            if (eventsBound || realtimeClient == null || realtimeAuthClient == null || heartbeat == null) return;
            eventsBound = true;
            realtimeClient.StateChanged += HandleRealtimeClientStateChanged;
            realtimeClient.EnvelopeReceived += HandleRealtimeEnvelopeReceived;
            realtimeClient.TransportErrorReceived += HandleTransportErrorReceived;
            realtimeClient.Disconnected += HandleRealtimeDisconnected;
            realtimeAuthClient.Authenticated += HandleRealtimeAuthenticated;
            realtimeAuthClient.AuthenticationFailed += HandleRealtimeAuthenticationFailed;
            heartbeat.ConnectionTimeout += HandleHeartbeatConnectionTimeout;
        }

        //* این تابع همه رویدادهای اتصال فعلی را پیش از نابودی کلاینت جدا می‌کند.
        private void UnbindClientEvents()
        {
            if (!eventsBound) return;
            eventsBound = false;

            if (realtimeClient != null)
            {
                realtimeClient.StateChanged -= HandleRealtimeClientStateChanged;
                realtimeClient.EnvelopeReceived -= HandleRealtimeEnvelopeReceived;
                realtimeClient.TransportErrorReceived -= HandleTransportErrorReceived;
                realtimeClient.Disconnected -= HandleRealtimeDisconnected;
            }

            if (realtimeAuthClient != null)
            {
                realtimeAuthClient.Authenticated -= HandleRealtimeAuthenticated;
                realtimeAuthClient.AuthenticationFailed -= HandleRealtimeAuthenticationFailed;
            }

            if (heartbeat != null) heartbeat.ConnectionTimeout -= HandleHeartbeatConnectionTimeout;
        }

        //* این تابع منابع اتصال قبلی را بدون حذف مدیر دائمی آزاد می‌کند.
        private void CleanupClientObjects()
        {
            StopHeartbeat();
            UnbindClientEvents();
            realtimeLobbyClient?.Dispose();
            realtimeLobbyClient = null;
            gameServerClient?.Dispose();
            gameServerClient = null;
            realtimeAuthClient?.Dispose();
            realtimeAuthClient = null;
            realtimeClient?.Dispose();
            realtimeClient = null;
            heartbeat?.Dispose();
            heartbeat = null;
            authWaiter = null;
            lastAuthenticationError = null;
        }

        #endregion

        #region بازاتصال

        //* این تابع تنظیمات بازاتصال را روی کنترلر مشترک و آزمایش‌شده پروژه اعمال می‌کند.
        private void ConfigureReconnectController()
        {
            reconnect = new RealtimeReconnect
            {
                maxAttempts = 0,
                initialDelayMs = Mathf.Max(0, reconnectInitialDelayMs),
                maxDelayMs = Mathf.Max(0, reconnectMaximumDelayMs),
                totalTimeoutMs = GetPermanentReconnectFailureTimeoutMilliseconds(),
                delayMultiplier = Mathf.Max(1f, reconnectDelayMultiplier),
                logReconnect = false
            };

            reconnectMaximumAttempts = Mathf.Max(1, reconnectMaximumAttempts);
            reconnectTotalTimeoutMs = GetPermanentReconnectFailureTimeoutMilliseconds();

            reconnect.ReconnectAttemptStarted += HandleReconnectAttemptStarted;
            reconnect.ReconnectSucceeded += HandleReconnectSucceeded;
            reconnect.ReconnectFailed += HandleReconnectFailed;
        }

        //* این تابع پس از قطع غیرعمدی، ابتدا سلامت شبکه را بررسی و سپس حلقه بازاتصال را آغاز می‌کند.
        private async Task BeginReconnectAfterUnexpectedDisconnectAsync(string reason)
        {
            if (!enableAutomaticReconnect || !hasLobbyEntryRequested || !hasReachedRealtimeReady || applicationIsQuitting || suppressDisconnectHandling) return;

            BeginUnifiedRecovery(reason);
            if (!HasUnifiedRecoveryTimeRemaining())
            {
                FailUnifiedRecovery("recovery_deadline_reached_before_reconnect_start");
                return;
            }

            bool serverReachable = await IsServerReachableSilentlyAsync();

            if (!serverReachable)
            {
                SetState(FlowState.WaitingForNetwork, reason);
                return;
            }

            if (!CanStartRealtimeFlow(out string blockedReason))
            {
                NetworkFileLogger.Warning("REALTIME_RECONNECT_BLOCKED", blockedReason);
                return;
            }

            SetState(FlowState.Reconnecting, reason);
            PublishRealtimeProgress("اتصال Realtime قطع شد. در حال تلاش برای اتصال دوباره...", reason);

            reconnect.totalTimeoutMs = GetUnifiedRecoveryRemainingMilliseconds();
            reconnect.maxAttempts = 0;
            reconnect.Start(TryReconnectOnceAsync);
        }

        //* این تابع یک تلاش کامل بازاتصال شامل اتصال و احراز هویت را اجرا می‌کند.
        private async Task<bool> TryReconnectOnceAsync(CancellationToken cancellationToken)
        {
            return await EnsureRealtimeReadyAsync(true, cancellationToken);
        }

        //* این تابع با بررسی بی‌صدای مدیر شبکه مشخص می‌کند شکست مربوط به اینترنت است یا خود ریل‌تایم.
        private async Task<bool> IsServerReachableSilentlyAsync()
        {
            if (StartupNetworkSceneRouter.Instance == null || !StartupNetworkSceneRouter.IsOnline) return false;
            return await StartupNetworkSceneRouter.Instance.CheckNetFastSilentAsync();
        }

        //* این تابع آغاز هر تلاش بازاتصال را در وضعیت و گزارش سراسری ثبت می‌کند.
        private void HandleReconnectAttemptStarted(int attempt, int delayMs)
        {
            SetState(FlowState.Reconnecting, "attempt=" + attempt);
            PublishRealtimeProgress("در حال تلاش دوباره برای اتصال Realtime...", "attempt=" + attempt + " | delayMs=" + delayMs);
        }

        //* این تابع موفقیت بازاتصال را ثبت و در صورت نیاز فهرست ساختمان‌ها را تازه می‌کند.
        private async void HandleReconnectSucceeded(int attempt)
        {
            NetworkFileLogger.Info("REALTIME_RECONNECT", "بازاتصال موفق شد | attempt=" + attempt + " | recoveryRunning=" + unifiedRecoveryRunning + " | recoveryRemainingSeconds=" + RecoveryRemainingSeconds.ToString("F1"));
            GlobalMessageManager.Clear(RealtimeProgressMessageId);
            GlobalMessageManager.Clear(RealtimeErrorMessageId);

            if (hasLobbyEntryRequested && loadBuildingsAfterRealtimeAuthentication)
            {
                try
                {
                    await RefreshCompletedBuildingsAsync();
                }
                catch (Exception ex)
                {
                    NetworkFileLogger.Exception("REALTIME_RECONNECT_BUILDINGS", ex);
                }
            }
        }

        //* این تابع شکست نهایی بازاتصال را فقط بعد از پایان مهلت مشترک بازیابی نهایی می‌کند.
        private async void HandleReconnectFailed(string reason)
        {
            if (unifiedRecoveryRunning && HasUnifiedRecoveryTimeRemaining())
            {
                bool serverReachableDuringWindow = await IsServerReachableSilentlyAsync();

                if (!serverReachableDuringWindow)
                {
                    SetState(FlowState.WaitingForNetwork, reason);
                    return;
                }

                reconnect.totalTimeoutMs = GetUnifiedRecoveryRemainingMilliseconds();
                reconnect.maxAttempts = 0;
                reconnect.Start(TryReconnectOnceAsync);
                return;
            }

            bool serverReachable = await IsServerReachableSilentlyAsync();
            if (!serverReachable && unifiedRecoveryRunning && HasUnifiedRecoveryTimeRemaining())
            {
                SetState(FlowState.WaitingForNetwork, reason);
                return;
            }

            FailUnifiedRecovery(string.IsNullOrWhiteSpace(reason) ? "reconnect_failed" : reason);
        }

        #endregion

        #region رویدادهای اتصال

        //* این تابع انولوپ دریافتی را بدون آشکار کردن کلاینت داخلی برای مصرف کننده های صحنه منتشر می کند.
        private void HandleRealtimeEnvelopeReceived(RealtimeEnvelope envelope)
        {
            if (envelope == null) return;

            Action<RealtimeEnvelope> handler = RealtimeEnvelopeReceived;
            if (handler == null) return;

            try
            {
                handler(envelope);
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("REALTIME_ENVELOPE_CONSUMER", ex);
            }
        }

        //* این تابع تغییر وضعیت کُر را فقط برای گزارش و هماهنگی وضعیت مدیر دریافت می‌کند.
        private void HandleRealtimeClientStateChanged(RealtimeConnectionState state)
        {
            NetworkFileLogger.Info("REALTIME_CLIENT_STATE", "state=" + state);
        }

        //* این تابع خطای خام ترنسپورت را در گزارش ثبت می‌کند و نمایش آن را تا مشخص‌شدن وضعیت شبکه به تأخیر می‌اندازد.
        private void HandleTransportErrorReceived(string error)
        {
            NetworkFileLogger.Warning("REALTIME_TRANSPORT", error ?? string.Empty);
        }

        //* این تابع قطع اتصال غیرعمدی را به مسیر بازاتصال می‌فرستد و قطع عمدی را نادیده می‌گیرد.
        private async void HandleRealtimeDisconnected(string reason)
        {
            bool wasRealtimeReady = hasReachedRealtimeReady;

            StopHeartbeat();
            RememberRoomForReconnect();
            realtimeAuthClient?.ResetAuthState();
            CompleteAuthWaiter(false);

            if (wasRealtimeReady)
            {
                BeginUnifiedRecovery(reason ?? "realtime_disconnected");
                OnRealtimeDisconnected?.Invoke(reason ?? string.Empty);
            }

            NetworkFileLogger.Warning("REALTIME_DISCONNECTED", reason ?? string.Empty);

            if (suppressDisconnectHandling || applicationIsQuitting) return;

            CleanupClientObjects();
            if (!wasRealtimeReady) return;

            await BeginReconnectAfterUnexpectedDisconnectAsync(reason ?? "realtime_disconnected");
        }

        //* این تابع پاسخ موفق احراز هویت را ثبت و منتظر مربوط را کامل می‌کند.
        private void HandleRealtimeAuthenticated(string connectionId, string userId)
        {
            lastAuthenticationError = null;
            CompleteAuthWaiter(true);
            NetworkFileLogger.Info("REALTIME_AUTH_OK", "connectionId=" + (connectionId ?? string.Empty) + " | userId=" + (userId ?? string.Empty));
        }

        //* این تابع خطای احراز هویت را نگه می‌دارد و انتظار پاسخ را ناموفق کامل می‌کند.
        private void HandleRealtimeAuthenticationFailed(RealtimeError error)
        {
            lastAuthenticationError = error;
            CompleteAuthWaiter(false);
            NetworkFileLogger.Warning("REALTIME_AUTH_FAILED", FormatRealtimeError(error));
        }

        //* این تابع پس از عبور پینگ‌های ناموفق، اتصال فعلی را می‌بندد تا بازاتصال استاندارد آغاز شود.
        private async void HandleHeartbeatConnectionTimeout()
        {
            NetworkFileLogger.Warning("REALTIME_HEARTBEAT", "پاسخ پونگ در مهلت تعیین‌شده دریافت نشد.");
            if (realtimeClient == null || !realtimeClient.IsConnected) return;

            try
            {
                await realtimeClient.DisconnectAsync("heartbeat_timeout");
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("REALTIME_HEARTBEAT", ex);
            }
        }

        #endregion

        #region رویدادهای سراسری

        //* این تابع شنونده‌های شبکه و ورود را ثبت می‌کند تا مدیر در تمام صحنه‌ها جریان خود را بازیابی کند.
        private void BindGlobalEvents()
        {
            StartupNetworkSceneRouter.OnNetworkStateChanged += HandleNetworkStateChanged;
            GlobalAuthManager.OnAuthStateChanged += HandleAuthStateChanged;
            GlobalAuthManager.OnLoginReady += HandleLoginReady;
        }

        //* این تابع شنونده‌های شبکه و ورود را هنگام نابودی مدیر جدا می‌کند.
        private void UnbindGlobalEvents()
        {
            StartupNetworkSceneRouter.OnNetworkStateChanged -= HandleNetworkStateChanged;
            GlobalAuthManager.OnAuthStateChanged -= HandleAuthStateChanged;
            GlobalAuthManager.OnLoginReady -= HandleLoginReady;
        }

        //* این تابع هنگام قطع شبکه اتصال موقت را پاک و پس از برخط شدن همان مهلت مشترک بازیابی را ادامه می‌دهد.
        private async void HandleNetworkStateChanged(StartupNetworkSceneRouter.NetworkState state)
        {
            if (Instance != this || !hasLobbyEntryRequested) return;

            if (state != StartupNetworkSceneRouter.NetworkState.Online)
            {
                if (hasReachedRealtimeReady) BeginUnifiedRecovery("network_state=" + state);
                reconnect?.Stop();
                StopHeartbeat();
                RememberRoomForReconnect();
                SetState(FlowState.WaitingForNetwork, "network_state=" + state);
                GlobalMessageManager.Clear(RealtimeProgressMessageId);
                GlobalMessageManager.Clear(RealtimeErrorMessageId);
                GlobalMessageManager.Clear(BuildingsProgressMessageId);
                GlobalMessageManager.Clear(BuildingsErrorMessageId);
                await DisconnectTransportForTemporaryFailureAsync("network_state=" + state);
                return;
            }

            if (unifiedRecoveryRunning && !HasUnifiedRecoveryTimeRemaining())
            {
                FailUnifiedRecovery("network_recovered_after_client_recovery_deadline");
                return;
            }

            if (permanentRecoveryFailureWaitingForLobby)
            {
                SetState(IsGlobalAuthenticationReady() ? FlowState.Failed : FlowState.WaitingForAuthentication, "network_restored_waiting_for_fresh_lobby");
                NetworkFileLogger.Info("REALTIME_PERMANENT_RECOVERY", "Network restored, but automatic realtime reconnect is blocked until Lobby 1 is loaded.");
                return;
            }

            if (IsGlobalAuthenticationReady())
            {
                if (unifiedRecoveryRunning) await BeginReconnectAfterUnexpectedDisconnectAsync("network_recovered");
                else await EnsureRealtimeReadyAsync(hasReachedRealtimeReady);
            }
            else SetState(FlowState.WaitingForAuthentication, "network_online_waiting_for_login_init");
        }

        //* این تابع تغییر وضعیت ورود را دریافت می‌کند و فقط پس از ورود کامل اجازه اتصال ریل‌تایم می‌دهد.
        private void HandleAuthStateChanged(GlobalAuthManager.AuthState state)
        {
            if (Instance != this || !hasLobbyEntryRequested) return;
            if (state == GlobalAuthManager.AuthState.Authenticated) return;
            if (state == GlobalAuthManager.AuthState.WaitingForNetwork) SetState(FlowState.WaitingForNetwork, "auth_waiting_for_network");
            else SetState(FlowState.WaitingForAuthentication, "auth_state=" + state);
        }

        //* این تابع پس از ورود یا ورود مجدد موفق، اتصال ریل‌تایم را در همان مهلت مشترک بازیابی ادامه می‌دهد.
        private async void HandleLoginReady(AuthUserDto user)
        {
            if (Instance != this || !hasLobbyEntryRequested) return;
            if (StartupNetworkSceneRouter.Instance == null || !StartupNetworkSceneRouter.IsOnline) return;

            try
            {
                if (permanentRecoveryFailureWaitingForLobby)
                {
                    SetState(FlowState.Failed, "login_ready_waiting_for_fresh_lobby");
                    NetworkFileLogger.Info("REALTIME_PERMANENT_RECOVERY", "Authentication recovered, but old realtime and room recovery will not restart before Lobby 1.");
                    return;
                }

                if (unifiedRecoveryRunning)
                {
                    await BeginReconnectAfterUnexpectedDisconnectAsync("login_ready_during_recovery");
                    return;
                }

                bool ready = await EnsureRealtimeReadyAsync(hasReachedRealtimeReady);
                if (ready && loadBuildingsAfterRealtimeAuthentication) await RefreshCompletedBuildingsAsync();
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("REALTIME_AFTER_LOGIN_READY", ex);
            }
        }

        //* این تابع اتصال فعلی را برای قطع موقت شبکه بدون فعال‌کردن مسیر خروج رسمی آزاد می‌کند.
        private async Task DisconnectTransportForTemporaryFailureAsync(string reason)
        {
            suppressDisconnectHandling = true;

            try
            {
                if (realtimeClient != null && realtimeClient.IsConnected) await realtimeClient.DisconnectAsync(reason);
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("REALTIME_TEMP_DISCONNECT", ex);
            }
            finally
            {
                CleanupClientObjects();
                suppressDisconnectHandling = false;
            }
        }

        #endregion

        #region مهلت مشترک بازیابی ۲۱۰ ثانیه‌ای

        //* این تابع مهلت بازیابی قدیمی پروژه را فقط یک بار از لحظه نخست قطعی آغاز می‌کند.
        private void BeginUnifiedRecovery(string reason)
        {
            if (unifiedRecoveryRunning || applicationIsQuitting || suppressDisconnectHandling) return;

            unifiedRecoveryRunning = true;
            unifiedRecoveryStartedAt = Time.realtimeSinceStartup;
            unifiedRecoveryDeadlineAt = unifiedRecoveryStartedAt + GetPermanentReconnectFailureTimeoutSeconds();
            unifiedRecoveryReason = string.IsNullOrWhiteSpace(reason) ? "connection_lost" : reason.Trim();
            recoveryRequiresDedicatedGameServer = isJoinedRoom || shouldRejoinRoomAfterReconnect || !string.IsNullOrWhiteSpace(currentRoomId);

            if (unifiedRecoveryTimeoutCoroutine != null) StopCoroutine(unifiedRecoveryTimeoutCoroutine);
            unifiedRecoveryTimeoutCoroutine = StartCoroutine(UnifiedRecoveryTimeoutRoutine());

            NetworkFileLogger.Info("UNIFIED_RECOVERY_STARTED", "reason=" + unifiedRecoveryReason + " | serverGraceSeconds=" + ServerSessionReconnectGraceSeconds.ToString("F1") + " | clientAttemptSeconds=" + ClientReconnectAttemptTimeoutSeconds.ToString("F1") + " | safetyMarginSeconds=" + Mathf.Max(0f, ServerSessionReconnectGraceSeconds - ClientReconnectAttemptTimeoutSeconds).ToString("F1") + " | requiresDedicated=" + recoveryRequiresDedicatedGameServer + " | roomId=" + currentRoomId);
        }

        //* این تابع تا پایان مهلت ۱۸۰ ثانیه‌ای کلاینت صبر می‌کند و سپس شکست نهایی را اعلام می‌کند.
        private IEnumerator UnifiedRecoveryTimeoutRoutine()
        {
            while (unifiedRecoveryRunning && HasUnifiedRecoveryTimeRemaining()) yield return null;

            unifiedRecoveryTimeoutCoroutine = null;
            if (unifiedRecoveryRunning) FailUnifiedRecovery("unified_recovery_timeout:" + unifiedRecoveryReason);
        }

        //* این تابع هنگام قطع مستقل Game Server همان مهلت مشترک بازیابی را بدون ساخت تایمر تازه آغاز می‌کند.
        public void BeginUnifiedRecoveryFromDedicatedDisconnect(string reason)
        {
            if (!hasReachedRealtimeReady || string.IsNullOrWhiteSpace(currentRoomId)) return;
            BeginUnifiedRecovery(string.IsNullOrWhiteSpace(reason) ? "dedicated_disconnected" : reason.Trim());
            recoveryRequiresDedicatedGameServer = true;
        }

        //* این تابع پس از احراز موفق دوباره Game Server کل چرخه بازیابی مشترک را پایان می‌دهد.
        public void CompleteUnifiedRecoveryAfterGameServerAuthenticated(string reason)
        {
            if (!unifiedRecoveryRunning) return;
            CompleteUnifiedRecovery(string.IsNullOrWhiteSpace(reason) ? "dedicated_authenticated" : reason.Trim());
        }

        //* این تابع بازیابی موفق را جمع می‌کند و پیام‌های موقت ریل‌تایم و روم را پاک می‌کند.
        private void CompleteUnifiedRecovery(string reason)
        {
            if (!unifiedRecoveryRunning) return;

            float elapsedSeconds = RecoveryElapsedSeconds;
            CancelUnifiedRecovery(reason, true);
            GlobalMessageManager.Clear(RealtimeProgressMessageId);
            GlobalMessageManager.Clear(RealtimeErrorMessageId);
            GlobalMessageManager.Clear(RoomProgressMessageId);
            GlobalMessageManager.Clear(RoomErrorMessageId);
            NetworkFileLogger.Info("UNIFIED_RECOVERY_SUCCEEDED", "reason=" + (reason ?? string.Empty) + " | elapsedSeconds=" + elapsedSeconds.ToString("F1") + " | roomId=" + currentRoomId);
        }

        //* این تابع بازیابی ناموفق را پس از پایان بودجه مشترک نهایی می‌کند.
        private void FailUnifiedRecovery(string reason)
        {
            if (!unifiedRecoveryRunning && CurrentState == FlowState.Failed) return;

            string safeReason = string.IsNullOrWhiteSpace(reason) ? "unified_recovery_failed" : reason.Trim();
            float elapsedSeconds = unifiedRecoveryStartedAt >= 0f ? Mathf.Max(0f, Time.realtimeSinceStartup - unifiedRecoveryStartedAt) : 0f;
            CancelUnifiedRecovery(safeReason, false);
            reconnect?.Stop();
            StopHeartbeat();
            permanentRecoveryFailureWaitingForLobby = true;
            ClearRoomContext(true);
            SetState(FlowState.Failed, safeReason);
            NotifyRealtimeReconnectFailedPermanently(safeReason);
            ShowRealtimeFailure("اتصال کامل نشد. دوباره وارد روم شوید.", safeReason, true);
            NetworkFileLogger.Warning("UNIFIED_RECOVERY_FAILED", "reason=" + safeReason + " | elapsedSeconds=" + elapsedSeconds.ToString("F1") + " | serverGraceSeconds=" + ServerSessionReconnectGraceSeconds.ToString("F1"));
        }

        //* این تابع تایمر و وضعیت مشترک بازیابی را بدون حذف مدیر دائمی آزاد می‌کند.
        private void CancelUnifiedRecovery(string reason, bool succeeded)
        {
            if (unifiedRecoveryTimeoutCoroutine != null)
            {
                StopCoroutine(unifiedRecoveryTimeoutCoroutine);
                unifiedRecoveryTimeoutCoroutine = null;
            }

            if (!unifiedRecoveryRunning) return;

            unifiedRecoveryRunning = false;
            recoveryRequiresDedicatedGameServer = false;
            unifiedRecoveryStartedAt = -1f;
            unifiedRecoveryDeadlineAt = -1f;
            unifiedRecoveryReason = string.Empty;
            reconnect?.Stop();
            NetworkFileLogger.Info("UNIFIED_RECOVERY_STOPPED", "reason=" + (reason ?? string.Empty) + " | succeeded=" + succeeded);
        }

        //* این تابع مشخص می‌کند هنوز از مهلت مشترک بازیابی کلاینت زمان باقی مانده است یا نه.
        private bool HasUnifiedRecoveryTimeRemaining()
        {
            return unifiedRecoveryRunning && unifiedRecoveryDeadlineAt > Time.realtimeSinceStartup;
        }

        //* این تابع زمان باقی‌مانده مشترک را برای کنترلر Reconnect به میلی‌ثانیه تبدیل می‌کند.
        private int GetUnifiedRecoveryRemainingMilliseconds()
        {
            if (!unifiedRecoveryRunning) return GetPermanentReconnectFailureTimeoutMilliseconds();
            return Mathf.Max(1, Mathf.RoundToInt(RecoveryRemainingSeconds * 1000f));
        }

        //* این تابع زمان تلاش کلاینت را همان مقدار آزموده‌شده ۱۸۰ ثانیه نگه می‌دارد.
        private float GetPermanentReconnectFailureTimeoutSeconds()
        {
            float serverGrace = Mathf.Max(30f, serverSessionReconnectGraceSeconds);
            float requested = Mathf.Clamp(permanentReconnectFailureTimeoutSeconds, 5f, serverGrace);
            return Mathf.Min(requested, Mathf.Max(5f, serverGrace - 30f));
        }

        //* این تابع مهلت تلاش کلاینت را به میلی‌ثانیه تبدیل می‌کند.
        private int GetPermanentReconnectFailureTimeoutMilliseconds()
        {
            return Mathf.Max(1000, Mathf.RoundToInt(GetPermanentReconnectFailureTimeoutSeconds() * 1000f));
        }

        #endregion

        #region پیام‌ها و خطاها

        //* این تابع پیام پیشرفت ریل‌تایم را فقط زمانی منتشر می‌کند که مدیر شبکه مالک پیام مهم‌تری نباشد.
        private void PublishRealtimeProgress(string message, string technicalDetails)
        {
            if (!CanShowRealtimeOrRequestMessage()) return;

            GlobalMessageManager.Publish(RealtimeProgressMessageId, GlobalMessageManager.MessageSource.Realtime, GlobalMessageManager.MessageType.Information, GlobalMessageManager.Priorities.Reconnecting, "اتصال به Realtime", message, technicalDetails ?? string.Empty, 0f, true, false);
        }

        //* این تابع پیام دریافت ساختمان‌ها را بدون امکان بستن تا پایان درخواست نمایش می‌دهد.
        private void PublishBuildingsProgress(string message, string technicalDetails)
        {
            if (!CanShowRealtimeOrRequestMessage()) return;

            GlobalMessageManager.Publish(BuildingsProgressMessageId, GlobalMessageManager.MessageSource.Request, GlobalMessageManager.MessageType.Information, GlobalMessageManager.Priorities.Reconnecting, "ساختمان‌ها", message, technicalDetails ?? string.Empty, 0f, true, false);
        }

        //* این تابع شکست جریان ریل‌تایم را پس از بررسی بی‌صدای شبکه مدیریت می‌کند.
        private async Task HandleRealtimeFlowFailureAsync(string userMessage, string technicalDetails, bool reconnecting)
        {
            StopHeartbeat();
            await CleanupFailedConnectionAsync(technicalDetails);
            GlobalMessageManager.Clear(RealtimeProgressMessageId);
            bool serverReachable = await IsServerReachableSilentlyAsync();

            if (!serverReachable)
            {
                reconnect?.Stop();
                SetState(FlowState.WaitingForNetwork, technicalDetails);
                return;
            }

            if (reconnecting)
            {
                SetState(FlowState.Reconnecting, technicalDetails);
                return;
            }

            SetState(FlowState.Failed, technicalDetails);
            ShowRealtimeFailure(userMessage, technicalDetails, true);
        }

        //* این تابع اتصال ناموفق یا احراز هویت ناقص را بدون فعال‌کردن مسیر بازاتصال آزاد می‌کند.
        private async Task CleanupFailedConnectionAsync(string reason)
        {
            suppressDisconnectHandling = true;

            try
            {
                if (realtimeClient != null && realtimeClient.IsConnected) await realtimeClient.DisconnectAsync("realtime_flow_failed:" + (reason ?? string.Empty));
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("REALTIME_FAILED_CONNECTION_CLEANUP", ex);
            }
            finally
            {
                CleanupClientObjects();
                suppressDisconnectHandling = false;
            }
        }

        //* این تابع خطای واقعی ریل‌تایم را با امکان تلاش دوباره نمایش می‌دهد.
        private void ShowRealtimeFailure(string userMessage, string technicalDetails, bool allowRetry)
        {
            if (!CanShowRealtimeOrRequestMessage()) return;

            GlobalMessageManager.ShowError(RealtimeErrorMessageId, "اتصال به Realtime", userMessage, technicalDetails ?? string.Empty, 0f, false, GlobalMessageManager.MessageSource.Realtime, allowRetry, allowRetry ? RetryEnterLobbyAsync : null);
        }

        //* این تابع شکست دریافت ساختمان‌ها را فقط در صورت سالم‌بودن شبکه نمایش می‌دهد.
        private async Task ShowBuildingsFailureIfNeededAsync(CompletedBuildingsLoadResult result)
        {
            bool serverReachable = !result.IsNetworkError || await IsServerReachableSilentlyAsync();
            if (!serverReachable || !CanShowRealtimeOrRequestMessage()) return;

            GlobalMessageManager.ShowError(BuildingsErrorMessageId, "دریافت ساختمان‌ها", string.IsNullOrWhiteSpace(result.ErrorMessage) ? "فهرست ساختمان‌ها دریافت نشد." : result.ErrorMessage, result.TechnicalDetails, 0f, false, GlobalMessageManager.MessageSource.Request, true, RetryBuildingsAsync);
        }

        //* این تابع مشخص می‌کند پیام ریل‌تایم یا درخواست در وضعیت فعلی اجازه نمایش دارد یا نه.
        private bool CanShowRealtimeOrRequestMessage()
        {
            return StartupNetworkSceneRouter.Instance != null && StartupNetworkSceneRouter.IsOnline && !GlobalMessageManager.HasActiveNetworkPriorityMessage;
        }

        //* این تابع دکمه تلاش دوباره پیام ریل‌تایم را به جریان کامل ورود لابی متصل می‌کند.
        private async Task RetryEnterLobbyAsync()
        {
            await EnterLobbyAsync();
        }

        //* این تابع دکمه تلاش دوباره ساختمان‌ها را فقط به دریافت دوباره فهرست متصل می‌کند.
        private async Task RetryBuildingsAsync()
        {
            await RefreshCompletedBuildingsAsync();
        }

        #endregion

        #region ابزارهای داخلی

        //* این تابع پس از شکست دریافت ساختمان‌ها، وضعیت مدیر را بر اساس شبکه، ورود و اتصال فعلی بازمی‌گرداند.
        private void RestoreStateAfterBuildingsFailure(string details)
        {
            if (StartupNetworkSceneRouter.Instance == null || !StartupNetworkSceneRouter.IsOnline) SetState(FlowState.WaitingForNetwork, details);
            else if (!IsGlobalAuthenticationReady()) SetState(FlowState.WaitingForAuthentication, details);
            else if (IsRealtimeReady) SetState(ResolveReadyState(), details);
            else SetState(FlowState.Failed, details);
        }

        //* این تابع بررسی می‌کند مدیر ورود در وضعیت نهایی و دارای کاربر معتبر باشد.
        private bool IsGlobalAuthenticationReady()
        {
            return GlobalAuthManager.Instance != null && GlobalAuthManager.Instance.isLogin && GlobalAuthManager.Instance.CurrentUser != null && GlobalAuthManager.CurrentAuthState == GlobalAuthManager.AuthState.Authenticated;
        }

        //* این تابع مشخص می‌کند اکسس توکن خالی، منقضی یا نزدیک زمان انقضا است یا نه.
        private bool IsAccessTokenRefreshRequired(string accessToken)
        {
            if (string.IsNullOrWhiteSpace(accessToken)) return true;
            if (!TryReadJwtExpiryUnixSeconds(accessToken, out long expiresAtUnixSeconds)) return false;

            long nowUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int safeSkewSeconds = Mathf.Clamp(accessTokenRefreshSkewSeconds, 0, 3600);
            return expiresAtUnixSeconds <= nowUnixSeconds + safeSkewSeconds;
        }

        //* این تابع زمان انقضای توکن را از بخش اطلاعات داخلی آن می‌خواند.
        private static bool TryReadJwtExpiryUnixSeconds(string token, out long expiresAtUnixSeconds)
        {
            expiresAtUnixSeconds = 0;
            string payloadJson = ReadJwtPayloadJson(token);
            if (string.IsNullOrWhiteSpace(payloadJson)) return false;
            return TryExtractJsonLongValue(payloadJson, "exp", out expiresAtUnixSeconds);
        }

        //* این تابع بخش اطلاعات توکن را از قالب رمزگذاری نشانی‌پسند به متن جیسون تبدیل می‌کند.
        private static string ReadJwtPayloadJson(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return string.Empty;
            string[] parts = token.Split('.');
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1])) return string.Empty;

            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            int padding = payload.Length % 4;
            if (padding == 2) payload += "==";
            else if (padding == 3) payload += "=";
            else if (padding != 0) return string.Empty;

            try
            {
                return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            }
            catch
            {
                return string.Empty;
            }
        }

        //* این تابع مقدار عددی یک کلید جیسون را بدون کتابخانه اضافی استخراج می‌کند.
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
            while (valueEnd < json.Length && char.IsDigit(json[valueEnd])) valueEnd++;
            if (valueEnd <= valueStart) return false;
            return long.TryParse(json.Substring(valueStart, valueEnd - valueStart), out value);
        }

        //* این تابع بررسی می‌کند خطای احراز هویت به انقضا یا نامعتبر بودن توکن مربوط باشد.
        private bool IsAuthenticationTokenFailure(RealtimeError error)
        {
            if (error == null) return false;
            string value = ((error.code ?? string.Empty) + " " + (error.message ?? string.Empty)).ToUpperInvariant();
            return error.IsTokenExpired() || value.Contains("TOKEN") || value.Contains("JWT") || value.Contains("AUTH_REQUIRED") || value.Contains("UNAUTHENTICATED");
        }

        //* این تابع خطای ریل‌تایم را برای گزارش فنی به متن امن تبدیل می‌کند.
        private string FormatRealtimeError(RealtimeError error)
        {
            return error == null ? "unknown" : "code=" + (error.code ?? string.Empty) + " | message=" + (error.message ?? string.Empty) + " | details=" + (error.detailsJson ?? string.Empty);
        }

        //* این تابع انتظار احراز هویت جاری را با نتیجه داده‌شده کامل می‌کند.
        private void CompleteAuthWaiter(bool result)
        {
            TaskCompletionSource<bool> waiter = authWaiter;
            authWaiter = null;
            waiter?.TrySetResult(result);
        }

        //* این تابع یک انتظار تازه برای پاسخ احراز هویت می‌سازد.
        private static TaskCompletionSource<bool> CreateBoolWaiter()
        {
            return new TaskCompletionSource<bool>();
        }

        //* این تابع هارت‌بیت را فقط پس از اتصال و احراز هویت کامل آغاز می‌کند.
        private void StartHeartbeat()
        {
            if (!enableHeartbeat || heartbeat == null || !IsRealtimeReady) return;
            heartbeat.Reset();
            heartbeat.Start();
        }

        //* این تابع هارت‌بیت فعال را بدون تغییر وضعیت اتصال متوقف می‌کند.
        private void StopHeartbeat()
        {
            heartbeat?.Stop();
        }

        //* این تابع شکست نهایی بازاتصال را به شنونده‌های گیم سرور اعلام می‌کند.
        private void NotifyRealtimeReconnectFailedPermanently(string reason)
        {
            Action<string> handler = OnRealtimeReconnectFailedPermanently;
            if (handler == null) return;

            try
            {
                handler(reason ?? string.Empty);
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("REALTIME_RECONNECT_FAILED_EVENT", ex);
            }
        }

        //* این تابع رویداد آماده‌شدن ریل‌تایم را با محافظ خطا اجرا می‌کند.
        private void NotifyRealtimeReady()
        {
            Action handler = OnRealtimeReady;
            if (handler == null) return;

            try
            {
                handler();
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("REALTIME_READY_EVENT", ex);
            }
        }

        //* این تابع نسخه کامل فهرست ساختمان‌ها را به شنونده‌های رابط لابی تحویل می‌دهد.
        private void NotifyBuildingsUpdated()
        {
            Action<IReadOnlyList<CompletedBuildingDto>> handler = OnBuildingsUpdated;
            if (handler == null) return;

            try
            {
                handler(completedBuildings.AsReadOnly());
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("REALTIME_BUILDINGS_EVENT", ex);
            }
        }

        //* این تابع وضعیت مدیر را تغییر و رویداد مربوط را فقط هنگام تغییر واقعی اجرا می‌کند.
        private void SetState(FlowState state, string details)
        {
            FlowState previous = CurrentState;
            CurrentState = state;
            if (previous == state) return;

            NetworkFileLogger.Info("REALTIME_MANAGER_STATE", "previous=" + previous + " | current=" + state + " | details=" + (details ?? string.Empty));
            Action<FlowState> handler = OnStateChanged;
            if (handler == null) return;

            try
            {
                handler(state);
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("REALTIME_MANAGER_STATE_EVENT", ex);
            }
        }

        //* این تابع توکن لغو عملیات ورودی را به طول عمر مدیر متصل می‌کند.
        private CancellationTokenSource CreateLinkedLifecycleToken(CancellationToken cancellationToken, bool limitToUnifiedRecovery = false)
        {
            CancellationTokenSource linkedCts = lifecycleCts == null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifecycleCts.Token);

            if (limitToUnifiedRecovery && unifiedRecoveryRunning) linkedCts.CancelAfter(GetUnifiedRecoveryRemainingMilliseconds());
            return linkedCts;
        }

        #endregion
    }
}
