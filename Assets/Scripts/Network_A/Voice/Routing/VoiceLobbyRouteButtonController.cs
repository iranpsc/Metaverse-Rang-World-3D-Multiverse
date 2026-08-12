using UnityEngine;
using UnityEngine.UI;

namespace Network_A.Voice.Client.Routing
{
    public sealed class VoiceLobbyRouteButtonController : MonoBehaviour
    {
        [Header("Voice Route UI")]
        [SerializeField] private Button voiceButton;

        //* این تابع هنگام فعال‌شدن رابط لابی، دکمه صوت را به انتخاب مسیر صوت متصل می‌کند.
        private void OnEnable()
        {
            if (voiceButton == null)
            {
                Debug.LogError("VOICE_LOBBY_ROUTE_BUTTON=FAIL | reason=voice_button_missing");
                enabled = false;
                return;
            }

            voiceButton.onClick.RemoveListener(Btn_SelectVoiceMode);
            voiceButton.onClick.AddListener(Btn_SelectVoiceMode);
            Debug.Log("VOICE_LOBBY_ROUTE_BUTTON=READY | button=" + voiceButton.name);
        }

        //* این تابع هنگام غیرفعال‌شدن رابط لابی، اتصال رویداد دکمه صوت را آزاد می‌کند.
        private void OnDisable()
        {
            if (voiceButton != null) voiceButton.onClick.RemoveListener(Btn_SelectVoiceMode);
        }

        //* این تابع عمومی با کلیک دکمه، مقصد ورود بعدی را صحنه سه‌بعدی صوت قرار می‌دهد.
        public void Btn_SelectVoiceMode()
        {
            VoiceLobbyRouteSelection.SelectVoiceMode();
        }
    }
}

/*
توضیح فایل:
این فایل فقط دکمه لابی را به انتخاب مسیر صوت متصل می‌کند و هیچ ورود مستقیم به صحنه، روم یا سرور اختصاصی انجام نمی‌دهد.
*/
