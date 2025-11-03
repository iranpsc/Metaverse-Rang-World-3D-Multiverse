using UnityEngine;

namespace Meta
{
    [AddComponentMenu("Meta/Meta_OnPlatformEnable")]
    [HelpURL("https://google.com")]
    public class Meta_OnPlatformEnable : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private GameObject[] ObjectToEnable;

        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        void Start()
        {
            bool _IsAndroid = false;
            bool _IsWebGL = Application.platform == RuntimePlatform.WebGLPlayer;
            bool _IsAndroidBrowser = false;

            _IsAndroid = Application.platform == RuntimePlatform.Android;

            if (_IsAndroid || _IsAndroidBrowser)
            {
                foreach (var obj in ObjectToEnable)
                {
                    obj.SetActive(false);
                }

                if (EnableLog)
                    Debug.Log("[Meta_OnPlatformEnable] Disabled objects for Android platform or Android browser.");
            }
            else if (_IsWebGL)
            {
                if (EnableLog)
                    Debug.Log("[Meta_OnPlatformEnable] Running WebGL on Desktop.");
            }
        }
    }
}