using UnityEngine;
using Mirror;
using UnityEngine.SceneManagement;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_AutoStart")]
    [HelpURL("https://google.com")]
    public class Meta_AutoStart : MonoBehaviour
    {
        [Header("Optional: Delay startup")]
        public float StartupDelay = 0.1f;
        public bool AutoStart;

        [SerializeField] private string WebGLScene = "Base.WebLobby";
        [SerializeField] private string WindowScene = "Base.Lobby";

        private void Start()
        {
            if (AutoStart) Invoke(nameof(StartNetwork), StartupDelay);
            //NetworkManager.singleton.StartHost();
        }

        public void StartNetwork()
        {
#if UNITY_SERVER
            // ---- SERVER BUILD ----
            if (NetworkManager.singleton != null)
            {
                NetworkManager.singleton.StartServer();
                Debug.Log("[Meta_AutoStart] Server started automatically.");
            }

#elif UNITY_WEBGL && !UNITY_EDITOR
            // ---- WEBGL BUILD ----
            if (NetworkManager.singleton != null)
            {
                NetworkManager.singleton.networkAddress = "3ddevelop.irpsc.com/game";
                NetworkManager.singleton.StartClient();
                Debug.Log("[Meta_AutoStart] WebGL client started automatically (address: /game).");
            }

#else
            // ---- DESKTOP / MOBILE CLIENT ----
            if (NetworkManager.singleton != null)
            {
                NetworkManager.singleton.networkAddress = "3ddevelop.irpsc.com";
                NetworkManager.singleton.StartClient();
                //NetworkManager.singleton.StartHost();

                Debug.Log("[Meta_AutoStart] Client started automatically (address: root).");
            }
#endif
        }

        /*       public void GetLobby()
              {
      #if UNITY_WEBGL && !UNITY_EDITOR
                  SceneManager.LoadSceneAsync("Lobby 1 WebGL", LoadSceneMode.Single);
      #else
                  SceneManager.LoadSceneAsync("Lobby 1", LoadSceneMode.Single);
      #endif
              } */

        public void GetLobby()
        {
#if UNITY_WEBGL
            SceneManager.LoadSceneAsync(WebGLScene, LoadSceneMode.Single);
#else
            SceneManager.LoadSceneAsync(WindowScene, LoadSceneMode.Single);
#endif

        }
    }
}