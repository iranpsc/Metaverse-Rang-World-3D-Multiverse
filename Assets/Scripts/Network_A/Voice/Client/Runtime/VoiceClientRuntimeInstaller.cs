using System;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Voice.Client.Routing;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network_A.Voice.Client.Runtime
{
    public static class VoiceClientRuntimeInstaller
    {
        private const string RootName = "Voice_Client_Runtime_Root";
        private static int lastProcessedSceneHandle = int.MinValue;
        private static bool applicationQuitting;

        //* این تابع پیش از بارگذاری نخستین صحنه، دریافت رویداد بارگذاری صحنه‌ها را به‌صورت یکتا آماده می‌کند.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeSceneHandling()
        {
            lastProcessedSceneHandle = int.MinValue;
            applicationQuitting = false;
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
            Application.quitting -= HandleApplicationQuitting;
            Application.quitting += HandleApplicationQuitting;

            Debug.Log(
                "VOICE_CLIENT_INSTALLER_INITIALIZED" +
                " | batchMode=" + Application.isBatchMode);
        }

        //* این تابع پس از نخستین صحنه، نصب کلاینت صوت را با همان مسیر مشترک بررسی می‌کند.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallAfterSceneLoad()
        {
            Scene scene = SceneManager.GetActiveScene();

            Debug.Log(
                "VOICE_CLIENT_SCENE_EVENT" +
                " | source=after_scene_load" +
                " | scene=" + scene.name);

            ApplyRuntimeForScene(scene);
        }

        //* این تابع پس از هر تغییر صحنه، نصب یا پاک‌سازی کلاینت صوت را برای همان صحنه اجرا می‌کند.
        private static void HandleSceneLoaded(Scene scene, LoadSceneMode loadMode)
        {
            Debug.Log(
                "VOICE_CLIENT_SCENE_EVENT" +
                " | source=scene_loaded" +
                " | scene=" + scene.name +
                " | loadMode=" + loadMode);

            ApplyRuntimeForScene(scene);
        }

        //* این تابع هنگام خروج آنی برنامه، Runtime صوت را پیش از بسته‌شدن فرایند وارد مسیر پاک‌سازی می‌کند.
        private static void HandleApplicationQuitting()
        {
            applicationQuitting = true;
            GameObject root = GameObject.Find(RootName);
            VoiceClientRuntime runtime = root != null ? root.GetComponent<VoiceClientRuntime>() : null;

            if (runtime != null)
            {
                Debug.Log("VOICE_CLIENT_ROUTE_APPLICATION_QUIT_CLEANUP=START");
                _ = runtime.DisconnectGracefullyAsync(
                    "application_quit_route",
                    1200,
                    CancellationToken.None);
            }
        }

        //* این تابع کلاینت صوت را فقط برای مسیر صوت یا فعال‌سازی صریح آزمایشی می‌سازد و هنگام بازگشت مسیر صوت به لابی آن را آزاد می‌کند.
        private static void ApplyRuntimeForScene(Scene scene)
        {
            if (Application.isBatchMode)
            {
                Debug.Log("VOICE_CLIENT_RUNTIME_SKIPPED | reason=batch_mode");
                return;
            }

            if (!scene.IsValid())
            {
                Debug.LogWarning("VOICE_CLIENT_RUNTIME_SKIPPED | reason=invalid_scene");
                return;
            }

            if (applicationQuitting)
            {
                Debug.Log("VOICE_CLIENT_RUNTIME_SKIPPED | reason=application_quitting");
                return;
            }

            if (scene.handle == lastProcessedSceneHandle)
            {
                Debug.Log(
                    "VOICE_CLIENT_RUNTIME_SKIPPED" +
                    " | reason=scene_already_processed" +
                    " | scene=" + scene.name);

                return;
            }

            lastProcessedSceneHandle = scene.handle;

            bool explicitlyEnabled = IsEnabled();

            bool selectedVoiceScene =
                VoiceLobbyRouteSelection.IsVoiceModeSelected &&
                string.Equals(
                    scene.name,
                    VoiceLobbyRouteSelection.VoiceGameplaySceneName,
                    StringComparison.Ordinal);

            GameObject root = GameObject.Find(RootName);

            Debug.Log(
                "VOICE_CLIENT_RUNTIME_CHECK" +
                " | scene=" + scene.name +
                " | explicitEnabled=" + explicitlyEnabled +
                " | selectedVoiceScene=" + selectedVoiceScene +
                " | rootExists=" + (root != null));

            if (!explicitlyEnabled && !selectedVoiceScene)
            {
                if (root != null)
                {
                    _ = StopRuntimeForSceneExitAsync(root, scene.name);
                    return;
                }

                Debug.Log(
                    "VOICE_CLIENT_RUNTIME_SKIPPED" +
                    " | reason=voice_route_not_selected" +
                    " | scene=" + scene.name);

                return;
            }

            bool rootCreated = root == null;

            if (rootCreated)
            {
                root = new GameObject(RootName);
            }

            UnityEngine.Object.DontDestroyOnLoad(root);

            VoiceClientRuntime runtime = root.GetComponent<VoiceClientRuntime>();
            bool runtimeCreated = runtime == null;

            if (runtimeCreated)
            {
                runtime = root.AddComponent<VoiceClientRuntime>();
            }

            runtime.Initialize();

            VoiceClientAutoConnector connector = root.GetComponent<VoiceClientAutoConnector>();
            bool connectorCreated = connector == null;

            if (connectorCreated)
            {
                connector = root.AddComponent<VoiceClientAutoConnector>();
            }

            connector.Initialize(runtime);

            Debug.Log(
                "VOICE_CLIENT_RUNTIME_CREATED" +
                " | scene=" + scene.name +
                " | rootCreated=" + rootCreated +
                " | runtimeCreated=" + runtimeCreated +
                " | connectorCreated=" + connectorCreated);

            if (ReadFlag("METAVERSE_VOICE_LIVE_TEST_ENABLED", "voiceTest=1"))
            {
                VoiceClientLiveTestController liveTest = root.GetComponent<VoiceClientLiveTestController>();

                if (liveTest == null)
                {
                    liveTest = root.AddComponent<VoiceClientLiveTestController>();
                }

                liveTest.Initialize(
                    runtime,
                    ReadFlag("METAVERSE_VOICE_LIVE_TEST_AUTO_MIC", "voiceAutoMic=1"),
                    ReadFlag("METAVERSE_VOICE_LIVE_TEST_RECORDING_CONSENT", "voiceRecord=1"));
            }

            Debug.Log(
                "VOICE_CLIENT_ROUTE_RUNTIME=READY" +
                " | scene=" + scene.name +
                " | source=" + (selectedVoiceScene ? "lobby_voice_route" : "explicit_test_flag"));
        }

        //* این تابع هنگام خروج از صحنه صوت، ابتدا خروج پروتکلی را کامل و سپس ریشه Runtime را نابود می‌کند.
        private static async Task StopRuntimeForSceneExitAsync(GameObject root, string sceneName)
        {
            bool disconnected = true;
            VoiceClientRuntime runtime = root != null ? root.GetComponent<VoiceClientRuntime>() : null;

            try
            {
                if (runtime != null)
                {
                    Debug.Log(
                        "VOICE_CLIENT_ROUTE_DISCONNECT_START=PASS" +
                        " | scene=" + sceneName);

                    disconnected = await runtime.DisconnectGracefullyAsync(
                        "voice_route_left_scene_" + sceneName,
                        3500,
                        CancellationToken.None);
                }
            }
            catch (Exception exception)
            {
                disconnected = false;

                Debug.LogWarning(
                    "VOICE_CLIENT_ROUTE_DISCONNECT=FAIL" +
                    " | scene=" + sceneName +
                    " | error=" + exception.Message);
            }

            Debug.Log(
                "VOICE_CLIENT_ROUTE_DISCONNECT=" + (disconnected ? "PASS" : "FAIL") +
                " | scene=" + sceneName);

            if (root != null)
            {
                UnityEngine.Object.Destroy(root);
            }

            Debug.Log("VOICE_CLIENT_ROUTE_RUNTIME=STOPPED | scene=" + sceneName);
        }

        //* این تابع فعال‌سازی صریح آزمایشی کلاینت صوت را از محیط اجرای همان پردازش می‌خواند.
        private static bool IsEnabled()
        {
            string value = Environment.GetEnvironmentVariable("METAVERSE_VOICE_CLIENT_ENABLED");

#if UNITY_WEBGL && !UNITY_EDITOR
            return Application.absoluteURL.IndexOf("voice=1", StringComparison.OrdinalIgnoreCase) >= 0;
#else
            return
                string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
#endif
        }

        //* این تابع پرچم‌های اختیاری آزمون زنده را از محیط اجرا یا نشانی وب می‌خواند.
        private static bool ReadFlag(string environmentName, string webGlQuery)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return Application.absoluteURL.IndexOf(webGlQuery, StringComparison.OrdinalIgnoreCase) >= 0;
#else
            string value = Environment.GetEnvironmentVariable(environmentName);

            return
                string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
#endif
        }
    }

    internal sealed class VoiceClientAutoConnector : MonoBehaviour
    {
        private VoiceClientRuntime runtime;
        private bool attempted;
        private bool initialized;
        private float nextAttemptAt;
        private string lastWaitReason;
        private bool playerWasReady;
        private CancellationTokenSource activeConnectCts;

        //* این تابع نمونه کلاینت صوت را برای شروع خودکار اتصال دریافت می‌کند.
        public void Initialize(VoiceClientRuntime value)
        {
            if (initialized && ReferenceEquals(runtime, value))
            {
                Debug.Log(
                    "VOICE_CLIENT_CONNECTOR_REUSED=PASS" +
                    " | runtimeAssigned=" + (runtime != null) +
                    " | attempted=" + attempted);

                return;
            }

            runtime = value;
            attempted = false;
            initialized = true;
            nextAttemptAt = 0f;
            lastWaitReason = null;
            playerWasReady = MetaverseNetworkClient.isReady;

            Debug.Log(
                "VOICE_CLIENT_CONNECTOR_INITIALIZED" +
                " | runtimeAssigned=" + (runtime != null));
        }

        //* این تابع فقط از وضعیت آماده بازیکن برای شروع یا پاک‌سازی اتصال صوت استفاده می‌کند.
        private async void Update()
        {
            if (runtime == null)
            {
                LogWaitReason("runtime_missing");
                return;
            }

            bool playerReady = MetaverseNetworkClient.isReady;

            if (!playerReady)
            {
                nextAttemptAt = 0f;

                if (activeConnectCts != null)
                {
                    activeConnectCts.Cancel();
                    playerWasReady = false;
                    LogWaitReason("dedicated_player_not_ready_connect_cancelled");
                    return;
                }

                bool cleanupRequired =
                    playerWasReady ||
                    runtime.IsAuthenticated ||
                    !string.IsNullOrWhiteSpace(runtime.VoiceConnectionId);

                playerWasReady = false;

                if (cleanupRequired && !runtime.IsDisconnecting)
                {
                    attempted = true;

                    try
                    {
                        await runtime.DisconnectForPlayerUnavailableAsync(
                            "dedicated_player_not_ready",
                            1500,
                            CancellationToken.None);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogWarning(
                            "VOICE_CLIENT_PLAYER_STATE_CLEANUP=FAIL" +
                            " | error=" + exception.Message);
                    }
                    finally
                    {
                        attempted = false;
                    }
                }

                LogWaitReason("dedicated_player_not_ready");
                return;
            }

            playerWasReady = true;

            if (runtime.IsAuthenticated)
            {
                attempted = false;
                LogWaitReason("player_ready_voice_authenticated");
                return;
            }

            if (attempted) return;

            if (runtime.IsDisconnecting)
            {
                LogWaitReason("voice_cleanup_in_progress");
                return;
            }

            if (Time.realtimeSinceStartup < nextAttemptAt)
            {
                LogWaitReason("player_ready_voice_retry_delay");
                return;
            }

            lastWaitReason = null;
            attempted = true;
            activeConnectCts?.Dispose();
            CancellationTokenSource connectCts = new CancellationTokenSource();
            activeConnectCts = connectCts;

            Debug.Log(
                "VOICE_CLIENT_CONNECT_ATTEMPT" +
                " | source=player_ready" +
                " | scene=" + SceneManager.GetActiveScene().name);

            bool connected = false;

            try
            {
                connected = await runtime.ConnectAsync(connectCts.Token);
            }
            catch (OperationCanceledException)
            {
                connected = false;
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VOICE_CLIENT_ROUTE_CONNECT=FAIL" +
                    " | reason=exception" +
                    " | exception=" + exception);

            }
            finally
            {
                if (ReferenceEquals(activeConnectCts, connectCts))
                {
                    activeConnectCts = null;
                }

                connectCts.Dispose();
                attempted = false;
            }

            Debug.Log(
                "VOICE_CLIENT_ROUTE_CONNECT=" + (connected ? "PASS" : "FAIL") +
                " | playerReady=" + MetaverseNetworkClient.isReady +
                " | scene=" + SceneManager.GetActiveScene().name +
                (connected ? string.Empty : " | retryInSeconds=3"));

            if (connected) return;

            nextAttemptAt = MetaverseNetworkClient.isReady
                ? Time.realtimeSinceStartup + 3f
                : 0f;
        }

        //* این تابع هنگام نابودی Connector از تلاش‌های بعدی اتصال جلوگیری می‌کند.
        private void OnDisable()
        {
            activeConnectCts?.Cancel();
            activeConnectCts?.Dispose();
            activeConnectCts = null;
            attempted = true;
            LogWaitReason("connector_disabled");
        }

        //* این تابع علت انتظار اتصال را فقط هنگام تغییر وضعیت در لاگ عمومی یونیتی ثبت می‌کند.
        private void LogWaitReason(string reason)
        {
            if (string.Equals(lastWaitReason, reason, StringComparison.Ordinal)) return;

            lastWaitReason = reason;
            Debug.Log("VOICE_CLIENT_CONNECT_WAIT | reason=" + reason);
        }
    }

    internal sealed class VoiceClientLiveTestController : MonoBehaviour
    {
        private VoiceClientRuntime runtime;
        private bool autoMic;
        private bool recordingConsent;
        private bool micStarted;
        private int consentedSessionCount;
        private float nextLogAt;

        //* این تابع کنترلر آزمون زنده را با وضعیت میکروفن و رضایت ضبط تعیین‌شده آماده می‌کند.
        public void Initialize(VoiceClientRuntime value, bool enableAutoMic, bool enableRecordingConsent)
        {
            runtime = value;
            autoMic = enableAutoMic;
            recordingConsent = enableRecordingConsent;

            Debug.Log(
                "VOICE_V9_LIVE_TEST_CONTROLLER=PASS" +
                " | autoMic=" + autoMic +
                " | recordingConsent=" + recordingConsent);
        }

        //* این تابع وضعیت اتصال، نشست‌ها و میکروفن را در آزمون زنده ثبت می‌کند و فقط در صورت اجازه صریح، قابلیت‌های تست را فعال می‌کند.
        private void Update()
        {
            if (runtime == null) return;

            if (runtime.IsAuthenticated && autoMic && !micStarted)
            {
                runtime.SetMicrophoneMuted(false);
                micStarted = true;

                Debug.Log("VOICE_V9_LIVE_TEST_MIC_STARTED=PASS");
            }

            if (runtime.ActiveSessionCount < consentedSessionCount)
            {
                consentedSessionCount = runtime.ActiveSessionCount;

                Debug.Log(
                    "VOICE_V9_LIVE_TEST_RECORDING_CONSENT_RESET=PASS" +
                    " | sessionCount=" + consentedSessionCount);
            }

            if (
                runtime.IsAuthenticated &&
                recordingConsent &&
                runtime.ActiveSessionCount > consentedSessionCount
            )
            {
                runtime.SetRecordingConsentForAll(true);
                consentedSessionCount = runtime.ActiveSessionCount;

                Debug.Log(
                    "VOICE_V9_LIVE_TEST_RECORDING_CONSENT_SENT=PASS" +
                    " | sessionCount=" + consentedSessionCount);
            }

            if (Time.realtimeSinceStartup < nextLogAt) return;

            nextLogAt = Time.realtimeSinceStartup + 5f;

            Debug.Log(
                "VOICE_V9_LIVE_CLIENT_STATUS" +
                " | authenticated=" + runtime.IsAuthenticated +
                " | sessions=" + runtime.ActiveSessionCount +
                " | micMuted=" + runtime.IsMicrophoneMuted);
        }
    }
}

/*
توضیح فایل:
این فایل فقط نصب Runtime صوت را پس از انتخاب مسیر صوت یا فعال‌سازی صریح آزمایشی انجام می‌دهد. این نسخه هیچ رابط کاربری، پنل، دکمه یا Canvas نمی‌سازد. کنترل‌های کاربری باید به‌صورت دستی داخل صحنه ساخته شوند و با VoiceSceneUserConsentPanelController به Runtime صوت وصل شوند. مسیر آزمون زنده قبلی فقط با پرچم‌های آزمایشی فعال می‌ماند.
*/
