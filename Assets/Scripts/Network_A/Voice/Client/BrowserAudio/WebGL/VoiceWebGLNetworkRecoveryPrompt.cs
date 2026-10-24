#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Network_A.DedicatedGameServer.Client;
using Network_A.Realtime.Controllers;
using Network_A.Voice.Dedicated;
using Network_A.Realtime.Transport;
using Network_A.Voice.Client.Runtime;
using Network_A.Voice.Client.Routing.WebGL;
using RTLTMPro;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Network_A.Voice.Client.BrowserAudio
{
    public static class VoiceWebGLNetworkRecoveryPromptInstaller
    {
        private const string RootName = "Voice_WebGL_Network_Recovery_Prompt";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!scene.IsValid() ||
                !VoiceWebGLLobbyRouteSelection.IsVoiceModeSelected ||
                !string.Equals(scene.name,
                    VoiceWebGLLobbyRouteSelection.VoiceGameplaySceneName,
                    StringComparison.Ordinal))
                return;

            GameObject root = GameObject.Find(RootName);
            if (root == null)
            {
                root = new GameObject(RootName, typeof(RectTransform),
                    typeof(Canvas), typeof(CanvasScaler),
                    typeof(GraphicRaycaster));
                UnityEngine.Object.DontDestroyOnLoad(root);
            }

            VoiceWebGLNetworkRecoveryPrompt prompt =
                root.GetComponent<VoiceWebGLNetworkRecoveryPrompt>();
            if (prompt == null)
                prompt = root.AddComponent<VoiceWebGLNetworkRecoveryPrompt>();
            prompt.PrepareForVoiceScene();
        }
    }

    [DisallowMultipleComponent]
    [DefaultExecutionOrder(400)]
    public sealed class VoiceWebGLNetworkRecoveryPrompt : MonoBehaviour
    {
        private const string RuntimeRoot = "Voice_Client_Runtime_Root";

        [DllImport("__Internal")]
        private static extern int VME2W4RecoveryOverlayShow(
            string rootName, string title, string mediaAction,
            string fullAction, int controlsEnabled);

        [DllImport("__Internal")]
        private static extern void VME2W4RecoveryOverlayHide();

        [DllImport("__Internal")]
        private static extern int VME2W4VoiceReloadControlShow(
            string rootName, int enabled);

        [DllImport("__Internal")]
        private static extern void VME2W4VoiceReloadControlHide();

        private const float ManualFullResetCooldownSeconds = 10f;
        private const float PermanentButtonRefreshSeconds = 0.5f;
        private const float FaultSettleSeconds = 0.8f;
        private const float RetryCooldownSeconds = 12f;
        private const float FaultMemorySeconds = 20f;

        private VoiceClientRuntime runtime;
        private VoiceBrowserVoiceRuntimeAdapter adapter;
        private VoiceBrowserMediaSession observedMediaSession;
        private FieldInfo mediaSessionField;
        private FieldInfo mediaBoundField;
        private FieldInfo mediaReconnectPendingField;
        private FieldInfo consecutiveMediaFailuresField;
        private FieldInfo retryAtField;
        private FieldInfo microphoneRequestedField;
        private MethodInfo resetMediaSessionMethod;
        private MethodInfo restoreMicrophoneMirrorMethod;

        private GameObject popup;
        private static TMP_FontAsset sceneRtlFont;
        private RTLTextMeshPro titleText;
        private Button reconnectButton;
        private Button fullVoiceButton;
        private bool scenePrepared;
        private bool displaying;
        private bool fullVoiceResetRunning;
        private float firstFaultAt = -1f;
        private float latestFaultAt = -1f;
        private float nextRecoveryAt;
        private float lastMediaBoundAt = -1f;
        private int mediaRecoveryAttempts;
        private string lastFaultReason = string.Empty;
        private float nextManualFullResetAt;
        private float nextPersistentButtonRefreshAt;
        private float missingNearSessionSince = -1f;
        private float nextNearPeerProbeAt;
        private bool cachedNearPeer;
        private DedicatedRemotePlayerStateReceiver remoteStateReceiver;
        private G7ThreeDModeController threeDModeController;

        private void Awake()
        {
            Canvas canvas = GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 32000;
            CanvasScaler scaler = GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
        }

        internal void PrepareForVoiceScene()
        {
            scenePrepared = true;
            firstFaultAt = -1f;
            latestFaultAt = -1f;
            lastMediaBoundAt = -1f;
            mediaRecoveryAttempts = 0;
            nextRecoveryAt = 0f;
            lastFaultReason = string.Empty;
            missingNearSessionSince = -1f;
            nextNearPeerProbeAt = 0f;
            cachedNearPeer = false;
            HidePrompt();
            EnsureUi();
            Debug.Log("VOICE_WEBGL_RECOVERY_PROMPT=READY | manualFullVoiceOnly=True | roomExit=False");
        }

        private static bool IsVoiceScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            return scene.IsValid() &&
                VoiceWebGLLobbyRouteSelection.IsVoiceModeSelected &&
                string.Equals(scene.name,
                    VoiceWebGLLobbyRouteSelection.VoiceGameplaySceneName,
                    StringComparison.Ordinal);
        }

        private static bool IsRoomReady()
        {
            RealtimeRoomGameServerManager manager =
                RealtimeRoomGameServerManager.Instance;
            return manager != null && manager.IsRealtimeReady &&
                manager.IsJoinedRoom && !manager.IsRoomExitInProgress &&
                !string.IsNullOrWhiteSpace(manager.CurrentRoomId);
        }

        private void Update()
        {
            if (!IsVoiceScene())
            {
                if (scenePrepared)
                {
                    scenePrepared = false;
                    firstFaultAt = -1f;
                    latestFaultAt = -1f;
                    HidePrompt();
                    HidePersistentReloadButton();
                    UnsubscribeMedia();
                    runtime = null;
                    adapter = null;
                }
                return;
            }

            if (!scenePrepared)
                PrepareForVoiceScene();

            ResolveVoiceComponents();
            RefreshPersistentReloadButton();
            if (runtime == null || adapter == null ||
                !runtime.IsVoiceEligible || !MetaverseNetworkClient.isReady)
            {
                missingNearSessionSince = -1f;
                // Once opened, retain the manual-only prompt during a transient
                // control outage. The action is disabled until room/Voice is ready.
                if (displaying)
                    RefreshBrowserOverlay(false);
                return;
            }

            float now = Time.realtimeSinceStartup;
            // Never display recovery controls for a player alone in the room.
            // A media fault without an eligible nearby Dedicated peer does not
            // justify resetting Voice control or capture.
            bool nearPeer = IsNearbyPeerPresent(now);
            if (!nearPeer)
            {
                missingNearSessionSince = -1f;
                firstFaultAt = -1f;
                // Preserve an already visible prompt during a transient room
                // outage. Hide it for a verified solo player in a ready room.
                if (displaying && !IsRoomReady())
                {
                    RefreshBrowserOverlay(false);
                    return;
                }
                HidePrompt();
                return;
            }
            bool missingNearSession = IsNearPeerSessionMissing(now, nearPeer);
            bool mediaBound = GetBoolean(mediaBoundField);
            bool unexpectedDisconnect = GetBoolean(mediaReconnectPendingField);
            int failures = GetInteger(consecutiveMediaFailuresField);
            bool evidence = unexpectedDisconnect || failures > 0 ||
                (latestFaultAt >= 0f && now - latestFaultAt < FaultMemorySeconds);
            bool confirmedFault =
                (evidence &&
                 (!mediaBound ||
                  (latestFaultAt >= 0f && now - latestFaultAt <= 3f))) ||
                missingNearSession;
            if (missingNearSession && !evidence)
                lastFaultReason = "authenticated_near_peer_session_missing";

            // Display is latched: never auto-reconnect or auto-dismiss.
            // A shown prompt waits for an explicit user click, except when the
            // player leaves this scene/room or no nearby participant remains.
            if (!confirmedFault && !displaying)
            {
                firstFaultAt = -1f;
                if (runtime.ActiveSessionCount > 0 && mediaBound &&
                    lastMediaBoundAt >= 0f && now - lastMediaBoundAt > 2f)
                {
                    latestFaultAt = -1f;
                    mediaRecoveryAttempts = 0;
                    lastFaultReason = string.Empty;
                }
                return;
            }

            if (!displaying)
            {
                if (firstFaultAt < 0f)
                    firstFaultAt = now;
                if (now - firstFaultAt < FaultSettleSeconds ||
                    now < nextRecoveryAt)
                    return;
                ShowPrompt(now);
            }

            bool available = WebGLWebSocketRealtimeTransport.IsBrowserOnline &&
                IsRoomReady() && !fullVoiceResetRunning &&
                !runtime.IsDisconnecting && now >= nextManualFullResetAt;
            if (fullVoiceButton != null)
                fullVoiceButton.interactable = available;
            RefreshBrowserOverlay(available);
        }

        // The popup (including its media-failure branch) is meaningful only
        // when another Dedicated participant is present and actually nearby.
        private bool IsNearbyPeerPresent(float now)
        {
            if (!IsRoomReady())
            {
                cachedNearPeer = false;
                nextNearPeerProbeAt = 0f;
                return false;
            }

            if (now < nextNearPeerProbeAt)
                return cachedNearPeer;

            nextNearPeerProbeAt = now + 0.75f;
            if (remoteStateReceiver == null)
                remoteStateReceiver =
                    FindObjectOfType<DedicatedRemotePlayerStateReceiver>();
            if (threeDModeController == null)
                threeDModeController = FindObjectOfType<G7ThreeDModeController>();

            cachedNearPeer = false;
            Transform local = threeDModeController != null
                ? threeDModeController.GetLocalPlayerTransform() : null;
            if (local == null || remoteStateReceiver == null ||
                remoteStateReceiver.RemotePlayerCount == 0)
                return false;

            var states = remoteStateReceiver.CreateSnapshot();
            RealtimeRoomGameServerManager room =
                RealtimeRoomGameServerManager.Instance;
            string roomId = room != null ? room.CurrentRoomId : string.Empty;
            if (string.IsNullOrWhiteSpace(roomId))
                return false;

            for (int i = 0; i < states.Count; i += 1)
            {
                DedicatedRemotePlayerState peer = states[i];
                if (peer == null || peer.roomId != roomId)
                    continue;

                if (Vector3.Distance(local.position, peer.Position) <=
                    VoiceDedicatedProximityEvaluator.EnterDistanceMeters)
                {
                    cachedNearPeer = true;
                    break;
                }
            }
            return cachedNearPeer;
        }

        // Missing logical sessions are observed; never manufactured or
        // force-reconnected by this UI. Only the authority may create them.
        private bool IsNearPeerSessionMissing(float now, bool nearPeer)
        {
            if (runtime == null || runtime.ActiveSessionCount > 0 ||
                !runtime.IsAuthenticated || !IsRoomReady() || !nearPeer)
            {
                missingNearSessionSince = -1f;
                return false;
            }

            if (missingNearSessionSince < 0f)
                missingNearSessionSince = now;
            return now - missingNearSessionSince >= 5f;
        }

        private void ResolveVoiceComponents()
        {
            if (runtime == null)
            {
                GameObject root = GameObject.Find(RuntimeRoot);
                if (root != null)
                    runtime = root.GetComponent<VoiceClientRuntime>();
            }

            if (adapter == null)
            {
                adapter = FindObjectOfType<VoiceBrowserVoiceRuntimeAdapter>();
                if (adapter != null)
                {
                    Type t = typeof(VoiceBrowserVoiceRuntimeAdapter);
                    BindingFlags flags = BindingFlags.NonPublic |
                                         BindingFlags.Instance;
                    mediaSessionField = t.GetField("mediaSession", flags);
                    mediaBoundField = t.GetField("mediaBound", flags);
                    mediaReconnectPendingField = t.GetField("mediaReconnectPending", flags);
                    consecutiveMediaFailuresField = t.GetField(
                        "consecutiveMediaFailures", flags);
                    retryAtField = t.GetField("retryAt", flags);
                    microphoneRequestedField = t.GetField("microphoneRequested", flags);
                    resetMediaSessionMethod = t.GetMethod("ResetMediaSession", flags);
                    restoreMicrophoneMirrorMethod = t.GetMethod(
                        "SetRuntimeMicrophoneMirror", flags);
                    if (mediaSessionField == null || mediaBoundField == null ||
                        resetMediaSessionMethod == null)
                        Debug.LogError("VOICE_WEBGL_RECOVERY_PROMPT=INCOMPATIBLE_ADAPTER");
                }
            }

            if (adapter == null || mediaSessionField == null)
                return;
            VoiceBrowserMediaSession current =
                mediaSessionField.GetValue(adapter) as VoiceBrowserMediaSession;
            if (!ReferenceEquals(current, observedMediaSession))
            {
                UnsubscribeMedia();
                observedMediaSession = current;
                if (current != null)
                {
                    current.Failed += HandleMediaFailure;
                    current.Disconnected += HandleMediaDisconnected;
                    current.Bound += HandleMediaBound;
                }
            }
        }

        private void HandleMediaFailure(string reason)
        {
            ObserveFault("media_failed_" + Safe(reason));
        }

        private void HandleMediaDisconnected(string reason, bool intentional)
        {
            if (!intentional)
                ObserveFault("media_disconnected_" + Safe(reason));
        }

        private void HandleMediaBound(string connectionId)
        {
            lastMediaBoundAt = Time.realtimeSinceStartup;
        }

        private void ObserveFault(string reason)
        {
            latestFaultAt = Time.realtimeSinceStartup;
            lastFaultReason = reason;
            Debug.LogWarning("VOICE_WEBGL_RECOVERY_PROMPT=FAULT | reason=" + reason);
        }

        private bool GetBoolean(FieldInfo field)
        {
            return adapter != null && field != null &&
                field.GetValue(adapter) is bool b && b;
        }

        private int GetInteger(FieldInfo field)
        {
            if (adapter == null || field == null) return 0;
            object value = field.GetValue(adapter);
            return value is int number ? number : 0;
        }

        private void RecoverMedia(string source)
        {
            if (adapter == null || resetMediaSessionMethod == null ||
                !WebGLWebSocketRealtimeTransport.IsBrowserOnline ||
                !IsRoomReady() || fullVoiceResetRunning)
                return;

            try
            {
                bool recapturing = string.Equals(
                    source, "button", StringComparison.Ordinal) &&
                    GetBoolean(microphoneRequestedField) &&
                    adapter.RecreateAudioPipelineForUserRecovery();
                if (!recapturing)
                {
                    resetMediaSessionMethod.Invoke(adapter,
                        new object[] { "user_safe_recovery_" + Safe(source) });
                }
                retryAtField?.SetValue(adapter, 0f);
                mediaRecoveryAttempts += 1;
                nextRecoveryAt = Time.realtimeSinceStartup +
                    RetryCooldownSeconds;
                firstFaultAt = -1f;
                latestFaultAt = -1f;
                HidePrompt();
                Debug.LogWarning(
                    "VOICE_WEBGL_RECOVERY_PROMPT=MEDIA_RESTART" +
                    " | source=" + source +
                    " | attempts=" + mediaRecoveryAttempts +
                    " | roomExit=False" +
                    " | captureRestart=" + recapturing +
                    " | previousFault=" + lastFaultReason);
            }
            catch (Exception error)
            {
                nextRecoveryAt = Time.realtimeSinceStartup +
                    RetryCooldownSeconds;
                HidePrompt();
                Debug.LogError("VOICE_WEBGL_RECOVERY_PROMPT=ERROR | detail=" +
                    Safe(error.Message));
            }
        }

        private async void RecoverCompleteVoice()
        {
            if (Time.realtimeSinceStartup < nextManualFullResetAt ||
                fullVoiceResetRunning || runtime == null ||
                runtime.IsDisconnecting || !runtime.IsVoiceEligible ||
                !IsRoomReady() || !MetaverseNetworkClient.isReady ||
                !WebGLWebSocketRealtimeTransport.IsBrowserOnline)
                return;

            fullVoiceResetRunning = true;
            nextManualFullResetAt = Time.realtimeSinceStartup +
                ManualFullResetCooldownSeconds;
            nextPersistentButtonRefreshAt = 0f;
            bool selectedMic = GetBoolean(microphoneRequestedField);
            bool selectedSpeakerOff = runtime.IsSpeakerOff;
            nextRecoveryAt = Time.realtimeSinceStartup + 20f;
            HidePrompt();
            try
            {
                // Start capture rebuild synchronously from this user gesture.
                // The control reconnect below must not inherit a stopped stream.
                bool captureRestarted = selectedMic && adapter != null &&
                    adapter.RecreateAudioPipelineForUserRecovery();
                bool result = await runtime.DisconnectForPlayerUnavailableAsync(
                    "user_full_voice_reconnect_without_room_exit", 1500,
                    CancellationToken.None);
                if (runtime != null && runtime.IsSpeakerOff != selectedSpeakerOff)
                    runtime.SetSpeakerOff(selectedSpeakerOff);
                if (selectedMic && runtime != null)
                {
                    // Voice cleanup mutes its publisher; restore the already
                    // selected microphone without acquiring a second capture.
                    if (adapter != null && restoreMicrophoneMirrorMethod != null)
                        restoreMicrophoneMirrorMethod.Invoke(adapter,
                            new object[] { false });
                    else
                        runtime.SetMicrophoneMuted(false);
                }
                if (adapter != null)
                    retryAtField?.SetValue(adapter, 0f);
                // VoiceClientAutoConnector reconnects control automatically.
                Debug.Log("VOICE_WEBGL_RECOVERY_PROMPT=FULL_VOICE_RECONNECT" +
                    " | result=" + result +
                    " | captureRestarted=" + captureRestarted +
                    " | roomExit=False");
            }
            catch (Exception error)
            {
                Debug.LogError("VOICE_WEBGL_RECOVERY_PROMPT=FULL_VOICE_ERROR | detail=" +
                    Safe(error.Message));
            }
            finally
            {
                firstFaultAt = -1f;
                latestFaultAt = -1f;
                fullVoiceResetRunning = false;
                nextPersistentButtonRefreshAt = 0f;
            }
        }

        private void RefreshPersistentReloadButton()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextPersistentButtonRefreshAt) return;
            nextPersistentButtonRefreshAt = now + PermanentButtonRefreshSeconds;

            // A user-designed Unity UI button takes precedence over the DOM one.
            // Keep the browser recovery prompt for faults, but do not duplicate
            // the permanent reload control on the screen.
            if (FindObjectOfType<VoiceSceneReloadSoundButton>(true) != null)
            {
                try { VME2W4VoiceReloadControlHide(); }
                catch (Exception) { }
                return;
            }

            bool enabled = runtime != null &&
                !runtime.IsDisconnecting && runtime.IsVoiceEligible &&
                MetaverseNetworkClient.isReady && IsRoomReady() &&
                WebGLWebSocketRealtimeTransport.IsBrowserOnline &&
                !fullVoiceResetRunning && now >= nextManualFullResetAt;

            try
            {
                VME2W4VoiceReloadControlShow(gameObject.name, enabled ? 1 : 0);
            }
            catch (Exception exception)
            {
                Debug.LogError("VOICE_W4_PERMANENT_RELOAD_UI=UNAVAILABLE | detail=" +
                    Safe(exception.Message));
            }
        }

        private void HidePersistentReloadButton()
        {
            nextPersistentButtonRefreshAt = 0f;
            try { VME2W4VoiceReloadControlHide(); }
            catch (Exception) { }
        }

        private void EnsureUi()
        {
            if (popup != null) return;
            RTLTextMeshPro[] existingTexts =
                FindObjectsOfType<RTLTextMeshPro>(true);
            foreach (RTLTextMeshPro existing in existingTexts)
            {
                if (existing != null && existing.font != null &&
                    !existing.transform.IsChildOf(transform))
                {
                    sceneRtlFont = existing.font;
                    break;
                }
            }
            popup = new GameObject("RecoveryPanel", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Image));
            popup.transform.SetParent(transform, false);
            RectTransform rect = popup.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -80f);
            rect.sizeDelta = new Vector2(780f, 150f);
            Image background = popup.GetComponent<Image>();
            background.color = new Color(0.08f, 0.10f, 0.14f, 0.94f);
            background.raycastTarget = false;

            titleText = MakeText(popup.transform, "RecoveryTitle",
                new Vector2(0f, -15f), new Vector2(740f, 48f), 28f);
            // Only the manual full-Voice action is offered in the popup.
            // Keep the separate permanent ReloadSound UI and its handler.
            Button complete = MakeButton(popup.transform,
                "RecoveryFullVoiceButton", new Vector2(0f, -91f),
                new Vector2(560f, 63f), out RTLTextMeshPro fullText);
            fullText.text = "بازسازی کامل صدا";
            complete.onClick.AddListener(RecoverCompleteVoice);
            fullVoiceButton = complete;
            popup.SetActive(false);
        }

        private static RTLTextMeshPro MakeText(
            Transform parent, string name, Vector2 pos,
            Vector2 size, float fontSize)
        {
            GameObject go = new GameObject(name, typeof(RectTransform),
                typeof(CanvasRenderer), typeof(RTLTextMeshPro));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = size;
            RTLTextMeshPro label = go.GetComponent<RTLTextMeshPro>();
            label.fontSize = fontSize;
            label.alignment = TextAlignmentOptions.Center;
            label.color = Color.white;
            label.raycastTarget = false;
            if (sceneRtlFont != null)
                label.font = sceneRtlFont;
            return label;
        }

        private static Button MakeButton(
            Transform parent, string name, Vector2 pos,
            Vector2 size, out RTLTextMeshPro label)
        {
            GameObject go = new GameObject(name, typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = size;
            Image image = go.GetComponent<Image>();
            image.color = new Color(0.13f, 0.42f, 0.68f, 1f);
            Button button = go.GetComponent<Button>();
            button.targetGraphic = image;
            label = MakeText(go.transform, "Label", new Vector2(0f, -9f),
                new Vector2(size.x - 16f, size.y - 14f), 23f);
            return button;
        }

        private void ShowPrompt(float now)
        {
            if (popup == null) EnsureUi();
            if (popup == null) return;
            if (EventSystem.current == null)
            {
                Debug.LogWarning(
                    "VOICE_WEBGL_RECOVERY_PROMPT=NO_EVENT_SYSTEM | buttonMayNotRespond=True");
            }
            displaying = true;
            // The browser overlay renders Persian through the browser font stack.
            // Keep the Unity popup hidden: its LiberationSans SDF lacks glyphs.
            popup.SetActive(false);
            RefreshBrowserOverlay(
                WebGLWebSocketRealtimeTransport.IsBrowserOnline && IsRoomReady());
            Debug.LogWarning("VOICE_WEBGL_RECOVERY_PROMPT=SHOWN" +
                " | reason=" + lastFaultReason +
                " | manualFullVoiceOnly=True" +
                " | roomExit=False");
        }

        // Browser DOM buttons invoke only the existing Voice recovery handlers.
        public void HandleBrowserMediaRecoveryClicked(string ignored)
        {
            RecoverMedia("button");
        }

        public void HandleBrowserFullVoiceRecoveryClicked(string ignored)
        {
            RecoverCompleteVoice();
        }

        private void RefreshBrowserOverlay(bool available)
        {
            if (!displaying) return;
            string title = available
                ? "اختلال ارتباط صوتی تشخیص داده شد"
                : "در انتظار بازگشت اینترنت و اتصال اتاق";
            // The media-action slot is intentionally empty for compatibility
            // with the unchanged native interop function signature.
            string mediaAction = string.Empty;
            try
            {
                VME2W4RecoveryOverlayShow(
                    gameObject.name, title, mediaAction,
                    "بازسازی کامل صدا", available ? 1 : 0);
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VOICE_W4_RECOVERY_OVERLAY=UNAVAILABLE | detail=" +
                    Safe(exception.Message));
            }
        }

        private void HidePrompt()
        {
            bool wasDisplaying = displaying;
            displaying = false;
            if (popup != null) popup.SetActive(false);
            if (!wasDisplaying) return;
            try { VME2W4RecoveryOverlayHide(); }
            catch (Exception) { }
        }

        private void UnsubscribeMedia()
        {
            if (observedMediaSession != null)
            {
                observedMediaSession.Failed -= HandleMediaFailure;
                observedMediaSession.Disconnected -= HandleMediaDisconnected;
                observedMediaSession.Bound -= HandleMediaBound;
            }
            observedMediaSession = null;
        }

        private void OnDestroy()
        {
            HidePrompt();
            HidePersistentReloadButton();
            UnsubscribeMedia();
            if (reconnectButton != null)
                reconnectButton.onClick.RemoveAllListeners();
            if (fullVoiceButton != null)
                fullVoiceButton.onClick.RemoveAllListeners();
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
#endif
