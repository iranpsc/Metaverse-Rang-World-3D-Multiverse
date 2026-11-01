#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using Network_A.Voice.Client.Routing;
using UnityEngine;
using UnityEngine.SceneManagement;

// Windows-only runtime addition: does not change Lobby, Realtime, Dedicated, WebGL or Quest sources.
namespace Network_A.Voice.Client.WebRTC.Windows
{
    /// <summary>
    /// Makes the Voice gameplay scene the automatic route for every building entered
    /// from the Windows Lobby 1. This only selects a route; room membership, Dedicated
    /// authentication and scene transition remain owned by the existing systems.
    /// </summary>
    [DefaultExecutionOrder(32000)]
    public sealed class WindowsLobbyVoiceOnlyRouteBootstrap : MonoBehaviour
    {
        private const string RootName = "VME2_Windows_Lobby_Voice_Only_Route";
        private static WindowsLobbyVoiceOnlyRouteBootstrap instance;
        private int currentLobbySceneHandle = int.MinValue;
        private float nextButtonCheckTime;
        private int routeEnforcements;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (Application.isBatchMode) return;
#if UNITY_EDITOR
            // Do not alter an Editor session whose active build target is WebGL/Quest/Android.
            if (UnityEditor.EditorUserBuildSettings.activeBuildTarget !=
                UnityEditor.BuildTarget.StandaloneWindows64) return;
#endif
            if (instance != null) return;
            GameObject root = GameObject.Find(RootName);
            if (root == null) root = new GameObject(RootName);
            DontDestroyOnLoad(root);
            instance = root.GetComponent<WindowsLobbyVoiceOnlyRouteBootstrap>();
            if (instance == null) instance = root.AddComponent<WindowsLobbyVoiceOnlyRouteBootstrap>();
            Debug.Log("VME2_WIN_LOBBY_AUTO_VOICE=INSTALLED | scope=windows | sharedRouteSourceChanged=false | dedicatedFlowChanged=false");
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            ApplyVoiceRouteIfLobby("enable");
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(instance, this)) instance = null;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode loadMode)
        {
            if (!scene.IsValid() ||
                !string.Equals(scene.name, VoiceLobbyRouteSelection.LobbySceneName, StringComparison.Ordinal))
                return;

            // The existing VoiceLobbyRouteSelection listener may reset NORMAL on scene load.
            // LateUpdate also enforces VOICE once all sceneLoaded handlers have run.
            currentLobbySceneHandle = int.MinValue;
            nextButtonCheckTime = 0f;
            ApplyVoiceRouteIfLobby("scene_loaded");
        }

        private void LateUpdate()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() ||
                !string.Equals(scene.name, VoiceLobbyRouteSelection.LobbySceneName, StringComparison.Ordinal))
                return;

            // Ensures correctness independent of sceneLoaded subscriber ordering and
            // also after any later normal-route reset, without per-frame logging.
            ApplyVoiceRouteIfLobby("late_update");

            // The old selection button is now redundant; hide it only in Windows Lobby 1.
            // No scene/prefab asset is edited. Throttle search to avoid per-frame Find.
            if (Time.unscaledTime < nextButtonCheckTime) return;
            nextButtonCheckTime = Time.unscaledTime + 1f;
            HideLegacyVoiceButton("Btn_Set_To_Voice");
            HideLegacyVoiceButton("Btn_Voice");
        }

        private void ApplyVoiceRouteIfLobby(string source)
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() ||
                !string.Equals(scene.name, VoiceLobbyRouteSelection.LobbySceneName, StringComparison.Ordinal))
                return;

            bool firstInScene = currentLobbySceneHandle != scene.handle;
            if (firstInScene) currentLobbySceneHandle = scene.handle;

            if (!VoiceLobbyRouteSelection.IsVoiceModeSelected)
            {
                VoiceLobbyRouteSelection.SelectVoiceMode();
                routeEnforcements++;
                Debug.Log("VME2_WIN_LOBBY_AUTO_VOICE=SELECTED | source=" + source +
                          " | targetScene=" + VoiceLobbyRouteSelection.VoiceGameplaySceneName +
                          " | sceneHandle=" + scene.handle +
                          " | enforcement=" + routeEnforcements);
            }
            else if (firstInScene)
            {
                Debug.Log("VME2_WIN_LOBBY_AUTO_VOICE=READY | sceneHandle=" + scene.handle +
                          " | targetScene=" + VoiceLobbyRouteSelection.VoiceGameplaySceneName);
            }
        }

        private static void HideLegacyVoiceButton(string objectName)
        {
            GameObject button = GameObject.Find(objectName);
            if (button == null) return;
            button.SetActive(false);
            Debug.Log("VME2_WIN_LOBBY_AUTO_VOICE=LEGACY_BUTTON_HIDDEN | button=" + objectName);
        }
    }
}
#endif
