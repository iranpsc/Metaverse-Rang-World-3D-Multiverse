using System;
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

        [Header("Room")]
        [SerializeField] private string roomNamePrefix = "WebGL G7 Lobby Room";
        [SerializeField] private string roomDescription = "Room created by Unity G7 Room Lobby test.";
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

        [Header("Keep Alive")]
        [SerializeField] private bool enableTestKeepAlive = false;
        [SerializeField] private int keepAliveIntervalMs = 5000;
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
        [Header("Room List UI")]
        [SerializeField] private Transform roomListContent;
        [SerializeField] private RealtimeRoomListItemView roomListItemPrefab;
        [SerializeField] private bool disableRoomListWhileJoining = true;
        [SerializeField] private bool clearRoomListOnJoinSuccess = false;
        private RealtimeClient realtimeClient;
        private RealtimeAuthClient realtimeAuthClient;
        private RealtimeLobbyClient realtimeLobbyClient;
        private GameServerClient gameServerClient;
        private CancellationTokenSource lifecycleCts;
        private CancellationTokenSource keepAliveCts;

        private TaskCompletionSource<bool> authWaiter;
        private TaskCompletionSource<bool> leaveAckWaiter;

        private string activeServerUrl = string.Empty;
        private string activeRoomId = string.Empty;
        private string activeRoomName = string.Empty;

        private bool isConnected;
        private bool isAuthenticated;
        private bool isJoined;
        private bool eventsBound;
        private bool isCleaningUp;
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
        public string CurrentUserId => currentRealtimeUserId;
        public string CurrentUserName => currentRealtimeUserName;
        public bool IsJoinedRoom => isJoined;
        public bool IsRealtimeReadyState => IsRealtimeReady();

        public event Action<string> OnRoomJoinedFor3D;
        public event Action<string> OnRoomLeftFor3D;
        public event Action<string> OnRealtimeDisconnectedFor3D;
        public event Action<string, string> OnPlayerJoinedFor3D;
        public event Action<string, string> OnPlayerLeftFor3D;
        public event Action<RealtimeEnvelope> OnPlayerStateReceivedFor3D;
        public event Action<RealtimeEnvelope> OnRoomMembersSnapshotReceivedFor3D;

        private bool lastSendMessageButtonInteractable;
        private string lastSendMessageButtonStateReason = string.Empty;
        private string lastMessageInputTextForButtonSync = string.Empty;
        private bool lastRealtimeReadyForSendButtonSync;
        private bool lastJoinedForSendButtonSync;
        private bool lastJoinRoomRunningForSendButtonSync;
        private bool lastLeaveRoomRunningForSendButtonSync;
        private bool lastCleaningUpForSendButtonSync;
        private bool lastSendMessageRunningForButtonSync;

        private void Awake()
        {
            EnsureLifecycleToken();
            activeServerUrl = ResolveRealtimeServerUrl();
            activeRoomName = BuildRoomName();
            LogUiReferences("Awake");
            UpdateRoomDisplay();
            SetStatus("Ready");
            Log("G7 controller ready. url=" + activeServerUrl);
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
            BindMessageInputEvents();
            BindRoomNameInputEvents();
            SyncCreateRoomButtonFromRoomInput(true);
            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            SyncSendMessageButtonFromMessageInput(true);
        }

        private void Update()
        {
            DetectRealtimeConnectionDrop();
            SyncCreateRoomButtonFromRoomInput(false);
            SyncSendMessageButtonFromMessageInput(false);
            ApplyPendingUiRefresh();
        }

        private async void OnDestroy()
        {
            try
            {
                await CleanupAsync("G7 object destroyed", true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[G7-RoomLobby] Destroy cleanup warning: " + ex.Message);
            }
            finally
            {
                UnbindMessageInputEvents();
                UnbindRoomNameInputEvents();
                StopKeepAliveLoop();
                lifecycleCts?.Cancel();
                lifecycleCts?.Dispose();
                lifecycleCts = null;
            }
        }

        public async void ConnectAndAuthButton()
        {
            if (isConnectAndAuthRunning) return;

            isConnectAndAuthRunning = true;
            UpdateConnectionButtons();
            UpdateCreateRoomButton();

            try
            {
                await LoginCheckConnectAndAuthAsync();
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
            await CleanupAsync("Manual G7 disconnect");
        }

        public async void RunFullLobbyTestButton()
        {
            await RunFullLobbyTestAsync();
        }

        public async Task<bool> RunFullLobbyTestAsync()
        {
            Log("G7 full lobby test started.");

            if (!await LoginCheckConnectAndAuthAsync()) return false;
            if (!await CreateRoomAsync()) return false;
            if (!await ListRoomsAsync()) return false;
            if (!await JoinFirstListedRoomAsync()) return false;
            if (!await SendChatMessageAsync("G7 lobby test message")) return false;
            if (!await LeaveRoomAsync()) return false;

            Log("G7 full lobby test completed.");
            SetStatus("G7 PASSED");
            return true;
        }

        public async Task<bool> LoginCheckConnectAndAuthAsync()
        {
            EnsureLifecycleToken();

            if (IsRealtimeReady())
            {
                await RefreshCurrentUserCreatedRoomStateAsync();
                UpdateConnectionButtons();
                UpdateCreateRoomButton();
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

            string storedToken = SecureTokenStorage.GetAccessToken();
            if (string.IsNullOrWhiteSpace(storedToken))
            {
                return Fail("Stored access token is empty. First login with normal Auth UI.");
            }

            UpdateCurrentUserIdentityFromStoredToken();

            if (realtimeClient == null) CreateClientObjects();

            bool connected = await ConnectAsync();
            if (!connected) return Fail("Realtime connect failed.");

            bool authenticated = await AuthenticateWithStoredTokenAsync();
            if (!authenticated) return Fail("Realtime auth failed.");

            await RefreshCurrentUserCreatedRoomStateAsync();

            StartKeepAliveLoop();

            ShowRealtimeSuccessMessage("Realtime connected and authenticated.");
            Log("Realtime connection and auth completed.");
            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();
            return true;
        }

        private async Task<bool> ConnectAsync()
        {
            EnsureLifecycleToken();
            activeServerUrl = ResolveRealtimeServerUrl();
            Log("Connecting to " + activeServerUrl + " | uiTimeoutMs=" + connectTimeoutMs);

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
            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();
            return isConnected;
        }

        private async Task<bool> AuthenticateWithStoredTokenAsync()
        {
            EnsureLifecycleToken();
            authWaiter = CreateBoolWaiter();

            using (CancellationTokenSource authCts = CreateLinkedTimeoutToken(waitTimeoutMs))
            {
                bool sent = await realtimeAuthClient.AuthenticateWithStoredTokenAsync(authCts.Token);
                if (!sent) return Fail("Realtime auth message was not sent.");

                bool ok = await WaitForBoolAsync(authWaiter, waitTimeoutMs, authCts.Token);
                isAuthenticated = ok && realtimeAuthClient.IsAuthenticated;

                Log("Auth result: " + isAuthenticated);
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
                UpdateSendMessageButton();
                return true;
            }

            if (!await LoginCheckConnectAndAuthAsync()) return false;

            RealtimeLobbyListRoomsResult result = await realtimeLobbyClient.ListRoomsAsync(
                CreateReliableOptions(),
                lifecycleCts.Token
            );

            if (result == null) return Fail("List rooms result is null.");
            if (!result.isSuccess) return Fail("List rooms failed: " + result.errorMessage);

            lastListedRooms = result.Rooms ?? Array.Empty<RealtimeRoomDto>();

            RenderRooms(lastListedRooms);
            RenderRoomListButtons(lastListedRooms);
            SetListRoomsButtonInteractable(!isJoined);

            if (isJoined) ShowRealtimeWarningMessage("You are already inside a room.");
            else ShowRealtimeInfoMessage("Rooms refreshed. Count: " + result.Count);
            Log("List rooms result: count=" + result.Count);
            UpdateSendMessageButton();

            if (!string.IsNullOrWhiteSpace(lastCreatedRoomId))
            {
                RealtimeRoomDto listedRoom = result.response == null ? null : result.response.FindRoomById(lastCreatedRoomId);
                Log("Created room exists in list: " + (listedRoom != null));
            }

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
                if (!await LoginCheckConnectAndAuthAsync()) return false;
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

                joinedRoom = selectedListedRoom ?? FindLastListedRoom(activeRoomId);
                if (joinedRoom != null)
                {
                    joinedRoom.Normalize();
                    joinedRoom.onlineCount = Mathf.Clamp(joinedRoom.onlineCount + 1, 1, joinedRoom.maxPlayers);

                    UpdateRoomDisplay(joinedRoom, true);
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
            if (!await LoginCheckConnectAndAuthAsync()) return false;

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
                UpdateConnectionButtons();
                UpdateSendMessageButton();
                return true;
            }

            isLeaveRoomRunning = true;
            UpdateConnectionButtons();
            UpdateSendMessageButton();

            try
            {
                leaveAckWaiter = CreateBoolWaiter();

                bool sent = await gameServerClient.LeaveRoomAsync(activeRoomId, lifecycleCts.Token);
                if (!sent) return Fail("Leave room message was not sent.");

                bool ack = await WaitForBoolAsync(leaveAckWaiter, waitTimeoutMs, lifecycleCts.Token);
                isJoined = !ack;

                if (ack)
                {
                    string leftRoomIdFor3D = activeRoomId;
                    OnRoomLeftFor3D?.Invoke(leftRoomIdFor3D);

                    joinedRoom = null;
                    selectedListedRoom = null;
                    activeRoomId = string.Empty;
                    activeRoomName = string.Empty;
                    SetRoomListInteractable(true);
                    UpdateListRoomsButton();
                    UpdateRoomDisplay();
                    UpdateSendMessageButton();
                }

                Log("Leave room ack result: " + ack);
                if (ack) ShowRealtimeWarningMessage("Left room. Select another room if needed.");
                else ShowRealtimeErrorMessage("Leave timeout.");

                UpdateConnectionButtons();
                UpdateSendMessageButton();
                return ack;
            }
            finally
            {
                isLeaveRoomRunning = false;
                UpdateConnectionButtons();
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
            ShowRealtimeErrorMessage("Transport error: " + error);
            UpdateConnectionButtons();
            UpdateSendMessageButton();
        }

        private void HandleDisconnected(string reason)
        {
            StopKeepAliveLoop();
            transportDropAlreadyHandled = true;
            isConnected = false;
            isAuthenticated = false;
            isJoined = false;
            isJoinRoomRunning = false;
            isLeaveRoomRunning = false;
            isSendMessageRunning = false;
            joinedRoom = null;

            SetRoomListInteractable(false);
            SetListRoomsButtonInteractable(false);
            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();

            Log("Disconnected: " + reason);
            ShowRealtimeWarningMessage("Realtime disconnected. You left all rooms.");
            OnRealtimeDisconnectedFor3D?.Invoke(reason);
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
            isAuthenticated = true;
            currentRealtimeUserId = string.IsNullOrWhiteSpace(userId) ? currentRealtimeUserId : userId.Trim();
            UpdateCurrentUserIdentityFromStoredToken();
            Log("Authenticated. connectionId=" + connectionId + " userId=" + currentRealtimeUserId + " userName=" + currentRealtimeUserName);
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

            Log("Game ack: " + ack.originalMessageId + " | processed=" + ack.IsProcessed());

            if (ack.originalMessageId.StartsWith("leave_room_", StringComparison.OrdinalIgnoreCase))
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

            OnPlayerJoinedFor3D?.Invoke(playerId, displayName);

            Log("Player joined: " + displayName);
            ShowRealtimeInfoMessage(displayName + " joined");

            if (!IsSameText(playerId, currentRealtimeUserId))
            {
                ApplyPresenceOnlineCountDelta(1, "player_joined", displayName);
            }
        }

        private void HandlePlayerLeftReceived(GameServerPresenceEvent presence)
        {
            if (presence == null) return;

            string playerId = ResolvePresencePlayerIdFor3D(presence);
            string displayName = ResolvePresenceDisplayName(presence, playerId);

            OnPlayerLeftFor3D?.Invoke(playerId, displayName);

            Log("Player left: " + displayName);
            ShowRealtimeWarningMessage(displayName + " left");

            if (!IsSameText(playerId, currentRealtimeUserId))
            {
                ApplyPresenceOnlineCountDelta(-1, "player_left", displayName);
            }
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

            int maxPlayersSafe = Mathf.Max(1, joinedRoom.maxPlayers);
            int minUsersSafe = isJoined ? 1 : 0;
            int currentOnlineCount = Mathf.Max(minUsersSafe, joinedRoom.onlineCount);

            joinedRoom.onlineCount = Mathf.Clamp(currentOnlineCount + delta, minUsersSafe, maxPlayersSafe);
            UpdateRoomDisplay(joinedRoom, true);
            Log("Room users updated from " + source + ". player=" + displayName + " | users=" + joinedRoom.onlineCount + "/" + maxPlayersSafe);
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

            if (connectButton != null) connectButton.interactable = !isConnectAndAuthRunning && !isCleaningUp && !ready;
            if (disconnectButton != null) disconnectButton.interactable = !isConnectAndAuthRunning && !isCleaningUp && ready;
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
            if (transportDropAlreadyHandled) return;

            transportDropAlreadyHandled = true;
            StopKeepAliveLoop();

            Log(reason + " | connected=" + isConnected
                + " | authenticated=" + isAuthenticated
                + " | joined=" + isJoined
                + " | clientConnected=" + (realtimeClient != null && realtimeClient.IsConnected)
                + " | authClientAuthenticated=" + (realtimeAuthClient != null && realtimeAuthClient.IsAuthenticated));

            isConnected = false;
            isAuthenticated = false;
            isJoined = false;

            SetRoomListInteractable(false);
            SetListRoomsButtonInteractable(false);
            UpdateConnectionButtons();
            UpdateCreateRoomButton();
            UpdateSendMessageButton();
            ShowRealtimeWarningMessage("Realtime disconnected. Connect/Auth again.");
            OnRealtimeDisconnectedFor3D?.Invoke(reason);
        }

        private void StartKeepAliveLoop()
        {
            if (!enableTestKeepAlive)
            {
                Log("KeepAlive skipped. enableTestKeepAlive=false");
                return;
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

                    using (CancellationTokenSource pingCts = CreateLinkedTimeoutToken(Mathf.Max(1000, sendTimeoutMs)))
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

        private async Task CleanupAsync(string reason, bool objectDestroy = false)
        {
            if (isCleaningUp) return;

            isCleaningUp = true;
            Log("Cleanup started: " + reason + " | objectDestroy=" + objectDestroy);
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

                CleanupClientObjectsOnly();

                isConnected = false;
                isAuthenticated = false;
                isJoined = false;
                isConnectAndAuthRunning = false;
                isCreateRoomRunning = false;
                isJoinRoomRunning = false;
                isLeaveRoomRunning = false;
                transportDropAlreadyHandled = true;

                SetListRoomsButtonInteractable(false);
                if (!objectDestroy) ShowRealtimeWarningMessage("Disconnected. You left all rooms.");
            }
            finally
            {
                isCleaningUp = false;
                Log("Cleanup completed: " + reason);
                UpdateConnectionButtons();
                UpdateCreateRoomButton();
                UpdateSendMessageButton();
            }
        }

        private void CleanupClientObjectsOnly()
        {
            StopKeepAliveLoop();
            ClearRoomListButtons();

            selectedListedRoom = null;
            joinedRoom = null;
            isJoiningFromRoomList = false;
            isJoinRoomRunning = false;
            isLeaveRoomRunning = false;
            lastListedRooms = Array.Empty<RealtimeRoomDto>();
            lastCreatedRoomId = string.Empty;
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
            string line = "[G7-RoomLobby] " + safeMessage;
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

        //* این تابع مقدار تکست را اعمال می کند و در صورت نیاز مش تکست مش پرو را تازه سازی می کند.
        private void ApplyTextMeshValue(TextMeshProUGUI targetText, string value)
        {
            if (targetText == null) return;

            targetText.text = value ?? string.Empty;

            if (!forceTextMeshRefreshAfterUiApply) return;

            targetText.SetVerticesDirty();
            targetText.SetLayoutDirty();
            targetText.ForceMeshUpdate(true, true);
        }

        //* این تابع وضعیت وصل بودن رفرنس های یو آی را مستقیم در کنسول چاپ می کند.
        private void LogUiReferences(string source)
        {
            Debug.Log("[G7-RoomLobby] UI refs " + source
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

        private void UpdateRoomDisplay(RealtimeRoomDto room, bool joined)
        {
            if (room == null)
            {
                QueueRoomText("Room: -\nOwner: -\nUsers: -");
                return;
            }

            room.Normalize();

            int onlineCount = joined ? Mathf.Max(1, room.onlineCount) : room.onlineCount;
            string ownerName = string.IsNullOrWhiteSpace(room.ownerUserName) ? "-" : room.ownerUserName;
            string roomName = string.IsNullOrWhiteSpace(room.roomName) ? "-" : room.roomName;

            QueueRoomText(
                "Room: " + roomName +
                "\nOwner: " + ownerName +
                "\nUsers: " + onlineCount + "/" + room.maxPlayers
            );
        }
    }
}
