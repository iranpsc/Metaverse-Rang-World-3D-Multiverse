#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Voice.Client.Routing.WebGL;
using Network_A.Voice.Client.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network_A.Voice.Client.BrowserAudio
{
    public static class VoiceWebGLUserEligibilitySessionLifecycleInstaller
    {
        private const string HookRootName = "Voice_WebGL_User_Eligibility_Session_Lifecycle";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
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

            if (root.GetComponent<VoiceWebGLUserEligibilitySessionLifecycleHook>() == null)
                root.AddComponent<VoiceWebGLUserEligibilitySessionLifecycleHook>();
        }
    }

    [DisallowMultipleComponent]
    public sealed class VoiceWebGLUserEligibilitySessionLifecycleHook : MonoBehaviour
    {
        private const string RuntimeRootName = "Voice_Client_Runtime_Root";
        private const float ControlSelectionSettleSeconds = 0.2f;
        private const int VoiceDisconnectTimeoutMs = 1500;
        private const string DisconnectReason = "user_mic_and_speaker_off";

        private VoiceClientRuntime runtime;
        private VoiceBrowserVoiceRuntimeAdapter browserAdapter;
        private FieldInfo microphoneRequestedField;
        private CancellationTokenSource lifetimeCts;
        private float bothOffSince = -1f;
        private bool disconnectInFlight;
        private bool reportedMissingSelection;

        private void Awake()
        {
            lifetimeCts = new CancellationTokenSource();
            Debug.Log("VOICE_W4_WEBGL_USER_ELIGIBILITY_HOOK=READY");
        }

        private void Update()
        {
            if (!IsVoiceSceneActive())
            {
                bothOffSince = -1f;
                runtime = null;
                browserAdapter = null;
                return;
            }

            ResolveRuntimeAndAdapter();
            if (runtime == null || browserAdapter == null ||
                microphoneRequestedField == null || lifetimeCts == null ||
                lifetimeCts.IsCancellationRequested)
            {
                bothOffSince = -1f;
                return;
            }

            bool microphoneRequested;
            try
            {
                object selected = microphoneRequestedField.GetValue(browserAdapter);
                if (!(selected is bool))
                {
                    bothOffSince = -1f;
                    return;
                }

                microphoneRequested = (bool)selected;
            }
            catch (Exception exception)
            {
                bothOffSince = -1f;
                Debug.LogWarning(
                    "VOICE_W4_USER_ELIGIBILITY_SELECTION=FAIL" +
                    " | error=" + exception.Message);
                return;
            }

            if (microphoneRequested || !runtime.IsSpeakerOff)
            {
                bothOffSince = -1f;
                return;
            }

            if (bothOffSince < 0f)
            {
                bothOffSince = Time.realtimeSinceStartup;
                return;
            }

            if (Time.realtimeSinceStartup - bothOffSince < ControlSelectionSettleSeconds ||
                disconnectInFlight || runtime.IsDisconnecting)
            {
                return;
            }

            if (!runtime.IsAuthenticated &&
                runtime.ActiveSessionCount == 0 &&
                string.IsNullOrWhiteSpace(runtime.VoiceConnectionId))
            {
                return;
            }

            disconnectInFlight = true;
            _ = DisconnectVoiceForUserSelectionAsync(runtime, lifetimeCts.Token);
        }

        private static bool IsVoiceSceneActive()
        {
            Scene scene = SceneManager.GetActiveScene();
            return scene.IsValid() &&
                   VoiceWebGLLobbyRouteSelection.IsVoiceModeSelected &&
                   string.Equals(
                       scene.name,
                       VoiceWebGLLobbyRouteSelection.VoiceGameplaySceneName,
                       StringComparison.Ordinal);
        }

        private void ResolveRuntimeAndAdapter()
        {
            if (runtime == null)
            {
                GameObject root = GameObject.Find(RuntimeRootName);
                if (root != null) runtime = root.GetComponent<VoiceClientRuntime>();
            }

            if (browserAdapter == null)
                browserAdapter = FindFirstObjectByType<VoiceBrowserVoiceRuntimeAdapter>();

            if (browserAdapter == null || microphoneRequestedField != null)
                return;

            microphoneRequestedField = typeof(VoiceBrowserVoiceRuntimeAdapter).GetField(
                "microphoneRequested",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (microphoneRequestedField == null && !reportedMissingSelection)
            {
                reportedMissingSelection = true;
                Debug.LogError(
                    "VOICE_W4_USER_ELIGIBILITY_SELECTION=FAIL" +
                    " | reason=microphone_requested_field_missing");
            }
        }

        private async Task DisconnectVoiceForUserSelectionAsync(
            VoiceClientRuntime targetRuntime,
            CancellationToken cancellationToken)
        {
            bool disconnected = false;
            try
            {
                Debug.Log(
                    "VOICE_W4_USER_ELIGIBILITY_DISCONNECT_REQUEST=START" +
                    " | reason=" + DisconnectReason +
                    " | sessionCount=" + targetRuntime.ActiveSessionCount);

                disconnected = await targetRuntime.DisconnectForPlayerUnavailableAsync(
                    DisconnectReason,
                    VoiceDisconnectTimeoutMs,
                    cancellationToken);

                if (this != null && !cancellationToken.IsCancellationRequested)
                {
                    Debug.Log(
                        "VOICE_W4_USER_ELIGIBILITY_DISCONNECT_REQUEST=" +
                        (disconnected ? "PASS" : "FAIL") +
                        " | controlDisconnectSent=" + disconnected +
                        " | sessionCount=" +
                        (targetRuntime != null ? targetRuntime.ActiveSessionCount : -1));
                }
            }
            catch (OperationCanceledException)
            {
                if (this != null)
                    Debug.Log("VOICE_W4_USER_ELIGIBILITY_DISCONNECT_REQUEST=CANCELLED");
            }
            catch (Exception exception)
            {
                if (this != null)
                {
                    Debug.LogError(
                        "VOICE_W4_USER_ELIGIBILITY_DISCONNECT_REQUEST=FAIL" +
                        " | error=" + exception.Message);
                }
            }
            finally
            {
                if (this != null) disconnectInFlight = false;
            }
        }

        private void OnDestroy()
        {
            if (lifetimeCts != null)
            {
                lifetimeCts.Cancel();
                lifetimeCts.Dispose();
                lifetimeCts = null;
            }
        }
    }
}
#endif
