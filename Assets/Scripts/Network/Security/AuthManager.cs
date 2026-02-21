using UnityEngine;
using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Assets.Scripts.Network.Core.Interfaces;
using Assets.Scripts.Network.Core.Models;
using Assets.Scripts.Network.Core.Utils;
using Assets.Scripts.Network.HTTP;
using System.Collections.Generic;
using Assets.Scripts.Network.Security.PlatformTokenStorage;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Assets.Scripts.Network.Security
{
    /// <summary>
    /// Main authentication manager
    /// This class manages the full lifecycle of login, token refresh, and error handling
    /// </summary>
    public class AuthManager : MonoBehaviour
    {
        public static AuthManager Instance { get; private set; }

        private ITokenStorage tokenStorage;
        private RefreshTokenHandler refreshTokenHandler;
        private HTTPClient httpClient; // Uses stage 3 (temporarily direct in this stage)

        // Authentication state
        public bool IsAuthenticated => tokenStorage != null && tokenStorage.IsTokenValid();
        //   public string CurrentUserId => tokenStorage?.GetUserId();

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
                Initialize();
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private void Initialize()
        {

            tokenStorage = TokenStorageFactory.Create();

            httpClient = new HTTPClient(this);

            refreshTokenHandler = new RefreshTokenHandler(tokenStorage, httpClient.Logger);


        }

        /// <summary>
        /// Login with username/email and password
        /// </summary>
        public async Task<AuthResult> LoginAsync(string userName, string password, CancellationToken cancellationToken = default)
        {
            try
            {
                // Validate inputs
                if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
                {
                    return AuthResult.Failure("Username and password are required.");
                }

                // Build login request
                var loginRequest = new LoginRequest
                {
                    username = userName,
                    password = password,
                };

                string jsonBody = JSONSerializer.Serialize(loginRequest);

                // Send request (uses HTTPClient)
                var result = await SendAuthRequestAsync(EndpointUrlConfig.LoginEndpoint, jsonBody, cancellationToken);

                if (result.IsSuccess && !string.IsNullOrEmpty(result.RawData))
                {
                    var authResponse = JSONSerializer.Deserialize<AuthResponse>(result.RawData);
                    if (!string.IsNullOrEmpty(authResponse?.access_token))
                    {
                        tokenStorage.SaveTokens(
                            authResponse.access_token,
                            authResponse.refresh_token,
                            authResponse.expires_in
                        // authResponse.user?.userId
                        );
                        return AuthResult.Success(authResponse);
                    }
                    else
                    {
                        return AuthResult.Failure(authResponse.message ?? "Authentication failed.");
                    }
                }
                else
                {
                    string errorMsg = result.Error?.Message ?? "Failed to connect to the server.";
                    return AuthResult.Failure(errorMsg);
                }
            }
            catch (OperationCanceledException)
            {
                return AuthResult.Failure("Login request was cancelled.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AuthManager] Unexpected error during login: {ex.Message}");
                return AuthResult.Failure("Internal error during login.");
            }
        }


        /// <summary>
        /// Register a new user
        /// </summary>
        public async Task<AuthResult> RegisterAsync(string username, string email, string password, string avatarId, string displayName, CancellationToken cancellationToken = default)
        {
            try
            {
                // Validate inputs
                if (string.IsNullOrWhiteSpace(username) || username.Length < 3)
                    return AuthResult.Failure("Username must be at least 3 characters.");

                if (string.IsNullOrWhiteSpace(email) || !email.Contains("@"))
                    return AuthResult.Failure("Please enter a valid email address.");

                if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
                    return AuthResult.Failure("Password must be at least 6 characters.");

                var registerRequest = new RegisterRequest
                {
                    username = username,
                    email = email,
                    password = password,
                    avatarId = avatarId,
                    displayName = displayName,
                };

                string jsonBody = JSONSerializer.Serialize(registerRequest);

                var result = await SendAuthRequestAsync(EndpointUrlConfig.RegisterEndpoint, jsonBody, cancellationToken);

                if (result.IsSuccess && !string.IsNullOrEmpty(result.RawData))
                {
                    var authResponse = JSONSerializer.Deserialize<AuthResponse>(result.RawData);

                    if (authResponse.success && !string.IsNullOrEmpty(authResponse.token?.access_token))
                    {
                        tokenStorage.SaveTokens(
                            authResponse.token.access_token,
                            authResponse.token.refresh_token,
                            authResponse.token.expires_in
                        // authResponse.user?.userId ?? authResponse.user?.userId // kept as-is
                        );

                        Debug.Log("[AuthManager] Tokens saved successfully.");
                        return AuthResult.Success(authResponse);
                    }
                    else
                    {
                        return AuthResult.Failure(authResponse.message ?? "Registration failed.");
                    }
                }
                else
                {
                    return AuthResult.Failure(result.Error?.Message ?? "Failed to connect to the server.");
                }
            }
            catch (OperationCanceledException)
            {
                return AuthResult.Failure("Registration request was cancelled.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AuthManager] Unexpected error during registration: {ex.Message}");
                return AuthResult.Failure("Internal error during registration.");
            }
        }

        /// <summary>
        /// Refresh token using refresh-token
        /// </summary>
        public async Task<AuthResult> RefreshTokenAsync(CancellationToken cancellationToken = default)
        {
            return await refreshTokenHandler.RefreshTokenAsync(cancellationToken);
        }

        public async Task<ProfileResult> FetchProfileAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // 1) Optional: If token is not valid, fail fast instead of calling /me

                // 2) Build RequestModel
                var request = new RequestModel
                {
                    Method = HttpMethod.GET,
                    Url = EndpointUrlConfig.MeEndpoint,   // e.g. "api/auth/me"
                    TimeoutMs = 15000
                };

                // 3) Send (Authorization header set by HTTPHeadersManager when needed)
                var response = (ResponseModel)await httpClient.SendAsync(request, cancellationToken);

                // 4) Validate response
                if (!response.IsSuccess || string.IsNullOrEmpty(response.RawData))
                    return ProfileResult.Failure(response.Error?.Message ?? "Invalid server response.");

                // 5) Deserialize
                var profile = JSONSerializer.Deserialize<UserProfileResponse>(response.RawData);

                if (profile == null)
                    return ProfileResult.Failure("Failed to deserialize profile response (Deserialize returned null).");

                if (!profile.success)
                    return ProfileResult.Failure(profile.message ?? "Profile fetch failed.");

                if (profile.user == null)
                    return ProfileResult.Failure("Profile response received but user is null.");

                Debug.Log($"[AuthManager] ✅ Profile fetched: {profile.user.email}");

                return ProfileResult.Success(profile);
            }
            catch (OperationCanceledException)
            {
                return ProfileResult.Failure("Profile request was cancelled.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AuthManager] Unexpected error in FetchProfileAsync: {ex.Message}");
                return ProfileResult.Failure("Internal error while fetching profile.");
            }
        }

        /// <summary>
        /// Logout (clear tokens)
        /// </summary>
        public void Logout()
        {
            tokenStorage?.ClearTokens();
            Debug.Log("[AuthManager] User logged out successfully.");
        }

        /// <summary>
        /// Send auth request (was UnityWebRequest direct in earlier stages; now via HTTPClient)
        /// </summary>
        private async Task<ResponseModel> SendAuthRequestAsync(string endpoint, string jsonBody, CancellationToken cancellationToken)
        {
            var request = new RequestModel
            {
                Method = HttpMethod.POST,
                Url = endpoint,
                Body = jsonBody,
                TimeoutMs = 15000
            }
            .AddHeader("Accept", "application/json");

            // ✅ This request is public (no Authorization)
            request.Tags ??= new List<string>();
            request.Tags.Add("NoAuth");

            return (ResponseModel)await httpClient.SendAsync(request, cancellationToken);
        }

        /// <summary>
        /// Device info for security
        /// </summary>
        private string GetDeviceInfo()
        {
            return $"Platform:{Application.platform}, OS:{SystemInfo.operatingSystem}, Model:{SystemInfo.deviceModel}, Version:{Application.version}";
        }

        /// <summary>
        /// Get current auth token (for other requests)
        /// </summary>
        public string GetAuthToken()
        {
            if (!IsAuthenticated)
            {
                Debug.LogWarning("[AuthManager] Auth token requested but user is not authenticated.");
                return null;
            }

            return tokenStorage.GetToken();
        }
    }

    /// <summary>
    /// Auth operation result
    /// </summary>
    public class AuthResult
    {
        public bool IsSuccess { get; private set; }
        public AuthResponse AuthResponse { get; private set; }
        public string ErrorMessage { get; private set; }

        private AuthResult(bool isSuccess, AuthResponse response = null, string errorMessage = null)
        {
            IsSuccess = isSuccess;
            AuthResponse = response;
            ErrorMessage = errorMessage;
        }

        public static AuthResult Success(AuthResponse response)
        {
            return new AuthResult(true, response);
        }

        public static AuthResult Failure(string errorMessage)
        {
            return new AuthResult(false, null, errorMessage);
        }
    }

    /// <summary>
    /// Simple Editor-only token storage (for development)
    /// </summary>
    public class EditorTokenStorage : ITokenStorage
    {
        private string token;
        private string refreshToken;
        private long expiryTimestamp;

        private const string KEY_TOKEN = "METAVERSE_AUTH_TOKEN";
        private const string KEY_REFRESH = "METAVERSE_AUTH_REFRESH";
        private const string KEY_EXPIRY = "METAVERSE_AUTH_EXPIRY";
        //  private const string KEY_USERID = "METAVERSE_AUTH_USERID";

        public EditorTokenStorage()
        {
            // Debug.Log("[EditorTokenStorage] Created (new). (Disk-Only) RAM is not the source of truth.");
            LoadFromDisk(withLog: true);
        }

        public void SaveTokens(string token, string refreshToken, int expiresIn)
        {
            Debug.Log("[EditorTokenStorage] SaveTokens ");

            // Fields are set for debug visibility, but the source of truth is Disk
            this.token = token;
            this.refreshToken = refreshToken;
            this.expiryTimestamp = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeSeconds();


            SaveToDisk(withLog: true);
        }

        public string GetToken()
        {
            LoadFromDisk(withLog: true);
            Debug.Log($"[EditorTokenStorage] GetToken   -> {(string.IsNullOrEmpty(token) ? "EMPTY" : "SET")}");
            return token;
        }

        public string GetRefreshToken()
        {
            LoadFromDisk(withLog: true);
            Debug.Log($"[EditorTokenStorage] GetRefreshToken  -> {(string.IsNullOrEmpty(refreshToken) ? "EMPTY" : "SET")}");
            return refreshToken;
        }

        /*   public string GetUserId()
          {
              LoadFromDisk(withLog: true);
              Debug.Log($"[EditorTokenStorage] GetUserId  -> {(string.IsNullOrEmpty(userId) ? "EMPTY" : userId)}");
              return userId;
          } */

        public bool IsTokenValid()
        {
            LoadFromDisk(withLog: true);

            if (string.IsNullOrEmpty(token))
            {
                Debug.Log("[EditorTokenStorage] IsTokenValid  -> false (token is empty).");
                return false;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool valid = now < (expiryTimestamp - 300);

            Debug.Log($"[EditorTokenStorage] IsTokenValid   -> {valid} | now={now} | expiry={expiryTimestamp} | buffer=300s");
            return valid;
        }

        public void ClearTokens()
        {
            Debug.Log("[EditorTokenStorage] ClearTokens  -> Clearing EditorPrefs + fields...");

            token = null;
            refreshToken = null;
            expiryTimestamp = 0;
            //  userId = null;

            DeleteFromDisk(withLog: true);
        }

        // ------------------------
        // Disk Helpers
        // ------------------------

        private void LoadFromDisk(bool withLog)
        {
#if UNITY_EDITOR
            string diskToken = EditorPrefs.GetString(KEY_TOKEN, "");
            string diskRefresh = EditorPrefs.GetString(KEY_REFRESH, "");
           // string diskUserId = EditorPrefs.GetString(KEY_USERID, "");
            string expiryStr = EditorPrefs.GetString(KEY_EXPIRY, "0");

            long diskExpiry = 0;
            long.TryParse(expiryStr, out diskExpiry);

            token = string.IsNullOrEmpty(diskToken) ? null : diskToken;
            refreshToken = string.IsNullOrEmpty(diskRefresh) ? null : diskRefresh;
            //   userId = string.IsNullOrEmpty(diskUserId) ? null : diskUserId;
            expiryTimestamp = diskExpiry;

            if (withLog)
            {
                Debug.Log($"[EditorTokenStorage] Load Data -> " +
                          $"token={(string.IsNullOrEmpty(token) ? "EMPTY" : "SET")} | " +
                          $"refresh={(string.IsNullOrEmpty(refreshToken) ? "EMPTY" : "SET")} | " +
                          //   $"userId={(string.IsNullOrEmpty(userId) ? "EMPTY" : userId)} | " +
                          $"expiry={expiryTimestamp}");
            }
#else
            token = null;
            refreshToken = null;
            //  userId = null;
            expiryTimestamp = 0;

            if (withLog)
                Debug.LogWarning("[EditorTokenStorage] LoadFromDisk called but UNITY_EDITOR is false -> Disk storage not available.");
#endif
        }

        private void SaveToDisk(bool withLog)
        {
#if UNITY_EDITOR
            EditorPrefs.SetString(KEY_TOKEN, token ?? "");
            EditorPrefs.SetString(KEY_REFRESH, refreshToken ?? "");
            //   EditorPrefs.SetString(KEY_USERID, userId ?? "");
            EditorPrefs.SetString(KEY_EXPIRY, expiryTimestamp.ToString());

            if (withLog)
            {
                Debug.Log("[EditorTokenStorage] SaveToDisk (Disk-Only) -> EditorPrefs updated.");
                Debug.Log($"[EditorTokenStorage] Saved -> " +
                          $"token={(string.IsNullOrEmpty(token) ? "EMPTY" : "SET")} | " +
                          $"refresh={(string.IsNullOrEmpty(refreshToken) ? "EMPTY" : "SET")} | " +
                          // $"userId={(string.IsNullOrEmpty(userId) ? "EMPTY" : userId)} | " +
                          $"expiry={expiryTimestamp}");
            }
#endif
        }

        private void DeleteFromDisk(bool withLog)
        {
#if UNITY_EDITOR
            EditorPrefs.DeleteKey(KEY_TOKEN);
            EditorPrefs.DeleteKey(KEY_REFRESH);
         //   EditorPrefs.DeleteKey(KEY_USERID);
            EditorPrefs.DeleteKey(KEY_EXPIRY);

            if (withLog)
                Debug.Log("[EditorTokenStorage] DeleteFromDisk (Disk-Only) -> EditorPrefs keys deleted.");
#endif
        }
    }
}
