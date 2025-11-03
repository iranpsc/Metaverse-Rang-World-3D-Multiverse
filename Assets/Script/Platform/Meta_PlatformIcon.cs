using UnityEngine;
using UnityEngine.UI;
using static Meta.Meta_PlatformDetector;

namespace Meta
{
    [AddComponentMenu("Meta/Platform Icon")]
    [HelpURL("https://google.com")]
    public class Meta_PlatformIcon : MonoBehaviour
    {
        [Header("References")]
        public Image Icon;

        [Header("Settings")]
        public Sprite WindowsIcon;
        public Sprite AndroidIcon;
        public Sprite IosIcon;
        public Sprite VRIcon;
        public Sprite WindowsWebGLIcon;
        public Sprite AndroidWebGLIcon;
        public Sprite UnknowIcon;

        [Header("Debugger")]
        [SerializeField] private bool EnableLog;

        void Start()
        {
            var _Platform = Meta_PlatformDetector.GetPlatformType();

            if (_Platform == PlatformType.Windows)
                Icon.sprite = WindowsIcon;

            else if (_Platform == PlatformType.Android)
                Icon.sprite = AndroidIcon;

            else if (_Platform == PlatformType.IOS)
                Icon.sprite = IosIcon;

            else if (_Platform == PlatformType.VR)
                Icon.sprite = VRIcon;

            else if (_Platform == PlatformType.WebGL_Windows)
                Icon.sprite = WindowsWebGLIcon;

            else if (_Platform == PlatformType.WebGL_Android)
                Icon.sprite = AndroidWebGLIcon;

            else if (_Platform == PlatformType.Unknow)
                Icon.sprite = UnknowIcon;
        }
    }
}