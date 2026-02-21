using System;
using System.Threading;
using System.Threading.Tasks;
using Assets.Scripts.Network.Core.Models;
using Assets.Scripts.Network.Core.Utils;
using UnityEngine;
using Assets.Scripts.Network.HTTP; // برای NetworkLogger
namespace Assets.Scripts.Network.Security
{
    /// <summary>
    /// مدیر رفرش حرفه‌ای توکن با کنترل کامل همزمانی
    /// </summary>
    public class RefreshTokenHandler
    {
        private readonly ITokenStorage tokenStorage;
        private readonly NetworkLogger logger;
        // نگهداری Task جاری رفرش (اگر وجود داشته باشد)
        private Task<AuthResult> refreshTask;

        // قفل برای جلوگیری از race condition
        private readonly object refreshLock = new object();

        private int refreshWaiters = 0;          // how many callers waited on the same refresh task
        private int refreshSequenceId = 0;       // incremental id per refresh run (optional but useful)


        //* For NetworkLogger
        public RefreshTokenHandler(ITokenStorage tokenStorage, NetworkLogger logger)
        {
            this.tokenStorage = tokenStorage ?? throw new ArgumentNullException(nameof(tokenStorage));
            this.logger = logger;
        }
        public RefreshTokenHandler(ITokenStorage tokenStorage)
        {
            this.tokenStorage = tokenStorage ?? throw new ArgumentNullException(nameof(tokenStorage));
        }

        /// <summary>
        /// Thread-safe token refresh
        /// </summary>
        public Task<AuthResult> RefreshTokenAsync(CancellationToken cancellationToken = default)
        {
            lock (refreshLock)
            {
                // If a refresh is already running, return the same task
                if (refreshTask != null && !refreshTask.IsCompleted)
                {
                    refreshWaiters++;

                    logger?.LogInfo(
                        "RefreshTokenAsync: queued",
                        $"waiters={refreshWaiters}, taskStatus={refreshTask.Status}"
                    );

                    return refreshTask;
                }

                // Start a new refresh
                refreshSequenceId++;
                refreshWaiters = 0;

                logger?.LogInfo(
                    "RefreshTokenAsync: start",
                    $"sequence={refreshSequenceId}"
                );

                refreshTask = InternalRefreshAsync(cancellationToken, refreshSequenceId);
                return refreshTask;
            }
        }


        /// <summary>
        /// Actual refresh execution
        /// </summary>
        private async Task<AuthResult> InternalRefreshAsync(CancellationToken cancellationToken, int sequenceId)
        {
            Debug.Log("[RefreshTokenHandler] InternalRefreshAsync started. (1)");

            try
            {
                string currentRefreshToken = tokenStorage.GetRefreshToken();

                Debug.Log("[RefreshTokenHandler] Read refresh token from storage. (2)");

                if (string.IsNullOrEmpty(currentRefreshToken))//*Go Login
                {
                    return AuthResult.Failure("[RefreshTokenHandler] Refresh token is missing — user must login again.");
                }
                //* Set Rfresh Request
                var refreshRequest = new RefreshTokenRequest
                {
                    refreshToken = currentRefreshToken,
                    deviceInfo = $"Platform:{Application.platform}, Fingerprint:{CryptoService.GenerateDeviceFingerprint()}"
                };

                string jsonBody = JSONSerializer.Serialize(refreshRequest);

                string url = URLBuilder.BuildFromRequest(new RequestModel
                {
                    Url = EndpointUrlConfig.RefreshEndpoint,
                    Method = HttpMethod.POST
                });

                Debug.Log($"3 [Url]  : {url}");
                var result = await SendRefreshRequestAsync(url, jsonBody, cancellationToken);

                if (result.IsSuccess && !string.IsNullOrEmpty(result.RawData))
                {
                    var authResponse = JSONSerializer.Deserialize<AuthResponse>(result.RawData);

                    if (authResponse.success && !string.IsNullOrEmpty(authResponse.token?.access_token))
                    {
                        Debug.Log($"4 [Save Access Token]  : {authResponse.token.access_token}");
                        tokenStorage.SaveTokens(
                            authResponse.token.access_token,
                            authResponse.token.refresh_token,
                            authResponse.token.expires_in
                        //authResponse.user?.userId ?? authResponse.user?.userId // kept as-is
                        );

                        Debug.Log("[RefreshTokenHandler] Tokens saved successfully.(5)");
                        return AuthResult.Success(authResponse);
                    }
                    else
                    {
                        tokenStorage.ClearTokens();
                        return AuthResult.Failure(authResponse.message ?? "[RefreshTokenHandler] Refresh failed — user must login again.");
                    }
                }

                return AuthResult.Failure(result.Error?.Message ?? "[RefreshTokenHandler] Failed to reach refresh endpoint.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RefreshTokenHandler] Unexpected error during refresh: {ex.Message}");
                return AuthResult.Failure("[RefreshTokenHandler] Internal error while refreshing token.");
            }
            finally
            {
                lock (refreshLock)
                {
                    // ✅ log end state + queued count
                    logger?.LogInfo(
                        "RefreshTokenAsync: finished",
                        $"sequence={sequenceId}, queuedWaiters={refreshWaiters}"
                    );

                    refreshTask = null;
                    refreshWaiters = 0;
                }
            }
        }

        /// <summary>
        /// Send refresh request
        /// </summary>
        private async Task<ResponseModel> SendRefreshRequestAsync(string url, string jsonBody, CancellationToken cancellationToken)
        {
            using (var webRequest = new UnityEngine.Networking.UnityWebRequest(url, "POST"))
            {
                webRequest.SetRequestHeader("Content-Type", "application/json");

                webRequest.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(jsonBody));
                webRequest.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                webRequest.timeout = 10;

                var operation = webRequest.SendWebRequest();

                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        webRequest.Abort();
                        return ResponseModel.Cancelled(Guid.NewGuid().ToString("N"));
                    }

                    await Task.Delay(10);
                }

                if (webRequest.result == UnityEngine.Networking.UnityWebRequest.Result.ConnectionError ||
                    webRequest.result == UnityEngine.Networking.UnityWebRequest.Result.ProtocolError)
                {
                    return ResponseModel.Failure(
                        new NetworkError(
                            NetworkErrorCode.TokenExpired,
                            "Token refresh request failed",
                            webRequest.error
                        ),
                        webRequest.downloadHandler?.text ?? string.Empty,
                        (int)webRequest.responseCode
                    );
                }
                Debug.Log($"5555555555555[Url]  : {webRequest.downloadHandler.text}");
                return ResponseModel.Success(
                    webRequest.downloadHandler.text,
                    (int)webRequest.responseCode
                );
            }
        }
    }
}
