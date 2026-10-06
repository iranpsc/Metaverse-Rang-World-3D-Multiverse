#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace Network_A.Voice.Dedicated
{
    public static class VoiceV3CompletePhaseDirectTest
    {
        //* این تابع تمام آزمون‌های مستقیم فاز سوم صوت را با یک فرمان اجرا می‌کند.
        [MenuItem("Tools/Network A/Voice/Run Complete Voice V3 Phase Tests")]
        public static void RunFromEditorMenu()
        {
            try
            {
                VoiceDedicatedProximityEvaluatorDirectTest.RunFromEditorMenu();
                VoiceDedicatedAuthorityPhaseDirectTest.RunCompletePhaseTests();
                VoiceDedicatedStabilityBenchmarkDirectTest.RunFromEditorMenu();

                Debug.Log("VOICE_V3_COMPLETE_DIRECT_TESTS=PASS");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VOICE_V3_COMPLETE_DIRECT_TESTS=FAIL | " + exception);

                throw;
            }
        }
    }
}

/*
توضیح فایل:
این فایل فقط داخل ادیتور یونیتی کامپایل می‌شود و تمام آزمون‌های مستقیم فاصله، هویت، دلتا، وابستگی‌های زمان اجرا و بنچمارک پایداری فاز سوم صوت را با یک فرمان اجرا می‌کند.
*/
#endif
