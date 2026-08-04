using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Bootstrap;
using Network_A.Realtime.Controllers;
using Network_A.Tests.Realtime;
using Network_A.UI;
using UnityEngine;

namespace Network_A.DedicatedGameServer.Client
{
    public class DedicatedRemotePlayerViewController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private DedicatedGameServerWsClient wsClient;
        [SerializeField] private DedicatedRemotePlayerStateReceiver remoteStateReceiver;
        [SerializeField] private G7ThreeDModeController threeDModeController;
        [SerializeField] private RealtimeRoomGameServerManager realtimeRoomGameServerManager;
        [SerializeField] private RealtimeWebSocketG7RoomLobbyTestController realtimeRoomController;
        [SerializeField] private RealtimeGrpcStreamingG7RoomLobbyTestController grpcStreamingRealtimeRoomController;
        [SerializeField] private DedicatedPlayerStateAutoSender legacyPlayerStateAutoSender;

        [Header("Startup")]
        [SerializeField] private bool autoEnter3DModeAfterDedicatedAuth = true;
        [SerializeField] private bool ensureLocalPlayerAfterDedicatedAuth = true;
        [SerializeField] private bool setLocalNameAfterDedicatedAuth = true;
        [SerializeField] private bool autoStartLocalStateSenderAfterDedicatedAuth = true;
        [SerializeField] private bool disableLegacyDedicatedPlayerStateAutoSender = true;
        [SerializeField] private bool applyReceiverSnapshotAfterAuth = true;

        [Header("Local State Send")]
        [SerializeField] private float sendRatePerSecond = 12f;
        [SerializeField] private bool sendOnlyWhenChanged = true;
        [SerializeField] private float minPositionDelta = 0.015f;
        [SerializeField] private float minRotationDelta = 0.75f;
        [SerializeField] private float unchangedStateHeartbeatSeconds = 2f;

        [Header("Remote Cleanup")]
        [SerializeField] private bool clearRemotePlayersOnDedicatedDisconnect = true;
        [SerializeField] private bool removeRemotePlayersWhenStateTimeout = true;
        [SerializeField] private float remotePlayerStateTimeoutSeconds = 8f;
        [SerializeField] private float remotePlayerReconnectGraceSeconds = 210f;
        [SerializeField] private float remoteTimeoutCheckIntervalSeconds = 1f;

        [Header("Remote Temporary Deactivation")]
        [SerializeField] private bool deactivateRemotePlayerWhenStateSilent = true;
        [SerializeField] private float remotePlayerDeactivateAfterStateSilenceSeconds = 3f;
        [SerializeField] private float remotePlayerDeactivateCheckIntervalSeconds = 0.25f;

        [Header("Remote Cleanup Safety")]
        [SerializeField] private bool useServerLeaveAsPrimaryRemoteRemoval = true;
        [SerializeField] private bool preserveRemotePlayersDuringRealtimeReconnect = true;
        [SerializeField] private bool preserveRemotePlayersOnTransientDedicatedDisconnect = true;
        [SerializeField] private bool clearRemotePlayersAfterPermanentReconnectFailure = true;
        [SerializeField] private bool refreshRemoteTimeoutWindowAfterReconnect = true;
        [SerializeField] private bool forceSceneSweepRemoteViewsOnManualDedicatedDisconnect = true;
        [SerializeField] private bool forceSceneSweepRemoteViewsOnRealtimeRoomLeft = true;
        [SerializeField] private bool neverDestroySharedWorld3DRootDuringSceneSweep = true;
        [SerializeField] private bool autoProtectSharedWorld3DRootByName = true;
        [SerializeField] private string sharedWorld3DRootName = "Shared_World_3D_Root";
        [SerializeField] private GameObject sharedWorld3DRoot;
        [SerializeField] private Transform[] protectedSceneRoots;
        [SerializeField] private float remoteTimeoutStaleLogIntervalSeconds = 5f;

        [Header("Realtime Leave Sync")]
        [SerializeField] private bool disconnectDedicatedOnRealtimeRoomLeft = true;
        [SerializeField] private bool stopLocalStateOnRealtimeRoomLeft = true;
        [SerializeField] private bool clearRemotePlayersOnRealtimeRoomLeft = true;

        [Header("Dedicated Disconnect Suppression")]
        [SerializeField] private bool suppressRealtimeRespawnAfterDedicatedLeft = true;
        [SerializeField] private float suppressRealtimeRespawnSeconds = 300f;
        [SerializeField] private float suppressRealtimeRespawnCheckIntervalSeconds = 0.25f;
        [Header("Dedicated Presence UI Source")]
        [SerializeField] private bool emitDedicatedPresenceUiEvents = true;
        [SerializeField] private bool emitJoinedFromDedicatedPresenceEvent = true;
        [SerializeField] private bool emitJoinedFromFirstDedicatedState = true;
        [SerializeField] private bool confirmDedicatedLeftByStateTimeout = true;
        [SerializeField] private bool emitLeftWhenDedicatedStateTimeouts = true;
        [SerializeField] private float dedicatedLeftStateSilenceConfirmSeconds = 1.5f;
        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;
        [SerializeField] private bool logLocalSend = false;
        [SerializeField] private bool logRemoteApply = true;
        [SerializeField] private bool logLocalMovementSendToLogText = true;
        [SerializeField] private float localMovementSendLogIntervalSeconds = 0.5f;
        private float nextLocalMovementSendLogTime;
        private bool isDedicatedGameplayActive;
        private bool isLocalSendInFlight;
        private bool hasLastSentState;
        private long localSequence;
        private float nextSendTime;
        private float lastStateHeartbeatSendTime;
        private float nextRemoteTimeoutCheckTime;
        private float nextRemoteDeactivateCheckTime;
        private Vector3 lastSentPosition;
        private Quaternion lastSentRotation = Quaternion.identity;
        private CancellationTokenSource localSendCts;
        private readonly Dictionary<string, float> dict_remoteLastSeenTimeByPlayerId = new Dictionary<string, float>();
        private readonly Dictionary<string, float> dict_remoteLastDedicatedStateTimeByPlayerId = new Dictionary<string, float>();
        private readonly Dictionary<string, string> dict_remoteNamesByPlayerId = new Dictionary<string, string>();
        private readonly Dictionary<string, float> dict_suppressedRemoteRespawnUntilByPlayerId = new Dictionary<string, float>();
        private float nextSuppressedRemoteRemovalCheckTime;
        private bool realtimeReconnectInProgress;
        private bool realtimeReconnectFailedPermanently;
        private bool remoteCleanupAppliedForCurrentExit;
        private float nextRemoteTimeoutStaleLogTime;
        private float nextRemoteRecoveryPreserveLogTime;

        public event Action<string, string> DedicatedRemotePlayerJoinedForUi;
        public event Action<string, string> DedicatedRemotePlayerLeftForUi;
        public event Action<int> DedicatedRoomOnlineCountChangedForUi;
        private readonly Dictionary<string, float> dict_pendingDedicatedLeftTimeByPlayerId = new Dictionary<string, float>();
        private readonly Dictionary<string, string> dict_pendingDedicatedLeftReasonByPlayerId = new Dictionary<string, string>();
        private readonly HashSet<string> set_remoteJoinedUiShownByPlayerId = new HashSet<string>();
        private readonly HashSet<string> set_remoteBrowserHiddenPlayerIds = new HashSet<string>();
        private bool hasWebGLVisibilityRevision;
        private int lastWebGLVisibilityRevision;
        //* این تابع رفرنس های لازم را در شروع آبجکت پیدا می کند.
        private void Awake()
        {
            ResolveReferences();
            ApplyLegacySenderPolicy();
        }

        //* این تابع هنگام فعال شدن آبجکت، ایونت های ددیکیتد را وصل می کند.
        private void OnEnable()
        {
            ResolveReferences();
            ApplyLegacySenderPolicy();
            BindEvents();

            if (wsClient != null && wsClient.IsAuthenticated)
            {
                BeginDedicatedGameplayAfterAuth();
            }
        }

        //* این تابع هنگام غیرفعال شدن آبجکت، ایونت ها و ارسال لوکال را متوقف می کند.
        private void OnDisable()
        {
            UnbindEvents();
            StopLocalStateSending("disabled");
        }

        //* این تابع هر فریم ارسال لوکال و پاکسازی ریموت های قدیمی را مدیریت می کند.
        private void Update()
        {
            bool localWebGLDocumentHidden = HandleLocalWebGLVisibilityLifecycle();

            if (!localWebGLDocumentHidden)
            {
                DeactivateStateSilentRemotePlayers();
            }

            RemoveTimedOutRemotePlayers();
            EnforceSuppressedRemoteRemoval();
            TickLocalStateSend();
        }

        //* این تابع از اینسپکتور مسیر گیم پلی ددیکیتد را دستی فعال می کند.
        [ContextMenu("Begin Dedicated Remote View")]
        public void Btn_BeginDedicatedRemoteView()
        {
            BeginDedicatedGameplayAfterAuth();
        }

        //* این تابع از اینسپکتور ارسال وضعیت لوکال را دستی شروع می کند.
        [ContextMenu("Start Dedicated Local State Send")]
        public void Btn_StartLocalStateSending()
        {
            StartLocalStateSending();
        }

        //* این تابع از اینسپکتور ارسال وضعیت لوکال را دستی متوقف می کند.
        [ContextMenu("Stop Dedicated Local State Send")]
        public void Btn_StopLocalStateSending()
        {
            StopLocalStateSending("manual_stop");
        }

        //* این تابع از اینسپکتور کلون های ریموت ددیکیتد را پاک می کند.
        [ContextMenu("Clear Dedicated Remote Players")]
        public void Btn_ClearDedicatedRemotePlayers()
        {
            ForceClearRemotePlayersAfterDedicatedDisconnect("manual_context_clear", true, "context_menu_clear_v3");
        }

        //* این تابع رفرنس های خالی را از صحنه پیدا می کند.
        private void ResolveReferences()
        {
            if (wsClient == null) wsClient = DedicatedGameServerWsClient.Instance;
            if (wsClient == null) wsClient = FindObjectOfType<DedicatedGameServerWsClient>(true);
            if (remoteStateReceiver == null) remoteStateReceiver = FindObjectOfType<DedicatedRemotePlayerStateReceiver>(true);
            if (threeDModeController == null) threeDModeController = FindObjectOfType<G7ThreeDModeController>(true);
            if (realtimeRoomGameServerManager == null) realtimeRoomGameServerManager = RealtimeRoomGameServerManager.Instance;
            if (realtimeRoomController == null) realtimeRoomController = FindObjectOfType<RealtimeWebSocketG7RoomLobbyTestController>(true);
            if (grpcStreamingRealtimeRoomController == null) grpcStreamingRealtimeRoomController = FindObjectOfType<RealtimeGrpcStreamingG7RoomLobbyTestController>(true);
            if (legacyPlayerStateAutoSender == null) legacyPlayerStateAutoSender = FindObjectOfType<DedicatedPlayerStateAutoSender>(true);
        }

        //* این تابع ارسال کننده قدیمی را برای جلوگیری از ارسال از ترنسفورم اشتباه خاموش می کند.
        private void ApplyLegacySenderPolicy()
        {
            if (!disableLegacyDedicatedPlayerStateAutoSender) return;
            if (legacyPlayerStateAutoSender == null) return;
            if (!legacyPlayerStateAutoSender.enabled) return;

            legacyPlayerStateAutoSender.StopSending("ds9_remote_view_controller_takes_over");
            legacyPlayerStateAutoSender.enabled = false;
            Log("Legacy DedicatedPlayerStateAutoSender disabled. DS-9 controller sends local player state.");
        }

        //* این تابع ایونت های ددیکیتد را وصل می کند.
        private void BindEvents()
        {
            if (wsClient != null)
            {
                wsClient.Authenticated -= HandleDedicatedAuthenticated;
                wsClient.Disconnected -= HandleDedicatedDisconnected;

                wsClient.Authenticated += HandleDedicatedAuthenticated;
                wsClient.Disconnected += HandleDedicatedDisconnected;
            }

            if (remoteStateReceiver != null)
            {
                remoteStateReceiver.RemotePlayerStateReceived -= HandleRemotePlayerStateReceived;
                remoteStateReceiver.RemotePlayerJoined -= HandleRemotePlayerJoined;
                remoteStateReceiver.RemotePlayerLeft -= HandleRemotePlayerLeft;
                remoteStateReceiver.RemotePlayerVisibilityChanged -= HandleRemotePlayerVisibilityChanged;

                remoteStateReceiver.RemotePlayerStateReceived += HandleRemotePlayerStateReceived;
                remoteStateReceiver.RemotePlayerJoined += HandleRemotePlayerJoined;
                remoteStateReceiver.RemotePlayerLeft += HandleRemotePlayerLeft;
                remoteStateReceiver.RemotePlayerVisibilityChanged += HandleRemotePlayerVisibilityChanged;
            }

            if (realtimeRoomGameServerManager != null)
            {
                RealtimeRoomGameServerManager.OnRealtimeReady -= HandleRealtimeReadyAfterRecovery;
                RealtimeRoomGameServerManager.OnRoomJoinedFor3D -= HandleRealtimeRoomJoinedAfterRecovery;
                RealtimeRoomGameServerManager.OnRoomLeftFor3D -= HandleRealtimeRoomLeft;
                RealtimeRoomGameServerManager.OnRealtimeDisconnected -= HandleRealtimeConnectionLostForReconnect;
                RealtimeRoomGameServerManager.OnRealtimeReconnectFailedPermanently -= HandleRealtimeReconnectFailedPermanently;

                RealtimeRoomGameServerManager.OnRealtimeReady += HandleRealtimeReadyAfterRecovery;
                RealtimeRoomGameServerManager.OnRoomJoinedFor3D += HandleRealtimeRoomJoinedAfterRecovery;
                RealtimeRoomGameServerManager.OnRoomLeftFor3D += HandleRealtimeRoomLeft;
                RealtimeRoomGameServerManager.OnRealtimeDisconnected += HandleRealtimeConnectionLostForReconnect;
                RealtimeRoomGameServerManager.OnRealtimeReconnectFailedPermanently += HandleRealtimeReconnectFailedPermanently;
            }

            if (realtimeRoomController != null)
            {
                realtimeRoomController.OnRoomLeftFor3D -= HandleRealtimeRoomLeft;
                realtimeRoomController.OnRealtimeConnectionLostForReconnectFor3D -= HandleRealtimeConnectionLostForReconnect;
                realtimeRoomController.OnRealtimeReconnectFailedPermanentlyFor3D -= HandleRealtimeReconnectFailedPermanently;

                realtimeRoomController.OnRoomLeftFor3D += HandleRealtimeRoomLeft;
                realtimeRoomController.OnRealtimeConnectionLostForReconnectFor3D += HandleRealtimeConnectionLostForReconnect;
                realtimeRoomController.OnRealtimeReconnectFailedPermanentlyFor3D += HandleRealtimeReconnectFailedPermanently;
            }

            if (grpcStreamingRealtimeRoomController != null)
            {
                grpcStreamingRealtimeRoomController.OnRoomLeftFor3D -= HandleRealtimeRoomLeft;
                grpcStreamingRealtimeRoomController.OnRealtimeConnectionLostForReconnectFor3D -= HandleRealtimeConnectionLostForReconnect;
                grpcStreamingRealtimeRoomController.OnRealtimeReconnectFailedPermanentlyFor3D -= HandleRealtimeReconnectFailedPermanently;

                grpcStreamingRealtimeRoomController.OnRoomLeftFor3D += HandleRealtimeRoomLeft;
                grpcStreamingRealtimeRoomController.OnRealtimeConnectionLostForReconnectFor3D += HandleRealtimeConnectionLostForReconnect;
                grpcStreamingRealtimeRoomController.OnRealtimeReconnectFailedPermanentlyFor3D += HandleRealtimeReconnectFailedPermanently;
            }
        }

        //* این تابع ایونت های وصل شده را قطع می کند.
        private void UnbindEvents()
        {
            if (wsClient != null)
            {
                wsClient.Authenticated -= HandleDedicatedAuthenticated;
                wsClient.Disconnected -= HandleDedicatedDisconnected;
            }

            if (remoteStateReceiver != null)
            {
                remoteStateReceiver.RemotePlayerStateReceived -= HandleRemotePlayerStateReceived;
                remoteStateReceiver.RemotePlayerJoined -= HandleRemotePlayerJoined;
                remoteStateReceiver.RemotePlayerLeft -= HandleRemotePlayerLeft;
                remoteStateReceiver.RemotePlayerVisibilityChanged -= HandleRemotePlayerVisibilityChanged;
            }

            if (realtimeRoomGameServerManager != null)
            {
                RealtimeRoomGameServerManager.OnRealtimeReady -= HandleRealtimeReadyAfterRecovery;
                RealtimeRoomGameServerManager.OnRoomJoinedFor3D -= HandleRealtimeRoomJoinedAfterRecovery;
                RealtimeRoomGameServerManager.OnRoomLeftFor3D -= HandleRealtimeRoomLeft;
                RealtimeRoomGameServerManager.OnRealtimeDisconnected -= HandleRealtimeConnectionLostForReconnect;
                RealtimeRoomGameServerManager.OnRealtimeReconnectFailedPermanently -= HandleRealtimeReconnectFailedPermanently;
            }

            if (realtimeRoomController != null)
            {
                realtimeRoomController.OnRoomLeftFor3D -= HandleRealtimeRoomLeft;
                realtimeRoomController.OnRealtimeConnectionLostForReconnectFor3D -= HandleRealtimeConnectionLostForReconnect;
                realtimeRoomController.OnRealtimeReconnectFailedPermanentlyFor3D -= HandleRealtimeReconnectFailedPermanently;
            }

            if (grpcStreamingRealtimeRoomController != null)
            {
                grpcStreamingRealtimeRoomController.OnRoomLeftFor3D -= HandleRealtimeRoomLeft;
                grpcStreamingRealtimeRoomController.OnRealtimeConnectionLostForReconnectFor3D -= HandleRealtimeConnectionLostForReconnect;
                grpcStreamingRealtimeRoomController.OnRealtimeReconnectFailedPermanentlyFor3D -= HandleRealtimeReconnectFailedPermanently;
            }
        }

        //* این تابع بعد از احراز ددیکیتد، مسیر نمایش ریموت پلیرها را آماده می کند.
        private void HandleDedicatedAuthenticated()
        {
            BeginDedicatedGameplayAfterAuth();
        }

        //* این تابع بعد از احراز ددیکیتد، گیم پلی را آماده می کند و هنگام ریکانکت موقعیت پلیر موجود را حفظ می کند.
        private void BeginDedicatedGameplayAfterAuth()
        {
            ResolveReferences();
            ApplyLegacySenderPolicy();

            bool wasRealtimeReconnectInProgress = realtimeReconnectInProgress;
            remoteCleanupAppliedForCurrentExit = false;

            if (wsClient == null || !wsClient.IsAuthenticated)
            {
                Log("Begin ignored. Dedicated websocket is not authenticated yet.");
                return;
            }

            if (threeDModeController == null)
            {
                Debug.LogError(
                    "[DedicatedRemotePlayerViewController] G7ThreeDModeController is missing."
                );

                return;
            }

            bool hasResumeState = wsClient.TryConsumePendingPlayerResumeState(
                out Vector3 resumePosition,
                out Quaternion resumeRotation,
                out Vector3 resumeVelocity,
                out long resumeSequence
            );

            if (setLocalNameAfterDedicatedAuth)
            {
                threeDModeController.SetLocalPlayerDisplayName(
                    ResolveLocalDisplayName()
                );
            }

            if (autoEnter3DModeAfterDedicatedAuth &&
                !threeDModeController.IsThreeDModeActive)
            {
                if (wasRealtimeReconnectInProgress || hasResumeState)
                {
                    threeDModeController.EnterThreeDModePreservingLocalPlayer();

                    Log(
                        "3D mode restored for dedicated reconnect without resetting local player."
                    );
                }
                else
                {
                    threeDModeController.EnterThreeDMode();
                }
            }
            else if (
                ensureLocalPlayerAfterDedicatedAuth &&
                threeDModeController.GetLocalPlayerTransform() == null
            )
            {
                threeDModeController.EnsureLocalPlayerSpawned();

                Log(
                    "Local player created after dedicated auth because no existing local transform was present."
                );
            }

            long initialSequence = Math.Max(0L, localSequence);

            if (hasResumeState)
            {
                bool resumeApplied =
                    threeDModeController.ApplyLocalPlayerAuthoritativeTransform(
                        resumePosition,
                        resumeRotation
                    );

                if (!resumeApplied)
                {
                    isDedicatedGameplayActive = false;

                    Debug.LogError(
                        "[DedicatedRemotePlayerViewController] Dedicated player resume state could not be applied. Local sender was not started."
                    );

                    return;
                }

                initialSequence = Math.Max(0L, resumeSequence);

                Log(
                    "Authoritative local player resume applied | sequence=" +
                    initialSequence +
                    " | position=" +
                    resumePosition +
                    " | velocity=" +
                    resumeVelocity
                );
            }
            else if (wasRealtimeReconnectInProgress)
            {
                Debug.LogWarning(
                    "[DedicatedRemotePlayerViewController] Dedicated reconnect completed without server resume state. Existing local transform and sequence are preserved as fallback."
                );
            }

            MarkRealtimeReconnectRecovered("dedicated_authenticated");

            isDedicatedGameplayActive = true;
            ClearSuppressedRemoteRespawnCache("dedicated_authenticated");

            if (applyReceiverSnapshotAfterAuth)
            {
                ApplyReceiverSnapshot();
            }

            if (autoStartLocalStateSenderAfterDedicatedAuth)
            {
                StartLocalStateSending(initialSequence);
            }
            else
            {
                ResetLocalSendState(initialSequence);
            }

            Log(
                "Dedicated remote view ready | roomId=" +
                SafeForLog(wsClient.RoomId) +
                " | playerId=" +
                SafeForLog(wsClient.PlayerId) +
                " | resumeApplied=" +
                hasResumeState +
                " | initialSequence=" +
                initialSequence
            );
        }

        //* این تابع بعد از قطع ددیکیتد، ارسال لوکال و کلون ها را بر اساس نوع قطع مدیریت می کند.
        //* این تابع بعد از قطع ددیکیتد، ارسال لوکال و کلون ها را بر اساس نوع قطع مدیریت می کند.
        private void HandleDedicatedDisconnected(string reason)
        {
            string safeReason = string.IsNullOrWhiteSpace(reason)
                ? "unknown_dedicated_disconnect"
                : reason.Trim();

            isDedicatedGameplayActive = false;
            StopLocalStateSending(safeReason);

            if (IsManualDedicatedDisconnectReason(safeReason))
            {
                realtimeReconnectInProgress = false;
                realtimeReconnectFailedPermanently = false;

                ForceClearRemotePlayersAfterDedicatedDisconnect(
                    safeReason,
                    true,
                    "manual_dedicated_disconnect_v2"
                );

                remoteCleanupAppliedForCurrentExit = true;
                return;
            }

            remoteCleanupAppliedForCurrentExit = false;

            if (ShouldPreserveRemotePlayersOnDedicatedDisconnect(safeReason))
            {
                Log(
                    "Transient dedicated disconnect preserved remote players | reason=" +
                    SafeForLog(safeReason)
                );

                return;
            }

            ForceClearRemotePlayersAfterDedicatedDisconnect(
                safeReason,
                clearRemotePlayersOnDedicatedDisconnect,
                "non_transient_dedicated_disconnect_v2"
            );
        }

        //* این تابع وقتی کلاینت از روم ریل تایم خارج شد، اتصال ددیکیتد همان روم را هم تمیز قطع می کند.
        //* این تابع وقتی کلاینت از روم ریل تایم خارج شد، اتصال ددیکیتد همان روم را هم تمیز قطع می کند.
        private void HandleRealtimeRoomLeft(string roomId)
        {
            Log(
                "Realtime room left received for dedicated cleanup | roomId=" +
                SafeForLog(roomId)
            );

            if (!MatchesDedicatedRoomForRealtimeLeave(roomId))
            {
                Log(
                    "Realtime leave ignored. Dedicated room is different. realtimeRoomId=" +
                    SafeForLog(roomId) +
                    " | dedicatedRoomId=" +
                    SafeForLog(wsClient != null ? wsClient.RoomId : string.Empty)
                );

                return;
            }

            isDedicatedGameplayActive = false;
            realtimeReconnectInProgress = false;
            realtimeReconnectFailedPermanently = false;

            bool cleanupAlreadyApplied =
                remoteCleanupAppliedForCurrentExit;

            if (stopLocalStateOnRealtimeRoomLeft &&
                !cleanupAlreadyApplied)
            {
                StopLocalStateSending("realtime_room_left");
            }

            if (clearRemotePlayersOnRealtimeRoomLeft &&
                !cleanupAlreadyApplied)
            {
                ForceClearRemotePlayersAfterDedicatedDisconnect(
                    "realtime_room_left",
                    true,
                    "realtime_room_left_v3"
                );
            }
            else if (cleanupAlreadyApplied)
            {
                Log(
                    "Realtime room-left cleanup reused the completed manual dedicated cleanup. Duplicate remote sweep skipped."
                );
            }

            ClearSuppressedRemoteRespawnCache("realtime_room_left");
            remoteCleanupAppliedForCurrentExit = false;

            if (disconnectDedicatedOnRealtimeRoomLeft &&
                wsClient != null &&
                wsClient.IsConnected)
            {
                wsClient.Disconnect("realtime_room_left");

                Log(
                    "Dedicated websocket disconnected after realtime leave | roomId=" +
                    SafeForLog(roomId)
                );
            }
        }

        //* این تابع پیام وضعیت ریموت را به کلون سه بعدی اعمال می کند.
        private void HandleRemotePlayerStateReceived(DedicatedRemotePlayerState state)
        {
            if (!CanApplyRemoteState(state)) return;

            string playerId = state.ResolvePlayerId();
            ClearSuppressedRemoteRespawn(playerId, "dedicated_state_received");
            CancelPendingDedicatedLeftAfterFreshState(playerId, "dedicated_state_received");

            string displayName = ResolveRemoteDisplayName(playerId, state.userName);
            bool isFirstDedicatedStateForUi = !dict_remoteLastSeenTimeByPlayerId.ContainsKey(playerId) &&
                                              !set_remoteJoinedUiShownByPlayerId.Contains(playerId);

            float stateReceivedAt = Time.unscaledTime;
            dict_remoteLastSeenTimeByPlayerId[playerId] = stateReceivedAt;
            dict_remoteLastDedicatedStateTimeByPlayerId[playerId] = stateReceivedAt;
            dict_remoteNamesByPlayerId[playerId] = displayName;

            if (emitJoinedFromFirstDedicatedState && isFirstDedicatedStateForUi)
            {
                EmitDedicatedRemotePlayerJoinedForUi(playerId, displayName, "dedicated_state_first_seen");
            }

            bool reactivatedAfterSilence =
                threeDModeController.SetRemotePlayerActive(playerId, true);

            threeDModeController.SpawnOrUpdateRemotePlayer(playerId, displayName, state.Position, state.Rotation);

            if (reactivatedAfterSilence)
            {
                Log(
                    "Remote player reactivated after fresh dedicated state | playerId=" +
                    SafeForLog(playerId) +
                    " | sequence=" +
                    state.sequence
                );
            }

            if (logRemoteApply)
            {
                Log("Remote player view updated | playerId=" + SafeForLog(playerId) + " | sequence=" + state.sequence + " | pos=" + state.Position);
            }
        }

        //* این تابع ورود ریموت پلیر را برای نام و وضعیت اولیه ذخیره می کند.
        private void HandleRemotePlayerJoined(DedicatedRemotePresenceEvent evt)
        {
            if (evt == null) return;

            string playerId = evt.ResolvePlayerId();
            if (string.IsNullOrWhiteSpace(playerId)) return;

            playerId = playerId.Trim();
            if (IsLocalPlayer(playerId)) return;

            ClearSuppressedRemoteRespawn(playerId, "dedicated_join_received");
            ClearPendingDedicatedLeft(playerId, "dedicated_join_received");
            set_remoteBrowserHiddenPlayerIds.Remove(playerId);

            string displayName = ResolveRemoteDisplayName(playerId, evt.userName);
            dict_remoteNamesByPlayerId[playerId] = displayName;
            dict_remoteLastSeenTimeByPlayerId[playerId] = Time.unscaledTime;

            if (emitJoinedFromDedicatedPresenceEvent)
            {
                EmitDedicatedRemotePlayerJoinedForUi(playerId, displayName, "dedicated_presence_joined");
            }

            EmitDedicatedRoomOnlineCountForUi(evt.onlineCount, "dedicated_presence_joined");

            Log("Remote player joined cached | playerId=" + SafeForLog(playerId));
        }

        //* این تابع خروج ریموت پلیر را به حذف کلون وصل می کند.
        private void HandleRemotePlayerLeft(DedicatedRemotePresenceEvent evt)
        {
            if (evt == null) return;

            string playerId = evt.ResolvePlayerId();
            if (string.IsNullOrWhiteSpace(playerId)) return;

            playerId = playerId.Trim();
            if (IsLocalPlayer(playerId)) return;

            set_remoteBrowserHiddenPlayerIds.Remove(playerId);

            EmitDedicatedRoomOnlineCountForUi(evt.onlineCount, "dedicated_presence_left");
            MarkDedicatedRemotePlayerLeftPending(playerId, evt.reason);

            if (confirmDedicatedLeftByStateTimeout)
            {
                Log("Remote player_left pending until dedicated state timeout. playerId=" + SafeForLog(playerId) + " | reason=" + SafeForLog(evt.reason));
                return;
            }

            if (!useServerLeaveAsPrimaryRemoteRemoval)
            {
                Log("Remote player_left deferred to state timeout because dedicated state stream is source of truth. playerId=" + SafeForLog(playerId) + " | reason=" + SafeForLog(evt.reason));
                return;
            }

            string displayName = ResolveRemoteDisplayName(playerId, string.Empty);
            RemoveRemotePlayerFromDedicatedView(playerId, "leave");
            EmitDedicatedRemotePlayerLeftForUi(playerId, displayName, "dedicated_presence_left");
            SuppressRemoteRespawn(playerId, evt.reason);
        }

        //* این تابع وضعیت hidden تایید شده توسط سرور را برای نمایش مشترک WebGL و Windows اعمال می کند.
        private void HandleRemotePlayerVisibilityChanged(
            DedicatedRemotePlayerVisibilityEvent evt)
        {
            if (evt == null) return;
            if (!MatchesCurrentDedicatedRoom(evt.roomId)) return;

            string playerId = evt.ResolvePlayerId();
            if (string.IsNullOrWhiteSpace(playerId)) return;

            playerId = playerId.Trim();
            if (IsLocalPlayer(playerId)) return;

            float now = Time.unscaledTime;

            if (evt.hidden)
            {
                set_remoteBrowserHiddenPlayerIds.Add(playerId);
            }
            else
            {
                set_remoteBrowserHiddenPlayerIds.Remove(playerId);
            }

            dict_remoteLastSeenTimeByPlayerId[playerId] = now;
            dict_remoteLastDedicatedStateTimeByPlayerId[playerId] = now;

            bool reactivated =
                threeDModeController != null &&
                threeDModeController.SetRemotePlayerActive(playerId, true);

            Log(
                "Remote browser visibility applied | playerId=" +
                SafeForLog(playerId) +
                " | hidden=" +
                evt.hidden +
                " | reactivated=" +
                reactivated);
        }

        //* این تابع اسنپ شات ذخیره شده ریسیور را روی صحنه اعمال می کند.
        private void ApplyReceiverSnapshot()
        {
            if (remoteStateReceiver == null) return;

            List<DedicatedRemotePlayerState> snapshot = remoteStateReceiver.CreateSnapshot();
            for (int i = 0; i < snapshot.Count; i++)
            {
                HandleRemotePlayerStateReceived(snapshot[i]);
            }

            List<DedicatedRemotePlayerVisibilityEvent> visibilitySnapshot =
                remoteStateReceiver.CreateVisibilitySnapshot();

            for (int i = 0; i < visibilitySnapshot.Count; i++)
            {
                HandleRemotePlayerVisibilityChanged(visibilitySnapshot[i]);
            }
        }

        //* این تابع ارسال وضعیت لوکال را فعال می کند.
        public void StartLocalStateSending()
        {
            StartLocalStateSending(Math.Max(0L, localSequence));
        }

        //* این تابع ارسال وضعیت لوکال را با ادامه سیکوئنس معتبر قبلی یا سرور فعال می کند.
        private void StartLocalStateSending(long initialSequence)
        {
            if (wsClient == null || !wsClient.IsAuthenticated)
            {
                Debug.LogWarning("[DedicatedRemotePlayerViewController] Cannot start local sender. Dedicated websocket is not authenticated.");
                return;
            }

            if (threeDModeController == null || threeDModeController.GetLocalPlayerTransform() == null)
            {
                Debug.LogWarning("[DedicatedRemotePlayerViewController] Cannot start local sender. Local player transform is missing.");
                return;
            }

            if (localSendCts != null)
            {
                localSendCts.Cancel();
                localSendCts.Dispose();
            }

            localSendCts = new CancellationTokenSource();
            isDedicatedGameplayActive = true;
            nextSendTime = 0f;
            ResetLocalSendState(initialSequence);

            Log(
                "Dedicated local state sender started | initialSequence=" +
                localSequence
            );
        }

        //* این تابع ارسال وضعیت لوکال را متوقف می کند.
        public void StopLocalStateSending(string reason)
        {
            if (localSendCts != null)
            {
                localSendCts.Cancel();
                localSendCts.Dispose();
                localSendCts = null;
            }

            isLocalSendInFlight = false;
            Log("Dedicated local state sender stopped | reason=" + reason);
        }

        //* این تابع هر فریم در صورت رسیدن زمان، وضعیت لوکال را به ددیکیتد می فرستد.
        private void TickLocalStateSend()
        {
            if (!CanSendLocalState()) return;

            float safeRate = Mathf.Max(1f, sendRatePerSecond);
            if (Time.unscaledTime < nextSendTime) return;

            Transform localTransform = threeDModeController.GetLocalPlayerTransform();
            if (localTransform == null) return;

            bool heartbeatDue = Time.unscaledTime - lastStateHeartbeatSendTime >= Mathf.Max(1f, unchangedStateHeartbeatSeconds);
            if (sendOnlyWhenChanged && !HasLocalStateChanged(localTransform.position, localTransform.rotation) && !heartbeatDue)
            {
                nextSendTime = Time.unscaledTime + 1f / safeRate;
                return;
            }

            nextSendTime = Time.unscaledTime + 1f / safeRate;
            _ = SendLocalStateAsync(localTransform.position, localTransform.rotation);
        }

        //* این تابع بررسی می کند که ارسال وضعیت لوکال مجاز است یا نه.
        private bool CanSendLocalState()
        {
            if (!isDedicatedGameplayActive) return false;
            if (isLocalSendInFlight) return false;
            if (wsClient == null || !wsClient.IsConnected || !wsClient.IsAuthenticated) return false;
            if (threeDModeController == null || !threeDModeController.IsThreeDModeActive) return false;
            if (threeDModeController.GetLocalPlayerTransform() == null) return false;
            return true;
        }

        //* این تابع یک وضعیت لوکال را به ددیکیتد سرور ارسال می کند.
        private async Task<bool> SendLocalStateAsync(Vector3 position, Quaternion rotation)
        {
            if (wsClient == null) return false;

            isLocalSendInFlight = true;

            try
            {
                localSequence++;

                Vector3 velocity = Vector3.zero;
                float dt = Mathf.Max(0.0001f, Time.unscaledTime - lastStateHeartbeatSendTime);
                if (hasLastSentState) velocity = (position - lastSentPosition) / dt;

                bool sent = await wsClient.SendPlayerStateAsync(
                    position,
                    rotation,
                    velocity,
                    localSequence,
                    localSendCts != null ? localSendCts.Token : CancellationToken.None);

                if (sent)
                {
                    lastSentPosition = position;
                    lastSentRotation = rotation;
                    lastStateHeartbeatSendTime = Time.unscaledTime;
                    hasLastSentState = true;
                }

                if (logLocalSend)
                {
                    Log("Local player_state sent | sequence=" + localSequence + " | sent=" + sent + " | pos=" + position);
                }

                LogLocalMovementSendToLogText(sent, position, rotation, velocity);

                return sent;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError("[DedicatedRemotePlayerViewController] Send local state failed | " + ex.Message);
                return false;
            }
            finally
            {
                isLocalSendInFlight = false;
            }
        }

        //* این تابع بررسی می کند که وضعیت لوکال نسبت به آخرین ارسال تغییر کرده است یا نه.
        private bool HasLocalStateChanged(Vector3 position, Quaternion rotation)
        {
            if (!hasLastSentState) return true;
            if (Vector3.Distance(lastSentPosition, position) >= Mathf.Max(0f, minPositionDelta)) return true;
            if (Quaternion.Angle(lastSentRotation, rotation) >= Mathf.Max(0f, minRotationDelta)) return true;
            return false;
        }

        //* این تابع وضعیت ارسال لوکال را ریست می کند.
        private void ResetLocalSendState(long initialSequence = 0)
        {
            hasLastSentState = false;
            localSequence = Math.Max(0L, initialSequence);
            lastStateHeartbeatSendTime = Time.unscaledTime;

            Transform localTransform =
                threeDModeController != null
                    ? threeDModeController.GetLocalPlayerTransform()
                    : null;

            if (localTransform != null)
            {
                lastSentPosition = localTransform.position;
                lastSentRotation = localTransform.rotation;
            }
        }

        //* این تابع بررسی می کند که وضعیت ریموت قابل اعمال روی صحنه است یا نه.
        private bool CanApplyRemoteState(DedicatedRemotePlayerState state)
        {
            if (state == null) return false;
            if (threeDModeController == null || !threeDModeController.IsThreeDModeActive) return false;
            if (!MatchesCurrentDedicatedRoom(state.roomId)) return false;

            string playerId = state.ResolvePlayerId();
            if (string.IsNullOrWhiteSpace(playerId)) return false;
            if (IsLocalPlayer(playerId)) return false;

            return true;
        }

        //* این تابع فقط چرخه عمر تب WebGL محلی را تشخیص می دهد و رفتار Windows را تغییر نمی دهد.
        private bool HandleLocalWebGLVisibilityLifecycle()
        {
            if (wsClient == null) return false;

            if (!wsClient.TryGetWebGLDocumentVisibility(
                    out int revision,
                    out bool hidden))
            {
                return false;
            }

            if (!hasWebGLVisibilityRevision)
            {
                hasWebGLVisibilityRevision = true;
                lastWebGLVisibilityRevision = revision;
                return hidden;
            }

            if (revision == lastWebGLVisibilityRevision)
            {
                return hidden;
            }

            lastWebGLVisibilityRevision = revision;

            if (!hidden)
            {
                RestoreRemoteStateWindowsAfterWebGLResume();
                ForceImmediateLocalStateAfterWebGLResume();
            }

            return hidden;
        }

        //* این تابع هنگام برگشت تب WebGL پنجره زمانی ریموت ها را تازه می کند تا قبل از پردازش صف پیام ها خاموش نشوند.
        private void RestoreRemoteStateWindowsAfterWebGLResume()
        {
            float now = Time.unscaledTime;
            HashSet<string> playerIds = new HashSet<string>();

            foreach (string playerId in dict_remoteLastSeenTimeByPlayerId.Keys)
            {
                if (!string.IsNullOrWhiteSpace(playerId))
                {
                    playerIds.Add(playerId);
                }
            }

            foreach (string playerId in dict_remoteLastDedicatedStateTimeByPlayerId.Keys)
            {
                if (!string.IsNullOrWhiteSpace(playerId))
                {
                    playerIds.Add(playerId);
                }
            }

            foreach (string playerId in playerIds)
            {
                dict_remoteLastSeenTimeByPlayerId[playerId] = now;
                dict_remoteLastDedicatedStateTimeByPlayerId[playerId] = now;
                threeDModeController?.SetRemotePlayerActive(playerId, true);
            }

            nextRemoteDeactivateCheckTime =
                now + Mathf.Max(0.25f, remotePlayerDeactivateCheckIntervalSeconds);

            nextRemoteTimeoutCheckTime =
                now + Mathf.Max(0.25f, remoteTimeoutCheckIntervalSeconds);

            Log(
                "WebGL visibility resumed. Remote state windows restored before timeout checks | count=" +
                playerIds.Count);
        }

        //* این تابع بعد از Visible شدن تب، یک player_state تازه را در اولین فرصت ارسال می کند.
        private void ForceImmediateLocalStateAfterWebGLResume()
        {
            if (!isDedicatedGameplayActive) return;
            if (wsClient == null || !wsClient.IsAuthenticated) return;

            hasLastSentState = false;
            nextSendTime = 0f;
            lastStateHeartbeatSendTime = 0f;

            Log("WebGL visibility resumed. Immediate local player_state requested.");
        }

        //* این تابع بعد از سکوت امن وضعیت، نمایش ریموت را موقتاً غیرفعال می‌کند ولی شیء را تا پایان مهلت بازیابی نگه می‌دارد.
        private void DeactivateStateSilentRemotePlayers()
        {
            if (!deactivateRemotePlayerWhenStateSilent) return;
            if (threeDModeController == null) return;
            if (Time.unscaledTime < nextRemoteDeactivateCheckTime) return;

            nextRemoteDeactivateCheckTime =
                Time.unscaledTime +
                Mathf.Max(0.1f, remotePlayerDeactivateCheckIntervalSeconds);

            if (dict_remoteLastDedicatedStateTimeByPlayerId.Count <= 0) return;

            if (ShouldPreserveRemoteVisibilityDuringConnectionRecovery())
            {
                PreserveRemoteVisibilityDuringConnectionRecovery("dedicated_state_silence_guard");
                return;
            }

            // همان رفتار آزموده‌شده قبلی حفظ می‌شود: سکوت وضعیت پس از آستانه امن،
            // فقط نمایش ریموت را موقتاً غیرفعال می‌کند و شیء تا پایان مهلت ۲۱۰ ثانیه‌ای نگه داشته می‌شود.

            float silenceSeconds = ResolveRemotePlayerDeactivateSilenceSeconds();
            float now = Time.unscaledTime;

            foreach (KeyValuePair<string, float> pair in dict_remoteLastDedicatedStateTimeByPlayerId)
            {
                if (set_remoteBrowserHiddenPlayerIds.Contains(pair.Key)) continue;
                if (now - pair.Value <= silenceSeconds) continue;

                bool changed =
                    threeDModeController.SetRemotePlayerActive(
                        pair.Key,
                        false
                    );

                if (!changed) continue;

                Log(
                    "Remote player temporarily deactivated after dedicated state silence | playerId=" +
                    SafeForLog(pair.Key) +
                    " | silenceSeconds=" +
                    (now - pair.Value).ToString("F2") +
                    " | thresholdSeconds=" +
                    silenceSeconds.ToString("F2")
                );
            }
        }

        //* این تابع بازه امن غیرفعال سازی را طوری محاسبه می کند که یک هارت بیت دیررس سالم باعث خاموش و روشن شدن اشتباه نشود.
        private float ResolveRemotePlayerDeactivateSilenceSeconds()
        {
            float configuredSeconds =
                Mathf.Max(
                    1f,
                    remotePlayerDeactivateAfterStateSilenceSeconds
                );

            float heartbeatIntervalSeconds =
                Mathf.Max(
                    1f,
                    unchangedStateHeartbeatSeconds
                );

            float deactivateCheckIntervalSeconds =
                Mathf.Max(
                    0.1f,
                    remotePlayerDeactivateCheckIntervalSeconds
                );

            float heartbeatSafeSeconds =
                heartbeatIntervalSeconds * 2.5f +
                deactivateCheckIntervalSeconds;

            return Mathf.Max(
                configuredSeconds,
                heartbeatSafeSeconds
            );
        }

        //* این تابع ریموت پلیرهایی را که مدتی وضعیت نفرستاده اند حذف می کند.
        private void RemoveTimedOutRemotePlayers()
        {
            if (!removeRemotePlayersWhenStateTimeout) return;
            if (Time.unscaledTime < nextRemoteTimeoutCheckTime) return;


            if (ProcessPendingDedicatedLeftConfirmations())
            {
                return;
            }

            nextRemoteTimeoutCheckTime = Time.unscaledTime + Mathf.Max(0.25f, remoteTimeoutCheckIntervalSeconds);
            if (dict_remoteLastSeenTimeByPlayerId.Count <= 0) return;

            if (ShouldPreserveTimedOutRemotePlayers())
            {
                LogRemoteTimeoutPreservedIfDue();
                return;
            }

            float timeoutSeconds = Mathf.Max(1f, remotePlayerStateTimeoutSeconds, remotePlayerReconnectGraceSeconds);
            List<string> timedOutPlayerIds = null;

            foreach (KeyValuePair<string, float> pair in dict_remoteLastSeenTimeByPlayerId)
            {
                if (set_remoteBrowserHiddenPlayerIds.Contains(pair.Key)) continue;
                if (Time.unscaledTime - pair.Value <= timeoutSeconds) continue;
                if (timedOutPlayerIds == null) timedOutPlayerIds = new List<string>();
                timedOutPlayerIds.Add(pair.Key);
            }

            if (timedOutPlayerIds == null) return;

            for (int i = 0; i < timedOutPlayerIds.Count; i++)
            {
                string playerId = timedOutPlayerIds[i];
                RemoveRemotePlayerFromDedicatedView(playerId, "timeout");
            }
        }

        //* این تابع پس از آماده شدن دوباره ریل تایم، فلگ قدیمی بازیابی را در صورت زنده بودن اتصال ددیکیتد پاک می کند.
        private void HandleRealtimeReadyAfterRecovery()
        {
            if (!realtimeReconnectInProgress) return;
            if (wsClient == null || !wsClient.IsConnected || !wsClient.IsAuthenticated) return;
            if (realtimeRoomGameServerManager == null || !realtimeRoomGameServerManager.IsJoinedRoom) return;
            MarkRealtimeReconnectRecovered("realtime_ready_while_dedicated_alive");
        }

        //* این تابع پس از ورود دوباره به همان روم، بازیابی ریموت ها را در صورت سالم بودن ددیکیتد نهایی می کند.
        private void HandleRealtimeRoomJoinedAfterRecovery(string roomId)
        {
            if (!realtimeReconnectInProgress) return;
            if (wsClient == null || !wsClient.IsConnected || !wsClient.IsAuthenticated) return;
            MarkRealtimeReconnectRecovered("realtime_room_rejoined_while_dedicated_alive:" + SafeForLog(roomId));
        }

        //* این تابع هنگام قطع موقت ریل تایم، حذف تایم اوت ریموت ها را متوقف می کند.
        private void HandleRealtimeConnectionLostForReconnect(string reason)
        {
            if (!preserveRemotePlayersDuringRealtimeReconnect) return;

            realtimeReconnectInProgress = true;
            realtimeReconnectFailedPermanently = false;
            RefreshRemoteTimeoutWindow("realtime_reconnect_started");
            Log("Realtime reconnect started. Remote players preserved | reason=" + SafeForLog(reason));
        }

        //* این تابع بعد از شکست کامل ریکانکت، خروج اجباری لوکال ریموت های ددیکیتد را انجام می دهد.
        private void HandleRealtimeReconnectFailedPermanently(string reason)
        {
            realtimeReconnectInProgress = false;
            realtimeReconnectFailedPermanently = true;
            isDedicatedGameplayActive = false;
            StopLocalStateSending("realtime_reconnect_failed_permanently");

            if (!clearRemotePlayersAfterPermanentReconnectFailure)
            {
                Log("Permanent reconnect failure received but remote clear is disabled | reason=" + SafeForLog(reason));
                return;
            }

            SuppressKnownRemotePlayersBeforeClear(reason);
            dict_remoteLastSeenTimeByPlayerId.Clear();
            dict_remoteNamesByPlayerId.Clear();
            ClearRemotePlayers();
            Log("Remote players cleared after permanent reconnect failure | reason=" + SafeForLog(reason));
        }

        //* این تابع بعد از برگشت اتصال، وضعیت ریکانکت را به حالت سالم برمی گرداند.
        private void MarkRealtimeReconnectRecovered(string reason)
        {
            bool hadReconnectState = realtimeReconnectInProgress || realtimeReconnectFailedPermanently;
            realtimeReconnectInProgress = false;
            realtimeReconnectFailedPermanently = false;

            if (refreshRemoteTimeoutWindowAfterReconnect)
            {
                RefreshRemoteTimeoutWindow(reason);
            }

            if (hadReconnectState)
            {
                Log("Realtime reconnect recovered for dedicated remote view | reason=" + SafeForLog(reason));
            }
        }

        //* این تابع زمان آخرین دریافت ریموت ها را تازه می کند تا بلافاصله بعد از برگشت اتصال حذف نشوند.
        private void RefreshRemoteTimeoutWindow(string reason)
        {
            HashSet<string> playerIds = new HashSet<string>();

            foreach (string playerId in dict_remoteLastSeenTimeByPlayerId.Keys)
            {
                if (!string.IsNullOrWhiteSpace(playerId)) playerIds.Add(playerId);
            }

            foreach (string playerId in dict_remoteLastDedicatedStateTimeByPlayerId.Keys)
            {
                if (!string.IsNullOrWhiteSpace(playerId)) playerIds.Add(playerId);
            }

            if (playerIds.Count <= 0) return;

            float now = Time.unscaledTime;

            foreach (string playerId in playerIds)
            {
                dict_remoteLastSeenTimeByPlayerId[playerId] = now;
                dict_remoteLastDedicatedStateTimeByPlayerId[playerId] = now;
                threeDModeController?.SetRemotePlayerActive(playerId, true);
            }

            Log("Remote timeout window refreshed | reason=" + SafeForLog(reason) + " | count=" + playerIds.Count);
        }

        //* این تابع تشخیص می دهد آیا حذف تایم اوت ریموت ها فعلاً باید متوقف شود یا نه.
        private bool ShouldPreserveTimedOutRemotePlayers()
        {
            if (realtimeReconnectFailedPermanently) return false;
            if (preserveRemotePlayersDuringRealtimeReconnect && realtimeReconnectInProgress) return true;
            return false;
        }

        //* این تابع تشخیص می دهد آیا هنگام قطع ددیکیتد باید ریموت ها در صحنه بمانند یا نه.
        private bool ShouldPreserveRemotePlayersOnDedicatedDisconnect(string reason)
        {
            if (IsManualDedicatedDisconnectReason(reason)) return false;
            if (!preserveRemotePlayersOnTransientDedicatedDisconnect) return false;
            if (realtimeReconnectFailedPermanently) return false;
            if (preserveRemotePlayersDuringRealtimeReconnect && realtimeReconnectInProgress) return true;
            if (IsTransientDisconnectReason(reason)) return true;
            return false;
        }

        //* این تابع دلیل قطع ددیکیتد را از نظر خروج دستی کاربر بررسی می کند.
        private bool IsManualDedicatedDisconnectReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return false;
            }

            string value = reason.Trim().ToLowerInvariant();

            if (value.Contains("manual_game_server_disconnect"))
            {
                return true;
            }

            if (value.Contains("manual_dedicated_disconnect"))
            {
                return true;
            }

            if (value.Contains("user_requested_game_server_disconnect"))
            {
                return true;
            }

            if (value.Contains("user_requested_exit"))
            {
                return true;
            }

            if (value.Contains("user_exit_whole_game"))
            {
                return true;
            }

            if (value.Contains("exit_whole_game"))
            {
                return true;
            }

            if (value.Contains("application_quit"))
            {
                return true;
            }

            if (value.Contains("manual_disconnect"))
            {
                return true;
            }

            if (value.Contains("disconnect_button"))
            {
                return true;
            }

            if (value.Contains("realtime_room_left"))
            {
                return true;
            }

            return false;
        }

        //* این تابع وضعیت زنده بودن یا در حال برگشت بودن سشن ددیکیتد را تشخیص می دهد.
        private bool IsDedicatedSessionStillAliveOrRecovering()
        {
            if (realtimeReconnectInProgress) return true;
            if (wsClient != null && (wsClient.IsConnected || wsClient.IsAuthenticated)) return true;
            return isDedicatedGameplayActive;
        }

        //* این تابع از لحظه شروع بررسی شبکه تا پایان ورود، ریل تایم و ددیکیتد، خاموش شدن ریموت ها را متوقف می کند.
        private bool ShouldPreserveRemoteVisibilityDuringConnectionRecovery()
        {
            if (realtimeReconnectFailedPermanently) return false;
            if (realtimeReconnectInProgress) return true;
            if (GlobalMessageManager.HasActiveNetworkPriorityMessage) return true;
            if (StartupNetworkSceneRouter.Instance != null && !StartupNetworkSceneRouter.IsOnline) return true;
            if (RealtimeRoomGameServerManager.Instance != null && RealtimeRoomGameServerManager.Instance.IsRecoveryRunning) return true;

            if (GlobalAuthManager.Instance != null)
            {
                GlobalAuthManager.AuthState authState = GlobalAuthManager.CurrentAuthState;
                if (authState == GlobalAuthManager.AuthState.WaitingForNetwork || authState == GlobalAuthManager.AuthState.FetchingUser) return true;
            }

            if (wsClient != null && (!wsClient.IsConnected || !wsClient.IsAuthenticated)) return true;
            return false;
        }

        //* این تابع هنگام نامطمئن بودن اتصال، زمان ریموت ها را تازه و نمایش آنها را فعال نگه می دارد.
        private void PreserveRemoteVisibilityDuringConnectionRecovery(string reason)
        {
            HashSet<string> playerIds = new HashSet<string>();

            foreach (string playerId in dict_remoteLastSeenTimeByPlayerId.Keys)
            {
                if (!string.IsNullOrWhiteSpace(playerId)) playerIds.Add(playerId);
            }

            foreach (string playerId in dict_remoteLastDedicatedStateTimeByPlayerId.Keys)
            {
                if (!string.IsNullOrWhiteSpace(playerId)) playerIds.Add(playerId);
            }

            if (playerIds.Count <= 0) return;

            float now = Time.unscaledTime;

            foreach (string playerId in playerIds)
            {
                dict_remoteLastSeenTimeByPlayerId[playerId] = now;
                dict_remoteLastDedicatedStateTimeByPlayerId[playerId] = now;
                threeDModeController?.SetRemotePlayerActive(playerId, true);
            }

            if (!verboseLogs || now < nextRemoteRecoveryPreserveLogTime) return;
            nextRemoteRecoveryPreserveLogTime = now + Mathf.Max(1f, remoteTimeoutStaleLogIntervalSeconds);
            Log("Remote players kept visible during connection recovery | count=" + playerIds.Count + " | reason=" + SafeForLog(reason));
        }

        //* این تابع فقط بازیابی واقعی همین کلاینت را برای نگه داشتن خروج در انتظار معتبر می داند.
        private bool ShouldPreservePendingDedicatedLeftConfirmations()
        {
            if (realtimeReconnectFailedPermanently) return false;
            if (StartupNetworkSceneRouter.Instance != null && !StartupNetworkSceneRouter.IsOnline) return true;

            if (GlobalAuthManager.Instance != null)
            {
                GlobalAuthManager.AuthState authState = GlobalAuthManager.CurrentAuthState;
                if (authState == GlobalAuthManager.AuthState.WaitingForNetwork || authState == GlobalAuthManager.AuthState.FetchingUser) return true;
            }

            if (realtimeRoomGameServerManager != null)
            {
                if (realtimeRoomGameServerManager.IsRecoveryRunning) return true;
                if (!realtimeRoomGameServerManager.IsRealtimeReady || !realtimeRoomGameServerManager.IsJoinedRoom) return true;
            }

            if (wsClient == null || !wsClient.IsConnected || !wsClient.IsAuthenticated) return true;
            return false;
        }

        //* این تابع زمان خروج های در انتظار را هنگام بازیابی تازه می کند تا قطع موقت به خروج قطعی تبدیل نشود.
        private void RefreshPendingDedicatedLeftWindowsDuringRecovery()
        {
            if (dict_pendingDedicatedLeftTimeByPlayerId.Count <= 0) return;

            List<string> playerIds = new List<string>(dict_pendingDedicatedLeftTimeByPlayerId.Keys);
            float now = Time.unscaledTime;

            for (int i = 0; i < playerIds.Count; i++) dict_pendingDedicatedLeftTimeByPlayerId[playerIds[i]] = now;
        }

        //* این تابع دلیل قطع را از نظر موقت یا خطای شبکه بودن بررسی می کند.
        private bool IsTransientDisconnectReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;

            string value = reason.Trim().ToLowerInvariant();
            if (value.Contains("reconnect")) return true;
            if (value.Contains("timeout")) return true;
            if (value.Contains("transport")) return true;
            if (value.Contains("network")) return true;
            if (value.Contains("receive failed")) return true;
            if (value.Contains("send failed")) return true;
            if (value.Contains("connection lost")) return true;
            if (value.Contains("unexpected")) return true;
            if (value.Contains("abnormal")) return true;
            if (value.Contains("ping")) return true;
            if (value.Contains("pong")) return true;
            if (value.Contains("socket error")) return true;
            if (value.Contains("grpc")) return true;
            return false;
        }

        //* این تابع فقط برای دیباگ نشان می دهد که حذف تایم اوت به خاطر حفاظت ریکانکت انجام نشده است.
        private void LogRemoteTimeoutPreservedIfDue()
        {
            if (!verboseLogs) return;
            if (Time.unscaledTime < nextRemoteTimeoutStaleLogTime) return;

            nextRemoteTimeoutStaleLogTime = Time.unscaledTime + Mathf.Max(1f, remoteTimeoutStaleLogIntervalSeconds);
            Log("Remote timeout removal skipped while session is alive or reconnecting | count=" + dict_remoteLastSeenTimeByPlayerId.Count);
        }

        //* این تابع پاکسازی ریموت های ددیکیتد را برای قطع غیرقابل نگهداری یا خروج دستی متمرکز می کند.
        private void ForceClearRemotePlayersAfterDedicatedDisconnect(string reason, bool shouldClearSceneRemotes, string source)
        {
            List<string> knownPlayerIds = CollectKnownRemotePlayerIds();
            SuppressKnownRemotePlayersBeforeClear(reason);
            dict_remoteLastSeenTimeByPlayerId.Clear();
            dict_remoteLastDedicatedStateTimeByPlayerId.Clear();
            dict_remoteNamesByPlayerId.Clear();
            set_remoteBrowserHiddenPlayerIds.Clear();

            if (!shouldClearSceneRemotes)
            {
                Log("Dedicated remote player scene clear skipped by inspector flag | source=" + SafeForLog(source) + " | reason=" + SafeForLog(reason));
                return;
            }

            RemoveKnownRemotePlayersFromThreeDController(knownPlayerIds, source);
            ClearRemotePlayers();

            bool allowFullRemoteViewSweep = ShouldForceFullRemoteViewSweep(source);
            int sceneSweepCount = ForceDestroySceneRemoteViews(knownPlayerIds, source, allowFullRemoteViewSweep);

            Log("Dedicated remote players force-cleared after disconnect | source=" + SafeForLog(source) + " | reason=" + SafeForLog(reason) + " | knownIds=" + knownPlayerIds.Count + " | sceneSweep=" + sceneSweepCount);
        }

        //* این تابع همه ریموت پلیرهای ددیکیتد را از کنترلر سه بعدی پاک می کند.
        private void ClearRemotePlayers()
        {
            dict_remoteLastSeenTimeByPlayerId.Clear();
            dict_remoteLastDedicatedStateTimeByPlayerId.Clear();
            dict_remoteNamesByPlayerId.Clear();
            set_remoteBrowserHiddenPlayerIds.Clear();
            threeDModeController?.ClearRemotePlayers();
        }

        //* این تابع پلیرهای ریموت شناخته شده را قبل از پاکسازی عمومی در لیست ضد اسپاون مجدد می گذارد.
        private void SuppressKnownRemotePlayersBeforeClear(string reason)
        {
            if (!suppressRealtimeRespawnAfterDedicatedLeft) return;

            List<string> playerIds = new List<string>();

            foreach (KeyValuePair<string, float> pair in dict_remoteLastSeenTimeByPlayerId)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key)) playerIds.Add(pair.Key.Trim());
            }

            foreach (KeyValuePair<string, string> pair in dict_remoteNamesByPlayerId)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !playerIds.Contains(pair.Key.Trim())) playerIds.Add(pair.Key.Trim());
            }

            for (int i = 0; i < playerIds.Count; i++)
            {
                SuppressRemoteRespawn(playerIds[i], reason);
            }
        }

        //* این تابع بعد از دریافت player_left، اجازه نمی دهد مسیر ریل تایم همان پلیر را دوباره در صحنه بسازد.
        private void SuppressRemoteRespawn(string playerId, string reason)
        {
            if (!suppressRealtimeRespawnAfterDedicatedLeft) return;
            if (string.IsNullOrWhiteSpace(playerId)) return;

            string safePlayerId = playerId.Trim();
            if (IsLocalPlayer(safePlayerId)) return;

            float safeSeconds = Mathf.Max(1f, suppressRealtimeRespawnSeconds);
            dict_suppressedRemoteRespawnUntilByPlayerId[safePlayerId] = Time.unscaledTime + safeSeconds;
            nextSuppressedRemoteRemovalCheckTime = 0f;
            Log("Remote respawn suppressed | playerId=" + SafeForLog(safePlayerId) + " | reason=" + SafeForLog(reason));
        }

        //* این تابع وقتی همان پلیر دوباره از ددیکیتد پیام معتبر می فرستد، قفل ضد اسپاون را باز می کند.
        private void ClearSuppressedRemoteRespawn(string playerId, string reason)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return;

            string safePlayerId = playerId.Trim();
            if (!dict_suppressedRemoteRespawnUntilByPlayerId.Remove(safePlayerId)) return;

            Log("Remote respawn suppression cleared | playerId=" + SafeForLog(safePlayerId) + " | reason=" + SafeForLog(reason));
        }

        //* این تابع همه قفل های ضد اسپاون را پاک می کند.
        private void ClearSuppressedRemoteRespawnCache(string reason)
        {
            if (dict_suppressedRemoteRespawnUntilByPlayerId.Count <= 0) return;

            dict_suppressedRemoteRespawnUntilByPlayerId.Clear();
            Log("Remote respawn suppression cache cleared | reason=" + SafeForLog(reason));
        }

        //* این تابع در چند بازه کوتاه پلیرهای خارج شده از ددیکیتد را از مسیر ریل تایم هم دوباره حذف می کند.
        private void EnforceSuppressedRemoteRemoval()
        {
            if (!suppressRealtimeRespawnAfterDedicatedLeft) return;
            if (dict_suppressedRemoteRespawnUntilByPlayerId.Count <= 0) return;
            if (threeDModeController == null) return;
            if (Time.unscaledTime < nextSuppressedRemoteRemovalCheckTime) return;

            nextSuppressedRemoteRemovalCheckTime = Time.unscaledTime + Mathf.Max(0.05f, suppressRealtimeRespawnCheckIntervalSeconds);

            List<string> expiredPlayerIds = null;
            List<string> activePlayerIds = null;
            float now = Time.unscaledTime;

            foreach (KeyValuePair<string, float> pair in dict_suppressedRemoteRespawnUntilByPlayerId)
            {
                if (pair.Value <= now && wsClient != null && wsClient.IsConnected && wsClient.IsAuthenticated)
                {
                    if (expiredPlayerIds == null) expiredPlayerIds = new List<string>();
                    expiredPlayerIds.Add(pair.Key);
                    continue;
                }

                if (activePlayerIds == null) activePlayerIds = new List<string>();
                activePlayerIds.Add(pair.Key);
            }

            if (expiredPlayerIds != null)
            {
                for (int i = 0; i < expiredPlayerIds.Count; i++)
                {
                    dict_suppressedRemoteRespawnUntilByPlayerId.Remove(expiredPlayerIds[i]);
                }
            }

            if (activePlayerIds == null) return;

            for (int i = 0; i < activePlayerIds.Count; i++)
            {
                threeDModeController.RemoveRemotePlayer(activePlayerIds[i]);
            }
        }

        //* این تابع همه شناسه های ریموت شناخته شده را قبل از پاکسازی جمع می کند.
        private List<string> CollectKnownRemotePlayerIds()
        {
            List<string> playerIds = new List<string>();

            foreach (KeyValuePair<string, float> pair in dict_remoteLastSeenTimeByPlayerId)
            {
                AddUniquePlayerId(playerIds, pair.Key);
            }

            foreach (KeyValuePair<string, string> pair in dict_remoteNamesByPlayerId)
            {
                AddUniquePlayerId(playerIds, pair.Key);
            }

            if (remoteStateReceiver != null)
            {
                List<DedicatedRemotePlayerState> snapshot = remoteStateReceiver.CreateSnapshot();
                for (int i = 0; i < snapshot.Count; i++)
                {
                    if (snapshot[i] == null) continue;
                    AddUniquePlayerId(playerIds, snapshot[i].ResolvePlayerId());
                    AddUniquePlayerId(playerIds, snapshot[i].userId);
                    AddUniquePlayerId(playerIds, snapshot[i].connectionId);
                }
            }

            return playerIds;
        }

        //* این تابع شناسه تکراری یا خالی را وارد لیست نمی کند.
        private void AddUniquePlayerId(List<string> playerIds, string playerId)
        {
            if (playerIds == null) return;
            if (string.IsNullOrWhiteSpace(playerId)) return;

            string safePlayerId = playerId.Trim();
            if (IsLocalPlayer(safePlayerId)) return;

            for (int i = 0; i < playerIds.Count; i++)
            {
                if (string.Equals(playerIds[i], safePlayerId, StringComparison.Ordinal)) return;
            }

            playerIds.Add(safePlayerId);
        }

        //* این تابع ریموت های شناخته شده را قبل از پاکسازی عمومی از کنترلر سه بعدی حذف می کند.
        private void RemoveKnownRemotePlayersFromThreeDController(List<string> playerIds, string source)
        {
            if (threeDModeController == null) return;
            if (playerIds == null || playerIds.Count <= 0) return;

            for (int i = 0; i < playerIds.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(playerIds[i])) continue;
                threeDModeController.RemoveRemotePlayer(playerIds[i]);
            }

            Log("Known remote players removed from 3D controller | source=" + SafeForLog(source) + " | count=" + playerIds.Count);
        }

        //* این تابع مشخص می کند پاکسازی کامل کامپوننت های ریموت ویو در صحنه مجاز است یا نه.
        private bool ShouldForceFullRemoteViewSweep(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return false;

            string value = source.Trim().ToLowerInvariant();
            if (value.Contains("manual_dedicated_disconnect") && forceSceneSweepRemoteViewsOnManualDedicatedDisconnect) return true;
            if (value.Contains("context_menu_clear") && forceSceneSweepRemoteViewsOnManualDedicatedDisconnect) return true;
            if (value.Contains("realtime_room_left") && forceSceneSweepRemoteViewsOnRealtimeRoomLeft) return true;
            if (value.Contains("permanent_reconnect") && clearRemotePlayersAfterPermanentReconnectFailure) return true;
            return false;
        }

        //* این تابع به عنوان fallback، خود آبجکت های G7RemotePlayerView را از صحنه حذف می کند.
        private int ForceDestroySceneRemoteViews(List<string> knownPlayerIds, string source, bool allowFullRemoteViewSweep)
        {
            MonoBehaviour[] behaviours = FindObjectsOfType<MonoBehaviour>(true);
            if (behaviours == null || behaviours.Length <= 0) return 0;

            int destroyedCount = 0;
            HashSet<GameObject> targets = new HashSet<GameObject>();

            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null) continue;
                if (!IsRemotePlayerViewBehaviour(behaviour)) continue;
                if (IsLocalPlayerSceneObject(behaviour.gameObject)) continue;

                bool shouldDestroy = allowFullRemoteViewSweep || DoesRemoteViewMatchKnownPlayer(behaviour, knownPlayerIds);
                if (!shouldDestroy) continue;

                targets.Add(behaviour.gameObject);
            }

            foreach (GameObject target in targets)
            {
                if (target == null) continue;
                if (IsProtectedSceneRootTarget(target))
                {
                    Log("Protected scene root was skipped during remote fallback sweep | target=" + SafeForLog(target.name) + " | source=" + SafeForLog(source));
                    continue;
                }

                Destroy(target);
                destroyedCount++;
            }

            if (destroyedCount > 0)
            {
                Log("Scene remote player views destroyed by fallback sweep | source=" + SafeForLog(source) + " | count=" + destroyedCount + " | fullSweep=" + allowFullRemoteViewSweep);
            }

            return destroyedCount;
        }

        private bool IsProtectedSceneRootTarget(GameObject target)
        {
            if (!neverDestroySharedWorld3DRootDuringSceneSweep || target == null) return false;

            Transform targetTransform = target.transform;
            if (targetTransform == null) return false;

            if (sharedWorld3DRoot != null)
            {
                Transform sharedRootTransform = sharedWorld3DRoot.transform;
                if (targetTransform == sharedRootTransform) return true;
                if (sharedRootTransform != null && sharedRootTransform.IsChildOf(targetTransform)) return true;
            }

            if (protectedSceneRoots != null)
            {
                for (int i = 0; i < protectedSceneRoots.Length; i++)
                {
                    Transform protectedRoot = protectedSceneRoots[i];
                    if (protectedRoot == null) continue;
                    if (targetTransform == protectedRoot) return true;
                    if (protectedRoot.IsChildOf(targetTransform)) return true;
                }
            }

            return autoProtectSharedWorld3DRootByName
                   && !string.IsNullOrWhiteSpace(sharedWorld3DRootName)
                   && string.Equals(target.name, sharedWorld3DRootName, StringComparison.Ordinal);
        }

        //* این تابع فقط کامپوننت های ویوی ریموت را هدف می گیرد و کنترلرها را حذف نمی کند.
        private bool IsRemotePlayerViewBehaviour(MonoBehaviour behaviour)
        {
            if (behaviour == null) return false;

            string typeName = behaviour.GetType().Name;
            if (string.IsNullOrWhiteSpace(typeName)) return false;
            if (typeName == nameof(DedicatedRemotePlayerViewController)) return false;
            if (typeName.Contains("Controller")) return false;
            if (typeName == "G7RemotePlayerView") return true;
            if (typeName.EndsWith("RemotePlayerView", StringComparison.Ordinal)) return true;
            return false;
        }

        //* این تابع جلوی حذف آبجکت لوکال را در fallback می گیرد.
        private bool IsLocalPlayerSceneObject(GameObject target)
        {
            if (target == null) return true;

            Transform localTransform = threeDModeController != null ? threeDModeController.GetLocalPlayerTransform() : null;
            if (localTransform == null) return false;
            if (target.transform == localTransform) return true;
            if (target.transform.IsChildOf(localTransform)) return true;
            return false;
        }

        //* این تابع بررسی می کند کامپوننت ریموت ویو مربوط به یکی از شناسه های شناخته شده هست یا نه.
        private bool DoesRemoteViewMatchKnownPlayer(MonoBehaviour behaviour, List<string> knownPlayerIds)
        {
            if (behaviour == null) return false;
            if (knownPlayerIds == null || knownPlayerIds.Count <= 0) return false;

            string lookupText = BuildRemoteViewLookupText(behaviour).ToLowerInvariant();
            for (int i = 0; i < knownPlayerIds.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(knownPlayerIds[i])) continue;
                string id = knownPlayerIds[i].Trim().ToLowerInvariant();
                if (lookupText.Contains(id)) return true;
            }

            return false;
        }

        //* این تابع نام آبجکت و فیلدهای شناسه ای ریموت ویو را برای fallback جمع می کند.
        private string BuildRemoteViewLookupText(MonoBehaviour behaviour)
        {
            if (behaviour == null) return string.Empty;

            string result = BuildTransformPath(behaviour.transform);
            result += " " + ReadStringMemberByName(behaviour, "playerId");
            result += " " + ReadStringMemberByName(behaviour, "PlayerId");
            result += " " + ReadStringMemberByName(behaviour, "userId");
            result += " " + ReadStringMemberByName(behaviour, "UserId");
            result += " " + ReadStringMemberByName(behaviour, "remotePlayerId");
            result += " " + ReadStringMemberByName(behaviour, "RemotePlayerId");
            result += " " + ReadStringMemberByName(behaviour, "displayName");
            result += " " + ReadStringMemberByName(behaviour, "DisplayName");
            return result;
        }

        //* این تابع مسیر آبجکت را برای پیدا کردن شناسه در نام پدرها می سازد.
        private string BuildTransformPath(Transform target)
        {
            if (target == null) return string.Empty;

            string path = target.name;
            Transform current = target.parent;

            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
        }

        //* این تابع مقدار فیلد یا پراپرتی رشته ای را با رفلکشن می خواند.
        private string ReadStringMemberByName(object instance, string memberName)
        {
            if (instance == null || string.IsNullOrWhiteSpace(memberName)) return string.Empty;

            Type type = instance.GetType();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            FieldInfo field = type.GetField(memberName, flags);
            if (field != null && field.FieldType == typeof(string))
            {
                return field.GetValue(instance) as string ?? string.Empty;
            }

            PropertyInfo property = type.GetProperty(memberName, flags);
            if (property != null && property.PropertyType == typeof(string) && property.CanRead)
            {
                try
                {
                    return property.GetValue(instance, null) as string ?? string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }

            return string.Empty;
        }

        //* این تابع پلیر ریموت را از همه کش های ددیکیتد و صحنه حذف می کند.
        private void RemoveRemotePlayerFromDedicatedView(string playerId, string reason)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return;

            string safePlayerId = playerId.Trim();
            if (IsLocalPlayer(safePlayerId)) return;

            dict_remoteLastSeenTimeByPlayerId.Remove(safePlayerId);
            dict_remoteLastDedicatedStateTimeByPlayerId.Remove(safePlayerId);
            dict_remoteNamesByPlayerId.Remove(safePlayerId);
            dict_pendingDedicatedLeftTimeByPlayerId.Remove(safePlayerId);
            dict_pendingDedicatedLeftReasonByPlayerId.Remove(safePlayerId);
            set_remoteJoinedUiShownByPlayerId.Remove(safePlayerId);
            set_remoteBrowserHiddenPlayerIds.Remove(safePlayerId);
            threeDModeController?.RemoveRemotePlayer(safePlayerId);

            Log("Remote player removed | playerId=" + SafeForLog(safePlayerId) + " | reason=" + SafeForLog(reason));
        }
        //* این تابع ورود واقعی ریموت را فقط از مسیر ددیکیتد به رابط کاربری اعلام می کند.
        private void EmitDedicatedRemotePlayerJoinedForUi(string playerId, string displayName, string reason)
        {
            if (!emitDedicatedPresenceUiEvents) return;
            if (string.IsNullOrWhiteSpace(playerId)) return;

            string safePlayerId = playerId.Trim();
            if (IsLocalPlayer(safePlayerId)) return;
            if (set_remoteJoinedUiShownByPlayerId.Contains(safePlayerId)) return;

            set_remoteJoinedUiShownByPlayerId.Add(safePlayerId);

            string safeDisplayName = ResolveRemoteDisplayName(safePlayerId, displayName);
            DedicatedRemotePlayerJoinedForUi?.Invoke(safePlayerId, safeDisplayName);

            Log("Dedicated presence UI joined emitted | playerId=" + SafeForLog(safePlayerId) + " | name=" + SafeForLog(safeDisplayName) + " | reason=" + SafeForLog(reason));
        }

        //* این تابع خروج واقعی ریموت را بعد از قطع شدن جریان وضعیت ددیکیتد به رابط کاربری اعلام می کند.
        private void EmitDedicatedRemotePlayerLeftForUi(string playerId, string displayName, string reason)
        {
            if (!emitDedicatedPresenceUiEvents) return;
            if (string.IsNullOrWhiteSpace(playerId)) return;

            string safePlayerId = playerId.Trim();
            if (IsLocalPlayer(safePlayerId)) return;

            string safeDisplayName = ResolveRemoteDisplayName(safePlayerId, displayName);
            DedicatedRemotePlayerLeftForUi?.Invoke(safePlayerId, safeDisplayName);

            Log("Dedicated presence UI left emitted | playerId=" + SafeForLog(safePlayerId) + " | name=" + SafeForLog(safeDisplayName) + " | reason=" + SafeForLog(reason));
        }

        //* این تابع تعداد authoritative رجیستری Dedicated Server را برای UI کاربر مقابل ارسال می کند.
        private void EmitDedicatedRoomOnlineCountForUi(int onlineCount, string reason)
        {
            if (onlineCount <= 0) return;

            DedicatedRoomOnlineCountChangedForUi?.Invoke(onlineCount);
            Log(
                "Dedicated authoritative room users emitted | online=" +
                onlineCount +
                " | reason=" +
                SafeForLog(reason)
            );
        }

        //* این تابع خروج های ددیکیتد در انتظار تایید را بعد از قطع شدن جریان وضعیت همان پلیر نهایی می کند.
        private bool ProcessPendingDedicatedLeftConfirmations()
        {
            if (!confirmDedicatedLeftByStateTimeout) return false;
            if (dict_pendingDedicatedLeftTimeByPlayerId.Count <= 0) return false;

            if (ShouldPreservePendingDedicatedLeftConfirmations())
            {
                RefreshPendingDedicatedLeftWindowsDuringRecovery();
                PreserveRemoteVisibilityDuringConnectionRecovery("pending_dedicated_left_guard");
                return false;
            }

            float silenceSeconds = Mathf.Max(0.5f, dedicatedLeftStateSilenceConfirmSeconds);
            List<string> confirmedLeftPlayerIds = null;

            foreach (KeyValuePair<string, float> pair in dict_pendingDedicatedLeftTimeByPlayerId)
            {
                string playerId = pair.Key;
                float referenceTime = pair.Value;

                if (dict_remoteLastSeenTimeByPlayerId.TryGetValue(playerId, out float lastSeenTime))
                {
                    referenceTime = Mathf.Max(referenceTime, lastSeenTime);
                }

                if (Time.unscaledTime - referenceTime <= silenceSeconds) continue;

                if (confirmedLeftPlayerIds == null) confirmedLeftPlayerIds = new List<string>();
                confirmedLeftPlayerIds.Add(playerId);
            }

            if (confirmedLeftPlayerIds == null) return false;

            for (int i = 0; i < confirmedLeftPlayerIds.Count; i++)
            {
                string playerId = confirmedLeftPlayerIds[i];
                string displayName = ResolveRemoteDisplayName(playerId, string.Empty);
                string leftReason = ResolvePendingDedicatedLeftReason(playerId, "dedicated_presence_left_confirmed_by_state_silence");

                RemoveRemotePlayerFromDedicatedView(playerId, leftReason);
                EmitDedicatedRemotePlayerLeftForUi(playerId, displayName, leftReason);
                SuppressRemoteRespawn(playerId, leftReason);
                ClearPendingDedicatedLeft(playerId, leftReason);

                Log("Dedicated pending left confirmed by state silence | playerId=" + SafeForLog(playerId) + " | reason=" + SafeForLog(leftReason));
            }

            return true;
        }

        //* این تابع بررسی می کند برای این پلیر خروج ددیکیتد در انتظار تایید وجود دارد یا نه.
        private bool HasPendingDedicatedLeft(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return false;
            return dict_pendingDedicatedLeftTimeByPlayerId.ContainsKey(playerId.Trim());
        }

        //* این تابع با رسیدن وضعیت تازه همان پلیر، خروج موقت و تاییدنشده را لغو می کند.
        private void CancelPendingDedicatedLeftAfterFreshState(string playerId, string reason)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return;

            string safePlayerId = playerId.Trim();
            if (!dict_pendingDedicatedLeftTimeByPlayerId.ContainsKey(safePlayerId)) return;

            ClearPendingDedicatedLeft(safePlayerId, reason);
            Log("Dedicated pending left cancelled by fresh state | playerId=" + SafeForLog(safePlayerId) + " | reason=" + SafeForLog(reason));
        }

        //* این تابع خروج ددیکیتد را تا زمان تایم اوت وضعیت همان پلیر در صف تایید نگه می دارد.
        private void MarkDedicatedRemotePlayerLeftPending(string playerId, string reason)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return;

            string safePlayerId = playerId.Trim();
            if (IsLocalPlayer(safePlayerId)) return;

            dict_pendingDedicatedLeftTimeByPlayerId[safePlayerId] = Time.unscaledTime;
            dict_pendingDedicatedLeftReasonByPlayerId[safePlayerId] = string.IsNullOrWhiteSpace(reason) ? "dedicated_presence_left" : reason.Trim();
        }

        //* این تابع صف تایید خروج ددیکیتد را پاک می کند.
        private void ClearPendingDedicatedLeft(string playerId, string reason)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return;

            string safePlayerId = playerId.Trim();
            bool removedTime = dict_pendingDedicatedLeftTimeByPlayerId.Remove(safePlayerId);
            bool removedReason = dict_pendingDedicatedLeftReasonByPlayerId.Remove(safePlayerId);

            if (removedTime || removedReason)
            {
                Log("Dedicated pending left cleared | playerId=" + SafeForLog(safePlayerId) + " | reason=" + SafeForLog(reason));
            }
        }

        //* این تابع دلیل خروج در انتظار تایید را برای پیام نهایی برمی گرداند.
        private string ResolvePendingDedicatedLeftReason(string playerId, string fallbackReason)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return fallbackReason;

            string safePlayerId = playerId.Trim();

            if (dict_pendingDedicatedLeftReasonByPlayerId.TryGetValue(safePlayerId, out string reason) && !string.IsNullOrWhiteSpace(reason))
            {
                return reason.Trim();
            }

            return string.IsNullOrWhiteSpace(fallbackReason) ? "timeout" : fallbackReason.Trim();
        }
        //* این تابع بررسی می کند خروج از روم ریل تایم به همین اتصال ددیکیتد مربوط است یا نه.
        private bool MatchesDedicatedRoomForRealtimeLeave(string roomId)
        {
            if (string.IsNullOrWhiteSpace(roomId)) return true;
            if (wsClient == null) return true;
            if (string.IsNullOrWhiteSpace(wsClient.RoomId)) return true;
            return string.Equals(roomId.Trim(), wsClient.RoomId.Trim(), StringComparison.Ordinal);
        }

        //* این تابع روم پیام ددیکیتد را با روم فعلی مقایسه می کند.
        private bool MatchesCurrentDedicatedRoom(string roomId)
        {
            if (wsClient == null) return true;
            if (string.IsNullOrWhiteSpace(wsClient.RoomId)) return true;
            if (string.IsNullOrWhiteSpace(roomId)) return true;
            return string.Equals(roomId.Trim(), wsClient.RoomId.Trim(), StringComparison.Ordinal);
        }

        //* این تابع تشخیص می دهد پیام متعلق به پلیر لوکال است یا نه.
        private bool IsLocalPlayer(string playerId)
        {
            if (wsClient == null || string.IsNullOrWhiteSpace(playerId)) return false;
            string safePlayerId = playerId.Trim();
            if (!string.IsNullOrWhiteSpace(wsClient.PlayerId) && safePlayerId == wsClient.PlayerId.Trim()) return true;
            if (!string.IsNullOrWhiteSpace(wsClient.UserId) && safePlayerId == wsClient.UserId.Trim()) return true;
            return false;
        }

        //* این تابع نام نمایشی لوکال را از ریل تایم یا ددیکیتد می سازد.
        private string ResolveLocalDisplayName()
        {
            string realtimeUserName = ResolveRealtimeUserName();
            if (!string.IsNullOrWhiteSpace(realtimeUserName)) return realtimeUserName.Trim();
            if (wsClient != null && !string.IsNullOrWhiteSpace(wsClient.PlayerId)) return wsClient.PlayerId.Trim();
            if (wsClient != null && !string.IsNullOrWhiteSpace(wsClient.UserId)) return wsClient.UserId.Trim();
            return "You";
        }

        //* این تابع نام یوزر ریل تایم را از کنترلر مناسب پلتفرم می خواند.
        private string ResolveRealtimeUserName()
        {
            if (grpcStreamingRealtimeRoomController != null && !string.IsNullOrWhiteSpace(grpcStreamingRealtimeRoomController.CurrentUserName))
            {
                return grpcStreamingRealtimeRoomController.CurrentUserName.Trim();
            }

            if (realtimeRoomController != null && !string.IsNullOrWhiteSpace(realtimeRoomController.CurrentUserName))
            {
                return realtimeRoomController.CurrentUserName.Trim();
            }

            return string.Empty;
        }

        //* این تابع نام نمایشی ریموت را از پیام یا کش می سازد.
        private string ResolveRemoteDisplayName(string playerId, string messageUserName)
        {
            if (!string.IsNullOrWhiteSpace(messageUserName)) return messageUserName.Trim();
            if (!string.IsNullOrWhiteSpace(playerId) && dict_remoteNamesByPlayerId.TryGetValue(playerId.Trim(), out string cachedName) && !string.IsNullOrWhiteSpace(cachedName)) return cachedName.Trim();
            return string.IsNullOrWhiteSpace(playerId) ? "Remote Player" : playerId.Trim();
        }

        private void Log(string message)
        {
            if (!verboseLogs) return;
            Debug.Log("[DedicatedRemotePlayerViewController] " + message);
        }

        private string SafeForLog(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<empty>" : value.Trim();
        }


        //* این تابع دیتای ارسال حرکت لوکال به گیم سرور را داخل لاگ تکست ریل تایم چاپ می کند.
        private void LogLocalMovementSendToLogText(bool sent, Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            if (!logLocalMovementSendToLogText) return;
            if (!sent) return;

            bool isMoving = velocity.sqrMagnitude > 0.0004f;
            if (!isMoving) return;

            if (Time.unscaledTime < nextLocalMovementSendLogTime) return;

            nextLocalMovementSendLogTime = Time.unscaledTime + Mathf.Max(0.1f, localMovementSendLogIntervalSeconds);

            string line =
                "GAME_SERVER_MOVE_SENT | seq=" + localSequence +
                " | pos=" + FormatVector3ForMovementLog(position) +
                " | rotY=" + rotation.eulerAngles.y.ToString("F1") +
                " | vel=" + FormatVector3ForMovementLog(velocity);

            if (grpcStreamingRealtimeRoomController != null)
            {
                grpcStreamingRealtimeRoomController.AppendExternalLogTextLine("DedicatedMove", line);
                return;
            }

            Debug.Log("[DedicatedRemotePlayerViewController] " + line);
        }

        //* این تابع وکتور حرکت را کوتاه و خوانا برای لاگ آماده می کند.
        private string FormatVector3ForMovementLog(Vector3 value)
        {
            return "(" +
                   value.x.ToString("F2") + ", " +
                   value.y.ToString("F2") + ", " +
                   value.z.ToString("F2") +
                   ")";
        }
        /*
        توضیح مکتوب فایل:
        این اسکریپت فاز DS-9 است و فقط روی کلاینت استفاده می شود.
        بعد از auth_ok ددیکیتد، حالت سه بعدی را آماده می کند، وضعیت پلیر لوکال را از روی Local Player واقعی می فرستد،
        و پیام های player_state دریافتی از DedicatedRemotePlayerStateReceiver را به کلون های ریموت در G7ThreeDModeController وصل می کند.
        DedicatedPlayerStateAutoSender قدیمی در این مسیر خاموش می شود تا وضعیت از ترنسفورم اشتباه ارسال نشود.
        */
    }
}
