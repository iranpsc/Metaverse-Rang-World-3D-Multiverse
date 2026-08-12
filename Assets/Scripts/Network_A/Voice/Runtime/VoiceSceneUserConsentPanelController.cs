using System.Collections;
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
        [SerializeField] private RTLTextMeshPro microphoneButtonText;

        [Header("Recording Consent")]
        [SerializeField] private Button recordingConsentButton;
        [SerializeField] private RTLTextMeshPro recordingConsentButtonText;

        [Header("Speaker")]
        [SerializeField] private Button speakerButton;
        [SerializeField] private RTLTextMeshPro speakerButtonText;

        [Header("Mute All")]
        [SerializeField] private Button muteAllButton;
        [SerializeField] private RTLTextMeshPro muteAllButtonText;

        private VoiceClientRuntime runtime;
        private bool microphonePermissionRequestRunning;
        private bool recordingConsentWanted;
        private bool speakerOff;
        private bool muteAllIncoming;
        private int lastConsentAppliedSessionCount = -1;

        //* این تابع هنگام فعال شدن پنل، دکمه‌های دستی صحنه را به کنترل‌های صوت وصل می‌کند.
        private void OnEnable()
        {
            TryResolveRuntime();

            if (microphoneButton != null)
            {
                microphoneButton.onClick.RemoveListener(HandleMicrophoneButtonClicked);
                microphoneButton.onClick.AddListener(HandleMicrophoneButtonClicked);
            }

            if (recordingConsentButton != null)
            {
                recordingConsentButton.onClick.RemoveListener(HandleRecordingConsentButtonClicked);
                recordingConsentButton.onClick.AddListener(HandleRecordingConsentButtonClicked);
            }

            if (speakerButton != null)
            {
                speakerButton.onClick.RemoveListener(HandleSpeakerButtonClicked);
                speakerButton.onClick.AddListener(HandleSpeakerButtonClicked);
            }

            if (muteAllButton != null)
            {
                muteAllButton.onClick.RemoveListener(HandleMuteAllButtonClicked);
                muteAllButton.onClick.AddListener(HandleMuteAllButtonClicked);
            }

            Debug.Log("VOICE_V6_SCENE_USER_CONSENT_PANEL=READY");
            UpdateUi();
        }

        //* این تابع هنگام غیرفعال شدن پنل، اتصال دکمه‌ها را آزاد می‌کند.
        private void OnDisable()
        {
            if (microphoneButton != null)
            {
                microphoneButton.onClick.RemoveListener(HandleMicrophoneButtonClicked);
            }

            if (recordingConsentButton != null)
            {
                recordingConsentButton.onClick.RemoveListener(HandleRecordingConsentButtonClicked);
            }

            if (speakerButton != null)
            {
                speakerButton.onClick.RemoveListener(HandleSpeakerButtonClicked);
            }

            if (muteAllButton != null)
            {
                muteAllButton.onClick.RemoveListener(HandleMuteAllButtonClicked);
            }
        }

        //* این تابع وضعیت Runtime را پیدا می‌کند، رضایت ضبط را برای نشست‌های تازه اعمال می‌کند و نوشته‌های پنل را به‌روز نگه می‌دارد.
        private void Update()
        {
            TryResolveRuntime();

            if (
                runtime != null &&
                runtime.IsAuthenticated &&
                recordingConsentWanted &&
                runtime.ActiveSessionCount > 0 &&
                runtime.ActiveSessionCount != lastConsentAppliedSessionCount
            )
            {
                runtime.SetRecordingConsentForAll(true);
                lastConsentAppliedSessionCount = runtime.ActiveSessionCount;

                Debug.Log(
                    "VOICE_V6_RECORDING_CONSENT_USER_APPLIED=PASS" +
                    " | sessionCount=" + runtime.ActiveSessionCount);
            }

            UpdateUi();
        }

        //* این تابع Runtime صوت را از ریشه ساخته‌شده توسط مسیر Voice پیدا می‌کند.
        private void TryResolveRuntime()
        {
            if (runtime != null) return;

            GameObject root = GameObject.Find(RuntimeRootName);

            if (root == null) return;

            runtime = root.GetComponent<VoiceClientRuntime>();
        }

        //* این تابع فقط با کلیک مستقیم کاربر اجازه میکروفن را می‌گیرد و سپس میکروفن را روشن می‌کند.
        private void HandleMicrophoneButtonClicked()
        {
            if (runtime == null || !runtime.IsAuthenticated)
            {
                Debug.LogWarning("VOICE_V6_MIC_USER_ACTION=FAIL | reason=voice_not_authenticated");
                UpdateUi();
                return;
            }

            if (!runtime.IsMicrophoneMuted)
            {
                runtime.SetMicrophoneMuted(true);

                Debug.Log("VOICE_V6_MIC_USER_DISABLED=PASS");

                UpdateUi();
                return;
            }

            if (microphonePermissionRequestRunning) return;

            StartCoroutine(RequestMicrophonePermissionAndEnable());
            UpdateUi();
        }

        //* این تابع درخواست اجازه میکروفن را از سیستم می‌گیرد و فقط در صورت تأیید کاربر، دریافت صدا را فعال می‌کند.
        private IEnumerator RequestMicrophonePermissionAndEnable()
        {
            microphonePermissionRequestRunning = true;

            Debug.Log("VOICE_V6_MIC_PERMISSION_REQUEST=START");

            AsyncOperation request =
                Application.RequestUserAuthorization(UserAuthorization.Microphone);

            yield return request;

            microphonePermissionRequestRunning = false;

            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                runtime.SetMicrophoneMuted(true);

                Debug.LogWarning(
                    "VOICE_V6_MIC_PERMISSION=FAIL" +
                    " | reason=user_denied_or_platform_denied");

                UpdateUi();
                yield break;
            }

            runtime.SetMicrophoneMuted(false);

            Debug.Log("VOICE_V6_MIC_PERMISSION=PASS");
            Debug.Log("VOICE_V6_MIC_USER_ENABLED=PASS");

            UpdateUi();
        }

        //* این تابع رضایت ضبط را فقط با انتخاب مستقیم کاربر تغییر می‌دهد و به نشست‌های فعال می‌فرستد.
        private void HandleRecordingConsentButtonClicked()
        {
            if (runtime == null || !runtime.IsAuthenticated)
            {
                Debug.LogWarning(
                    "VOICE_V6_RECORDING_CONSENT_USER_ACTION=FAIL" +
                    " | reason=voice_not_authenticated");

                UpdateUi();
                return;
            }

            recordingConsentWanted = !recordingConsentWanted;
            lastConsentAppliedSessionCount = -1;

            runtime.SetRecordingConsentForAll(recordingConsentWanted);

            Debug.Log(
                "VOICE_V6_RECORDING_CONSENT_USER_SELECTED=PASS" +
                " | consented=" + recordingConsentWanted +
                " | sessionCount=" + runtime.ActiveSessionCount);

            UpdateUi();
        }

        //* این تابع بلندگو را برای کاربر خاموش یا روشن می‌کند.
        private void HandleSpeakerButtonClicked()
        {
            if (runtime == null || !runtime.IsAuthenticated)
            {
                UpdateUi();
                return;
            }

            speakerOff = !speakerOff;
            runtime.SetSpeakerOff(speakerOff);

            Debug.Log(
                "VOICE_V6_SPEAKER_USER_SELECTED=PASS" +
                " | speakerOff=" + speakerOff);

            UpdateUi();
        }

        //* این تابع دریافت همه صداهای ورودی را قطع یا وصل می‌کند.
        private void HandleMuteAllButtonClicked()
        {
            if (runtime == null || !runtime.IsAuthenticated)
            {
                UpdateUi();
                return;
            }

            muteAllIncoming = !muteAllIncoming;
            runtime.SetMuteAllIncoming(muteAllIncoming);

            Debug.Log(
                "VOICE_V6_MUTE_ALL_USER_SELECTED=PASS" +
                " | muteAll=" + muteAllIncoming);

            UpdateUi();
        }

        //* این تابع متن‌ها و فعال بودن دکمه‌ها را بر اساس وضعیت واقعی Runtime به‌روزرسانی می‌کند.
        private void UpdateUi()
        {
            bool runtimeReady = runtime != null;
            bool authenticated = runtimeReady && runtime.IsAuthenticated;
            bool micMuted = !runtimeReady || runtime.IsMicrophoneMuted;
            int sessionCount = runtimeReady ? runtime.ActiveSessionCount : 0;

            if (statusText != null)
            {
                statusText.text =
                    "وضعیت صدا: " + (authenticated ? "وصل" : "در انتظار اتصال") + "\n" +
                    "نشست فعال: " + sessionCount + "\n" +
                    "میکروفن: " + (micMuted ? "خاموش" : "روشن") + "\n" +
                    "رضایت ضبط: " + (recordingConsentWanted ? "داده شده" : "داده نشده") + "\n" +
                    "ضبط فقط بعد از رضایت کاربر فعال می‌شود.";
            }

            if (microphoneButtonText != null)
            {
                if (microphonePermissionRequestRunning)
                {
                    microphoneButtonText.text = "در حال گرفتن اجازه میکروفن...";
                }
                else
                {
                    microphoneButtonText.text =
                        micMuted ? "روشن کردن میکروفن" : "خاموش کردن میکروفن";
                }
            }

            if (recordingConsentButtonText != null)
            {
                recordingConsentButtonText.text =
                    recordingConsentWanted ? "لغو رضایت ضبط" : "اجازه ضبط این مکالمه";
            }

            if (speakerButtonText != null)
            {
                speakerButtonText.text =
                    speakerOff ? "روشن کردن بلندگو" : "خاموش کردن بلندگو";
            }

            if (muteAllButtonText != null)
            {
                muteAllButtonText.text =
                    muteAllIncoming ? "شنیدن همه" : "قطع صدای همه";
            }

            if (microphoneButton != null)
            {
                microphoneButton.interactable =
                    authenticated && !microphonePermissionRequestRunning;
            }

            if (recordingConsentButton != null)
            {
                recordingConsentButton.interactable = authenticated;
            }

            if (speakerButton != null)
            {
                speakerButton.interactable = authenticated;
            }

            if (muteAllButton != null)
            {
                muteAllButton.interactable = authenticated;
            }
        }
    }
}

/*
توضیح فایل:
این فایل هیچ دکمه‌ای را در زمان اجرا نمی‌سازد. دکمه‌ها و نوشته‌ها باید به‌صورت دستی داخل صحنه ساخته شوند و سپس از بازرس یونیتی به این اسکریپت وصل شوند. نوشته‌های این پنل با آر تی ال تی ام پرو کار می‌کنند تا متن فارسی راست‌به‌چپ درست نمایش داده شود. این کنترلر فقط دکمه‌های دستی صحنه را به Runtime صوت متصل می‌کند. روشن کردن میکروفن فقط با کلیک کاربر و پس از دریافت اجازه میکروفن انجام می‌شود. رضایت ضبط نیز فقط با انتخاب مستقیم کاربر برای نشست‌های فعال ارسال می‌شود.
*/