using UnityEngine;
using static Meta.Meta_PlatformDetector;

namespace Meta
{
    [AddComponentMenu("Meta/JoyStick Activator")]
    [HelpURL("https://google.com")]
    public class Meta_JoyStickActivator : MonoBehaviour
    {

        [Header("References")]
        public GameObject JoyStick;

        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        public void Awake()
        {
            var _Platform = Meta_PlatformDetector.GetPlatformType();

            if (_Platform == PlatformType.WebGL_Windows)
                JoyStick.SetActive(false);

            else if (_Platform == PlatformType.WebGL_Android)
                JoyStick.SetActive(true);

            else
            {
                JoyStick.SetActive(false);
            }
        }
    }
}