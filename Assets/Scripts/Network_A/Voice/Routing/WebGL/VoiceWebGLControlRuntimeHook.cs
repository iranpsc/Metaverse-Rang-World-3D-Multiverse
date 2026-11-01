#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using Network_A.Voice.Client.MediaV2.WebGL;
using Network_A.Voice.Client.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network_A.Voice.Client.Routing.WebGL
{
    public static class VoiceWebGLControlConnectorBootstrap
    {
        private const string RootName = "Voice_Client_Runtime_Root";

        // این تابع پیش از نخستین صحنه رویداد بارگذاری را فقط در بیلد مرورگر ثبت می کند.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        // این تابع در صحنه صوت مرورگر اتصال خودکار قدیمی را غیرفعال و آزمون مستقل فاز نه را آماده می کند.
        private static void HandleSceneLoaded(Scene scene, LoadSceneMode loadMode)
        {
            if (!scene.IsValid()) return;
            if (!VoiceWebGLLobbyRouteSelection.IsVoiceModeSelected) return;
            if (!string.Equals(scene.name, VoiceWebGLLobbyRouteSelection.VoiceGameplaySceneName, StringComparison.Ordinal)) return;

            GameObject root = GameObject.Find(RootName);
            if (root == null) root = new GameObject(RootName);
            UnityEngine.Object.DontDestroyOnLoad(root);

            VoiceClientRuntime runtime = root.GetComponent<VoiceClientRuntime>();
            if (runtime == null) runtime = root.AddComponent<VoiceClientRuntime>();
            runtime.Initialize();

            VoiceClientAutoConnector legacyConnector = root.GetComponent<VoiceClientAutoConnector>();
            if (legacyConnector == null) legacyConnector = root.AddComponent<VoiceClientAutoConnector>();
            legacyConnector.enabled = false;

            VoiceWebGLPhase9LiveProbe probe = root.GetComponent<VoiceWebGLPhase9LiveProbe>();
            if (probe == null) probe = root.AddComponent<VoiceWebGLPhase9LiveProbe>();
            probe.BeginIfNeeded();

            Debug.Log("VOICE_WEBGL_PHASE9_BOOTSTRAP=READY | platform=WebGL | legacyConnectorDisabled=True | windowsUntouched=True");
        }
    }
}
#endif
