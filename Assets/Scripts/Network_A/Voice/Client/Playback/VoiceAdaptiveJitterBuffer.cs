using System;
using System.Collections.Generic;
using Network_A.Voice.Client.Audio;
using Network_A.Voice.Client.Codec;

namespace Network_A.Voice.Client.Playback
{
    internal enum VoiceJitterBufferReadStatus
    {
        NotReady = 0,
        FrameReady = 1,
        MissingFrame = 2
    }

    internal readonly struct VoiceJitterBufferReadResult
    {
        // این سازنده نتیجه هر تلاش برای خواندن بافر نوسان را همراه با شماره فریم، زمان رسانه و وضعیت همان فریم نگه می دارد.
        public VoiceJitterBufferReadResult(VoiceJitterBufferReadStatus status, ulong sequence, ulong mediaTimestamp100Ns, VoiceEncodedMediaFrame frame)
        {
            Status = status;
            Sequence = sequence;
            MediaTimestamp100Ns = mediaTimestamp100Ns;
            Frame = frame;
        }

        public VoiceJitterBufferReadStatus Status { get; }
        public ulong Sequence { get; }
        public ulong MediaTimestamp100Ns { get; }
        public VoiceEncodedMediaFrame Frame { get; }
    }

    internal readonly struct VoiceJitterBufferSnapshot
    {
        // این سازنده وضعیت قابل اندازه گیری بافر را در یک لحظه ثبت می کند تا رفتار تطبیقی آن بدون دست زدن به مسیر صوت اصلی بررسی شود.
        public VoiceJitterBufferSnapshot(int bufferedFrames, int targetDelayMs, double estimatedJitterMs, ulong expectedSequence, long acceptedFrames, long duplicateDrops, long lateDrops, long reorderedFrames, long missingFrames, long overflowDrops)
        {
            BufferedFrames = bufferedFrames;
            TargetDelayMs = targetDelayMs;
            EstimatedJitterMs = estimatedJitterMs;
            ExpectedSequence = expectedSequence;
            AcceptedFrames = acceptedFrames;
            DuplicateDrops = duplicateDrops;
            LateDrops = lateDrops;
            ReorderedFrames = reorderedFrames;
            MissingFrames = missingFrames;
            OverflowDrops = overflowDrops;
        }

        public int BufferedFrames { get; }
        public int TargetDelayMs { get; }
        public double EstimatedJitterMs { get; }
        public ulong ExpectedSequence { get; }
        public long AcceptedFrames { get; }
        public long DuplicateDrops { get; }
        public long LateDrops { get; }
        public long ReorderedFrames { get; }
        public long MissingFrames { get; }
        public long OverflowDrops { get; }
    }

    internal sealed class VoiceAdaptiveJitterBuffer
    {
        private const long TimeUnitsPerMillisecond = 10000L;
        private const long FrameDuration100Ns = (long)VoiceAudioContract.FrameDurationMs * TimeUnitsPerMillisecond;
        private const int MinimumTargetDelayMs = 40;
        private const int MaximumTargetDelayMs = 200;
        private const int MaximumBufferedFrames = 64;
        private const double JitterMultiplier = 4.0d;
        private readonly SortedDictionary<ulong, BufferedFrame> frames = new SortedDictionary<ulong, BufferedFrame>();
        private bool initialized;
        private ulong anchorSequence;
        private ulong anchorMediaTimestamp100Ns;
        private long anchorArrivalTimestamp100Ns;
        private ulong expectedSequence;
        private ulong highestReceivedSequence;
        private bool hasHighestReceivedSequence;
        private bool hasPreviousTransit;
        private double previousTransit100Ns;
        private double estimatedJitter100Ns;
        private int targetDelayMs = MinimumTargetDelayMs;
        private long acceptedFrames;
        private long duplicateDrops;
        private long lateDrops;
        private long reorderedFrames;
        private long missingFrames;
        private long overflowDrops;

        private readonly struct BufferedFrame
        {
            // این سازنده بسته فشرده شده و زمان رسیدن آن را کنار هم نگه می دارد تا ترتیب پخش از ترتیب رسیدن جدا بماند.
            public BufferedFrame(VoiceEncodedMediaFrame frame, long arrivalTimestamp100Ns)
            {
                Frame = frame;
                ArrivalTimestamp100Ns = arrivalTimestamp100Ns;
            }

            public VoiceEncodedMediaFrame Frame { get; }
            public long ArrivalTimestamp100Ns { get; }
        }

        // این تابع تمام وضعیت بافر، شمارنده ها و مبنای زمانی را پاک می کند تا یک جریان رسانه ای تازه از ابتدا آغاز شود.
        public void Reset()
        {
            frames.Clear();
            initialized = false;
            anchorSequence = 0;
            anchorMediaTimestamp100Ns = 0;
            anchorArrivalTimestamp100Ns = 0;
            expectedSequence = 0;
            highestReceivedSequence = 0;
            hasHighestReceivedSequence = false;
            hasPreviousTransit = false;
            previousTransit100Ns = 0d;
            estimatedJitter100Ns = 0d;
            targetDelayMs = MinimumTargetDelayMs;
            acceptedFrames = 0;
            duplicateDrops = 0;
            lateDrops = 0;
            reorderedFrames = 0;
            missingFrames = 0;
            overflowDrops = 0;
        }

        // این تابع یک بسته فشرده شده را با زمان رسیدن یکنواخت دریافت می کند، بسته های دیررس و تکراری را رد می کند و مقدار تاخیر هدف را بر پایه نوسان واقعی رسیدن به روز می کند.
        public bool Enqueue(VoiceEncodedMediaFrame frame, long arrivalTimestamp100Ns)
        {
            if (frame.Packet == null || frame.Packet.Length == 0) throw new ArgumentException("بسته فشرده شده ورودی خالی است.", nameof(frame));
            if (arrivalTimestamp100Ns < 0) throw new ArgumentOutOfRangeException(nameof(arrivalTimestamp100Ns));

            if (!initialized) Initialize(frame, arrivalTimestamp100Ns);
            if (frame.Sequence < expectedSequence)
            {
                lateDrops++;
                return false;
            }

            if (frames.ContainsKey(frame.Sequence))
            {
                duplicateDrops++;
                return false;
            }

            if (frames.Count >= MaximumBufferedFrames)
            {
                overflowDrops++;
                return false;
            }

            if (hasHighestReceivedSequence && frame.Sequence < highestReceivedSequence) reorderedFrames++;
            if (!hasHighestReceivedSequence || frame.Sequence > highestReceivedSequence)
            {
                highestReceivedSequence = frame.Sequence;
                hasHighestReceivedSequence = true;
            }

            UpdateJitter(frame.MediaTimestamp100Ns, arrivalTimestamp100Ns);
            frames.Add(frame.Sequence, new BufferedFrame(frame, arrivalTimestamp100Ns));
            acceptedFrames++;
            return true;
        }

        // این تابع فقط زمانی فریم بعدی را آزاد می کند که زمان پخش آن بر اساس تاخیر تطبیقی رسیده باشد و در صورت نبودن فریم مورد انتظار فقط گم شدن آن را گزارش می کند.
        public VoiceJitterBufferReadResult TryDequeue(long nowTimestamp100Ns)
        {
            if (!initialized || nowTimestamp100Ns < 0) return new VoiceJitterBufferReadResult(VoiceJitterBufferReadStatus.NotReady, expectedSequence, ExpectedMediaTimestamp100Ns(), default);
            long dueTimestamp100Ns = ExpectedDueTimestamp100Ns();
            if (nowTimestamp100Ns < dueTimestamp100Ns) return new VoiceJitterBufferReadResult(VoiceJitterBufferReadStatus.NotReady, expectedSequence, ExpectedMediaTimestamp100Ns(), default);

            if (frames.TryGetValue(expectedSequence, out BufferedFrame buffered))
            {
                frames.Remove(expectedSequence);
                VoiceJitterBufferReadResult result = new VoiceJitterBufferReadResult(VoiceJitterBufferReadStatus.FrameReady, expectedSequence, buffered.Frame.MediaTimestamp100Ns, buffered.Frame);
                expectedSequence++;
                return result;
            }

            if (hasHighestReceivedSequence && expectedSequence <= highestReceivedSequence)
            {
                ulong missingSequence = expectedSequence;
                ulong missingTimestamp100Ns = ExpectedMediaTimestamp100Ns();
                expectedSequence++;
                missingFrames++;
                return new VoiceJitterBufferReadResult(VoiceJitterBufferReadStatus.MissingFrame, missingSequence, missingTimestamp100Ns, default);
            }

            return new VoiceJitterBufferReadResult(VoiceJitterBufferReadStatus.NotReady, expectedSequence, ExpectedMediaTimestamp100Ns(), default);
        }

        // این تابع یک نمای فقط خواندنی از تاخیر هدف، نوسان برآوردشده، تعداد فریم های نگه داشته شده و شمارنده های خطا برمی گرداند.
        public VoiceJitterBufferSnapshot GetSnapshot()
        {
            return new VoiceJitterBufferSnapshot(frames.Count, targetDelayMs, estimatedJitter100Ns / TimeUnitsPerMillisecond, expectedSequence, acceptedFrames, duplicateDrops, lateDrops, reorderedFrames, missingFrames, overflowDrops);
        }

        // این تابع نخستین بسته معتبر را مبنای شماره فریم، زمان رسانه و زمان رسیدن قرار می دهد تا خط زمانی پخش مستقل از ساعت دیواری ساخته شود.
        private void Initialize(VoiceEncodedMediaFrame frame, long arrivalTimestamp100Ns)
        {
            initialized = true;
            anchorSequence = frame.Sequence;
            anchorMediaTimestamp100Ns = frame.MediaTimestamp100Ns;
            anchorArrivalTimestamp100Ns = arrivalTimestamp100Ns;
            expectedSequence = frame.Sequence;
            highestReceivedSequence = frame.Sequence;
            hasHighestReceivedSequence = true;
        }

        // این تابع تغییر زمان عبور بسته های پیاپی را با میانگین نرم شونده اندازه می گیرد و از روی آن تاخیر هدف بافر را در محدوده امن افزایش یا کاهش می دهد.
        private void UpdateJitter(ulong mediaTimestamp100Ns, long arrivalTimestamp100Ns)
        {
            double transit100Ns = arrivalTimestamp100Ns - (double)mediaTimestamp100Ns;
            if (hasPreviousTransit)
            {
                double delta100Ns = Math.Abs(transit100Ns - previousTransit100Ns);
                estimatedJitter100Ns += (delta100Ns - estimatedJitter100Ns) / 16d;
            }

            previousTransit100Ns = transit100Ns;
            hasPreviousTransit = true;
            double rawTargetMs = MinimumTargetDelayMs + JitterMultiplier * (estimatedJitter100Ns / TimeUnitsPerMillisecond);
            int roundedTargetMs = RoundUpToFrameDuration((int)Math.Ceiling(rawTargetMs));
            targetDelayMs = Math.Max(MinimumTargetDelayMs, Math.Min(MaximumTargetDelayMs, roundedTargetMs));
        }

        // این تابع تاخیر هدف را رو به بالا به مضرب کامل طول هر فریم گرد می کند تا مرزهای پخش همواره با فریم های بیست میلی ثانیه ای هم راستا بمانند.
        private static int RoundUpToFrameDuration(int delayMs)
        {
            int frameMs = VoiceAudioContract.FrameDurationMs;
            return ((delayMs + frameMs - 1) / frameMs) * frameMs;
        }

        // این تابع زمان پخش مورد انتظار فریم بعدی را از مبنای رسیدن نخستین بسته، فاصله شماره فریم و تاخیر هدف فعلی محاسبه می کند.
        private long ExpectedDueTimestamp100Ns()
        {
            ulong sequenceDistance = expectedSequence - anchorSequence;
            long mediaDistance100Ns = checked((long)sequenceDistance * FrameDuration100Ns);
            return checked(anchorArrivalTimestamp100Ns + mediaDistance100Ns + (long)targetDelayMs * TimeUnitsPerMillisecond);
        }

        // این تابع زمان رسانه فریم مورد انتظار را حتی در صورت گم شدن همان فریم از روی مبنای خط زمانی محاسبه می کند.
        private ulong ExpectedMediaTimestamp100Ns()
        {
            if (!initialized) return 0;
            ulong sequenceDistance = expectedSequence - anchorSequence;
            return checked(anchorMediaTimestamp100Ns + sequenceDistance * (ulong)FrameDuration100Ns);
        }
    }
}
