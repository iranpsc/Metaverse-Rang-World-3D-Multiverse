#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
using UnityEngine;

namespace Network_A.UI
{
    public static class WebGLInputSelectionCompatibility
    {
        [DllImport("__Internal")]
        private static extern int WebGLInputSelectionInstall();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            int installed = WebGLInputSelectionInstall();
            Debug.Log(
                "WEBGL_INPUT_SELECTION_COMPATIBILITY=" +
                (installed == 1 ? "READY" : "UNAVAILABLE") +
                " | unsupportedInputSelectionGuard=True");
        }
    }
}
#endif
