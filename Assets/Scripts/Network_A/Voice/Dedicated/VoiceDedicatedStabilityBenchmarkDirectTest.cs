#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace Network_A.Voice.Dedicated
{
    public static class VoiceDedicatedStabilityBenchmarkDirectTest
    {
        //* این تابع همه تست‌های مستقل محاسبه بنچمارک پایداری را یکجا اجرا می‌کند.
        [MenuItem("Tools/Network A/Voice/Run Dedicated Stability Benchmark Direct Tests")]
        public static void RunFromEditorMenu()
        {
            try
            {
                TestEnterAndExitTransientDurations();
                TestNoTransientUsesOneServerTick();
                TestOpenExcursionIsNotCompleted();
                TestDistanceStatistics();
                TestManualBenchmarkContract();

                Debug.Log("VOICE_V3_STABILITY_BENCHMARK_TRANSIENT_TRACKER=PASS");
                Debug.Log("VOICE_V3_STABILITY_BENCHMARK_DELAY_SELECTOR=PASS");
                Debug.Log("VOICE_V3_STABILITY_BENCHMARK_STATISTICS=PASS");
                Debug.Log("VOICE_V3_STABILITY_BENCHMARK_SETTINGS=PASS");
                Debug.Log("VOICE_V3_STABILITY_BENCHMARK_DIRECT_TESTS=PASS");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VOICE_V3_STABILITY_BENCHMARK_DIRECT_TESTS=FAIL | " +
                    exception);

                throw;
            }
        }

        //* این تابع مدت دو نوسان برگشتی و انتخاب مقدار بزرگ‌تر همراه با یک تیک را بررسی می‌کند.
        private static void TestEnterAndExitTransientDurations()
        {
            VoiceDedicatedThresholdExcursionTracker enterTracker =
                new VoiceDedicatedThresholdExcursionTracker(true, 3.0f);

            enterTracker.Sample(3.05f, 0);
            enterTracker.Sample(2.99f, 100);
            enterTracker.Sample(2.98f, 150);
            enterTracker.Sample(3.01f, 250);

            VoiceDedicatedThresholdExcursionTracker exitTracker =
                new VoiceDedicatedThresholdExcursionTracker(false, 3.5f);

            exitTracker.Sample(3.45f, 0);
            exitTracker.Sample(3.51f, 300);
            exitTracker.Sample(3.52f, 350);
            exitTracker.Sample(3.49f, 500);

            Require(
                enterTracker.CompletedCount == 1 &&
                enterTracker.MaxCompletedDurationMs == 150,
                "Enter transient duration was not measured correctly.");

            Require(
                exitTracker.CompletedCount == 1 &&
                exitTracker.MaxCompletedDurationMs == 200,
                "Exit transient duration was not measured correctly.");

            long selectedDelay =
                VoiceDedicatedStabilityDelaySelector.SelectDelayMilliseconds(
                    enterTracker.MaxCompletedDurationMs,
                    exitTracker.MaxCompletedDurationMs,
                    50);

            Require(
                selectedDelay == 250,
                "The selected delay did not add one measured server tick.");
        }

        //* این تابع نبود نوسان را به کمترین مقدار قابل مشاهده یعنی یک تیک واقعی تبدیل می‌کند.
        private static void TestNoTransientUsesOneServerTick()
        {
            long selectedDelay =
                VoiceDedicatedStabilityDelaySelector.SelectDelayMilliseconds(
                    0,
                    0,
                    50);

            Require(
                selectedDelay == 50,
                "A no-transient capture did not select one server tick.");
        }

        //* این تابع نوسان باز را تا پیش از برگشت از مرز به‌عنوان نوسان کامل قبول نمی‌کند.
        private static void TestOpenExcursionIsNotCompleted()
        {
            VoiceDedicatedThresholdExcursionTracker tracker =
                new VoiceDedicatedThresholdExcursionTracker(true, 3.0f);

            tracker.Sample(2.99f, 100);
            tracker.Sample(2.98f, 200);

            Require(
                tracker.IsOpen &&
                tracker.CompletedCount == 0 &&
                tracker.MaxCompletedDurationMs == 0,
                "An open threshold excursion was accepted as completed.");
        }

        //* این تابع شمارش، میانگین، کمینه و بیشینه فاصله را بدون نگهداری همه نمونه‌ها بررسی می‌کند.
        private static void TestDistanceStatistics()
        {
            VoiceDedicatedDistanceStatistics statistics =
                new VoiceDedicatedDistanceStatistics();

            statistics.Add(3.0f);
            statistics.Add(3.1f);
            statistics.Add(3.2f);

            Require(
                statistics.Count == 3 &&
                Math.Abs(statistics.Mean - 3.1) < 0.000001 &&
                Math.Abs(statistics.Minimum - 3.0f) < 0.000001f &&
                Math.Abs(statistics.Maximum - 3.2f) < 0.000001f,
                "Distance statistics were not calculated correctly.");
        }

        //* این تابع دستی‌بودن ابزار بنچمارک و قابل‌افزودن‌بودن آن به صحنه را بررسی می‌کند.
        private static void TestManualBenchmarkContract()
        {
            Require(
                typeof(MonoBehaviour).IsAssignableFrom(
                    typeof(VoiceDedicatedStabilityBenchmark)),
                "The dedicated stability benchmark is not a Unity component.");

            Require(
                typeof(VoiceDedicatedStabilityBenchmark)
                    .GetMethod("Configure") != null,
                "The dedicated stability benchmark does not expose Configure.");
        }

        //* این تابع شرط تست را بررسی و در صورت شکست خطای دقیق تولید می‌کند.
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}

/*
توضیح فایل:
این فایل فقط داخل ادیتور یونیتی کامپایل می‌شود و اندازه‌گیری نوسان برگشتی، محاسبه زمان پایداری، آمار فاصله و دستی‌بودن ابزار بنچمارک را بدون بیلد بررسی می‌کند.
*/
#endif

