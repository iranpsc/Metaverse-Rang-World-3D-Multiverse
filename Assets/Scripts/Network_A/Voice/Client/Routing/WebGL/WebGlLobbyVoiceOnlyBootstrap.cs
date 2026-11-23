#if UNITY_WEBGL || UNITY_EDITOR
using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using Network_A.Voice.Client.Routing.WebGL;

namespace Network_A.Voice.Client.Routing.WebGL
{
    // WebGL-only route selection: existing room/join/Dedicated systems remain the owners.
    [DefaultExecutionOrder(32000)]
    public sealed class WebGlLobbyVoiceOnlyBootstrap : MonoBehaviour
    {
        private const string RootName = "VME2_WebGL_Lobby_Voice_Only";
        private static WebGlLobbyVoiceOnlyBootstrap instance;
        private int lobbyHandle = int.MinValue;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (instance != null) return;
            GameObject root = GameObject.Find(RootName);
            if (root == null) root = new GameObject(RootName);
            DontDestroyOnLoad(root);
            instance = root.GetComponent<WebGlLobbyVoiceOnlyBootstrap>();
            if (instance == null) instance = root.AddComponent<WebGlLobbyVoiceOnlyBootstrap>();
            Debug.Log("VME2_WEBGL_LOBBY_AUTO_VOICE=INSTALLED | windowsUntouched=true");
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SelectVoiceInLobby("on_enable");
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!scene.IsValid() || !string.Equals(scene.name, VoiceWebGLLobbyRouteSelection.LobbySceneName, StringComparison.Ordinal)) return;
            lobbyHandle = int.MinValue;
            SelectVoiceInLobby("scene_loaded");
        }

        private void LateUpdate()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !string.Equals(scene.name, VoiceWebGLLobbyRouteSelection.LobbySceneName, StringComparison.Ordinal)) return;
            SelectVoiceInLobby("late_update");
        }

        private void SelectVoiceInLobby(string source)
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !string.Equals(scene.name, VoiceWebGLLobbyRouteSelection.LobbySceneName, StringComparison.Ordinal)) return;
            bool first = lobbyHandle != scene.handle;
            if (first) lobbyHandle = scene.handle;
            if (!VoiceWebGLLobbyRouteSelection.IsVoiceModeSelected)
            {
                VoiceWebGLLobbyRouteSelection.SelectVoiceMode();
                Debug.Log("VME2_WEBGL_LOBBY_AUTO_VOICE=SELECTED | source=" + source + " | target=" + VoiceWebGLLobbyRouteSelection.VoiceGameplaySceneName);
            }
            else if (first)
            {
                Debug.Log("VME2_WEBGL_LOBBY_AUTO_VOICE=READY | scene=" + scene.name);
            }
        }

    }
}
#endif
