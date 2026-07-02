using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Core;
using Network_A.Tests.Realtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Network_A.DedicatedGameServer.Client
{
    public class DedicatedClientTwoUserAutoFlowController : MonoBehaviour
    {
        [Serializable]
        public class ClientLoginProfile
        {
            public string label = "Client";
            public string userNameOrEmail = "";
            public string password = "";
        }

        public enum AutoStartClient
        {
            None,
            Client1,
            Client2
        }

        [Header("Client Profiles")]
        [SerializeField] private ClientLoginProfile client1 = new ClientLoginProfile { label = "Client 1" };
        [SerializeField] private ClientLoginProfile client2 = new ClientLoginProfile { label = "Client 2" };

        [Header("Buttons")]
        [SerializeField] private Button client1Button;
        [SerializeField] private Button client2Button;
        [SerializeField] private Button cancelButton;

        [Header("References")]
        [SerializeField] private AuthManager authManager;
        [SerializeField] private RealtimeWebSocketG7RoomLobbyTestController realtimeRoomController;
        [SerializeField] private DedicatedGameTicketClient ticketClient;
        [SerializeField] private DedicatedGameServerAutoConnectController autoConnectController;
        [SerializeField] private DedicatedGameServerWsClient wsClient;
        [SerializeField] private DedicatedPlayerStateAutoSender playerStateAutoSender;

        [Header("Flow")]
        [SerializeField] private AutoStartClient autoStartClient = AutoStartClient.None;
        [SerializeField] private float autoStartDelaySeconds = 0.5f;
        [SerializeField] private bool clearTokensBeforeLogin = true;
        [SerializeField] private bool disconnectDedicatedBeforeLogin = true;
        [SerializeField] private bool disconnectRealtimeBeforeLogin = true;
        [SerializeField] private bool runLoginInitAfterLogin = true;
        [SerializeField] private bool connectRealtimeAndAuth = true;
        [SerializeField] private bool listRoomsBeforeJoin = true;
        [SerializeField] private bool joinFirstListedRoom = true;
        [SerializeField] private bool connectDedicatedAfterJoin = true;
        [SerializeField] private bool startPlayerStateSenderAfterDedicatedAuth = false;

        [Header("Timing")]
        [SerializeField] private int waitAfterDisconnectMs = 500;
        [SerializeField] private int waitAfterLoginMs = 250;
        [SerializeField] private int waitAfterRealtimeMs = 250;
        [SerializeField] private int waitAfterJoinMs = 250;

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;
        [SerializeField] private TMP_Text statusText;

        private CancellationTokenSource flowCts;
        private bool isRunning;
        private string latestAccessToken = string.Empty;

        private void Awake()
        {
            EnsureReferences();
            BindButtons();
            ApplyDedicatedSafety();
            SetButtonsInteractable(true);
        }

        private async void Start()
        {
            if (autoStartClient == AutoStartClient.None) return;
            await Task.Delay(Mathf.RoundToInt(Mathf.Max(0f, autoStartDelaySeconds) * 1000f));
            if (autoStartClient == AutoStartClient.Client1) await RunClientFlowAsync(client1);
            if (autoStartClient == AutoStartClient.Client2) await RunClientFlowAsync(client2);
        }

        private void OnDestroy()
        {
            CancelFlow("destroyed");
            UnbindButtons();
        }

        public async void Btn_LoginClient1AndAutoConnect()
        {
            await RunClientFlowAsync(client1);
        }

        public async void Btn_LoginClient2AndAutoConnect()
        {
            await RunClientFlowAsync(client2);
        }

        public void Btn_CancelAutoFlow()
        {
            CancelFlow("manual_cancel");
        }

        public async Task<bool> RunClient1FlowAsync()
        {
            return await RunClientFlowAsync(client1);
        }

        public async Task<bool> RunClient2FlowAsync()
        {
            return await RunClientFlowAsync(client2);
        }

        private async Task<bool> RunClientFlowAsync(ClientLoginProfile profile)
        {
            if (isRunning)
            {
                Log("Flow ignored. Another flow is already running.");
                return false;
            }

            EnsureReferences();
            ApplyDedicatedSafety();

            if (!ValidateProfile(profile)) return false;
            if (!ValidateReferences()) return false;

            flowCts = new CancellationTokenSource();
            isRunning = true;
            SetButtonsInteractable(false);

            try
            {
                string label = Safe(profile.label, "Client");
                SetStatus(label + " flow started.");
                Log(label + " flow started | user=" + SafeForLog(profile.userNameOrEmail));

                await PrepareBeforeLoginAsync();

                bool loginOk = await LoginProfileAsync(profile, flowCts.Token);
                if (!loginOk) return false;

                if (connectRealtimeAndAuth)
                {
                    bool realtimeOk = await ConnectRealtimeJoinRoomAsync();
                    if (!realtimeOk) return false;
                }

                if (connectDedicatedAfterJoin)
                {
                    bool dedicatedOk = await ConnectDedicatedUsingJoinedRoomAsync();
                    if (!dedicatedOk) return false;
                }

                if (startPlayerStateSenderAfterDedicatedAuth && playerStateAutoSender != null)
                {
                    playerStateAutoSender.StartSending();
                }

                SetStatus(label + " flow finished successfully.");
                Log(label + " flow finished successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return Fail("Flow cancelled.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[DedicatedClientTwoUserAutoFlowController] Flow exception | " + ex);
                SetStatus("Flow exception: " + ex.Message);
                return false;
            }
            finally
            {
                isRunning = false;
                SetButtonsInteractable(true);
                DisposeFlowToken();
            }
        }

        private async Task PrepareBeforeLoginAsync()
        {
            if (disconnectDedicatedBeforeLogin && wsClient != null && wsClient.IsConnected)
            {
                wsClient.Disconnect("client_auto_flow_restart");
                await DelaySafe(waitAfterDisconnectMs);
            }

            if (disconnectRealtimeBeforeLogin && realtimeRoomController != null && realtimeRoomController.IsRealtimeReadyState)
            {
                realtimeRoomController.DisconnectButton();
                await DelaySafe(waitAfterDisconnectMs);
            }

            if (clearTokensBeforeLogin)
            {
                latestAccessToken = string.Empty;
                SecureTokenStorage.ClearTokens();
                await Task.Yield();
            }
        }

        private async Task<bool> LoginProfileAsync(ClientLoginProfile profile, CancellationToken cancellationToken)
        {
            SetStatus("Logging in " + Safe(profile.label, "Client") + "...");

            ApiResult<AuthResponseDto> loginResult = await authManager.LoginAsync(
                profile.userNameOrEmail.Trim(),
                profile.password,
                cancellationToken);

            if (loginResult == null) return Fail("Login result is null.");
            if (!loginResult.IsSuccess) return Fail("Login failed | status=" + loginResult.StatusCode + " | error=" + loginResult.ErrorMessage);

            latestAccessToken = ResolveAccessTokenFromLoginResult(loginResult);
            if (string.IsNullOrWhiteSpace(latestAccessToken)) latestAccessToken = SecureTokenStorage.GetAccessToken();

            await DelaySafe(waitAfterLoginMs);

            if (runLoginInitAfterLogin)
            {
                await authManager.Login_Init();
                await DelaySafe(waitAfterLoginMs);
            }

            if (authManager.CurrentUser == null) return Fail("Login_Init did not prepare AuthManager.CurrentUser.");

            string storedAccessToken = SecureTokenStorage.GetAccessToken();
            if (string.IsNullOrWhiteSpace(latestAccessToken)) latestAccessToken = storedAccessToken;
            if (string.IsNullOrWhiteSpace(latestAccessToken)) return Fail("Access token is empty after login.");

            Log("Login ok | currentUser=" + SafeForLog(authManager.CurrentUser.emailOrUsername) + " | userId=" + SafeForLog(authManager.CurrentUser.id));
            return true;
        }

        private string ResolveAccessTokenFromLoginResult(ApiResult<AuthResponseDto> loginResult)
        {
            if (loginResult == null || loginResult.Data == null) return string.Empty;
            return string.IsNullOrWhiteSpace(loginResult.Data.accessToken) ? string.Empty : loginResult.Data.accessToken.Trim();
        }

        private async Task<bool> ConnectRealtimeWithFreshTokenAsync()
        {
            string accessToken = latestAccessToken;
            if (string.IsNullOrWhiteSpace(accessToken)) accessToken = SecureTokenStorage.GetAccessToken();
            if (string.IsNullOrWhiteSpace(accessToken)) return Fail("Access token is empty before realtime connect.");

            bool realtimeOk = await realtimeRoomController.LoginCheckConnectAndAuthWithAccessTokenAsync(accessToken);
            if (realtimeOk) return true;

            Log("Realtime connect/auth failed once. Retrying with stored fresh token after clean disconnect.");

            if (realtimeRoomController != null)
            {
                realtimeRoomController.DisconnectButton();
                await DelaySafe(waitAfterDisconnectMs);
            }

            string retryToken = SecureTokenStorage.GetAccessToken();
            if (string.IsNullOrWhiteSpace(retryToken)) retryToken = accessToken;
            return await realtimeRoomController.LoginCheckConnectAndAuthWithAccessTokenAsync(retryToken);
        }

        private async Task<bool> ConnectRealtimeJoinRoomAsync()
        {
            if (realtimeRoomController == null) return Fail("Realtime room controller is missing.");

            SetStatus("Connecting realtime...");

            bool realtimeOk = await ConnectRealtimeWithFreshTokenAsync();
            if (!realtimeOk) return Fail("Realtime connect/auth failed.");

            await DelaySafe(waitAfterRealtimeMs);

            if (listRoomsBeforeJoin && !realtimeRoomController.IsJoinedRoom)
            {
                bool listed = await realtimeRoomController.ListRoomsAsync();
                if (!listed) return Fail("List rooms failed.");
            }

            if (joinFirstListedRoom && !realtimeRoomController.IsJoinedRoom)
            {
                bool joined = await realtimeRoomController.JoinFirstListedRoomAsync();
                if (!joined) return Fail("Join first listed room failed.");
            }

            await DelaySafe(waitAfterJoinMs);

            if (!realtimeRoomController.IsJoinedRoom) return Fail("Realtime client is not joined to a room.");
            if (string.IsNullOrWhiteSpace(realtimeRoomController.CurrentRoomId)) return Fail("Realtime CurrentRoomId is empty after join.");

            Log("Realtime joined | roomId=" + SafeForLog(realtimeRoomController.CurrentRoomId) + " | user=" + SafeForLog(realtimeRoomController.CurrentUserName));
            return true;
        }

        private async Task<bool> ConnectDedicatedUsingJoinedRoomAsync()
        {
            if (ticketClient == null) return Fail("DedicatedGameTicketClient is missing.");
            if (autoConnectController == null) return Fail("DedicatedGameServerAutoConnectController is missing.");
            if (realtimeRoomController == null) return Fail("Realtime room controller is missing.");

            string roomId = realtimeRoomController.CurrentRoomId;
            if (string.IsNullOrWhiteSpace(roomId)) return Fail("Cannot connect dedicated. Realtime room id is empty.");

            string displayName = ResolveCurrentDisplayName();

            SetPrivateField(ticketClient, "roomId", roomId.Trim());
            SetPrivateField(autoConnectController, "fallbackUserName", displayName);
            ApplyDedicatedSafety();

            SetStatus("Connecting dedicated game server...");
            Log("Dedicated connect started | roomId=" + SafeForLog(roomId) + " | displayName=" + SafeForLog(displayName));

            bool ok = await autoConnectController.RunAutoTicketConnectAndAuthAsync();
            if (!ok) return Fail("Dedicated ticket/connect/auth failed.");

            if (wsClient != null && !wsClient.IsAuthenticated) return Fail("Dedicated websocket is not authenticated after auto flow.");

            SetStatus("Dedicated game server connected.");
            Log("Dedicated connect finished | result=True | roomId=" + SafeForLog(roomId));
            return true;
        }

        private void EnsureReferences()
        {
            if (authManager == null) authManager = AuthManager.Instance;
            if (authManager == null) authManager = FindObjectOfType<AuthManager>(true);

            if (realtimeRoomController == null) realtimeRoomController = FindObjectOfType<RealtimeWebSocketG7RoomLobbyTestController>(true);
            if (ticketClient == null) ticketClient = FindObjectOfType<DedicatedGameTicketClient>(true);
            if (autoConnectController == null) autoConnectController = FindObjectOfType<DedicatedGameServerAutoConnectController>(true);

            if (wsClient == null) wsClient = DedicatedGameServerWsClient.Instance;
            if (wsClient == null) wsClient = FindObjectOfType<DedicatedGameServerWsClient>(true);

            if (playerStateAutoSender == null) playerStateAutoSender = FindObjectOfType<DedicatedPlayerStateAutoSender>(true);
        }

        private bool ValidateReferences()
        {
            if (authManager == null) return Fail("AuthManager is missing.");
            if (realtimeRoomController == null) return Fail("RealtimeWebSocketG7RoomLobbyTestController is missing.");
            if (ticketClient == null) return Fail("DedicatedGameTicketClient is missing.");
            if (autoConnectController == null) return Fail("DedicatedGameServerAutoConnectController is missing.");
            if (wsClient == null) Log("DedicatedGameServerWsClient is not assigned. AutoConnectController may create/find it.");
            return true;
        }

        private bool ValidateProfile(ClientLoginProfile profile)
        {
            if (profile == null) return Fail("Client profile is null.");
            if (string.IsNullOrWhiteSpace(profile.userNameOrEmail)) return Fail("Client username/email is empty.");
            if (string.IsNullOrWhiteSpace(profile.password)) return Fail("Client password is empty.");
            return true;
        }

        private void BindButtons()
        {
            if (client1Button != null)
            {
                client1Button.onClick.RemoveListener(Btn_LoginClient1AndAutoConnect);
                client1Button.onClick.AddListener(Btn_LoginClient1AndAutoConnect);
            }

            if (client2Button != null)
            {
                client2Button.onClick.RemoveListener(Btn_LoginClient2AndAutoConnect);
                client2Button.onClick.AddListener(Btn_LoginClient2AndAutoConnect);
            }

            if (cancelButton != null)
            {
                cancelButton.onClick.RemoveListener(Btn_CancelAutoFlow);
                cancelButton.onClick.AddListener(Btn_CancelAutoFlow);
            }
        }

        private void UnbindButtons()
        {
            if (client1Button != null) client1Button.onClick.RemoveListener(Btn_LoginClient1AndAutoConnect);
            if (client2Button != null) client2Button.onClick.RemoveListener(Btn_LoginClient2AndAutoConnect);
            if (cancelButton != null) cancelButton.onClick.RemoveListener(Btn_CancelAutoFlow);
        }

        private void ApplyDedicatedSafety()
        {
            if (autoConnectController == null) return;
            SetPrivateField(autoConnectController, "autoRunOnStart", false);
            SetPrivateField(autoConnectController, "waitForAccessToken", true);
            SetPrivateField(autoConnectController, "waitForAccessTokenSeconds", 60f);
        }

        private string ResolveCurrentDisplayName()
        {
            if (realtimeRoomController != null && !string.IsNullOrWhiteSpace(realtimeRoomController.CurrentUserName)) return realtimeRoomController.CurrentUserName.Trim();
            if (authManager != null && authManager.CurrentUser != null && !string.IsNullOrWhiteSpace(authManager.CurrentUser.emailOrUsername)) return authManager.CurrentUser.emailOrUsername.Trim();
            if (authManager != null && authManager.CurrentUser != null && !string.IsNullOrWhiteSpace(authManager.CurrentUser.id)) return authManager.CurrentUser.id.Trim();
            return "dedicated_client";
        }

        private void CancelFlow(string reason)
        {
            if (flowCts != null && !flowCts.IsCancellationRequested) flowCts.Cancel();
            Log("Cancel requested | reason=" + reason);
        }

        private void DisposeFlowToken()
        {
            if (flowCts == null) return;
            flowCts.Dispose();
            flowCts = null;
        }

        private async Task DelaySafe(int milliseconds)
        {
            int safeMs = Mathf.Max(0, milliseconds);
            if (safeMs <= 0) return;
            await Task.Delay(safeMs, flowCts != null ? flowCts.Token : CancellationToken.None);
        }

        private void SetButtonsInteractable(bool value)
        {
            if (client1Button != null) client1Button.interactable = value && !isRunning;
            if (client2Button != null) client2Button.interactable = value && !isRunning;
            if (cancelButton != null) cancelButton.interactable = isRunning;
        }

        private void SetPrivateField(object target, string fieldName, object value)
        {
            if (target == null || string.IsNullOrWhiteSpace(fieldName)) return;

            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field == null)
            {
                Debug.LogWarning("[DedicatedClientTwoUserAutoFlowController] Field not found | type=" + target.GetType().Name + " | field=" + fieldName);
                return;
            }

            try
            {
                field.SetValue(target, value);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DedicatedClientTwoUserAutoFlowController] Set field failed | field=" + fieldName + " | error=" + ex.Message);
            }
        }

        private bool Fail(string message)
        {
            string safeMessage = string.IsNullOrWhiteSpace(message) ? "Unknown error." : message.Trim();
            Debug.LogError("[DedicatedClientTwoUserAutoFlowController] " + safeMessage);
            SetStatus(safeMessage);
            return false;
        }

        private void SetStatus(string message)
        {
            if (statusText != null) statusText.text = message ?? string.Empty;
            Log(message);
        }

        private void Log(string message)
        {
            if (!verboseLogs) return;
            Debug.Log("[DedicatedClientTwoUserAutoFlowController] " + message);
        }

        private string Safe(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private string SafeForLog(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<empty>" : value.Trim();
        }
    }
}
