#if UNITY_EDITOR_WIN
using System;
using Network_A.Voice.Client.WebRTC.Windows;
using UnityEditor;
using UnityEngine;

namespace Network_A.Voice.Client.WebRTC.Windows.Editor
{
    internal static class Vme2WindowsWebRtcPhase1Check
    {
        [MenuItem("VME2/WebRTC Windows/Check Native Engine")]
        private static void CheckNativeEngine()
        {
            try
            {
                var version = Vme2WindowsWebRtcEngine.InitializeAndGetVersion();
                using (var device = new Vme2WindowsWebRtcDevice())
                {
                    if (device.IsLoaded)
                        throw new InvalidOperationException("New Device unexpectedly loaded");
                }
                Vme2WindowsWebRtcEngine.Shutdown();
                Debug.Log("VME2_WINDOWS_WEBRTC_PHASE1=PASS | version=" + version);
            }
            catch (Exception exception)
            {
                Debug.LogError("VME2_WINDOWS_WEBRTC_PHASE1=FAIL | " + exception);
            }
        }
    }
}
#endif
