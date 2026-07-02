using System;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using Network_A.Auth;
using UnityEngine;
using UnityEngine.UI;

namespace Network_A.DedicatedGameServer.Client
{
    public class DedicatedConnectGameServerAfterLatestLoginButton : MonoBehaviour
    {
        [Header("Connect Button")]
        [SerializeField] private Button connectButton;

        [Header("Optional Login/Register Capture")]
        [SerializeField] private Button loginButton;
        [SerializeField] private Button registerButton;
        [SerializeField] private UnityEngine.Object loginUserNameInput;
        [SerializeField] private UnityEngine.Object registerUserNameInput;

        [Header("References")]
        [SerializeField] private DedicatedGameServerAutoConnectController autoConnectController;
        [SerializeField] private DedicatedGameServerWsClient wsClient;

        [Header("Latest Login Rules")]
        [SerializeField] private bool captureLoginButtonClicks = true;
        [SerializeField] private bool readInputAgainOnConnect = true;
        [SerializeField] private bool forceLoginInitBeforeConnect = true;
        [SerializeField] private bool blockIfCurrentUserDoesNotMatchLatestLogin = true;
        [SerializeField] private bool allowContainsMatch = true;
        [SerializeField] private bool ignoreCase = true;
        [SerializeField] private float maxWaitForLatestLoginSeconds = 30f;
        [SerializeField] private float pollIntervalSeconds = 0.25f;
        [SerializeField] private float stableMatchedUserSeconds = 0.75f;

        [Header("Connect Rules")]
        [SerializeField] private bool requireLoggedInUser = true;
        [SerializeField] private bool requireAccessToken = true;
        [SerializeField] private bool disableButtonWhileConnecting = true;
        [SerializeField] private bool keepButtonDisabledAfterSuccess = true;
        [SerializeField] private bool disconnectBeforeConnect = true;

        [Header("Safety")]
        [SerializeField] private bool forceAutoRunOnStartOff = true;
        [SerializeField] private bool forceRetrySettingsOn = true;

        [Header("Debug")]
        [SerializeField] private bool verboseLogs = true;
        [SerializeField] private bool logPolling = false;

        private bool isConnecting;
        private bool hasConnectedSuccessfully;
        private string latestLoginUserNameOrEmail = "";
        private string tokenBeforeLatestLogin = "";
        private float latestLoginClickedAt;

        //* This function binds buttons and prevents automatic dedicated connect.
        private void Awake()
        {
            if (connectButton == null)
            {
                connectButton = GetComponent<Button>();
            }

            EnsureReferences();
            ApplyAutoConnectSafety();
            BindButtons();
            RefreshButtonState();

            Log("Latest-login connect button ready.");
        }

        //* This function keeps the connect button state aligned with auth state.
        private void Update()
        {
            RefreshButtonState();
        }

        //* This function finds required components.
        private void EnsureReferences()
        {
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

        //* This function binds connect, login and register buttons.
        private void BindButtons()
        {
            if (connectButton != null)
            {
                connectButton.onClick.RemoveListener(OnConnectClicked);
                connectButton.onClick.AddListener(OnConnectClicked);
            }

            if (!captureLoginButtonClicks) return;

            if (loginButton != null)
            {
                loginButton.onClick.RemoveListener(OnLoginClicked);
                loginButton.onClick.AddListener(OnLoginClicked);
            }

            if (registerButton != null)
            {
                registerButton.onClick.RemoveListener(OnRegisterClicked);
                registerButton.onClick.AddListener(OnRegisterClicked);
            }
        }

        //* This function captures the username/email that the user is trying to login with.
        private void OnLoginClicked()
        {
            CaptureLatestLoginUser("login_button", ReadTextFromObject(loginUserNameInput));
        }

        //* This function captures the username/email that the user is trying to register with.
        private void OnRegisterClicked()
        {
            CaptureLatestLoginUser("register_button", ReadTextFromObject(registerUserNameInput));
        }

        //* This function stores the latest manually requested login user.
        private void CaptureLatestLoginUser(string reason, string userNameOrEmail)
        {
            latestLoginUserNameOrEmail = Normalize(userNameOrEmail);
            tokenBeforeLatestLogin = SafeAccessToken();
            latestLoginClickedAt = Time.realtimeSinceStartup;

            Debug.Log("[DedicatedConnectGameServerAfterLatestLoginButton] Latest login captured | reason=" +
                      reason + " | latestLogin=" + SafeForLog(latestLoginUserNameOrEmail) +
                      " | tokenBeforeHash=" + HashForLog(tokenBeforeLatestLogin));
        }

        //* This context menu connects using the latest login user.
        [ContextMenu("Connect Game Server Using Latest Login")]
        public async void Btn_ConnectUsingLatestLogin()
        {
            await ConnectUsingLatestLoginAsync();
        }

        //* This function is called by the UI button.
        private async void OnConnectClicked()
        {
            await ConnectUsingLatestLoginAsync();
        }

        //* This function performs manual dedicated connect only if CurrentUser matches the latest login user.
        public async Task<bool> ConnectUsingLatestLoginAsync()
        {
            if (isConnecting)
            {
                Log("Connect ignored because connect is already running.");
                return false;
            }

            EnsureReferences();
            ApplyAutoConnectSafety();

            string expectedUser = ResolveExpectedUserForConnect();

            Debug.Log("[DedicatedConnectGameServerAfterLatestLoginButton] Connect requested | expectedLatestLogin=" +
                      SafeForLog(expectedUser));

            if (forceLoginInitBeforeConnect)
            {
                await ForceLoginInitAsync();
            }

            AuthUserSnapshot matchedUser = await WaitForCurrentUserAsync(expectedUser);

            if (requireLoggedInUser && !matchedUser.IsReady)
            {
                Debug.LogWarning("[DedicatedConnectGameServerAfterLatestLoginButton] Connect blocked. AuthManager.CurrentUser is not ready.");
                RefreshButtonState();
                return false;
            }

            if (requireAccessToken && string.IsNullOrWhiteSpace(SafeAccessToken()))
            {
                Debug.LogWarning("[DedicatedConnectGameServerAfterLatestLoginButton] Connect blocked. Access token is missing.");
                RefreshButtonState();
                return false;
            }

            if (blockIfCurrentUserDoesNotMatchLatestLogin &&
                !string.IsNullOrWhiteSpace(expectedUser) &&
                !MatchesExpectedUser(matchedUser, expectedUser))
            {
                Debug.LogError("[DedicatedConnectGameServerAfterLatestLoginButton] Connect blocked. CurrentUser does not match latest login. current=" +
                               SafeForLog(matchedUser.DisplayName) + " | currentKey=" + SafeForLog(matchedUser.UserKey) +
                               " | expectedLatestLogin=" + SafeForLog(expectedUser));
                RefreshButtonState();
                return false;
            }

            if (autoConnectController == null)
            {
                Debug.LogError("[DedicatedConnectGameServerAfterLatestLoginButton] DedicatedGameServerAutoConnectController is missing.");
                RefreshButtonState();
                return false;
            }

            isConnecting = true;
            RefreshButtonState();

            try
            {
                if (disconnectBeforeConnect && wsClient != null && wsClient.IsConnected)
                {
                    wsClient.Disconnect("manual_connect_after_latest_login");
                    await Task.Delay(250);
                }

                SetPrivateField(autoConnectController, "fallbackUserName", matchedUser.DisplayName);

                Debug.Log("[DedicatedConnectGameServerAfterLatestLoginButton] Manual connect started | userKey=" +
                          SafeForLog(matchedUser.UserKey) + " | displayName=" + SafeForLog(matchedUser.DisplayName) +
                          " | expectedLatestLogin=" + SafeForLog(expectedUser));

                bool ok = await autoConnectController.RunAutoTicketConnectAndAuthAsync();

                hasConnectedSuccessfully = ok;

                Debug.Log("[DedicatedConnectGameServerAfterLatestLoginButton] Manual connect finished | result=" +
                          ok + " | userKey=" + SafeForLog(matchedUser.UserKey) +
                          " | displayName=" + SafeForLog(matchedUser.DisplayName));

                return ok;
            }
            catch (Exception ex)
            {
                Debug.LogError("[DedicatedConnectGameServerAfterLatestLoginButton] Manual connect exception | " + ex.Message);
                return false;
            }
            finally
            {
                isConnecting = false;
                RefreshButtonState();
            }
        }

        //* This function resolves the expected user from latest click, current input fields or CurrentUser fallback.
        private string ResolveExpectedUserForConnect()
        {
            string expected = Normalize(latestLoginUserNameOrEmail);

            if (readInputAgainOnConnect)
            {
                string loginInput = Normalize(ReadTextFromObject(loginUserNameInput));
                string registerInput = Normalize(ReadTextFromObject(registerUserNameInput));

                if (!string.IsNullOrWhiteSpace(loginInput))
                {
                    expected = loginInput;
                }
                else if (!string.IsNullOrWhiteSpace(registerInput))
                {
                    expected = registerInput;
                }
            }

            if (!string.IsNullOrWhiteSpace(expected))
            {
                return expected;
            }

            AuthUserSnapshot snapshot = ReadAuthUserSnapshot();
            return snapshot.DisplayName;
        }

        //* This function calls AuthManager.Login_Init so CurrentUser is refreshed from the newest token.
        private async Task ForceLoginInitAsync()
        {
            try
            {
                AuthManager authManager = AuthManager.Instance;

                if (authManager == null)
                {
                    Debug.LogWarning("[DedicatedConnectGameServerAfterLatestLoginButton] AuthManager is missing. Login_Init cannot be forced.");
                    return;
                }

                AuthUserSnapshot before = ReadAuthUserSnapshot();

                Debug.Log("[DedicatedConnectGameServerAfterLatestLoginButton] Force Login_Init before connect | beforeUser=" +
                          SafeForLog(before.DisplayName) + " | beforeKey=" + SafeForLog(before.UserKey));

                await authManager.Login_Init();

                AuthUserSnapshot after = ReadAuthUserSnapshot();

                Debug.Log("[DedicatedConnectGameServerAfterLatestLoginButton] Login_Init finished before connect | afterUser=" +
                          SafeForLog(after.DisplayName) + " | afterKey=" + SafeForLog(after.UserKey));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DedicatedConnectGameServerAfterLatestLoginButton] Force Login_Init failed | " + ex.Message);
            }
        }

        //* This function waits until CurrentUser is ready and optionally matches the latest login user.
        private async Task<AuthUserSnapshot> WaitForCurrentUserAsync(string expectedUser)
        {
            float startedAt = Time.realtimeSinceStartup;
            float matchedStartedAt = -1f;
            AuthUserSnapshot lastSnapshot = ReadAuthUserSnapshot();

            while (Time.realtimeSinceStartup - startedAt <= Mathf.Max(1f, maxWaitForLatestLoginSeconds))
            {
                AuthUserSnapshot snapshot = ReadAuthUserSnapshot();
                lastSnapshot = snapshot;

                bool tokenReady = !requireAccessToken || !string.IsNullOrWhiteSpace(SafeAccessToken());
                bool userReady = !requireLoggedInUser || snapshot.IsReady;
                bool expectedEmpty = string.IsNullOrWhiteSpace(expectedUser);
                bool matched = expectedEmpty || MatchesExpectedUser(snapshot, expectedUser);

                if (logPolling)
                {
                    Log("Wait current user | userReady=" + userReady +
                        " | tokenReady=" + tokenReady +
                        " | matched=" + matched +
                        " | current=" + SafeForLog(snapshot.DisplayName) +
                        " | expected=" + SafeForLog(expectedUser));
                }

                if (tokenReady && userReady && matched)
                {
                    if (matchedStartedAt < 0f)
                    {
                        matchedStartedAt = Time.realtimeSinceStartup;

                        Debug.Log("[DedicatedConnectGameServerAfterLatestLoginButton] CurrentUser matched latest login | current=" +
                                  SafeForLog(snapshot.DisplayName) + " | expected=" + SafeForLog(expectedUser));
                    }

                    if (Time.realtimeSinceStartup - matchedStartedAt >= Mathf.Max(0f, stableMatchedUserSeconds))
                    {
                        return snapshot;
                    }
                }
                else
                {
                    matchedStartedAt = -1f;
                }

                await Task.Delay(Mathf.RoundToInt(Mathf.Max(0.1f, pollIntervalSeconds) * 1000f));
            }

            Debug.LogWarning("[DedicatedConnectGameServerAfterLatestLoginButton] Wait finished without expected match | current=" +
                             SafeForLog(lastSnapshot.DisplayName) + " | currentKey=" + SafeForLog(lastSnapshot.UserKey) +
                             " | expected=" + SafeForLog(expectedUser));

            return lastSnapshot;
        }

        //* This function checks whether CurrentUser matches expected latest login.
        private bool MatchesExpectedUser(AuthUserSnapshot snapshot, string expectedUser)
        {
            if (string.IsNullOrWhiteSpace(expectedUser))
            {
                return true;
            }

            if (!snapshot.IsReady)
            {
                return false;
            }

            string expected = Normalize(expectedUser);
            string display = Normalize(snapshot.DisplayName);
            string emailOrUsername = Normalize(snapshot.emailOrUsername);
            string userKey = Normalize(snapshot.UserKey);
            string userId = Normalize(snapshot.userId);

            StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            if (string.Equals(expected, display, comparison)) return true;
            if (string.Equals(expected, emailOrUsername, comparison)) return true;
            if (string.Equals(expected, userKey, comparison)) return true;
            if (string.Equals(expected, userId, comparison)) return true;

            if (allowContainsMatch)
            {
                if (!string.IsNullOrWhiteSpace(display) && display.IndexOf(expected, comparison) >= 0) return true;
                if (!string.IsNullOrWhiteSpace(emailOrUsername) && emailOrUsername.IndexOf(expected, comparison) >= 0) return true;
                if (!string.IsNullOrWhiteSpace(expected) && expected.IndexOf(display, comparison) >= 0) return !string.IsNullOrWhiteSpace(display);
                if (!string.IsNullOrWhiteSpace(expected) && expected.IndexOf(emailOrUsername, comparison) >= 0) return !string.IsNullOrWhiteSpace(emailOrUsername);
            }

            return false;
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
                SetPrivateField(autoConnectController, "retryUntilAuthenticated", true);
                SetPrivateField(autoConnectController, "maxAutoFlowSeconds", 90f);
                SetPrivateField(autoConnectController, "retryDelaySeconds", 2f);
                SetPrivateField(autoConnectController, "maxTicketAttempts", 30);
                SetPrivateField(autoConnectController, "retryOnUnauthorizedTicket", true);

                Log("AutoConnect retry settings forced ON.");
            }
        }

        //* This function refreshes interactable state.
        private void RefreshButtonState()
        {
            if (connectButton == null) return;

            bool connected = wsClient != null && wsClient.IsConnected && wsClient.IsAuthenticated;
            bool authReady = !requireLoggedInUser || ReadAuthUserSnapshot().IsReady;
            bool tokenReady = !requireAccessToken || !string.IsNullOrWhiteSpace(SafeAccessToken());

            bool interactable = !isConnecting && authReady && tokenReady;

            if (keepButtonDisabledAfterSuccess && (connected || hasConnectedSuccessfully))
            {
                interactable = false;
            }

            if (disableButtonWhileConnecting)
            {
                connectButton.interactable = interactable;
            }
        }

        //* This function reads AuthManager.CurrentUser safely.
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
                Debug.LogWarning("[DedicatedConnectGameServerAfterLatestLoginButton] Could not read AuthManager.CurrentUser | " + ex.Message);
            }

            return snapshot;
        }

        //* This function reads a string field or property by reflection.
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

        //* This function reads text from InputField, TMP_InputField, Text or TMP_Text by reflection.
        private string ReadTextFromObject(UnityEngine.Object source)
        {
            if (source == null) return string.Empty;

            try
            {
                Type type = source.GetType();

                PropertyInfo textProperty = type.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (textProperty != null && textProperty.PropertyType == typeof(string))
                {
                    return textProperty.GetValue(source, null) as string;
                }

                FieldInfo textField = type.GetField("text", BindingFlags.Instance | BindingFlags.Public);
                if (textField != null && textField.FieldType == typeof(string))
                {
                    return textField.GetValue(source) as string;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DedicatedConnectGameServerAfterLatestLoginButton] Could not read text from object | type=" +
                                 source.GetType().Name + " | error=" + ex.Message);
            }

            return string.Empty;
        }

        //* This function sets a private field by reflection.
        private void SetPrivateField(object target, string fieldName, object value)
        {
            if (target == null || string.IsNullOrWhiteSpace(fieldName)) return;

            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (field == null) return;

            try
            {
                field.SetValue(target, value);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DedicatedConnectGameServerAfterLatestLoginButton] Could not set field | field=" +
                                 fieldName + " | error=" + ex.Message);
            }
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

        //* This function normalizes user text.
        private string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        //* This function hides empty values in logs.
        private string SafeForLog(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<empty>" : value.Trim();
        }

        //* This function returns a short hash for logs without printing the token.
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

        //* This function removes button listeners.
        private void OnDestroy()
        {
            if (connectButton != null)
            {
                connectButton.onClick.RemoveListener(OnConnectClicked);
            }

            if (loginButton != null)
            {
                loginButton.onClick.RemoveListener(OnLoginClicked);
            }

            if (registerButton != null)
            {
                registerButton.onClick.RemoveListener(OnRegisterClicked);
            }
        }

        //* This function prints wrapper logs.
        private void Log(string message)
        {
            if (!verboseLogs) return;

            Debug.Log("[DedicatedConnectGameServerAfterLatestLoginButton] " + message);
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
        این فایل فقط رپر دکمه اتصال است و هیچ فایل قبلی را تغییر نمی دهد.
        مشکل این بود که Auto Login در شروع بیلد، یوزر قدیمی را آماده می کرد.
        بعد کاربر دستی با یوزر جدید لاگین می کرد، اما دکمه اتصال هنوز ممکن بود CurrentUser قدیمی را بخواند.
        این رپر موقع کلیک اتصال، Login_Init را دوباره اجرا می کند و صبر می کند CurrentUser با آخرین نام کاربری وارد شده یکی شود.
        اگر CurrentUser با آخرین لاگین یکی نشود، اتصال به گیم سرور را بلاک می کند تا یوزر اشتباه وارد Dedicated نشود.
        */
    }
}
