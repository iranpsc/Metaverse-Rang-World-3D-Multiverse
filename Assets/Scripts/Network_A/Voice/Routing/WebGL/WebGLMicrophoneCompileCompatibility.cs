#if UNITY_WEBGL
using UnityEngine;

namespace Network_A.Voice.Client.Capture
{
    internal static class Microphone
    {
        private static readonly string[] EmptyDevices = new string[0];

        // این ویژگی در وب جی ال فهرست خالی برمی گرداند تا مسیر قدیمی ویندوز وارد ضبط مرورگر نشود.
        public static string[] devices
        {
            get { return EmptyDevices; }
        }

        // این تابع در وب جی ال موقعیت ضبط قدیمی را نامعتبر اعلام می کند زیرا ضبط مرورگر در این فاز فعال نیست.
        public static int GetPosition(string deviceName)
        {
            return -1;
        }

        // این تابع در وب جی ال ضبط قدیمی را آغاز نمی کند و مقدار خالی برمی گرداند.
        public static AudioClip Start(string deviceName, bool loop, int lengthSec, int frequency)
        {
            return null;
        }

        // این تابع در وب جی ال اعلام می کند که مسیر ضبط قدیمی فعال نیست.
        public static bool IsRecording(string deviceName)
        {
            return false;
        }

        // این تابع در وب جی ال برای سازگاری با مسیر توقف قدیمی بدون انجام عملیات باقی می ماند.
        public static void End(string deviceName)
        {
        }
    }
}
#endif
