#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections;
using Network_A.DedicatedGameServer.Client;
using Network_A.Realtime.Controllers;
using Network_A.Voice.Client.Routing.WebGL;
using Network_A.Voice.Client.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network_A.Voice.Client.BrowserAudio.WebGL
{
    // Only the user-initiated WebGL Reload Room path is allowed to wake this hook.
    // It does not reconnect Voice by itself, reset audio, or modify the shared runtime.
    public static class VoiceWebGLManualRoomRestartVoiceRearmHook
    {
        private const string HostName = "VME2_WebGL_Room_Reload_Voice_Rearm_Host";
        private const string RuntimeRootName = "Voice_Client_Runtime_Root";
        private const string VoiceSceneName = "WebGL_Enviroment_Voice";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InstallSceneObserver()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!scene.IsValid() || scene.name != VoiceSceneName ||
                !VoiceWebGLLobbyRouteSelection.IsVoiceModeSelected)
                return;

            RealtimeRoomGameServerManager manager = RealtimeRoomGameServerManager.Instance;
            if (manager == null || !manager.IsManualRoomRestartRunning)
                return;

            GameObject host = GameObject.Find(HostName);
            if (host == null)
                host = new GameObject(HostName);
            UnityEngine.Object.DontDestroyOnLoad(host);

            VoiceWebGLManualRoomRestartRearmRunner runner =
                host.GetComponent<VoiceWebGLManualRoomRestartRearmRunner>();
            if (runner == null)
                runner = host.AddComponent<VoiceWebGLManualRoomRestartRearmRunner>();

            runner.Arm(scene.handle, manager.CurrentRoomId, RuntimeRootName, VoiceSceneName);
        }
    }

    internal sealed class VoiceWebGLManualRoomRestartRearmRunner : MonoBehaviour
    {
        private Coroutine pending;

        public void Arm(int sceneHandle, string roomId, string runtimeRootName, string voiceSceneName)
        {
            if (pending != null)
            {
                StopCoroutine(pending);
                pending = null;
            }
            // The formal LEAVE has already cleared CurrentRoomId at this point;
            // the new JOIN is still pending. An empty room ID is expected here.
            Debug.Log("VOICE_WEBGL_ROOM_RELOAD_REARM=ARMED | roomIdAtSceneLoad=" + roomId);
            pending = StartCoroutine(WaitForConfirmedRoomThenRearm(
                sceneHandle, roomId, runtimeRootName, voiceSceneName));
        }

        private IEnumerator WaitForConfirmedRoomThenRearm(
            int sceneHandle, string roomId, string runtimeRootName, string voiceSceneName)
        {
            // The existing manager uses a 30s Dedicated authentication window.
            // This hook never takes over that window; it only observes its success.
            float deadline = Time.realtimeSinceStartup + 35f;
            while (Time.realtimeSinceStartup < deadline)
            {
                Scene scene = SceneManager.GetActiveScene();
                if (!scene.IsValid() || scene.handle != sceneHandle ||
                    scene.name != voiceSceneName)
                {
                    pending = null;
                    yield break;
                }

                RealtimeRoomGameServerManager manager = RealtimeRoomGameServerManager.Instance;
                DedicatedGameServerRealtimeRoomBinderWebGL binder =
                    DedicatedGameServerRealtimeRoomBinderWebGL.Instance;
                DedicatedGameServerWsClient socket = DedicatedGameServerWsClient.Instance;

                string joinedRoomId = manager != null ? manager.CurrentRoomId : string.Empty;
                bool ready = manager != null && binder != null && socket != null &&
                    manager.IsRealtimeReady && manager.IsJoinedRoom &&
                    !string.IsNullOrWhiteSpace(joinedRoomId) &&
                    !string.Equals(joinedRoomId, "room_public_lobby_main", StringComparison.Ordinal) &&
                    (string.IsNullOrWhiteSpace(roomId) ||
                     string.Equals(joinedRoomId, roomId, StringComparison.Ordinal)) &&
                    binder.HasActiveDedicatedConnection && !binder.IsDisconnectFlowRunning &&
                    !binder.IsConnectFlowRunning && socket.IsReady &&
                    string.Equals(socket.RoomId, joinedRoomId, StringComparison.Ordinal) &&
                    MetaverseNetworkClient.isReady;

                if (!ready)
                {
                    yield return null;
                    continue;
                }

                GameObject root = GameObject.Find(runtimeRootName);
                VoiceClientRuntime runtime = root != null
                    ? root.GetComponent<VoiceClientRuntime>() : null;
                VoiceClientAutoConnector connector = root != null
                    ? root.GetComponent<VoiceClientAutoConnector>() : null;

                if (runtime == null || connector == null)
                {
                    // The regular runtime installer may still be finishing its scene pass.
                    yield return null;
                    continue;
                }

                if (!connector.enabled)
                {
                    // OnEnable of the existing connector already performs a WebGL-only
                    // rearm after its own OnDisable. Do not reset private state or call
                    // Voice ConnectAsync from a second owner.
                    connector.enabled = true;
                    Debug.Log("VOICE_WEBGL_ROOM_RELOAD_REARM=PASS | roomId=" + joinedRoomId +
                              " | connector=enabled | audioReset=False | serverChanged=False");
                }
                else
                {
                    Debug.Log("VOICE_WEBGL_ROOM_RELOAD_REARM=ALREADY_ENABLED | roomId=" + joinedRoomId);
                }

                pending = null;
                yield break;
            }

            Debug.LogWarning("VOICE_WEBGL_ROOM_RELOAD_REARM=NOT_READY | roomId=" + roomId +
                             " | action=none | existing_recovery_preserved=True");
            pending = null;
        }
    }
}
#endif
