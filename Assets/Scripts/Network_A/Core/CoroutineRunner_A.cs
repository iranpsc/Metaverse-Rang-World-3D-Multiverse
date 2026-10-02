using System.Collections;
using UnityEngine;

namespace Network_A.Core
{
    public sealed class CoroutineRunner_A : MonoBehaviour
    {
        private static CoroutineRunner_A _instance;

        //* نمونه موجود را ثبت می‌کند، نمونه تکراری را حذف می‌کند
        //* و نمونه اصلی را هنگام اجرای برنامه بین صحنه‌ها نگه می‌دارد.
        private void Awake()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        //* روال دریافت‌شده را فقط هنگام اجرای برنامه آغاز می‌کند.
        public static Coroutine Run(IEnumerator routine)
        {
            if (routine == null || !EnsureInstance())
            {
                return null;
            }

            return _instance.StartCoroutine(routine);
        }

        //* روال مشخص‌شده را در صورت وجود متوقف می‌کند.
        public static void Stop(Coroutine routine)
        {
            if (_instance == null || routine == null)
            {
                return;
            }

            _instance.StopCoroutine(routine);
        }

        //* تمام روال‌های در حال اجرا را متوقف می‌کند.
        public static void StopAll()
        {
            if (_instance == null)
            {
                return;
            }

            _instance.StopAllCoroutines();
        }

        //* ابتدا نمونه‌ای را که در صحنه قرار داده شده پیدا می‌کند.
        //* فقط اگر نمونه‌ای وجود نداشته باشد و برنامه در حال اجرا باشد،
        //* یک نمونه کمکی جدید می‌سازد.
        private static bool EnsureInstance()
        {
            if (!Application.isPlaying)
            {
                return false;
            }

            if (_instance != null)
            {
                return true;
            }

            CoroutineRunner_A existingRunner =
                FindFirstObjectByType<CoroutineRunner_A>();

            if (existingRunner != null)
            {
                _instance = existingRunner;
                return true;
            }

            GameObject runnerObject =
                new GameObject("Network_A_CoroutineRunner");

            runnerObject.AddComponent<CoroutineRunner_A>();

            return _instance != null;
        }
    }
}
