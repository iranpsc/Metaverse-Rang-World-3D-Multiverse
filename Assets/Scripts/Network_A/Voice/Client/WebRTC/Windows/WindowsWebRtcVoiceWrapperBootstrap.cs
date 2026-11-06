#if (UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN) && !UNITY_WEBGL
using Network_A.Voice.Client.Runtime;
using Network_A.Voice.Client.Capture;
using UnityEngine;

namespace Network_A.Voice.Client.WebRTC.Windows
{
    // Windows-only installer. The older Windows MediaV2 parity adapter is a
    // different microphone owner (and is rejected by the current media server).
    // It must not detach VoiceClientRuntime.HandleFrameEncoded before the
    // native WebRTC bridge inspects and safely takes over that existing event.
    // Do not modify shared VoiceClientRuntime/Capture/Playback source files.
    [DefaultExecutionOrder(32000)]
    public sealed class WindowsWebRtcVoiceWrapperBootstrap : MonoBehaviour
    {
        private static WindowsWebRtcVoiceWrapperBootstrap instance;
        private static bool standaloneOwnerReported;
        private float nextScan;

        // The legacy adapter is installed with BeforeSceneLoad, but its actual
        // event takeover occurs in Update. AfterSceneLoad still runs before
        // the first Update: disable it now, not at a late scene scan.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            PreserveNativeMediaOwnership();
#endif
            if (instance != null) return;
            var host = new GameObject("VME2_Windows_Voice_Wrapper_Installer");
            DontDestroyOnLoad(host);
            instance = host.AddComponent<WindowsWebRtcVoiceWrapperBootstrap>();
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        private static void PreserveNativeMediaOwnership()
        {
            // Find by an exact component name rather than a compile-time type:
            // this keeps the native bridge independent of the deprecated MediaV2
            // implementation and does not affect the WebGL or Unity Editor paths.
            GameObject oldRoot = GameObject.Find("Voice_Windows_Media_WebGL_Parity_Runtime");
            if (oldRoot == null)
            {
                if (!standaloneOwnerReported)
                {
                    Debug.LogWarning("VME2_WINDOWS_VOICE_STANDALONE_OWNER=CHECK" +
                        " | oldMediaAdapter=not_found_yet" +
                        " | nativeOwner=bootstrap" +
                        " | webglAndSharedScripts=untouched");
                    standaloneOwnerReported = true;
                }
                return;
            }

            int found = 0;
            int disabled = 0;
            foreach (MonoBehaviour component in oldRoot.GetComponents<MonoBehaviour>())
            {
                if (component == null || component.GetType().FullName !=
                    "Network_A.Voice.Client.MediaV2.Windows.VoiceWindowsMediaWebGLParityRuntimeAdapter")
                    continue;
                found++;
                if (component.enabled)
                {
                    component.enabled = false;
                    disabled++;
                }
            }

            if (disabled != 0 || !standaloneOwnerReported)
            {
                standaloneOwnerReported = true;
                Debug.Log("VME2_WINDOWS_VOICE_STANDALONE_OWNER=" +
                    (found == 0 ? "CHECK" : "PASS") +
                    " | parityAdaptersFound=" + found +
                    " | parityAdaptersDisabled=" + disabled +
                    " | nativeOwner=bootstrap" +
                    " | legacyFrameHandlerPreserved=true" +
                    " | webglAndSharedScripts=untouched");
            }
        }
#endif

        private void Update()
        {
            if (Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + 0.75f;
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            // Also guard against later reactivation or recreation of the old
            // adapter, including room/scene transitions.
            PreserveNativeMediaOwnership();
#endif
            VoiceClientRuntime[] runtimes = FindObjectsByType<VoiceClientRuntime>(FindObjectsSortMode.None);
            foreach (VoiceClientRuntime runtime in runtimes)
            {
                if (runtime == null || !runtime.isActiveAndEnabled) continue;
                if (runtime.GetComponent<WindowsWebRtcVoiceBridge>() != null) continue;
                var microphone = runtime.GetComponent<VoiceMicrophonePublisher>();
                if (microphone == null) continue;
                var bridge = runtime.gameObject.AddComponent<WindowsWebRtcVoiceBridge>();
                bridge.Initialize(runtime, microphone);
            }
        }

        private void OnDestroy() { if (instance == this) instance = null; }
    }
}
#endif
