#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using Network_A.DedicatedGameServer.Client;
using Network_A.Realtime.Controllers;
using Network_A.UI;
using Network_A.Voice.Client.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Network_A.Voice.Client.WebRTC.Windows
{
    [DisallowMultipleComponent]
    public sealed class WindowsVoiceEntryAndRecoveryUiWrapper : MonoBehaviour
    {
        private const string VoiceScene = "Grpc_Enviroment_Voice";
        private const string LobbyScene = "Lobby 1";
        private const string VoiceRoot = "Voice_Client_Runtime_Root";
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo DebugPanelField = typeof(GlobalMessageManager).GetField("pnl_ServerDebug", PrivateInstance);
        private static readonly FieldInfo RecoveryReasonField = typeof(RealtimeRoomGameServerManager).GetField("unifiedRecoveryReason", PrivateInstance);
        private static readonly FieldInfo LeftPanelFlag = typeof(DedicatedGameServerRealtimeRoomBinder).GetField("showDedicatedPresenceLeftInServerDebugPanel", PrivateInstance);
        private static readonly FieldInfo StatusPresenceFlag = typeof(DedicatedGameServerRealtimeRoomBinder).GetField("showDedicatedPresenceInStatusText", PrivateInstance);
        private static WindowsVoiceEntryAndRecoveryUiWrapper instance;
        private VoiceClientRuntime initializedMicRuntime;
        private Coroutine permissionOperation;
        private Button trackedMicrophoneButton;
        private bool microphoneDesiredOn;
        private bool userMicrophoneChoiceMade;
        private Coroutine userChoiceCapture;
        private string pendingLobbyMessageId = string.Empty;
        private DedicatedGameServerRealtimeRoomBinder controlledBinder;
        private bool originalLeftPanel;
        private bool originalStatusPresence;
        private bool overridesApplied;
        private float nextScan;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (instance != null) return;
            GameObject host = new GameObject("VME2_Windows_Voice_Entry_And_Recovery_UI");
            DontDestroyOnLoad(host);
            instance = host.AddComponent<WindowsVoiceEntryAndRecoveryUiWrapper>();
        }

        private void OnEnable()
        {
            GlobalMessageManager.OnDisplayedMessageChanged += OnDisplayedMessageChanged;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            nextScan = 0f;
            if (scene.name != VoiceScene)
            {
                initializedMicRuntime = null;
                userMicrophoneChoiceMade = false;
                microphoneDesiredOn = false;
            }
            if (scene.name != LobbyScene) RestoreLobbyFlags();
        }

        private void Update()
        {
            string scene = SceneManager.GetActiveScene().name;
            if (scene != VoiceScene && scene != LobbyScene)
            {
                RestoreLobbyFlags();
                return;
            }
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + 0.25f;
                if (scene == LobbyScene) ApplyLobbyFlags();
                else
                {
                    RestoreLobbyFlags();
                    TryEnableInitialVoiceMicrophone();
                    BindUserMicrophoneButton();
                    RestoreMicrophoneAfterReconnect();
                }
            }
            if (scene == VoiceScene) SuppressDedicatedOnlyRepeatPanel();
            if (!string.IsNullOrEmpty(pendingLobbyMessageId))
            {
                string id = pendingLobbyMessageId;
                pendingLobbyMessageId = string.Empty;
                if (GlobalMessageManager.CurrentMessageId == id)
                    GlobalMessageManager.Clear(id);
            }
        }

        private void TryEnableInitialVoiceMicrophone()
        {
            GameObject root = GameObject.Find(VoiceRoot);
            VoiceClientRuntime current = root != null ? root.GetComponent<VoiceClientRuntime>() : null;
            if (current == null || current == initializedMicRuntime) return;
            if (permissionOperation != null) return;
            initializedMicRuntime = current;
            permissionOperation = StartCoroutine(EnableMicrophoneWithPermission(current));
        }

        private IEnumerator EnableMicrophoneWithPermission(VoiceClientRuntime target)
        {
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                AsyncOperation request = Application.RequestUserAuthorization(UserAuthorization.Microphone);
                yield return request;
            }
            if (target != null && SceneManager.GetActiveScene().name == VoiceScene &&
                Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                if (!userMicrophoneChoiceMade && target.IsMicrophoneMuted)
                    target.SetMicrophoneMuted(false);
                if (!userMicrophoneChoiceMade)
                    microphoneDesiredOn = !target.IsMicrophoneMuted;
                Debug.Log("VME2_WIN_MIC_INITIAL_AUTO=RESULT | muted=" + target.IsMicrophoneMuted +
                    " | permission=granted | manualButtonPreserved=true");
            }
            else Debug.LogWarning("VME2_WIN_MIC_INITIAL_AUTO=SKIPPED | reason=permission_or_scene_or_runtime");
            permissionOperation = null;
        }

        private void BindUserMicrophoneButton()
        {
            var panel = FindObjectOfType<VoiceSceneUserConsentPanelController>();
            FieldInfo field = typeof(VoiceSceneUserConsentPanelController).GetField("microphoneButton", PrivateInstance);
            Button current = panel != null && field != null ? field.GetValue(panel) as Button : null;
            if (current == trackedMicrophoneButton) return;
            if (trackedMicrophoneButton != null)
                trackedMicrophoneButton.onClick.RemoveListener(ObserveUserMicrophoneChoice);
            trackedMicrophoneButton = current;
            if (trackedMicrophoneButton != null)
                trackedMicrophoneButton.onClick.AddListener(ObserveUserMicrophoneChoice);
        }

        private void ObserveUserMicrophoneChoice()
        {
            userMicrophoneChoiceMade = true;
            if (userChoiceCapture != null) StopCoroutine(userChoiceCapture);
            userChoiceCapture = StartCoroutine(CaptureUserChoiceAfterButtonAction());
        }

        private IEnumerator CaptureUserChoiceAfterButtonAction()
        {
            yield return null;
            GameObject root = GameObject.Find(VoiceRoot);
            VoiceClientRuntime current = root != null ? root.GetComponent<VoiceClientRuntime>() : null;
            if (current != null)
            {
                microphoneDesiredOn = !current.IsMicrophoneMuted;
                Debug.Log("VME2_WIN_MIC_USER_CHOICE=OBSERVED | desiredOn=" + microphoneDesiredOn);
            }
            userChoiceCapture = null;
        }

        private void RestoreMicrophoneAfterReconnect()
        {
            if (!microphoneDesiredOn || !Application.HasUserAuthorization(UserAuthorization.Microphone)) return;
            GameObject root = GameObject.Find(VoiceRoot);
            VoiceClientRuntime current = root != null ? root.GetComponent<VoiceClientRuntime>() : null;
            var ws = DedicatedGameServerWsClient.Instance;
            var room = RealtimeRoomGameServerManager.Instance;
            if (current == null || !current.IsAuthenticated || !current.IsMicrophoneMuted ||
                ws == null || !ws.IsReady || room == null || room.IsRecoveryRunning ||
                room.IsRoomExitInProgress || room.IsManualRoomRestartRunning ||
                !room.IsJoinedRoom || !room.IsRealtimeReady ||
                !string.Equals(room.CurrentRoomId, ws.RoomId, StringComparison.Ordinal)) return;
            Guid voiceIdentity, dedicatedIdentity;
            if (!Guid.TryParse(current.VoiceConnectionId, out voiceIdentity) ||
                !Guid.TryParse(ws.ConnectionId, out dedicatedIdentity) ||
                voiceIdentity != dedicatedIdentity) return;
            current.SetMicrophoneMuted(false);
            Debug.Log("VME2_WIN_MIC_RECONNECT_RESTORE=RESULT | muted=" + current.IsMicrophoneMuted +
                " | dedicatedIdentityVerified=true | userMuteChoicePreserved=true");
        }

        private void ApplyLobbyFlags()
        {
            var binder = DedicatedGameServerRealtimeRoomBinder.Instance;
            if (binder == controlledBinder && overridesApplied) return;
            RestoreLobbyFlags();
            if (binder == null || LeftPanelFlag == null || StatusPresenceFlag == null) return;
            if (LeftPanelFlag.FieldType != typeof(bool) || StatusPresenceFlag.FieldType != typeof(bool)) return;
            controlledBinder = binder;
            originalLeftPanel = (bool)LeftPanelFlag.GetValue(binder);
            originalStatusPresence = (bool)StatusPresenceFlag.GetValue(binder);
            LeftPanelFlag.SetValue(binder, false);
            StatusPresenceFlag.SetValue(binder, false);
            overridesApplied = true;
            Debug.Log("VME2_WIN_LOBBY_PRESENCE_PANEL=SUPPRESSED | countdownsUntouched=true");
        }

        private void RestoreLobbyFlags()
        {
            if (overridesApplied && controlledBinder != null)
            {
                LeftPanelFlag.SetValue(controlledBinder, originalLeftPanel);
                StatusPresenceFlag.SetValue(controlledBinder, originalStatusPresence);
            }
            controlledBinder = null;
            overridesApplied = false;
        }

        private void OnDisplayedMessageChanged(string id)
        {
            if (SceneManager.GetActiveScene().name != LobbyScene || string.IsNullOrEmpty(id)) return;
            if (!id.StartsWith("GLOBAL_DEDICATED_PLAYER_JOINED_", StringComparison.Ordinal) &&
                !id.StartsWith("GLOBAL_DEDICATED_PLAYER_LEFT_", StringComparison.Ordinal)) return;
            pendingLobbyMessageId = id;
            HideCurrentPanel(id);
        }

        private static void HideCurrentPanel(string id)
        {
            if (GlobalMessageManager.CurrentMessageId != id || DebugPanelField == null) return;
            var messages = GlobalMessageManager.Instance;
            GameObject panel = messages != null ? DebugPanelField.GetValue(messages) as GameObject : null;
            if (panel != null && panel.activeSelf) panel.SetActive(false);
        }

        private void SuppressDedicatedOnlyRepeatPanel()
        {
            string id = GlobalMessageManager.CurrentMessageId;
            if (id != "GLOBAL_DEDICATED_CONNECT_PROGRESS") return;
            var room = RealtimeRoomGameServerManager.Instance;
            if (room == null || !room.IsRecoveryRunning || !room.IsRealtimeReady ||
                !room.IsJoinedRoom || room.IsRoomExitInProgress || room.IsManualRoomRestartRunning ||
                RecoveryReasonField == null) return;
            string reason = RecoveryReasonField.GetValue(room) as string;
            if (string.IsNullOrEmpty(reason) ||
                !reason.StartsWith("dedicated_disconnected:", StringComparison.Ordinal)) return;
            HideCurrentPanel(id);
        }

        private void OnDisable()
        {
            GlobalMessageManager.OnDisplayedMessageChanged -= OnDisplayedMessageChanged;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            RestoreLobbyFlags();
            if (trackedMicrophoneButton != null)
                trackedMicrophoneButton.onClick.RemoveListener(ObserveUserMicrophoneChoice);
            trackedMicrophoneButton = null;
            if (userChoiceCapture != null) StopCoroutine(userChoiceCapture);
            userChoiceCapture = null;
            if (permissionOperation != null) StopCoroutine(permissionOperation);
            permissionOperation = null;
        }

        private void OnDestroy() { if (instance == this) instance = null; }
    }
}
#endif
