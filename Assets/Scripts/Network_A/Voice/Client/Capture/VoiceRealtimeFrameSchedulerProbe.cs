using System;
using System.Threading;
using Network_A.Voice.Client.Audio;
using UnityEngine;

namespace Network_A.Voice.Client.Capture.Vme2
{
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    internal sealed class VoiceRealtimeFrameSchedulerProbe : MonoBehaviour
    {
        private const string ProbeArgument = "--vme2-phase5-scheduler-test";
        private const uint AudclntBufferflagsDataDiscontinuity = 0x00000001;
        private const uint AudclntBufferflagsTimestampError = 0x00000004;
        private readonly object sync = new object();
        private WindowsWasapiNativeCapture capture;
        private WindowsWasapiNativeFormat nativeFormat;
        private VoiceAudioFormatAdapter nativeAdapter;
        private VoiceAudioClockTracker nativeClock;
        private VoiceRealtimeFrameScheduler nativeScheduler;
        private bool nativeFormatReady;
        private float[] nativeFloatBuffer = Array.Empty<float>();
        private float[] nativeAdaptedBuffer = Array.Empty<float>();
        private string pendingFailure = string.Empty;
        private float nextMetricTime;
        private long nativeFramesObserved;
        private ulong nativeLastSequence;
        private ulong nativeLastTimestamp100Ns;
        private bool nativeFrameSizeValid = true;
        private bool nativeTimestampOrderValid = true;
        private bool nativeSequenceOrderValid = true;
        private bool nativeClockValid;
        private int nativeSchedulerThreadId;
        private int mainThreadId;

        // این تابع در شروع برنامه فقط در صورت وجود آرگومان صریح Phase 5 آزمایشگر مستقل Scheduler را ایجاد می کند.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!HasProbeArgument()) return;
            GameObject root = new GameObject("VME2_Phase5_FrameScheduler_Probe");
            DontDestroyOnLoad(root);
            root.AddComponent<VoiceRealtimeFrameSchedulerProbe>();
        }

        // این تابع آرگومان های خط فرمان را بررسی می کند تا Probe بدون درخواست صریح هیچ وقت در اجرای عادی فعال نشود.
        private static bool HasProbeArgument()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length; index++) if (string.Equals(arguments[index], ProbeArgument, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // این تابع تست مصنوعی Scheduler را اجرا می کند و سپس زنجیره واقعی Phase 2 تا Phase 5 را برای تست سخت افزار راه می اندازد.
        private void Awake()
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            Debug.Log("VME2_PHASE5_SCHEDULER_PROBE=START | feedsProductionVoice=False");
            RunSyntheticSchedulerTest(mainThreadId);
            capture = new WindowsWasapiNativeCapture();
            capture.FormatReady += HandleNativeFormatReady;
            capture.PacketCaptured += HandleNativePacketCaptured;
            capture.Failed += HandleNativeFailure;
            capture.Start();
            nextMetricTime = Time.unscaledTime + 1f;
        }

        // این تابع فقط برای ثبت دوره ای نتیجه تست سخت افزاری استفاده می شود و هیچ زمان بندی صوتی داخل Update انجام نمی دهد.
        private void Update()
        {
            FlushFailure();
            if (Time.unscaledTime < nextMetricTime) return;
            nextMetricTime = Time.unscaledTime + 1f;
            WriteNativeMetric();
        }

        // این تابع هنگام نابودی Probe تمام Subscriptionها و Capture مستقل را بدون تاثیر روی Voice اصلی آزاد می کند.
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
            Debug.Log("VME2_PHASE5_SCHEDULER_PROBE=STOP | feedsProductionVoice=False");
        }

        // این تابع Scheduler را روی یک نخ مستقل با اندازه Chunkهای نامنظم آزمایش می کند تا ترتیب Sample، Sequence و Timestamp بدون Unity Update تایید شود.
        private static void RunSyntheticSchedulerTest(int unityMainThreadId)
        {
            bool completed = false;
            bool pass = false;
            int producedFrames = 0;
            bool workerThread = false;
            string failure = string.Empty;
            Thread worker = new Thread(() =>
            {
                try
                {
                    workerThread = Thread.CurrentThread.ManagedThreadId != unityMainThreadId;
                    VoiceRealtimeFrameScheduler scheduler = new VoiceRealtimeFrameScheduler();
                    const int expectedFrameCount = 100;
                    const int totalSamples = expectedFrameCount * VoiceAudioContract.SamplesPerFrame;
                    float[] source = new float[totalSamples];
                    for (int index = 0; index < source.Length; index++) source[index] = SyntheticSample(index);

                    int expectedSampleIndex = 0;
                    ulong expectedSequence = 0;
                    ulong expectedTimestamp100Ns = 123000000UL;
                    bool samplesValid = true;
                    bool sequenceValid = true;
                    bool timestampsValid = true;
                    scheduler.FrameReady += frame =>
                    {
                        producedFrames++;
                        if (frame.Samples.Count != VoiceAudioContract.SamplesPerFrame) samplesValid = false;
                        if (frame.Sequence != expectedSequence) sequenceValid = false;
                        if (frame.MediaTimestamp100Ns != expectedTimestamp100Ns) timestampsValid = false;
                        for (int index = 0; index < frame.Samples.Count; index++) if (Math.Abs(frame.Samples.Array[frame.Samples.Offset + index] - SyntheticSample(expectedSampleIndex++)) > 0.000001f) samplesValid = false;
                        expectedSequence++;
                        expectedTimestamp100Ns += 200000UL;
                    };

                    int[] chunks = { 137, 480, 777, 63, 1440, 251, 960, 311 };
                    int sourceOffset = 0;
                    int chunkIndex = 0;
                    while (sourceOffset < source.Length)
                    {
                        int count = Math.Min(chunks[chunkIndex % chunks.Length], source.Length - sourceOffset);
                        ulong chunkTimestamp = 123000000UL + SamplesTo100Ns(sourceOffset);
                        scheduler.Push(source, sourceOffset, count, chunkTimestamp, false);
                        sourceOffset += count;
                        chunkIndex++;
                    }

                    pass = producedFrames == expectedFrameCount && scheduler.BufferedSampleCount == 0 && scheduler.TotalSamplesPushed == totalSamples && scheduler.TotalFramesEmitted == expectedFrameCount && samplesValid && sequenceValid && timestampsValid;
                }
                catch (Exception ex)
                {
                    failure = ex.GetType().Name + ":" + ex.Message;
                }
                finally
                {
                    completed = true;
                }
            }) { IsBackground = true, Name = "VME2 Phase5 Synthetic Scheduler" };

            worker.Start();
            worker.Join();
            Debug.Log("VME2_PHASE5_SYNTHETIC=" + (completed && pass ? "PASS" : "FAIL") + " | frames=" + producedFrames + " | frameSamples=" + VoiceAudioContract.SamplesPerFrame + " | frameDurationMs=" + VoiceAudioContract.FrameDurationMs + " | workerThread=" + workerThread + " | failure=" + Safe(failure) + " | feedsProductionVoice=False");
        }

        // این تابع فرمت واقعی WASAPI را دریافت می کند و زنجیره Phase 3، Phase 4 و Scheduler Phase 5 را بر اساس مشخصات همان دستگاه می سازد.
        private void HandleNativeFormatReady(WindowsWasapiNativeFormat value)
        {
            lock (sync)
            {
                nativeFormat = value;
                nativeAdapter = new VoiceAudioFormatAdapter(value.SampleRate, value.Channels);
                nativeClock = new VoiceAudioClockTracker(value.SampleRate);
                nativeScheduler = new VoiceRealtimeFrameScheduler();
                nativeScheduler.FrameReady += HandleScheduledNativeFrame;
                nativeFormatReady = true;
                nativeFramesObserved = 0;
                nativeLastSequence = 0;
                nativeLastTimestamp100Ns = 0;
                nativeFrameSizeValid = true;
                nativeTimestampOrderValid = true;
                nativeSequenceOrderValid = true;
                nativeClockValid = false;
                nativeSchedulerThreadId = 0;
            }
        }

        // این تابع Packet واقعی WASAPI را از Phase 2 می گیرد، از Adapter Phase 3 و Clock Phase 4 عبور می دهد و PCM استاندارد را به Scheduler Phase 5 تحویل می دهد.
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

            bool timestampValid = (packet.Flags & AudclntBufferflagsTimestampError) == 0;
            bool discontinuity = (packet.Flags & AudclntBufferflagsDataDiscontinuity) != 0;
            VoiceAudioClockObservation observation = clock.Observe(packet.DevicePositionFrames, packet.QpcPosition100Ns, timestampValid, discontinuity);
            if (!observation.IsValid) return;

            int floatSampleCount = DecodeNativePacketToFloat(packet.Data, format, ref nativeFloatBuffer);
            if (floatSampleCount <= 0) return;
            int inputFrames = VoiceChannelAdapter.GetFrameCount(floatSampleCount, format.Channels);
            EnsureFloatBufferCapacity(ref nativeAdaptedBuffer, adapter.GetMaximumOutputSampleCount(inputFrames) + VoiceAudioContract.SamplesPerFrame);
            int written = adapter.ProcessInterleavedFloat(nativeFloatBuffer, 0, floatSampleCount, nativeAdaptedBuffer, 0, nativeAdaptedBuffer.Length);
            if (written <= 0) return;

            lock (sync) nativeClockValid = true;
            scheduler.Push(nativeAdaptedBuffer, 0, written, observation.MediaTimestamp100Ns, observation.DiscontinuityDetected);
        }

        // این تابع هر فریم ۲۰ میلی ثانیه ای تولید شده روی نخ Capture را فقط برای کنترل اندازه، Sequence و Timestamp بررسی می کند.
        private void HandleScheduledNativeFrame(VoiceScheduledAudioFrame frame)
        {
            int threadId = Thread.CurrentThread.ManagedThreadId;
            lock (sync)
            {
                if (nativeFramesObserved > 0)
                {
                    if (frame.Sequence != nativeLastSequence + 1UL) nativeSequenceOrderValid = false;
                    if (frame.MediaTimestamp100Ns != nativeLastTimestamp100Ns + 200000UL) nativeTimestampOrderValid = false;
                }

                if (frame.Samples.Count != VoiceAudioContract.SamplesPerFrame) nativeFrameSizeValid = false;
                nativeFramesObserved++;
                nativeLastSequence = frame.Sequence;
                nativeLastTimestamp100Ns = frame.MediaTimestamp100Ns;
                nativeSchedulerThreadId = threadId;
            }
        }

        // این تابع داده خام WASAPI را فقط برای دو فرمت تایید شده Float32 و PCM16 به Float32 تبدیل می کند تا تست Phase 5 بتواند از ورودی واقعی استفاده کند.
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

        // این تابع ظرفیت آرایه Float32 را فقط هنگام نیاز افزایش می دهد تا Callback سخت افزاری در حالت عادی تخصیص حافظه تکراری نداشته باشد.
        private static void EnsureFloatBufferCapacity(ref float[] buffer, int requiredCapacity)
        {
            if (requiredCapacity <= buffer.Length) return;
            int newCapacity = Math.Max(1024, buffer.Length);
            while (newCapacity < requiredCapacity) newCapacity *= 2;
            Array.Resize(ref buffer, newCapacity);
        }

        // این تابع نمونه قابل پیش بینی تست مصنوعی را می سازد تا جابه جایی، حذف یا تکرار Sample بین Chunkها قابل تشخیص باشد.
        private static float SyntheticSample(int index)
        {
            return ((index % 2001) - 1000) / 1000f;
        }

        // این تابع تعداد Sample استاندارد را به Timeline صد نانوثانیه تبدیل می کند تا Timestamp هر Chunk مصنوعی از همان قرارداد صوتی محاسبه شود.
        private static ulong SamplesTo100Ns(int samples)
        {
            return (ulong)samples * 10000000UL / (ulong)VoiceAudioContract.SampleRate;
        }

        // این تابع خطای Capture مستقل را برای ثبت امن روی نخ اصلی نگه می دارد و هیچ وضعیت Production را تغییر نمی دهد.
        private void HandleNativeFailure(string message)
        {
            lock (sync) pendingFailure = message ?? string.Empty;
        }

        // این تابع وضعیت واقعی زنجیره Phase 2 تا Phase 5 را کپی می کند و معیارهای Frame Scheduler را برای PASS یا FAIL در لاگ می نویسد.
        private void WriteNativeMetric()
        {
            WindowsWasapiNativeFormat format;
            VoiceAudioFormatAdapter adapter;
            VoiceRealtimeFrameScheduler scheduler;
            long frames;
            ulong lastSequence;
            ulong lastTimestamp;
            bool frameSizeValid;
            bool timestampOrderValid;
            bool sequenceOrderValid;
            bool clockValid;
            int schedulerThreadId;

            lock (sync)
            {
                if (!nativeFormatReady || nativeAdapter == null || nativeScheduler == null) return;
                format = nativeFormat;
                adapter = nativeAdapter;
                scheduler = nativeScheduler;
                frames = nativeFramesObserved;
                lastSequence = nativeLastSequence;
                lastTimestamp = nativeLastTimestamp100Ns;
                frameSizeValid = nativeFrameSizeValid;
                timestampOrderValid = nativeTimestampOrderValid;
                sequenceOrderValid = nativeSequenceOrderValid;
                clockValid = nativeClockValid;
                schedulerThreadId = nativeSchedulerThreadId;
            }

            bool callbackOffMainThread = schedulerThreadId != 0 && schedulerThreadId != mainThreadId;
            bool pass = frames > 0 && frameSizeValid && timestampOrderValid && sequenceOrderValid && clockValid && callbackOffMainThread;
            Debug.Log("VME2_PHASE5_NATIVE=" + (pass ? "PASS" : "WAIT") + " | sourceRate=" + format.SampleRate + " | sourceChannels=" + format.Channels + " | targetRate=" + VoiceAudioContract.SampleRate + " | targetChannels=" + VoiceAudioContract.Channels + " | scheduledFrames=" + frames + " | bufferedSamples=" + scheduler.BufferedSampleCount + " | lastSequence=" + lastSequence + " | lastTimestamp100Ns=" + lastTimestamp + " | frameSamples=" + VoiceAudioContract.SamplesPerFrame + " | frameDurationMs=" + VoiceAudioContract.FrameDurationMs + " | frameSizeValid=" + frameSizeValid + " | sequenceValid=" + sequenceOrderValid + " | timestampValid=" + timestampOrderValid + " | clockValid=" + clockValid + " | callbackOffMainThread=" + callbackOffMainThread + " | phase3Resampling=" + adapter.RequiresResampling + " | phase3ChannelAdaptation=" + adapter.RequiresChannelAdaptation + " | feedsProductionVoice=False");
        }

        // این تابع خطای معوق را فقط یک بار روی نخ اصلی ثبت می کند تا خطای Capture با لاگ یونیتی قابل بررسی باشد.
        private void FlushFailure()
        {
            string failure;
            lock (sync)
            {
                failure = pendingFailure;
                pendingFailure = string.Empty;
            }

            if (!string.IsNullOrEmpty(failure)) Debug.LogError("VME2_PHASE5_NATIVE=FAIL | " + Safe(failure) + " | feedsProductionVoice=False");
        }

        // این تابع متن خطا را برای قرار گرفتن امن در یک خط لاگ آماده می کند.
        private static string Safe(string value)
        {
            return string.IsNullOrEmpty(value) ? "<empty>" : value.Replace("\r", " ").Replace("\n", " ");
        }
    }
#endif
}
