#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Network_A.Lobby;
using Network_A.Lobby.Buildings;
using Network_A.UI;
using Network_A.DedicatedGameServer.Client;
using Network_A.Realtime.Controllers;

namespace Network_A.Voice.Client.Routing.WebGL
{
    // Restores the existing server-debug panel during a WebGL building entry.
    // Keeps input blocked until arrival; does not create any Canvas or UI object.
    [DefaultExecutionOrder(31999)]
    public sealed class WebGlLobbyBuildingJoinPanelGuard : MonoBehaviour
    {
        private const string RootName = "VME2_WebGL_Lobby_Join_Panel_Guard";
        private const string MessageId = "VME2_WEBGL_BUILDING_JOIN_GUARD";
        private const float MaxOverallJoinSeconds = 75f; // safety watchdog, NOT the dedicated countdown
        private const float DefaultDedicatedCountdownSeconds = 25f; // Binder fallback only
        private static WebGlLobbyBuildingJoinPanelGuard instance;
        private Lobby1RealtimeSceneController lobby;
        private readonly Dictionary<Button, bool> savedButtonStates = new Dictionary<Button, bool>();
        private bool entryActive;
        private bool previousDedicatedFlow;
        private float entryStartedAt;
        private float dedicatedStartedAt;
        private float dedicatedCountdownBudget = DefaultDedicatedCountdownSeconds;
        private float nextRender;
        private int lastSeconds = int.MinValue;
        private bool noGlobalManagerWarned;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (instance != null) return;
            GameObject root = GameObject.Find(RootName);
            if (root == null) root = new GameObject(RootName);
            DontDestroyOnLoad(root);
            instance = root.GetComponent<WebGlLobbyBuildingJoinPanelGuard>();
            if (instance == null) instance = root.AddComponent<WebGlLobbyBuildingJoinPanelGuard>();
            Debug.Log("VME2_WEBGL_JOIN_PANEL_GUARD=INSTALLED | existingPanelOnly=true");
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            BindLobby();
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            UnbindLobby();
            FinishEntry("disabled");
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!scene.IsValid()) return;
            if (scene.name != VoiceWebGLLobbyRouteSelection.LobbySceneName)
            {
                FinishEntry("scene_changed:" + scene.name);
                UnbindLobby();
                return;
            }
            UnbindLobby();
            BindLobby();
        }

        private void Update()
        {
            if (lobby == null) BindLobby();
            if (!entryActive) return;
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || scene.name != VoiceWebGLLobbyRouteSelection.LobbySceneName)
            {
                FinishEntry("lobby_left");
                return;
            }
            if (Time.unscaledTime - entryStartedAt > MaxOverallJoinSeconds)
            {
                FinishEntry("join_watchdog_expired");
                Debug.LogWarning("VME2_WEBGL_JOIN_PANEL_GUARD=TIMEOUT | join exceeded watchdog; buttons restored");
                return;
            }
            DedicatedGameServerRealtimeRoomBinderWebGL binder = DedicatedGameServerRealtimeRoomBinderWebGL.Instance;
            bool dedicatedRunning = binder != null && binder.IsConnectFlowRunning;
            if (dedicatedRunning && !previousDedicatedFlow)
            {
                dedicatedStartedAt = Time.unscaledTime;
                dedicatedCountdownBudget = ReadExistingDedicatedCountdown(binder);
                lastSeconds = int.MinValue;
                Debug.Log("VME2_WEBGL_JOIN_PANEL_GUARD=DEDICATED_COUNTDOWN_START | seconds=" + dedicatedCountdownBudget);
            }
            previousDedicatedFlow = dedicatedRunning;
            if (Time.unscaledTime >= nextRender)
            {
                nextRender = Time.unscaledTime + 0.2f;
                RenderProgress(dedicatedRunning);
            }
        }

        private void LateUpdate()
        {
            // The underlying room UI can reenable a button during its own Update.
            if (entryActive) LockLobbyButtons();
        }

        private void BindLobby()
        {
            Scene active = SceneManager.GetActiveScene();
            if (!active.IsValid() || active.name != VoiceWebGLLobbyRouteSelection.LobbySceneName) return;
            Lobby1RealtimeSceneController next = UnityEngine.Object.FindFirstObjectByType<Lobby1RealtimeSceneController>();
            if (next == lobby) return;
            UnbindLobby();
            lobby = next;
            if (lobby != null)
            {
                lobby.OnBuildingSelected += OnBuildingSelected;
                Debug.Log("VME2_WEBGL_JOIN_PANEL_GUARD=LOBBY_BOUND");
            }
        }

        private void UnbindLobby()
        {
            if (lobby != null) lobby.OnBuildingSelected -= OnBuildingSelected;
            lobby = null;
        }

        private void OnBuildingSelected(CompletedBuildingDto building)
        {
            if (entryActive || building == null) return;
            entryActive = true;
            previousDedicatedFlow = false;
            entryStartedAt = Time.unscaledTime;
            nextRender = 0f;
            lastSeconds = int.MinValue;
            Debug.Log("VME2_WEBGL_JOIN_PANEL_GUARD=ENTRY_STARTED | featureId=" + building.feature_id);
            RenderProgress(false);
            LockLobbyButtons();
        }

        private void RenderProgress(bool dedicatedRunning)
        {
            if (!GlobalMessageManager.IsReady)
            {
                if (!noGlobalManagerWarned)
                {
                    noGlobalManagerWarned = true;
                    Debug.LogError("VME2_WEBGL_JOIN_PANEL_GUARD=PANEL_MISSING | reason=global_message_manager_not_ready");
                }
                return;
            }
            string status;
            int remaining = -1;
            if (dedicatedRunning)
            {
                remaining = Mathf.Max(0, Mathf.CeilToInt(dedicatedCountdownBudget - (Time.unscaledTime - dedicatedStartedAt)));
                status = "در حال دریافت تیکت و اتصال به روم صوتی...\nزمان باقی‌مانده: " + remaining + " ثانیه";
            }
            else
            {
                RealtimeRoomGameServerManager manager = RealtimeRoomGameServerManager.Instance;
                status = manager != null && manager.IsJoinedRoom && !manager.IsInsidePublicLobbyRoom
                    ? "اتصال به روم برقرار شد. در حال آماده‌سازی Game Server..."
                    : "در حال خروج از لابی و ورود به روم صوتی...";
            }
            if (remaining == lastSeconds && GlobalMessageManager.Contains(MessageId)) return;
            lastSeconds = remaining;
            GlobalMessageManager.Publish(
                MessageId,
                GlobalMessageManager.MessageSource.Realtime,
                GlobalMessageManager.MessageType.Information,
                GlobalMessageManager.Priorities.Reconnecting + 1,
                "ورود به روم صوتی",
                status,
                "VME2_WEBGL_JOIN_PANEL_GUARD | dedicatedRunning=" + dedicatedRunning,
                0f, true, false);
            GlobalMessageManager.RebindPanel();
            Debug.Log("VME2_WEBGL_JOIN_PANEL_GUARD=PANEL_SHOWN | dedicated=" + dedicatedRunning + " | remaining=" + remaining);
        }

        private static float ReadExistingDedicatedCountdown(DedicatedGameServerRealtimeRoomBinderWebGL binder)
        {
            if (binder == null) return DefaultDedicatedCountdownSeconds;
            try
            {
                FieldInfo field = typeof(DedicatedGameServerRealtimeRoomBinderWebGL).GetField(
                    "dedicatedConnectFlowTimeoutSeconds", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null) return DefaultDedicatedCountdownSeconds;
                object value = field.GetValue(binder);
                float timeout = value is float ? (float)value : DefaultDedicatedCountdownSeconds;
                if (timeout >= 5f && timeout <= 120f) return timeout;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("VME2_WEBGL_JOIN_PANEL_GUARD=TIMEOUT_READ_FALLBACK | " + exception.Message);
            }
            return DefaultDedicatedCountdownSeconds;
        }

        private void LockLobbyButtons()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid()) return;
            GameObject[] roots = scene.GetRootGameObjects();
            foreach (GameObject root in roots)
            {
                Button[] buttons = root.GetComponentsInChildren<Button>(true);
                foreach (Button button in buttons)
                {
                    if (button == null || IsServerDebugPanelChild(button.transform)) continue;
                    if (!savedButtonStates.ContainsKey(button)) savedButtonStates[button] = button.interactable;
                    if (button.interactable) button.interactable = false;
                }
            }
        }

        private static bool IsServerDebugPanelChild(Transform transform)
        {
            Transform current = transform;
            while (current != null)
            {
                if (current.name == "Pnl_ServerDebug") return true;
                current = current.parent;
            }
            return false;
        }

        private void FinishEntry(string reason)
        {
            if (!entryActive) return;
            entryActive = false;
            previousDedicatedFlow = false;
            GlobalMessageManager.Clear(MessageId);
            foreach (KeyValuePair<Button, bool> pair in savedButtonStates)
            {
                if (pair.Key != null) pair.Key.interactable = pair.Value;
            }
            savedButtonStates.Clear();
            Debug.Log("VME2_WEBGL_JOIN_PANEL_GUARD=RELEASED | reason=" + reason);
        }
    }
}
#endif
