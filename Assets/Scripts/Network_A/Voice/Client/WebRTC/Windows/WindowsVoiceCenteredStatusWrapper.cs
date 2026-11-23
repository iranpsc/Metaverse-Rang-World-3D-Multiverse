#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;
using System.Reflection;
using Network_A.DedicatedGameServer.Client;
using Network_A.Bootstrap;
using Network_A.Realtime.Controllers;
using Network_A.UI;
using Network_A.Voice.Client.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Network_A.Voice.Client.WebRTC.Windows
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(32000)]
    public sealed class WindowsVoiceCenteredStatusWrapper : MonoBehaviour
    {
        private const string VoiceScene = "Grpc_Enviroment_Voice";
        private const string RootName = "VME2_Windows_Voice_Centered_Status";
        private const float RefreshSeconds = 0.25f;
        private const float NoticeSeconds = 3f;
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static WindowsVoiceCenteredStatusWrapper instance;
        private DedicatedGameServerRealtimeRoomBinder binder;
        private DedicatedRemotePlayerViewController presence;
        private DedicatedGameServerWsClient dedicated;
        private VoiceClientRuntime voice;
        private WindowsWebRtcVoiceBridge nativeBridge;
        private Canvas statusCanvas;
        private TextMeshProUGUI statusText;
        private TextMeshProUGUI gameServerStateText;
        private string lastGameServerRendered = string.Empty;
        private bool targetTextsBound;
        private LabelSettings statusLabelSettings;
        private LabelSettings gameServerLabelSettings;

        private struct LabelSettings
        {
            public TextMeshProUGUI Target;
            public bool AutoSizing;
            public bool WordWrapping;
            public float MinimumSize;
            public float MaximumSize;
            public TextOverflowModes Overflow;
        }
        private FieldInfo suppressLeftPanelField;
        private bool originalLeftPanelEnabled;
        private bool leftPanelOverrideApplied;
        private float nextReferencesAt;
        private float nextStatusAt;
        private long lastPcmSamples;
        private long lastNonSilentSamples;
        private float lastPcmAdvanceAt = -1f;
        private string lastPanelNoticeSignature = string.Empty;
        private float panelNoticeExpiresAt;
        private float lastPanelNoticeSeenAt = -1f;
        private string networkUiStatus = string.Empty;
        private bool canonicalOfflinePanelAllowed;
        private static readonly FieldInfo BinderDebugPanelField =
            typeof(DedicatedGameServerRealtimeRoomBinder).GetField(
                "pnlServerDebug", PrivateInstance);

        private float noticeUntil;
        private string noticeText = string.Empty;
        private string pendingClearId = string.Empty;
        private string lastRendered = string.Empty;
        private FieldInfo globalDisplayedEntryField;
        private FieldInfo globalEntryMessageField;
        private FieldInfo globalEntrySourceField;
        private FieldInfo globalEntryTypeField;
        private FieldInfo globalEntryStickyField;
        private FieldInfo globalEntryRetryField;
        private FieldInfo globalEntryReloginField;
        private FieldInfo globalPanelField;
        private FieldInfo globalPanelMessageField;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallInitialScene()
        {
            InstallForScene(SceneManager.GetActiveScene());
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            InstallForScene(scene);
        }

        private static void InstallForScene(Scene scene)
        {
            if (!scene.IsValid() || !string.Equals(scene.name, VoiceScene, StringComparison.Ordinal))
                return;
            if (instance != null) return;
            GameObject host = new GameObject(RootName);
            instance = host.AddComponent<WindowsVoiceCenteredStatusWrapper>();
        }

        private void OnEnable()
        {
            GlobalMessageManager.OnDisplayedMessageChanged += OnGlobalMessageChanged;
            nextReferencesAt = 0f;
            nextStatusAt = 0f;
            CacheGlobalMessageFields();
            ResolveReferences();
        }

        private void Update()
        {
            if (!string.Equals(SceneManager.GetActiveScene().name, VoiceScene, StringComparison.Ordinal))
                return;

            float now = Time.unscaledTime;
            if (now >= nextReferencesAt)
            {
                nextReferencesAt = now + 1f;
                ResolveReferences();
            }

            if (!string.IsNullOrEmpty(pendingClearId))
            {
                string id = pendingClearId;
                pendingClearId = string.Empty;
                if (string.Equals(GlobalMessageManager.CurrentMessageId, id, StringComparison.Ordinal))
                    GlobalMessageManager.Clear(id);
            }

            if (now < nextStatusAt) return;
            nextStatusAt = now + RefreshSeconds;
            UpdateCenteredText(now);
        }

        private void ResolveReferences()
        {
            DedicatedGameServerRealtimeRoomBinder nextBinder = DedicatedGameServerRealtimeRoomBinder.Instance;
            if (nextBinder != binder)
            {
                ReleaseBinder();
                binder = nextBinder;
                if (binder != null)
                {
                    binder.StatusChanged += OnBinderStatusChanged;
                    suppressLeftPanelField = typeof(DedicatedGameServerRealtimeRoomBinder).GetField(
                        "showDedicatedPresenceLeftInServerDebugPanel", PrivateInstance);
                    if (suppressLeftPanelField != null && suppressLeftPanelField.FieldType == typeof(bool))
                    {
                        originalLeftPanelEnabled = (bool)suppressLeftPanelField.GetValue(binder);
                        suppressLeftPanelField.SetValue(binder, false);
                        leftPanelOverrideApplied = true;
                        Debug.Log("VME2_WIN_CENTER_PRESENCE_PANEL=LEFT_SUPPRESSED | binderSourceChanged=false");
                    }
                }
            }

            DedicatedRemotePlayerViewController nextPresence = FindObjectOfType<DedicatedRemotePlayerViewController>();
            if (nextPresence != presence)
            {
                if (presence != null)
                {
                    presence.DedicatedRemotePlayerJoinedForUi -= OnPlayerJoined;
                    presence.DedicatedRemotePlayerLeftForUi -= OnPlayerLeft;
                }
                presence = nextPresence;
                if (presence != null)
                {
                    presence.DedicatedRemotePlayerJoinedForUi += OnPlayerJoined;
                    presence.DedicatedRemotePlayerLeftForUi += OnPlayerLeft;
                }
            }

            if (dedicated == null) dedicated = FindObjectOfType<DedicatedGameServerWsClient>();
            if (voice == null) voice = FindObjectOfType<VoiceClientRuntime>();
            if (nativeBridge == null) nativeBridge = FindObjectOfType<WindowsWebRtcVoiceBridge>();
            if (statusText == null) CreateCenteredText();
        }

        private void CreateCenteredText()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !string.Equals(scene.name, VoiceScene, StringComparison.Ordinal))
                return;

            GameObject canvasObject = null;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (!string.Equals(roots[i].name, "MainCanvas", StringComparison.Ordinal)) continue;
                canvasObject = roots[i];
                break;
            }
            if (canvasObject == null) return;

            Transform statusTransform = canvasObject.transform.Find("StatusText");
            Transform gameServerTransform = canvasObject.transform.Find("GameServerStateText");
            if (statusTransform == null || gameServerTransform == null) return;

            TextMeshProUGUI sceneStatus = statusTransform.GetComponent<TextMeshProUGUI>();
            TextMeshProUGUI sceneGameServer = gameServerTransform.GetComponent<TextMeshProUGUI>();
            if (sceneStatus == null || sceneGameServer == null || sceneStatus == sceneGameServer)
                return;

            // Reuse the two scene labels. Do not create an overlay or change their RectTransforms.
            statusText = sceneStatus;
            gameServerStateText = sceneGameServer;
            statusCanvas = canvasObject.GetComponent<Canvas>();
            statusLabelSettings = CaptureLabelSettings(statusText);
            gameServerLabelSettings = CaptureLabelSettings(gameServerStateText);
            ConfigureSingleLine(statusText);
            ConfigureSingleLine(gameServerStateText);
            targetTextsBound = true;
            lastRendered = string.Empty;
            lastGameServerRendered = string.Empty;
            nextStatusAt = 0f;

            Debug.Log("VME2_WIN_STATUS_EXISTING_TEXT=BOUND | scene=" + VoiceScene +
                " | status=MainCanvas/StatusText | gameServer=MainCanvas/GameServerStateText" +
                " | extraCanvas=false | extraText=false | originalRectTransforms=true");
        }


        private static TMP_Text FindFontReference()
        {
            GameObject canvas = GameObject.Find("MainCanvas");
            if (canvas != null)
            {
                Transform existing = canvas.transform.Find("GameServerStateText");
                if (existing == null) existing = canvas.transform.Find("StatusText");
                if (existing != null)
                {
                    TMP_Text text = existing.GetComponent<TMP_Text>();
                    if (text != null && text.font != null) return text;
                }
                TMP_Text[] labels = canvas.GetComponentsInChildren<TMP_Text>(true);
                foreach (TMP_Text label in labels)
                    if (label != null && label.font != null) return label;
            }
            return null;
        }

        private void UpdateCenteredText(float now)
        {
            if (statusText == null || gameServerStateText == null) return;

            string gameStatus = dedicated != null && dedicated.IsReady
                ? "سرور بازی: متصل"
                : binder != null && binder.IsConnectFlowRunning
                    ? "سرور بازی: در حال اتصال"
                    : RealtimeRoomGameServerManager.Instance != null &&
                      RealtimeRoomGameServerManager.Instance.IsRecoveryRunning
                        ? "سرور بازی: در حال بازیابی"
                        : "سرور بازی: در انتظار اتصال";

            string voiceStatus;
            if (dedicated == null || !dedicated.IsReady)
                voiceStatus = "صوت: در انتظار سرور بازی";
            else if (voice == null || !voice.IsAuthenticated)
                voiceStatus = "صوت: اتصال کنترل برقرار نیست";
            else if (voice.ActiveSessionCount == 0)
                voiceStatus = "صوت: نشست فعالی ثبت نشده";
            else if (nativeBridge == null || !nativeBridge.NativeProducerActive)
                voiceStatus = "صوت: نشست فعال؛ ارسال آماده نیست";
            else if (nativeBridge.NativeReadyConsumerCount == 0)
                voiceStatus = "صوت: نشست فعال؛ منتظر صدای کاربران";
            else
            {
                long pcm = nativeBridge.NativeReceivedPcmSampleCount;
                long audible = nativeBridge.NativeReceivedNonSilentSampleCount;
                if (pcm < lastPcmSamples || audible < lastNonSilentSamples)
                    lastPcmAdvanceAt = -1f; // A new Voice generation reset counters.
                if (pcm > lastPcmSamples) lastPcmAdvanceAt = now;
                bool pcmFlowing = lastPcmAdvanceAt >= 0f && now - lastPcmAdvanceAt < 5f;
                lastPcmSamples = pcm;
                lastNonSilentSamples = audible;
                voiceStatus = pcmFlowing
                    ? (audible > 0 ? "صوت: نشست فعال؛ دریافت PCM برقرار" :
                        "صوت: نشست فعال؛ PCM سکوت دریافت می‌شود")
                    : "صوت: نشست فعال؛ PCM جدید دریافت نشده";
            }

            // Presence notices temporarily replace the voice line, never append a third line.
            lastGameServerRendered = now < panelNoticeExpiresAt &&
                !string.IsNullOrWhiteSpace(networkUiStatus)
                ? networkUiStatus : gameStatus;
            lastRendered = now < noticeUntil && !string.IsNullOrWhiteSpace(noticeText)
                ? noticeText
                : voiceStatus;
            RenderToSceneLabels();
        }

        private void LateUpdate()
        {
            if (!targetTextsBound ||
                !string.Equals(SceneManager.GetActiveScene().name, VoiceScene,
                    StringComparison.Ordinal)) return;
            RouteWindowsNetworkPanelsToSceneText();
            RenderToSceneLabels();
        }

        // Suppress ONLY connection/network popups in the existing voice scene.
        // Real room-join countdowns, Login/Retry actions and other dialogs are preserved.
        // This changes presentation, not the HTTP HEALTH verdict or any socket recovery.
        private void RouteWindowsNetworkPanelsToSceneText()
        {
            if (statusText == null || gameServerStateText == null) return;
            GlobalMessageManager manager = GlobalMessageManager.Instance;
            if (manager != null && globalPanelField != null)
            {
                GameObject panel = globalPanelField.GetValue(manager) as GameObject;
                if (panel != null && panel.activeSelf && IsGlobalNetworkStatus(manager))
                    RoutePanelIfNetworkNotice(panel, "global_network");
            }
            if (binder != null && BinderDebugPanelField != null)
            {
                GameObject panel = BinderDebugPanelField.GetValue(binder) as GameObject;
                if (panel != null && panel.activeSelf)
                    RoutePanelIfNetworkNotice(panel, "dedicated");
            }
        }

        private bool IsGlobalNetworkStatus(GlobalMessageManager manager)
        {
            if (globalDisplayedEntryField == null || globalEntrySourceField == null ||
                globalEntryTypeField == null) return false;
            object entry = globalDisplayedEntryField.GetValue(manager);
            if (entry == null) return false;
            string source = Convert.ToString(globalEntrySourceField.GetValue(entry));
            string type = Convert.ToString(globalEntryTypeField.GetValue(entry));
            return string.Equals(source, "Network", StringComparison.Ordinal) &&
                   string.Equals(type, "NetworkStatus", StringComparison.Ordinal);
        }

        // Real outage is the one canonical, sticky GlobalMessageManager notice
        // emitted by the Windows HTTP /health authority (status 0 after its debounce).
        // Socket, Dedicated, Voice and non-200 service failures never opt in here.
        private bool IsCanonicalHealthInternetOutagePanel(GameObject panel)
        {
            if (StartupNetworkSceneRouter.CurrentState !=
                    StartupNetworkSceneRouter.NetworkState.InternetUnavailable ||
                !string.Equals(GlobalMessageManager.CurrentMessageId,
                    "GLOBAL_NETWORK_STATE", StringComparison.Ordinal)) return false;

            GlobalMessageManager manager = GlobalMessageManager.Instance;
            return manager != null && globalPanelField != null &&
                ReferenceEquals(globalPanelField.GetValue(manager), panel) &&
                IsGlobalNetworkStatus(manager);
        }

        private void RoutePanelIfNetworkNotice(GameObject panel, string source)
        {
            // Never hide the authoritative Internet Lost panel when /health
            // has actually confirmed a loss. The global manager keeps it visible
            // and clears it on HTTP 200. Applies even if Binder shares this panel.
            if (IsCanonicalHealthInternetOutagePanel(panel))
            {
                if (!canonicalOfflinePanelAllowed)
                {
                    canonicalOfflinePanelAllowed = true;
                    Debug.Log("VME2_WIN_INTERNET_PANEL=VISIBLE" +
                        " | verdict=http_health_unavailable" +
                        " | message=GLOBAL_NETWORK_STATE" +
                        " | owner=GlobalMessageManager");
                }
                return;
            }
            if (StartupNetworkSceneRouter.CurrentState !=
                    StartupNetworkSceneRouter.NetworkState.InternetUnavailable)
                canonicalOfflinePanelAllowed = false;

            TextMeshProUGUI[] labels = panel.GetComponentsInChildren<TextMeshProUGUI>(true);
            string title = string.Empty;
            string message = string.Empty;
            string technical = string.Empty;
            foreach (TextMeshProUGUI label in labels)
            {
                if (label == null) continue;
                if (label.name == "Txt_ServerDebugTitle") title = label.text;
                else if (label.name == "Txt_ServerDebugMessage") message = label.text;
                else if (label.name == "Txt_ServerDebugTechnical") technical = label.text;
            }
            // Never hide a genuine timed room connect screen, even if its owner is Network.
            if (ContainsInvariant(message, "زمان باقی") ||
                ContainsInvariant(technical, "remainingSeconds=") ||
                ContainsInvariant(message, "remaining seconds")) return;

            if (source == "dedicated" && !IsDedicatedNetworkNotice(title, message, technical))
                return;
            if (source == "global_network" &&
                (GlobalMessageManager.CurrentMessageId == "GLOBAL_DEDICATED_CONNECT_PROGRESS"))
                return;

            string signature = source + "|" + title + "|" + message;
            float noticeNow = Time.unscaledTime;
            if (!string.Equals(signature, lastPanelNoticeSignature, StringComparison.Ordinal) ||
                lastPanelNoticeSeenAt < 0f || noticeNow - lastPanelNoticeSeenAt > 1f)
            {
                lastPanelNoticeSignature = signature;
                bool httpOnline = StartupNetworkSceneRouter.CurrentState ==
                    StartupNetworkSceneRouter.NetworkState.Online;
                // Copy the actual panel message to the existing status label;
                // never label a healthy HTTP origin as "internet disconnected".
                string visibleMessage = !string.IsNullOrWhiteSpace(message)
                    ? message.Trim() : title.Trim();
                visibleMessage = visibleMessage.Replace("\r", " ").Replace("\n", " ");
                bool falseInternetLoss = httpOnline &&
                    (ContainsInvariant(visibleMessage, "اینترنت قطع") ||
                     ContainsInvariant(visibleMessage, "اتصال اینترنت قطع") ||
                     ContainsInvariant(title, "اینترنت قطع"));
                networkUiStatus = falseInternetLoss
                    ? "سرور بازی: اختلال اتصال سرویس؛ HTTP HEALTH سالم"
                    : "سرور بازی: " + visibleMessage;
                panelNoticeExpiresAt = Time.unscaledTime + 3f;
                nextStatusAt = 0f;
                Debug.Log("VME2_WIN_NETWORK_PANEL=ROUTED_TO_EXISTING_TEXT" +
                    " | source=" + source + " | healthOnline=" + httpOnline +
                    " | countdown=false | internetVerdictUnchanged=true");
            }
            lastPanelNoticeSeenAt = noticeNow;
            // GlobalMessageManager may re-activate a sticky message every 150 ms.
            // Enforce the voice-scene presentation policy after its Update, every frame.
            panel.SetActive(false);
        }

        private static bool IsDedicatedNetworkNotice(string title, string message, string technical)
        {
            return ContainsInvariant(title, "اینترنت") ||
                ContainsInvariant(message, "اینترنت") ||
                ContainsInvariant(title, "بازیابی اتصال") ||
                ContainsInvariant(title, "افت ارتباط") ||
                ContainsInvariant(technical, "GAME_SERVER_NETWORK_LOST") ||
                ContainsInvariant(technical, "GAME_SERVER_CONNECTION_LOST") ||
                ContainsInvariant(technical, "GAME_SERVER_RECONNECT") ||
                ContainsInvariant(technical, "REALTIME_TRANSPORT_DROP");
        }

        private static bool ContainsInvariant(string value, string needle)
        {
            return !string.IsNullOrEmpty(value) &&
                value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void RenderToSceneLabels()
        {
            if (statusText == null || gameServerStateText == null) return;
            if (!string.Equals(statusText.text, lastRendered, StringComparison.Ordinal))
                statusText.text = lastRendered;
            if (!string.Equals(gameServerStateText.text, lastGameServerRendered,
                    StringComparison.Ordinal))
                gameServerStateText.text = lastGameServerRendered;
        }

        private static LabelSettings CaptureLabelSettings(TextMeshProUGUI target)
        {
            return new LabelSettings
            {
                Target = target,
                AutoSizing = target.enableAutoSizing,
                WordWrapping = target.enableWordWrapping,
                MinimumSize = target.fontSizeMin,
                MaximumSize = target.fontSizeMax,
                Overflow = target.overflowMode
            };
        }

        private static void ConfigureSingleLine(TextMeshProUGUI target)
        {
            target.enableWordWrapping = false;
            target.overflowMode = TextOverflowModes.Ellipsis;
            target.enableAutoSizing = true;
            target.fontSizeMin = Mathf.Min(14f, target.fontSize);
            target.fontSizeMax = Mathf.Max(14f, target.fontSize);
        }

        private static void RestoreLabelSettings(LabelSettings settings)
        {
            if (settings.Target == null) return;
            settings.Target.enableAutoSizing = settings.AutoSizing;
            settings.Target.enableWordWrapping = settings.WordWrapping;
            settings.Target.fontSizeMin = settings.MinimumSize;
            settings.Target.fontSizeMax = settings.MaximumSize;
            settings.Target.overflowMode = settings.Overflow;
        }


        private void OnBinderStatusChanged(string status)
        {
            ShowNotice(status);
        }

        private void OnPlayerJoined(string playerId, string displayName)
        {
            ShowNotice(ResolveName(playerId, displayName) + " وارد روم شد.");
        }

        private void OnPlayerLeft(string playerId, string displayName)
        {
            ShowNotice(ResolveName(playerId, displayName) + " از روم خارج شد.");
        }

        private static string ResolveName(string playerId, string displayName)
        {
            return !string.IsNullOrWhiteSpace(displayName) ? displayName.Trim() :
                !string.IsNullOrWhiteSpace(playerId) ? playerId.Trim() : "کاربر";
        }

        private void ShowNotice(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            string sanitized = message.Trim();
            // A Dedicated/Realtime failure is not evidence of internet loss when
            // the canonical HTTP HEALTH verdict remains Online.
            if (StartupNetworkSceneRouter.CurrentState ==
                    StartupNetworkSceneRouter.NetworkState.Online &&
                (ContainsInvariant(sanitized, "اینترنت قطع") ||
                 ContainsInvariant(sanitized, "اتصال اینترنت قطع")))
                sanitized = "سرور بازی: اختلال ارتباط سرویس؛ HTTP HEALTH سالم";
            noticeText = sanitized;
            noticeUntil = Time.unscaledTime + NoticeSeconds;
            nextStatusAt = 0f;
        }

        private void CacheGlobalMessageFields()
        {
            globalDisplayedEntryField = typeof(GlobalMessageManager).GetField("displayedMessage", PrivateInstance);
            globalPanelField = typeof(GlobalMessageManager).GetField("pnl_ServerDebug", PrivateInstance);
            globalPanelMessageField = typeof(GlobalMessageManager).GetField("txt_ServerDebugMessage", PrivateInstance);
            Type entryType = globalDisplayedEntryField != null ? globalDisplayedEntryField.FieldType : null;
            if (entryType == null) return;
            globalEntryMessageField = entryType.GetField("Message");
            globalEntrySourceField = entryType.GetField("Source");
            globalEntryTypeField = entryType.GetField("Type");
            globalEntryStickyField = entryType.GetField("Sticky");
            globalEntryRetryField = entryType.GetField("ShowRetry");
            globalEntryReloginField = entryType.GetField("ShowRelogin");
        }

        private void OnGlobalMessageChanged(string id)
        {
            // The real InternetUnavailable message is sticky and must stay on
            // its existing panel; other sticky notices retain their old policy.
            if (string.Equals(id, "GLOBAL_NETWORK_STATE", StringComparison.Ordinal) &&
                StartupNetworkSceneRouter.CurrentState ==
                    StartupNetworkSceneRouter.NetworkState.InternetUnavailable)
                return;
            if (string.IsNullOrWhiteSpace(id) || globalDisplayedEntryField == null ||
                globalEntryMessageField == null || globalEntrySourceField == null ||
                globalEntryTypeField == null || globalEntryStickyField == null ||
                globalEntryRetryField == null || globalEntryReloginField == null)
                return;
            GlobalMessageManager manager = GlobalMessageManager.Instance;
            if (manager == null) return;
            object entry = globalDisplayedEntryField.GetValue(manager);
            if (entry == null) return;
            string message = globalEntryMessageField.GetValue(entry) as string;
            if (string.IsNullOrWhiteSpace(message)) return;

            // Countdowns, persistent statuses and user actions must retain their panels.
            if (id.EndsWith("_PROGRESS", StringComparison.Ordinal) ||
                message.Contains("زمان باقی‌مانده:") ||
                (bool)globalEntryStickyField.GetValue(entry) ||
                (bool)globalEntryRetryField.GetValue(entry) ||
                (bool)globalEntryReloginField.GetValue(entry)) return;
            string type = globalEntryTypeField.GetValue(entry).ToString();
            if (type != "Information" && type != "Success" && type != "Warning") return;

            ShowNotice(message);
            if (globalPanelField != null)
            {
                GameObject panel = globalPanelField.GetValue(manager) as GameObject;
                if (panel != null) panel.SetActive(false);
            }
            pendingClearId = id;
            Debug.Log("VME2_WIN_STATUS_NOTICE=QUEUED | id=" + id +
                " | target=MainCanvas/StatusText | countdownPreserved=true");
        }


        private void ReleaseBinder()
        {
            if (binder == null) return;
            binder.StatusChanged -= OnBinderStatusChanged;
            if (leftPanelOverrideApplied && suppressLeftPanelField != null)
                suppressLeftPanelField.SetValue(binder, originalLeftPanelEnabled);
            binder = null;
            leftPanelOverrideApplied = false;
            suppressLeftPanelField = null;
        }

        private void OnDisable()
        {
            if (targetTextsBound)
            {
                RestoreLabelSettings(statusLabelSettings);
                RestoreLabelSettings(gameServerLabelSettings);
                targetTextsBound = false;
            }
            GlobalMessageManager.OnDisplayedMessageChanged -= OnGlobalMessageChanged;
            ReleaseBinder();
            if (presence != null)
            {
                presence.DedicatedRemotePlayerJoinedForUi -= OnPlayerJoined;
                presence.DedicatedRemotePlayerLeftForUi -= OnPlayerLeft;
                presence = null;
            }
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
        }
    }
}
#endif
