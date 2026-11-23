using System;

namespace Network_A.Voice.Client.Audio
{
    internal sealed class VoiceAudioFormatAdapter
    {
        private readonly int sourceSampleRate;
        private readonly int sourceChannels;
        private readonly VoiceStreamingSincResampler resampler;
        private float[] monoScratch = new float[2048];

        public int SourceSampleRate => sourceSampleRate;
        public int SourceChannels => sourceChannels;
        public int TargetSampleRate => VoiceAudioContract.SampleRate;
        public int TargetChannels => VoiceAudioContract.Channels;
        public bool RequiresResampling => sourceSampleRate != VoiceAudioContract.SampleRate;
        public bool RequiresChannelAdaptation => sourceChannels != VoiceAudioContract.Channels;

        // این سازنده مشخصات واقعی دستگاه مبدا را دریافت می کند و مسیر تبدیل عمومی به قرارداد صوتی VME2 را آماده می کند.
        public VoiceAudioFormatAdapter(int sourceSampleRate, int sourceChannels)
        {
            if (sourceSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceSampleRate));
            if (sourceChannels <= 0) throw new ArgumentOutOfRangeException(nameof(sourceChannels));
            this.sourceSampleRate = sourceSampleRate;
            this.sourceChannels = sourceChannels;
            if (RequiresResampling) resampler = new VoiceStreamingSincResampler(sourceSampleRate, VoiceAudioContract.SampleRate);
        }

        // این تابع وضعیت پردازش پیوسته را برای شروع یک جریان صوتی جدید پاک می کند و هیچ تنظیمی از دستگاه قبلی را به جریان بعدی منتقل نمی کند.
        public void Reset()
        {
            resampler?.Reset();
        }

        // این تابع حداکثر تعداد نمونه مونو خروجی را برای تعداد فریم ورودی محاسبه می کند تا فراخواننده بتواند بافر کافی از قبل آماده کند.
        public int GetMaximumOutputSampleCount(int inputFrameCount)
        {
            if (inputFrameCount < 0) throw new ArgumentOutOfRangeException(nameof(inputFrameCount));
            if (!RequiresResampling) return inputFrameCount;
            return resampler.GetMaximumOutputSampleCount(inputFrameCount);
        }

        // این تابع صوت Float32 درهم تنیده با هر تعداد کانال و نرخ نمونه برداری را به خروجی ثابت ۴۸ کیلوهرتز مونو Float32 قرارداد VME2 تبدیل می کند.
        public int ProcessInterleavedFloat(float[] source, int sourceOffset, int sourceSampleCount, float[] destination, int destinationOffset, int destinationCapacity)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            int frameCount = VoiceChannelAdapter.GetFrameCount(sourceSampleCount, sourceChannels);
            EnsureMonoScratchCapacity(frameCount);
            int monoCount = VoiceChannelAdapter.ConvertInterleavedToMono(source, sourceOffset, sourceSampleCount, sourceChannels, monoScratch, 0);

            if (!RequiresResampling)
            {
                if (destinationCapacity < monoCount || destinationOffset < 0 || destinationOffset + monoCount > destination.Length) throw new ArgumentException("Destination buffer is too small.", nameof(destination));
                Array.Copy(monoScratch, 0, destination, destinationOffset, monoCount);
                return monoCount;
            }

            return resampler.Process(monoScratch, 0, monoCount, destination, destinationOffset, destinationCapacity);
        }

        // این تابع ظرفیت بافر موقت مونو را فقط در صورت نیاز افزایش می دهد تا تبدیل کانال در اجرای عادی باعث تخصیص حافظه مداوم نشود.
        private void EnsureMonoScratchCapacity(int requiredCapacity)
        {
            if (requiredCapacity <= monoScratch.Length) return;
            int newCapacity = monoScratch.Length;
            while (newCapacity < requiredCapacity) newCapacity *= 2;
            Array.Resize(ref monoScratch, newCapacity);
        }
    }
}
