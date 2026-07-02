using System.Collections;
using UnityEngine;

namespace Network_A.Core
{
    public sealed class CoroutineRunner_A : MonoBehaviour
    {
        private static CoroutineRunner_A _instance;

        //* Runs a coroutine from non-MonoBehaviour network classes.
        public static Coroutine Run(IEnumerator routine)
        {
            EnsureInstance();
            return _instance.StartCoroutine(routine);
        }

        //* Stops a running coroutine safely.
        public static void Stop(Coroutine routine)
        {
            if (_instance != null && routine != null) _instance.StopCoroutine(routine);
        }

        //* Stops all network helper coroutines.
        public static void StopAll()
        {
            if (_instance != null) _instance.StopAllCoroutines();
        }

        //* Creates the hidden runner object if needed.
        private static void EnsureInstance()
        {
            if (_instance != null) return;
            var obj = new GameObject("Network_A_CoroutineRunner");
            _instance = obj.AddComponent<CoroutineRunner_A>();
            DontDestroyOnLoad(obj);
        }
    }
}
