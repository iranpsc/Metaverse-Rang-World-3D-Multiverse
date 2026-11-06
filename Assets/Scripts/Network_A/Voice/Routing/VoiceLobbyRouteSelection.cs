using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network_A.Voice.Client.Routing
{
    public static class VoiceLobbyRouteSelection
    {
        public const string LobbySceneName = "Lobby 1";
        public const string NormalGameplaySceneName = "Grpc_Enviroment";
        public const string VoiceGameplaySceneName = "Grpc_Enviroment_Voice";

        private static bool voiceModeSelected;

        public static bool IsVoiceModeSelected => voiceModeSelected;

        //* این تابع پیش از بارگذاری نخستین صحنه، حالت ورود را عادی می‌کند و تغییر صحنه‌ها را زیر نظر می‌گیرد.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeBeforeFirstScene()
        {
            voiceModeSelected = false;
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        //* این تابع انتخاب کاربر را برای ورود به محیط سه‌بعدی صوت ثبت می‌کند.
        public static void SelectVoiceMode()
        {
            voiceModeSelected = true;
            Debug.Log("VOICE_LOBBY_ROUTE_SELECTED=VOICE | targetScene=" + VoiceGameplaySceneName);
        }

        //* این تابع انتخاب کاربر را به مسیر عادی محیط سه‌بعدی بازمی‌گرداند.
        public static void SelectNormalMode()
        {
            voiceModeSelected = false;
            Debug.Log("VOICE_LOBBY_ROUTE_SELECTED=NORMAL | targetScene=" + NormalGameplaySceneName);
        }

        //* این تابع مقصد نهایی محیط سه‌بعدی را بدون تغییر مسیر روم، تیکت یا احراز سرور اختصاصی تعیین می‌کند.
        public static string ResolveGameplaySceneName(string normalGameplaySceneName)
        {
            string normalScene = string.IsNullOrWhiteSpace(normalGameplaySceneName)
                ? NormalGameplaySceneName
                : normalGameplaySceneName.Trim();
            string resolvedScene = voiceModeSelected ? VoiceGameplaySceneName : normalScene;

            Debug.Log(
                "VOICE_LOBBY_ROUTE_RESOLVED=" + (voiceModeSelected ? "VOICE" : "NORMAL") +
                " | targetScene=" + resolvedScene);

            return resolvedScene;
        }

        //* این تابع هنگام بازگشت واقعی به لابی، انتخاب قبلی صوت را پاک می‌کند تا ورود بعدی به‌صورت پیش‌فرض عادی باشد.
        private static void HandleSceneLoaded(Scene scene, LoadSceneMode loadMode)
        {
            if (!scene.IsValid() ||
                !string.Equals(scene.name, LobbySceneName, StringComparison.Ordinal)) return;

            voiceModeSelected = false;
            Debug.Log("VOICE_LOBBY_ROUTE_RESET=NORMAL | scene=" + scene.name);
        }
    }
}

/*
توضیح فایل:
این فایل فقط انتخاب مسیر عادی یا صوت را نگه می‌دارد و مقصد صحنه را پس از احراز سرور اختصاصی تعیین می‌کند؛ مسیر ورود روم، تیکت و اتصال شبکه را تغییر نمی‌دهد.
*/
