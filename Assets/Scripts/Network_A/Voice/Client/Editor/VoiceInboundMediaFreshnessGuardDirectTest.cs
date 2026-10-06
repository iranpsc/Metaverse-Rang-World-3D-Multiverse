#if UNITY_EDITOR
using System;
using Network_A.Voice.Client.Runtime;
using UnityEditor;
using UnityEngine;

namespace Network_A.Voice.Client.Editor
{
    public static class VoiceInboundMediaFreshnessGuardDirectTest
    {
        [MenuItem("Tools/Network A/Voice/Run Inbound Media Freshness Guard Test")]
        public static void RunFromEditorMenu()
        {
            try
            {
                VoiceInboundMediaFreshnessGuard guard =
                    new VoiceInboundMediaFreshnessGuard(1000);

                guard.ObserveServerControlTimestamp(1000, 1100);

                ulong freshAgeMs;
                Require(
                    guard.IsFresh(2000, 3000, out freshAgeMs),
                    "A media frame inside the transport age limit was rejected.");
                Require(freshAgeMs == 900, "Fresh media age calculation is invalid.");

                guard.ObserveServerControlTimestamp(2000, 5000);

                ulong staleAgeMs;
                Require(
                    !guard.IsFresh(2000, 3101, out staleAgeMs),
                    "A media frame outside the transport age limit was accepted.");
                Require(staleAgeMs == 1001, "Stale media age calculation is invalid.");

                guard.Reset();
                ulong uncalibratedAgeMs;
                Require(
                    guard.IsFresh(2000, 10000, out uncalibratedAgeMs),
                    "An uncalibrated guard must not reject media.");

                Debug.Log("VOICE_CLIENT_INBOUND_MEDIA_FRESHNESS_GUARD=PASS");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VOICE_CLIENT_INBOUND_MEDIA_FRESHNESS_GUARD=FAIL | " +
                    exception);
                throw;
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}

/*
توضیح فایل:
این فایل محاسبه اختلاف ساعت، پذیرش فریم تازه، رد فریم قدیمی و Reset محافظ رسانه ورودی را در Unity Editor بررسی می‌کند.
*/
#endif
