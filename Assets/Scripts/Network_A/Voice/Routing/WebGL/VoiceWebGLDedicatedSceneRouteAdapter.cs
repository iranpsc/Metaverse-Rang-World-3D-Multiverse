using Network_A.DedicatedGameServer.Client;
using Network_A.Realtime.Controllers;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network_A.Voice.Client.Routing.WebGL
{
    [DefaultExecutionOrder(-10000)]
    public sealed class VoiceWebGLDedicatedSceneRouteAdapter : MonoBehaviour
    {
        [SerializeField] private RealtimeRoomGameServerManager realtimeRoomGameServerManager;
        [SerializeField] private DedicatedGameServerWsClient wsClient;
        private bool bound;

        // این تابع پیش از بایندر موجود رفرنس های لازم را می گیرد و رویداد احراز سرور اختصاصی را متصل می کند.
        private void Awake()
        {
            ResolveReferences();
            BindAuthenticatedEvent();
        }

        // این تابع پس از فعال شدن دوباره آبجکت اتصال رویداد را فقط در صورت نیاز برقرار می کند.
        private void OnEnable()
        {
            ResolveReferences();
            BindAuthenticatedEvent();
        }

        // این تابع هنگام غیرفعال شدن آبجکت اتصال رویداد را آزاد می کند.
        private void OnDisable()
        {
            UnbindAuthenticatedEvent();
        }

        // این تابع رفرنس مدیر روم و کلاینت سرور اختصاصی را از همان آبجکت های موجود پیدا می کند.
        private void ResolveReferences()
        {
            if (realtimeRoomGameServerManager == null) realtimeRoomGameServerManager = RealtimeRoomGameServerManager.Instance;
            if (wsClient == null) wsClient = GetComponent<DedicatedGameServerWsClient>();
            if (wsClient == null) wsClient = DedicatedGameServerWsClient.Instance;
        }

        // این تابع رویداد احراز را یک بار و پیش از بایندر موجود ثبت می کند.
        private void BindAuthenticatedEvent()
        {
            if (bound || wsClient == null) return;
            wsClient.Authenticated += HandleDedicatedAuthenticated;
            bound = true;
        }

        // این تابع اتصال رویداد احراز را در پایان عمر آبجکت پاک می کند.
        private void UnbindAuthenticatedEvent()
        {
            if (!bound || wsClient == null) return;
            wsClient.Authenticated -= HandleDedicatedAuthenticated;
            bound = false;
        }

        // این تابع بعد از احراز سرور اختصاصی فقط برای روم ساختمان مقصد عادی یا صوت را پیش از ادامه بایندر موجود درخواست می کند.
        private void HandleDedicatedAuthenticated()
        {
            ResolveReferences();

            if (realtimeRoomGameServerManager == null || !realtimeRoomGameServerManager.IsJoinedRoom)
            {
                Debug.LogError("VOICE_WEBGL_SCENE_ROUTE=FAIL | reason=realtime_room_missing");
                return;
            }

            if (realtimeRoomGameServerManager.IsInsidePublicLobbyRoom)
            {
                Debug.Log("VOICE_WEBGL_SCENE_ROUTE=SKIP | reason=public_lobby");
                return;
            }

            string targetSceneName = VoiceWebGLLobbyRouteSelection.ResolveGameplaySceneName(VoiceWebGLLobbyRouteSelection.NormalGameplaySceneName);
            Scene activeScene = SceneManager.GetActiveScene();

            if (activeScene.IsValid() && activeScene.name == targetSceneName)
            {
                Debug.Log("VOICE_WEBGL_SCENE_ROUTE=READY | scene=" + targetSceneName + " | reason=already_active");
                return;
            }

            if (!Application.CanStreamedLevelBeLoaded(targetSceneName))
            {
                Debug.LogError("VOICE_WEBGL_SCENE_ROUTE=FAIL | reason=scene_not_in_build | scene=" + targetSceneName);
                if (wsClient != null) wsClient.Disconnect("voice_webgl_scene_not_in_build");
                return;
            }

            Debug.Log("VOICE_WEBGL_SCENE_ROUTE=LOAD | scene=" + targetSceneName);
            SceneManager.LoadScene(targetSceneName, LoadSceneMode.Single);
            Debug.Log("VOICE_WEBGL_SCENE_ROUTE=REQUESTED | scene=" + targetSceneName);
        }
    }
}
