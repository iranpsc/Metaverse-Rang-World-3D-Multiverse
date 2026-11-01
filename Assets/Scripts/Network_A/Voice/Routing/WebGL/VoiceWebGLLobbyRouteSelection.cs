using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network_A.Voice.Client.Routing.WebGL
{
    public static class VoiceWebGLLobbyRouteSelection
    {
        public const string LobbySceneName = "Lobby 1 WebGL";
        public const string NormalGameplaySceneName = "WebGL_Enviroment";
        public const string VoiceGameplaySceneName = "WebGL_Enviroment_Voice";

        private static bool voiceModeSelected;

        public static bool IsVoiceModeSelected => voiceModeSelected;

        //* این تابع پیش از بارگذاری نخستین صحنه حالت ورود را عادی می کند و تغییر صحنه ها را زیر نظر می گیرد.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeBeforeFirstScene()
        {
            voiceModeSelected = false;
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        //* این تابع انتخاب کاربر برای ورود به محیط سه بعدی صوت در مرورگر را ثبت می کند.
        public static void SelectVoiceMode()
        {
            voiceModeSelected = true;
            Debug.Log("VOICE_WEBGL_LOBBY_ROUTE_SELECTED=VOICE | targetScene=" + VoiceGameplaySceneName);
        }

        //* این تابع انتخاب کاربر را به مسیر عادی محیط سه بعدی مرورگر باز می گرداند.
        public static void SelectNormalMode()
        {
            voiceModeSelected = false;
            Debug.Log("VOICE_WEBGL_LOBBY_ROUTE_SELECTED=NORMAL | targetScene=" + NormalGameplaySceneName);
        }

        //* این تابع مقصد نهایی محیط سه بعدی مرورگر را بدون تغییر مسیر روم یا تیکت یا احراز سرور اختصاصی تعیین می کند.
        public static string ResolveGameplaySceneName(string normalGameplaySceneName)
        {
            string normalScene = string.IsNullOrWhiteSpace(normalGameplaySceneName) ? NormalGameplaySceneName : normalGameplaySceneName.Trim();
            string resolvedScene = voiceModeSelected ? VoiceGameplaySceneName : normalScene;
            Debug.Log("VOICE_WEBGL_LOBBY_ROUTE_RESOLVED=" + (voiceModeSelected ? "VOICE" : "NORMAL") + " | targetScene=" + resolvedScene);
            return resolvedScene;
        }

        //* این تابع هنگام بازگشت واقعی به لابی مرورگر انتخاب قبلی صوت را پاک می کند تا ورود بعدی به صورت پیش فرض عادی باشد.
        private static void HandleSceneLoaded(Scene scene, LoadSceneMode loadMode)
        {
            if (!scene.IsValid() || !string.Equals(scene.name, LobbySceneName, StringComparison.Ordinal)) return;
            voiceModeSelected = false;
            Debug.Log("VOICE_WEBGL_LOBBY_ROUTE_RESET=NORMAL | scene=" + scene.name);
        }
    }
}
