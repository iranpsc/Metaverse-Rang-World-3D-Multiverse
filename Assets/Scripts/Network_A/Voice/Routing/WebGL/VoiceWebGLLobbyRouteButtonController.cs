using UnityEngine;
using UnityEngine.UI;

namespace Network_A.Voice.Client.Routing.WebGL
{
    public sealed class VoiceWebGLLobbyRouteButtonController : MonoBehaviour
    {
        [SerializeField] private Button voiceButton;

        //* این تابع هنگام فعال شدن رابط لابی دکمه صوت را به انتخاب مسیر صوت مرورگر متصل می کند.
        private void OnEnable()
        {
            if (voiceButton == null)
            {
                Debug.LogError("VOICE_WEBGL_LOBBY_ROUTE_BUTTON=FAIL | reason=voice_button_missing");
                enabled = false;
                return;
            }

            voiceButton.onClick.RemoveListener(Btn_SelectVoiceMode);
            voiceButton.onClick.AddListener(Btn_SelectVoiceMode);
            Debug.Log("VOICE_WEBGL_LOBBY_ROUTE_BUTTON=READY | button=" + voiceButton.name);
        }

        //* این تابع هنگام غیرفعال شدن رابط لابی اتصال رویداد دکمه صوت را آزاد می کند.
        private void OnDisable()
        {
            if (voiceButton != null) voiceButton.onClick.RemoveListener(Btn_SelectVoiceMode);
        }

        //* این تابع با کلیک دکمه مقصد ورود بعدی را صحنه سه بعدی صوت مرورگر قرار می دهد.
        public void Btn_SelectVoiceMode()
        {
            VoiceWebGLLobbyRouteSelection.SelectVoiceMode();
        }
    }
}
