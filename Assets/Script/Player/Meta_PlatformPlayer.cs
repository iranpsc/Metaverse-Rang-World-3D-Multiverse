using System.Collections;
using UnityEngine;
using UnityEngine.XR.Management;

namespace Meta
{
    [AddComponentMenu("Meta/Meta PlatformPlayer")]
    public class Meta_PlatformPlayer : MonoBehaviour
    {
        [Header("References")]
        public GameObject[] VRComponent;
        public GameObject[] PC_AndroidComponent;

        private void Awake()
        {
#if UNITY_WEBGL
            DisableVR();
            Debug.Log("[Platform] WebGL detected (XR disabled)");
            return;
#endif

            if (ShouldUseXR())
            {
                DisablePC();
                StartCoroutine(StartXR());
            }
            else
            {
                DisableVR();
            }

            Debug.Log($"[Platform] {Application.platform}, XR Requested: {ShouldUseXR()}");
        }

        private bool ShouldUseXR()
        {
#if UNITY_ANDROID
            // Quest / XR-only Android devices
            return UnityEngine.XR.XRSettings.isDeviceActive;
#elif UNITY_STANDALONE_WIN
            // Allow VR on Windows (user may have headset)
            return true;
#else
            return false;
#endif
        }

        private IEnumerator StartXR()
        {
            var _manager = XRGeneralSettings.Instance.Manager;

            if (_manager.isInitializationComplete)
                yield break;

            yield return _manager.InitializeLoader();

            if (_manager.activeLoader == null)
            {
                Debug.LogWarning("[XR] No runtime found, running non-VR");
                DisableVR();
                yield break;
            }

            _manager.StartSubsystems();
            Debug.Log("[XR] XR started successfully");
        }

        private void DisableVR()
        {
            foreach (var _obj in VRComponent)
                if (_obj) _obj.SetActive(false);
        }

        private void DisablePC()
        {
            foreach (var _obj in PC_AndroidComponent)
                if (_obj) _obj.SetActive(false);
        }
    }
}
