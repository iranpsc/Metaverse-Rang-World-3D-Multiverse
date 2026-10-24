using System;
using System.Threading.Tasks;
using Network_A.Core;
using UnityEngine;

namespace Network_A.Auth
{
    public static class AuthRefreshManager
    {
        private static bool _isRefreshing;
        private static Task<bool> _refreshTask;
        private static readonly object _lock = new object();
        private static Func<Task<bool>> _refreshAction;

        public static Action OnRequireLoginUI;

        //* Registers the real refresh implementation. AuthManager owns the gRPC refresh call.
        public static void Configure(Func<Task<bool>> refreshAction)
        {
            _refreshAction = refreshAction;
            NetworkFileLogger.Info("AUTH_REFRESH_MANAGER", "Refresh action configured=" + (_refreshAction != null));
        }

        //* Runs refresh once and keeps the existing behavior of opening login UI after failure.
        public static Task<bool> Refresh()
        {
            return Refresh(true);
        }

        //* Runs refresh once and lets reconnect callers preserve the current session on temporary server failures.
        public static async Task<bool> Refresh(bool requireLoginUiOnFailure)
        {
            Task<bool> task;

            lock (_lock)
            {
                if (_isRefreshing)
                {
                    task = _refreshTask;
                    NetworkFileLogger.Info(
                        "AUTH_REFRESH_MANAGER",
                        "Refresh already running. Waiting for current task. requireLoginUiOnFailure=" +
                        requireLoginUiOnFailure
                    );
                }
                else
                {
                    if (_refreshAction == null)
                    {
                        Debug.Log("[AuthRefreshManager] Refresh action is not configured.");
                        NetworkFileLogger.Warning(
                            "AUTH_REFRESH_MANAGER",
                            "Refresh action is not configured. requireLoginUiOnFailure=" +
                            requireLoginUiOnFailure
                        );

                        if (requireLoginUiOnFailure) OnRequireLoginUI?.Invoke();
                        return false;
                    }

                    _isRefreshing = true;
                    _refreshTask = _refreshAction();
                    task = _refreshTask;

                    NetworkFileLogger.Info(
                        "AUTH_REFRESH_MANAGER",
                        "Refresh task started. requireLoginUiOnFailure=" +
                        requireLoginUiOnFailure
                    );
                }
            }

            try
            {
                bool ok = await task;

                NetworkFileLogger.Auth(
                    "REFRESH_RESULT",
                    ok,
                    ok
                        ? "refresh_success"
                        : "refresh_failed | requireLoginUiOnFailure=" +
                          requireLoginUiOnFailure,
                    string.Empty,
                    HasAccessToken(),
                    HasRefreshToken()
                );

                if (!ok && requireLoginUiOnFailure) OnRequireLoginUI?.Invoke();
                return ok;
            }
            catch (Exception ex)
            {
                Debug.LogError("[AuthRefreshManager] Refresh exception: " + ex);
                NetworkFileLogger.Exception("AUTH_REFRESH_MANAGER", ex);

                if (requireLoginUiOnFailure) OnRequireLoginUI?.Invoke();
                return false;
            }
            finally
            {
                lock (_lock)
                {
                    if (task == _refreshTask)
                    {
                        _isRefreshing = false;
                        _refreshTask = null;
                        NetworkFileLogger.Info("AUTH_REFRESH_MANAGER", "Refresh task cleared.");
                    }
                }
            }
        }

        //* Checks if an access token exists.
        private static bool HasAccessToken()
        {
            return !string.IsNullOrEmpty(SecureTokenStorage.GetAccessToken());
        }

        //* Checks if a refresh token exists.
        private static bool HasRefreshToken()
        {
            return !string.IsNullOrEmpty(SecureTokenStorage.GetRefreshToken());
        }
    }
}
