using System;
using Network_A.Voice.Client.Audio;
using UnityEngine;

namespace Network_A.Voice.Client.Capture.Vme2
{
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    internal sealed class VoiceAudioClockProbe : MonoBehaviour
    {
        private const string ProbeArgument = "--vme2-phase4-clock-test";
        private const uint AudclntBufferflagsDataDiscontinuity = 0x00000001;
        private const uint AudclntBufferflagsTimestampError = 0x00000004;
        private const double SyntheticDriftTolerancePpm = 2.0;
        private readonly object sync = new object();
        private WindowsWasapiNativeCapture capture;
        private VoiceAudioClockTracker nativeClock;
        private WindowsWasapiNativeFormat nativeFormat;
        private VoiceAudioClockObservation latestObservation;
        private bool nativeFormatReady;
        private string pendingFailure = string.Empty;
        private float nextMetricTime;

        // این تابع در شروع برنامه فقط در صورت وجود آرگومان مخصوص Phase 4 آزمایشگر مستقل ساعت صوتی را ایجاد می کند.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!HasProbeArgument()) return;
            GameObject root = new GameObject("VME2_Phase4_AudioClock_Probe");
            DontDestroyOnLoad(root);
            root.AddComponent<VoiceAudioClockProbe>();
        }

        // این تابع آرگومان های خط فرمان را بررسی می کند و اجازه اجرای Probe را فقط با شرط صریح Phase 4 می دهد.
        private static bool HasProbeArgument()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int index = 0; index < args.Length; index++) if (string.Equals(args[index], ProbeArgument, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // این تابع تست های مصنوعی را اجرا می کند و سپس WASAPI مستقل Phase 2 را فقط برای دریافت metadata ساعت سخت افزاری باز می کند.
        private void Awake()
        {
            Debug.Log("VME2_PHASE4_CLOCK_PROBE=START | feedsProductionVoice=False");
            RunSyntheticTests();
            capture = new WindowsWasapiNativeCapture();
            capture.FormatReady += HandleFormatReady;
            capture.PacketCaptured += HandlePacketCaptured;
            capture.Failed += HandleFailure;
            capture.Start();
            nextMetricTime = Time.unscaledTime + 1f;
        }

        // این تابع هر ثانیه آخرین اندازه گیری معتبر Native را روی نخ اصلی یونیتی ثبت می کند و هیچ داده ای به Voice اصلی نمی فرستد.
        private void Update()
        {
            FlushFailure();
            if (Time.unscaledTime < nextMetricTime) return;
            nextMetricTime = Time.unscaledTime + 1f;
            WriteNativeMetric();
        }

        // این تابع هنگام نابودی Probe همه رویدادها و Capture مستقل را تمیز متوقف می کند.
        private void OnDestroy()
        {
            if (capture == null) return;
            capture.FormatReady -= HandleFormatReady;
            capture.PacketCaptured -= HandlePacketCaptured;
            capture.Failed -= HandleFailure;
            capture.Stop();
            capture.Dispose();
            capture = null;
        }

        // این تابع چند ساعت مصنوعی با نرخ ها و Drift های مختلف می سازد تا محاسبه Phase 4 مستقل از سخت افزار همین لپ تاپ بررسی شود.
        private static void RunSyntheticTests()
        {
            RunSyntheticDriftCase(48000, 0d);
            RunSyntheticDriftCase(48000, 100d);
            RunSyntheticDriftCase(48000, -100d);
            RunSyntheticDriftCase(44100, 75d);
            RunSyntheticDriftCase(96000, -75d);
            RunSyntheticDiscontinuityCase();
            RunSyntheticTimestampErrorCase();
        }

        // این تابع یک ساعت مصنوعی را برای مدت کافی جلو می برد و بررسی می کند Drift اندازه گیری شده با Drift تزریق شده برابر باشد.
        private static void RunSyntheticDriftCase(int sampleRate, double injectedDriftPpm)
        {
            try
            {
                VoiceAudioClockTracker tracker = new VoiceAudioClockTracker(sampleRate);
                ulong startDeviceFrames = 123456UL;
                ulong startTimestamp100Ns = 9876543210UL;
                tracker.Observe(startDeviceFrames, startTimestamp100Ns, true, false);
                ulong deltaFrames = (ulong)sampleRate * 60UL;
                double effectiveRate = sampleRate * (1d + (injectedDriftPpm / 1000000d));
                ulong deltaTimestamp100Ns = (ulong)Math.Round((deltaFrames / effectiveRate) * 10000000d);
                VoiceAudioClockObservation result = tracker.Observe(startDeviceFrames + deltaFrames, startTimestamp100Ns + deltaTimestamp100Ns, true, false);
                bool pass = result.IsValid && result.HasDriftMeasurement && Math.Abs(result.DriftPpm - injectedDriftPpm) <= SyntheticDriftTolerancePpm;
                Debug.Log("VME2_PHASE4_SYNTHETIC=" + (pass ? "PASS" : "FAIL") + " | case=drift | sampleRate=" + sampleRate + " | injectedDriftPpm=" + injectedDriftPpm.ToString("F3") + " | measuredDriftPpm=" + result.DriftPpm.ToString("F3") + " | observedRate=" + result.ObservedSampleRate.ToString("F3") + " | feedsProductionVoice=False");
            }
            catch (Exception exception)
            {
                Debug.LogError("VME2_PHASE4_SYNTHETIC=FAIL | case=drift | sampleRate=" + sampleRate + " | exception=" + exception.GetType().Name + " | message=" + Safe(exception.Message));
            }
        }

        // این تابع یک پرش عمدی در ساعت مصنوعی ایجاد می کند و بررسی می کند Tracker به جای محاسبه Drift اشتباه مبنای جدید بسازد.
        private static void RunSyntheticDiscontinuityCase()
        {
            try
            {
                VoiceAudioClockTracker tracker = new VoiceAudioClockTracker(48000);
                tracker.Observe(0UL, 100000000UL, true, false);
                tracker.Observe(48000UL, 110000000UL, true, false);
                VoiceAudioClockObservation result = tracker.Observe(96000UL, 130000000UL, true, true);
                bool pass = result.IsValid && !result.HasDriftMeasurement && result.DiscontinuityDetected && tracker.HasAnchor;
                Debug.Log("VME2_PHASE4_SYNTHETIC=" + (pass ? "PASS" : "FAIL") + " | case=discontinuity | detected=" + result.DiscontinuityDetected + " | hasDrift=" + result.HasDriftMeasurement + " | feedsProductionVoice=False");
            }
            catch (Exception exception)
            {
                Debug.LogError("VME2_PHASE4_SYNTHETIC=FAIL | case=discontinuity | exception=" + exception.GetType().Name + " | message=" + Safe(exception.Message));
            }
        }

        // این تابع timestamp نامعتبر مصنوعی را تزریق می کند و بررسی می کند مبنای قبلی پاک شود و نتیجه نامعتبر اعلام شود.
        private static void RunSyntheticTimestampErrorCase()
        {
            try
            {
                VoiceAudioClockTracker tracker = new VoiceAudioClockTracker(48000);
                tracker.Observe(0UL, 100000000UL, true, false);
                VoiceAudioClockObservation result = tracker.Observe(480UL, 100100000UL, false, false);
                bool pass = !result.IsValid && result.DiscontinuityDetected && !tracker.HasAnchor;
                Debug.Log("VME2_PHASE4_SYNTHETIC=" + (pass ? "PASS" : "FAIL") + " | case=timestamp_error | valid=" + result.IsValid + " | anchorAfterError=" + tracker.HasAnchor + " | feedsProductionVoice=False");
            }
            catch (Exception exception)
            {
                Debug.LogError("VME2_PHASE4_SYNTHETIC=FAIL | case=timestamp_error | exception=" + exception.GetType().Name + " | message=" + Safe(exception.Message));
            }
        }

        // این تابع فرمت واقعی WASAPI را دریافت می کند و Tracker ساعت را با نرخ نمونه برداری همان دستگاه ایجاد می کند.
        private void HandleFormatReady(WindowsWasapiNativeFormat value)
        {
            lock (sync)
            {
                nativeFormat = value;
                nativeClock = new VoiceAudioClockTracker(value.SampleRate);
                nativeFormatReady = true;
                latestObservation = default;
            }
        }

        // این تابع فقط metadata زمان بندی Packet واقعی WASAPI را به Tracker Phase 4 می دهد و محتوای صوتی Packet را لمس نمی کند.
        private void HandlePacketCaptured(WindowsWasapiNativePacket packet)
        {
            lock (sync)
            {
                if (!nativeFormatReady || nativeClock == null) return;
                bool timestampValid = (packet.Flags & AudclntBufferflagsTimestampError) == 0;
                bool discontinuity = (packet.Flags & AudclntBufferflagsDataDiscontinuity) != 0;
                latestObservation = nativeClock.Observe(packet.DevicePositionFrames, packet.QpcPosition100Ns, timestampValid, discontinuity);
            }
        }

        // این تابع خطای Capture مستقل را برای ثبت امن روی نخ اصلی نگه می دارد و هیچ وضعیت Production را تغییر نمی دهد.
        private void HandleFailure(string message)
        {
            lock (sync) pendingFailure = message ?? string.Empty;
        }

        // این تابع آخرین وضعیت Clock واقعی را کپی می کند و Drift و timestamp را برای قضاوت PASS یا FAIL در لاگ می نویسد.
        private void WriteNativeMetric()
        {
            WindowsWasapiNativeFormat format;
            VoiceAudioClockObservation observation;
            bool ready;
            lock (sync)
            {
                format = nativeFormat;
                observation = latestObservation;
                ready = nativeFormatReady;
            }

            if (!ready) return;
            string result = observation.IsValid ? "PASS" : "WAIT";
            Debug.Log("VME2_PHASE4_NATIVE=" + result + " | sampleRate=" + format.SampleRate + " | devicePositionFrames=" + observation.DevicePositionFrames + " | qpcPosition100Ns=" + observation.MonotonicTimestamp100Ns + " | mediaTimestamp100Ns=" + observation.MediaTimestamp100Ns + " | observedRate=" + observation.ObservedSampleRate.ToString("F3") + " | driftPpm=" + observation.DriftPpm.ToString("F3") + " | hasDrift=" + observation.HasDriftMeasurement + " | discontinuity=" + observation.DiscontinuityDetected + " | feedsProductionVoice=False");
        }

        // این تابع خطای معوق را فقط یک بار روی نخ اصلی ثبت می کند تا خطای WASAPI با لاگ یونیتی قابل بررسی باشد.
        private void FlushFailure()
        {
            string failure;
            lock (sync)
            {
                failure = pendingFailure;
                pendingFailure = string.Empty;
            }

            if (!string.IsNullOrEmpty(failure)) Debug.LogError("VME2_PHASE4_NATIVE=FAIL | " + failure + " | feedsProductionVoice=False");
        }

        // این تابع متن خطا را برای قرار گرفتن امن در یک خط لاگ آماده می کند.
        private static string Safe(string value)
        {
            return string.IsNullOrEmpty(value) ? "<empty>" : value.Replace("\r", " ").Replace("\n", " ");
        }
    }
#endif
}
