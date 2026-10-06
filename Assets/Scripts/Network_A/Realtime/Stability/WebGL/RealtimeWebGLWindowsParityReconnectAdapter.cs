#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Bootstrap;
using Network_A.Realtime.Controllers;
using Network_A.Realtime.Transport;
using UnityEngine;
using UnityEngine.Networking;

namespace Network_A.Realtime.Stability.WebGL
{
    //* این Installer فقط Adapter جاافتادن سیگنال Offline مرورگر را در WebGL نصب می کند.
    public static class RealtimeWebGLWindowsParityReconnectInstaller
    {
        private const string RootName =
            "Realtime_WebGL_Windows_Parity_Reconnect";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject root = GameObject.Find(RootName);
            if (root == null) root = new GameObject(RootName);
            UnityEngine.Object.DontDestroyOnLoad(root);

            if (root.GetComponent<RealtimeWebGLWindowsParityReconnectAdapter>() == null)
            {
                root.AddComponent<RealtimeWebGLWindowsParityReconnectAdapter>();
            }

            Debug.Log(
                "REALTIME_WEBGL_WINDOWS_PARITY_RECONNECT=READY" +
                " | owner=RealtimeRoomGameServerManager" +
                " | adapterRole=missing_browser_offline_signal_only" +
                " | webglOnly=True" +
                " | voiceIndependent=True" +
                " | windowsPathChanged=False" +
                " | questPathChanged=False");
        }
    }

    //* Windows از NetworkStateChanged وارد مسیر رسمی Manager می شود. این Adapter فقط وقتی
    //* Browser Offline شده ولی همان Cleanup رسمی اجرا نشده، همان دو API داخلی Manager را صدا می زند.
    [DisallowMultipleComponent]
    public sealed class RealtimeWebGLWindowsParityReconnectAdapter : MonoBehaviour
    {
        private const float MissingOfflineSignalDelaySeconds = 0.75f;
        private const float RetryDelaySeconds = 1.0f;
        private const float FallbackRecoveryBudgetSeconds = 180.0f;
        private const float BrowserHealthProbeTimeoutSeconds = 3.0f;
        private const string BrowserHealthProbeFallbackUrl =
            "https://dev-world-3d.metarang.com/health";

        private CancellationTokenSource lifetimeCts;
        private CancellationTokenSource cycleCts;
        private MethodInfo rememberRoomMethod;
        private MethodInfo temporaryDisconnectMethod;
        private MethodInfo setNetworkStateMethod;

        private bool browserStateObserved;
        private bool browserWasOnline;
        private bool offlineCandidatePending;
        private bool recoveryRunning;
        private int recoveryGeneration;
        private int recoveryAttempts;
        private float browserOfflineObservedAt = -1f;
        private string expectedRoomId = string.Empty;
        private string staleRealtimeConnectionId = string.Empty;

        private void Awake()
        {
            lifetimeCts = new CancellationTokenSource();
        }

        private void Update()
        {
            bool browserOnline =
                WebGLWebSocketRealtimeTransport.IsBrowserOnline;

            if (!browserStateObserved)
            {
                browserStateObserved = true;
                browserWasOnline = browserOnline;
                return;
            }

            if (browserWasOnline && !browserOnline)
            {
                CaptureOfflineCandidate();
            }

            browserWasOnline = browserOnline;

            if (!offlineCandidatePending || recoveryRunning)
                return;

            if (Time.realtimeSinceStartup - browserOfflineObservedAt <
                MissingOfflineSignalDelaySeconds)
            {
                return;
            }

            RealtimeRoomGameServerManager manager =
                RealtimeRoomGameServerManager.Instance;

            if (!IsExpectedRoomContext(manager))
            {
                CancelRecovery("room_context_changed_or_exit_started");
                return;
            }

            string currentConnectionId =
                Safe(manager.RealtimeConnectionId);

            // اگر ConnectionId پاک یا عوض شده، مسیر استاندارد Manager همانند Windows
            // قبلاً Cleanup/Recovery را مالک شده و Adapter نباید مسیر دوم بسازد.
            if (currentConnectionId.Length == 0 ||
                !string.Equals(
                    currentConnectionId,
                    staleRealtimeConnectionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                CompleteBypass(
                    manager,
                    "standard_manager_recovery_already_owned");
                return;
            }

            recoveryRunning = true;
            int generation = recoveryGeneration;
            CancellationToken cancellationToken = cycleCts != null
                ? cycleCts.Token
                : lifetimeCts.Token;

            _ = RunWindowsParityRecoveryAsync(
                generation,
                cancellationToken);
        }

        //* فقط Context خوانده می شود؛ 750ms به مسیر استاندارد Manager فرصت داده می شود.
        private void CaptureOfflineCandidate()
        {
            RealtimeRoomGameServerManager manager =
                RealtimeRoomGameServerManager.Instance;

            if (manager == null ||
                !manager.HasLobbyEntryRequested ||
                manager.IsRoomExitInProgress ||
                string.IsNullOrWhiteSpace(manager.CurrentRoomId) ||
                string.IsNullOrWhiteSpace(manager.RealtimeConnectionId))
            {
                return;
            }

            recoveryGeneration += 1;
            offlineCandidatePending = true;
            recoveryRunning = false;
            recoveryAttempts = 0;
            browserOfflineObservedAt = Time.realtimeSinceStartup;
            expectedRoomId = Safe(manager.CurrentRoomId);
            staleRealtimeConnectionId = Safe(manager.RealtimeConnectionId);

            if (cycleCts != null)
            {
                cycleCts.Cancel();
                cycleCts.Dispose();
            }

            cycleCts = CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeCts.Token);

            Debug.Log(
                "REALTIME_WEBGL_WINDOWS_PARITY_RECONNECT=CANDIDATE" +
                " | roomId=" + expectedRoomId +
                " | staleRealtimeConnectionId=" +
                    staleRealtimeConnectionId +
                " | browserOnline=False" +
                " | mutationApplied=False");
        }

        //* این همان ترتیب Windows است: حفظ Room، آزادسازی Transport، انتظار Online،
        //* EnsureRealtimeReady(true)، Rejoin خود Manager و سپس رویداد RoomJoined برای Binder.
        private async Task RunWindowsParityRecoveryAsync(
            int generation,
            CancellationToken cancellationToken)
        {
            try
            {
                RealtimeRoomGameServerManager manager =
                    RealtimeRoomGameServerManager.Instance;

                if (!IsExpectedRoomContext(manager))
                {
                    CancelRecovery("room_context_changed_before_cleanup");
                    return;
                }

                string currentConnectionId =
                    Safe(manager.RealtimeConnectionId);

                if (currentConnectionId.Length == 0 ||
                    !string.Equals(
                        currentConnectionId,
                        staleRealtimeConnectionId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    CompleteBypass(
                        manager,
                        "standard_manager_recovery_won_race");
                    return;
                }

                ResolveManagerRecoveryMethods();

                if (rememberRoomMethod == null ||
                    temporaryDisconnectMethod == null)
                {
                    FailRecovery(
                        "required_windows_manager_api_missing",
                        manager);
                    return;
                }

                manager.BeginUnifiedRecoveryFromDedicatedDisconnect(
                    "webgl_missing_offline_signal_windows_parity");

                rememberRoomMethod.Invoke(manager, null);

                Task cleanupTask =
                    temporaryDisconnectMethod.Invoke(
                        manager,
                        new object[]
                        {
                            "webgl_missing_offline_signal_windows_parity"
                        }) as Task;

                if (cleanupTask == null)
                {
                    FailRecovery(
                        "temporary_disconnect_task_missing",
                        manager);
                    return;
                }

                Debug.Log(
                    "REALTIME_WEBGL_WINDOWS_PARITY_RECONNECT=FALLBACK_OWNED" +
                    " | roomId=" + expectedRoomId +
                    " | staleRealtimeConnectionId=" +
                        staleRealtimeConnectionId +
                    " | reason=browser_offline_signal_not_consumed_by_manager" +
                    " | sequence=remember_room_then_disconnect_transport");

                await cleanupTask;
                cancellationToken.ThrowIfCancellationRequested();

                float budgetSeconds =
                    manager.IsRecoveryRunning
                        ? Mathf.Max(1.0f, manager.RecoveryRemainingSeconds)
                        : FallbackRecoveryBudgetSeconds;

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
                            "room_context_changed_during_recovery");
                        return;
                    }

                    if (IsFreshSameRoomReady(manager))
                    {
                        CompleteRecovery(manager);
                        return;
                    }

                    bool serverReachable =
                        await IsServerReachableAsync(cancellationToken);

                    if (!serverReachable)
                    {
                        await WaitForRetryAsync(
                            generation,
                            cancellationToken);
                        continue;
                    }

                    recoveryAttempts += 1;

                    Debug.Log(
                        "REALTIME_WEBGL_WINDOWS_PARITY_RECONNECT=ATTEMPT" +
                        " | attempt=" + recoveryAttempts +
                        " | roomId=" + expectedRoomId +
                        " | activeHealthConfirmed=True" +
                        " | operation=EnsureRealtimeReadyAsync_true");

                    bool realtimeReady =
                        await manager.EnsureRealtimeReadyAsync(
                            true,
                            cancellationToken);

                    if (realtimeReady && IsFreshSameRoomReady(manager))
                    {
                        CompleteRecovery(manager);
                        return;
                    }

                    await WaitForRetryAsync(
                        generation,
                        cancellationToken);
                }

                if (IsCurrentRecovery(generation))
                {
                    FailRecovery(
                        "recovery_deadline_exceeded",
                        RealtimeRoomGameServerManager.Instance);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                FailRecovery(
                    "exception_" + Safe(UnwrapExceptionMessage(exception)),
                    RealtimeRoomGameServerManager.Instance);
            }
            finally
            {
                if (generation == recoveryGeneration)
                    recoveryRunning = false;
            }
        }

        private async Task<bool> IsServerReachableAsync(
            CancellationToken cancellationToken)
        {
            if (!WebGLWebSocketRealtimeTransport.IsBrowserOnline)
            {
                return false;
            }

            string url = BuildBrowserHealthProbeUrl();
            float startedAt = Time.realtimeSinceStartup;

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = Mathf.Clamp(
                    Mathf.CeilToInt(BrowserHealthProbeTimeoutSeconds),
                    1,
                    5);

                request.SetRequestHeader("Accept", "application/json");
                request.SetRequestHeader(
                    "Cache-Control",
                    "no-cache, no-store, max-age=0");
                request.SetRequestHeader("Pragma", "no-cache");

                UnityWebRequestAsyncOperation operation =
                    request.SendWebRequest();
                float deadline =
                    startedAt + BrowserHealthProbeTimeoutSeconds;

                while (!operation.isDone &&
                       Time.realtimeSinceStartup < deadline)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        request.Abort();
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    await Task.Yield();
                }

                bool deadlineExceeded = !operation.isDone;
                if (deadlineExceeded) request.Abort();

                cancellationToken.ThrowIfCancellationRequested();

                bool transportFailed =
                    request.result ==
                        UnityWebRequest.Result.ConnectionError ||
                    request.result ==
                        UnityWebRequest.Result.DataProcessingError;
                bool reachable =
                    !deadlineExceeded &&
                    request.responseCode > 0 &&
                    !transportFailed &&
                    WebGLWebSocketRealtimeTransport.IsBrowserOnline;

                if (reachable)
                {
                    PublishVerifiedOnlineState(url);
                }

                bool ready =
                    reachable &&
                    StartupNetworkSceneRouter.IsOnline;
                int elapsedMs = Mathf.Max(
                    0,
                    Mathf.RoundToInt(
                        (Time.realtimeSinceStartup - startedAt) * 1000f));

                Debug.Log(
                    "REALTIME_WEBGL_WINDOWS_PARITY_RECONNECT=HEALTH_" +
                    (ready ? "PASS" : "WAIT") +
                    " | roomId=" + expectedRoomId +
                    " | source=direct_browser_health" +
                    " | url=" + Safe(url) +
                    " | reachable=" + reachable +
                    " | deadlineExceeded=" + deadlineExceeded +
                    " | status=" + request.responseCode +
                    " | result=" + request.result +
                    " | elapsedMs=" + elapsedMs +
                    " | globalNetworkState=" +
                        StartupNetworkSceneRouter.CurrentState);

                return ready;
            }
        }

        private void PublishVerifiedOnlineState(string healthUrl)
        {
            StartupNetworkSceneRouter router =
                StartupNetworkSceneRouter.Instance;

            if (router == null ||
                StartupNetworkSceneRouter.IsOnline)
            {
                return;
            }

            if (setNetworkStateMethod == null)
            {
                setNetworkStateMethod =
                    typeof(StartupNetworkSceneRouter).GetMethod(
                        "SetNetworkState",
                        BindingFlags.Instance |
                        BindingFlags.NonPublic,
                        null,
                        new Type[]
                        {
                            typeof(StartupNetworkSceneRouter.NetworkState),
                            typeof(string)
                        },
                        null);
            }

            if (setNetworkStateMethod == null) return;

            setNetworkStateMethod.Invoke(
                router,
                new object[]
                {
                    StartupNetworkSceneRouter.NetworkState.Online,
                    "webgl_direct_health_recovered | url=" +
                        Safe(healthUrl)
                });

            Debug.Log(
                "REALTIME_WEBGL_WINDOWS_PARITY_RECONNECT=" +
                "GLOBAL_ONLINE_SIGNAL" +
                " | roomId=" + expectedRoomId +
                " | source=verified_direct_browser_health");
        }

        private static string BuildBrowserHealthProbeUrl()
        {
            string baseUrl = BrowserHealthProbeFallbackUrl;

            if (Uri.TryCreate(
                    Application.absoluteURL,
                    UriKind.Absolute,
                    out Uri pageUri))
            {
                baseUrl =
                    pageUri.GetLeftPart(UriPartial.Authority) +
                    "/health";
            }

            string separator = baseUrl.Contains("?") ? "&" : "?";

            return baseUrl +
                   separator +
                   "webglReconnectProbe=" +
                   DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

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

        private bool IsFreshSameRoomReady(
            RealtimeRoomGameServerManager manager)
        {
            if (!IsExpectedRoomContext(manager) ||
                !manager.IsRealtimeReady ||
                !manager.IsJoinedRoom)
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

        private void CompleteBypass(
            RealtimeRoomGameServerManager manager,
            string reason)
        {
            string roomId = expectedRoomId;
            string currentConnectionId =
                manager != null
                    ? Safe(manager.RealtimeConnectionId)
                    : string.Empty;

            ClearRecoveryState();

            Debug.Log(
                "REALTIME_WEBGL_WINDOWS_PARITY_RECONNECT=BYPASS" +
                " | reason=" + Safe(reason) +
                " | roomId=" + roomId +
                " | currentRealtimeConnectionId=" +
                    currentConnectionId +
                " | recoveryOwner=standard_manager");
        }

        private void CompleteRecovery(
            RealtimeRoomGameServerManager manager)
        {
            string roomId = expectedRoomId;
            string previousConnectionId =
                staleRealtimeConnectionId;
            string currentConnectionId =
                manager != null
                    ? Safe(manager.RealtimeConnectionId)
                    : string.Empty;
            int attempts = recoveryAttempts;

            ClearRecoveryState();

            Debug.Log(
                "REALTIME_WEBGL_WINDOWS_PARITY_RECONNECT=PASS" +
                " | attempts=" + attempts +
                " | roomId=" + roomId +
                " | previousRealtimeConnectionId=" +
                    previousConnectionId +
                " | currentRealtimeConnectionId=" +
                    currentConnectionId +
                " | realtimeReady=True" +
                " | roomJoined=True" +
                " | windowsPathChanged=False" +
                " | questPathChanged=False");
        }

        private void CancelRecovery(string reason)
        {
            ClearRecoveryState();

            Debug.Log(
                "REALTIME_WEBGL_WINDOWS_PARITY_RECONNECT=CANCELED" +
                " | reason=" + Safe(reason));
        }

        private void FailRecovery(
            string reason,
            RealtimeRoomGameServerManager manager)
        {
            string roomId = expectedRoomId;
            int attempts = recoveryAttempts;
            bool realtimeReady =
                manager != null && manager.IsRealtimeReady;
            bool roomJoined =
                manager != null && manager.IsJoinedRoom;

            ClearRecoveryState();

            Debug.LogError(
                "REALTIME_WEBGL_WINDOWS_PARITY_RECONNECT=FAIL" +
                " | reason=" + Safe(reason) +
                " | attempts=" + attempts +
                " | roomId=" + roomId +
                " | realtimeReady=" + realtimeReady +
                " | roomJoined=" + roomJoined +
                " | windowsPathChanged=False" +
                " | questPathChanged=False");
        }

        private void ClearRecoveryState()
        {
            offlineCandidatePending = false;
            recoveryRunning = false;
            recoveryAttempts = 0;
            browserOfflineObservedAt = -1f;
            expectedRoomId = string.Empty;
            staleRealtimeConnectionId = string.Empty;
        }

        private void ResolveManagerRecoveryMethods()
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
        }

        private bool IsCurrentRecovery(int generation)
        {
            return offlineCandidatePending &&
                   generation == recoveryGeneration &&
                   lifetimeCts != null &&
                   !lifetimeCts.IsCancellationRequested &&
                   cycleCts != null &&
                   !cycleCts.IsCancellationRequested;
        }

        private void OnDestroy()
        {
            offlineCandidatePending = false;
            recoveryGeneration += 1;

            if (cycleCts != null)
            {
                cycleCts.Cancel();
                cycleCts.Dispose();
                cycleCts = null;
            }

            if (lifetimeCts != null)
            {
                lifetimeCts.Cancel();
                lifetimeCts.Dispose();
                lifetimeCts = null;
            }

            rememberRoomMethod = null;
            temporaryDisconnectMethod = null;
            setNetworkStateMethod = null;
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().Replace("|", "/");
        }

        private static string UnwrapExceptionMessage(
            Exception exception)
        {
            if (exception == null) return string.Empty;

            Exception current = exception;
            while (current.InnerException != null)
                current = current.InnerException;

            return current.Message;
        }
    }
}
#endif
