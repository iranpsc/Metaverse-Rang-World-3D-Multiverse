using UnityEngine;
using UnityEngine.XR;

namespace Meta
{
    [AddComponentMenu("Meta/Platform Detector")]
    [HelpURL("https://google.com")]
    public class Meta_PlatformDetector
    {
        public enum PlatformType
        {
            Windows,
            Android,
            IOS,
            WebGL_Windows,
            WebGL_Android,
            VR,
            Unknow
        }

        public static PlatformType GetPlatformType()
        {
            if (XRSettings.isDeviceActive) return PlatformType.VR;

            switch (Application.platform)
            {
                case RuntimePlatform.WindowsPlayer:
                    return PlatformType.Windows;

                case RuntimePlatform.Android:
                    return PlatformType.Android;

                case RuntimePlatform.IPhonePlayer:
                    return PlatformType.IOS;

                case RuntimePlatform.WebGLPlayer:
                    return DetectSubPlatform();

                default:
                    return PlatformType.Unknow;
            }
        }

        private static PlatformType DetectSubPlatform()
        {
            string _OS = SystemInfo.operatingSystem.ToLower();
            string _DeviceModel = SystemInfo.deviceModel.ToLower();

            if (_OS.Contains("android") || _DeviceModel.Contains("samsung") || _DeviceModel.Contains("pixel") || _DeviceModel.Contains("xiaomi"))
                return PlatformType.WebGL_Android;

            if (_OS.Contains("windows"))
                return PlatformType.WebGL_Windows;

            return PlatformType.Unknow;
        }

        public static bool IsVR()
        {
            return XRSettings.isDeviceActive;
        }

        public static bool IsWebGL()
        {
            return Application.platform == RuntimePlatform.WebGLPlayer;
        }
    }
}