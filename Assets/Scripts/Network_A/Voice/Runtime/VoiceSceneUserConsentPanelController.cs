using System;
using System.Collections;
using Network_A.DedicatedGameServer.Client;
using Network_A.Realtime.Controllers;
using Network_A.Voice.Dedicated;
using RTLTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Network_A.Voice.Client.Runtime
{
    public sealed class VoiceSceneUserConsentPanelController : MonoBehaviour
    {
        private const string RuntimeRootName = "Voice_Client_Runtime_Root";

        [Header("Status")]
        [SerializeField] private RTLTextMeshPro statusText;

        [Header("Microphone")]
        [SerializeField] private Button microphoneButton;
        [SerializeField] private Sprite micOnSprite;
        [SerializeField] private Sprite micOffSprite;

        [Header("Speaker")]
        [SerializeField] private Button speakerButton;
        [SerializeField] private Sprite speakerOnSprite;
        [SerializeField] private Sprite speakerOffSprite;

        private VoiceClientRuntime runtime;
        private bool microphonePermissionRequestRunning;
        private bool speakerOff;
        private bool displayedStatusInitialized;
        private bool displayedAuthenticated;
        private int displayedSessionCount = -1;

        // Visual-only connection indicators. Never rotate a Button's own
        // RectTransform: that would change the UI hit area and mobile layout.
        private const float SessionSpinnerSpeedDegrees = 230f;
        private const int SessionSpinnerSegments = 10;
        private const float SessionSpinnerRadius = 57f;
        private RectTransform microphoneSessionSpinner;
        private RectTransform speakerSessionSpinner;
        private RectTransform microphoneSessionStatusBadge;
        private RectTransform speakerSessionStatusBadge;
        private bool sessionSpinnerVisible;
        private const float SessionSpinnerPeerCheckIntervalSeconds = 0.25f;
        private float nextSessionSpinnerPeerCheckAt;
        private bool cachedNearbyVoicePeer;
        private DedicatedRemotePlayerStateReceiver sessionSpinnerRemoteStates;
        private G7ThreeDModeController sessionSpinnerThreeDMode;

        //* این تابع هنگام فعال شدن پنل، دکمه‌های دستی صحنه را به کنترل‌های صوت وصل می‌کند.
        private void OnEnable()
        {
            displayedStatusInitialized = false;
            TryResolveRuntime();

            if (microphoneButton != null)
            {
                microphoneButton.onClick.RemoveListener(
                    HandleMicrophoneButtonClicked);

                microphoneButton.onClick.AddListener(
                    HandleMicrophoneButtonClicked);
            }

            if (speakerButton != null)
            {
                speakerButton.onClick.RemoveListener(
                    HandleSpeakerButtonClicked);

                speakerButton.onClick.AddListener(
                    HandleSpeakerButtonClicked);
            }

            Debug.Log("VOICE_V6_SCENE_USER_CONSENT_PANEL=READY" +
                " | micButtonAssigned=" + (microphoneButton != null) +
                " | speakerButtonAssigned=" + (speakerButton != null));

            UpdateUi();
        }

        //* این تابع هنگام غیرفعال شدن پنل، اتصال دکمه‌ها را آزاد می‌کند.
        private void OnDisable()
        {
            SetSessionSpinnerVisible(false);
            StopAllCoroutines();
            nextSessionSpinnerPeerCheckAt = 0f;
            cachedNearbyVoicePeer = false;
            microphonePermissionRequestRunning = false;
            if (microphoneButton != null)
            {
                microphoneButton.onClick.RemoveListener(
                    HandleMicrophoneButtonClicked);
            }

            if (speakerButton != null)
            {
                speakerButton.onClick.RemoveListener(
                    HandleSpeakerButtonClicked);
            }
        }

        //* این تابع وضعیت Runtime را پیدا می‌کند و ظاهر پنل را با وضعیت واقعی Voice هماهنگ نگه می‌دارد.
        private void Update()
        {
            TryResolveRuntime();
            UpdateUi();
            AnimateSessionSpinners();
        }

        //* این تابع Runtime صوت را از ریشه ساخته‌شده توسط مسیر Voice پیدا می‌کند.
        private void TryResolveRuntime()
        {
            if (runtime != null)
                return;

            GameObject root = GameObject.Find(RuntimeRootName);

            if (root == null)
                return;

            runtime = root.GetComponent<VoiceClientRuntime>();
        }

        //* این تابع فقط با کلیک مستقیم کاربر اجازه میکروفن را می‌گیرد و سپس میکروفن را روشن می‌کند.
        private void HandleMicrophoneButtonClicked()
        {
            TryResolveRuntime();
            Debug.Log("VOICE_W4_MIC_WIDGET_CLICK=RECEIVED" +
                " | runtimeReady=" + (runtime != null) +
                " | requestRunning=" + microphonePermissionRequestRunning);
            if (runtime == null)
            {
                Debug.LogWarning(
                    "VOICE_V6_MIC_USER_ACTION=FAIL" +
                    " | reason=runtime_missing");

                UpdateUi();
                return;
            }

            if (!runtime.IsMicrophoneMuted)
            {
                runtime.SetMicrophoneMuted(true);

                Debug.Log(
                    "VOICE_V6_MIC_USER_DISABLED=" +
                    (runtime.IsMicrophoneMuted ? "PASS" : "FAIL") +
                    " | actualMuted=" + runtime.IsMicrophoneMuted);

                UpdateUi();
                return;
            }

            if (microphonePermissionRequestRunning)
                return;

            if (Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                runtime.SetMicrophoneMuted(false);
                Debug.Log("VOICE_W4_MIC_WIDGET_ENABLE=" +
                    (!runtime.IsMicrophoneMuted ? "PASS" : "FAIL") +
                    " | authorization=already_granted" +
                    " | actualMuted=" + runtime.IsMicrophoneMuted);
                UpdateUi();
                return;
            }

            StartCoroutine(
                RequestMicrophonePermissionAndEnable());

            UpdateUi();
        }

        //* این تابع درخواست اجازه میکروفن را از سیستم می‌گیرد و فقط در صورت تأیید کاربر، دریافت صدا را فعال می‌کند.
        private IEnumerator RequestMicrophonePermissionAndEnable()
        {
            microphonePermissionRequestRunning = true;

            Debug.Log(
                "VOICE_V6_MIC_PERMISSION_REQUEST=START");

            AsyncOperation request =
                Application.RequestUserAuthorization(
                    UserAuthorization.Microphone);

            yield return request;

            microphonePermissionRequestRunning = false;

            if (runtime == null)
            {
                Debug.LogWarning("VOICE_W4_MIC_WIDGET_ENABLE=SKIPPED" +
                    " | reason=runtime_missing_after_permission");
                yield break;
            }

            if (!Application.HasUserAuthorization(
                    UserAuthorization.Microphone))
            {
                runtime.SetMicrophoneMuted(true);

                Debug.LogWarning(
                    "VOICE_V6_MIC_PERMISSION=FAIL" +
                    " | reason=user_denied_or_platform_denied");

                UpdateUi();
                yield break;
            }

            if (runtime == null)
            {
                Debug.LogWarning("VOICE_W4_MIC_WIDGET_ENABLE=SKIPPED" +
                    " | reason=runtime_lost_during_permission");
                yield break;
            }
            runtime.SetMicrophoneMuted(false);

            Debug.Log(
                "VOICE_V6_MIC_PERMISSION=PASS");

            Debug.Log(
                "VOICE_V6_MIC_USER_ENABLED=" +
                (!runtime.IsMicrophoneMuted ? "PASS" : "FAIL") +
                " | actualMuted=" + runtime.IsMicrophoneMuted);

            UpdateUi();
        }

        //* این تابع دکمه اصلی Speaker را کنترل می‌کند و تمام صدای ورودی کاربر را قطع یا وصل می‌کند.
        private void HandleSpeakerButtonClicked()
        {
            if (runtime == null)
            {
                Debug.LogWarning(
                    "VOICE_V6_SPEAKER_USER_ACTION=FAIL" +
                    " | reason=runtime_missing");

                UpdateUi();
                return;
            }

            speakerOff = !runtime.IsSpeakerOff;

            runtime.SetSpeakerOff(speakerOff);

            Debug.Log(
                "VOICE_V6_SPEAKER_USER_SELECTED=PASS" +
                " | speakerOff=" + speakerOff +
                " | scope=all_incoming_audio");

            UpdateUi();
        }

        //* این تابع وضعیت دوخطی پنل، اسپرایت‌ها و فعال بودن دکمه‌ها را بر اساس Runtime واقعی به‌روزرسانی می‌کند.
        private void UpdateUi()
        {
            bool runtimeReady = runtime != null;

            bool authenticated =
                runtimeReady &&
                runtime.IsAuthenticated;

            bool micMuted =
                !runtimeReady ||
                runtime.IsMicrophoneMuted;

            speakerOff =
                runtimeReady
                    ? runtime.IsSpeakerOff
                    : speakerOff;

            int sessionCount =
                runtimeReady
                    ? runtime.ActiveSessionCount
                    : 0;

            // An absent session is not an active connection attempt when
            // no eligible Dedicated participant is within voice-entry range.
            // Visual only: this never starts, stops or resets any connection.
            bool waitingForNearbyVoiceSession =
                runtimeReady && runtime.IsVoiceEligible &&
                (!authenticated || sessionCount <= 0) &&
                IsNearbyVoicePeerPresent();
            SetSessionSpinnerVisible(waitingForNearbyVoiceSession);

            if (statusText != null &&
                (!displayedStatusInitialized ||
                 displayedAuthenticated != authenticated ||
                 displayedSessionCount != sessionCount))
            {
                statusText.text =
                    "وضعیت صدا: " +
                    (authenticated && sessionCount > 0
                        ? "وصل"
                        : authenticated
                            ? "در انتظار تشکیل نشست"
                            : "در انتظار اتصال") +
                    "\n" +
                    "نشست فعال: " +
                    sessionCount;

                displayedAuthenticated = authenticated;
                displayedSessionCount = sessionCount;
                displayedStatusInitialized = true;
            }

            if (microphoneButton != null &&
                microphoneButton.image != null)
            {
                Sprite targetMicSprite =
                    micMuted
                        ? micOffSprite
                        : micOnSprite;

                if (targetMicSprite != null &&
                    microphoneButton.image.sprite != targetMicSprite)
                {
                    microphoneButton.image.sprite =
                        targetMicSprite;

                    Debug.Log(
                        "VOICE_V6_MIC_SPRITE_CHANGED=PASS" +
                        " | state=" +
                        (micMuted ? "OFF" : "ON") +
                        " | sprite=" +
                        targetMicSprite.name);
                }
            }

            if (speakerButton != null &&
                speakerButton.image != null)
            {
                Sprite targetSpeakerSprite =
                    speakerOff
                        ? speakerOffSprite
                        : speakerOnSprite;

                if (targetSpeakerSprite != null &&
                    speakerButton.image.sprite != targetSpeakerSprite)
                {
                    speakerButton.image.sprite =
                        targetSpeakerSprite;

                    Debug.Log(
                        "VOICE_V6_SPEAKER_SPRITE_CHANGED=PASS" +
                        " | state=" +
                        (speakerOff ? "OFF" : "ON") +
                        " | sprite=" +
                        targetSpeakerSprite.name);
                }
            }

            if (microphoneButton != null)
            {
                bool shouldBeInteractable = runtimeReady && !microphonePermissionRequestRunning;
                if (microphoneButton.interactable != shouldBeInteractable)
                    microphoneButton.interactable = shouldBeInteractable;
            }

            if (speakerButton != null)
            {
                if (speakerButton.interactable != runtimeReady)
                    speakerButton.interactable = runtimeReady;
            }
        }

        // Follow the same Dedicated room/position source and 3m entry policy
        // already used by the WebGL voice recovery prompt. Never infer a
        // nearby peer from ActiveSessionCount alone or from a cached scene
        // object during a disconnected/room-exit state.
        private bool IsNearbyVoicePeerPresent()
        {
            RealtimeRoomGameServerManager room =
                RealtimeRoomGameServerManager.Instance;
            DedicatedGameServerWsClient dedicated =
                DedicatedGameServerWsClient.Instance;

            if (room == null || !room.IsRealtimeReady || !room.IsJoinedRoom ||
                room.IsRoomExitInProgress ||
                string.IsNullOrWhiteSpace(room.CurrentRoomId) ||
                dedicated == null || !dedicated.IsReady ||
                !string.Equals(dedicated.RoomId, room.CurrentRoomId,
                    StringComparison.Ordinal) ||
                !dedicated.HasRecentInboundMessage(8f))
            {
                cachedNearbyVoicePeer = false;
                nextSessionSpinnerPeerCheckAt = 0f;
                return false;
            }

            float now = Time.realtimeSinceStartup;
            if (now < nextSessionSpinnerPeerCheckAt)
                return cachedNearbyVoicePeer;

            nextSessionSpinnerPeerCheckAt =
                now + SessionSpinnerPeerCheckIntervalSeconds;
            cachedNearbyVoicePeer = false;

            if (sessionSpinnerRemoteStates == null)
                sessionSpinnerRemoteStates =
                    FindObjectOfType<DedicatedRemotePlayerStateReceiver>();
            if (sessionSpinnerThreeDMode == null)
                sessionSpinnerThreeDMode =
                    FindObjectOfType<G7ThreeDModeController>();

            Transform local = sessionSpinnerThreeDMode != null
                ? sessionSpinnerThreeDMode.GetLocalPlayerTransform()
                : null;
            if (local == null || sessionSpinnerRemoteStates == null ||
                sessionSpinnerRemoteStates.RemotePlayerCount == 0)
                return false;

            var peers = sessionSpinnerRemoteStates.CreateSnapshot();
            for (int i = 0; i < peers.Count; i++)
            {
                DedicatedRemotePlayerState peer = peers[i];
                if (peer == null ||
                    !string.Equals(peer.roomId, room.CurrentRoomId,
                        StringComparison.Ordinal))
                    continue;

                if (Vector3.Distance(local.position, peer.Position) <=
                    VoiceDedicatedProximityEvaluator.EnterDistanceMeters)
                {
                    cachedNearbyVoicePeer = true;
                    break;
                }
            }
            return cachedNearbyVoicePeer;
        }

        private void SetSessionSpinnerVisible(bool visible)
        {
            bool wasVisible = sessionSpinnerVisible;
            if (visible)
            {
                if (microphoneSessionSpinner == null)
                    microphoneSessionSpinner = CreateSessionSpinner(
                        microphoneButton, "Voice_Microphone_Connecting_Ring");

                if (speakerSessionSpinner == null)
                    speakerSessionSpinner = CreateSessionSpinner(
                        speakerButton, "Voice_Speaker_Connecting_Ring");

                if (microphoneSessionStatusBadge == null)
                    microphoneSessionStatusBadge = CreateSessionStatusBadge(
                        microphoneButton, "Voice_Microphone_Connecting_Status");

                if (speakerSessionStatusBadge == null)
                    speakerSessionStatusBadge = CreateSessionStatusBadge(
                        speakerButton, "Voice_Speaker_Connecting_Status");
            }

            // Only decorations change; button interaction and Voice stay intact.
            ApplySessionSpinnerVisibility(microphoneSessionSpinner, visible);
            ApplySessionSpinnerVisibility(speakerSessionSpinner, visible);
            ApplySessionSpinnerVisibility(microphoneSessionStatusBadge, visible);
            ApplySessionSpinnerVisibility(speakerSessionStatusBadge, visible);
            sessionSpinnerVisible = visible &&
                (microphoneSessionSpinner != null || speakerSessionSpinner != null);

            if (wasVisible != sessionSpinnerVisible)
            {
                Debug.Log("VOICE_SESSION_UI_SPINNER=" +
                    (sessionSpinnerVisible ? "START" : "STOP") +
                    " | source=voice_client_runtime" +
                    " | visualOnly=True");
            }
        }

        private static void ApplySessionSpinnerVisibility(
            RectTransform spinner, bool visible)
        {
            if (spinner == null)
                return;

            if (spinner.gameObject.activeSelf != visible)
                spinner.gameObject.SetActive(visible);

            if (!visible)
                spinner.localRotation = Quaternion.identity;
        }

        private void AnimateSessionSpinners()
        {
            if (!sessionSpinnerVisible)
                return;

            float angle = SessionSpinnerSpeedDegrees * Time.unscaledDeltaTime;
            if (microphoneSessionSpinner != null)
                microphoneSessionSpinner.Rotate(0f, 0f, -angle);
            if (speakerSessionSpinner != null)
                speakerSessionSpinner.Rotate(0f, 0f, -angle);
        }

        private static RectTransform CreateSessionSpinner(
            Button button, string spinnerName)
        {
            if (button == null)
                return null;

            Transform existing = button.transform.Find(spinnerName);
            if (existing != null)
                return existing as RectTransform;

            GameObject root = new GameObject(
                spinnerName, typeof(RectTransform));
            root.layer = button.gameObject.layer;

            RectTransform spinner = root.GetComponent<RectTransform>();
            spinner.SetParent(button.transform, false);
            spinner.anchorMin = new Vector2(0.5f, 0.5f);
            spinner.anchorMax = new Vector2(0.5f, 0.5f);
            spinner.pivot = new Vector2(0.5f, 0.5f);
            spinner.anchoredPosition = Vector2.zero;
            spinner.sizeDelta = Vector2.zero;
            spinner.localScale = Vector3.one;

            for (int i = 0; i < SessionSpinnerSegments; i++)
            {
                GameObject segment = new GameObject(
                    "Segment_" + i,
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Image));
                segment.layer = button.gameObject.layer;

                RectTransform rect = segment.GetComponent<RectTransform>();
                rect.SetParent(spinner, false);
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(4.5f, 11f);

                float degrees = i * 360f / SessionSpinnerSegments;
                float radians = degrees * Mathf.Deg2Rad;
                rect.anchoredPosition = new Vector2(
                    Mathf.Sin(radians) * SessionSpinnerRadius,
                    Mathf.Cos(radians) * SessionSpinnerRadius);
                rect.localRotation = Quaternion.Euler(0f, 0f, -degrees);

                Image image = segment.GetComponent<Image>();
                image.raycastTarget = false;
                image.color = new Color(
                    1f, 0.66f, 0.19f,
                    0.22f + 0.73f * (i + 1f) / SessionSpinnerSegments);
            }

            Debug.Log("VOICE_SESSION_UI_SPINNER=READY" +
                " | target=" + button.name +
                " | visualOnly=True");
            return spinner;
        }

        private static RectTransform CreateSessionStatusBadge(
            Button button, string badgeName)
        {
            if (button == null)
                return null;

            Transform existing = button.transform.Find(badgeName);
            if (existing != null)
                return existing as RectTransform;

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
            {
                Debug.LogWarning("VOICE_SESSION_UI_BADGE_FONT=UNAVAILABLE");
                return null;
            }

            GameObject badgeObject = new GameObject(
                badgeName, typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Image));
            badgeObject.layer = button.gameObject.layer;

            RectTransform badge = badgeObject.GetComponent<RectTransform>();
            badge.SetParent(button.transform, false);
            badge.anchorMin = new Vector2(0.5f, 0.5f);
            badge.anchorMax = new Vector2(0.5f, 0.5f);
            badge.pivot = new Vector2(0.5f, 0.5f);
            badge.anchoredPosition = new Vector2(38f, 38f);
            badge.sizeDelta = new Vector2(26f, 26f);
            badge.localScale = Vector3.one;

            Image background = badgeObject.GetComponent<Image>();
            background.raycastTarget = false;
            background.color = new Color(0.22f, 0.14f, 0.06f, 0.94f);

            GameObject markObject = new GameObject(
                "Connecting_Mark", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Text));
            markObject.layer = button.gameObject.layer;
            RectTransform markRect = markObject.GetComponent<RectTransform>();
            markRect.SetParent(badge, false);
            markRect.anchorMin = Vector2.zero;
            markRect.anchorMax = Vector2.one;
            markRect.offsetMin = Vector2.zero;
            markRect.offsetMax = Vector2.zero;

            Text mark = markObject.GetComponent<Text>();
            mark.font = font;
            mark.text = "?";
            mark.fontSize = 21;
            mark.fontStyle = FontStyle.Bold;
            mark.alignment = TextAnchor.MiddleCenter;
            mark.color = new Color(1f, 0.85f, 0.45f, 1f);
            mark.raycastTarget = false;
            return badge;
        }
    }
}

/*
توضیح فایل:
این فایل هیچ دکمه‌ای را در زمان اجرا نمی‌سازد.
دکمه‌ها باید به‌صورت دستی داخل صحنه ساخته شوند و سپس از Inspector به این اسکریپت متصل شوند.

این کنترلر دکمه اصلی Microphone و Speaker را به VoiceClientRuntime متصل می‌کند.

وضعیت Microphone دیگر با Text نمایش داده نمی‌شود.
برای نمایش وضعیت Microphone از Sprite استفاده می‌شود:

Mic روشن:
Mic_On

Mic خاموش:
Mic_Off

وضعیت Speaker نیز دیگر با Text نمایش داده نمی‌شود.
برای نمایش وضعیت Speaker از Sprite استفاده می‌شود:

Speaker روشن:
Speaker_On

Speaker خاموش:
Speaker_Off

دکمه اصلی Speaker تنها کنترل عمومی دریافت صدا است.
با خاموش کردن Speaker تمام صداهای ورودی برای کاربر محلی قطع می‌شوند و با روشن کردن آن دوباره دریافت صدا فعال می‌شود.

کنترل جداگانه Mute All در این Controller وجود ندارد.

دکمه و Text مربوط به Recording Consent به‌طور کامل از این Controller حذف شده‌اند.
اجازه ضبط صدای کاربر دیگر از یک دکمه مستقل دریافت نمی‌شود.
وضعیت روشن یا خاموش بودن Microphone منبع رفتار ضبط صدای خود کاربر است.

Txt_Voice_Status فقط دو خط نمایش می‌دهد:

خط اول:
وضعیت اتصال Voice

خط دوم:
تعداد نشست‌های فعال Voice

روشن کردن Microphone فقط با کلیک مستقیم کاربر انجام می‌شود.
در صورت نیاز، ابتدا اجازه دسترسی Microphone از سیستم درخواست می‌شود و فقط پس از تایید، Microphone فعال خواهد شد.
*/