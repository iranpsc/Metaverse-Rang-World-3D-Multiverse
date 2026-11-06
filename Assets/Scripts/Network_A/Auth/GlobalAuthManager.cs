using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Bootstrap;
using Network_A.Core;
using Network_A.UI;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Network_A.Auth
{
    [DefaultExecutionOrder(-8000)]
    public sealed class GlobalAuthManager : MonoBehaviour
    {
        #region وضعیت‌ها و تنظیمات

        public enum AuthState
        {
            SignedOut,
            LoggingIn,
            FetchingUser,
            SigningOut,
            Authenticated,
            WaitingForNetwork,
            ManualLoginRequired
        }

        private enum AuthFailureReason
        {
            None,
            NetworkUnavailable,
            ServerUnavailable,
            Timeout,
            AccessTokenExpired,
            RefreshTokenExpired,
            RefreshTokenRevoked,
            InvalidToken,
            Unauthorized,
            InvalidResponse,
            Unknown
        }

        private const string RememberMeKey = "Network_A_GlobalAuth_RememberMe";
        private const string LoginProgressMessageId = "GLOBAL_AUTH_LOGIN_PROGRESS";
        private const string LoginResultMessageId = "GLOBAL_AUTH_LOGIN_RESULT";
        private const string RefreshProgressMessageId = "GLOBAL_AUTH_REFRESH_PROGRESS";
        private const string LogoutProgressMessageId = "GLOBAL_AUTH_LOGOUT_PROGRESS";
        private const string LogoutResultMessageId = "GLOBAL_AUTH_LOGOUT_RESULT";
        private const string AuthErrorMessageId = "GLOBAL_AUTH_ERROR";

        public static GlobalAuthManager Instance { get; private set; }
        public static AuthState CurrentAuthState { get; private set; } = AuthState.SignedOut;
        public static event Action<AuthState> OnAuthStateChanged;
        public static event Action<AuthUserDto> OnLoginReady;
        public static event Action OnTokensRefreshed;
        public static event Action<bool> OnRememberMeChanged;
        public static event Action OnLogoutStarted;
        public static event Func<CancellationToken, Task> OnLogoutCleanupRequested;
        public static event Action OnLogoutCompleted;

        [Header("Lifetime")]
        [SerializeField] private bool runInBackground = true;

        [Header("Remember Me")]
        [SerializeField] private bool rememberMeDefaultValue;

        [Header("Login UI")]
        [SerializeField] private GameObject pnl_Login;
        [SerializeField] private TMP_InputField in_Log_UserName;
        [SerializeField] private TMP_InputField in_Log_Pass;
        [SerializeField] private Toggle tgl_RememberMe;
        [SerializeField] private Button btn_Log;

        [Header("Validation")]
        [SerializeField, Min(1)] private int minUsernameLength = 7;
        [SerializeField, Min(1)] private int minPasswordLength = 7;

        [Header("Scenes")]
        [SerializeField] private string loginSceneName = "Login 1";
        [SerializeField] private bool loadAvatarSelectorAfterLoginInit = true;
        [SerializeField] private string avatarSelectorSceneName = "Avatar Selector";

        [Header("Messages")]
        [SerializeField] private string loginProgressMessage = "در حال ورود...";
        [SerializeField] private string loginInitProgressMessage = "در حال دریافت اطلاعات کاربر...";
        [SerializeField] private string refreshProgressMessage = "نشست کاربری در حال تمدید است...";
        [SerializeField] private string loginSuccessMessage = "ورود با موفقیت انجام شد.";
        [SerializeField, Min(0.1f)] private float loginSuccessMessageDurationSeconds = 1.5f;
        [SerializeField] private string noNetworkMessage = "ارتباط با سرور برقرار نیست.";
        [SerializeField] private string manualLoginMessage = "لطفاً نام کاربری و رمز عبور را وارد کنید.";
        [SerializeField] private string expiredSessionMessage = "نشست شما منقضی شده است. لطفاً دوباره وارد شوید.";

        [Header("Logout")]
        [SerializeField] private string logoutProgressMessage = "در حال خروج از حساب کاربری...";
        [SerializeField] private string logoutSuccessMessage = "از حساب کاربری خارج شدید.";
        [SerializeField, Min(0.1f)] private float logoutSuccessMessageDurationSeconds = 2f;
        [SerializeField, Min(1f)] private float logoutCleanupTimeoutSeconds = 15f;
        [SerializeField] private bool loadLoginSceneAfterLogout = true;

        [Header("Automatic Login Recovery")]
        [SerializeField, Min(0.1f)] private float transientLoginRetryInitialDelaySeconds = 0.75f;
        [SerializeField, Min(0.5f)] private float transientLoginRetryMaximumDelaySeconds = 4f;
        [SerializeField, Min(5f)] private float transientLoginRetryWindowSeconds = 180f;

        [HideInInspector] public bool isLogin;
        [HideInInspector] public AuthUserDto CurrentUser;

        public bool RememberMeEnabled => rememberMeEnabled;
        public bool IsAuthOperationRunning => authOperationRunning || logoutRunning;
        public bool IsLogoutRunning => logoutRunning;

        private CancellationTokenSource lifecycleCts;
        private CancellationTokenSource transientLoginRetryCts;
        private bool rememberMeEnabled;
        private bool authOperationRunning;
        private bool logoutRunning;
        private bool transientLoginRetryRunning;
        private int transientLoginRetryAttempt;
        private AuthFailureReason lastRefreshFailureReason = AuthFailureReason.None;
        private string lastRefreshFailureDetails = string.Empty;

        #endregion

        #region چرخه حیات

        //* این تابع نمونه اصلی مدیر ورود، تنظیمات اولیه، مقدار ذخیره‌شده و روند تمدید نشست را آماده می‌کند.
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                NetworkFileLogger.Warning("GLOBAL_AUTH", "Duplicate GlobalAuthManager destroyed.");
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            lifecycleCts = new CancellationTokenSource();
            rememberMeEnabled = PlayerPrefs.HasKey(RememberMeKey) ? PlayerPrefs.GetInt(RememberMeKey, 0) == 1 : rememberMeDefaultValue;

            if (runInBackground) Application.runInBackground = true;

            if (btn_Log != null)
            {
                btn_Log.onClick.RemoveListener(Login);
                btn_Log.onClick.AddListener(Login);
            }

            if (tgl_RememberMe != null) tgl_RememberMe.SetIsOnWithoutNotify(rememberMeEnabled);
            if (pnl_Login != null) pnl_Login.SetActive(false);
            if (!HasValidLoginUi()) NetworkFileLogger.Error("GLOBAL_AUTH_UI", "مراجع رابط ورود سراسری در بازرس کامل تنظیم نشده‌اند.");

            GlobalMessageManager.EnsureInstance();

            AuthRefreshManager.Configure(RefreshInternalAsync);
            AuthRefreshManager.OnRequireLoginUI = HandleRefreshRequireLogin;
            StartupNetworkSceneRouter.OnNetworkStateChanged += HandleNetworkStateChanged;

            SetAuthState(AuthState.SignedOut, "initialized");
            NetworkFileLogger.Info("GLOBAL_AUTH", "Awake ready | rememberMe=" + rememberMeEnabled + " | access=" + !string.IsNullOrEmpty(SecureTokenStorage.GetAccessToken()) + " | refresh=" + !string.IsNullOrEmpty(SecureTokenStorage.GetRefreshToken()) + " | transport=" + ServerConfig.CurrentTransportKind + " | endpoint=" + ServerConfig.CurrentEndpoint);
        }

        //* این تابع در آغاز صحنه، وضعیت شبکه را بررسی می‌کند و بر اساس مقدار ذخیره‌شده ورود خودکار یا ورود دستی را آغاز می‌کند.
        private async void Start()
        {
            if (Instance != this) return;

            try
            {
                bool networkReady = StartupNetworkSceneRouter.Instance != null && StartupNetworkSceneRouter.IsOnline;

                if (!networkReady)
                {
                    SetAuthState(AuthState.WaitingForNetwork, "startup_network_not_ready");

                    if (StartupNetworkSceneRouter.Instance == null)
                    {
                        GlobalMessageManager.ShowWarning(AuthErrorMessageId, "ارتباط شبکه", noNetworkMessage,
                     "StartupNetworkSceneRouter is not available.", 0f, GlobalMessageManager.MessageSource.Authentication);
                    }

                    return;
                }

                if (rememberMeEnabled)
                {
                    await Login_Init();
                    return;
                }

                RequestManualLogin("remember_me_disabled");
            }
            catch (OperationCanceledException)
            {
                NetworkFileLogger.Warning("GLOBAL_AUTH_START", "Startup auth flow cancelled.");
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("GLOBAL_AUTH_START", ex);
                GlobalMessageManager.ShowError(AuthErrorMessageId, "خطای ورود", "راه‌اندازی ورود انجام نشد.", ex.ToString(), 0f, true, GlobalMessageManager.MessageSource.Authentication);
            }
        }

        //* این تابع هنگام نابودی مدیر ورود، شنونده‌ها، رویدادها و منابع لغو عملیات را آزاد می‌کند.
        private void OnDestroy()
        {
            if (Instance != this) return;

            StartupNetworkSceneRouter.OnNetworkStateChanged -= HandleNetworkStateChanged;
            if (btn_Log != null) btn_Log.onClick.RemoveListener(Login);

            if (AuthRefreshManager.OnRequireLoginUI == HandleRefreshRequireLogin) AuthRefreshManager.OnRequireLoginUI = null;
            AuthRefreshManager.Configure(null);
            CancelTransientLoginRetry("manager_destroyed");

            if (lifecycleCts != null)
            {
                if (!lifecycleCts.IsCancellationRequested) lifecycleCts.Cancel();
                lifecycleCts.Dispose();
                lifecycleCts = null;
            }

            Instance = null;
        }

        #endregion

        #region ورود و دریافت کاربر

        //* این تابع اطلاعات رابط ورود سراسری را می‌خواند و عملیات ورود دستی را اجرا می‌کند.
        public async void Login()
        {
            if (logoutRunning) return;

            if (!HasValidLoginUi())
            {
                GlobalMessageManager.ShowError(AuthErrorMessageId, "خطای رابط ورود", "مراجع رابط ورود سراسری در بازرس کامل تنظیم نشده‌اند.", string.Empty, 0f, true, GlobalMessageManager.MessageSource.Authentication);
                return;
            }

            if (authOperationRunning) return;

            CancelTransientLoginRetry("manual_login_button");
            btn_Log.interactable = false;

            try
            {
                await LoginAsync(in_Log_UserName.text, in_Log_Pass.text, tgl_RememberMe != null && tgl_RememberMe.isOn);
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("GLOBAL_AUTH_LOGIN_BUTTON", ex);
                RequestManualLogin(ex.Message);
                GlobalMessageManager.ShowError(AuthErrorMessageId, "خطای ورود", "ورود انجام نشد. دوباره تلاش کنید.", ex.ToString(), 0f, false, GlobalMessageManager.MessageSource.Authentication);
            }
            finally
            {
                ApplyLoginUiState(CurrentAuthState);
            }
        }

        //* این تابع ورود دستی را با اطلاعات دریافت‌شده از رابط همان صحنه انجام می‌دهد و پس از موفقیت اطلاعات کاربر را دریافت می‌کند.
        public async Task<bool> LoginAsync(string username, string password, bool rememberMe)
        {
            if (authOperationRunning || logoutRunning) return false;

            CancelTransientLoginRetry("manual_login_started");
            username = (username ?? string.Empty).Trim();
            password = password ?? string.Empty;

            if (username.Length < minUsernameLength)
            {
                GlobalMessageManager.ShowWarning(AuthErrorMessageId, "اطلاعات ورود", "نام کاربری باید حداقل " + minUsernameLength + " کاراکتر باشد.", string.Empty, 4f, GlobalMessageManager.MessageSource.Authentication);
                return false;
            }

            if (password.Length < minPasswordLength)
            {
                GlobalMessageManager.ShowWarning(AuthErrorMessageId, "اطلاعات ورود", "رمز عبور باید حداقل " + minPasswordLength + " کاراکتر باشد.", string.Empty, 4f, GlobalMessageManager.MessageSource.Authentication);
                return false;
            }

            bool loginSucceeded = false;

            try
            {
                bool networkReady = StartupNetworkSceneRouter.Instance != null && StartupNetworkSceneRouter.IsOnline;

                if (!networkReady)
                {
                    SetAuthState(AuthState.WaitingForNetwork, "manual_login_network_not_ready");
                    GlobalMessageManager.ShowWarning(AuthErrorMessageId, "ارتباط شبکه", noNetworkMessage, string.Empty, 0f, GlobalMessageManager.MessageSource.Authentication);
                    return false;
                }

                authOperationRunning = true;
                SetAuthState(AuthState.LoggingIn, "manual_login_started");
                GlobalMessageManager.ShowInfo(LoginProgressMessageId, "ورود به حساب", loginProgressMessage, string.Empty, 0f, GlobalMessageManager.MessageSource.Authentication);

                ApiResult<AuthResponseDto> result = await AuthService.LoginAsync(username, password, lifecycleCts.Token);
                bool resultIsValid = result != null && result.IsSuccess && result.Data != null && result.Data.success && !string.IsNullOrWhiteSpace(result.Data.accessToken);

                if (!resultIsValid)
                {
                    string error = result != null ? (result.ErrorMessage ?? string.Empty) + " | " + (result.RawBody ?? string.Empty) : "Login result is null.";
                    AuthFailureReason reason = ClassifyFailure(error, result != null ? result.StatusCode : 0, result == null || result.IsNetworkError);
                    if (reason == AuthFailureReason.NetworkUnavailable || reason == AuthFailureReason.ServerUnavailable || reason == AuthFailureReason.Timeout) SetAuthState(AuthState.WaitingForNetwork, error);
                    else SetAuthState(AuthState.ManualLoginRequired, error);

                    string userMessage = reason == AuthFailureReason.NetworkUnavailable || reason == AuthFailureReason.ServerUnavailable || reason == AuthFailureReason.Timeout ? noNetworkMessage : error.IndexOf("INVALID_CREDENTIALS", StringComparison.OrdinalIgnoreCase) >= 0 ? "نام کاربری یا رمز عبور اشتباه است." : "ورود انجام نشد. دوباره تلاش کنید.";
                    if (StartupNetworkSceneRouter.Instance != null && StartupNetworkSceneRouter.IsOnline) GlobalMessageManager.ClearNetworkMessages();
                    GlobalMessageManager.ShowError(AuthErrorMessageId, "ورود ناموفق", userMessage, "status=" + (result != null ? result.StatusCode : 0) + " | error=" + error, 0f, false, GlobalMessageManager.MessageSource.Authentication);
                    NetworkFileLogger.Auth("GLOBAL_LOGIN_FAILED", false, error, username, !string.IsNullOrEmpty(SecureTokenStorage.GetAccessToken()), !string.IsNullOrEmpty(SecureTokenStorage.GetRefreshToken()));
                    return false;
                }

                SetRememberMe(rememberMe);
                loginSucceeded = true;
                NetworkFileLogger.Auth("GLOBAL_LOGIN_SUCCESS", true, result.Data.message, username, !string.IsNullOrEmpty(SecureTokenStorage.GetAccessToken()), !string.IsNullOrEmpty(SecureTokenStorage.GetRefreshToken()));
            }
            catch (OperationCanceledException)
            {
                NetworkFileLogger.Warning("GLOBAL_AUTH_LOGIN", "Manual login cancelled.");
            }
            catch (Exception ex)
            {
                RequestManualLogin(ex.Message);
                NetworkFileLogger.Exception("GLOBAL_AUTH_LOGIN", ex);
                if (StartupNetworkSceneRouter.Instance != null && StartupNetworkSceneRouter.IsOnline) GlobalMessageManager.ClearNetworkMessages();
                GlobalMessageManager.ShowError(AuthErrorMessageId, "خطای ورود", "ورود انجام نشد. دوباره تلاش کنید.", ex.ToString(), 0f, false, GlobalMessageManager.MessageSource.Authentication);
            }
            finally
            {
                authOperationRunning = false;
                GlobalMessageManager.Clear(LoginProgressMessageId);
                ApplyLoginUiState(CurrentAuthState);
            }

            if (!loginSucceeded) return false;
            await Login_Init(true);
            return isLogin && CurrentUser != null;
        }

        //* این تابع اطلاعات کاربر را دریافت می‌کند و در صورت منقضی‌شدن نشست، یک بار نشست را تمدید و درخواست را تکرار می‌کند.
        public async Task Login_Init(bool manualLoginCompleted = false)
        {
            if (authOperationRunning || logoutRunning) return;

            authOperationRunning = true;

            try
            {
                lastRefreshFailureReason = AuthFailureReason.None;
                lastRefreshFailureDetails = string.Empty;

                if (!manualLoginCompleted && !rememberMeEnabled)
                {
                    RequestManualLogin("remember_me_disabled");
                    GlobalMessageManager.ClearNetworkMessages();
                    GlobalMessageManager.ShowWarning(AuthErrorMessageId, "ورود لازم است", manualLoginMessage, "Remember Me is disabled.", 0f, GlobalMessageManager.MessageSource.Authentication);
                    return;
                }

                bool networkReady = StartupNetworkSceneRouter.Instance != null && StartupNetworkSceneRouter.IsOnline;

                if (!networkReady)
                {
                    SetAuthState(AuthState.WaitingForNetwork, "login_init_network_not_ready");
                    return;
                }

                if (string.IsNullOrEmpty(SecureTokenStorage.GetAccessToken()) && string.IsNullOrEmpty(SecureTokenStorage.GetRefreshToken()))
                {
                    RequestManualLogin("local_tokens_are_empty");
                    GlobalMessageManager.ClearNetworkMessages();
                    GlobalMessageManager.ShowWarning(AuthErrorMessageId, "ورود لازم است", manualLoginMessage, "Access token and refresh token are empty.", 0f, GlobalMessageManager.MessageSource.Authentication);
                    return;
                }

                SetAuthState(AuthState.FetchingUser, "login_init_started");
                GlobalMessageManager.ShowInfo(LoginProgressMessageId, "دریافت اطلاعات کاربر", loginInitProgressMessage, string.Empty, 0f, GlobalMessageManager.MessageSource.Authentication);

                ApiResult<GetUserDataResponseDto> result = await AuthService.GetCurrentUserAsync(lifecycleCts.Token);

                string firstError = result != null ? (result.ErrorMessage ?? string.Empty) + " | " + (result.RawBody ?? string.Empty) : "GetUserData result is null.";

                bool tokenRejected = result != null && !result.IsSuccess && (result.StatusCode == 401 || result.StatusCode == 16 || firstError.IndexOf("expired", StringComparison.OrdinalIgnoreCase) >= 0 || firstError.IndexOf("unauth", StringComparison.OrdinalIgnoreCase) >= 0 || firstError.IndexOf("authentication failed", StringComparison.OrdinalIgnoreCase) >= 0);

                bool requestManagerAlreadyTriedRefresh = firstError.IndexOf("Refresh token failed", StringComparison.OrdinalIgnoreCase) >= 0;

                if (tokenRejected && !requestManagerAlreadyTriedRefresh && !string.IsNullOrEmpty(SecureTokenStorage.GetRefreshToken()))
                {
                    bool refreshed = await Refresh();

                    if (refreshed)
                    {
                        SetAuthState(AuthState.FetchingUser, "refresh_succeeded_retry_get_user");
                        result = await AuthService.GetCurrentUserAsync(lifecycleCts.Token);
                    }
                }

                bool userResultIsValid = result != null && result.IsSuccess && result.Data != null && result.Data.success && result.Data.user != null;

                if (!userResultIsValid)
                {
                    string error = result != null ? (result.ErrorMessage ?? string.Empty) + " | " + (result.RawBody ?? string.Empty) : "GetUserData result is null.";

                    AuthFailureReason reason = lastRefreshFailureReason != AuthFailureReason.None ? lastRefreshFailureReason : ClassifyFailure(error, result != null ? result.StatusCode : 0, result == null || result.IsNetworkError);

                    bool networkFailure = reason == AuthFailureReason.NetworkUnavailable || reason == AuthFailureReason.ServerUnavailable || reason == AuthFailureReason.Timeout;

                    if (networkFailure)
                    {
                        SetAuthState(AuthState.WaitingForNetwork, error);

                        if (rememberMeEnabled) ScheduleTransientLoginRetry(error);
                        if (StartupNetworkSceneRouter.Instance == null) { GlobalMessageManager.ShowWarning(AuthErrorMessageId, "ارتباط شبکه", noNetworkMessage, error, 0f, GlobalMessageManager.MessageSource.Authentication); }

                        return;
                    }

                    if (reason == AuthFailureReason.RefreshTokenExpired || reason == AuthFailureReason.RefreshTokenRevoked || reason == AuthFailureReason.InvalidToken || reason == AuthFailureReason.Unauthorized || reason == AuthFailureReason.AccessTokenExpired) { SecureTokenStorage.ClearTokens(); }

                    isLogin = false;
                    CurrentUser = null;
                    RequestManualLogin(error);
                    GlobalMessageManager.ClearNetworkMessages();
                    GlobalMessageManager.ShowWarning(AuthErrorMessageId, "ورود دوباره لازم است", expiredSessionMessage, error, 0f, GlobalMessageManager.MessageSource.Authentication);
                    return;
                }

                if (StartupNetworkSceneRouter.Instance == null || !StartupNetworkSceneRouter.IsOnline)
                {
                    SetAuthState(AuthState.WaitingForNetwork, "login_init_completed_after_network_lost");
                    return;
                }

                CurrentUser = result.Data.user;
                isLogin = true;
                lastRefreshFailureReason = AuthFailureReason.None;
                lastRefreshFailureDetails = string.Empty;
                CancelTransientLoginRetry("login_init_succeeded");
                SetAuthState(AuthState.Authenticated, "login_init_succeeded");

                GlobalMessageManager.ClearNetworkMessages();
                GlobalMessageManager.ClearFromSource(GlobalMessageManager.MessageSource.Authentication);
                GlobalMessageManager.ShowSuccess(LoginResultMessageId, "ورود موفق", loginSuccessMessage, "userId=" + CurrentUser.id + " | username=" + CurrentUser.emailOrUsername, Mathf.Max(0.1f, loginSuccessMessageDurationSeconds), GlobalMessageManager.MessageSource.Authentication);

                NetworkFileLogger.Auth("GLOBAL_LOGIN_INIT_SUCCESS", true, result.Data.message, CurrentUser.emailOrUsername, !string.IsNullOrEmpty(SecureTokenStorage.GetAccessToken()), !string.IsNullOrEmpty(SecureTokenStorage.GetRefreshToken()));

                Action<AuthUserDto> handler = OnLoginReady;

                if (handler != null)
                {
                    try
                    {
                        handler(CurrentUser);
                    }
                    catch (Exception ex)
                    {
                        NetworkFileLogger.Exception("GLOBAL_LOGIN_READY_EVENT", ex);
                    }
                }

                Scene activeScene = SceneManager.GetActiveScene();
                if (loadAvatarSelectorAfterLoginInit && string.Equals(activeScene.name, loginSceneName, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(avatarSelectorSceneName))
                {
                    AsyncOperation loadOperation = SceneManager.LoadSceneAsync(avatarSelectorSceneName, LoadSceneMode.Single);

                    if (loadOperation == null) throw new InvalidOperationException("Avatar Selector scene load operation is null.");

                    while (!loadOperation.isDone)
                    {
                        lifecycleCts.Token.ThrowIfCancellationRequested();
                        await Task.Yield();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                NetworkFileLogger.Warning("GLOBAL_LOGIN_INIT", "Login_Init cancelled.");
            }
            catch (Exception ex)
            {
                AuthFailureReason reason = ClassifyFailure(ex.ToString(), 0, false);
                NetworkFileLogger.Exception("GLOBAL_LOGIN_INIT", ex);

                if (IsTransientAuthFailure(reason))
                {
                    SetAuthState(AuthState.WaitingForNetwork, ex.Message);
                    if (rememberMeEnabled) ScheduleTransientLoginRetry(ex.ToString());
                    return;
                }

                isLogin = false;
                CurrentUser = null;
                RequestManualLogin(ex.Message);
                if (StartupNetworkSceneRouter.Instance != null && StartupNetworkSceneRouter.IsOnline) GlobalMessageManager.ClearNetworkMessages();
                GlobalMessageManager.ShowError(AuthErrorMessageId, "خطای ورود", "دریافت اطلاعات کاربر انجام نشد.", ex.ToString(), 0f, false, GlobalMessageManager.MessageSource.Authentication);
            }
            finally
            {
                authOperationRunning = false;
                GlobalMessageManager.Clear(LoginProgressMessageId);
                ApplyLoginUiState(CurrentAuthState);
            }
        }

        #endregion

        #region تنظیم مرا به خاطر بسپار و خروج رسمی

        //* این تابع مقدار «مرا به خاطر بسپار» را بدون پایان‌دادن نشست فعلی تغییر می‌دهد و در حافظه محلی ذخیره می‌کند.
        public void SetRememberMe(bool enabled)
        {
            bool changed = rememberMeEnabled != enabled;
            rememberMeEnabled = enabled;
            PlayerPrefs.SetInt(RememberMeKey, rememberMeEnabled ? 1 : 0);
            PlayerPrefs.Save();

            if (tgl_RememberMe != null) tgl_RememberMe.SetIsOnWithoutNotify(rememberMeEnabled);

            NetworkFileLogger.Info("GLOBAL_AUTH_REMEMBER_ME", "enabled=" + rememberMeEnabled);

            if (!changed) return;

            Action<bool> handler = OnRememberMeChanged;

            if (handler == null) return;

            foreach (Action<bool> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(rememberMeEnabled);
                }
                catch (Exception ex)
                {
                    NetworkFileLogger.Exception("GLOBAL_AUTH_REMEMBER_ME_EVENT", ex);
                }
            }
        }

        //* این تابع برای اتصال مستقیم دکمه خروج در بازرس استفاده می‌شود و مسیر غیرهم‌زمان رابط را به خروج رسمی وصل می‌کند.
        public async void Logout()
        {
            try
            {
                await LogoutAsync();
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("GLOBAL_AUTH_LOGOUT_BUTTON", ex);
            }
        }

        //* این تابع خروج رسمی را آغاز می‌کند، فرصت پاک‌سازی شبکه را می‌دهد و حتی در صورت خطای شبکه نشست محلی را به‌طور قطعی پاک می‌کند.
        public async Task LogoutAsync()
        {
            if (logoutRunning) return;

            logoutRunning = true;
            CancelTransientLoginRetry("official_logout_started");
            SetAuthState(AuthState.SigningOut, "official_logout_started");
            GlobalMessageManager.ClearFromSource(GlobalMessageManager.MessageSource.Authentication);
            GlobalMessageManager.ShowInfo(LogoutProgressMessageId, "خروج از حساب", logoutProgressMessage, string.Empty, 0f, GlobalMessageManager.MessageSource.Authentication);
            InvokeSimpleEvent(OnLogoutStarted, "GLOBAL_AUTH_LOGOUT_STARTED_EVENT");

            try
            {
                await RunLogoutCleanupHandlersAsync();
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("GLOBAL_AUTH_LOGOUT_CLEANUP", ex);
            }
            finally
            {
                SecureTokenStorage.ClearTokens();
                CurrentUser = null;
                isLogin = false;
                lastRefreshFailureReason = AuthFailureReason.None;
                lastRefreshFailureDetails = string.Empty;
                SetRememberMe(false);

                if (in_Log_Pass != null) in_Log_Pass.text = string.Empty;

                GlobalMessageManager.Clear(LoginProgressMessageId);
                GlobalMessageManager.Clear(RefreshProgressMessageId);
                GlobalMessageManager.Clear(LogoutProgressMessageId);
                GlobalMessageManager.ClearNetworkMessages();

                SetAuthState(AuthState.ManualLoginRequired, "official_logout_completed");
                if (pnl_Login != null) pnl_Login.SetActive(true);

                GlobalMessageManager.ShowSuccess(
                    LogoutResultMessageId,
                    "خروج انجام شد",
                    logoutSuccessMessage,
                    string.Empty,
                    Mathf.Max(0.1f, logoutSuccessMessageDurationSeconds),
                    GlobalMessageManager.MessageSource.Authentication
                );

                NetworkFileLogger.Auth("GLOBAL_LOGOUT_COMPLETED", true, "Local authentication state cleared.", string.Empty, false, false);
                InvokeSimpleEvent(OnLogoutCompleted, "GLOBAL_AUTH_LOGOUT_COMPLETED_EVENT");
                logoutRunning = false;
                ApplyLoginUiState(CurrentAuthState);
            }

            await LoadLoginSceneAfterLogoutAsync();
        }

        //* این تابع تمام درخواست‌کننده‌های پاک‌سازی Realtime، روم و Game Server را با مهلت محدود اجرا می‌کند.
        private async Task RunLogoutCleanupHandlersAsync()
        {
            Func<CancellationToken, Task> handler = OnLogoutCleanupRequested;

            if (handler == null)
            {
                NetworkFileLogger.Warning("GLOBAL_AUTH_LOGOUT_CLEANUP", "No logout cleanup handler is registered. Local logout will continue.");
                return;
            }

            using (CancellationTokenSource timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Mathf.Max(1f, logoutCleanupTimeoutSeconds))))
            using (CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, lifecycleCts.Token))
            {
                foreach (Func<CancellationToken, Task> subscriber in handler.GetInvocationList())
                {
                    try
                    {
                        Task cleanupTask = subscriber(linkedCts.Token);
                        if (cleanupTask != null) await cleanupTask;
                    }
                    catch (OperationCanceledException)
                    {
                        if (lifecycleCts != null && lifecycleCts.IsCancellationRequested) throw;
                        NetworkFileLogger.Warning("GLOBAL_AUTH_LOGOUT_CLEANUP", "Logout cleanup handler timed out or was cancelled. Local logout will continue.");
                    }
                    catch (Exception ex)
                    {
                        NetworkFileLogger.Exception("GLOBAL_AUTH_LOGOUT_CLEANUP_HANDLER", ex);
                    }
                }
            }
        }

        //* این تابع پس از پاک‌شدن نشست، در صورت نیاز صحنه ورود را بارگذاری می‌کند.
        private async Task LoadLoginSceneAfterLogoutAsync()
        {
            if (!loadLoginSceneAfterLogout || string.IsNullOrWhiteSpace(loginSceneName)) return;

            Scene activeScene = SceneManager.GetActiveScene();
            if (string.Equals(activeScene.name, loginSceneName, StringComparison.Ordinal)) return;

            AsyncOperation loadOperation = SceneManager.LoadSceneAsync(loginSceneName, LoadSceneMode.Single);

            if (loadOperation == null)
            {
                NetworkFileLogger.Error("GLOBAL_AUTH_LOGOUT_SCENE", "Login scene load operation is null. scene=" + loginSceneName);
                return;
            }

            while (!loadOperation.isDone)
            {
                if (lifecycleCts == null || lifecycleCts.IsCancellationRequested) return;
                await Task.Yield();
            }
        }

        //* این تابع رویدادهای ساده خروج را برای هر شنونده جداگانه و ایمن اجرا می‌کند.
        private static void InvokeSimpleEvent(Action handler, string logTag)
        {
            if (handler == null) return;

            foreach (Action subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber();
                }
                catch (Exception ex)
                {
                    NetworkFileLogger.Exception(logTag, ex);
                }
            }
        }

        #endregion

        #region تمدید نشست و بازیابی ورود

        //* این تابع همه درخواست‌های هم‌زمان تمدید نشست را به مسیر مشترک می‌فرستد تا فقط یک درخواست واقعی اجرا شود.
        public Task<bool> Refresh()
        {
            return AuthRefreshManager.Refresh();
        }

        //* این تابع درخواست واقعی تمدید نشست را با روش ارتباطی مناسب اجرا می‌کند و نتیجه را برای ذخیره‌سازی آماده می‌سازد.
        private async Task<bool> RefreshInternalAsync()
        {
            lastRefreshFailureReason = AuthFailureReason.None;
            lastRefreshFailureDetails = string.Empty;

            bool networkReady = StartupNetworkSceneRouter.Instance != null && StartupNetworkSceneRouter.IsOnline;

            if (!networkReady) return SetRefreshFailure(AuthFailureReason.NetworkUnavailable, "Refresh blocked because network is not ready.");

            string refreshToken = SecureTokenStorage.GetRefreshToken();
            if (string.IsNullOrWhiteSpace(refreshToken)) return SetRefreshFailure(AuthFailureReason.Unauthorized, "Refresh token is empty.");

            GlobalMessageManager.ShowInfo(RefreshProgressMessageId, "تمدید نشست", refreshProgressMessage, string.Empty, 0f, GlobalMessageManager.MessageSource.Authentication);

            byte[] message = AuthProtoMapper.EncodeRefreshRequest(refreshToken);
            int timeoutSeconds = Mathf.Max(1, ServerConfig.TimeoutSeconds);

            using (CancellationTokenSource timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            using (CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, lifecycleCts.Token))
            {
                if (ServerConfig.IsGrpcNative())
                {
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_ANDROID
                    ApiResult<byte[]> raw = await GrpcNativeUnaryClient.SendAsync(ServerConfig.ServiceName, "Refresh", message, false, null, linkedCts.Token, "GLOBAL_REFRESH_NATIVE");

                    if (raw == null) return SetRefreshFailure(AuthFailureReason.InvalidResponse, "Native refresh result is null.");

                    if (!raw.IsSuccess) { return SetRefreshFailure(ClassifyFailure(raw.ErrorMessage, raw.StatusCode, raw.IsNetworkError), "Native refresh failed | status=" + raw.StatusCode + " | error=" + raw.ErrorMessage); }

                    AuthResponseDto dto = AuthProtoMapper.DecodeAuthResponse(AuthService.ReadNativeBytes(raw));
                    return SaveRefreshDto(dto, refreshToken);
#else
                    return SetRefreshFailure(AuthFailureReason.Unknown, "Native refresh is not enabled for this platform.");
#endif
                }

                byte[] frame = AuthProtoMapper.EncodeGrpcWebUnaryRequest(message);

                using (UnityWebRequest request = new UnityWebRequest(ServerConfig.RefreshUrl, UnityWebRequest.kHttpVerbPOST))
                {
                    request.timeout = timeoutSeconds;
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.uploadHandler = new UploadHandlerRaw(frame);

                    foreach (KeyValuePair<string, string> pair in AuthService.BuildGrpcWebHeaders()) request.SetRequestHeader(pair.Key, pair.Value);

                    try
                    {
                        await UnityWebRequestAsync.SendAsync(request, linkedCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        if (lifecycleCts.IsCancellationRequested) throw;
                        return SetRefreshFailure(AuthFailureReason.Timeout, "Refresh timeout after " + timeoutSeconds + " seconds.");
                    }
                    catch (Exception ex)
                    {
                        return SetRefreshFailure(ClassifyFailure(ex.Message, 0, true), "Refresh exception | " + ex.Message);
                    }

                    byte[] rawBytes = request.downloadHandler != null && request.downloadHandler.data != null ? request.downloadHandler.data : new byte[0];

                    string body = request.downloadHandler != null ? request.downloadHandler.text ?? string.Empty : string.Empty;

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        string error = (request.error ?? string.Empty) + " | " + body;
                        bool networkError = request.responseCode <= 0 || request.result == UnityWebRequest.Result.ConnectionError || request.result == UnityWebRequest.Result.DataProcessingError;

                        return SetRefreshFailure(ClassifyFailure(error, (int)request.responseCode, networkError), "gRPC-Web refresh failed | status=" + request.responseCode + " | result=" + request.result + " | error=" + error);
                    }

                    if (rawBytes.Length == 0) return SetRefreshFailure(AuthFailureReason.InvalidResponse, "Refresh response bytes are empty.");

                    byte[] protoBytes;
                    Dictionary<string, string> trailers;

                    if (!AuthProtoMapper.TryDecodeGrpcWebUnaryResponse(rawBytes, out protoBytes, out trailers)) return SetRefreshFailure(AuthFailureReason.InvalidResponse, "Invalid gRPC-Web refresh response.");

                    string grpcStatus = AuthService.ReadTrailer(trailers, "grpc-status");
                    string grpcMessage = AuthService.DecodeGrpcMessage(AuthService.ReadTrailer(trailers, "grpc-message"));

                    if (!string.IsNullOrEmpty(grpcStatus) && grpcStatus != "0")
                    {
                        int parsedStatus;
                        int status = int.TryParse(grpcStatus, out parsedStatus) ? parsedStatus : 0;

                        return SetRefreshFailure(ClassifyFailure(grpcMessage, status, false), "gRPC refresh failed | grpcStatus=" + grpcStatus + " | grpcMessage=" + grpcMessage);
                    }

                    AuthResponseDto dto = AuthProtoMapper.DecodeAuthResponse(protoBytes);
                    return SaveRefreshDto(dto, refreshToken);
                }
            }
        }

        //* این تابع تغییر وضعیت شبکه را دریافت می‌کند و پس از بازیابی بر اساس مقدار ذخیره‌شده ورود خودکار یا دستی را آغاز می‌کند.
        private async void HandleNetworkStateChanged(StartupNetworkSceneRouter.NetworkState state)
        {
            if (Instance != this || logoutRunning) return;

            if (state != StartupNetworkSceneRouter.NetworkState.Online)
            {
                SetAuthState(AuthState.WaitingForNetwork, "network_state=" + state);
                return;
            }

            if (authOperationRunning) return;

            if (rememberMeEnabled)
            {
                if (transientLoginRetryRunning) return;
                await Login_Init();
                return;
            }

            isLogin = false;
            CurrentUser = null;
            RequestManualLogin("network_recovered_remember_me_disabled");
            GlobalMessageManager.ClearNetworkMessages();
            GlobalMessageManager.ShowWarning(AuthErrorMessageId, "ورود لازم است", manualLoginMessage, "Network recovered while Remember Me is disabled.", 0f, GlobalMessageManager.MessageSource.Authentication);
        }

        //* این تابع پس از شکست غیرشبکه‌ای تمدید نشست، اطلاعات نشست محلی را پاک می‌کند و رابط ورود را نمایش می‌دهد.
        private void HandleRefreshRequireLogin()
        {
            if (lastRefreshFailureReason == AuthFailureReason.NetworkUnavailable || lastRefreshFailureReason == AuthFailureReason.ServerUnavailable || lastRefreshFailureReason == AuthFailureReason.Timeout)
            {
                if (!isLogin) SetAuthState(AuthState.WaitingForNetwork, lastRefreshFailureDetails);
                if (rememberMeEnabled) ScheduleTransientLoginRetry(lastRefreshFailureDetails);
                return;
            }

            SecureTokenStorage.ClearTokens();
            isLogin = false;
            CurrentUser = null;
            RequestManualLogin(lastRefreshFailureDetails);

            GlobalMessageManager.ShowWarning(AuthErrorMessageId, "ورود دوباره لازم است", expiredSessionMessage, lastRefreshFailureDetails, 0f, GlobalMessageManager.MessageSource.Authentication);
        }

        //* این تابع وضعیت ورود دستی را ثبت می‌کند و پنل ورود سراسری را نمایش می‌دهد.
        private void RequestManualLogin(string details)
        {
            CancelTransientLoginRetry("manual_login_required");
            SetAuthState(AuthState.ManualLoginRequired, details);
            if (tgl_RememberMe != null) tgl_RememberMe.SetIsOnWithoutNotify(rememberMeEnabled);
            if (pnl_Login != null) pnl_Login.SetActive(true);
        }

        //* این تابع پاسخ تمدید نشست را بررسی می‌کند و نشانه‌های دسترسی تازه را در حافظه امن ذخیره می‌کند.
        private bool SaveRefreshDto(AuthResponseDto dto, string previousRefreshToken)
        {
            if (dto == null) return SetRefreshFailure(AuthFailureReason.InvalidResponse, "Decoded refresh dto is null.");

            if (!dto.success) { return SetRefreshFailure(ClassifyFailure(dto.message, 401, false), "Refresh success=false | message=" + dto.message); }

            if (string.IsNullOrWhiteSpace(dto.accessToken)) return SetRefreshFailure(AuthFailureReason.InvalidToken, "Refresh access token is empty.");

            string finalRefreshToken = string.IsNullOrWhiteSpace(dto.refreshToken) ? previousRefreshToken : dto.refreshToken;

            SecureTokenStorage.SaveTokens(dto.accessToken, finalRefreshToken);
            lastRefreshFailureReason = AuthFailureReason.None;
            lastRefreshFailureDetails = string.Empty;
            GlobalMessageManager.Clear(RefreshProgressMessageId);

            if (isLogin && CurrentUser != null) SetAuthState(AuthState.Authenticated, "refresh_succeeded");

            NetworkFileLogger.TokenState("GLOBAL_REFRESH_SUCCESS", SecureTokenStorage.GetAccessToken(), SecureTokenStorage.GetRefreshToken());

            Action handler = OnTokensRefreshed;

            if (handler != null)
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    NetworkFileLogger.Exception("GLOBAL_TOKENS_REFRESHED_EVENT", ex);
                }
            }
            return true;
        }

        //* این تابع علت و جزئیات شکست تمدید نشست را ذخیره می‌کند و پیام انتظار را می‌بندد.
        private bool SetRefreshFailure(AuthFailureReason reason, string details)
        {
            lastRefreshFailureReason = reason == AuthFailureReason.None ? AuthFailureReason.Unknown : reason;

            lastRefreshFailureDetails = details ?? string.Empty;
            GlobalMessageManager.Clear(RefreshProgressMessageId);
            NetworkFileLogger.Warning("GLOBAL_AUTH_REFRESH", "reason=" + lastRefreshFailureReason + " | " + lastRefreshFailureDetails);
            return false;
        }

        #endregion

        #region تلاش دوباره ورود خودکار

        //* این تابع پس از خطاهای موقت شبکه یا کانال نیتیو، فقط یک حلقه تلاش دوباره ورود خودکار را ایجاد می کند.
        private void ScheduleTransientLoginRetry(string reason)
        {
            if (Instance != this || !rememberMeEnabled || CurrentAuthState == AuthState.Authenticated) return;
            if (transientLoginRetryRunning || lifecycleCts == null || lifecycleCts.IsCancellationRequested) return;

            transientLoginRetryRunning = true;
            transientLoginRetryAttempt = 0;
            transientLoginRetryCts = CancellationTokenSource.CreateLinkedTokenSource(lifecycleCts.Token);
            _ = RunTransientLoginRetryAsync(reason, transientLoginRetryCts);
            NetworkFileLogger.Warning("GLOBAL_AUTH_RETRY", "تلاش دوباره ورود خودکار زمان بندی شد | reason=" + (reason ?? string.Empty));
        }

        //* این تابع تا پایان مهلت بازیابی، هنگام برخط بودن سرور Login_Init را با فاصله افزایشی دوباره اجرا می کند.
        private async Task RunTransientLoginRetryAsync(string reason, CancellationTokenSource ownerCts)
        {
            CancellationToken cancellationToken = ownerCts.Token;
            DateTime deadlineUtc = DateTime.UtcNow.AddSeconds(Math.Max(5f, transientLoginRetryWindowSeconds));
            float delaySeconds = Math.Max(0.1f, transientLoginRetryInitialDelaySeconds);

            try
            {
                while (Instance == this && rememberMeEnabled && CurrentAuthState != AuthState.Authenticated)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (DateTime.UtcNow >= deadlineUtc)
                    {
                        NetworkFileLogger.Warning("GLOBAL_AUTH_RETRY", "مهلت تلاش دوباره ورود خودکار پایان یافت | attempts=" + transientLoginRetryAttempt + " | reason=" + (reason ?? string.Empty));
                        return;
                    }

                    if (CurrentAuthState == AuthState.ManualLoginRequired) return;

                    if (StartupNetworkSceneRouter.Instance == null || !StartupNetworkSceneRouter.IsOnline || authOperationRunning)
                    {
                        await Task.Delay(250, cancellationToken);
                        continue;
                    }

                    transientLoginRetryAttempt++;
                    int delayMilliseconds = Mathf.Max(100, Mathf.RoundToInt(delaySeconds * 1000f));
                    NetworkFileLogger.Info("GLOBAL_AUTH_RETRY", "attempt=" + transientLoginRetryAttempt + " | delayMs=" + delayMilliseconds + " | reason=" + (reason ?? string.Empty));
                    await Task.Delay(delayMilliseconds, cancellationToken);

                    if (StartupNetworkSceneRouter.Instance == null || !StartupNetworkSceneRouter.IsOnline || authOperationRunning) continue;

                    await Login_Init();
                    if (isLogin && CurrentUser != null && CurrentAuthState == AuthState.Authenticated) return;
                    if (CurrentAuthState == AuthState.ManualLoginRequired) return;

                    delaySeconds = Math.Min(Math.Max(0.5f, transientLoginRetryMaximumDelaySeconds), delaySeconds * 1.75f);
                }
            }
            catch (OperationCanceledException)
            {
                NetworkFileLogger.Info("GLOBAL_AUTH_RETRY", "تلاش دوباره ورود خودکار متوقف شد.");
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("GLOBAL_AUTH_RETRY", ex);
            }
            finally
            {
                if (ReferenceEquals(transientLoginRetryCts, ownerCts)) transientLoginRetryCts = null;
                ownerCts.Dispose();
                transientLoginRetryRunning = false;
                transientLoginRetryAttempt = 0;
            }
        }

        //* این تابع حلقه تلاش دوباره ورود خودکار را پس از موفقیت، ورود دستی یا نابودی مدیر متوقف می کند.
        private void CancelTransientLoginRetry(string reason)
        {
            if (transientLoginRetryCts == null || transientLoginRetryCts.IsCancellationRequested) return;
            transientLoginRetryCts.Cancel();
            NetworkFileLogger.Info("GLOBAL_AUTH_RETRY", "لغو شد | reason=" + (reason ?? string.Empty));
        }

        //* این تابع مشخص می کند شکست ورود از نوع موقت و قابل تلاش دوباره است یا نه.
        private static bool IsTransientAuthFailure(AuthFailureReason reason)
        {
            return reason == AuthFailureReason.NetworkUnavailable || reason == AuthFailureReason.ServerUnavailable || reason == AuthFailureReason.Timeout;
        }

        #endregion

        #region ابزارهای داخلی

        //* این تابع کامل‌بودن مراجع رابط ورود سراسری را بدون جست‌وجوی خودکار در صحنه بررسی می‌کند.
        private bool HasValidLoginUi()
        {
            return pnl_Login != null && in_Log_UserName != null && in_Log_Pass != null && btn_Log != null;
        }

        //* این تابع پنل و دکمه ورود سراسری را بر اساس وضعیت فعلی ورود تنظیم می‌کند.
        private void ApplyLoginUiState(AuthState state)
        {
            if (pnl_Login != null)
            {
                if (state == AuthState.Authenticated) pnl_Login.SetActive(false);
                else if (state == AuthState.ManualLoginRequired) pnl_Login.SetActive(true);
            }

            if (btn_Log != null) btn_Log.interactable = !authOperationRunning && !logoutRunning && state != AuthState.LoggingIn && state != AuthState.FetchingUser && state != AuthState.SigningOut;
        }

        //* این تابع پیام و کد خطا را بررسی می‌کند و علت مناسب شکست را برای تصمیم‌گیری برمی‌گرداند.
        private AuthFailureReason ClassifyFailure(string error, int statusCode, bool networkError)
        {
            string decoded = AuthService.DecodeGrpcMessage(error ?? string.Empty);
            string upper = decoded.ToUpperInvariant();

            //* فقط وضعیت نهایی مدیر شبکه برای تشخیص قطع ارتباط خوانده می‌شود و این کلاس بررسی جداگانه‌ای انجام نمی‌دهد.
            if (StartupNetworkSceneRouter.Instance != null && StartupNetworkSceneRouter.CurrentState == StartupNetworkSceneRouter.NetworkState.InternetUnavailable) { return AuthFailureReason.NetworkUnavailable; }

            if (upper.Contains("REFRESH_TOKEN_EXPIRED") || upper.Contains("REFRESH TOKEN EXPIRED")) return AuthFailureReason.RefreshTokenExpired;

            if (upper.Contains("REFRESH_TOKEN_REVOKED") || upper.Contains("REFRESH TOKEN REVOKED") || upper.Contains("TOKEN_REVOKED")) { return AuthFailureReason.RefreshTokenRevoked; }

            if (upper.Contains("TIMEOUT") || upper.Contains("TIMED OUT") || upper.Contains("CANCELED") || upper.Contains("CANCELLED") || statusCode == 4) { return AuthFailureReason.Timeout; }

            if (upper.Contains("TOKEN_EXPIRED") || upper.Contains("JWT EXPIRED") || upper.Contains("JWT IS EXPIRED") || upper.Contains("ACCESS TOKEN EXPIRED")) { return AuthFailureReason.AccessTokenExpired; }

            if (upper.Contains("INVALID TOKEN") || upper.Contains("INVALID_TOKEN") || upper.Contains("MALFORMED TOKEN") || upper.Contains("MALFORMED JWT")) { return AuthFailureReason.InvalidToken; }

            if (statusCode == 401 || statusCode == 16 || upper.Contains("UNAUTHORIZED") || upper.Contains("UNAUTHENTICATED") || upper.Contains("AUTHENTICATION_FAILED")) { return AuthFailureReason.Unauthorized; }

            if (networkError || statusCode == 14 || statusCode == 502 || statusCode == 503 || statusCode == 504 || upper.Contains("CONNECTION") || upper.Contains("UNAVAILABLE") || upper.Contains("DNS") || upper.Contains("HOST") || upper.Contains("STREAM REMOVED") || upper.Contains("RST_STREAM") || upper.Contains("HTTP/2")) { return AuthFailureReason.ServerUnavailable; }

            if (upper.Contains("EMPTY") || upper.Contains("INVALID RESPONSE") || upper.Contains("DECODE")) { return AuthFailureReason.InvalidResponse; }

            return AuthFailureReason.Unknown;
        }

        //* این تابع وضعیت کلی ورود را تغییر می‌دهد، آن را در گزارش ثبت می‌کند و رویداد مربوط را می‌فرستد.
        private void SetAuthState(AuthState state, string details)
        {
            AuthState previous = CurrentAuthState;
            CurrentAuthState = state;
            ApplyLoginUiState(state);

            if (previous == state) return;

            NetworkFileLogger.Info("GLOBAL_AUTH_STATE", "previous=" + previous + " | current=" + state + " | details=" + (details ?? string.Empty));

            Action<AuthState> handler = OnAuthStateChanged;

            if (handler != null)
            {
                try
                {
                    handler(state);
                }
                catch (Exception ex)
                {
                    NetworkFileLogger.Exception("GLOBAL_AUTH_STATE_EVENT", ex);
                }
            }
        }

        #endregion
    }
}
