using System;
using System.Threading;
using Network_A.Realtime.Controllers;
using Network_A.Voice.Client.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if UNITY_WEBGL && !UNITY_EDITOR
using Network_A.Realtime.Transport;
#endif

namespace Network_A.Voice.Client.BrowserAudio
{
    // Attach to the existing scene-owned Btn_ReloadSound UI object.
    // The button is NEVER responsible for leaving the Realtime room or Game Server.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Button))]
    public sealed class VoiceSceneReloadSoundButton : MonoBehaviour
    {
        private const string VoiceSceneName = "WebGL_Enviroment_Voice";
        private const string RuntimeRootName = "Voice_Client_Runtime_Root";
        private const float ClickCooldownSeconds = 10f;

        private Button reloadButton;
        private float nextClickAllowedAt;
#if !UNITY_WEBGL || UNITY_EDITOR
        private bool resetInProgress;
#endif

        private void Awake()
        {
            reloadButton = GetComponent<Button>();
            if (reloadButton != null && reloadButton.targetGraphic == null)
                reloadButton.targetGraphic = GetComponent<Graphic>();
        }

        private void OnEnable()
        {
            if (reloadButton == null) reloadButton = GetComponent<Button>();
            if (reloadButton != null)
            {
                reloadButton.onClick.RemoveListener(HandleReloadClick);
                reloadButton.onClick.AddListener(HandleReloadClick);
            }
        }

        private void OnDisable()
        {
            if (reloadButton != null)
                reloadButton.onClick.RemoveListener(HandleReloadClick);
        }

        // Automatically bound to Button.onClick; no Inspector OnClick entry is needed.
        public void HandleReloadClick()
        {
            if (Time.realtimeSinceStartup < nextClickAllowedAt)
                return;

#if !UNITY_WEBGL || UNITY_EDITOR
            if (resetInProgress)
                return;
#endif

            if (!string.Equals(SceneManager.GetActiveScene().name,
                    VoiceSceneName, StringComparison.Ordinal))
            {
                Debug.LogWarning("VOICE_SCENE_RELOAD=SKIPPED | reason=not_voice_scene");
                return;
            }

            RealtimeRoomGameServerManager room =
                RealtimeRoomGameServerManager.Instance;
            if (room == null || !room.IsRealtimeReady || !room.IsJoinedRoom ||
                room.IsRoomExitInProgress || string.IsNullOrWhiteSpace(room.CurrentRoomId) ||
                !MetaverseNetworkClient.isReady)
            {
                Debug.LogWarning("VOICE_SCENE_RELOAD=SKIPPED | reason=room_or_player_not_ready");
                return;
            }

            GameObject root = GameObject.Find(RuntimeRootName);
            VoiceClientRuntime runtime = root != null
                ? root.GetComponent<VoiceClientRuntime>() : null;
            if (runtime == null || runtime.IsDisconnecting || !runtime.IsVoiceEligible)
            {
                Debug.LogWarning("VOICE_SCENE_RELOAD=SKIPPED | reason=voice_runtime_not_ready_or_controls_off");
                return;
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            if (!WebGLWebSocketRealtimeTransport.IsBrowserOnline)
            {
                Debug.LogWarning("VOICE_SCENE_RELOAD=SKIPPED | reason=browser_offline");
                return;
            }

            VoiceWebGLNetworkRecoveryPrompt recovery =
                FindObjectOfType<VoiceWebGLNetworkRecoveryPrompt>();
            if (recovery == null)
            {
                Debug.LogError("VOICE_SCENE_RELOAD=FAILED | reason=webgl_recovery_owner_missing");
                return;
            }

            nextClickAllowedAt = Time.realtimeSinceStartup + ClickCooldownSeconds;
            recovery.HandleBrowserFullVoiceRecoveryClicked(string.Empty);
            Debug.Log("VOICE_SCENE_RELOAD=REQUESTED | path=webgl_full_voice | roomExit=False | gameServerChanged=False");
#else
            // Editor/Windows control-path fallback. WebGL uses the recovery owner
            // above to also restart its browser microphone/media pipeline.
            nextClickAllowedAt = Time.realtimeSinceStartup + ClickCooldownSeconds;
            ReloadEditorOrWindowsAsync(runtime);
#endif
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        private async void ReloadEditorOrWindowsAsync(VoiceClientRuntime runtime)
        {
            resetInProgress = true;
            bool microphoneWasMuted = runtime.IsMicrophoneMuted;
            bool speakerWasOff = runtime.IsSpeakerOff;
            try
            {
                bool disconnectSent = await runtime.DisconnectForPlayerUnavailableAsync(
                    "manual_voice_reload_without_room_exit", 1500, CancellationToken.None);
                if (runtime != null)
                {
                    if (runtime.IsSpeakerOff != speakerWasOff)
                        runtime.SetSpeakerOff(speakerWasOff);
                    if (!microphoneWasMuted)
                        runtime.SetMicrophoneMuted(false);
                }
                // VoiceClientAutoConnector reconnects once the Voice cleanup finishes.
                Debug.Log("VOICE_SCENE_RELOAD=CONTROL_RESET_DONE" +
                    " | disconnectSent=" + disconnectSent +
                    " | autoReconnect=True | roomExit=False | gameServerChanged=False");
            }
            catch (Exception error)
            {
                Debug.LogError("VOICE_SCENE_RELOAD=ERROR | detail=" + error.Message);
            }
            finally
            {
                resetInProgress = false;
            }
        }
#endif
    }
}
