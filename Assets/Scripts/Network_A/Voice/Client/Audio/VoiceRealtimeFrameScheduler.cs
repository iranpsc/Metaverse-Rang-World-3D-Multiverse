using System;

namespace Network_A.Voice.Client.Audio
{
    internal readonly struct VoiceScheduledAudioFrame
    {
        // این سازنده مشخصات یک فریم زمان بندی شده را همراه با نمونه های همان فریم در یک ساختار سبک ذخیره می کند.
        public VoiceScheduledAudioFrame(ulong sequence, ulong mediaTimestamp100Ns, ArraySegment<float> samples)
        {
            Sequence = sequence;
            MediaTimestamp100Ns = mediaTimestamp100Ns;
            Samples = samples;
        }

        public ulong Sequence { get; }
        public ulong MediaTimestamp100Ns { get; }
        public ArraySegment<float> Samples { get; }
    }

    internal sealed class VoiceRealtimeFrameScheduler
    {
        private const ulong TimeUnitsPerSecond = 10000000UL;
        private readonly float[] frameBuffer = new float[VoiceAudioContract.SamplesPerFrame];
        private int bufferedSampleCount;
        private ulong nextSequence;
        private ulong nextFrameTimestamp100Ns;
        private bool hasTimelineAnchor;
        private long totalSamplesPushed;
        private long totalFramesEmitted;
        private int discontinuityCount;

        public event Action<VoiceScheduledAudioFrame> FrameReady;

        public int BufferedSampleCount => bufferedSampleCount;
        public ulong NextSequence => nextSequence;
        public long TotalSamplesPushed => totalSamplesPushed;
        public long TotalFramesEmitted => totalFramesEmitted;
        public int DiscontinuityCount => discontinuityCount;
        public bool HasTimelineAnchor => hasTimelineAnchor;

        // این تابع وضعیت زمان بندی را برای شروع یک جریان صوتی جدید پاک می کند و شمارنده فریم را از ابتدا آغاز می کند.
        public void Reset()
        {
            bufferedSampleCount = 0;
            nextSequence = 0;
            nextFrameTimestamp100Ns = 0;
            hasTimelineAnchor = false;
            totalSamplesPushed = 0;
            totalFramesEmitted = 0;
            discontinuityCount = 0;
            Array.Clear(frameBuffer, 0, frameBuffer.Length);
        }

        // این تابع نمونه های استاندارد ۴۸ کیلوهرتز مونو Float32 را دریافت می کند و بدون وابستگی به Update یونیتی فریم های دقیق ۹۶۰ نمونه ای تولید می کند.
        public void Push(float[] source, int sourceOffset, int sampleCount, ulong firstSampleTimestamp100Ns, bool discontinuity)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (sourceOffset < 0 || sampleCount < 0 || sourceOffset + sampleCount > source.Length) throw new ArgumentOutOfRangeException(nameof(sourceOffset));
            if (sampleCount == 0) return;

            if (discontinuity)
            {
                bufferedSampleCount = 0;
                hasTimelineAnchor = false;
                discontinuityCount++;
            }

            if (!hasTimelineAnchor)
            {
                nextFrameTimestamp100Ns = firstSampleTimestamp100Ns;
                hasTimelineAnchor = true;
            }

            int readOffset = sourceOffset;
            int remaining = sampleCount;
            totalSamplesPushed += sampleCount;

            while (remaining > 0)
            {
                int writable = VoiceAudioContract.SamplesPerFrame - bufferedSampleCount;
                int copyCount = Math.Min(writable, remaining);
                Array.Copy(source, readOffset, frameBuffer, bufferedSampleCount, copyCount);
                bufferedSampleCount += copyCount;
                readOffset += copyCount;
                remaining -= copyCount;
                if (bufferedSampleCount == VoiceAudioContract.SamplesPerFrame) EmitFrame();
            }
        }

        // این تابع یک فریم کامل را با Sequence افزایشی و Timestamp دقیق ۲۰ میلی ثانیه منتشر می کند و همان بافر را برای فریم بعدی آماده نگه می دارد.
        private void EmitFrame()
        {
            VoiceScheduledAudioFrame frame = new VoiceScheduledAudioFrame(nextSequence, nextFrameTimestamp100Ns, new ArraySegment<float>(frameBuffer, 0, VoiceAudioContract.SamplesPerFrame));
            FrameReady?.Invoke(frame);
            nextSequence++;
            totalFramesEmitted++;
            nextFrameTimestamp100Ns += FrameDuration100Ns();
            bufferedSampleCount = 0;
        }

        // این تابع طول ثابت هر فریم قرارداد VME2 را از میلی ثانیه به واحد صد نانوثانیه تبدیل می کند تا Timestamp به ساعت رسانه وابسته بماند.
        private static ulong FrameDuration100Ns()
        {
            return checked((ulong)VoiceAudioContract.FrameDurationMs * TimeUnitsPerSecond / 1000UL);
        }
    }
}
