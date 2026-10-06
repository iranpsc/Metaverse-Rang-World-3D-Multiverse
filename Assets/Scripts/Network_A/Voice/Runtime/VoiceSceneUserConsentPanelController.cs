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
        [SerializeField] private Sprite micOnSprite;
        [SerializeField] private Sprite micOffSprite;

        [Header("Speaker")]
        [SerializeField] private Button speakerButton;
        [SerializeField] private Sprite speakerOnSprite;
        [SerializeField] private Sprite speakerOffSprite;

        private VoiceClientRuntime runtime;
        private bool microphonePermissionRequestRunning;
        private bool speakerOff;

        //* این تابع هنگام فعال شدن پنل، دکمه‌های دستی صحنه را به کنترل‌های صوت وصل می‌کند.
        private void OnEnable()
        {
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

            Debug.Log("VOICE_V6_SCENE_USER_CONSENT_PANEL=READY");

            UpdateUi();
        }

        //* این تابع هنگام غیرفعال شدن پنل، اتصال دکمه‌ها را آزاد می‌کند.
        private void OnDisable()
        {
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
                    "VOICE_V6_MIC_USER_DISABLED=PASS");

                UpdateUi();
                return;
            }

            if (microphonePermissionRequestRunning)
                return;

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

            runtime.SetMicrophoneMuted(false);

            Debug.Log(
                "VOICE_V6_MIC_PERMISSION=PASS");

            Debug.Log(
                "VOICE_V6_MIC_USER_ENABLED=PASS");

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

            if (statusText != null)
            {
                statusText.text =
                    "وضعیت صدا: " +
                    (authenticated
                        ? "وصل"
                        : "در انتظار اتصال") +
                    "\n" +
                    "نشست فعال: " +
                    sessionCount;
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
                microphoneButton.interactable =
                    runtimeReady &&
                    !microphonePermissionRequestRunning;
            }

            if (speakerButton != null)
            {
                speakerButton.interactable =
                    runtimeReady;
            }
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