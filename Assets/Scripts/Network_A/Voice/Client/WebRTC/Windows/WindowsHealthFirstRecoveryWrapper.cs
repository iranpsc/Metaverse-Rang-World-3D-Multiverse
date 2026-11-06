#if (UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN) && !UNITY_WEBGL
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Auth;
using Network_A.Bootstrap;
using Network_A.Realtime.Controllers;
using Network_A.UI;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace Network_A.Voice.Client.WebRTC.Windows
{
    // WindowsPlayer-only network reachability authority.
    // Does not replace/reimplement Realtime, Dedicated, or SFU sessions.
    // The existing RealtimeRoomGameServerManager remains the only connection owner.
    [DefaultExecutionOrder(-9000)]
    [DisallowMultipleComponent]
    public sealed class WindowsHealthFirstRecoveryWrapper : MonoBehaviour
    {
        private const string DedicatedHealthUrl = "https://dev-world-3d.metarang.com/health";
        private const float ProbeTimeoutSeconds = 3f;
        private const float ProbeIntervalSeconds = 1f;
        // A single unanswered HTTP probe is not proof of a lost connection.
        private const int MissingHttpResponsesToConfirmOutage = 2;
        private const float RecoveryExtensionSeconds = 180f;
        private const float RecoveryExtensionThresholdSeconds = 25f;
        private const string HealthStatusMessageId = "GLOBAL_NETWORK_STATE";
        private const float ExtendedHealthUiExplanationSeconds = 9f;

        private static WindowsHealthFirstRecoveryWrapper instance;
        private StartupNetworkSceneRouter router;
        private MethodInfo routerSetState;
        private FieldInfo managerRecoveryDeadline;
        private bool routerOwned;
        private bool alive;
        private bool probing;
        private bool hasVerdict;
        private bool lastReachable;
        private bool lastVerifiedHealth200;
        private float nextProbeAt;
        private float nextRouterScanAt;
        private float nextDeadlineCheckAt;
        private float nextRecoveryWakeCheckAt;
        private float lastHealthResponseAt = -1f;
        private float nextStatusLogAt;
        private string guardedRoomId = string.Empty;
        private int probeGeneration;
        private int lastHttpStatus;
        private int wakeGeneration;
        private bool wakeRunning;
        private float nextVoiceRecoveryPumpAt;
        private int consecutiveNoHttpResponses;
        private float firstNoHttpResponseAt = -1f;
        private bool provisionalHealthCheckingVisible;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (Application.isEditor || instance != null) return;
            var host = new GameObject("VME2_Windows_Health_First_Owner");
            DontDestroyOnLoad(host);
            instance = host.AddComponent<WindowsHealthFirstRecoveryWrapper>();
        }

        // This call is after the first scene's Awake and before its Start: the
        // old gRPC-first StartupRouter monitor must never become a second verdict owner.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void TakeInitialOwnership()
        {
            if (instance != null) instance.TryAdoptRouter("after_scene_load");
        }

        private void Awake()
        {
            alive = true;
            routerSetState = typeof(StartupNetworkSceneRouter).GetMethod(
                "SetNetworkState", BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(StartupNetworkSceneRouter.NetworkState), typeof(string) }, null);
            managerRecoveryDeadline = typeof(RealtimeRoomGameServerManager).GetField(
                "unifiedRecoveryDeadlineAt", BindingFlags.Instance | BindingFlags.NonPublic);
            SceneManager.sceneLoaded += OnSceneLoaded;
            RealtimeRoomGameServerManager.OnRoomJoinedFor3D += OnRoomJoined;
            RealtimeRoomGameServerManager.OnRoomLeftFor3D += OnRoomLeft;

            // Reflection is only used against exact API signatures from the checked-in
            // source snapshot; any mismatch refuses takeover rather than guessing.
            if (routerSetState == null || routerSetState.ReturnType != typeof(void))
                Debug.LogError("VME2_WIN_HEALTH_AUTHORITY=UNSUPPORTED | missing=SetNetworkState | takeover=false");
            if (managerRecoveryDeadline == null || managerRecoveryDeadline.FieldType != typeof(float))
                Debug.LogWarning("VME2_WIN_HEALTH_RECOVERY_BUDGET=UNSUPPORTED | no_private_field_mutation");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            TryAdoptRouter("scene_loaded:" + scene.name);
        }

        private bool TryAdoptRouter(string source)
        {
            if (!alive || routerSetState == null || !ServerConfigBootstrap.HasAppliedConfiguration)
                return false;
            // Fail open in Local testing: leave the existing local Router untouched.
            if (ServerConfigBootstrap.AppliedEnvironment != ServerConfigBootstrap.ServerEnvironment.Dedicated)
            {
                if (nextStatusLogAt == 0f)
                {
                    nextStatusLogAt = float.PositiveInfinity;
                    Debug.Log("VME2_WIN_HEALTH_AUTHORITY=SKIPPED | reason=local_environment | existingRouterPreserved=true");
                }
                return false;
            }
            var current = StartupNetworkSceneRouter.Instance;
            if (current == null) return false;

            if (routerOwned && router == current)
            {
                if (current.enabled) current.enabled = false;
                return true;
            }

            // No changes to the shared component source or serialized scene.
            // Setting enabled=false before Start prevents its gRPC-first monitor
            // and prevents contradictory InternetUnavailable UI/event decisions.
            current.enabled = false;
            router = current;
            routerOwned = true;
            Debug.Log("VME2_WIN_HEALTH_AUTHORITY=OWNER_SELECTED" +
                      " | source=" + source +
                      " | sharedRouterSourceChanged=false" +
                      " | oldGrpcFirstMonitor=component_disabled" +
                      " | onlyHttp200CanDeclareOnline=true");
            return true;
        }

        private void Update()
        {
            if (!alive) return;
            float now = Time.realtimeSinceStartup;
            if (now >= nextRouterScanAt)
            {
                nextRouterScanAt = now + 0.2f;
                TryAdoptRouter("runtime_scan");
                ObserveRoomContext();
            }
            if (now >= nextDeadlineCheckAt)
            {
                nextDeadlineCheckAt = now + 1f;
                ProtectExistingManagerRecoveryBudget();
            }
            // If a legacy gRPC silent preflight leaves Manager waiting while
            // /health is still fresh-200, wake the SAME manager through its own
            // serialized EnsureRealtimeReadyAsync gate. No second transport owner.
            if (now >= nextRecoveryWakeCheckAt)
            {
                nextRecoveryWakeCheckAt = now + 5f;
                if (lastVerifiedHealth200 && lastHealthResponseAt >= 0f &&
                    now - lastHealthResponseAt < 4f && !wakeRunning)
                    _ = WakeManagerIfWaitingAsync(++wakeGeneration);
            }
            if (now >= nextVoiceRecoveryPumpAt)
            {
                // Observe late Dedicated ConnectionId changes quickly after the
                // original Manager has completed room recovery. Still ONE HEALTH
                // authority; no new probe or Realtime/Dedicated socket is opened.
                nextVoiceRecoveryPumpAt = now + 0.05f;
                // The same HEALTH verdict owner schedules Voice recovery.
                // A service error NEVER changes the Internet verdict.
                WindowsVoiceRecoveryButtons.PumpHealthVoiceRecovery(
                    lastVerifiedHealth200 && lastHealthResponseAt >= 0f &&
                    now - lastHealthResponseAt < 4f);
            }
            if (!routerOwned || probing || now < nextProbeAt) return;
            if (ServerConfigBootstrap.HasAppliedConfiguration &&
                ServerConfigBootstrap.AppliedEnvironment != ServerConfigBootstrap.ServerEnvironment.Dedicated)
            {
                if (now >= nextStatusLogAt)
                {
                    nextStatusLogAt = now + 30f;
                    Debug.LogError("VME2_WIN_HEALTH_AUTHORITY=UNSUPPORTED_ENV | expected=Dedicated | source=local_requires_separate_health_origin");
                }
                return;
            }
            nextProbeAt = now + ProbeIntervalSeconds;
            _ = ProbeHealthOnceAsync(++probeGeneration);
        }

        private async Task ProbeHealthOnceAsync(int generation)
        {
            if (probing) return;
            probing = true;
            bool reached = false;
            int status = 0;
            string error = string.Empty;
            float start = Time.realtimeSinceStartup;
            try
            {
                using (var request = UnityWebRequest.Get(DedicatedHealthUrl))
                {
                    request.timeout = (int)ProbeTimeoutSeconds;
                    request.SetRequestHeader("Accept", "application/json");
                    request.SetRequestHeader("Cache-Control", "no-cache, no-store, max-age=0");
                    request.SetRequestHeader("Pragma", "no-cache");
                    var operation = request.SendWebRequest();
                    float deadline = Time.realtimeSinceStartup + ProbeTimeoutSeconds;
                    while (!operation.isDone && Time.realtimeSinceStartup < deadline && alive)
                        await Task.Yield();
                    if (!alive) { request.Abort(); return; }
                    if (!operation.isDone) request.Abort();
                    status = (int)request.responseCode;
                    reached = operation.isDone && status == 200 &&
                        request.result == UnityWebRequest.Result.Success;
                    error = request.error ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ":" + ex.Message;
            }
            finally
            {
                probing = false;
            }
            if (!alive || generation != probeGeneration) return;
            lastVerifiedHealth200 = reached;
            lastHealthResponseAt = Time.realtimeSinceStartup;
            lastHttpStatus = status;
            TrackNoHttpResponseForUi(reached, status);
            if (!hasVerdict || reached != lastReachable || Time.realtimeSinceStartup >= nextStatusLogAt)
            {
                Debug.Log("VME2_WIN_HEALTH_200=" + (reached ? "PASS" : "WAIT") +
                    " | status=" + status + " | latencyMs=" +
                    Mathf.RoundToInt((Time.realtimeSinceStartup - start) * 1000f) +
                    " | error=" + Sanitize(error) + " | url=" + DedicatedHealthUrl +
                    " | verdictOwner=windows_health_only");
                nextStatusLogAt = Time.realtimeSinceStartup + 30f;
            }

            // One missed HTTP response may be a per-process timeout even while
            // other clients on the same connection have fresh /health 200.
            // Do not notify the shared router or arm Voice recovery until the
            // *next completed health probe* also has no HTTP response.
            // The existing Update loop schedules that independent probe.
            if (!reached && status == 0 &&
                consecutiveNoHttpResponses < MissingHttpResponsesToConfirmOutage)
            {
                provisionalHealthCheckingVisible = routerOwned && router != null &&
                    GlobalMessageManager.IsReady;
                if (provisionalHealthCheckingVisible)
                    RefreshNoHttpResponseUi(error);
                Debug.Log("VME2_WIN_HEALTH_CONFIRMATION=PENDING" +
                    " | missingResponses=" + consecutiveNoHttpResponses +
                    " | required=" + MissingHttpResponsesToConfirmOutage +
                    " | routerStatePreserved=true | voiceOutageNotArmed=true");
                return;
            }

            // Clear only the checking message published by this wrapper if a
            // following probe resolved it without changing the router state.
            if (provisionalHealthCheckingVisible)
            {
                provisionalHealthCheckingVisible = false;
                if ((reached || status != 0) && GlobalMessageManager.IsReady)
                    GlobalMessageManager.Clear(HealthStatusMessageId);
            }

            bool rising = hasVerdict && !lastReachable && reached;
            bool falling = hasVerdict && lastReachable && !reached;
            // Snapshot active in-room Voice immediately at the true online->offline
            // edge, before the shared network/voice managers clear their state.
            if (falling)
                WindowsVoiceRecoveryButtons.NotifyHealthLostForVoice();
            bool changed = !hasVerdict || lastReachable != reached;
            hasVerdict = true;
            lastReachable = reached;
            if (!changed)
            {
                if (!reached && status == 0) RefreshNoHttpResponseUi(error);
                return;
            }

            if (!TryAdoptRouter("health_verdict") || router == null) return;
            try
            {
                StartupNetworkSceneRouter.NetworkState state = reached
                    ? StartupNetworkSceneRouter.NetworkState.Online
                    : (status == 0
                        ? StartupNetworkSceneRouter.NetworkState.InternetUnavailable
                        : StartupNetworkSceneRouter.NetworkState.ServerUnavailable);
                routerSetState.Invoke(router, new object[] {
                    state, "windows_get_health_only | httpStatus=" + status +
                        " | healthy=" + reached + " | origin=" + DedicatedHealthUrl });

                if (!reached && status == 0) RefreshNoHttpResponseUi(error);

                if (rising)
                {
                    // Voice service registry can lose a Realtime entry while
                    // Unity still sees a valid local gRPC connection. Recover
                    // only after the canonical manager and Dedicated are ready.
                    WindowsVoiceRecoveryButtons.NotifyHealthRecoveredForVoice();
                    Debug.Log("VME2_WIN_HEALTH_RECOVERY=WAKE | freshHttp200=true | next=existing_manager_and_voice_registry_repair");
                    _ = WakeManagerIfWaitingAsync(++wakeGeneration);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("VME2_WIN_HEALTH_AUTHORITY=STATE_APPLY_FAILED | " + Sanitize(ex.Message));
            }
        }

        // Counts unanswered probes for both UI detail and the verdict-confirmation gate.
        private void TrackNoHttpResponseForUi(bool reached, int status)
        {
            if (!reached && status == 0)
            {
                if (consecutiveNoHttpResponses == 0)
                    firstNoHttpResponseAt = Time.realtimeSinceStartup;
                consecutiveNoHttpResponses++;
                return;
            }
            if (consecutiveNoHttpResponses > 0)
                Debug.Log("VME2_WIN_HEALTH_UI=RESOLVED" +
                    " | previousMissingHttpResponses=" + consecutiveNoHttpResponses +
                    " | resolvedHttpStatus=" + status);
            consecutiveNoHttpResponses = 0;
            firstNoHttpResponseAt = -1f;
        }

        // Refresh the SAME existing network message on every unanswered health probe.
        // For prolonged failure, report the actual evidence instead of a frozen offline claim.
        private void RefreshNoHttpResponseUi(string lastRequestError)
        {
            if (!routerOwned || router == null || !GlobalMessageManager.IsReady) return;
            float elapsed = firstNoHttpResponseAt < 0f ? 0f :
                Mathf.Max(0f, Time.realtimeSinceStartup - firstNoHttpResponseAt);
            bool prolonged = elapsed >= ExtendedHealthUiExplanationSeconds;
            string detail = "windows_get_health_only | httpStatus=0 | noHttpResponse=true" +
                " | retryCount=" + consecutiveNoHttpResponses +
                " | waitingSeconds=" + elapsed.ToString("F1") +
                " | lastRequestError=" + Sanitize(string.IsNullOrWhiteSpace(lastRequestError)
                    ? "no_http_response_or_manual_timeout" : lastRequestError) +
                " | origin=" + DedicatedHealthUrl;
            GlobalMessageManager.ShowNetworkChecking(HealthStatusMessageId,
                prolonged ? "پاسخ سرور هنوز دریافت نشده" : "در حال بررسی ارتباط",
                prolonged
                    ? "بررسی سلامت سرور هنوز پاسخ معتبر نگرفته است. تلاش مجدد خودکار ادامه دارد؛ علت قطعی اینترنت تأیید نشده است."
                    : "درخواست سلامت هنوز پاسخ نداده است. در حال تلاش مجدد خودکار هستیم؛ اتصال اینترنت لزوماً قطع نیست.",
                detail);
            Debug.Log("VME2_WIN_HEALTH_UI=NO_HTTP_RESPONSE" +
                " | attempts=" + consecutiveNoHttpResponses +
                " | elapsedSeconds=" + elapsed.ToString("F1") +
                " | prolonged=" + prolonged +
                " | error=" + Sanitize(lastRequestError));
        }

        private void ObserveRoomContext()
        {
            var manager = RealtimeRoomGameServerManager.Instance;
            if (manager == null) return;
            if (!manager.HasLobbyEntryRequested || manager.IsRoomExitInProgress ||
                manager.IsSwitchingFromPublicLobbyToBuildingRoom)
            {
                // Intentional leaving/changing room must never resurrect a stale room.
                guardedRoomId = string.Empty;
                return;
            }
            if (manager.IsJoinedRoom && !string.IsNullOrWhiteSpace(manager.CurrentRoomId))
                guardedRoomId = manager.CurrentRoomId;
        }

        private void OnRoomJoined(string roomId)
        {
            if (!string.IsNullOrWhiteSpace(roomId)) guardedRoomId = roomId;
        }

        private void OnRoomLeft(string roomId)
        {
            if (string.Equals(guardedRoomId, roomId, StringComparison.Ordinal))
            {
                guardedRoomId = string.Empty;
                Debug.Log("VME2_WIN_HEALTH_RECOVERY=ROOM_RELEASED | reason=room_left_event");
            }
        }

        private void ProtectExistingManagerRecoveryBudget()
        {
            var manager = RealtimeRoomGameServerManager.Instance;
            if (manager == null || managerRecoveryDeadline == null ||
                managerRecoveryDeadline.FieldType != typeof(float) ||
                !manager.HasLobbyEntryRequested || !manager.IsRecoveryRunning ||
                manager.IsRoomExitInProgress || string.IsNullOrWhiteSpace(guardedRoomId) ||
                !string.Equals(guardedRoomId, manager.CurrentRoomId, StringComparison.Ordinal))
                return;
            try
            {
                float remaining = (float)managerRecoveryDeadline.GetValue(manager) - Time.realtimeSinceStartup;
                if (remaining > RecoveryExtensionThresholdSeconds) return;
                managerRecoveryDeadline.SetValue(manager,
                    Time.realtimeSinceStartup + RecoveryExtensionSeconds);
                Debug.Log("VME2_WIN_HEALTH_RECOVERY=BUDGET_EXTENDED" +
                    " | roomPreserved=true | automaticRetryOwner=existing_manager" +
                    " | previousRemainingSeconds=" + remaining.ToString("F1") +
                    " | health200=" + lastVerifiedHealth200 +
                    " | note=manager_private_field_guarded");
            }
            catch (Exception ex)
            {
                Debug.LogError("VME2_WIN_HEALTH_RECOVERY=BUDGET_GUARD_FAILED | " + Sanitize(ex.Message));
            }
        }

        // Only an emergency wake after the *existing* manager remains WaitingForNetwork
        // despite fresh HTTP 200. The manager's own connectionGate owns serialization,
        // authentication and room join. This wrapper never creates a Realtime transport.
        private async Task WakeManagerIfWaitingAsync(int generation)
        {
            if (wakeRunning) return;
            wakeRunning = true;
            try
            {
                await Task.Yield(); // Allow canonical OnNetworkStateChanged to run first.
                var manager = RealtimeRoomGameServerManager.Instance;
                if (!alive || generation != wakeGeneration || !lastVerifiedHealth200 ||
                    lastHealthResponseAt < 0f || Time.realtimeSinceStartup - lastHealthResponseAt > 4f ||
                    manager == null || !manager.HasLobbyEntryRequested ||
                    manager.IsRoomExitInProgress || manager.IsPermanentRecoveryFailureWaitingForLobby ||
                    !manager.IsRecoveryRunning || manager.IsRealtimeReady ||
                    RealtimeRoomGameServerManager.CurrentState != RealtimeRoomGameServerManager.FlowState.WaitingForNetwork ||
                    GlobalAuthManager.CurrentAuthState != GlobalAuthManager.AuthState.Authenticated)
                    return;
                Debug.Log("VME2_WIN_HEALTH_RECOVERY=MANAGER_WAKE | cause=http200_no_native_grpc_preflight | owner=existing_manager");
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    bool okay = await manager.EnsureRealtimeReadyAsync(true, timeout.Token);
                    Debug.Log("VME2_WIN_HEALTH_RECOVERY=MANAGER_WAKE_RESULT | ready=" + okay +
                              " | joined=" + manager.IsJoinedRoom +
                              " | roomId=" + Sanitize(manager.CurrentRoomId));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("VME2_WIN_HEALTH_RECOVERY=MANAGER_WAKE_ERROR | " + Sanitize(ex.Message));
            }
            finally
            {
                wakeRunning = false;
            }
        }

        private static string Sanitize(string input)
        {
            return (input ?? string.Empty).Replace("\n", " ").Replace("\r", " ").Replace("|", "/");
        }

        private void OnDestroy()
        {
            alive = false;
            probeGeneration++;
            wakeGeneration++;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            RealtimeRoomGameServerManager.OnRoomJoinedFor3D -= OnRoomJoined;
            RealtimeRoomGameServerManager.OnRoomLeftFor3D -= OnRoomLeft;
            if (instance == this) instance = null;
        }
    }
}
#endif
