using UnityEngine;
using Mirror;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_AutoStart")]
    [HelpURL("https://google.com")]
    public class Meta_AutoStart : MonoBehaviour
    {
        [Header("Optional: Delay startup")]
        public float StartupDelay = 0.1f;
        public bool AutoStart;

        private void Start()
        {
            if (AutoStart) Invoke(nameof(StartNetwork), StartupDelay);
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

#elif UNITY_WEBGL
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

                Debug.Log("[Meta_AutoStart] Client started automatically (address: root).");
            }
#endif
        }
    }
}
