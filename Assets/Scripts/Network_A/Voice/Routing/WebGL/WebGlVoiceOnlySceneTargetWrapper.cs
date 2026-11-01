using UnityEngine;

namespace Network_A.Voice.Client.Routing.WebGL
{
    // WebGL-only gameplay scene selection. Dedicated connection, room membership,
    // and actual scene loading remain owned by the existing WebGL Binder.
    // This is called only for building rooms, never the public lobby.
    public static class WebGlVoiceOnlySceneTargetWrapper
    {
        public static string ResolveForBuildingRoom()
        {
            // The voice runtime installer checks this state when the voice scene loads.
            // Set it BEFORE the WebGL Binder starts LoadSceneAsync.
            if (!VoiceWebGLLobbyRouteSelection.IsVoiceModeSelected)
            {
                VoiceWebGLLobbyRouteSelection.SelectVoiceMode();
            }

            string scene = VoiceWebGLLobbyRouteSelection.VoiceGameplaySceneName;
            Debug.Log("VME2_WEBGL_BUILDING_SCENE_TARGET=VOICE | scene=" + scene +
                      " | selected=" + VoiceWebGLLobbyRouteSelection.IsVoiceModeSelected +
                      " | normal_scene_ignored=true");
            return scene;
        }
    }
}
