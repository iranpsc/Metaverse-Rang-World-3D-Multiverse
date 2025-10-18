using UnityEngine;
using System.Runtime.InteropServices;

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

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern int DetectMobilePlatform();
#endif

        void Start()
        {
            bool _IsAndroid = false;
            bool _IsWebGL = Application.platform == RuntimePlatform.WebGLPlayer;
            bool _IsAndroidBrowser = false;

#if UNITY_WEBGL && !UNITY_EDITOR
            // Detect if WebGL was opened on Android
            _IsAndroidBrowser = DetectMobilePlatform() == 1;
#endif

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