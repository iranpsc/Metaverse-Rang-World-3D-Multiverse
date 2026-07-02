using System;
using System.Collections;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Core;
using UnityEngine;

namespace Network_A.Tests
{
    [DefaultExecutionOrder(100)]
    public class GrpcAuthSmoke_A : MonoBehaviour
    {
        [Header("Test Credentials")]
        public string User = "test_user_unity_9001";
        public string Pass = "1234";

        [Header("Options")]
        public bool RunOnStart = true;
        public bool ClearTokensBeforeRun = true;
        public bool RunRefreshTest = true;

        private bool hasStarted;

        //* Logs early component lifetime information.
        private void Awake()
        {
            Debug.Log("[Network_A Smoke] Awake called.");
            NetworkFileLogger.Info("SMOKE", "Awake called. gameObjectActive=" + gameObject.activeInHierarchy + " componentEnabled=" + enabled);
        }

        //* Logs component enable state.
        private void OnEnable()
        {
            Debug.Log("[Network_A Smoke] OnEnable called.");
            NetworkFileLogger.Info("SMOKE", "OnEnable called. runOnStart=" + RunOnStart);
        }

        //* Starts the smoke test through a coroutine wrapper so exceptions are not swallowed by async void.
        private void Start()
        {
            Debug.Log("[Network_A Smoke] Start called. RunOnStart=" + RunOnStart);
            NetworkFileLogger.Info("SMOKE", "Start called. RunOnStart=" + RunOnStart);

            if (!RunOnStart)
            {
                Debug.LogWarning("[Network_A Smoke] RunOnStart is disabled.");
                NetworkFileLogger.Warning("SMOKE", "RunOnStart is disabled. Smoke test will not run automatically.");
                return;
            }

            if (hasStarted)
            {
                Debug.LogWarning("[Network_A Smoke] Smoke test already started.");
                NetworkFileLogger.Warning("SMOKE", "Smoke test already started. Duplicate Start ignored.");
                return;
            }

            hasStarted = true;
            StartCoroutine(StartSmokeRoutine());
        }

        //* Waits one frame, validates dependencies, then runs the async smoke test safely.
        private IEnumerator StartSmokeRoutine()
        {
            Debug.Log("[Network_A Smoke] StartSmokeRoutine entered.");
            NetworkFileLogger.Info("SMOKE", "StartSmokeRoutine entered.");

            yield return null;

            if (AuthManager.Instance == null)
            {
                Debug.LogError("[Network_A Smoke] AuthManager.Instance is null after one frame.");
                NetworkFileLogger.Error("SMOKE", "AuthManager.Instance is null after one frame.");
                yield break;
            }

            Debug.Log("[Network_A Smoke] AuthManager found. Running smoke test.");
            NetworkFileLogger.Info("SMOKE", "AuthManager found. Running smoke test.");

            Task task = RunSmoke();

            while (!task.IsCompleted) yield return null;

            if (task.IsFaulted)
            {
                Exception exception = task.Exception != null ? task.Exception.GetBaseException() : null;
                Debug.LogError("[Network_A Smoke] RunSmoke failed: " + (exception != null ? exception.Message : "Unknown error"));
                NetworkFileLogger.Exception("SMOKE", exception);
                yield break;
            }

            Debug.Log("[Network_A Smoke] StartSmokeRoutine completed.");
            NetworkFileLogger.Info("SMOKE", "StartSmokeRoutine completed.");
        }

        //* Executes Register, Login, GetUserData and optional Refresh using the new unified API.
        public async Task RunSmoke()
        {
            Debug.Log("[Network_A Smoke] RunSmoke started.");
            NetworkFileLogger.Info("SMOKE", "RunSmoke started. user=" + User + " clearTokens=" + ClearTokensBeforeRun + " runRefresh=" + RunRefreshTest);

            if (ClearTokensBeforeRun)
            {
                SecureTokenStorage.ClearTokens();
                NetworkFileLogger.TokenState("SMOKE_AFTER_CLEAR", SecureTokenStorage.GetAccessToken(), SecureTokenStorage.GetRefreshToken());
            }

            if (AuthManager.Instance == null)
            {
                Debug.LogError("[Network_A Smoke] AuthManager is missing in scene.");
                NetworkFileLogger.Error("SMOKE", "AuthManager is missing in scene.");
                return;
            }

            Debug.Log("=== Network_A Smoke: Register -> Login -> GetUserData ===");
            NetworkFileLogger.Info("SMOKE", "=== Register -> Login -> GetUserData ===");

            ApiResult<AuthResponseDto> reg = await AuthManager.Instance.RegisterAsync(User, Pass);
            Debug.Log("[A_REGISTER] ok=" + reg.IsSuccess + " msg=" + ReadAuthMessage(reg) + " access=" + ShortToken(ReadAccess(reg)) + " refresh=" + ShortToken(ReadRefresh(reg)) + " user=" + ReadUser(reg));
            NetworkFileLogger.Auth("SMOKE_REGISTER", reg.IsSuccess, ReadAuthMessage(reg), ReadUser(reg), !string.IsNullOrEmpty(ReadAccess(reg)), !string.IsNullOrEmpty(ReadRefresh(reg)));

            if (!reg.IsSuccess)
            {
                Debug.LogError("[Network_A Smoke] Register failed. Test ended.");
                NetworkFileLogger.Error("SMOKE", "Register failed. Test ended.");
                return;
            }

            ApiResult<AuthResponseDto> login = await AuthManager.Instance.LoginAsync(User, Pass);
            Debug.Log("[A_LOGIN] ok=" + login.IsSuccess + " msg=" + ReadAuthMessage(login) + " access=" + ShortToken(ReadAccess(login)) + " refresh=" + ShortToken(ReadRefresh(login)) + " user=" + ReadUser(login));
            NetworkFileLogger.Auth("SMOKE_LOGIN", login.IsSuccess, ReadAuthMessage(login), ReadUser(login), !string.IsNullOrEmpty(ReadAccess(login)), !string.IsNullOrEmpty(ReadRefresh(login)));

            if (!login.IsSuccess || login.Data == null || string.IsNullOrEmpty(login.Data.accessToken))
            {
                Debug.LogError("[Network_A Smoke] Login failed or access token missing. Test ended.");
                NetworkFileLogger.Error("SMOKE", "Login failed or access token missing. Test ended.");
                return;
            }

            Debug.Log("[A_TOKENS_AFTER_LOGIN] access=" + ShortToken(SecureTokenStorage.GetAccessToken()) + " refresh=" + ShortToken(SecureTokenStorage.GetRefreshToken()));
            NetworkFileLogger.TokenState("SMOKE_AFTER_LOGIN", SecureTokenStorage.GetAccessToken(), SecureTokenStorage.GetRefreshToken());

            ApiResult<GetUserDataResponseDto> me = await AuthManager.Instance.GetUserDataAsync();
            Debug.Log("[A_ME] ok=" + me.IsSuccess + " msg=" + ReadMeMessage(me) + " id=" + ReadMeId(me) + " user=" + ReadMeUser(me) + " created=" + ReadMeCreated(me));
            NetworkFileLogger.Auth("SMOKE_ME", me.IsSuccess, ReadMeMessage(me), ReadMeUser(me), !string.IsNullOrEmpty(SecureTokenStorage.GetAccessToken()), !string.IsNullOrEmpty(SecureTokenStorage.GetRefreshToken()));

            if (RunRefreshTest)
            {
                ApiResult<AuthResponseDto> refresh = await AuthManager.Instance.RefreshAsync();
                Debug.Log("[A_REFRESH] ok=" + refresh.IsSuccess + " msg=" + ReadAuthMessage(refresh) + " access=" + ShortToken(SecureTokenStorage.GetAccessToken()) + " refresh=" + ShortToken(SecureTokenStorage.GetRefreshToken()));
                NetworkFileLogger.Auth("SMOKE_REFRESH", refresh.IsSuccess, ReadAuthMessage(refresh), string.Empty, !string.IsNullOrEmpty(SecureTokenStorage.GetAccessToken()), !string.IsNullOrEmpty(SecureTokenStorage.GetRefreshToken()));
                NetworkFileLogger.TokenState("SMOKE_AFTER_REFRESH", SecureTokenStorage.GetAccessToken(), SecureTokenStorage.GetRefreshToken());

                ApiResult<GetUserDataResponseDto> me2 = await AuthManager.Instance.GetUserDataAsync();
                Debug.Log("[A_ME_AFTER_REFRESH] ok=" + me2.IsSuccess + " msg=" + ReadMeMessage(me2) + " id=" + ReadMeId(me2) + " user=" + ReadMeUser(me2) + " created=" + ReadMeCreated(me2));
                NetworkFileLogger.Auth("SMOKE_ME_AFTER_REFRESH", me2.IsSuccess, ReadMeMessage(me2), ReadMeUser(me2), !string.IsNullOrEmpty(SecureTokenStorage.GetAccessToken()), !string.IsNullOrEmpty(SecureTokenStorage.GetRefreshToken()));
            }

            Debug.Log("[Network_A Smoke] Done.");
            NetworkFileLogger.Info("SMOKE", "RunSmoke done. LogFile=" + NetworkFileLogger.CurrentLogFilePath);
        }

        //* Shortens token for safe logs.
        private string ShortToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return "<empty>";
            if (token.Length <= 14) return token;
            return token.Substring(0, 6) + "..." + token.Substring(token.Length - 6);
        }

        //* Reads access token from auth result safely.
        private string ReadAccess(ApiResult<AuthResponseDto> result)
        {
            return result != null && result.Data != null ? result.Data.accessToken : string.Empty;
        }

        //* Reads refresh token from auth result safely.
        private string ReadRefresh(ApiResult<AuthResponseDto> result)
        {
            return result != null && result.Data != null ? result.Data.refreshToken : string.Empty;
        }

        //* Reads auth message from result safely.
        private string ReadAuthMessage(ApiResult<AuthResponseDto> result)
        {
            if (result == null) return "null";
            return result.Data != null ? result.Data.message : result.ErrorMessage;
        }

        //* Reads auth user name from result safely.
        private string ReadUser(ApiResult<AuthResponseDto> result)
        {
            return result != null && result.Data != null && result.Data.user != null ? result.Data.user.emailOrUsername : string.Empty;
        }

        //* Reads get-user-data message safely.
        private string ReadMeMessage(ApiResult<GetUserDataResponseDto> result)
        {
            if (result == null) return "null";
            return result.Data != null ? result.Data.message : result.ErrorMessage;
        }

        //* Reads user id from get-user-data result safely.
        private string ReadMeId(ApiResult<GetUserDataResponseDto> result)
        {
            return result != null && result.Data != null && result.Data.user != null ? result.Data.user.id : string.Empty;
        }

        //* Reads username from get-user-data result safely.
        private string ReadMeUser(ApiResult<GetUserDataResponseDto> result)
        {
            return result != null && result.Data != null && result.Data.user != null ? result.Data.user.emailOrUsername : string.Empty;
        }

        //* Reads created timestamp from get-user-data result safely.
        private long ReadMeCreated(ApiResult<GetUserDataResponseDto> result)
        {
            return result != null && result.Data != null && result.Data.user != null ? result.Data.user.createdAtUnix : 0;
        }
    }
}