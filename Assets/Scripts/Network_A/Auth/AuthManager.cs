using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using RTLTMPro;
using Network_A.Core;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Project.UI.MainMenu;
namespace Network_A.Auth
{
    public class AuthManager : MonoBehaviour
    {
        public static AuthManager Instance { get; private set; }

        #region <ENUM>
        public enum ServerType
        {
            Local,
            Dedicated,
            Custom
        }

        [Header("Server Select")]
        [SerializeField] private ServerType serverType = ServerType.Local;
        [SerializeField] private string dedicatedHost = "127.0.0.1";
        [SerializeField] private int dedicatedPort = 8443;
        [SerializeField] private bool dedicatedUseTls = true;
        [SerializeField] private string customHost = "localhost";
        [SerializeField] private int customPort = 8443;
        [SerializeField] private bool customUseTls = true;

        [Header("Native Dedicated gRPC")]
        [SerializeField] private string nativeDedicatedHost = "dev-world-3d.metarang.com";
        [SerializeField] private int nativeDedicatedPort = 50052;
        [SerializeField] private bool nativeDedicatedUseTls = true;

#if UNITY_EDITOR
        [Header("Editor Native Auth Test")]
        [SerializeField] private bool editorUseNativeGrpcAuth;
#endif
        #endregion

        #region <Run Options>
        [Header("Run Options")]
        [SerializeField] private bool clearTokensOnStart;
        [SerializeField] private bool checkNetOnStart = true;
        [SerializeField] private bool autoLoginInitAfterHealth = true;
        [SerializeField] private bool showRegisterPanelIfFirstInstall = true;
        #endregion

        #region <Register UI>
        [Header("--- Register Input ---")]
        [SerializeField] private GameObject pnl_Reg;
        [SerializeField] private TMP_InputField in_RegUserName;
        [SerializeField] private TMP_InputField in_RegPass;
        [SerializeField] private Button btn_Reg;
        private bool is_First_Reg;
        #endregion

        #region <Login UI>
        [Header("--- Manual Login Input ---")]
        [SerializeField] private GameObject pnl_Login;
        [SerializeField] private TMP_InputField in_Log_UserName;
        [SerializeField] private TMP_InputField in_Log_Pass;
        [SerializeField] private Button btn_Log;
        #endregion

        #region <Auth Input Validation>
        [Header("Auth Input Validation")]
        [SerializeField] private int minUsernameLength = 7;
        [SerializeField] private int minPasswordLength = 7;
        #endregion

        #region <Status UI>
        [Header("Status UI")]
        [SerializeField] private TMP_Text txt_Status;
        #endregion
        #region <Server Debug UI>
        [Header("Server Debug UI")]
        [SerializeField] private GameObject pnl_ServerDebug;
        [SerializeField] private RTLTextMeshPro txt_ServerDebug;
        [SerializeField] private Button btn_ServerDebugClose;
        [SerializeField] private bool showServerDebugPanelOnServerMessage = true;
        #endregion

        #region <Login State>
        [HideInInspector] public bool isLogin;
        [HideInInspector] public AuthUserDto CurrentUser;
        private string emailOrUsername;
        private string password;
        #endregion

        #region <Check Network>
        [Header("Check Network")]
        [SerializeField] private bool hasPing;
        [SerializeField] private float checkNet_Time_Limit = 5.0f;
        private float checkNet_TimeCount;
        private bool netWarning_Disabled;
        [SerializeField] private bool isCheckingNet;
        #endregion

        #region <_____________________________________________MONO_____________________________________________>
        //* Initializes singleton, server config and refresh pipeline.

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);

                ApplyServerType();

                AuthRefreshManager.Configure(RefreshInternalAsync);
                AuthRefreshManager.OnRequireLoginUI = ShowLoginRequired;

                NetworkFileLogger.Info("AUTH_MANAGER", "Awake ready. clearTokensOnStart=" + clearTokensOnStart);
            }
            else
            {
                NetworkFileLogger.Warning("AUTH_MANAGER", "Duplicate AuthManager detected. Destroying duplicate component only.");
                Destroy(this);
            }


            Application.runInBackground = true;

        }

        //* Starts auth startup flow after Awake is fully completed.
        private async void Start()
        {
            isLogin = false;
            SetupServerDebugPanel();
            if (clearTokensOnStart)
            {
                bool cleared = await ClearTokensOnStartAsync();

                if (!cleared)
                {
                    ShowWarningMessage("پاک کردن توکن انجام نشد. لطفاً دوباره تلاش کنید.");
                    return;
                }
            }

            if (checkNetOnStart) CheckNet();
        }

        //* Optional repeated network check timer.
        private void Update()
        {
            checkNet_TimeCount += Time.deltaTime;
            if (checkNet_TimeCount > checkNet_Time_Limit) checkNet_TimeCount = 0;
        }
        #endregion

        #region <Register && Login && Login_Init>
        //* Button entry point. Reads register input and starts register flow.
        public void Register()
        {
            SetButton(btn_Reg, false);
            RunSafely(Register_cor, "REGISTER_BUTTON");
        }

        //* Reads username/password from register UI, sends Register request, saves tokens and runs Login_Init.
        public async Task Register_cor()
        {
            string user = ReadInput(in_RegUserName);
            string pass = ReadInput(in_RegPass, false);

            if (!ValidateCredentials(user, pass, "REGISTER"))
            {
                SetButton(btn_Reg, true);
                return;
            }

            emailOrUsername = user;
            password = pass;

            NetworkFileLogger.Auth("REGISTER_START", true, "Register button flow started.", user, HasAccessToken(), HasRefreshToken());
            ShowInfoMessage("در حال ثبت نام...");

            ApiResult<AuthResponseDto> res = await RegisterAsync(user, pass);

            if (!res.IsSuccess)
            {
                SetButton(btn_Reg, true);
                string userMessage = BuildUserMessage("REGISTER", res);
                ShowErrorMessage(userMessage);
                NetworkFileLogger.Auth("REGISTER_FAILED", false, res.ErrorMessage, user, HasAccessToken(), HasRefreshToken());
                return;
            }

            isLogin = true;
            is_First_Reg = true;
            DH_Pnl_Reg(false);
            DH_Pnl_Login(false);

            ShowSuccessMessage("ثبت نام با موفقیت انجام شد");
            NetworkFileLogger.Auth("REGISTER_SUCCESS", true, ReadAuthMessage(res), user, HasAccessToken(), HasRefreshToken());

            await Task.Delay(300);
            await Login_Init();
            SetButton(btn_Reg, true);
        }

        //* Button entry point. Reads login input and starts login flow.
        public void Login()
        {
            SetButton(btn_Log, false);
            RunSafely(Login_Cor, "LOGIN_BUTTON");
        }

        //* Reads username/password from login UI, sends Login request, saves tokens and runs Login_Init.
        public async Task Login_Cor()
        {
            string user = ReadInput(in_Log_UserName);
            string pass = ReadInput(in_Log_Pass, false);

            if (!ValidateCredentials(user, pass, "LOGIN"))
            {
                SetButton(btn_Log, true);
                return;
            }

            emailOrUsername = user;
            password = pass;

            NetworkFileLogger.Auth("LOGIN_START", true, "Manual login flow started.", user, HasAccessToken(), HasRefreshToken());
            ShowInfoMessage("در حال ورود...");

            ApiResult<AuthResponseDto> res = await LoginAsync(user, pass);

            if (!res.IsSuccess)
            {
                SetButton(btn_Log, true);
                string userMessage = BuildUserMessage("LOGIN", res);
                ShowErrorMessage(userMessage);
                NetworkFileLogger.Auth("LOGIN_FAILED", false, res.ErrorMessage, user, HasAccessToken(), HasRefreshToken());
                return;
            }

            isLogin = true;
            DH_Pnl_Reg(false);
            DH_Pnl_Login(false);

            ShowSuccessMessage("ورود موفق بود");
            NetworkFileLogger.Auth("LOGIN_SUCCESS", true, ReadAuthMessage(res), user, HasAccessToken(), HasRefreshToken());

            await Task.Delay(300);
            await Login_Init();
            SetButton(btn_Log, true);
        }

        //* Runs after Register/Login success and also on next app opens when refresh token exists.
        public async Task Login_Init()
        {
            EnsureAuthServerConfig();

            NetworkFileLogger.Auth("LOGIN_INIT_START", true, "Login_Init started.", string.Empty, HasAccessToken(), HasRefreshToken());
            ShowInfoMessage("در حال دریافت اطلاعات کاربر | Request: GetUserData | Function: Login_Init");

            ApiResult<GetUserDataResponseDto> res = await GetUserDataAsync();

            if (res != null && !res.IsSuccess && IsUnauthorizedOrExpired(res.StatusCode, res.ErrorMessage) && HasRefreshToken())
            {
                NetworkFileLogger.Warning("LOGIN_INIT_REFRESH", "GetUserData failed with expired or unauthenticated token. Trying refresh before showing login UI. status=" + res.StatusCode + " error=" + res.ErrorMessage);
                ShowInfoMessage("نشست کاربری در حال تمدید است...");

                bool refreshed = await RefreshInternalAsync();

                if (refreshed)
                {
                    NetworkFileLogger.Info("LOGIN_INIT_REFRESH", "Refresh succeeded. Retrying GetUserData.");
                    res = await GetUserDataAsync();
                }
                else
                {
                    NetworkFileLogger.Warning("LOGIN_INIT_REFRESH", "Refresh failed. Login UI is required.");
                }
            }

            if (res == null)
            {
                isLogin = false;
                ShowErrorMessage("خطا در دریافت اطلاعات کاربر");
                NetworkFileLogger.Auth("LOGIN_INIT_NULL", false, "GetUserData result is null.", string.Empty, HasAccessToken(), HasRefreshToken());
                return;
            }

            if (!res.IsSuccess)
            {
                isLogin = false;
                if (IsUnauthorizedOrExpired(res.StatusCode, res.ErrorMessage))
                {
                    ShowWarningMessage("نشست کاربری منقضی شده است. لطفاً دوباره وارد شوید.");
                    ShowLoginRequired();
                }
                else
                {
                    ShowErrorMessage(BuildUserMessage("LOGIN_INIT", res));
                }

                NetworkFileLogger.Auth("LOGIN_INIT_FAILED", false, res.ErrorMessage, string.Empty, HasAccessToken(), HasRefreshToken());
                return;
            }

            if (res.Data == null || res.Data.user == null)
            {
                isLogin = false;
                ShowErrorMessage("اطلاعات کاربر از سرور دریافت نشد");
                NetworkFileLogger.Auth("LOGIN_INIT_EMPTY_USER", false, "User data is empty.", string.Empty, HasAccessToken(), HasRefreshToken());
                return;
            }

            isLogin = true;
            CurrentUser = res.Data.user;
            GetUserData_For_Menu_Base(res.Data);

            if (is_First_Reg) is_First_Reg = false;

            ShowSuccessMessage("اطلاعات کاربر دریافت شد | User: " + CurrentUser.emailOrUsername + " | Request: GetUserData");
            NetworkFileLogger.Auth("LOGIN_INIT_SUCCESS", true, res.Data.message, CurrentUser.emailOrUsername, HasAccessToken(), HasRefreshToken());
        }

        //* Keeps MindReader-style wait wrapper for external callers.
        public async Task WaitForLogin_Init()
        {
            await Task.Delay(1000);
            await Login_Init();
        }

        //* Stores current user data for menu/game systems. Extend this method later for project-specific lists/UI.
        private void GetUserData_For_Menu_Base(GetUserDataResponseDto response)
        {
            if (response == null || response.user == null) return;
            CurrentUser = response.user;
            NetworkFileLogger.Data("USER_DATA", "id", CurrentUser.id);
            NetworkFileLogger.Data("USER_DATA", "emailOrUsername", CurrentUser.emailOrUsername);
            NetworkFileLogger.Data("USER_DATA", "createdAtUnix", CurrentUser.createdAtUnix.ToString());
        }
        #endregion

        #region <Public Auth API>
        //* Registers a user through RequestManager queue and internal gRPC-Web mapper.
        public async Task<ApiResult<AuthResponseDto>> RegisterAsync(string emailOrUsernameValue, string passwordValue, CancellationToken ct = default(CancellationToken))
        {
            byte[] message = AuthProtoMapper.EncodeLoginLikeRequest(emailOrUsernameValue, passwordValue);
            ApiResult<AuthResponseDto> result = await SendAuthUnaryAsync(ServerConfig.RegisterUrl, message, false, "REGISTER", ct);
            SaveTokensIfPresent(result);
            return result;
        }

        //* Logs in through RequestManager queue and stores returned tokens.
        public async Task<ApiResult<AuthResponseDto>> LoginAsync(string emailOrUsernameValue, string passwordValue, CancellationToken ct = default(CancellationToken))
        {
            byte[] message = AuthProtoMapper.EncodeLoginLikeRequest(emailOrUsernameValue, passwordValue);
            ApiResult<AuthResponseDto> result = await SendAuthUnaryAsync(ServerConfig.LoginUrl, message, false, "LOGIN", ct);
            SaveTokensIfPresent(result);
            return result;
        }

        //* Gets current user data through RequestManager queue using stored access token.
        public Task<ApiResult<GetUserDataResponseDto>> GetUserDataAsync(CancellationToken ct = default(CancellationToken))
        {
            byte[] message = AuthProtoMapper.EncodeEmptyRequest();
            return SendGetUserDataUnaryAsync(ServerConfig.GetUserDataUrl, message, true, "GET_USER_DATA", ct);
        }

        //* Refreshes tokens using AuthRefreshManager-compatible internal flow.
        public async Task<ApiResult<AuthResponseDto>> RefreshAsync(CancellationToken ct = default(CancellationToken))
        {
            bool ok = await RefreshInternalAsync();
            if (!ok) return ApiResult<AuthResponseDto>.Failure("Refresh failed", 401, false, string.Empty, new byte[0]);

            var dto = new AuthResponseDto
            {
                success = true,
                message = "ok",
                accessToken = SecureTokenStorage.GetAccessToken(),
                refreshToken = SecureTokenStorage.GetRefreshToken(),
                user = CurrentUser
            };

            return ApiResult<AuthResponseDto>.Success(dto, 200, string.Empty, new byte[0]);
        }

        //* Clears local tokens and returns UI to login/register state.
        //* Button entry point. Sends server logout request before clearing local tokens.

        #endregion

        #region  < Logout > 

        //* Button entry point. Sends server logout request before clearing local tokens.
        public void Logout()
        {
            RunSafely(LogoutAsync, "LOGOUT_BUTTON");
        }

        //* Sends Logout request to server and clears local tokens only after successful server response.

        //* Logs out only the current device/session and clears local tokens only after successful server response.
        public async Task LogoutAsync()
        {
            string userId = CurrentUser != null ? CurrentUser.id : string.Empty;
            string refreshToken = SecureTokenStorage.GetRefreshToken();

            NetworkFileLogger.Auth("LOGOUT_START", true, "Current session logout started.", userId, HasAccessToken(), HasRefreshToken());
            ShowInfoMessage("در حال خروج...");

            if (string.IsNullOrEmpty(refreshToken))
            {
                SetStatus("خروج ناموفق بود: refresh token وجود ندارد");
                NetworkFileLogger.Auth("LOGOUT_FAILED", false, "Refresh token is empty.", userId, HasAccessToken(), HasRefreshToken());
                return;
            }

            byte[] message = AuthProtoMapper.EncodeLogoutRequest(refreshToken);
            ApiResult<AuthResponseDto> res = await SendAuthUnaryAsync(ServerConfig.LogoutUrl, message, true, "LOGOUT", default(CancellationToken));

            if (res == null)
            {
                ShowErrorMessage("خروج انجام نشد. پاسخ سرور دریافت نشد.");
                NetworkFileLogger.Auth("LOGOUT_NULL", false, "Logout result is null.", userId, HasAccessToken(), HasRefreshToken());
                return;
            }

            if (!res.IsSuccess)
            {
                ShowErrorMessage(BuildUserMessage("LOGOUT", res));
                NetworkFileLogger.Auth("LOGOUT_FAILED", false, res.ErrorMessage, userId, HasAccessToken(), HasRefreshToken());
                return;
            }

            if (res.Data == null || !res.Data.success)
            {
                string messageText = res.Data != null ? res.Data.message : "Logout response data is null.";
                ShowErrorMessage("خروج انجام نشد. لطفاً دوباره تلاش کنید.");
                NetworkFileLogger.Auth("LOGOUT_FAILED", false, messageText, userId, HasAccessToken(), HasRefreshToken());
                return;
            }

            SecureTokenStorage.ClearTokens();
            isLogin = false;
            CurrentUser = null;
            DH_Pnl_Login(true);
            ShowSuccessMessage("خروج انجام شد");
            NetworkFileLogger.Auth("LOGOUT_SUCCESS", true, ReadAuthMessage(res), userId, HasAccessToken(), HasRefreshToken());
        }
        //* Button entry point for logging out from all devices.
        public void LogoutAllDevices()
        {
            RunSafely(LogoutAllDevicesAsync, "LOGOUT_ALL_BUTTON");
        }
        //* Logs out the user from all devices and clears local tokens only after successful server response.
        public async Task LogoutAllDevicesAsync()
        {
            string userId = CurrentUser != null ? CurrentUser.id : string.Empty;

            NetworkFileLogger.Auth("LOGOUT_ALL_START", true, "All devices logout started.", userId, HasAccessToken(), HasRefreshToken());
            SetStatus("در حال خروج از همه دستگاه‌ها...");

            byte[] message = AuthProtoMapper.EncodeEmptyRequest();
            ApiResult<AuthResponseDto> res = await SendAuthUnaryAsync(ServerConfig.LogoutAllDevicesUrl, message, true, "LOGOUT_ALL", default(CancellationToken));

            if (res == null)
            {
                SetStatus("خروج از همه دستگاه‌ها ناموفق بود: پاسخ سرور خالی است");
                NetworkFileLogger.Auth("LOGOUT_ALL_NULL", false, "LogoutAllDevices result is null.", userId, HasAccessToken(), HasRefreshToken());
                return;
            }

            if (!res.IsSuccess)
            {
                SetStatus("خروج از همه دستگاه‌ها ناموفق بود: " + res.ErrorMessage);
                NetworkFileLogger.Auth("LOGOUT_ALL_FAILED", false, res.ErrorMessage, userId, HasAccessToken(), HasRefreshToken());
                return;
            }

            if (res.Data == null || !res.Data.success)
            {
                string messageText = res.Data != null ? res.Data.message : "LogoutAllDevices response data is null.";
                SetStatus("خروج از همه دستگاه‌ها ناموفق بود: " + messageText);
                NetworkFileLogger.Auth("LOGOUT_ALL_FAILED", false, messageText, userId, HasAccessToken(), HasRefreshToken());
                return;
            }

            SecureTokenStorage.ClearTokens();
            isLogin = false;
            CurrentUser = null;
            DH_Pnl_Login(true);
            SetStatus("خروج از همه دستگاه‌ها انجام شد");
            NetworkFileLogger.Auth("LOGOUT_ALL_SUCCESS", true, ReadAuthMessage(res), userId, HasAccessToken(), HasRefreshToken());
        }
        #endregion

        #region <Connection Health CheckNet>
        //* Starts health check flow like MindReader.
        public void CheckNet()
        {
            RunSafely(CheckNetAsync, "CHECK_NET");
        }

        //* Checks backend health through selected transport and then chooses Login_Init or Register panel.
        private async Task CheckNetAsync()
        {
            if (isCheckingNet) return;

            EnsureAuthServerConfig();

            isCheckingNet = true;
            NetworkFileLogger.Info("CHECK_NET", "Health check started. transport=" + ServerConfig.CurrentTransportKind);

            try
            {
                ApiResult<byte[]> res;

                if (ServerConfig.IsGrpcNative())
                {
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_ANDROID
                    res = await GrpcNativeUnaryClient.SendAsync(ServerConfig.HealthServiceName, "Check", AuthProtoMapper.EncodeEmptyRequest(), false, null, default(CancellationToken), "HEALTH_NATIVE");
#else
                    res = ApiResult<byte[]>.Failure("Native gRPC is enabled in ServerConfig, but this platform is not enabled for Native health.", 0, true);
#endif
                }
                else
                {
                    byte[] frame = AuthProtoMapper.EncodeGrpcWebUnaryRequest(AuthProtoMapper.EncodeEmptyRequest());
                    res = await RequestManager.Send<byte[]>(ServerConfig.HealthUrl, UnityWebRequest.kHttpVerbPOST, frame, false, BuildGrpcWebHeaders(), default(CancellationToken), "HEALTH");
                }

                if (res != null && res.IsSuccess)
                {
                    hasPing = true;
                    netWarning_Disabled = false;
                    SetStatus("سرور در دسترس است");
                    NetworkFileLogger.Info("CHECK_NET", "Server reachable. transport=" + ServerConfig.CurrentTransportKind);

                    if (autoLoginInitAfterHealth && !IsFirstTimeInstallation()) await Login_Init();
                    else if (showRegisterPanelIfFirstInstall) DH_Pnl_Reg(true);

                    return;
                }

                hasPing = false;

                if (!netWarning_Disabled)
                {
                    netWarning_Disabled = true;
                    ShowErrorMessage("ارتباط با سرور برقرار نشد");
                }

                NetworkFileLogger.Warning("CHECK_NET", "Health check failed. " + (res != null ? res.ErrorMessage : "Result is null."));
            }
            catch (Exception ex)
            {
                hasPing = false;

                if (!netWarning_Disabled)
                {
                    netWarning_Disabled = true;
                    ShowErrorMessage("ارتباط با سرور برقرار نشد");
                }

                NetworkFileLogger.Exception("CHECK_NET", ex);
            }
            finally
            {
                isCheckingNet = false;
            }
        }

        //* Returns true if no refresh token exists and player must register/login manually.
        public bool IsFirstTimeInstallation()
        {
            return string.IsNullOrEmpty(SecureTokenStorage.GetRefreshToken());
        }

        //* این تابع خطای منقضی شدن یا نامعتبر بودن آث را برای مسیر وب‌جی‌آر‌پی‌سی و جی‌آر‌پی‌سی نیتیو یکسان تشخیص می دهد.
        private bool IsUnauthorizedOrExpired(int statusCode, string errorMessage)
        {
            if (statusCode == 401) return true;
            if (statusCode == 16) return true;

            string message = errorMessage ?? string.Empty;
            return message.IndexOf("expired", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("unauth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("Authentication failed", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        #endregion

        #region <Panels>
        //* Shows or hides register panel.
        public void DH_Pnl_Reg(bool state)
        {
            if (pnl_Reg != null) pnl_Reg.SetActive(state);
        }

        //* Shows or hides login panel.
        public void DH_Pnl_Login(bool state)
        {
            if (pnl_Login != null) pnl_Login.SetActive(state);
        }

        public void DH_Pnl_ServerDebug(bool state)
        {
            if (pnl_ServerDebug != null) pnl_ServerDebug.SetActive(state);
        }

        private void SetupServerDebugPanel()
        {
            if (btn_ServerDebugClose != null)
            {
                btn_ServerDebugClose.onClick.RemoveListener(HideServerDebugPanel);
                btn_ServerDebugClose.onClick.AddListener(HideServerDebugPanel);
            }

            DH_Pnl_ServerDebug(false);
        }

        private void HideServerDebugPanel()
        {
            DH_Pnl_ServerDebug(false);
        }
        #endregion

        #region <Server Type>
        //* Applies selected server endpoint and transport to ServerConfig.
        public void ApplyServerType()
        {
            ApplyDefaultTransportForPlatform();

            switch (serverType)
            {
                case ServerType.Local:
                    if (ServerConfig.IsGrpcNative()) ServerConfig.UseLocalGrpcNative();
                    else ServerConfig.UseLocalGrpcWeb();

                    Debug.Log("Server Mode : Local");
                    NetworkFileLogger.Info("SERVER_CONFIG", "Server Mode : Local | transport=" + ServerConfig.CurrentTransportKind + " endpoint=" + ServerConfig.CurrentEndpoint.ToString());
                    break;

                case ServerType.Dedicated:
                    if (ServerConfig.IsGrpcNative())
                    {
                        ServerConfig.UseGrpcNativeEndpoint(new Endpoint(nativeDedicatedHost, nativeDedicatedPort, nativeDedicatedUseTls));
                    }
                    else
                    {
                        ServerConfig.UseGrpcWebEndpoint(new Endpoint(dedicatedHost, dedicatedPort, dedicatedUseTls));
                    }

                    Debug.Log("Server Mode : Dedicated");
                    NetworkFileLogger.Info("SERVER_CONFIG", "Server Mode : Dedicated | transport=" + ServerConfig.CurrentTransportKind + " endpoint=" + ServerConfig.CurrentEndpoint.ToString());
                    break;

                case ServerType.Custom:
                    ServerConfig.UseEndpoint(new Endpoint(customHost, customPort, customUseTls));
                    Debug.Log("Server Mode : Custom");
                    NetworkFileLogger.Info("SERVER_CONFIG", "Server Mode : Custom | transport=" + ServerConfig.CurrentTransportKind + " endpoint=" + ServerConfig.CurrentEndpoint.ToString());
                    break;
            }
        }

        //* این تابع قبل از هر درخواست آث، مسیر آث را دوباره از روی تنظیمات همین آث‌منیجر قفل می کند تا اسکریپت های دیگر آن را تغییر ندهند.
        private void EnsureAuthServerConfig()
        {
            ApplyServerType();

            NetworkFileLogger.Info(
                "AUTH_CONFIG_LOCK",
                "Auth config locked | transport=" + ServerConfig.CurrentTransportKind +
                " endpoint=" + ServerConfig.CurrentEndpoint.ToString()
            );
        }
        //* Selects the default transport for the current Unity platform.
        //* این تابع ترنسپورت آث را بر اساس پلتفرم انتخاب می کند و در ادیتور فقط با گزینه تستی به جی‌آر‌پی‌سی نیتیو می رود.
        //* این تابع ترنسپورت آث را بر اساس پلتفرم انتخاب می کند و در ادیتور فقط با گزینه تستی به جی‌آر‌پی‌سی نیتیو می رود.
        private void ApplyDefaultTransportForPlatform()
        {
#if UNITY_EDITOR
            NetworkFileLogger.Info("AUTH_TRANSPORT_SELECT", "UNITY_EDITOR | editorUseNativeGrpcAuth=" + editorUseNativeGrpcAuth);
#endif
#if UNITY_WEBGL && !UNITY_EDITOR
    ServerConfig.UseTransport(TransportKind.GrpcWeb);
#elif UNITY_EDITOR
            ServerConfig.UseTransport(editorUseNativeGrpcAuth ? TransportKind.GrpcNative : TransportKind.GrpcWeb);
#elif UNITY_STANDALONE_WIN || UNITY_ANDROID
    ServerConfig.UseTransport(TransportKind.GrpcNative);
#else
    ServerConfig.UseTransport(TransportKind.GrpcWeb);
#endif
        }
        #endregion

        #region <Internal gRPC Send>
        //* Sends an AuthReply unary request through the selected transport and decodes it internally.
        private async Task<ApiResult<AuthResponseDto>> SendAuthUnaryAsync(string url, byte[] protoMessage, bool auth, string logTag, CancellationToken ct)
        {
            EnsureAuthServerConfig();

            if (ServerConfig.IsGrpcNative())
            {
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_ANDROID
                string methodName = GetNativeAuthMethodName(logTag);
                ApiResult<byte[]> raw = await GrpcNativeUnaryClient.SendAsync(ServerConfig.ServiceName, methodName, protoMessage, auth, null, ct, logTag + "_NATIVE");

                if (!raw.IsSuccess) return ApiResult<AuthResponseDto>.Failure(raw.ErrorMessage, raw.StatusCode, raw.IsNetworkError, raw.RawBody, raw.RawBytes);

                byte[] message = ReadNativeBytes(raw);
                AuthResponseDto dto = AuthProtoMapper.DecodeAuthResponse(message);

                return ApiResult<AuthResponseDto>.Success(dto, raw.StatusCode, raw.RawBody, raw.RawBytes);
#else
                return ApiResult<AuthResponseDto>.Failure("Native gRPC is enabled in ServerConfig, but this platform is not enabled for Native auth.", 0, true);
#endif
            }

            byte[] frame = AuthProtoMapper.EncodeGrpcWebUnaryRequest(protoMessage);
            ApiResult<byte[]> webRaw = await RequestManager.Send<byte[]>(url, UnityWebRequest.kHttpVerbPOST, frame, auth, BuildGrpcWebHeaders(), ct, logTag);

            if (!webRaw.IsSuccess) return BuildAuthFailureFromWebRaw(webRaw);

            byte[] webMessage;
            Dictionary<string, string> trailers;

            if (!AuthProtoMapper.TryDecodeGrpcWebUnaryResponse(webRaw.RawBytes, out webMessage, out trailers)) return ApiResult<AuthResponseDto>.Failure("Invalid gRPC-Web response", webRaw.StatusCode, false, webRaw.RawBody, webRaw.RawBytes);

            string grpcStatus = ReadTrailer(trailers, "grpc-status");
            if (!string.IsNullOrEmpty(grpcStatus) && grpcStatus != "0") return BuildAuthFailureFromGrpcTrailers(webRaw, trailers);

            AuthResponseDto webDto = AuthProtoMapper.DecodeAuthResponse(webMessage);
            return ApiResult<AuthResponseDto>.Success(webDto, webRaw.StatusCode, webRaw.RawBody, webRaw.RawBytes);
        }

        //* Sends a GetUserData unary request through the selected transport and decodes it internally.
        private async Task<ApiResult<GetUserDataResponseDto>> SendGetUserDataUnaryAsync(string url, byte[] protoMessage, bool auth, string logTag, CancellationToken ct)
        {
            EnsureAuthServerConfig();

            if (ServerConfig.IsGrpcNative())
            {
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_ANDROID
                ApiResult<byte[]> raw = await GrpcNativeUnaryClient.SendAsync(ServerConfig.ServiceName, "GetUserData", protoMessage, auth, null, ct, logTag + "_NATIVE");

                if (!raw.IsSuccess) return ApiResult<GetUserDataResponseDto>.Failure(raw.ErrorMessage, raw.StatusCode, raw.IsNetworkError, raw.RawBody, raw.RawBytes);

                byte[] message = ReadNativeBytes(raw);
                GetUserDataResponseDto dto = AuthProtoMapper.DecodeGetUserDataResponse(message);

                return ApiResult<GetUserDataResponseDto>.Success(dto, raw.StatusCode, raw.RawBody, raw.RawBytes);
#else
                return ApiResult<GetUserDataResponseDto>.Failure("Native gRPC is enabled in ServerConfig, but this platform is not enabled for Native GetUserData.", 0, true);
#endif
            }

            byte[] frame = AuthProtoMapper.EncodeGrpcWebUnaryRequest(protoMessage);
            ApiResult<byte[]> webRaw = await RequestManager.Send<byte[]>(url, UnityWebRequest.kHttpVerbPOST, frame, auth, BuildGrpcWebHeaders(), ct, logTag);

            if (!webRaw.IsSuccess) return ApiResult<GetUserDataResponseDto>.Failure(webRaw.ErrorMessage, webRaw.StatusCode, webRaw.IsNetworkError, webRaw.RawBody, webRaw.RawBytes);

            byte[] webMessage;
            Dictionary<string, string> trailers;

            if (!AuthProtoMapper.TryDecodeGrpcWebUnaryResponse(webRaw.RawBytes, out webMessage, out trailers)) return ApiResult<GetUserDataResponseDto>.Failure("Invalid gRPC-Web response", webRaw.StatusCode, false, webRaw.RawBody, webRaw.RawBytes);

            string grpcStatus = ReadTrailer(trailers, "grpc-status");
            if (!string.IsNullOrEmpty(grpcStatus) && grpcStatus != "0") return ApiResult<GetUserDataResponseDto>.Failure(ReadTrailer(trailers, "grpc-message"), webRaw.StatusCode, false, webRaw.RawBody, webRaw.RawBytes);

            GetUserDataResponseDto webDto = AuthProtoMapper.DecodeGetUserDataResponse(webMessage);
            return ApiResult<GetUserDataResponseDto>.Success(webDto, webRaw.StatusCode, webRaw.RawBody, webRaw.RawBytes);
        }

        //* Refreshes token without entering the main RequestManager queue.
        async Task<bool> RefreshInternalAsync()
        {
            EnsureAuthServerConfig();

            string refreshToken = SecureTokenStorage.GetRefreshToken();
            NetworkFileLogger.TokenState("REFRESH_BEFORE_SEND", SecureTokenStorage.GetAccessToken(), refreshToken);

            if (string.IsNullOrEmpty(refreshToken))
            {
                NetworkFileLogger.Warning("REFRESH", "Refresh token is empty.");
                return false;
            }

            byte[] message = AuthProtoMapper.EncodeRefreshRequest(refreshToken);

            if (ServerConfig.IsGrpcNative())
            {
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_ANDROID
                ApiResult<byte[]> raw = await GrpcNativeUnaryClient.SendAsync(
                    ServerConfig.ServiceName,
                    "Refresh",
                    message,
                    false,
                    null,
                    new CancellationTokenSource(TimeSpan.FromSeconds(ServerConfig.TimeoutSeconds)).Token,
                    "REFRESH_NATIVE"
                );

                if (raw == null)
                {
                    NetworkFileLogger.Warning("REFRESH", "Native refresh result is null.");
                    return false;
                }

                if (!raw.IsSuccess)
                {
                    NetworkFileLogger.Warning("REFRESH", "Native refresh failed. status=" + raw.StatusCode + " error=" + raw.ErrorMessage);
                    return false;
                }

                byte[] protoBytes = ReadNativeBytes(raw);
                AuthResponseDto dto = AuthProtoMapper.DecodeAuthResponse(protoBytes);

                return SaveRefreshDto(dto, refreshToken);
#else
                NetworkFileLogger.Warning("REFRESH", "Native gRPC is enabled in ServerConfig, but this platform is not enabled for Native refresh.");
                return false;
#endif
            }

            byte[] frame = AuthProtoMapper.EncodeGrpcWebUnaryRequest(message);

            using (UnityWebRequest req = new UnityWebRequest(ServerConfig.RefreshUrl, UnityWebRequest.kHttpVerbPOST))
            {
                req.timeout = ServerConfig.TimeoutSeconds;
                req.downloadHandler = new DownloadHandlerBuffer();
                req.uploadHandler = new UploadHandlerRaw(frame);

                foreach (var pair in BuildGrpcWebHeaders()) req.SetRequestHeader(pair.Key, pair.Value);

                try
                {
                    NetworkFileLogger.Request("refresh", "START", ServerConfig.RefreshUrl, "POST", 0, "grpc_web_direct_not_queued frameBytes=" + frame.Length);
                    await UnityWebRequestAsync.SendAsync(req, new CancellationTokenSource(TimeSpan.FromSeconds(ServerConfig.TimeoutSeconds)).Token);
                }
                catch (Exception ex)
                {
                    NetworkFileLogger.Exception("REFRESH", ex);
                    return false;
                }

                byte[] rawBytes = req.downloadHandler != null && req.downloadHandler.data != null ? req.downloadHandler.data : new byte[0];
                string bodyText = req.downloadHandler != null ? (req.downloadHandler.text ?? string.Empty) : string.Empty;

                NetworkFileLogger.Request(
                    "refresh",
                    "RESPONSE",
                    ServerConfig.RefreshUrl,
                    "POST",
                    (int)req.responseCode,
                    "result=" + req.result + " error=" + req.error + " textLength=" + bodyText.Length + " bytes=" + rawBytes.Length
                );

                if (req.result != UnityWebRequest.Result.Success)
                {
                    NetworkFileLogger.Warning("REFRESH", "UnityWebRequest failed. status=" + req.responseCode + " result=" + req.result + " error=" + req.error + " body=" + bodyText);
                    return false;
                }

                if (rawBytes.Length == 0)
                {
                    NetworkFileLogger.Warning("REFRESH", "Empty gRPC-Web refresh response. status=" + req.responseCode + " body=" + bodyText);
                    return false;
                }

                byte[] protoBytes;
                Dictionary<string, string> trailers;

                if (!AuthProtoMapper.TryDecodeGrpcWebUnaryResponse(rawBytes, out protoBytes, out trailers))
                {
                    NetworkFileLogger.Warning("REFRESH", "Invalid gRPC-Web refresh response. rawBytes=" + rawBytes.Length + " body=" + bodyText);
                    return false;
                }

                string grpcStatus = ReadTrailer(trailers, "grpc-status");
                string grpcMessage = ReadTrailer(trailers, "grpc-message");

                if (!string.IsNullOrEmpty(grpcStatus) && grpcStatus != "0")
                {
                    NetworkFileLogger.Warning("REFRESH", "gRPC refresh failed. grpcStatus=" + grpcStatus + " grpcMessage=" + grpcMessage);
                    return false;
                }

                AuthResponseDto dto = AuthProtoMapper.DecodeAuthResponse(protoBytes);
                return SaveRefreshDto(dto, refreshToken);
            }
        }
        //* Reads response bytes from native ApiResult.
        private byte[] ReadNativeBytes(ApiResult<byte[]> raw)
        {
            if (raw == null) return new byte[0];
            if (raw.Data != null && raw.Data.Length > 0) return raw.Data;
            return raw.RawBytes ?? new byte[0];
        }

        //* Maps current auth log tag to native gRPC method name.
        private string GetNativeAuthMethodName(string logTag)
        {
            if (logTag == "REGISTER") return "Register";
            if (logTag == "LOGIN") return "Login";
            if (logTag == "LOGOUT") return "Logout";
            if (logTag == "LOGOUT_ALL") return "LogoutAllDevices";
            if (logTag == "REFRESH") return "Refresh";
            return logTag;
        }

        //* Validates refresh response and stores returned tokens.
        private bool SaveRefreshDto(AuthResponseDto dto, string previousRefreshToken)
        {
            if (dto == null)
            {
                NetworkFileLogger.Warning("REFRESH", "Decoded refresh dto is null.");
                return false;
            }

            if (!dto.success)
            {
                NetworkFileLogger.Warning("REFRESH", "Refresh dto success=false message=" + dto.message);
                return false;
            }

            if (string.IsNullOrEmpty(dto.accessToken))
            {
                NetworkFileLogger.Warning("REFRESH", "Refresh dto accessToken is empty. message=" + dto.message);
                return false;
            }

            SecureTokenStorage.SaveTokens(dto.accessToken, string.IsNullOrEmpty(dto.refreshToken) ? previousRefreshToken : dto.refreshToken);

            NetworkFileLogger.TokenState("REFRESH_SUCCESS", SecureTokenStorage.GetAccessToken(), SecureTokenStorage.GetRefreshToken());

            return true;
        }
        //* Builds gRPC-Web headers for Envoy.
        private Dictionary<string, string> BuildGrpcWebHeaders()
        {
            return new Dictionary<string, string>
            {
                { "Content-Type", "application/grpc-web+proto" },
                { "Accept", "application/grpc-web+proto" },
                { "X-Grpc-Web", "1" },
                { "X-User-Agent", "grpc-web-unity" },
                { "X-Metaverse-Client", Application.platform.ToString() },
                { "X-Metaverse-Version", Application.version }
            };
        }
        #endregion

        #region <Helpers>

        private void UpdateServerDebugText(string stage, string detail)
        {
            if (txt_ServerDebug == null) return;

            txt_ServerDebug.text = BuildServerDebugText(stage, detail);

            if (showServerDebugPanelOnServerMessage) DH_Pnl_ServerDebug(true);
        }

        private string BuildServerDebugText(string stage, string detail)
        {
            string userId = CurrentUser != null ? SafeText(CurrentUser.id) : "-";
            string userName = CurrentUser != null ? SafeText(CurrentUser.emailOrUsername) : "-";
            string createdAtUnix = CurrentUser != null ? CurrentUser.createdAtUnix.ToString() : "-";

            return
                "گزارش سرور\n" +
                "------------------------------\n" +
                RtlLine("مرحله", stage) +
                RtlLine("پیام", detail) +
                "\n" +

                "اتصال\n" +
                RtlLine("حالت سرور", serverType.ToString()) +
                RtlLine("نوع ارتباط", ServerConfig.CurrentTransportKind.ToString()) +
                RtlLine("آدرس سرور", ServerConfig.CurrentEndpoint.ToString()) +
                RtlLine("وضعیت سلامت", hasPing ? "Connected" : "Not Connected") +
                "\n" +

                "ورود کاربر\n" +
                RtlLine("وارد شده", isLogin ? "Yes" : "No") +
                RtlLine("اکسس توکن", HasAccessToken() ? "Exists" : "Empty") +
                RtlLine("رفرش توکن", HasRefreshToken() ? "Exists" : "Empty") +
                "\n" +

                "کاربر دریافت شده از سرور\n" +
                RtlLine("شناسه", userId) +
                RtlLine("نام کاربر", userName) +
                RtlLine("تاریخ ساخت یونیکس", createdAtUnix) +
                "\n" +

                RtlLine("زمان کلاینت", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        private string Ltr(string value)
        {
            if (string.IsNullOrEmpty(value)) return "-";
            return "\u200E" + value + "\u200E";
        }

        private string RtlLine(string label, string value)
        {
            return label + ": " + Ltr(value) + "\n";
        }

        private string SafeText(string value)
        {
            return string.IsNullOrEmpty(value) ? "-" : value;
        }

        //* Stores access and refresh token if the response contains them.
        private void SaveTokensIfPresent(ApiResult<AuthResponseDto> result)
        {

            if (result == null || !result.IsSuccess || result.Data == null) return;
            if (string.IsNullOrEmpty(result.Data.accessToken)) return;
            SecureTokenStorage.SaveTokens(result.Data.accessToken, result.Data.refreshToken);


            NetworkFileLogger.TokenState("SAVE_TOKENS", SecureTokenStorage.GetAccessToken(), SecureTokenStorage.GetRefreshToken());
        }

        //* Runs async UI flows safely from button methods.
        private async void RunSafely(Func<Task> action, string tag)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception(tag, ex);
                SetStatus("خطای غیرمنتظره: " + ex.Message);
                SetButton(btn_Reg, true);
                SetButton(btn_Log, true);
            }
        }

        //* Validates user input before sending auth request.
        private bool ValidateCredentials(string user, string pass, string stage)
        {
            string stageKey = string.IsNullOrEmpty(stage) ? string.Empty : stage.ToUpperInvariant();
            string actionName = stageKey == "REGISTER" ? "ثبت نام" : "ورود";

            if (string.IsNullOrWhiteSpace(user))
            {
                ShowWarningMessage("نام کاربری وارد نشده است");
                NetworkFileLogger.Warning(stage, "Username is empty before " + actionName + ".");
                return false;
            }

            if (user.Trim().Length < minUsernameLength)
            {
                ShowWarningMessage("نام کاربری باید حداقل " + minUsernameLength + " کاراکتر باشد");
                NetworkFileLogger.Warning(stage, "Username length is less than " + minUsernameLength + " before " + actionName + ".");
                return false;
            }

            if (string.IsNullOrEmpty(pass))
            {
                ShowWarningMessage("رمز عبور وارد نشده است");
                NetworkFileLogger.Warning(stage, "Password is empty before " + actionName + ".");
                return false;
            }

            if (pass.Length < minPasswordLength)
            {
                ShowWarningMessage("رمز عبور باید حداقل " + minPasswordLength + " کاراکتر باشد");
                NetworkFileLogger.Warning(stage, "Password length is less than " + minPasswordLength + " before " + actionName + ".");
                return false;
            }

            return true;
        }

        //* Shows login panel when token refresh fails.
        private void ShowLoginRequired()
        {
            DH_Pnl_Login(true);
            ShowWarningMessage("ورود شما منقضی شده است. لطفاً دوباره وارد شوید.");
        }

        //* Reads TMP input safely.
        private string ReadInput(TMP_InputField input, bool trim = true)
        {
            if (input == null) return string.Empty;
            return trim ? input.text.Trim() : input.text;
        }

        //* Changes button interactable safely.
        private void SetButton(Button button, bool value)
        {
            if (button != null) button.interactable = value;
        }

        //* Updates status text and log.
        private void SetStatus(string message)
        {
            if (txt_Status != null) txt_Status.text = message;
            Debug.Log("[Network_A.AuthManager] " + message);
            NetworkFileLogger.Info("AUTH_STATUS", message);
        }

        //* Reads gRPC trailer dictionary safely.
        private string ReadTrailer(Dictionary<string, string> trailers, string key)
        {
            if (trailers == null || string.IsNullOrEmpty(key)) return string.Empty;
            string value;
            if (trailers.TryGetValue(key, out value)) return value;
            return trailers.TryGetValue(key.ToLowerInvariant(), out value) ? value : string.Empty;
        }

        //* Builds an auth failure from a gRPC-Web raw response and preserves server error codes.
        private ApiResult<AuthResponseDto> BuildAuthFailureFromWebRaw(ApiResult<byte[]> webRaw)
        {
            if (webRaw == null) return ApiResult<AuthResponseDto>.Failure("Unknown auth error", 0, true, string.Empty, new byte[0]);

            string errorMessage = ExtractGrpcWebAuthErrorMessage(webRaw);
            if (string.IsNullOrEmpty(errorMessage)) errorMessage = webRaw.ErrorMessage;

            return ApiResult<AuthResponseDto>.Failure(errorMessage, webRaw.StatusCode, webRaw.IsNetworkError, webRaw.RawBody, webRaw.RawBytes);
        }

        //* Builds an auth failure from decoded gRPC-Web trailers.
        private ApiResult<AuthResponseDto> BuildAuthFailureFromGrpcTrailers(ApiResult<byte[]> webRaw, Dictionary<string, string> trailers)
        {
            string grpcMessage = DecodeGrpcErrorMessage(ReadTrailer(trailers, "grpc-message"));
            string grpcStatus = ReadTrailer(trailers, "grpc-status");
            string knownError = FindKnownAuthErrorToken(grpcMessage);

            if (!string.IsNullOrEmpty(knownError)) grpcMessage = knownError;
            if (string.IsNullOrEmpty(grpcMessage)) grpcMessage = "GRPC_STATUS_" + grpcStatus;

            return ApiResult<AuthResponseDto>.Failure(grpcMessage, webRaw.StatusCode, false, webRaw.RawBody, webRaw.RawBytes);
        }

        //* Extracts a useful auth error from gRPC-Web bytes, body, and ErrorMessage.
        private string ExtractGrpcWebAuthErrorMessage(ApiResult<byte[]> webRaw)
        {
            if (webRaw == null) return string.Empty;

            string knownError = FindKnownAuthErrorToken(webRaw.ErrorMessage, webRaw.RawBody, ReadKnownAuthErrorFromBytes(webRaw.RawBytes));
            if (!string.IsNullOrEmpty(knownError)) return knownError;

            if (webRaw.RawBytes != null && webRaw.RawBytes.Length > 0)
            {
                byte[] messageBytes;
                Dictionary<string, string> trailers;

                if (AuthProtoMapper.TryDecodeGrpcWebUnaryResponse(webRaw.RawBytes, out messageBytes, out trailers))
                {
                    string grpcMessage = DecodeGrpcErrorMessage(ReadTrailer(trailers, "grpc-message"));
                    string grpcStatus = ReadTrailer(trailers, "grpc-status");
                    string trailerKnownError = FindKnownAuthErrorToken(grpcMessage);

                    if (!string.IsNullOrEmpty(trailerKnownError)) return trailerKnownError;
                    if (!string.IsNullOrEmpty(grpcMessage)) return grpcMessage;
                    if (!string.IsNullOrEmpty(grpcStatus) && grpcStatus != "0") return "GRPC_STATUS_" + grpcStatus;
                }
            }

            if (!string.IsNullOrEmpty(webRaw.RawBody)) return webRaw.RawBody;
            return webRaw.ErrorMessage;
        }

        //* Reads auth message safely.
        private string ReadAuthMessage(ApiResult<AuthResponseDto> result)
        {
            if (result == null) return "null";
            if (result.Data != null && !string.IsNullOrEmpty(result.Data.message)) return result.Data.message;
            return result.ErrorMessage;
        }

        //* Combines error fields so UI mapping can see gRPC-Web trailer errors.
        private string CombineErrorParts(params string[] parts)
        {
            if (parts == null || parts.Length == 0) return string.Empty;

            string combined = string.Empty;

            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;
                if (!string.IsNullOrEmpty(combined)) combined += " | ";
                combined += parts[i];
            }

            return combined;
        }

        //* Reads known auth error tokens from raw bytes when gRPC-Web puts them in trailers.
        private string ReadKnownAuthErrorFromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;

            try
            {
                string text = Encoding.UTF8.GetString(bytes);
                return FindKnownAuthErrorToken(text);
            }
            catch
            {
                return string.Empty;
            }
        }

        //* Finds server auth error tokens in any received text.
        private string FindKnownAuthErrorToken(params string[] values)
        {
            if (values == null || values.Length == 0) return string.Empty;

            for (int i = 0; i < values.Length; i++)
            {
                string value = DecodeGrpcErrorMessage(values[i]);
                if (string.IsNullOrEmpty(value)) continue;

                string upper = value.ToUpperInvariant();

                if (upper.Contains("EMAIL_ALREADY_EXISTS")) return "EMAIL_ALREADY_EXISTS";
                if (upper.Contains("USERNAME_ALREADY_EXISTS")) return "USERNAME_ALREADY_EXISTS";
                if (upper.Contains("INVALID_CREDENTIALS")) return "INVALID_CREDENTIALS";
                if (upper.Contains("TOKEN_EXPIRED")) return "TOKEN_EXPIRED";
                if (upper.Contains("AUTHENTICATION_FAILED")) return "AUTHENTICATION_FAILED";
            }

            return string.Empty;
        }

        //* Decodes gRPC-Web error text so mapped messages work with encoded trailers.
        private string DecodeGrpcErrorMessage(string rawError)
        {
            if (string.IsNullOrEmpty(rawError)) return string.Empty;

            string decoded = rawError.Replace("+", " ");

            try
            {
                decoded = UnityWebRequest.UnEscapeURL(decoded);
            }
            catch
            {
                decoded = rawError;
            }

            return decoded;
        }

        //* Returns true if access token exists.
        private bool HasAccessToken()
        {
            return !string.IsNullOrEmpty(SecureTokenStorage.GetAccessToken());
        }

        //* Returns true if refresh token exists.
        private bool HasRefreshToken()
        {
            return !string.IsNullOrEmpty(SecureTokenStorage.GetRefreshToken());
        }
        //* Clears local tokens safely at startup before network check.
        private async Task<bool> ClearTokensOnStartAsync()
        {
            try
            {
                NetworkFileLogger.Info("AUTH_MANAGER", "ClearTokensOnStart started.");
                await Task.Yield();

                SecureTokenStorage.ClearTokens();

                await Task.Yield();
                NetworkFileLogger.TokenState("CLEAR_TOKENS_ON_START_DONE", SecureTokenStorage.GetAccessToken(), SecureTokenStorage.GetRefreshToken());

                return true;
            }
            catch (Exception ex)
            {
                NetworkFileLogger.Exception("CLEAR_TOKENS_ON_START", ex);
                return false;
            }
        }
        #region < Message Status > 

        //* Shows an info message in status text and main menu message popup.
        private void ShowInfoMessage(string message)
        {
            SetStatus(message);
            UpdateServerDebugText("Info", message);
            MainMenuMessageManager.Info(message);
        }

        //* Shows a success message in status text and main menu message popup.
        private void ShowSuccessMessage(string message)
        {
            SetStatus(message);
            UpdateServerDebugText("Success", message);
            MainMenuMessageManager.Success(message);
        }

        //* Shows a warning message in status text and main menu message popup.
        private void ShowWarningMessage(string message)
        {
            SetStatus(message);
            UpdateServerDebugText("Warning", message);
            MainMenuMessageManager.Warning(message);
        }

        //* Shows an error message in status text and main menu message popup.
        private void ShowErrorMessage(string message)
        {
            SetStatus(message);
            UpdateServerDebugText("Error", message);
            MainMenuMessageManager.Error(message);
        }

        //* Converts technical auth/network errors to user-friendly Persian messages.
        private string BuildUserMessage(string stage, ApiResult<AuthResponseDto> result)
        {
            string raw = result != null ? CombineErrorParts(result.ErrorMessage, result.RawBody, ReadKnownAuthErrorFromBytes(result.RawBytes)) : string.Empty;
            int status = result != null ? result.StatusCode : 0;
            return BuildUserMessage(stage, raw, status);
        }

        //* Converts technical user-data errors to user-friendly Persian messages.
        private string BuildUserMessage(string stage, ApiResult<GetUserDataResponseDto> result)
        {
            string raw = result != null ? CombineErrorParts(result.ErrorMessage, result.RawBody, ReadKnownAuthErrorFromBytes(result.RawBytes)) : string.Empty;
            int status = result != null ? result.StatusCode : 0;
            return BuildUserMessage(stage, raw, status);
        }

        //* Maps raw error text and status code to a clean UI message.
        private string BuildUserMessage(string stage, string rawError, int statusCode)
        {
            string stageKey = string.IsNullOrEmpty(stage) ? string.Empty : stage.ToUpperInvariant();
            string decodedError = DecodeGrpcErrorMessage(rawError);
            string error = string.IsNullOrEmpty(decodedError) ? string.Empty : decodedError.ToLowerInvariant();

            if (statusCode == 0) return "ارتباط با سرور برقرار نشد. لطفاً اتصال اینترنت را بررسی کنید.";

            if (stageKey == "REGISTER" && error.Contains("email_already_exists")) return "این نام کاربری قبلاً ثبت شده است.";

            if (stageKey == "REGISTER")
            {
                if (error.Contains("username_already_exists")) return "این نام کاربری قبلاً ثبت شده است.";
                if (error.Contains("username already exists")) return "این نام کاربری قبلاً ثبت شده است.";
                if (error.Contains("user name already exists")) return "این نام کاربری قبلاً ثبت شده است.";
                if (error.Contains("email_already_exists")) return "این نام کاربری قبلاً ثبت شده است.";
                if (error.Contains("email already exists")) return "این نام کاربری قبلاً ثبت شده است.";
                if (error.Contains("user already exists")) return "این نام کاربری قبلاً ثبت شده است.";
                if (statusCode == 6) return "این نام کاربری قبلاً ثبت شده است.";
            }

            if (error.Contains("username_already_exists")) return "این نام کاربری قبلاً ثبت شده است.";
            if (error.Contains("username already exists")) return "این نام کاربری قبلاً ثبت شده است.";
            if (error.Contains("user name already exists")) return "این نام کاربری قبلاً ثبت شده است.";
            if (error.Contains("email_already_exists")) return "این ایمیل قبلاً ثبت شده است.";
            if (error.Contains("email already exists")) return "این ایمیل قبلاً ثبت شده است.";
            if (error.Contains("invalid credentials")) return "نام کاربری یا رمز عبور اشتباه است.";
            if (error.Contains("jwt expired") || error.Contains("access token expired")) return "نشست شما منقضی شده است.";
            if (error.Contains("refresh token revoked")) return "ورود شما منقضی شده است. لطفاً دوباره وارد شوید.";
            if (error.Contains("refresh token") && error.Contains("required")) return "اطلاعات ورود کامل نیست. لطفاً دوباره وارد شوید.";
            if (error.Contains("missing authorization") || error.Contains("missing access token")) return "برای ادامه باید وارد حساب کاربری شوید.";
            if (error.Contains("deadline") || error.Contains("timeout")) return "پاسخ سرور بیش از حد طول کشید. لطفاً دوباره تلاش کنید.";
            if (error.Contains("cannot connect") || error.Contains("failed to connect")) return "اتصال به سرور برقرار نشد.";

            if (stageKey == "REGISTER") return "ثبت نام انجام نشد. لطفاً دوباره تلاش کنید.";
            if (stageKey == "LOGIN") return "ورود انجام نشد. لطفاً دوباره تلاش کنید.";
            if (stageKey == "LOGIN_INIT") return "دریافت اطلاعات کاربر انجام نشد.";
            if (stageKey == "LOGOUT") return "خروج انجام نشد. لطفاً دوباره تلاش کنید.";
            if (stageKey == "REFRESH") return "تمدید ورود انجام نشد. لطفاً دوباره وارد شوید.";

            return "عملیات انجام نشد. لطفاً دوباره تلاش کنید.";
        }




        #endregion

        #endregion
    }
}
