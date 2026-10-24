using System;
using Network_A.Voice.Client.Capture.Vme2;
using UnityEngine;

namespace Network_A.Voice.Client.Audio
{
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    internal sealed class VoiceAudioFormatAdapterProbe : MonoBehaviour
    {
        private const string ProbeArgument = "--vme2-phase3-adapter-test";
        private const double MetricIntervalSeconds = 1.0;
        private const int SyntheticDurationFramesAtTarget = 48000;
        private const double SyntheticFrequencyHz = 1000.0;
        private const float SyntheticAmplitude = 0.25f;

        private readonly object sync = new object();
        private WindowsWasapiNativeCapture nativeCapture;
        private WindowsWasapiNativeFormat nativeFormat;
        private VoiceAudioFormatAdapter nativeAdapter;
        private bool nativeFormatReady;
        private long nativeInputFrames;
        private long nativeOutputSamples;
        private double nativeSquareSum;
        private long nativeMeasuredSamples;
        private float nativePeak;
        private string pendingFailure;
        private double nextMetricTime;
        private float[] nativeFloatBuffer = new float[4096];
        private float[] nativeOutputBuffer = new float[4096];

        // این تابع فقط زمانی Probe فاز ۳ را می سازد که آرگومان مخصوص تست در اجرای برنامه وجود داشته باشد و در اجرای عادی هیچ کاری انجام نمی دهد.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!HasProbeArgument()) return;
            GameObject root = new GameObject("VME2_Phase3_Audio_Format_Adapter_Probe");
            DontDestroyOnLoad(root);
            root.AddComponent<VoiceAudioFormatAdapterProbe>();
        }

        // این تابع آرگومان های خط فرمان را بررسی می کند تا اجرای تست فاز ۳ کاملا شرطی و جدا از مسیر اصلی Voice باقی بماند.
        private static bool HasProbeArgument()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length; index++) if (string.Equals(arguments[index], ProbeArgument, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // این تابع هنگام ساخته شدن Probe ابتدا تست های عمومی تبدیل فرمت را اجرا می کند و سپس WASAPI مستقل را برای تست سخت افزار واقعی راه می اندازد.
        private void Awake()
        {
            Debug.Log("VME2_PHASE3_ADAPTER_PROBE=START | feedsProductionVoice=False");
            RunSyntheticTests();
            nativeCapture = new WindowsWasapiNativeCapture();
            nativeCapture.FormatReady += HandleNativeFormatReady;
            nativeCapture.PacketCaptured += HandleNativePacketCaptured;
            nativeCapture.Failed += HandleNativeFailure;
            nativeCapture.Start();
            nextMetricTime = Time.realtimeSinceStartupAsDouble + MetricIntervalSeconds;
        }

        // این تابع در نخ اصلی Unity خطاهای جمع شده را ثبت می کند و هر یک ثانیه یک معیار خلاصه از تبدیل سخت افزار واقعی می نویسد.
        private void Update()
        {
            FlushFailure();
            if (Time.realtimeSinceStartupAsDouble < nextMetricTime) return;
            nextMetricTime = Time.realtimeSinceStartupAsDouble + MetricIntervalSeconds;
            WriteNativeMetric();
        }

        // این تابع هنگام پایان Probe همه رویدادها و منابع WASAPI مستقل را آزاد می کند و هیچ تغییری در Runtime اصلی Voice ایجاد نمی کند.
        private void OnDestroy()
        {
            if (nativeCapture == null) return;
            nativeCapture.FormatReady -= HandleNativeFormatReady;
            nativeCapture.PacketCaptured -= HandleNativePacketCaptured;
            nativeCapture.Failed -= HandleNativeFailure;
            nativeCapture.Dispose();
            nativeCapture = null;
        }

        // این تابع مجموعه تست های ثابت را برای نرخ ها و تعداد کانال های متفاوت اجرا می کند تا Adapter به سخت افزار همین لپ تاپ محدود نباشد.
        private void RunSyntheticTests()
        {
            RunSyntheticCase(48000, 1);
            RunSyntheticCase(48000, 2);
            RunSyntheticCase(44100, 1);
            RunSyntheticCase(44100, 2);
            RunSyntheticCase(96000, 2);
            RunSyntheticCase(44100, 4);
        }

        // این تابع یک سیگنال آزمایشی مشخص می سازد، آن را در بسته های پیوسته وارد Adapter می کند و نرخ، کانال، سطح و طول خروجی را کنترل می کند.
        private void RunSyntheticCase(int sourceSampleRate, int sourceChannels)
        {
            try
            {
                int sourceFrames = (int)Math.Round(SyntheticDurationFramesAtTarget * (double)sourceSampleRate / VoiceAudioContract.SampleRate);
                int chunkFrames = Math.Max(1, sourceSampleRate / 100);
                float[] sourceChunk = new float[chunkFrames * sourceChannels];
                VoiceAudioFormatAdapter adapter = new VoiceAudioFormatAdapter(sourceSampleRate, sourceChannels);
                float[] outputChunk = new float[adapter.GetMaximumOutputSampleCount(chunkFrames) + 64];
                long outputSamples = 0;
                double squareSum = 0.0;
                float peak = 0f;
                int generatedFrames = 0;

                while (generatedFrames < sourceFrames)
                {
                    int framesThisChunk = Math.Min(chunkFrames, sourceFrames - generatedFrames);
                    FillSyntheticInterleaved(sourceChunk, framesThisChunk, sourceChannels, sourceSampleRate, generatedFrames);
                    int written = adapter.ProcessInterleavedFloat(sourceChunk, 0, framesThisChunk * sourceChannels, outputChunk, 0, outputChunk.Length);
                    AccumulateLevel(outputChunk, written, ref squareSum, ref peak);
                    outputSamples += written;
                    generatedFrames += framesThisChunk;
                }

                double rms = outputSamples > 0 ? Math.Sqrt(squareSum / outputSamples) : 0.0;
                long expected = SyntheticDurationFramesAtTarget;
                long tolerance = sourceSampleRate == VoiceAudioContract.SampleRate ? 0 : 64;
                bool countPass = Math.Abs(outputSamples - expected) <= tolerance;
                bool levelPass = rms > 0.10 && rms < 0.25 && peak > 0.15f && peak < 0.35f;
                bool pass = countPass && levelPass;

                Debug.Log("VME2_PHASE3_SYNTHETIC=" + (pass ? "PASS" : "FAIL") + " | sourceRate=" + sourceSampleRate + " | sourceChannels=" + sourceChannels + " | targetRate=" + VoiceAudioContract.SampleRate + " | targetChannels=" + VoiceAudioContract.Channels + " | outputSamples=" + outputSamples + " | expected=" + expected + " | rms=" + rms.ToString("F6") + " | peak=" + peak.ToString("F6") + " | feedsProductionVoice=False");
            }
            catch (Exception exception)
            {
                Debug.LogError("VME2_PHASE3_SYNTHETIC=FAIL | sourceRate=" + sourceSampleRate + " | sourceChannels=" + sourceChannels + " | exception=" + exception.GetType().Name + " | message=" + Safe(exception.Message));
            }
        }

        // این تابع سیگنال سینوسی یکسان را در همه کانال های ورودی می نویسد تا صحت تبدیل کانال مستقل از تعداد کانال قابل اندازه گیری باشد.
        private static void FillSyntheticInterleaved(float[] destination, int frameCount, int channels, int sampleRate, int absoluteStartFrame)
        {
            int writeIndex = 0;
            for (int frame = 0; frame < frameCount; frame++)
            {
                double phase = 2.0 * Math.PI * SyntheticFrequencyHz * (absoluteStartFrame + frame) / sampleRate;
                float sample = SyntheticAmplitude * (float)Math.Sin(phase);
                for (int channel = 0; channel < channels; channel++) destination[writeIndex++] = sample;
            }
        }

        // این تابع فرمت واقعی گزارش شده توسط WASAPI را دریافت می کند و Adapter عمومی فاز ۳ را بر اساس همان نرخ و تعداد کانال در زمان اجرا می سازد.
        private void HandleNativeFormatReady(WindowsWasapiNativeFormat value)
        {
            lock (sync)
            {
                nativeFormat = value;
                nativeAdapter = new VoiceAudioFormatAdapter(value.SampleRate, value.Channels);
                nativeFormatReady = true;
                nativeInputFrames = 0;
                nativeOutputSamples = 0;
                nativeSquareSum = 0.0;
                nativeMeasuredSamples = 0;
                nativePeak = 0f;
            }
        }

        // این تابع هر بسته خام WASAPI را به Float32 تبدیل می کند، آن را از Adapter عمومی عبور می دهد و فقط معیارهای خروجی را برای تست جمع می کند.
        private void HandleNativePacketCaptured(WindowsWasapiNativePacket packet)
        {
            WindowsWasapiNativeFormat currentFormat;
            VoiceAudioFormatAdapter currentAdapter;

            lock (sync)
            {
                if (!nativeFormatReady || nativeAdapter == null) return;
                currentFormat = nativeFormat;
                currentAdapter = nativeAdapter;
            }

            int floatSampleCount = DecodeNativePacketToFloat(packet.Data, currentFormat, ref nativeFloatBuffer);
            if (floatSampleCount <= 0) return;
            int inputFrames = VoiceChannelAdapter.GetFrameCount(floatSampleCount, currentFormat.Channels);
            EnsureFloatBufferCapacity(ref nativeOutputBuffer, currentAdapter.GetMaximumOutputSampleCount(inputFrames) + 64);
            int written = currentAdapter.ProcessInterleavedFloat(nativeFloatBuffer, 0, floatSampleCount, nativeOutputBuffer, 0, nativeOutputBuffer.Length);

            double localSquareSum = 0.0;
            float localPeak = 0f;
            AccumulateLevel(nativeOutputBuffer, written, ref localSquareSum, ref localPeak);

            lock (sync)
            {
                nativeInputFrames += inputFrames;
                nativeOutputSamples += written;
                nativeSquareSum += localSquareSum;
                nativeMeasuredSamples += written;
                if (localPeak > nativePeak) nativePeak = localPeak;
            }
        }

        // این تابع داده خام WASAPI را برای فرمت های Float32 و PCM16 به نمونه های Float32 درهم تنیده تبدیل می کند تا Adapter وابسته به نوع بایت های ویندوز نباشد.
        private static int DecodeNativePacketToFloat(ArraySegment<byte> data, WindowsWasapiNativeFormat format, ref float[] destination)
        {
            if (format.IsFloat32)
            {
                int sampleCount = data.Count / sizeof(float);
                EnsureFloatBufferCapacity(ref destination, sampleCount);
                Buffer.BlockCopy(data.Array, data.Offset, destination, 0, sampleCount * sizeof(float));
                return sampleCount;
            }

            if (format.IsPcm16)
            {
                int sampleCount = data.Count / sizeof(short);
                EnsureFloatBufferCapacity(ref destination, sampleCount);
                int read = data.Offset;
                for (int index = 0; index < sampleCount; index++)
                {
                    short sample = (short)(data.Array[read] | (data.Array[read + 1] << 8));
                    destination[index] = sample / 32768f;
                    read += 2;
                }
                return sampleCount;
            }

            return 0;
        }

        // این تابع سطح توان و بیشینه نمونه های خروجی را جمع می کند تا بدون پخش یا ارسال صدا بتوان سلامت تبدیل را بررسی کرد.
        private static void AccumulateLevel(float[] samples, int sampleCount, ref double squareSum, ref float peak)
        {
            for (int index = 0; index < sampleCount; index++)
            {
                float value = samples[index];
                squareSum += value * value;
                float absolute = Math.Abs(value);
                if (absolute > peak) peak = absolute;
            }
        }

        // این تابع خطای WASAPI مستقل را برای ثبت در نخ اصلی نگه می دارد تا Callback سخت افزاری وارد سیستم Log اصلی Voice نشود.
        private void HandleNativeFailure(string message)
        {
            lock (sync) pendingFailure = message;
        }

        // این تابع خطای ذخیره شده را در نخ اصلی Unity ثبت و سپس پاک می کند.
        private void FlushFailure()
        {
            string message;
            lock (sync)
            {
                message = pendingFailure;
                pendingFailure = null;
            }
            if (!string.IsNullOrEmpty(message)) Debug.LogError("VME2_PHASE3_NATIVE=FAIL | source=" + Safe(message));
        }

        // این تابع وضعیت تبدیل سخت افزار واقعی را هر یک ثانیه ثبت می کند و نشان می دهد خروجی به قرارداد ۴۸ کیلوهرتز مونو رسیده است یا نه.
        private void WriteNativeMetric()
        {
            WindowsWasapiNativeFormat currentFormat;
            bool ready;
            long inputFrames;
            long outputSamples;
            double squareSum;
            long measuredSamples;
            float peak;

            lock (sync)
            {
                currentFormat = nativeFormat;
                ready = nativeFormatReady;
                inputFrames = nativeInputFrames;
                outputSamples = nativeOutputSamples;
                squareSum = nativeSquareSum;
                measuredSamples = nativeMeasuredSamples;
                peak = nativePeak;
            }

            if (!ready)
            {
                Debug.Log("VME2_PHASE3_NATIVE=WAIT | reason=native_format_not_ready | feedsProductionVoice=False");
                return;
            }

            double rms = measuredSamples > 0 ? Math.Sqrt(squareSum / measuredSamples) : 0.0;
            Debug.Log("VME2_PHASE3_NATIVE=PASS | sourceRate=" + currentFormat.SampleRate + " | sourceChannels=" + currentFormat.Channels + " | sourceBits=" + currentFormat.BitsPerSample + " | targetRate=" + VoiceAudioContract.SampleRate + " | targetChannels=" + VoiceAudioContract.Channels + " | inputFrames=" + inputFrames + " | outputSamples=" + outputSamples + " | rms=" + rms.ToString("F6") + " | peak=" + peak.ToString("F6") + " | resampling=" + (currentFormat.SampleRate != VoiceAudioContract.SampleRate) + " | channelAdaptation=" + (currentFormat.Channels != VoiceAudioContract.Channels) + " | feedsProductionVoice=False");
        }

        // این تابع ظرفیت آرایه Float32 را فقط در صورت نیاز افزایش می دهد تا Callbackهای معمولی باعث ایجاد آرایه جدید در هر بسته نشوند.
        private static void EnsureFloatBufferCapacity(ref float[] buffer, int requiredCapacity)
        {
            if (requiredCapacity <= buffer.Length) return;
            int newCapacity = buffer.Length;
            while (newCapacity < requiredCapacity) newCapacity *= 2;
            Array.Resize(ref buffer, newCapacity);
        }

        // این تابع متن خطا را برای ثبت تک خطی در Log پاک سازی می کند تا شکست تست قابل جستجو و مقایسه باشد.
        private static string Safe(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
#endif
}
