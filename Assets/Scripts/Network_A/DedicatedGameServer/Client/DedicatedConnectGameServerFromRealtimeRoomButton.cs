using System;
using System.Reflection;
using System.Threading.Tasks;
using Network_A.Auth;
using UnityEngine;
using UnityEngine.UI;

namespace Network_A.DedicatedGameServer.Client
{
    public class DedicatedConnectGameServerFromRealtimeRoomButton : MonoBehaviour
    {
        [Header("Button")]
        [SerializeField] private Button connectButton;

        [Header("Realtime Room Source")]
        [SerializeField] private UnityEngine.Object realtimeRoomController;

        [Header("Dedicated References")]
        [SerializeField] private DedicatedGameTicketClient ticketClient;
        [SerializeField] private DedicatedGameServerAutoConnectController autoConnectController;
        [SerializeField] private DedicatedGameServerWsClient wsClient;

        [Header("Rules")]
        [SerializeField] private bool requireRealtimeReady = true;
        [SerializeField] private bool requireJoinedRoom = true;
        [SerializeField] private bool requireAccessToken = true;
        [SerializeField] private bool blockIfAuthAndRealtimeMismatch = true;
        [SerializeField] private bool disconnectBeforeConnect = true;
        [SerializeField] private bool keepButtonDisabledAfterSuccess = true;

        [Header("Safety")]
        [SerializeField] private bool forceAutoRunOnStartOff = true;
        [SerializeField] private bool forceRetrySettingsOn = true;
        [SerializeField] private bool clearPreferredServerIdWhenUsingRealtimeRoom = true;

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;
        [SerializeField] private bool logStateEverySecond = false;

        private bool isConnecting;
        private bool hasConnectedSuccessfully;
        private float nextStateLogAt;

        //* This function binds the button and prepares safe manual dedicated connect.
        private void Awake()
        {
            if (connectButton == null)
            {
                connectButton = GetComponent<Button>();
            }

            EnsureReferences();
            ApplyAutoConnectSafety();
            BindButton();
            RefreshButtonState();

            Log("Realtime-room connect button ready.");
        }

        //* This function keeps button state updated.
        private void Update()
        {
            RefreshButtonState();

            if (logStateEverySecond && Time.realtimeSinceStartup >= nextStateLogAt)
            {
                nextStateLogAt = Time.realtimeSinceStartup + 1f;
                PrintCurrentState("tick");
            }
        }

        //* This function finds references without changing old scripts.
        private void EnsureReferences()
        {
            if (realtimeRoomController == null)
            {
                realtimeRoomController = FindObjectOfTypeByName("RealtimeWebSocketG7RoomLobbyTestController");
            }

            if (realtimeRoomController == null)
            {
                realtimeRoomController = FindObjectOfTypeByName("RealtimeGrpcStreamingG7RoomLobbyTestController");
            }

            if (ticketClient == null)
            {
                ticketClient = GetComponent<DedicatedGameTicketClient>();
            }

            if (ticketClient == null)
            {
                ticketClient = FindObjectOfType<DedicatedGameTicketClient>(true);
            }

            if (autoConnectController == null)
            {
                autoConnectController = GetComponent<DedicatedGameServerAutoConnectController>();
            }

            if (autoConnectController == null)
            {
                autoConnectController = FindObjectOfType<DedicatedGameServerAutoConnectController>(true);
            }

            if (wsClient == null)
            {
                wsClient = GetComponent<DedicatedGameServerWsClient>();
            }

            if (wsClient == null)
            {
                wsClient = DedicatedGameServerWsClient.Instance;
            }

            if (wsClient == null)
            {
                wsClient = FindObjectOfType<DedicatedGameServerWsClient>(true);
            }
        }

        //* This function binds the UI button click.
        private void BindButton()
        {
            if (connectButton == null) return;

            connectButton.onClick.RemoveListener(OnConnectClicked);
            connectButton.onClick.AddListener(OnConnectClicked);
        }

        //* This function is called by the UI button.
        private async void OnConnectClicked()
        {
            await ConnectUsingRealtimeRoomAsync();
        }

        //* This context menu connects manually using realtime room data.
        [ContextMenu("Connect Game Server From Realtime Room")]
        public async void Btn_ConnectGameServerFromRealtimeRoom()
        {
            await ConnectUsingRealtimeRoomAsync();
        }

        //* This function connects to the dedicated game server using the already joined realtime room and current auth user.
        public async Task<bool> ConnectUsingRealtimeRoomAsync()
        {
            if (isConnecting)
            {
                Log("Connect ignored because it is already running.");
                return false;
            }

            EnsureReferences();
            ApplyAutoConnectSafety();

            RealtimeRoomSnapshot realtime = ReadRealtimeSnapshot();
            AuthUserSnapshot auth = ReadAuthUserSnapshot();
            string accessToken = SafeAccessToken();

            PrintStateForConnect(realtime, auth, accessToken);

            if (requireRealtimeReady && !realtime.IsRealtimeReady)
            {
                Debug.LogWarning("[DedicatedConnectGameServerFromRealtimeRoomButton] Connect blocked. Realtime is not ready.");
                RefreshButtonState();
                return false;
            }

            if (requireJoinedRoom && !realtime.IsJoinedRoom)
            {
                Debug.LogWarning("[DedicatedConnectGameServerFromRealtimeRoomButton] Connect blocked. User is not joined to a realtime room.");
                RefreshButtonState();
                return false;
            }

            if (string.IsNullOrWhiteSpace(realtime.RoomId))
            {
                Debug.LogWarning("[DedicatedConnectGameServerFromRealtimeRoomButton] Connect blocked. Realtime CurrentRoomId is empty.");
                RefreshButtonState();
                return false;
            }

            if (!auth.IsReady)
            {
                Debug.LogWarning("[DedicatedConnectGameServerFromRealtimeRoomButton] Connect blocked. AuthManager.CurrentUser is not ready.");
                RefreshButtonState();
                return false;
            }

            if (requireAccessToken && string.IsNullOrWhiteSpace(accessToken))
            {
                Debug.LogWarning("[DedicatedConnectGameServerFromRealtimeRoomButton] Connect blocked. Access token is missing.");
                RefreshButtonState();
                return false;
            }

            if (blockIfAuthAndRealtimeMismatch && !IsAuthRealtimeSameUser(auth, realtime))
            {
                Debug.LogError("[DedicatedConnectGameServerFromRealtimeRoomButton] Connect blocked. Auth user and Realtime user do not match. authUser=" +
                               SafeForLog(auth.DisplayName) + " | authKey=" + SafeForLog(auth.UserKey) +
                               " | realtimeUser=" + SafeForLog(realtime.UserName) + " | realtimeUserId=" + SafeForLog(realtime.UserId) +
                               " | roomId=" + SafeForLog(realtime.RoomId) +
                               " | roomName=" + SafeForLog(realtime.RoomName));
                RefreshButtonState();
                return false;
            }

            if (ticketClient == null)
            {
                Debug.LogError("[DedicatedConnectGameServerFromRealtimeRoomButton] DedicatedGameTicketClient is missing.");
                RefreshButtonState();
                return false;
            }

            if (autoConnectController == null)
            {
                Debug.LogError("[DedicatedConnectGameServerFromRealtimeRoomButton] DedicatedGameServerAutoConnectController is missing.");
                RefreshButtonState();
                return false;
            }

            isConnecting = true;
            RefreshButtonState();

            try
            {
                ticketClient.SetRoomContext(realtime.RoomId, realtime.RoomName);
                if (clearPreferredServerIdWhenUsingRealtimeRoom) ticketClient.SetPreferredServerId(string.Empty);
                SetPrivateField(ticketClient, "roomId", realtime.RoomId);
                SetPrivateField(ticketClient, "roomName", realtime.RoomName, false);
                SetPrivateField(autoConnectController, "fallbackUserName", ResolveFallbackUserName(auth, realtime));

                if (disconnectBeforeConnect && wsClient != null && wsClient.IsConnected)
                {
                    wsClient.Disconnect("manual_connect_from_realtime_room");
                    await Task.Delay(250);
                }

                Debug.Log("[DedicatedConnectGameServerFromRealtimeRoomButton] Manual connect started | roomId=" +
                          SafeForLog(realtime.RoomId) + " | roomName=" + SafeForLog(realtime.RoomName) +
                          " | authUser=" + SafeForLog(auth.DisplayName) +
                          " | realtimeUser=" + SafeForLog(realtime.UserName));

                bool ok = await autoConnectController.RunAutoTicketConnectAndAuthAsync();

                hasConnectedSuccessfully = ok;

                Debug.Log("[DedicatedConnectGameServerFromRealtimeRoomButton] Manual connect finished | result=" +
                          ok + " | roomId=" + SafeForLog(realtime.RoomId) +
                          " | authUser=" + SafeForLog(auth.DisplayName));

                return ok;
            }
            catch (Exception ex)
            {
                Debug.LogError("[DedicatedConnectGameServerFromRealtimeRoomButton] Manual connect exception | " + ex.Message);
                return false;
            }
            finally
            {
                isConnecting = false;
                RefreshButtonState();
            }
        }

        //* This function reads realtime room/user values from RealtimeWebSocketG7RoomLobbyTestController.
        private RealtimeRoomSnapshot ReadRealtimeSnapshot()
        {
            RealtimeRoomSnapshot snapshot = new RealtimeRoomSnapshot();

            if (realtimeRoomController == null)
            {
                return snapshot;
            }

            snapshot.RoomId = ReadStringMember(realtimeRoomController, "CurrentRoomId");
            snapshot.RoomName = ReadStringMember(realtimeRoomController, "CurrentRoomName");
            snapshot.UserId = ReadStringMember(realtimeRoomController, "CurrentUserId");
            snapshot.UserName = ReadStringMember(realtimeRoomController, "CurrentUserName");

            snapshot.IsJoinedRoom = ReadBoolMember(realtimeRoomController, "IsJoinedRoom");
            snapshot.IsRealtimeReady = ReadBoolMember(realtimeRoomController, "IsRealtimeReadyState");

            if (string.IsNullOrWhiteSpace(snapshot.RoomName)) snapshot.RoomName = snapshot.RoomId;

            return snapshot;
        }

        //* This function reads AuthManager.CurrentUser.
        private AuthUserSnapshot ReadAuthUserSnapshot()
        {
            AuthUserSnapshot snapshot = new AuthUserSnapshot();

            try
            {
                AuthManager authManager = AuthManager.Instance;
                if (authManager == null) return snapshot;

                object currentUser = authManager.CurrentUser;
                if (currentUser == null) return snapshot;

                string id = ReadStringMember(currentUser, "id");
                string emailOrUsername = ReadStringMember(currentUser, "emailOrUsername");

                snapshot.userId = id;
                snapshot.emailOrUsername = emailOrUsername;

                if (!string.IsNullOrWhiteSpace(id))
                {
                    snapshot.UserKey = id.Trim();
                }
                else if (!string.IsNullOrWhiteSpace(emailOrUsername))
                {
                    snapshot.UserKey = emailOrUsername.Trim();
                }

                if (!string.IsNullOrWhiteSpace(emailOrUsername))
                {
                    snapshot.DisplayName = emailOrUsername.Trim();
                }
                else
                {
                    snapshot.DisplayName = snapshot.UserKey;
                }

                snapshot.IsReady = !string.IsNullOrWhiteSpace(snapshot.UserKey);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DedicatedConnectGameServerFromRealtimeRoomButton] Could not read AuthManager.CurrentUser | " + ex.Message);
            }

            return snapshot;
        }

        //* This function checks that AuthManager user and G7 realtime user are the same player.
        private bool IsAuthRealtimeSameUser(AuthUserSnapshot auth, RealtimeRoomSnapshot realtime)
        {
            if (!auth.IsReady) return false;

            string authKey = Normalize(auth.UserKey);
            string authName = Normalize(auth.DisplayName);
            string authEmail = Normalize(auth.emailOrUsername);
            string authId = Normalize(auth.userId);

            string realtimeId = Normalize(realtime.UserId);
            string realtimeName = Normalize(realtime.UserName);

            if (string.IsNullOrWhiteSpace(realtimeId) && string.IsNullOrWhiteSpace(realtimeName))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(realtimeId) && EqualsText(authKey, realtimeId)) return true;
            if (!string.IsNullOrWhiteSpace(realtimeId) && EqualsText(authId, realtimeId)) return true;

            if (!string.IsNullOrWhiteSpace(realtimeName) && EqualsText(authName, realtimeName)) return true;
            if (!string.IsNullOrWhiteSpace(realtimeName) && EqualsText(authEmail, realtimeName)) return true;

            if (!string.IsNullOrWhiteSpace(realtimeName) &&
                !string.IsNullOrWhiteSpace(authEmail) &&
                realtimeName.IndexOf(SafeEmailPrefix(authEmail), StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        //* This function resolves display name for dedicated auth.
        private string ResolveFallbackUserName(AuthUserSnapshot auth, RealtimeRoomSnapshot realtime)
        {
            if (!string.IsNullOrWhiteSpace(realtime.UserName)) return realtime.UserName.Trim();
            if (!string.IsNullOrWhiteSpace(auth.DisplayName)) return auth.DisplayName.Trim();
            if (!string.IsNullOrWhiteSpace(auth.UserKey)) return auth.UserKey.Trim();
            return "dedicated_player";
        }

        //* This function disables AutoConnect start flow without changing the original file.
        private void ApplyAutoConnectSafety()
        {
            if (autoConnectController == null) return;

            if (forceAutoRunOnStartOff)
            {
                SetPrivateField(autoConnectController, "autoRunOnStart", false);
                Log("AutoConnect autoRunOnStart forced OFF.");
            }

            if (forceRetrySettingsOn)
            {
                SetPrivateField(autoConnectController, "waitForAccessToken", true);
                SetPrivateField(autoConnectController, "waitForAccessTokenSeconds", 60f);
                SetPrivateField(autoConnectController, "retryUntilAuthenticated", true, false);
                SetPrivateField(autoConnectController, "maxAutoFlowSeconds", 90f, false);
                SetPrivateField(autoConnectController, "retryDelaySeconds", 2f, false);
                SetPrivateField(autoConnectController, "maxTicketAttempts", 30, false);
                SetPrivateField(autoConnectController, "retryOnUnauthorizedTicket", true, false);
                Log("AutoConnect retry settings forced ON.");
            }
        }

        //* This function refreshes button interactable state.
        private void RefreshButtonState()
        {
            if (connectButton == null) return;

            EnsureReferences();

            RealtimeRoomSnapshot realtime = ReadRealtimeSnapshot();
            AuthUserSnapshot auth = ReadAuthUserSnapshot();

            bool connected = wsClient != null && wsClient.IsConnected && wsClient.IsAuthenticated;
            bool tokenReady = !requireAccessToken || !string.IsNullOrWhiteSpace(SafeAccessToken());

            bool ok =
                !isConnecting &&
                tokenReady &&
                auth.IsReady &&
                (!requireRealtimeReady || realtime.IsRealtimeReady) &&
                (!requireJoinedRoom || realtime.IsJoinedRoom) &&
                !string.IsNullOrWhiteSpace(realtime.RoomId);

            if (blockIfAuthAndRealtimeMismatch && ok)
            {
                ok = IsAuthRealtimeSameUser(auth, realtime);
            }

            if (keepButtonDisabledAfterSuccess && (connected || hasConnectedSuccessfully))
            {
                ok = false;
            }

            connectButton.interactable = ok;
        }

        //* This function prints current state to log.
        private void PrintCurrentState(string source)
        {
            RealtimeRoomSnapshot realtime = ReadRealtimeSnapshot();
            AuthUserSnapshot auth = ReadAuthUserSnapshot();

            Debug.Log("[DedicatedConnectGameServerFromRealtimeRoomButton] State | source=" + source +
                      " | authUser=" + SafeForLog(auth.DisplayName) +
                      " | authKey=" + SafeForLog(auth.UserKey) +
                      " | realtimeReady=" + realtime.IsRealtimeReady +
                      " | joined=" + realtime.IsJoinedRoom +
                      " | realtimeUserId=" + SafeForLog(realtime.UserId) +
                      " | realtimeUserName=" + SafeForLog(realtime.UserName) +
                      " | roomId=" + SafeForLog(realtime.RoomId) +
                      " | roomName=" + SafeForLog(realtime.RoomName));
        }

        //* This function prints state when connect is requested.
        private void PrintStateForConnect(RealtimeRoomSnapshot realtime, AuthUserSnapshot auth, string accessToken)
        {
            Debug.Log("[DedicatedConnectGameServerFromRealtimeRoomButton] Connect requested | authUser=" +
                      SafeForLog(auth.DisplayName) + " | authKey=" + SafeForLog(auth.UserKey) +
                      " | realtimeReady=" + realtime.IsRealtimeReady +
                      " | joined=" + realtime.IsJoinedRoom +
                      " | realtimeUserId=" + SafeForLog(realtime.UserId) +
                      " | realtimeUserName=" + SafeForLog(realtime.UserName) +
                      " | roomId=" + SafeForLog(realtime.RoomId) +
                      " | roomName=" + SafeForLog(realtime.RoomName) +
                      " | tokenHash=" + HashForLog(accessToken));
        }

        //* This function reads string property or field by reflection.
        private string ReadStringMember(object target, string memberName)
        {
            if (target == null || string.IsNullOrWhiteSpace(memberName)) return string.Empty;

            Type type = target.GetType();

            PropertyInfo property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.PropertyType == typeof(string))
            {
                return property.GetValue(target, null) as string;
            }

            FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(string))
            {
                return field.GetValue(target) as string;
            }

            return string.Empty;
        }

        //* This function reads bool property or field by reflection.
        private bool ReadBoolMember(object target, string memberName)
        {
            if (target == null || string.IsNullOrWhiteSpace(memberName)) return false;

            Type type = target.GetType();

            PropertyInfo property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.PropertyType == typeof(bool))
            {
                return (bool)property.GetValue(target, null);
            }

            FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(bool))
            {
                return (bool)field.GetValue(target);
            }

            return false;
        }

        //* This function sets private fields by reflection.
        private void SetPrivateField(object target, string fieldName, object value)
        {
            SetPrivateField(target, fieldName, value, true);
        }

        private void SetPrivateField(object target, string fieldName, object value, bool warnIfMissing)
        {
            if (target == null || string.IsNullOrWhiteSpace(fieldName)) return;

            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (field == null)
            {
                if (warnIfMissing)
                {
                    Debug.LogWarning("[DedicatedConnectGameServerFromRealtimeRoomButton] Field not found | type=" +
                                     target.GetType().Name + " | field=" + fieldName);
                }

                return;
            }

            try
            {
                field.SetValue(target, value);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DedicatedConnectGameServerFromRealtimeRoomButton] Could not set field | field=" +
                                 fieldName + " | error=" + ex.Message);
            }
        }

        //* This function finds a MonoBehaviour by class name.
        private UnityEngine.Object FindObjectOfTypeByName(string typeName)
        {
            MonoBehaviour[] behaviours = FindObjectsOfType<MonoBehaviour>(true);

            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null) continue;

                if (behaviour.GetType().Name == typeName)
                {
                    return behaviour;
                }
            }

            return null;
        }

        //* This function reads access token safely.
        private string SafeAccessToken()
        {
            try
            {
                return SecureTokenStorage.GetAccessToken();
            }
            catch
            {
                return string.Empty;
            }
        }

        //* This function returns a safe email prefix.
        private string SafeEmailPrefix(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return string.Empty;

            int atIndex = email.IndexOf('@');
            if (atIndex > 0) return email.Substring(0, atIndex);

            return email;
        }

        //* This function normalizes text.
        private string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        //* This function compares text safely.
        private bool EqualsText(string a, string b)
        {
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }

        //* This function hides empty log values.
        private string SafeForLog(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<empty>" : value.Trim();
        }

        //* This function returns a token hash without printing token.
        private string HashForLog(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "empty";

            unchecked
            {
                int hash = 17;

                for (int i = 0; i < value.Length; i++)
                {
                    hash = hash * 31 + value[i];
                }

                return hash.ToString("X8");
            }
        }

        //* This function removes the button listener.
        private void OnDestroy()
        {
            if (connectButton != null)
            {
                connectButton.onClick.RemoveListener(OnConnectClicked);
            }
        }

        //* This function prints logs.
        private void Log(string message)
        {
            if (!verboseLogs) return;

            Debug.Log("[DedicatedConnectGameServerFromRealtimeRoomButton] " + message);
        }

        private struct RealtimeRoomSnapshot
        {
            public bool IsRealtimeReady;
            public bool IsJoinedRoom;
            public string RoomId;
            public string RoomName;
            public string UserId;
            public string UserName;
        }

        private struct AuthUserSnapshot
        {
            public bool IsReady;
            public string UserKey;
            public string DisplayName;
            public string userId;
            public string emailOrUsername;
        }

        /*
        توضیح مکتوب فایل:
        این فایل فقط رپر دکمه اتصال گیم سرور است و هیچ فایل قبلی را تغییر نمی دهد.
        این نسخه برای ترتیب واقعی پروژه ساخته شده است:
        اول Login کامل می شود، بعد Connect To Realtime، بعد انتخاب Room از لیست، بعد Connect Game Server.
        این رپر دیگر Login_Init را صدا نمی زند، چون همان باعث برگشت به یوزر اتو لاگین قدیمی می شد.
        رپر از RealtimeWebSocketG7RoomLobbyTestController روم و یوزر فعلی ریل تایم را می خواند.
        اگر یوزر AuthManager و یوزر ریل تایم یکی نباشند، اتصال را بلاک می کند تا یوزر اشتباه وارد ددیکیتد نشود.
        */
    }
}
