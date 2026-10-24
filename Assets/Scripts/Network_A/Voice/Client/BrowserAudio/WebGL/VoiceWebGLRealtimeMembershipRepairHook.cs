#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using Network_A.Realtime.Controllers;
using Network_A.Voice.Client.Runtime;
using Network_A.Voice.Client.Routing.WebGL;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network_A.Voice.Client.BrowserAudio
{
    // Listens for authenticated server refusals; no scene UI, no Game Server disconnect.
    public static class VoiceWebGLRealtimeMembershipRepairInstaller
    {
        private const string OwnerName = "Voice_WebGL_Realtime_Membership_Repair";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!scene.IsValid() ||
                !string.Equals(scene.name, "WebGL_Enviroment_Voice", StringComparison.Ordinal) ||
                !VoiceWebGLLobbyRouteSelection.IsVoiceModeSelected)
                return;

            GameObject existing = GameObject.Find(OwnerName);
            if (existing != null) return;

            GameObject owner = new GameObject(OwnerName);
            UnityEngine.Object.DontDestroyOnLoad(owner);
            owner.AddComponent<VoiceWebGLRealtimeMembershipRepairHook>();
        }
    }

    [DisallowMultipleComponent]
    public sealed class VoiceWebGLRealtimeMembershipRepairHook : MonoBehaviour
    {
        private const string VoiceSceneName = "WebGL_Enviroment_Voice";
        private const float MinimumRepairIntervalSeconds = 20f;
        private VoiceClientRuntime observedRuntime;
        private bool repairing;
        private float nextRepairAt;

        private void Update()
        {
            if (!string.Equals(SceneManager.GetActiveScene().name, VoiceSceneName, StringComparison.Ordinal))
            {
                Unsubscribe();
                return;
            }

            if (observedRuntime == null)
            {
                GameObject runtimeRoot = GameObject.Find("Voice_Client_Runtime_Root");
                VoiceClientRuntime current = runtimeRoot != null
                    ? runtimeRoot.GetComponent<VoiceClientRuntime>() : null;
                if (current != null)
                {
                    observedRuntime = current;
                    observedRuntime.Failed += HandleVoiceFailure;
                }
            }
        }

        private void OnDisable() { Unsubscribe(); }
        private void OnDestroy() { Unsubscribe(); }

        private void Unsubscribe()
        {
            if (observedRuntime != null)
                observedRuntime.Failed -= HandleVoiceFailure;
            observedRuntime = null;
        }

        private void HandleVoiceFailure(string message)
        {
            if (string.IsNullOrEmpty(message) ||
                !message.Contains("Voice connection preparation failed.") ||
                !(message.StartsWith("Voice authentication failed: 6 |", StringComparison.Ordinal) ||
                  message.StartsWith("Voice authentication failed: 7 |", StringComparison.Ordinal)))
                return;

            if (repairing || Time.realtimeSinceStartup < nextRepairAt)
                return;

            RealtimeRoomGameServerManager room = RealtimeRoomGameServerManager.Instance;
            if (room == null || !room.IsRealtimeReady || !room.IsJoinedRoom ||
                room.IsRoomExitInProgress || room.IsRecoveryRunning ||
                string.IsNullOrWhiteSpace(room.CurrentRoomId) || !MetaverseNetworkClient.isReady)
            {
                Debug.LogWarning("VOICE_REALTIME_REPAIR=SKIPPED | reason=game_or_realtime_not_ready");
                return;
            }

            nextRepairAt = Time.realtimeSinceStartup + MinimumRepairIntervalSeconds;
            RunRepairAsync(room);
        }

        private async void RunRepairAsync(RealtimeRoomGameServerManager room)
        {
            repairing = true;
            try
            {
                Debug.Log("VOICE_REALTIME_REPAIR=START | cause=auth_6_or_7 | preserve_game_server=True");
                bool success = await room.RevalidateRealtimeMembershipForVoiceAsync();
                Debug.Log(success
                    ? "VOICE_REALTIME_REPAIR=PASS | voice_auto_retry=True | room_exit=False"
                    : "VOICE_REALTIME_REPAIR=FAILED | voice_auto_retry=True | room_exit=False");
            }
            catch (Exception error)
            {
                Debug.LogError("VOICE_REALTIME_REPAIR=ERROR | detail=" + error.Message);
            }
            finally
            {
                repairing = false;
            }
        }
    }
}
#endif
