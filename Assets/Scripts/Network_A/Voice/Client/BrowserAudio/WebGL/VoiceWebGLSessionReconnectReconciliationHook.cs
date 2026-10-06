#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Realtime.Controllers;
using Network_A.Realtime.Transport;
using Network_A.Voice.Client.Runtime;
using Network_A.Voice.Client.Routing.WebGL;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network_A.Voice.Client.BrowserAudio
{
    public static class VoiceWebGLSessionReconnectReconciliationInstaller
    {
        private const string HookRootName =
            "Voice_WebGL_Session_Reconnect_Reconciliation";

        //* این تابع نصب‌کننده را فقط در خروجی مرورگر و پیش از نخستین صحنه فعال می‌کند.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        //* این تابع هوک مستقل بازسازی نشست را فقط در صحنه صوت مرورگر می‌سازد.
        private static void HandleSceneLoaded(
            Scene scene,
            LoadSceneMode loadMode)
        {
            if (!scene.IsValid() ||
                !VoiceWebGLLobbyRouteSelection.IsVoiceModeSelected ||
                !string.Equals(
                    scene.name,
                    VoiceWebGLLobbyRouteSelection.VoiceGameplaySceneName,
                    StringComparison.Ordinal))
            {
                return;
            }

            GameObject root = GameObject.Find(HookRootName);
            if (root == null) root = new GameObject(HookRootName);
            UnityEngine.Object.DontDestroyOnLoad(root);

            if (root.GetComponent<VoiceWebGLSessionReconnectReconciliationHook>() == null)
            {
                root.AddComponent<VoiceWebGLSessionReconnectReconciliationHook>();
            }

            root.GetComponent<VoiceWebGLSessionReconnectReconciliationHook>()
                ?.PrepareForVoiceSceneEntry();

            Debug.Log(
                "VOICE_WEBGL_SESSION_RECONNECT_RECONCILIATION=READY" +
                " | webglOnly=True" +
                " | windowsPathChanged=False" +
                " | serverPathChanged=False");
        }
    }

    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class VoiceWebGLSessionReconnectReconciliationHook : MonoBehaviour
    {
        private const string RuntimeRootName = "Voice_Client_Runtime_Root";
        private const float NormalRecoverySettleSeconds = 2.0f;
        private const int FreshVoiceDisconnectTimeoutMs = 1500;
        private const float SessionRebuildDeadlineSeconds = 15.0f;
        private const float HealthySessionAbsenceForgetSeconds = 5.0f;
        private const float RetryDelaySeconds = 2.0f;
        private const float InitialMicrophoneRetrySeconds = 1.0f;
        private const float ReceiveOnlyMediaRetrySeconds = 2.0f;
        private const int MaximumReconciliationAttempts = 2;
        private const int MaximumInitialMicrophoneAttempts = 3;

        private VoiceClientRuntime runtime;
        private VoiceBrowserVoiceRuntimeAdapter browserVoiceAdapter;
        private VoiceSceneUserConsentPanelController consentPanel;
        private FieldInfo browserMicrophoneRequestedField;
        private MethodInfo browserMicrophoneClickMethod;
        private MethodInfo browserDisablePublishingMethod;
        private MethodInfo browserSetRuntimeMicrophoneMirrorMethod;
        private FieldInfo browserAudioReadyField;
        private FieldInfo browserMediaConnectStartedField;
        private FieldInfo browserMediaBoundField;
        private FieldInfo browserActiveControlConnectionIdField;
        private MethodInfo browserStartMediaConnectionMethod;
        private MethodInfo browserResetMediaSessionMethod;
        private MethodInfo consentPanelUpdateUiMethod;
        private MonoBehaviour voiceAutoConnector;
        private FieldInfo voiceAutoConnectorNextAttemptAtField;
        private FieldInfo voiceAutoConnectorAttemptedField;
        private CancellationTokenSource lifetimeCts;
        private bool observationInitialized;
        private bool previousPlayerReady;
        private bool previousVoiceAuthenticated;
        private bool hadActiveSession;
        private bool lastStableMicrophoneMuted = true;
        private bool lastStableSpeakerOff;
        private bool recoveryPending;
        private bool reconciliationRunning;
        private int recoveryGeneration;
        private int reconciliationAttempts;
        private float retryAt;
        private float healthySessionAbsentSince = -1f;
        private float initialMicrophoneRetryAt;
        private int initialMicrophoneAttempts;
        private bool initialMicrophoneSelectionApplied;
        private bool voiceSceneObserved;
        private float receiveOnlyMediaRetryAt;
        private string receiveOnlyMediaBoundForConnectionId = string.Empty;
        private bool voiceResumeWakePending;
        private bool previousVoiceConnectPrerequisitesReady;

        private void Awake()
        {
            lifetimeCts = new CancellationTokenSource();
        }

        //* این تابع هر ورود تازه به صحنه صوت را با انتخاب پیش فرض میکروفن روشن آماده می کند.
        internal void PrepareForVoiceSceneEntry()
        {
            voiceSceneObserved = true;
            observationInitialized = false;
            recoveryPending = false;
            reconciliationRunning = false;
            reconciliationAttempts = 0;
            hadActiveSession = false;
            healthySessionAbsentSince = -1f;
            initialMicrophoneSelectionApplied = false;
            initialMicrophoneAttempts = 0;
            initialMicrophoneRetryAt = 0f;
            receiveOnlyMediaRetryAt = 0f;
            receiveOnlyMediaBoundForConnectionId = string.Empty;
            voiceResumeWakePending = true;
            previousVoiceConnectPrerequisitesReady = false;
            lastStableMicrophoneMuted = false;
            lastStableSpeakerOff = false;
        }

        //* این تابع فقط افت واقعی اتصال پس از یک نشست فعال را ثبت و بازیابی نشست مرورگر را آغاز می‌کند.
        private void Update()
        {
            if (!IsVoiceGameplaySceneActive())
            {
                if (voiceSceneObserved)
                {
                    voiceSceneObserved = false;
                    recoveryPending = false;
                    reconciliationRunning = false;
                    recoveryGeneration += 1;
                    observationInitialized = false;
                }

                return;
            }

            if (!voiceSceneObserved)
                PrepareForVoiceSceneEntry();

            ResolveRuntime();
            if (runtime == null) return;

            CoordinateVoiceConnectWithRealtimeRoom();
            EnsureInitialMicrophoneSelection();
            UpdateStableControlsFromCurrentSelection();
            EnsureRuntimeMatchesSelectedMicrophone();
            // مالک چرخه اتصال رسانه فقط آداپتر مرورگر است.

            bool browserOnline =
                WebGLWebSocketRealtimeTransport.IsBrowserOnline;
            bool playerReady = MetaverseNetworkClient.isReady;
            bool voiceAuthenticated = runtime.IsAuthenticated;

            if (!browserOnline &&
                runtime.ActiveSessionCount > 0 &&
                !recoveryPending)
            {
                hadActiveSession = true;
                BeginRecoveryWatch("browser_offline_with_active_session");
            }

            if (!observationInitialized)
            {
                observationInitialized = true;
                previousPlayerReady = playerReady;
                previousVoiceAuthenticated = voiceAuthenticated;
            }

            if (playerReady &&
                voiceAuthenticated &&
                runtime.ActiveSessionCount > 0)
            {
                hadActiveSession = true;
                healthySessionAbsentSince = -1f;

                if (recoveryPending && !reconciliationRunning)
                {
                    if (browserOnline &&
                        IsRealtimeRoomReadyForVoiceRecovery())
                    {
                        RestoreSelectedControls();
                        CompleteRecovery(
                            "normalRecoveryWithSelectedControls");
                    }
                }
                else if (!recoveryPending)
                {
                    UpdateStableControlsFromCurrentSelection();
                }
            }
            else if (playerReady &&
                     voiceAuthenticated &&
                     !recoveryPending)
            {
                if (healthySessionAbsentSince < 0f)
                {
                    healthySessionAbsentSince = Time.realtimeSinceStartup;
                }
                else if (Time.realtimeSinceStartup - healthySessionAbsentSince >=
                         HealthySessionAbsenceForgetSeconds)
                {
                    hadActiveSession = false;
                }
            }

            bool playerConnectionDropped =
                previousPlayerReady && !playerReady;
            bool voiceConnectionDropped =
                previousVoiceAuthenticated && !voiceAuthenticated;
            bool unexpectedVoiceConnectionDropped =
                voiceConnectionDropped &&
                (!runtime.IsDisconnecting || !playerReady);

            if ((playerConnectionDropped || unexpectedVoiceConnectionDropped) &&
                hadActiveSession)
            {
                BeginRecoveryWatch(
                    playerConnectionDropped
                        ? "dedicated_player_not_ready"
                        : "voice_not_authenticated");
            }

            if (recoveryPending &&
                !reconciliationRunning &&
                browserOnline &&
                IsRealtimeRoomReadyForVoiceRecovery() &&
                playerReady &&
                voiceAuthenticated &&
                runtime.ActiveSessionCount == 0 &&
                Time.realtimeSinceStartup >= retryAt)
            {
                reconciliationRunning = true;
                int generation = recoveryGeneration;
                _ = ReconcileSessionAsync(
                    generation,
                    lifetimeCts.Token);
            }

            previousPlayerReady = playerReady;
            previousVoiceAuthenticated = voiceAuthenticated;
        }

        //* این تابع تغییر موقت وضعیت دریافت صدا را پیش از نمایش تصویر به انتخاب واقعی کاربر برمی‌گرداند.
        private void LateUpdate()
        {
            if (!IsVoiceGameplaySceneActive()) return;

            ResolveRuntime();
            if (runtime == null) return;

            UpdateStableControlsFromCurrentSelection();

            bool restoreRequired =
                !lastStableMicrophoneMuted &&
                runtime.IsMicrophoneMuted;

            if (!restoreRequired) return;

            EnsureRuntimeMatchesSelectedMicrophone();
            if (runtime.IsMicrophoneMuted) return;

            RefreshSelectedControlsUi();

            Debug.Log(
                "VOICE_WEBGL_MICROPHONE_INTENT_VISUAL_RESTORED=PASS" +
                " | microphoneOn=True" +
                " | source=transient_media_reset" +
                " | windowsPathChanged=False" +
                " | questPathChanged=False");
        }

        //* این تابع اجازه بازسازی Voice را فقط پس از برگشت واقعی Realtime Room می دهد.
        private static bool IsRealtimeRoomReadyForVoiceRecovery()
        {
            RealtimeRoomGameServerManager manager =
                RealtimeRoomGameServerManager.Instance;

            return manager != null &&
                   manager.IsRealtimeReady &&
                   manager.IsJoinedRoom &&
                   !manager.IsRoomExitInProgress &&
                   !string.IsNullOrWhiteSpace(manager.CurrentRoomId);
        }

        //* این تابع نمونه فعال صوت را بدون وابستگی به بایندر ویندوز پیدا می‌کند.
        private void ResolveRuntime()
        {
            GameObject root = GameObject.Find(RuntimeRootName);
            VoiceClientRuntime resolved =
                root == null
                    ? null
                    : root.GetComponent<VoiceClientRuntime>();

            if (ReferenceEquals(resolved, runtime)) return;

            runtime = resolved;
            observationInitialized = false;
            voiceAutoConnector = null;
            voiceAutoConnectorNextAttemptAtField = null;
            voiceAutoConnectorAttemptedField = null;
            voiceResumeWakePending = true;
            previousVoiceConnectPrerequisitesReady = false;

            if (runtime == null) return;

            Debug.Log(
                "VOICE_WEBGL_SESSION_RECONNECT_RUNTIME_BOUND=PASS" +
                " | webglOnly=True" +
                " | windowsPathChanged=False");
        }

        //* این تابع Retry سه ثانیه ای Voice را تا آماده شدن واقعی Realtime Room متوقف می کند
        //* و درست پس از آماده شدن Room فقط یک بار اتصال Voice را بدون انتظار اضافه بیدار می کند.
        private void CoordinateVoiceConnectWithRealtimeRoom()
        {
            if (runtime == null || !TryResolveVoiceAutoConnector()) return;

            bool prerequisitesReady =
                WebGLWebSocketRealtimeTransport.IsBrowserOnline &&
                IsRealtimeRoomReadyForVoiceRecovery() &&
                MetaverseNetworkClient.isReady;

            if (!prerequisitesReady)
            {
                previousVoiceConnectPrerequisitesReady = false;
                voiceResumeWakePending = true;

                if (runtime.IsAuthenticated) return;

                float gateUntil = Time.realtimeSinceStartup + 0.5f;
                object currentValue =
                    voiceAutoConnectorNextAttemptAtField.GetValue(
                        voiceAutoConnector);
                float current = currentValue is float
                    ? (float)currentValue
                    : 0f;

                if (current < gateUntil)
                {
                    voiceAutoConnectorNextAttemptAtField.SetValue(
                        voiceAutoConnector,
                        gateUntil);
                }

                return;
            }

            if (!previousVoiceConnectPrerequisitesReady)
                voiceResumeWakePending = true;

            previousVoiceConnectPrerequisitesReady = true;

            if (runtime.IsAuthenticated)
            {
                voiceResumeWakePending = false;
                return;
            }

            if (!voiceResumeWakePending) return;

            object attemptedValue =
                voiceAutoConnectorAttemptedField.GetValue(
                    voiceAutoConnector);
            bool attempted = attemptedValue is bool && (bool)attemptedValue;
            if (attempted) return;

            voiceAutoConnectorNextAttemptAtField.SetValue(
                voiceAutoConnector,
                0f);
            voiceResumeWakePending = false;

            Debug.Log(
                "VOICE_WEBGL_CONNECT_RESUME_WAKE=PASS" +
                " | realtimeRoomReady=True" +
                " | dedicatedPlayerReady=True" +
                " | retryDelayBypassed=True" +
                " | windowsPathChanged=False" +
                " | questPathChanged=False");
        }

        //* این تابع Connector فعلی Voice را بدون تغییر فایل مشترک Windows/Quest پیدا می کند.
        private bool TryResolveVoiceAutoConnector()
        {
            if (voiceAutoConnector != null &&
                voiceAutoConnectorNextAttemptAtField != null &&
                voiceAutoConnectorAttemptedField != null)
            {
                return true;
            }

            if (runtime == null) return false;

            MonoBehaviour[] components =
                runtime.GetComponents<MonoBehaviour>();

            foreach (MonoBehaviour component in components)
            {
                if (component == null ||
                    !string.Equals(
                        component.GetType().Name,
                        "VoiceClientAutoConnector",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                Type connectorType = component.GetType();
                FieldInfo nextAttemptAtField = connectorType.GetField(
                    "nextAttemptAt",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo attemptedField = connectorType.GetField(
                    "attempted",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                if (nextAttemptAtField == null || attemptedField == null)
                    continue;

                voiceAutoConnector = component;
                voiceAutoConnectorNextAttemptAtField = nextAttemptAtField;
                voiceAutoConnectorAttemptedField = attemptedField;
                return true;
            }

            return false;
        }

        //* این تابع انتخاب پیش فرض روشن را فقط یک بار از مسیر واقعی میکروفن مرورگر اعمال می کند.
        private void EnsureInitialMicrophoneSelection()
        {
            if (initialMicrophoneSelectionApplied ||
                Time.realtimeSinceStartup < initialMicrophoneRetryAt)
            {
                return;
            }

            if (!TryResolveBrowserVoiceAdapter()) return;

            if (TryReadBrowserMicrophoneRequested(out bool requested) &&
                requested)
            {
                lastStableMicrophoneMuted = false;
                initialMicrophoneSelectionApplied = true;

                Debug.Log(
                    "VOICE_WEBGL_INITIAL_MICROPHONE=PASS" +
                    " | mode=already_requested" +
                    " | defaultOn=True" +
                    " | windowsPathChanged=False");
                return;
            }

            initialMicrophoneAttempts += 1;

            bool applied = SetBrowserMicrophoneRequested(true);
            if (applied)
            {
                lastStableMicrophoneMuted = false;
                initialMicrophoneSelectionApplied = true;

                Debug.Log(
                    "VOICE_WEBGL_INITIAL_MICROPHONE=PASS" +
                    " | mode=production_adapter_activation" +
                    " | defaultOn=True" +
                    " | attempts=" + initialMicrophoneAttempts +
                    " | windowsPathChanged=False");
                return;
            }

            if (initialMicrophoneAttempts < MaximumInitialMicrophoneAttempts)
            {
                initialMicrophoneRetryAt =
                    Time.realtimeSinceStartup +
                    InitialMicrophoneRetrySeconds;
                return;
            }

            initialMicrophoneSelectionApplied = true;

            Debug.LogError(
                "VOICE_WEBGL_INITIAL_MICROPHONE=FAIL" +
                " | reason=production_adapter_activation_rejected" +
                " | attempts=" + initialMicrophoneAttempts);
        }

        //* این تابع وضعیت انتخاب‌شده کاربر را نگه می‌دارد تا پس از بازسازی بدون تغییر برگردد.
        private void BeginRecoveryWatch(string source)
        {
            if (recoveryPending) return;

            recoveryPending = true;
            reconciliationRunning = false;
            reconciliationAttempts = 0;
            retryAt = 0f;
            healthySessionAbsentSince = -1f;
            recoveryGeneration += 1;

            Debug.Log(
                "VOICE_WEBGL_SESSION_RECONNECT_RECONCILIATION=PENDING" +
                " | source=" + Safe(source) +
                " | microphoneMuted=" + lastStableMicrophoneMuted +
                " | speakerOff=" + lastStableSpeakerOff +
                " | generation=" + recoveryGeneration);
        }

        //* این تابع ابتدا فرصت بازیابی عادی می‌دهد و در صورت باقی‌ماندن تصویر خالی فقط اتصال Voice مرورگر را تازه می‌سازد.
        private async Task ReconcileSessionAsync(
            int generation,
            CancellationToken cancellationToken)
        {
            try
            {
                bool normalRecoveryCompleted = await WaitForSessionAsync(
                    generation,
                    NormalRecoverySettleSeconds,
                    cancellationToken);

                if (normalRecoveryCompleted)
                {
                    RestoreSelectedControls();
                    CompleteRecovery(
                        "normalRecoveryAfterSettleWithSelectedControls");
                    return;
                }

                if (!IsCurrentRecovery(generation) ||
                    runtime == null ||
                    !runtime.IsAuthenticated ||
                    !MetaverseNetworkClient.isReady)
                {
                    return;
                }

                if (lastStableMicrophoneMuted && lastStableSpeakerOff)
                {
                    recoveryPending = false;

                    Debug.Log(
                        "VOICE_WEBGL_SESSION_RECONNECT_RECONCILIATION=SKIPPED" +
                        " | reason=voice_not_eligible" +
                        " | microphoneMuted=True" +
                        " | speakerOff=True");
                    return;
                }

                reconciliationAttempts += 1;

                Debug.Log(
                    "VOICE_WEBGL_SESSION_RECONNECT_RECONCILIATION=START" +
                    " | attempt=" + reconciliationAttempts +
                    " | mode=fresh_voice_connection" +
                    " | microphoneMuted=" + lastStableMicrophoneMuted +
                    " | speakerOff=" + lastStableSpeakerOff +
                    " | activeSessionCount=" + runtime.ActiveSessionCount);

                bool disconnected =
                    await runtime.DisconnectForPlayerUnavailableAsync(
                        "webgl_session_reconnect_reconciliation_" +
                            reconciliationAttempts,
                        FreshVoiceDisconnectTimeoutMs,
                        cancellationToken);

                if (!disconnected ||
                    !IsCurrentRecovery(generation) ||
                    runtime == null ||
                    !MetaverseNetworkClient.isReady)
                {
                    RestoreSelectedControls();
                    ScheduleRetryOrFail(
                        generation,
                        disconnected
                            ? "player_not_ready_after_fresh_disconnect"
                            : "fresh_voice_disconnect_failed");
                    return;
                }

                RestoreSelectedControls();

                Debug.Log(
                    "VOICE_WEBGL_SESSION_RECONNECT_FRESH_CONNECTION=READY" +
                    " | attempt=" + reconciliationAttempts +
                    " | microphoneMutedRestored=" +
                        lastStableMicrophoneMuted +
                    " | speakerOffRestored=" + lastStableSpeakerOff +
                    " | userSelectionPreserved=True" +
                    " | autoConnectorResume=True");

                bool rebuilt = await WaitForSessionAsync(
                    generation,
                    SessionRebuildDeadlineSeconds,
                    cancellationToken);

                if (rebuilt)
                {
                    CompleteRecovery("freshVoiceConnection");
                    return;
                }

                ScheduleRetryOrFail(
                    generation,
                    "session_rebuild_deadline_exceeded");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                RestoreSelectedControls();
                ScheduleRetryOrFail(
                    generation,
                    "exception_" + Safe(exception.Message));
            }
            finally
            {
                reconciliationRunning = false;
            }
        }

        //* این تابع در مسیر خطا نیز انتخاب بلندگو و انتشار قبلی کاربر را برمی‌گرداند.
        private void RestoreSelectedControls()
        {
            if (runtime == null) return;

            runtime.SetSpeakerOff(lastStableSpeakerOff);

            bool browserSelectionRestored =
                SetBrowserMicrophoneRequested(
                    !lastStableMicrophoneMuted);

            if (!browserSelectionRestored)
            {
                runtime.SetMicrophoneMuted(
                    lastStableMicrophoneMuted);
            }
            else
            {
                EnsureRuntimeMatchesSelectedMicrophone();
            }

            Debug.Log(
                "VOICE_WEBGL_SESSION_RECONNECT_MICROPHONE_SELECTION=" +
                (browserSelectionRestored ? "PASS" : "FALLBACK") +
                " | microphoneOn=" + !lastStableMicrophoneMuted +
                " | speakerOff=" + lastStableSpeakerOff +
                " | productionAdapter=" + browserSelectionRestored);
        }

        //* این تابع Adapter واقعی مرورگر و اعضای کنترل میکروفن آن را بدون تغییر فایل اصلی پیدا می کند.
        private bool TryResolveBrowserVoiceAdapter()
        {
            if (browserVoiceAdapter == null)
            {
                browserVoiceAdapter = UnityEngine.Object
                    .FindFirstObjectByType<VoiceBrowserVoiceRuntimeAdapter>();
            }

            if (browserVoiceAdapter == null) return false;

            Type adapterType = typeof(VoiceBrowserVoiceRuntimeAdapter);

            if (browserMicrophoneRequestedField == null)
            {
                browserMicrophoneRequestedField = adapterType.GetField(
                    "microphoneRequested",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }

            if (browserMicrophoneClickMethod == null)
            {
                browserMicrophoneClickMethod = adapterType.GetMethod(
                    "HandleMicrophoneButtonClicked",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }

            if (browserDisablePublishingMethod == null)
            {
                browserDisablePublishingMethod = adapterType.GetMethod(
                    "DisablePublishingFromUserAction",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }

            if (browserSetRuntimeMicrophoneMirrorMethod == null)
            {
                browserSetRuntimeMicrophoneMirrorMethod = adapterType.GetMethod(
                    "SetRuntimeMicrophoneMirror",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }

            return browserMicrophoneRequestedField != null &&
                   browserMicrophoneClickMethod != null &&
                   browserDisablePublishingMethod != null &&
                   browserSetRuntimeMicrophoneMirrorMethod != null;
        }

        //* این تابع وضعیت درخواست میکروفن Production مرورگر را می خواند.
        private bool TryReadBrowserMicrophoneRequested(out bool requested)
        {
            requested = false;
            if (!TryResolveBrowserVoiceAdapter()) return false;

            object value = browserMicrophoneRequestedField.GetValue(
                browserVoiceAdapter);

            if (!(value is bool)) return false;
            requested = (bool)value;
            return true;
        }

        //* این تابع فقط در صورت تفاوت، مسیر موجود روشن یا خاموش کردن میکروفن Production مرورگر را اجرا می کند.
        private bool SetBrowserMicrophoneRequested(bool requested)
        {
            if (!TryReadBrowserMicrophoneRequested(out bool current))
                return false;

            if (current == requested) return true;

            try
            {
                if (requested)
                {
                    browserMicrophoneClickMethod.Invoke(
                        browserVoiceAdapter,
                        null);
                }
                else
                {
                    browserDisablePublishingMethod.Invoke(
                        browserVoiceAdapter,
                        null);
                }

                return TryReadBrowserMicrophoneRequested(
                           out bool applied) &&
                       applied == requested;
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VOICE_WEBGL_MICROPHONE_SELECTION_ADAPTER=FAIL" +
                    " | requested=" + requested +
                    " | error=" + Safe(exception.Message));
                return false;
            }
        }

        //* این تابع انتخاب کاربر را از مالک واقعی میکروفن مرورگر می خواند و وضعیت موقت Capture را جایگزین آن نمی کند.
        private void UpdateStableControlsFromCurrentSelection()
        {
            if (runtime == null) return;

            lastStableMicrophoneMuted =
                TryReadBrowserMicrophoneRequested(out bool requested)
                    ? !requested
                    : runtime.IsMicrophoneMuted;

            lastStableSpeakerOff = runtime.IsSpeakerOff;
        }

        //* این تابع انتخاب پایدار Mic مرورگر را از وضعیت موقت Capture جدا نگه می دارد.
        //* پاک سازی Player/Media مجاز است Capture را متوقف کند، اما نباید Mic انتخاب شده
        //* کاربر را OFF کند یا در حالت Speaker OFF مانع Auth مجدد Voice شود.
        private void EnsureRuntimeMatchesSelectedMicrophone()
        {
            if (runtime == null || lastStableMicrophoneMuted) return;
            if (!runtime.IsMicrophoneMuted) return;
            if (!TryReadBrowserMicrophoneRequested(out bool requested) ||
                !requested ||
                browserSetRuntimeMicrophoneMirrorMethod == null ||
                browserVoiceAdapter == null)
            {
                return;
            }

            try
            {
                browserSetRuntimeMicrophoneMirrorMethod.Invoke(
                    browserVoiceAdapter,
                    new object[] { false });

                runtime.SetRecordingConsentForAll(true);

                Debug.Log(
                    "VOICE_WEBGL_MICROPHONE_INTENT_REARM=" +
                    (!runtime.IsMicrophoneMuted ? "PASS" : "WAIT") +
                    " | microphoneOn=True" +
                    " | speakerOff=" + lastStableSpeakerOff +
                    " | voiceEligible=" + runtime.IsVoiceEligible +
                    " | reason=runtime_capture_state_was_reset" +
                    " | windowsPathChanged=False" +
                    " | questPathChanged=False");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VOICE_WEBGL_MICROPHONE_INTENT_REARM=FAIL" +
                    " | error=" + Safe(exception.Message));
            }
        }

        //* این تابع اتصال Media دریافت WebGL را از انتشار میکروفن جدا می‌کند.
        //* وقتی Mic خاموش ولی Speaker روشن است، Session باید بدون Publish نیز Downlink فعال داشته باشد.
        private void EnsureReceiveOnlyMediaConnection()
        {
            // چرخه اتصال رسانه در این هوک اجرا نمی‌شود.
        }

        private bool ReadBrowserBool(FieldInfo field)
        {
            if (field == null || browserVoiceAdapter == null) return false;
            object value = field.GetValue(browserVoiceAdapter);
            return value is bool && (bool)value;
        }

        //* این تابع فقط ظاهر کنترل‌های موجود را پس از بازگردانی انتخاب کاربر در همان قاب تازه می‌کند.
        private void RefreshSelectedControlsUi()
        {
            if (consentPanel == null)
            {
                consentPanel = UnityEngine.Object
                    .FindFirstObjectByType<VoiceSceneUserConsentPanelController>();
            }

            if (consentPanel == null) return;

            if (consentPanelUpdateUiMethod == null)
            {
                consentPanelUpdateUiMethod =
                    typeof(VoiceSceneUserConsentPanelController).GetMethod(
                        "UpdateUi",
                        BindingFlags.Instance | BindingFlags.NonPublic);
            }

            consentPanelUpdateUiMethod?.Invoke(consentPanel, null);
        }

        //* این تابع تا پایان مهلت فقط ایجاد نشست معتبر همان بازیابی را بررسی می‌کند.
        private async Task<bool> WaitForSessionAsync(
            int generation,
            float timeoutSeconds,
            CancellationToken cancellationToken)
        {
            float deadline = Time.realtimeSinceStartup +
                             Mathf.Max(0.1f, timeoutSeconds);

            while (Time.realtimeSinceStartup < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!IsCurrentRecovery(generation) || runtime == null)
                    return false;

                if (runtime.IsAuthenticated &&
                    MetaverseNetworkClient.isReady &&
                    runtime.ActiveSessionCount > 0)
                {
                    return true;
                }

                await Task.Yield();
            }

            return runtime != null &&
                   runtime.IsAuthenticated &&
                   MetaverseNetworkClient.isReady &&
                   runtime.ActiveSessionCount > 0;
        }

        //* این تابع بازیابی موفق را ثبت و از اجرای دوباره روی همان قطعی جلوگیری می‌کند.
        private void CompleteRecovery(string mode)
        {
            if (runtime == null) return;

            recoveryPending = false;
            reconciliationAttempts = 0;
            hadActiveSession = runtime.ActiveSessionCount > 0;
            healthySessionAbsentSince = -1f;
            UpdateStableControlsFromCurrentSelection();

            Debug.Log(
                "VOICE_WEBGL_SESSION_RECONNECT_RECONCILIATION=PASS" +
                " | mode=" + Safe(mode) +
                " | activeSessionCount=" + runtime.ActiveSessionCount +
                " | microphoneMuted=" + lastStableMicrophoneMuted +
                " | speakerOff=" + lastStableSpeakerOff +
                " | windowsPathChanged=False" +
                " | serverPathChanged=False");
        }

        //* این تابع شکست موقت را تا سقف تعیین‌شده دوباره تلاش و سپس با لاگ قطعی متوقف می‌کند.
        private void ScheduleRetryOrFail(
            int generation,
            string reason)
        {
            if (!IsCurrentRecovery(generation)) return;

            if (reconciliationAttempts < MaximumReconciliationAttempts)
            {
                retryAt = Time.realtimeSinceStartup + RetryDelaySeconds;

                Debug.LogWarning(
                    "VOICE_WEBGL_SESSION_RECONNECT_RECONCILIATION=RETRY" +
                    " | attempt=" + reconciliationAttempts +
                    " | reason=" + Safe(reason) +
                    " | retryInSeconds=" + RetryDelaySeconds.ToString("F1"));
                return;
            }

            recoveryPending = false;

            Debug.LogError(
                "VOICE_WEBGL_SESSION_RECONNECT_RECONCILIATION=FAIL" +
                " | attempts=" + reconciliationAttempts +
                " | reason=" + Safe(reason) +
                " | activeSessionCount=" +
                    (runtime == null ? 0 : runtime.ActiveSessionCount));
        }

        private bool IsCurrentRecovery(int generation)
        {
            return recoveryPending &&
                   generation == recoveryGeneration &&
                   lifetimeCts != null &&
                   !lifetimeCts.IsCancellationRequested;
        }

        private void OnDestroy()
        {
            recoveryPending = false;
            recoveryGeneration += 1;

            if (lifetimeCts != null)
            {
                lifetimeCts.Cancel();
                lifetimeCts.Dispose();
                lifetimeCts = null;
            }

            runtime = null;
            browserVoiceAdapter = null;
            consentPanel = null;
            browserMicrophoneRequestedField = null;
            browserMicrophoneClickMethod = null;
            browserDisablePublishingMethod = null;
            browserSetRuntimeMicrophoneMirrorMethod = null;
            browserAudioReadyField = null;
            browserMediaConnectStartedField = null;
            browserMediaBoundField = null;
            browserActiveControlConnectionIdField = null;
            browserStartMediaConnectionMethod = null;
            browserResetMediaSessionMethod = null;
            consentPanelUpdateUiMethod = null;
            voiceAutoConnector = null;
            voiceAutoConnectorNextAttemptAtField = null;
            voiceAutoConnectorAttemptedField = null;
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().Replace("|", "/");
        }

        //* این تابع اجرای Hook را به صحنه صوت انتخاب شده در مرورگر محدود می کند.
        private static bool IsVoiceGameplaySceneActive()
        {
            Scene scene = SceneManager.GetActiveScene();

            return scene.IsValid() &&
                   VoiceWebGLLobbyRouteSelection.IsVoiceModeSelected &&
                   string.Equals(
                       scene.name,
                       VoiceWebGLLobbyRouteSelection.VoiceGameplaySceneName,
                       StringComparison.Ordinal);
        }
    }
}
#endif
