using System;
using Network_A.Voice.Client.Audio;
using Network_A.Voice.Client.Codec;
using UnityEngine;

namespace Network_A.Voice.Client.Capture.Vme2
{
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    internal sealed class VoiceOpusMediaCodecProbe : MonoBehaviour
    {
        private const string ProbeArgument = "--vme2-phase6-codec-test";
        private const uint AudclntBufferflagsDataDiscontinuity = 0x00000001;
        private const uint AudclntBufferflagsTimestampError = 0x00000004;
        private readonly object sync = new object();
        private WindowsWasapiNativeCapture capture;
        private WindowsWasapiNativeFormat nativeFormat;
        private VoiceAudioFormatAdapter nativeAdapter;
        private VoiceAudioClockTracker nativeClock;
        private VoiceRealtimeFrameScheduler nativeScheduler;
        private VoiceOpusMediaCodecLayer nativeCodec;
        private bool nativeFormatReady;
        private float[] nativeFloatBuffer = Array.Empty<float>();
        private float[] nativeAdaptedBuffer = Array.Empty<float>();
        private string pendingFailure = string.Empty;
        private float nextMetricTime;
        private long encodedFrames;
        private long decodedFrames;
        private long totalPacketBytes;
        private int minimumPacketBytes = int.MaxValue;
        private int maximumPacketBytes;
        private bool packetValid = true;
        private bool decodedFrameSizeValid = true;
        private bool sequenceValid = true;
        private bool timestampValid = true;
        private bool sampleFinite = true;
        private float maximumDecodedPeak;
        private ulong lastEncodedSequence;
        private ulong lastEncodedTimestamp100Ns;

        // این تابع در شروع برنامه فقط در صورت وجود آرگومان صریح فاز ۶ آزمایشگر مستقل لایه کدک را ایجاد می کند.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!HasProbeArgument()) return;
            GameObject root = new GameObject("VME2_Phase6_Opus_Media_Codec_Probe");
            DontDestroyOnLoad(root);
            root.AddComponent<VoiceOpusMediaCodecProbe>();
        }

        // این تابع آرگومان های خط فرمان را بررسی می کند تا آزمایشگر فاز ۶ در اجرای عادی برنامه فعال نشود.
        private static bool HasProbeArgument()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length; index++) if (string.Equals(arguments[index], ProbeArgument, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // این تابع ابتدا آزمون های مصنوعی سه نرخ بیت مجاز را اجرا می کند و سپس زنجیره واقعی فاز ۲ تا فاز ۶ را روی میکروفون سخت افزار راه می اندازد.
        private void Awake()
        {
            Debug.Log("VME2_PHASE6_CODEC_PROBE=START | feedsProductionVoice=False");
            RunSyntheticCodecTest(28);
            RunSyntheticCodecTest(32);
            RunSyntheticCodecTest(40);
            capture = new WindowsWasapiNativeCapture();
            capture.FormatReady += HandleNativeFormatReady;
            capture.PacketCaptured += HandleNativePacketCaptured;
            capture.Failed += HandleNativeFailure;
            capture.Start();
            nextMetricTime = Time.unscaledTime + 1f;
        }

        // این تابع فقط نتیجه دوره ای آزمون سخت افزاری را روی رشته اصلی ثبت می کند و هیچ کدگذاری یا زمان بندی صوتی در آن انجام نمی شود.
        private void Update()
        {
            FlushFailure();
            if (Time.unscaledTime < nextMetricTime) return;
            nextMetricTime = Time.unscaledTime + 1f;
            WriteNativeMetric();
        }

        // این تابع هنگام نابودی آزمایشگر همه اتصال های رویدادی و منابع مستقل فاز ۶ را آزاد می کند و به مسیر صوت اصلی دست نمی زند.
        private void OnDestroy()
        {
            if (capture != null)
            {
                capture.FormatReady -= HandleNativeFormatReady;
                capture.PacketCaptured -= HandleNativePacketCaptured;
                capture.Failed -= HandleNativeFailure;
                capture.Stop();
                capture.Dispose();
                capture = null;
            }

            if (nativeScheduler != null) nativeScheduler.FrameReady -= HandleScheduledNativeFrame;
            nativeCodec?.Dispose();
            nativeCodec = null;
            Debug.Log("VME2_PHASE6_CODEC_PROBE=STOP | feedsProductionVoice=False");
        }

        // این تابع یک ثانیه سیگنال مصنوعی را به فریم های بیست میلی ثانیه ای تبدیل می کند، همه فریم ها را کدگذاری و باز می کند و سلامت هر سه نرخ بیت مجاز را می سنجد.
        private static void RunSyntheticCodecTest(int bitrateKbps)
        {
            bool pass = true;
            string failure = string.Empty;
            int encodedCount = 0;
            int decodedCount = 0;
            int minimumBytes = int.MaxValue;
            int maximumBytes = 0;
            long totalBytes = 0;
            float maximumPeak = 0f;

            try
            {
                using (VoiceOpusMediaCodecLayer codec = new VoiceOpusMediaCodecLayer(bitrateKbps))
                {
                    VoiceRealtimeFrameScheduler scheduler = new VoiceRealtimeFrameScheduler();
                    scheduler.FrameReady += frame =>
                    {
                        VoiceEncodedMediaFrame encoded = codec.Encode(frame);
                        VoiceDecodedMediaFrame decoded = codec.Decode(encoded);
                        encodedCount++;
                        decodedCount++;
                        if (encoded.Sequence != frame.Sequence || encoded.MediaTimestamp100Ns != frame.MediaTimestamp100Ns) pass = false;
                        if (decoded.Sequence != frame.Sequence || decoded.MediaTimestamp100Ns != frame.MediaTimestamp100Ns) pass = false;
                        if (decoded.Samples.Length != VoiceAudioContract.SamplesPerFrame) pass = false;
                        if (encoded.Packet.Length <= 0 || encoded.Packet.Length > 4096) pass = false;
                        minimumBytes = Math.Min(minimumBytes, encoded.Packet.Length);
                        maximumBytes = Math.Max(maximumBytes, encoded.Packet.Length);
                        totalBytes += encoded.Packet.Length;
                        maximumPeak = Math.Max(maximumPeak, MeasurePeak(decoded.Samples));
                        if (!AllFinite(decoded.Samples)) pass = false;
                    };

                    const int frameCount = 50;
                    float[] source = BuildSyntheticSignal(frameCount * VoiceAudioContract.SamplesPerFrame);
                    int[] chunks = { 137, 480, 777, 63, 1440, 251, 960, 311 };
                    int offset = 0;
                    int chunkIndex = 0;
                    while (offset < source.Length)
                    {
                        int count = Math.Min(chunks[chunkIndex % chunks.Length], source.Length - offset);
                        scheduler.Push(source, offset, count, SamplesTo100Ns(offset), false);
                        offset += count;
                        chunkIndex++;
                    }

                    pass = pass && encodedCount == frameCount && decodedCount == frameCount && scheduler.BufferedSampleCount == 0 && maximumPeak > 0.001f;
                }
            }
            catch (Exception ex)
            {
                pass = false;
                failure = ex.GetType().Name + ":" + ex.Message;
            }

            double averageBytes = encodedCount > 0 ? totalBytes / (double)encodedCount : 0d;
            if (minimumBytes == int.MaxValue) minimumBytes = 0;
            Debug.Log("VME2_PHASE6_SYNTHETIC=" + (pass ? "PASS" : "FAIL") + " | bitrateKbps=" + bitrateKbps + " | encodedFrames=" + encodedCount + " | decodedFrames=" + decodedCount + " | minPacketBytes=" + minimumBytes + " | maxPacketBytes=" + maximumBytes + " | avgPacketBytes=" + averageBytes.ToString("F2") + " | decodedPeak=" + maximumPeak.ToString("F6") + " | failure=" + Safe(failure) + " | feedsProductionVoice=False");
        }

        // این تابع فرمت واقعی میکروفون را دریافت می کند و اجزای مستقل فازهای ۳ تا ۶ را برای همان مشخصات سخت افزار ایجاد می کند.
        private void HandleNativeFormatReady(WindowsWasapiNativeFormat value)
        {
            lock (sync)
            {
                if (nativeScheduler != null) nativeScheduler.FrameReady -= HandleScheduledNativeFrame;
                nativeCodec?.Dispose();
                nativeFormat = value;
                nativeAdapter = new VoiceAudioFormatAdapter(value.SampleRate, value.Channels);
                nativeClock = new VoiceAudioClockTracker(value.SampleRate);
                nativeScheduler = new VoiceRealtimeFrameScheduler();
                nativeCodec = new VoiceOpusMediaCodecLayer(32);
                nativeScheduler.FrameReady += HandleScheduledNativeFrame;
                nativeFormatReady = true;
                encodedFrames = 0;
                decodedFrames = 0;
                totalPacketBytes = 0;
                minimumPacketBytes = int.MaxValue;
                maximumPacketBytes = 0;
                packetValid = true;
                decodedFrameSizeValid = true;
                sequenceValid = true;
                timestampValid = true;
                sampleFinite = true;
                maximumDecodedPeak = 0f;
                lastEncodedSequence = 0;
                lastEncodedTimestamp100Ns = 0;
            }
        }

        // این تابع بسته واقعی فاز ۲ را به نمونه شناور تبدیل می کند، از تبدیل فرمت فاز ۳ و ساعت فاز ۴ عبور می دهد و نتیجه را برای ساخت فریم به فاز ۵ می سپارد.
        private void HandleNativePacketCaptured(WindowsWasapiNativePacket packet)
        {
            WindowsWasapiNativeFormat format;
            VoiceAudioFormatAdapter adapter;
            VoiceAudioClockTracker clock;
            VoiceRealtimeFrameScheduler scheduler;

            lock (sync)
            {
                if (!nativeFormatReady || nativeAdapter == null || nativeClock == null || nativeScheduler == null) return;
                format = nativeFormat;
                adapter = nativeAdapter;
                clock = nativeClock;
                scheduler = nativeScheduler;
            }

            bool timestampIsValid = (packet.Flags & AudclntBufferflagsTimestampError) == 0;
            bool discontinuity = (packet.Flags & AudclntBufferflagsDataDiscontinuity) != 0;
            VoiceAudioClockObservation observation = clock.Observe(packet.DevicePositionFrames, packet.QpcPosition100Ns, timestampIsValid, discontinuity);
            if (!observation.IsValid) return;

            int floatSampleCount = DecodeNativePacketToFloat(packet.Data, format, ref nativeFloatBuffer);
            if (floatSampleCount <= 0) return;
            int inputFrames = VoiceChannelAdapter.GetFrameCount(floatSampleCount, format.Channels);
            EnsureFloatBufferCapacity(ref nativeAdaptedBuffer, adapter.GetMaximumOutputSampleCount(inputFrames) + VoiceAudioContract.SamplesPerFrame);
            int written = adapter.ProcessInterleavedFloat(nativeFloatBuffer, 0, floatSampleCount, nativeAdaptedBuffer, 0, nativeAdaptedBuffer.Length);
            if (written <= 0) return;
            scheduler.Push(nativeAdaptedBuffer, 0, written, observation.MediaTimestamp100Ns, observation.DiscontinuityDetected);
        }

        // این تابع هر فریم استاندارد فاز ۵ را بلافاصله با لایه فاز ۶ کدگذاری و باز می کند و شماره، زمان، اندازه بسته و سلامت نمونه های خروجی را می سنجد.
        private void HandleScheduledNativeFrame(VoiceScheduledAudioFrame frame)
        {
            VoiceOpusMediaCodecLayer codec;
            lock (sync)
            {
                if (nativeCodec == null) return;
                codec = nativeCodec;
            }

            try
            {
                VoiceEncodedMediaFrame encoded = codec.Encode(frame);
                VoiceDecodedMediaFrame decoded = codec.Decode(encoded);
                float peak = MeasurePeak(decoded.Samples);
                bool finite = AllFinite(decoded.Samples);

                lock (sync)
                {
                    if (encodedFrames > 0)
                    {
                        if (encoded.Sequence != lastEncodedSequence + 1UL) sequenceValid = false;
                        if (encoded.MediaTimestamp100Ns != lastEncodedTimestamp100Ns + 200000UL) timestampValid = false;
                    }

                    encodedFrames++;
                    decodedFrames++;
                    totalPacketBytes += encoded.Packet.Length;
                    minimumPacketBytes = Math.Min(minimumPacketBytes, encoded.Packet.Length);
                    maximumPacketBytes = Math.Max(maximumPacketBytes, encoded.Packet.Length);
                    if (encoded.Packet.Length <= 0 || encoded.Packet.Length > 4096) packetValid = false;
                    if (decoded.Samples.Length != VoiceAudioContract.SamplesPerFrame) decodedFrameSizeValid = false;
                    if (decoded.Sequence != encoded.Sequence) sequenceValid = false;
                    if (decoded.MediaTimestamp100Ns != encoded.MediaTimestamp100Ns) timestampValid = false;
                    if (!finite) sampleFinite = false;
                    maximumDecodedPeak = Math.Max(maximumDecodedPeak, peak);
                    lastEncodedSequence = encoded.Sequence;
                    lastEncodedTimestamp100Ns = encoded.MediaTimestamp100Ns;
                }
            }
            catch (Exception ex)
            {
                HandleNativeFailure("codec:" + ex.GetType().Name + ":" + ex.Message);
            }
        }

        // این تابع داده خام سخت افزار را فقط برای دو قالب تاییدشده شناور ۳۲ بیتی و عدد صحیح ۱۶ بیتی به نمونه های شناور تبدیل می کند.
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

        // این تابع ظرفیت آرایه نمونه های شناور را فقط هنگام نیاز بزرگ تر می کند تا در حالت پایدار تخصیص حافظه تکراری برای تبدیل ورودی ایجاد نشود.
        private static void EnsureFloatBufferCapacity(ref float[] buffer, int requiredCapacity)
        {
            if (requiredCapacity <= buffer.Length) return;
            int newCapacity = Math.Max(1024, buffer.Length);
            while (newCapacity < requiredCapacity) newCapacity *= 2;
            Array.Resize(ref buffer, newCapacity);
        }

        // این تابع یک سیگنال صوتی مصنوعی پیوسته می سازد تا کدگذار و بازکننده با داده غیرساکت و قابل اندازه گیری آزمایش شوند.
        private static float[] BuildSyntheticSignal(int sampleCount)
        {
            float[] samples = new float[sampleCount];
            for (int index = 0; index < samples.Length; index++) samples[index] = 0.2f * (float)Math.Sin(2d * Math.PI * 440d * index / VoiceAudioContract.SampleRate);
            return samples;
        }

        // این تابع بیشترین قدر مطلق نمونه های یک فریم بازشده را محاسبه می کند تا وجود سیگنال واقعی در خروجی بازکننده کنترل شود.
        private static float MeasurePeak(float[] samples)
        {
            float peak = 0f;
            for (int index = 0; index < samples.Length; index++) peak = Math.Max(peak, Math.Abs(samples[index]));
            return peak;
        }

        // این تابع همه نمونه های بازشده را از نظر عدد نامعتبر بررسی می کند تا خروجی خراب یا بی نهایت به عنوان نتیجه موفق پذیرفته نشود.
        private static bool AllFinite(float[] samples)
        {
            for (int index = 0; index < samples.Length; index++) if (float.IsNaN(samples[index]) || float.IsInfinity(samples[index])) return false;
            return true;
        }

        // این تابع تعداد نمونه استاندارد را به واحد صد نانوثانیه تبدیل می کند تا آزمون مصنوعی از همان خط زمانی فاز ۴ و ۵ استفاده کند.
        private static ulong SamplesTo100Ns(int samples)
        {
            return (ulong)samples * 10000000UL / (ulong)VoiceAudioContract.SampleRate;
        }

        // این تابع خطای دریافت یا کدک را به صورت امن ذخیره می کند تا ثبت گزارش روی رشته اصلی انجام شود و مسیر صوت اصلی تغییری نکند.
        private void HandleNativeFailure(string message)
        {
            lock (sync) pendingFailure = message ?? string.Empty;
        }

        // این تابع معیارهای زنجیره واقعی فاز ۲ تا فاز ۶ را کپی می کند و نتیجه کدگذاری و بازکردن اوپوس را به صورت دوره ای در گزارش می نویسد.
        private void WriteNativeMetric()
        {
            WindowsWasapiNativeFormat format;
            VoiceAudioFormatAdapter adapter;
            long localEncodedFrames;
            long localDecodedFrames;
            long localTotalPacketBytes;
            int localMinimumPacketBytes;
            int localMaximumPacketBytes;
            bool localPacketValid;
            bool localDecodedFrameSizeValid;
            bool localSequenceValid;
            bool localTimestampValid;
            bool localSampleFinite;
            float localMaximumDecodedPeak;

            lock (sync)
            {
                if (!nativeFormatReady || nativeAdapter == null || nativeCodec == null) return;
                format = nativeFormat;
                adapter = nativeAdapter;
                localEncodedFrames = encodedFrames;
                localDecodedFrames = decodedFrames;
                localTotalPacketBytes = totalPacketBytes;
                localMinimumPacketBytes = minimumPacketBytes == int.MaxValue ? 0 : minimumPacketBytes;
                localMaximumPacketBytes = maximumPacketBytes;
                localPacketValid = packetValid;
                localDecodedFrameSizeValid = decodedFrameSizeValid;
                localSequenceValid = sequenceValid;
                localTimestampValid = timestampValid;
                localSampleFinite = sampleFinite;
                localMaximumDecodedPeak = maximumDecodedPeak;
            }

            double averagePacketBytes = localEncodedFrames > 0 ? localTotalPacketBytes / (double)localEncodedFrames : 0d;
            bool pass = localEncodedFrames > 0 && localDecodedFrames == localEncodedFrames && localPacketValid && localDecodedFrameSizeValid && localSequenceValid && localTimestampValid && localSampleFinite;
            Debug.Log("VME2_PHASE6_NATIVE=" + (pass ? "PASS" : "FAIL") + " | sourceRate=" + format.SampleRate + " | sourceChannels=" + format.Channels + " | targetRate=" + VoiceAudioContract.SampleRate + " | targetChannels=" + VoiceAudioContract.Channels + " | bitrateKbps=32 | encodedFrames=" + localEncodedFrames + " | decodedFrames=" + localDecodedFrames + " | minPacketBytes=" + localMinimumPacketBytes + " | maxPacketBytes=" + localMaximumPacketBytes + " | avgPacketBytes=" + averagePacketBytes.ToString("F2") + " | decodedPeak=" + localMaximumDecodedPeak.ToString("F6") + " | packetValid=" + localPacketValid + " | decodedFrameSizeValid=" + localDecodedFrameSizeValid + " | sequenceValid=" + localSequenceValid + " | timestampValid=" + localTimestampValid + " | samplesFinite=" + localSampleFinite + " | phase3Resampling=" + adapter.RequiresResampling + " | phase3ChannelAdaptation=" + adapter.RequiresChannelAdaptation + " | feedsProductionVoice=False");
        }

        // این تابع خطای ذخیره شده روی رشته سخت افزار را یک بار روی رشته اصلی ثبت و سپس پاک می کند.
        private void FlushFailure()
        {
            string failure;
            lock (sync)
            {
                failure = pendingFailure;
                pendingFailure = string.Empty;
            }

            if (string.IsNullOrEmpty(failure)) return;
            Debug.LogError("VME2_PHASE6_FAILURE=" + Safe(failure) + " | feedsProductionVoice=False");
        }

        // این تابع متن خطا را برای ثبت تک خطی ایمن می کند تا شکست خط جدید یا جداکننده گزارش را به هم نریزد.
        private static string Safe(string value)
        {
            if (string.IsNullOrEmpty(value)) return "<empty>";
            return value.Replace("\r", " ").Replace("\n", " ").Replace("|", "/");
        }
    }
#endif
}
