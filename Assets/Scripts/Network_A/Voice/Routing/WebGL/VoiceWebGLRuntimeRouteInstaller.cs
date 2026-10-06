using System;
using System.Threading.Tasks;
using Network_A.Voice.Client.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network_A.Voice.Client.Routing.WebGL
{
    public static class VoiceWebGLRuntimeRouteInstaller
    {
        private const string RootName = "Voice_Client_Runtime_Root";
        private static int lastInstalledSceneHandle = int.MinValue;

        //* این تابع پیش از بارگذاری نخستین صحنه دریافت رویداد صحنه را برای مسیر صوت مرورگر آماده می کند.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeSceneHandling()
        {
            lastInstalledSceneHandle = int.MinValue;
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        //* این تابع پس از بارگذاری صحنه صوت مرورگر یک نوبت صبر می کند تا نصب کننده قبلی بررسی خودش را تمام کند.
        private static async void HandleSceneLoaded(Scene scene, LoadSceneMode loadMode)
        {
            if (!scene.IsValid()) return;
            if (!VoiceWebGLLobbyRouteSelection.IsVoiceModeSelected) return;
            if (!string.Equals(scene.name, VoiceWebGLLobbyRouteSelection.VoiceGameplaySceneName, StringComparison.Ordinal)) return;
            if (scene.handle == lastInstalledSceneHandle) return;

            lastInstalledSceneHandle = scene.handle;
            await Task.Yield();

            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid() || activeScene.handle != scene.handle) return;
            if (!VoiceWebGLLobbyRouteSelection.IsVoiceModeSelected) return;

            InstallRuntime(activeScene);
        }

        //* این تابع ریشه مشترک صوت را با همان کلاس های موجود می سازد و اتصال خودکار را بدون تغییر مسیر سالم قبلی آماده می کند.
        private static void InstallRuntime(Scene scene)
        {
            GameObject root = GameObject.Find(RootName);
            bool rootCreated = root == null;
            if (rootCreated) root = new GameObject(RootName);
            UnityEngine.Object.DontDestroyOnLoad(root);

            VoiceClientRuntime runtime = root.GetComponent<VoiceClientRuntime>();
            bool runtimeCreated = runtime == null;
            if (runtimeCreated) runtime = root.AddComponent<VoiceClientRuntime>();
            runtime.Initialize();

            VoiceClientAutoConnector connector = root.GetComponent<VoiceClientAutoConnector>();
            bool connectorCreated = connector == null;
            if (connectorCreated) connector = root.AddComponent<VoiceClientAutoConnector>();
            connector.Initialize(runtime);

            Debug.Log("VOICE_WEBGL_ROUTE_RUNTIME=READY | scene=" + scene.name + " | rootCreated=" + rootCreated + " | runtimeCreated=" + runtimeCreated + " | connectorCreated=" + connectorCreated);
        }
    }
}
