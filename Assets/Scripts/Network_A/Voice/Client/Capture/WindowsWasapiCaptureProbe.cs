using System;
using System.Threading;
using UnityEngine;

namespace Network_A.Voice.Client.Capture.Vme2
{
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    internal sealed class WindowsWasapiCaptureProbe : MonoBehaviour
    {
        private const string ProbeArgument = "--vme2-wasapi-probe";
        private const float LogIntervalSeconds = 1f;

        private readonly object statsSync = new object();

        private WindowsWasapiNativeCapture capture;
        private WindowsWasapiNativeFormat format;
        private bool formatReceived;
        private long packetCount;
        private long frameCount;
        private double latestRms;
        private double latestPeak;
        private ulong latestDevicePosition;
        private ulong latestQpcPosition100Ns;
        private string pendingFailure;
        private float nextLogTime;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        // این تابع فقط در صورتی آزمایشگر مستقل را می سازد که برنامه با آرگومان مخصوص تست واساپی اجرا شده باشد.
        private static void Bootstrap()
        {
            if (!HasProbeArgument()) return;
            if (FindFirstObjectByType<WindowsWasapiCaptureProbe>() != null) return;

            GameObject root = new GameObject("VME2_WASAPI_Capture_Probe");
            DontDestroyOnLoad(root);
            root.AddComponent<WindowsWasapiCaptureProbe>();
        }

        // این تابع آرگومان های اجرای برنامه را بررسی می کند و مشخص می کند تست مستقل واساپی باید فعال شود یا نه.
        private static bool HasProbeArgument()
        {
            string[] args = Environment.GetCommandLineArgs();

            for (int index = 0; index < args.Length; index++)
            {
                if (string.Equals(args[index], ProbeArgument, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // این تابع هنگام ساخته شدن آزمایشگر، کپچر مستقل واساپی را ایجاد می کند، رویدادها را وصل می کند و تست را شروع می کند.
        private void Awake()
        {
            capture = new WindowsWasapiNativeCapture();
            capture.FormatReady += HandleFormatReady;
            capture.PacketCaptured += HandlePacketCaptured;
            capture.Failed += HandleFailure;
            capture.Start();

            nextLogTime = Time.unscaledTime + LogIntervalSeconds;
            Debug.Log("VME2_PHASE2_WASAPI_PROBE=START");
        }

        // این تابع خطاهای رسیده از نخ کپچر را نمایش می دهد و هر یک ثانیه یک گزارش اندازه گیری از تست می نویسد.
        private void Update()
        {
            FlushFailure();

            if (Time.unscaledTime < nextLogTime) return;
            nextLogTime = Time.unscaledTime + LogIntervalSeconds;

            WriteCaptureMetric();
        }

        // این تابع هنگام پایان آزمایشگر همه رویدادها را جدا می کند و کپچر مستقل را به شکل امن متوقف و آزاد می کند.
        private void OnDestroy()
        {
            if (capture == null) return;

            capture.FormatReady -= HandleFormatReady;
            capture.PacketCaptured -= HandlePacketCaptured;
            capture.Failed -= HandleFailure;
            capture.Stop();
            capture.Dispose();
            capture = null;

            Debug.Log("VME2_PHASE2_WASAPI_PROBE=STOP");
        }

        // این تابع فرمت واقعی گزارش شده توسط ویندوز را دریافت و برای گزارش های بعدی تست ذخیره می کند.
        private void HandleFormatReady(WindowsWasapiNativeFormat value)
        {
            lock (statsSync)
            {
                format = value;
                formatReceived = true;
            }
        }

        // این تابع هر بسته خام دریافتی را شمارش می کند، سطح صدا را اندازه می گیرد و آخرین زمان بندی سخت افزاری را نگه می دارد.
        private void HandlePacketCaptured(WindowsWasapiNativePacket packet)
        {
            double rms = 0d;
            double peak = 0d;

            WindowsWasapiNativeFormat currentFormat;
            lock (statsSync)
            {
                currentFormat = format;
            }

            MeasurePacketLevel(packet.Data, currentFormat, out rms, out peak);

            lock (statsSync)
            {
                packetCount += 1;
                frameCount += packet.Frames;
                latestRms = rms;
                latestPeak = peak;
                latestDevicePosition = packet.DevicePositionFrames;
                latestQpcPosition100Ns = packet.QpcPosition100Ns;
            }
        }

        // این تابع خطای رسیده از نخ واساپی را بدون دستکاری مستقیم یونیتی برای نمایش در نخ اصلی ذخیره می کند.
        private void HandleFailure(string message)
        {
            Interlocked.Exchange(ref pendingFailure, message);
        }

        // این تابع خطای ذخیره شده را در نخ اصلی یونیتی می خواند و در لاگ به صورت خطا نمایش می دهد.
        private void FlushFailure()
        {
            string failure = Interlocked.Exchange(ref pendingFailure, null);
            if (string.IsNullOrEmpty(failure)) return;

            Debug.LogError(failure);
        }

        // این تابع نتیجه لحظه ای تست شامل دستگاه، فرمت، تعداد بسته، سطح صدا و زمان بندی را در لاگ ثبت می کند.
        private void WriteCaptureMetric()
        {
            WindowsWasapiNativeFormat currentFormat;
            bool currentFormatReceived;
            long currentPackets;
            long currentFrames;
            double currentRms;
            double currentPeak;
            ulong currentDevicePosition;
            ulong currentQpcPosition;

            lock (statsSync)
            {
                currentFormat = format;
                currentFormatReceived = formatReceived;
                currentPackets = packetCount;
                currentFrames = frameCount;
                currentRms = latestRms;
                currentPeak = latestPeak;
                currentDevicePosition = latestDevicePosition;
                currentQpcPosition = latestQpcPosition100Ns;
            }

            if (!currentFormatReceived)
            {
                Debug.Log("VME2_PHASE2_WASAPI_CAPTURE=WAITING_FOR_FORMAT");
                return;
            }

            Debug.Log(
                "VME2_PHASE2_WASAPI_CAPTURE=PASS" +
                " | capturing=" + (capture != null && capture.IsCapturing) +
                " | device=" + Safe(currentFormat.DeviceName) +
                " | sampleRate=" + currentFormat.SampleRate +
                " | channels=" + currentFormat.Channels +
                " | bitsPerSample=" + currentFormat.BitsPerSample +
                " | formatTag=" + currentFormat.FormatTag +
                " | subFormat=" + currentFormat.SubFormat.ToString("D") +
                " | packets=" + currentPackets +
                " | frames=" + currentFrames +
                " | rms=" + currentRms.ToString("F6") +
                " | peak=" + currentPeak.ToString("F6") +
                " | devicePositionFrames=" + currentDevicePosition +
                " | qpcPosition100ns=" + currentQpcPosition +
                " | feedsProductionVoice=False");
        }

        // این تابع بر اساس فرمت واقعی دستگاه، روش مناسب اندازه گیری سطح بسته صوتی را انتخاب می کند.
        private static void MeasurePacketLevel(
            ArraySegment<byte> data,
            WindowsWasapiNativeFormat currentFormat,
            out double rms,
            out double peak)
        {
            rms = 0d;
            peak = 0d;

            if (data.Array == null || data.Count <= 0) return;

            if (currentFormat.IsFloat32)
            {
                MeasureFloat32(data, out rms, out peak);
                return;
            }

            if (currentFormat.IsPcm16)
            {
                MeasurePcm16(data, out rms, out peak);
            }
        }

        // این تابع برای داده اعشاری ۳۲ بیتی مقدار آر ام اس و بیشینه دامنه صدا را محاسبه می کند.
        private static void MeasureFloat32(
            ArraySegment<byte> data,
            out double rms,
            out double peak)
        {
            double sumSquares = 0d;
            double max = 0d;
            int sampleCount = data.Count / sizeof(float);
            int offset = data.Offset;

            for (int index = 0; index < sampleCount; index++)
            {
                float sample = BitConverter.ToSingle(data.Array, offset);
                offset += sizeof(float);

                double absolute = Math.Abs(sample);
                if (absolute > max) max = absolute;
                sumSquares += sample * sample;
            }

            peak = max;
            rms = sampleCount > 0 ? Math.Sqrt(sumSquares / sampleCount) : 0d;
        }

        // این تابع برای داده پی سی ام ۱۶ بیتی مقدار آر ام اس و بیشینه دامنه صدا را محاسبه می کند.
        private static void MeasurePcm16(
            ArraySegment<byte> data,
            out double rms,
            out double peak)
        {
            double sumSquares = 0d;
            double max = 0d;
            int sampleCount = data.Count / sizeof(short);
            int offset = data.Offset;

            for (int index = 0; index < sampleCount; index++)
            {
                short raw = (short)(data.Array[offset] | (data.Array[offset + 1] << 8));
                offset += sizeof(short);

                double sample = raw / 32768d;
                double absolute = Math.Abs(sample);
                if (absolute > max) max = absolute;
                sumSquares += sample * sample;
            }

            peak = max;
            rms = sampleCount > 0 ? Math.Sqrt(sumSquares / sampleCount) : 0d;
        }

        // این تابع متن دستگاه یا گزارش را برای ثبت امن در لاگ پاکسازی می کند.
        private static string Safe(string value)
        {
            return string.IsNullOrEmpty(value) ? "<empty>" : value.Replace("|", "/");
        }
    }
#endif
}
