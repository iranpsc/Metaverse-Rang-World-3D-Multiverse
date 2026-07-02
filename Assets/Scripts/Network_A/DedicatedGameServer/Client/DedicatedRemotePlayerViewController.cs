using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Tests.Realtime;
using UnityEngine;

namespace Network_A.DedicatedGameServer.Client
{
    public class DedicatedRemotePlayerViewController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private DedicatedGameServerWsClient wsClient;
        [SerializeField] private DedicatedRemotePlayerStateReceiver remoteStateReceiver;
        [SerializeField] private G7ThreeDModeController threeDModeController;
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
        [SerializeField] private float remoteTimeoutCheckIntervalSeconds = 1f;

        [Header("Realtime Leave Sync")]
        [SerializeField] private bool disconnectDedicatedOnRealtimeRoomLeft = true;
        [SerializeField] private bool stopLocalStateOnRealtimeRoomLeft = true;
        [SerializeField] private bool clearRemotePlayersOnRealtimeRoomLeft = true;

        [Header("Dedicated Disconnect Suppression")]
        [SerializeField] private bool suppressRealtimeRespawnAfterDedicatedLeft = true;
        [SerializeField] private float suppressRealtimeRespawnSeconds = 300f;
        [SerializeField] private float suppressRealtimeRespawnCheckIntervalSeconds = 0.25f;

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;
        [SerializeField] private bool logLocalSend = false;
        [SerializeField] private bool logRemoteApply = true;

        private bool isDedicatedGameplayActive;
        private bool isLocalSendInFlight;
        private bool hasLastSentState;
        private long localSequence;
        private float nextSendTime;
        private float lastStateHeartbeatSendTime;
        private float nextRemoteTimeoutCheckTime;
        private Vector3 lastSentPosition;
        private Quaternion lastSentRotation = Quaternion.identity;
        private CancellationTokenSource localSendCts;
        private readonly Dictionary<string, float> dict_remoteLastSeenTimeByPlayerId = new Dictionary<string, float>();
        private readonly Dictionary<string, string> dict_remoteNamesByPlayerId = new Dictionary<string, string>();
        private readonly Dictionary<string, float> dict_suppressedRemoteRespawnUntilByPlayerId = new Dictionary<string, float>();
        private float nextSuppressedRemoteRemovalCheckTime;

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
            ClearRemotePlayers();
        }

        //* این تابع رفرنس های خالی را از صحنه پیدا می کند.
        private void ResolveReferences()
        {
            if (wsClient == null) wsClient = DedicatedGameServerWsClient.Instance;
            if (wsClient == null) wsClient = FindObjectOfType<DedicatedGameServerWsClient>(true);
            if (remoteStateReceiver == null) remoteStateReceiver = FindObjectOfType<DedicatedRemotePlayerStateReceiver>(true);
            if (threeDModeController == null) threeDModeController = FindObjectOfType<G7ThreeDModeController>(true);
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

                remoteStateReceiver.RemotePlayerStateReceived += HandleRemotePlayerStateReceived;
                remoteStateReceiver.RemotePlayerJoined += HandleRemotePlayerJoined;
                remoteStateReceiver.RemotePlayerLeft += HandleRemotePlayerLeft;
            }

            if (realtimeRoomController != null)
            {
                realtimeRoomController.OnRoomLeftFor3D -= HandleRealtimeRoomLeft;
                realtimeRoomController.OnRoomLeftFor3D += HandleRealtimeRoomLeft;
            }

            if (grpcStreamingRealtimeRoomController != null)
            {
                grpcStreamingRealtimeRoomController.OnRoomLeftFor3D -= HandleRealtimeRoomLeft;
                grpcStreamingRealtimeRoomController.OnRoomLeftFor3D += HandleRealtimeRoomLeft;
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
            }

            if (realtimeRoomController != null)
            {
                realtimeRoomController.OnRoomLeftFor3D -= HandleRealtimeRoomLeft;
            }

            if (grpcStreamingRealtimeRoomController != null)
            {
                grpcStreamingRealtimeRoomController.OnRoomLeftFor3D -= HandleRealtimeRoomLeft;
            }
        }

        //* این تابع بعد از احراز ددیکیتد، مسیر نمایش ریموت پلیرها را آماده می کند.
        private void HandleDedicatedAuthenticated()
        {
            BeginDedicatedGameplayAfterAuth();
        }

        //* این تابع آماده سازی گیم پلی ددیکیتد را فقط یک بار بعد از auth_ok انجام می دهد.
        private void BeginDedicatedGameplayAfterAuth()
        {
            ResolveReferences();
            ApplyLegacySenderPolicy();

            if (wsClient == null || !wsClient.IsAuthenticated)
            {
                Log("Begin ignored. Dedicated websocket is not authenticated yet.");
                return;
            }

            if (threeDModeController == null)
            {
                Debug.LogError("[DedicatedRemotePlayerViewController] G7ThreeDModeController is missing.");
                return;
            }

            if (setLocalNameAfterDedicatedAuth)
            {
                threeDModeController.SetLocalPlayerDisplayName(ResolveLocalDisplayName());
            }

            if (autoEnter3DModeAfterDedicatedAuth && !threeDModeController.IsThreeDModeActive)
            {
                threeDModeController.EnterThreeDMode();
            }
            else if (ensureLocalPlayerAfterDedicatedAuth)
            {
                threeDModeController.EnsureLocalPlayerSpawned();
            }

            isDedicatedGameplayActive = true;
            ClearSuppressedRemoteRespawnCache("dedicated_authenticated");
            ResetLocalSendState();

            if (applyReceiverSnapshotAfterAuth)
            {
                ApplyReceiverSnapshot();
            }

            if (autoStartLocalStateSenderAfterDedicatedAuth)
            {
                StartLocalStateSending();
            }

            Log("Dedicated remote view ready | roomId=" + SafeForLog(wsClient.RoomId) + " | playerId=" + SafeForLog(wsClient.PlayerId));
        }

        //* این تابع بعد از قطع ددیکیتد، ارسال لوکال و کلون ها را پاک می کند.
        private void HandleDedicatedDisconnected(string reason)
        {
            isDedicatedGameplayActive = false;
            StopLocalStateSending(reason);
            SuppressKnownRemotePlayersBeforeClear(reason);
            dict_remoteLastSeenTimeByPlayerId.Clear();
            dict_remoteNamesByPlayerId.Clear();

            if (clearRemotePlayersOnDedicatedDisconnect)
            {
                ClearRemotePlayers();
            }
        }

        //* این تابع وقتی کلاینت از روم ریل تایم خارج شد، اتصال ددیکیتد همان روم را هم تمیز قطع می کند.
        private void HandleRealtimeRoomLeft(string roomId)
        {
            Log("Realtime room left received for dedicated cleanup | roomId=" + SafeForLog(roomId));

            if (!MatchesDedicatedRoomForRealtimeLeave(roomId))
            {
                Log("Realtime leave ignored. Dedicated room is different. realtimeRoomId=" + SafeForLog(roomId) + " | dedicatedRoomId=" + SafeForLog(wsClient != null ? wsClient.RoomId : string.Empty));
                return;
            }

            isDedicatedGameplayActive = false;

            if (stopLocalStateOnRealtimeRoomLeft)
            {
                StopLocalStateSending("realtime_room_left");
            }

            if (clearRemotePlayersOnRealtimeRoomLeft)
            {
                ClearRemotePlayers();
            }

            ClearSuppressedRemoteRespawnCache("realtime_room_left");

            if (disconnectDedicatedOnRealtimeRoomLeft && wsClient != null && wsClient.IsConnected)
            {
                wsClient.Disconnect("realtime_room_left");
                Log("Dedicated websocket disconnected after realtime leave | roomId=" + SafeForLog(roomId));
            }
        }

        //* این تابع پیام وضعیت ریموت را به کلون سه بعدی اعمال می کند.
        private void HandleRemotePlayerStateReceived(DedicatedRemotePlayerState state)
        {
            if (!CanApplyRemoteState(state)) return;

            string playerId = state.ResolvePlayerId();
            ClearSuppressedRemoteRespawn(playerId, "dedicated_state_received");
            string displayName = ResolveRemoteDisplayName(playerId, state.userName);
            dict_remoteLastSeenTimeByPlayerId[playerId] = Time.unscaledTime;
            dict_remoteNamesByPlayerId[playerId] = displayName;

            threeDModeController.SpawnOrUpdateRemotePlayer(playerId, displayName, state.Position, state.Rotation);

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
            dict_remoteNamesByPlayerId[playerId] = ResolveRemoteDisplayName(playerId, evt.userName);
            dict_remoteLastSeenTimeByPlayerId[playerId] = Time.unscaledTime;
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

            RemoveRemotePlayerFromDedicatedView(playerId, "leave");
            SuppressRemoteRespawn(playerId, evt.reason);
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
        }

        //* این تابع ارسال وضعیت لوکال را فعال می کند.
        public void StartLocalStateSending()
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
            ResetLocalSendState();
            Log("Dedicated local state sender started.");
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
        private void ResetLocalSendState()
        {
            hasLastSentState = false;
            localSequence = 0;
            lastStateHeartbeatSendTime = Time.unscaledTime;
            Transform localTransform = threeDModeController != null ? threeDModeController.GetLocalPlayerTransform() : null;
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

        //* این تابع ریموت پلیرهایی را که مدتی وضعیت نفرستاده اند حذف می کند.
        private void RemoveTimedOutRemotePlayers()
        {
            if (!removeRemotePlayersWhenStateTimeout) return;
            if (Time.unscaledTime < nextRemoteTimeoutCheckTime) return;

            nextRemoteTimeoutCheckTime = Time.unscaledTime + Mathf.Max(0.25f, remoteTimeoutCheckIntervalSeconds);
            if (dict_remoteLastSeenTimeByPlayerId.Count <= 0) return;

            float timeoutSeconds = Mathf.Max(1f, remotePlayerStateTimeoutSeconds);
            List<string> timedOutPlayerIds = null;

            foreach (KeyValuePair<string, float> pair in dict_remoteLastSeenTimeByPlayerId)
            {
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

        //* این تابع همه ریموت پلیرهای ددیکیتد را از کنترلر سه بعدی پاک می کند.
        private void ClearRemotePlayers()
        {
            dict_remoteLastSeenTimeByPlayerId.Clear();
            dict_remoteNamesByPlayerId.Clear();
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

        //* این تابع پلیر ریموت را از همه کش های ددیکیتد و صحنه حذف می کند.
        private void RemoveRemotePlayerFromDedicatedView(string playerId, string reason)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return;

            string safePlayerId = playerId.Trim();
            dict_remoteLastSeenTimeByPlayerId.Remove(safePlayerId);
            dict_remoteNamesByPlayerId.Remove(safePlayerId);
            threeDModeController?.RemoveRemotePlayer(safePlayerId);
            Log("Remote player removed | reason=" + SafeForLog(reason) + " | playerId=" + SafeForLog(safePlayerId));
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

        /*
        توضیح مکتوب فایل:
        این اسکریپت فاز DS-9 است و فقط روی کلاینت استفاده می شود.
        بعد از auth_ok ددیکیتد، حالت سه بعدی را آماده می کند، وضعیت پلیر لوکال را از روی Local Player واقعی می فرستد،
        و پیام های player_state دریافتی از DedicatedRemotePlayerStateReceiver را به کلون های ریموت در G7ThreeDModeController وصل می کند.
        DedicatedPlayerStateAutoSender قدیمی در این مسیر خاموش می شود تا وضعیت از ترنسفورم اشتباه ارسال نشود.
        */
    }
}
