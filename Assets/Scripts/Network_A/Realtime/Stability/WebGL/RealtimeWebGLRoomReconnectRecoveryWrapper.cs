#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Bootstrap;
using Network_A.Realtime.Controllers;
using Network_A.Realtime.Transport;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network_A.Realtime.Stability.WebGL
{
    public static class RealtimeWebGLRoomReconnectRecoveryInstaller
    {
        private const string RootName =
            "Realtime_WebGL_Room_Reconnect_Recovery";

        //* این تابع بازیابی عمومی وب را پیش از بارگذاری نخستین صحنه نصب می کند.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            Debug.Log(
                "REALTIME_WEBGL_GENERAL_RECONNECT=DISABLED" +
                " | recoveryOwner=stable_realtime_manager_and_webgl_binder" +
                " | reason=remove_duplicate_recovery_path" +
                " | webglOnly=True" +
                " | voiceIndependent=True" +
                " | windowsPathChanged=False" +
                " | questPathChanged=False");
        }
    }

    [DisallowMultipleComponent]
    public sealed class RealtimeWebGLRoomReconnectRecoveryWrapper : MonoBehaviour
    {
        private const string WebGLLobbySceneName = "Lobby 1 WebGL";
        private const float RetryDelaySeconds = 1.0f;
        private const float MinimumRecoveryBudgetSeconds = 10.0f;
        private const float FreshLobbySettleSeconds = 5.0f;
        private const float FreshLobbyRetrySeconds = 2.0f;
        private const float FreshLobbyRecoveryBudgetSeconds = 180.0f;

        private CancellationTokenSource lifetimeCts;
        private MethodInfo rememberRoomMethod;
        private MethodInfo temporaryDisconnectMethod;
        private MethodInfo networkCheckMethod;
        private Task temporaryCleanupTask;
        private bool browserStateObserved;
        private bool browserWasOnline;
        private bool recoveryPending;
        private bool recoveryRunning;
        private int recoveryGeneration;
        private int recoveryNetworkProbeAttempts;
        private string expectedRoomId = string.Empty;
        private string staleRealtimeConnectionId = string.Empty;
        private bool permanentRecoveryObserved;
        private bool freshLobbyRecoveryRunning;
        private int permanentRecoveryGeneration;
        private int freshLobbyRecoveryAttempts;
        private int observedLobbySceneHandle = -1;
        private float freshLobbyRecoveryDeadlineAt = -1f;
        private float freshLobbyRetryAt = -1f;

        private void Awake()
        {
            lifetimeCts = new CancellationTokenSource();
        }

        //* این تابع تغییر واقعی وضعیت اینترنت مرورگر را مستقل از صدا دنبال می کند.
        private void Update()
        {
            bool browserOnline =
                WebGLWebSocketRealtimeTransport.IsBrowserOnline;

            ObservePermanentRecoveryFailure();

            if (!browserStateObserved)
            {
                browserStateObserved = true;
                browserWasOnline = browserOnline;
                TryStartFreshLobbyRecovery(browserOnline);
                return;
            }

            if (browserWasOnline && !browserOnline)
            {
                BeginRecoveryFromBrowserOffline();
            }

            if (recoveryPending &&
                !recoveryRunning &&
                browserOnline)
            {
                recoveryRunning = true;
                int generation = recoveryGeneration;
                _ = RecoverRealtimeAndRoomAsync(
                    generation,
                    lifetimeCts.Token);
            }

            TryStartFreshLobbyRecovery(browserOnline);

            browserWasOnline = browserOnline;
        }

        //* این تابع پایان مهلت روم قدیمی را تشخیص می دهد و بازیابی همان روم را برای همیشه متوقف می کند.
        private void ObservePermanentRecoveryFailure()
        {
            RealtimeRoomGameServerManager manager =
                RealtimeRoomGameServerManager.Instance;

            if (manager == null ||
                !manager.IsPermanentRecoveryFailureWaitingForLobby ||
                permanentRecoveryObserved)
            {
                return;
            }

            permanentRecoveryObserved = true;
            permanentRecoveryGeneration += 1;
            freshLobbyRecoveryRunning = false;
            freshLobbyRecoveryAttempts = 0;
            observedLobbySceneHandle = -1;
            freshLobbyRecoveryDeadlineAt = -1f;
            freshLobbyRetryAt = -1f;

            bool staleRoomRecoveryWasPending =
                recoveryPending || recoveryRunning;

            recoveryPending = false;
            recoveryRunning = false;
            recoveryGeneration += 1;
            temporaryCleanupTask = null;
            expectedRoomId = string.Empty;
            staleRealtimeConnectionId = string.Empty;

            Debug.Log(
                "REALTIME_WEBGL_PERMANENT_RECOVERY=OBSERVED" +
                " | staleRoomRecoveryCanceled=" +
                    staleRoomRecoveryWasPending +
                " | nextPath=fresh_lobby" +
                " | requiresOkButton=False" +
                " | webglOnly=True" +
                " | windowsPathChanged=False" +
                " | questPathChanged=False");
        }

        //* این تابع پس از بازگشت بایندر به صحنه لابی، ورود تازه را در صورت شکست تلاش نخست دوباره اجرا می کند.
        private void TryStartFreshLobbyRecovery(bool browserOnline)
        {
            if (!permanentRecoveryObserved ||
                freshLobbyRecoveryRunning ||
                !browserOnline)
            {
                return;
            }

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() ||
                !string.Equals(
                    scene.name,
                    WebGLLobbySceneName,
                    StringComparison.Ordinal))
            {
                return;
            }

            RealtimeRoomGameServerManager manager =
                RealtimeRoomGameServerManager.Instance;

            if (manager == null ||
                manager.IsPermanentRecoveryFailureWaitingForLobby)
            {
                return;
            }

            if (manager.IsRealtimeReady &&
                manager.IsInsidePublicLobbyRoom)
            {
                CompleteFreshLobbyRecovery(manager, 0);
                return;
            }

            if (observedLobbySceneHandle != scene.handle)
            {
                observedLobbySceneHandle = scene.handle;
                freshLobbyRecoveryDeadlineAt =
                    Time.realtimeSinceStartup +
                    FreshLobbyRecoveryBudgetSeconds;
                freshLobbyRetryAt =
                    Time.realtimeSinceStartup +
                    FreshLobbySettleSeconds;

                Debug.Log(
                    "REALTIME_WEBGL_PERMANENT_RECOVERY=FRESH_LOBBY_WAIT" +
                    " | scene=" + Safe(scene.name) +
                    " | settleSeconds=" + FreshLobbySettleSeconds.ToString("F1") +
                    " | retryIndependentOfOkButton=True");
                return;
            }

            if (Time.realtimeSinceStartup < freshLobbyRetryAt)
                return;

            if (freshLobbyRecoveryDeadlineAt > 0f &&
                Time.realtimeSinceStartup >=
                freshLobbyRecoveryDeadlineAt)
            {
                FailFreshLobbyRecovery(
                    "fresh_lobby_recovery_deadline_exceeded",
                    manager);
                return;
            }

            freshLobbyRecoveryRunning = true;
            int generation = permanentRecoveryGeneration;
            _ = RecoverFreshLobbyAsync(
                generation,
                lifetimeCts.Token);
        }

        //* این تابع فقط از رابط عمومی مدیر پایدار برای ساخت اتصال و عضویت تازه لابی استفاده می کند.
        private async Task RecoverFreshLobbyAsync(
            int generation,
            CancellationToken cancellationToken)
        {
            int attempt = 0;

            try
            {
                while (IsCurrentPermanentRecovery(generation))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!WebGLWebSocketRealtimeTransport.IsBrowserOnline)
                    {
                        ScheduleFreshLobbyRetry();
                        return;
                    }

                    Scene scene = SceneManager.GetActiveScene();
                    if (!scene.IsValid() ||
                        !string.Equals(
                            scene.name,
                            WebGLLobbySceneName,
                            StringComparison.Ordinal))
                    {
                        CancelFreshLobbyRecovery(
                            "lobby_scene_changed");
                        return;
                    }

                    RealtimeRoomGameServerManager manager =
                        RealtimeRoomGameServerManager.Instance;

                    if (manager == null)
                    {
                        ScheduleFreshLobbyRetry();
                        return;
                    }

                    if (manager.IsRealtimeReady &&
                        manager.IsInsidePublicLobbyRoom)
                    {
                        CompleteFreshLobbyRecovery(manager, attempt);
                        return;
                    }

                    bool networkReady =
                        await EnsureGlobalNetworkOnlineAsync(
                            cancellationToken);

                    if (!networkReady ||
                        !IsCurrentPermanentRecovery(generation))
                    {
                        ScheduleFreshLobbyRetry();
                        return;
                    }

                    freshLobbyRecoveryAttempts += 1;
                    attempt = freshLobbyRecoveryAttempts;

                    Debug.Log(
                        "REALTIME_WEBGL_PERMANENT_RECOVERY=FRESH_LOBBY_START" +
                        " | attempt=" + attempt +
                        " | browserOnline=True" +
                        " | requiresOkButton=False");

                    bool entered =
                        await manager.EnterLobbyAsync(
                            cancellationToken);

                    if (!IsCurrentPermanentRecovery(generation))
                        return;

                    if (entered &&
                        manager.IsRealtimeReady &&
                        manager.IsInsidePublicLobbyRoom)
                    {
                        CompleteFreshLobbyRecovery(manager, attempt);
                        return;
                    }

                    ScheduleFreshLobbyRetry();

                    Debug.LogWarning(
                        "REALTIME_WEBGL_PERMANENT_RECOVERY=FRESH_LOBBY_RETRY" +
                        " | attempt=" + attempt +
                        " | realtimeReady=" + manager.IsRealtimeReady +
                        " | publicLobbyJoined=" +
                            manager.IsInsidePublicLobbyRoom +
                        " | retryInSeconds=" +
                            FreshLobbyRetrySeconds.ToString("F1"));
                    return;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                ScheduleFreshLobbyRetry();

                Debug.LogError(
                    "REALTIME_WEBGL_PERMANENT_RECOVERY=FRESH_LOBBY_RETRY" +
                    " | reason=exception_" +
                        Safe(UnwrapExceptionMessage(exception)) +
                    " | retryInSeconds=" +
                        FreshLobbyRetrySeconds.ToString("F1"));
            }
            finally
            {
                if (generation == permanentRecoveryGeneration)
                {
                    freshLobbyRecoveryRunning = false;
                }
            }
        }

        private void ScheduleFreshLobbyRetry()
        {
            freshLobbyRetryAt =
                Time.realtimeSinceStartup +
                FreshLobbyRetrySeconds;
        }

        private void CompleteFreshLobbyRecovery(
            RealtimeRoomGameServerManager manager,
            int attempt)
        {
            string connectionId =
                Safe(manager.RealtimeConnectionId);

            permanentRecoveryObserved = false;
            freshLobbyRecoveryRunning = false;
            freshLobbyRecoveryAttempts = 0;
            observedLobbySceneHandle = -1;
            freshLobbyRecoveryDeadlineAt = -1f;
            freshLobbyRetryAt = -1f;

            Debug.Log(
                "REALTIME_WEBGL_PERMANENT_RECOVERY=PASS" +
                " | attempts=" + attempt +
                " | freshRealtimeConnectionId=" + connectionId +
                " | publicLobbyJoined=True" +
                " | requiresOkButton=False" +
                " | webglOnly=True" +
                " | windowsPathChanged=False" +
                " | questPathChanged=False");
        }

        private void CancelFreshLobbyRecovery(string reason)
        {
            permanentRecoveryObserved = false;
            freshLobbyRecoveryRunning = false;
            permanentRecoveryGeneration += 1;
            freshLobbyRecoveryAttempts = 0;
            observedLobbySceneHandle = -1;
            freshLobbyRecoveryDeadlineAt = -1f;
            freshLobbyRetryAt = -1f;

            Debug.Log(
                "REALTIME_WEBGL_PERMANENT_RECOVERY=CANCELED" +
                " | reason=" + Safe(reason));
        }

        private void FailFreshLobbyRecovery(
            string reason,
            RealtimeRoomGameServerManager manager)
        {
            permanentRecoveryObserved = false;
            freshLobbyRecoveryRunning = false;
            permanentRecoveryGeneration += 1;
            freshLobbyRecoveryAttempts = 0;
            observedLobbySceneHandle = -1;
            freshLobbyRecoveryDeadlineAt = -1f;
            freshLobbyRetryAt = -1f;

            Debug.LogError(
                "REALTIME_WEBGL_PERMANENT_RECOVERY=FAIL" +
                " | reason=" + Safe(reason) +
                " | realtimeReady=" +
                    (manager != null && manager.IsRealtimeReady) +
                " | publicLobbyJoined=" +
                    (manager != null &&
                     manager.IsInsidePublicLobbyRoom) +
                " | webglOnly=True" +
                " | windowsPathChanged=False" +
                " | questPathChanged=False");
        }

        //* این تابع در لحظه قطع اینترنت، روم قبلی را نگه می دارد و اتصال قدیمی را آزاد می کند.
        private void BeginRecoveryFromBrowserOffline()
        {
            RealtimeRoomGameServerManager manager =
                RealtimeRoomGameServerManager.Instance;

            if (manager == null ||
                !manager.HasLobbyEntryRequested ||
                !manager.IsJoinedRoom ||
                manager.IsRoomExitInProgress ||
                string.IsNullOrWhiteSpace(manager.CurrentRoomId))
            {
                return;
            }

            recoveryGeneration += 1;
            recoveryPending = true;
            recoveryRunning = false;
            recoveryNetworkProbeAttempts = 0;
            expectedRoomId = Safe(manager.CurrentRoomId);
            staleRealtimeConnectionId =
                Safe(manager.RealtimeConnectionId);

            ResolvePrivateMethods();

            if (rememberRoomMethod == null ||
                temporaryDisconnectMethod == null ||
                networkCheckMethod == null)
            {
                FailRecovery(
                    "required_stable_reconnect_api_missing",
                    0,
                    manager);
                return;
            }

            manager.BeginUnifiedRecoveryFromDedicatedDisconnect(
                "webgl_browser_offline_general_reconnect");

            try
            {
                rememberRoomMethod.Invoke(manager, null);
                temporaryCleanupTask =
                    temporaryDisconnectMethod.Invoke(
                        manager,
                        new object[]
                        {
                            "webgl_browser_offline_general_reconnect"
                        }) as Task;

                if (temporaryCleanupTask == null)
                {
                    FailRecovery(
                        "temporary_cleanup_task_missing",
                        0,
                        manager);
                    return;
                }

                Debug.Log(
                    "REALTIME_WEBGL_GENERAL_RECONNECT=PENDING" +
                    " | roomId=" + expectedRoomId +
                    " | staleRealtimeConnectionId=" +
                        staleRealtimeConnectionId +
                    " | browserOnline=False" +
                    " | cleanupStarted=" +
                        (temporaryCleanupTask != null) +
                    " | webglOnly=True" +
                    " | voiceIndependent=True");
            }
            catch (Exception exception)
            {
                temporaryCleanupTask = null;
                FailRecovery(
                    "offline_cleanup_start_failed_" +
                        Safe(UnwrapExceptionMessage(exception)),
                    0,
                    manager);
            }
        }

        //* این تابع پس از بازگشت اینترنت، شبکه، ریل تایم و عضویت روم را به ترتیب بازیابی می کند.
        private async Task RecoverRealtimeAndRoomAsync(
            int generation,
            CancellationToken cancellationToken)
        {
            int attempt = 0;
            bool connectStageStarted = false;

            try
            {
                if (temporaryCleanupTask != null)
                {
                    await temporaryCleanupTask;
                }

                if (!IsCurrentRecovery(generation)) return;

                Debug.Log(
                    "REALTIME_WEBGL_GENERAL_RECONNECT=WAITING_FOR_HEALTH" +
                    " | roomId=" + expectedRoomId +
                    " | staleRealtimeConnectionId=" +
                        staleRealtimeConnectionId +
                    " | browserOnline=True");

                RealtimeRoomGameServerManager manager =
                    RealtimeRoomGameServerManager.Instance;
                float budgetSeconds =
                    manager != null && manager.IsRecoveryRunning
                        ? Mathf.Max(
                            0.1f,
                            manager.RecoveryRemainingSeconds)
                        : manager != null
                            ? Mathf.Max(
                                MinimumRecoveryBudgetSeconds,
                                manager.ClientReconnectAttemptTimeoutSeconds)
                            : MinimumRecoveryBudgetSeconds;
                float deadline =
                    Time.realtimeSinceStartup + budgetSeconds;

                while (IsCurrentRecovery(generation) &&
                       Time.realtimeSinceStartup < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!WebGLWebSocketRealtimeTransport.IsBrowserOnline)
                    {
                        await WaitForRetryAsync(
                            generation,
                            cancellationToken);
                        continue;
                    }

                    manager = RealtimeRoomGameServerManager.Instance;

                    if (!IsExpectedRoomContext(manager))
                    {
                        CancelRecovery(
                            "room_context_changed_or_exit_started");
                        return;
                    }

                    if (IsFreshRoomConnectionReady(manager))
                    {
                        CompleteRecovery(manager, attempt);
                        return;
                    }

                    bool networkReady =
                        await EnsureGlobalNetworkOnlineAsync(
                            cancellationToken);

                    if (!networkReady)
                    {
                        await WaitForRetryAsync(
                            generation,
                            cancellationToken);
                        continue;
                    }

                    if (!connectStageStarted)
                    {
                        connectStageStarted = true;

                        Debug.Log(
                            "REALTIME_WEBGL_GENERAL_RECONNECT=START" +
                            " | roomId=" + expectedRoomId +
                            " | staleRealtimeConnectionId=" +
                                staleRealtimeConnectionId +
                            " | activeHealthConfirmed=True" +
                            " | browserOnline=True");
                    }

                    attempt += 1;

                    Debug.Log(
                        "REALTIME_WEBGL_GENERAL_RECONNECT=ATTEMPT" +
                        " | attempt=" + attempt +
                        " | roomId=" + expectedRoomId +
                        " | managerState=" +
                            RealtimeRoomGameServerManager.CurrentState +
                        " | activeHealthConfirmed=True");

                    bool realtimeReady =
                        await manager.EnsureRealtimeReadyAsync(
                            true,
                            cancellationToken);

                    if (realtimeReady &&
                        IsFreshRoomConnectionReady(manager))
                    {
                        CompleteRecovery(manager, attempt);
                        return;
                    }

                    await WaitForRetryAsync(
                        generation,
                        cancellationToken);
                }

                if (!IsCurrentRecovery(generation)) return;

                RealtimeRoomGameServerManager finalManager =
                    RealtimeRoomGameServerManager.Instance;
                FailRecovery(
                    "recovery_deadline_exceeded",
                    attempt,
                    finalManager);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                FailRecovery(
                    "recovery_exception_" +
                        Safe(UnwrapExceptionMessage(exception)),
                    attempt,
                    RealtimeRoomGameServerManager.Instance);
            }
            finally
            {
                if (generation == recoveryGeneration)
                {
                    recoveryRunning = false;
                }
            }
        }

        //* این تابع بررسی رسمی شبکه را اجرا می کند تا وضعیت عمومی پیش از اتصال دوباره برخط شود.
        private async Task<bool> EnsureGlobalNetworkOnlineAsync(
            CancellationToken cancellationToken)
        {
            StartupNetworkSceneRouter router =
                StartupNetworkSceneRouter.Instance;

            if (router == null ||
                !WebGLWebSocketRealtimeTransport.IsBrowserOnline)
            {
                return false;
            }

            recoveryNetworkProbeAttempts += 1;
            bool checkedOnline;

            // navigator.onLine only reports that a network interface exists.
            // Even when the global state still says Online, require a fresh
            // request to the real server before opening /ws.  This prevents the
            // ERR_NETWORK_CHANGED race visible in Build 0064.
            if (StartupNetworkSceneRouter.IsOnline)
            {
                checkedOnline =
                    await router.CheckNetFastSilentAsync(
                        cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
            }
            else
            {
                ResolvePrivateMethods();
                if (networkCheckMethod == null) return false;

                object result = networkCheckMethod.Invoke(router, null);
                Task<bool> checkTask = result as Task<bool>;
                if (checkTask == null) return false;

                cancellationToken.ThrowIfCancellationRequested();
                checkedOnline = await checkTask;
                cancellationToken.ThrowIfCancellationRequested();
            }

            bool ready =
                checkedOnline &&
                StartupNetworkSceneRouter.IsOnline &&
                WebGLWebSocketRealtimeTransport.IsBrowserOnline;

            Debug.Log(
                "REALTIME_WEBGL_GENERAL_RECONNECT=HEALTH_" +
                (ready ? "PASS" : "WAIT") +
                " | probeAttempt=" + recoveryNetworkProbeAttempts +
                " | browserOnline=" +
                    WebGLWebSocketRealtimeTransport.IsBrowserOnline +
                " | globalNetworkState=" +
                    StartupNetworkSceneRouter.CurrentState +
                " | serverReachable=" + checkedOnline);

            return ready;
        }

        //* این تابع تا تلاش بعدی روی زمان واقعی یونیتی صبر می کند.
        private async Task WaitForRetryAsync(
            int generation,
            CancellationToken cancellationToken)
        {
            float retryAt =
                Time.realtimeSinceStartup + RetryDelaySeconds;

            while (IsCurrentRecovery(generation) &&
                   Time.realtimeSinceStartup < retryAt)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        //* این تابع آماده بودن یک اتصال تازه و عضویت همان روم را بررسی می کند.
        private bool IsFreshRoomConnectionReady(
            RealtimeRoomGameServerManager manager)
        {
            if (manager == null ||
                !manager.IsRealtimeReady ||
                !manager.IsJoinedRoom ||
                !string.Equals(
                    Safe(manager.CurrentRoomId),
                    expectedRoomId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string currentConnectionId =
                Safe(manager.RealtimeConnectionId);

            return currentConnectionId.Length > 0 &&
                   !string.Equals(
                       currentConnectionId,
                       staleRealtimeConnectionId,
                       StringComparison.OrdinalIgnoreCase);
        }

        //* این تابع از ادامه بازیابی پس از خروج واقعی یا تغییر روم جلوگیری می کند.
        private bool IsExpectedRoomContext(
            RealtimeRoomGameServerManager manager)
        {
            return manager != null &&
                   manager.HasLobbyEntryRequested &&
                   !manager.IsRoomExitInProgress &&
                   string.Equals(
                       Safe(manager.CurrentRoomId),
                       expectedRoomId,
                       StringComparison.OrdinalIgnoreCase);
        }

        //* این تابع موفقیت اتصال عمومی و عضویت روم را ثبت می کند.
        private void CompleteRecovery(
            RealtimeRoomGameServerManager manager,
            int attempt)
        {
            string roomId = expectedRoomId;
            string connectionId =
                Safe(manager.RealtimeConnectionId);

            recoveryPending = false;
            recoveryNetworkProbeAttempts = 0;
            temporaryCleanupTask = null;
            expectedRoomId = string.Empty;
            staleRealtimeConnectionId = string.Empty;

            Debug.Log(
                "REALTIME_WEBGL_GENERAL_RECONNECT=PASS" +
                " | attempts=" + attempt +
                " | roomId=" + roomId +
                " | freshRealtimeConnectionId=" + connectionId +
                " | realtimeReady=True" +
                " | roomJoined=True" +
                " | webglOnly=True" +
                " | voiceIndependent=True" +
                " | windowsPathChanged=False" +
                " | questPathChanged=False");
        }

        //* این تابع بازیابی را هنگام تغییر عمدی جریان کاربر متوقف می کند.
        private void CancelRecovery(string reason)
        {
            recoveryPending = false;
            recoveryRunning = false;
            recoveryNetworkProbeAttempts = 0;
            recoveryGeneration += 1;
            temporaryCleanupTask = null;
            expectedRoomId = string.Empty;
            staleRealtimeConnectionId = string.Empty;

            Debug.Log(
                "REALTIME_WEBGL_GENERAL_RECONNECT=CANCELED" +
                " | reason=" + Safe(reason));
        }

        //* این تابع شکست نهایی همان چرخه را یک بار ثبت و اجرای تکراری را متوقف می کند.
        private void FailRecovery(
            string reason,
            int attempt,
            RealtimeRoomGameServerManager manager)
        {
            string roomId = expectedRoomId;

            recoveryPending = false;
            recoveryRunning = false;
            recoveryNetworkProbeAttempts = 0;
            temporaryCleanupTask = null;
            expectedRoomId = string.Empty;
            staleRealtimeConnectionId = string.Empty;

            Debug.LogError(
                "REALTIME_WEBGL_GENERAL_RECONNECT=FAIL" +
                " | reason=" + Safe(reason) +
                " | attempts=" + attempt +
                " | roomId=" + roomId +
                " | realtimeReady=" +
                    (manager != null && manager.IsRealtimeReady) +
                " | roomJoined=" +
                    (manager != null && manager.IsJoinedRoom) +
                " | webglOnly=True" +
                " | windowsPathChanged=False" +
                " | questPathChanged=False");
        }

        //* این تابع فقط نقاط اتصال لازم را از مدیران پایدار پروژه پیدا می کند.
        private void ResolvePrivateMethods()
        {
            Type managerType =
                typeof(RealtimeRoomGameServerManager);

            if (rememberRoomMethod == null)
            {
                rememberRoomMethod = managerType.GetMethod(
                    "RememberRoomForReconnect",
                    BindingFlags.Instance |
                    BindingFlags.NonPublic);
            }

            if (temporaryDisconnectMethod == null)
            {
                temporaryDisconnectMethod = managerType.GetMethod(
                    "DisconnectTransportForTemporaryFailureAsync",
                    BindingFlags.Instance |
                    BindingFlags.NonPublic);
            }

            if (networkCheckMethod == null)
            {
                networkCheckMethod =
                    typeof(StartupNetworkSceneRouter).GetMethod(
                        "CheckNowAsync",
                        BindingFlags.Instance |
                        BindingFlags.NonPublic);
            }
        }

        private bool IsCurrentRecovery(int generation)
        {
            return recoveryPending &&
                   generation == recoveryGeneration &&
                   lifetimeCts != null &&
                   !lifetimeCts.IsCancellationRequested;
        }

        private bool IsCurrentPermanentRecovery(int generation)
        {
            return permanentRecoveryObserved &&
                   generation == permanentRecoveryGeneration &&
                   lifetimeCts != null &&
                   !lifetimeCts.IsCancellationRequested;
        }

        private void OnDestroy()
        {
            recoveryPending = false;
            recoveryGeneration += 1;
            permanentRecoveryObserved = false;
            freshLobbyRecoveryRunning = false;
            permanentRecoveryGeneration += 1;

            if (lifetimeCts != null)
            {
                lifetimeCts.Cancel();
                lifetimeCts.Dispose();
                lifetimeCts = null;
            }

            temporaryCleanupTask = null;
            rememberRoomMethod = null;
            temporaryDisconnectMethod = null;
            networkCheckMethod = null;
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().Replace("|", "/");
        }

        private static string UnwrapExceptionMessage(Exception exception)
        {
            if (exception == null) return string.Empty;

            Exception current = exception;
            while (current.InnerException != null)
            {
                current = current.InnerException;
            }

            return current.Message;
        }
    }
}
#endif
