using UnityEngine;
#if UNITY_XR_MANAGEMENT
using UnityEngine.XR.Management;
#endif

namespace Meta
{
    [AddComponentMenu("Meta/Meta PlatformPlayer")]
    [HelpURL("https://google.com")]
    public class Meta_PlatformPlayer : MonoBehaviour
    {
        [Header("References")]
        public GameObject[] VRComponent;
        public GameObject[] PC_AndroidComponent;

        private void Start()
        {
            bool _isXR = IsXRActive();
            bool _isDesktop = Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor;
            bool _isAndroid = Application.platform == RuntimePlatform.Android;
            bool _isWebGL = Application.platform == RuntimePlatform.WebGLPlayer;

            // If running non-VR version (PC/Android/WebGL)
            if (!_isXR && (_isDesktop || _isAndroid || _isWebGL))
            {
                foreach (GameObject _obj in VRComponent)
                {
                    if (_obj != null)
                        _obj.SetActive(false);
                }
            }

            // If running in VR mode
            if (_isXR)
            {
                foreach (GameObject _obj in PC_AndroidComponent)
                {
                    if (_obj != null)
                        _obj.SetActive(false);
                }
            }

            Debug.Log($"[Meta_PlatformPlayer] Platform: {Application.platform}, XR Active: {_isXR}");
        }

        private bool IsXRActive()
        {
#if UNITY_XR_MANAGEMENT
            var _xrSettings = XRGeneralSettings.Instance;
            if (_xrSettings != null && _xrSettings.Manager != null)
            {
                var _loader = _xrSettings.Manager.activeLoader;
                return _loader != null;
            }
            return false;
#elif ENABLE_VR
            return UnityEngine.XR.XRSettings.isDeviceActive;
#else
            return false;
#endif
        }
    }
}
