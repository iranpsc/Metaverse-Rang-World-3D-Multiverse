using System;

namespace Network_A.Voice.Client.Audio
{
    internal sealed class VoiceStreamingSincResampler
    {
        private const int TapCount = 32;
        private const int HalfTapCount = TapCount / 2;
        private const double CutoffMargin = 0.94;

        private readonly int sourceSampleRate;
        private readonly int targetSampleRate;
        private readonly double sourceStepPerOutput;
        private readonly double cutoff;

        private float[] sampleBuffer = new float[4096];
        private int bufferedSampleCount;
        private double sourcePosition;

        // این سازنده نرخ نمونه برداری مبدا و مقصد را دریافت می کند و وضعیت Resampler پیوسته را برای پردازش بسته های پشت سرهم آماده می کند.
        public VoiceStreamingSincResampler(int sourceSampleRate, int targetSampleRate)
        {
            if (sourceSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceSampleRate));
            if (targetSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(targetSampleRate));
            this.sourceSampleRate = sourceSampleRate;
            this.targetSampleRate = targetSampleRate;
            sourceStepPerOutput = (double)sourceSampleRate / targetSampleRate;
            cutoff = Math.Min(1.0, (double)targetSampleRate / sourceSampleRate) * CutoffMargin;
            Reset();
        }

        // این تابع وضعیت داخلی Resampler را پاک می کند تا یک جریان صوتی جدید بدون باقی مانده جریان قبلی آغاز شود.
        public void Reset()
        {
            EnsureBufferCapacity(HalfTapCount + 1);
            Array.Clear(sampleBuffer, 0, HalfTapCount);
            bufferedSampleCount = HalfTapCount;
            sourcePosition = HalfTapCount;
        }

        // این تابع حداکثر فضای خروجی مورد نیاز برای تعداد مشخصی نمونه ورودی را محاسبه می کند تا پردازش بدون حدس اندازه بافر انجام شود.
        public int GetMaximumOutputSampleCount(int inputSampleCount)
        {
            if (inputSampleCount < 0) throw new ArgumentOutOfRangeException(nameof(inputSampleCount));
            return (int)Math.Ceiling((inputSampleCount + TapCount + 2) * (double)targetSampleRate / sourceSampleRate) + 4;
        }

        // این تابع نمونه های مونو را به صورت پیوسته Resample می کند و وضعیت مرز بسته ها را برای بسته بعدی نگه می دارد.
        public int Process(float[] source, int sourceOffset, int sourceSampleCount, float[] destination, int destinationOffset, int destinationCapacity)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (sourceOffset < 0 || sourceSampleCount < 0 || sourceOffset + sourceSampleCount > source.Length) throw new ArgumentOutOfRangeException(nameof(sourceOffset));
            if (destinationOffset < 0 || destinationCapacity < 0 || destinationOffset + destinationCapacity > destination.Length) throw new ArgumentOutOfRangeException(nameof(destinationOffset));

            AppendSamples(source, sourceOffset, sourceSampleCount);
            int written = 0;

            while (written < destinationCapacity && HasEnoughSamplesForNextOutput())
            {
                destination[destinationOffset + written] = InterpolateAt(sourcePosition);
                sourcePosition += sourceStepPerOutput;
                written++;
            }

            DiscardConsumedHistory();
            return written;
        }

        // این تابع نمونه های ورودی جدید را به بافر پیوسته اضافه می کند تا Resampler مرز بسته های شبکه یا سخت افزار را به عنوان قطع صدا در نظر نگیرد.
        private void AppendSamples(float[] source, int sourceOffset, int sourceSampleCount)
        {
            if (sourceSampleCount == 0) return;
            EnsureBufferCapacity(bufferedSampleCount + sourceSampleCount);
            Array.Copy(source, sourceOffset, sampleBuffer, bufferedSampleCount, sourceSampleCount);
            bufferedSampleCount += sourceSampleCount;
        }

        // این تابع بررسی می کند برای محاسبه نمونه خروجی بعدی، نمونه های کافی در سمت گذشته و آینده نقطه فعلی وجود داشته باشد.
        private bool HasEnoughSamplesForNextOutput()
        {
            int center = (int)Math.Floor(sourcePosition);
            return center - (HalfTapCount - 1) >= 0 && center + HalfTapCount < bufferedSampleCount;
        }

        // این تابع مقدار یک نمونه خروجی را با فیلتر Sinc پنجره دار محاسبه می کند تا تبدیل نرخ نمونه برداری برای افزایش و کاهش نرخ قابل استفاده باشد.
        private float InterpolateAt(double position)
        {
            int center = (int)Math.Floor(position);
            double weightedSum = 0.0;
            double weightSum = 0.0;

            for (int tap = -(HalfTapCount - 1); tap <= HalfTapCount; tap++)
            {
                int sampleIndex = center + tap;
                double distance = sampleIndex - position;
                double weight = cutoff * Sinc(cutoff * distance) * BlackmanWindow(distance / HalfTapCount);
                weightedSum += sampleBuffer[sampleIndex] * weight;
                weightSum += weight;
            }

            if (Math.Abs(weightSum) < 1e-12) return 0f;
            return (float)(weightedSum / weightSum);
        }

        // این تابع مقدار تابع Sinc نرمال شده را برای ساخت هسته فیلتر تبدیل نرخ نمونه برداری محاسبه می کند.
        private static double Sinc(double value)
        {
            if (Math.Abs(value) < 1e-12) return 1.0;
            double radians = Math.PI * value;
            return Math.Sin(radians) / radians;
        }

        // این تابع پنجره Blackman را روی هسته فیلتر اعمال می کند تا برش ناگهانی ضرایب باعث اعوجاج شدید در خروجی نشود.
        private static double BlackmanWindow(double normalizedDistance)
        {
            double absolute = Math.Abs(normalizedDistance);
            if (absolute >= 1.0) return 0.0;
            return 0.42 + 0.5 * Math.Cos(Math.PI * normalizedDistance) + 0.08 * Math.Cos(2.0 * Math.PI * normalizedDistance);
        }

        // این تابع نمونه های قدیمی که دیگر در محاسبات آینده استفاده نمی شوند را حذف می کند و فقط تاریخچه لازم فیلتر را نگه می دارد.
        private void DiscardConsumedHistory()
        {
            int discardCount = (int)Math.Floor(sourcePosition) - HalfTapCount;
            if (discardCount <= 0) return;
            int remaining = bufferedSampleCount - discardCount;
            if (remaining > 0) Array.Copy(sampleBuffer, discardCount, sampleBuffer, 0, remaining);
            bufferedSampleCount = Math.Max(0, remaining);
            sourcePosition -= discardCount;
        }

        // این تابع فقط در صورت نیاز ظرفیت بافر داخلی را بزرگ می کند تا هنگام پردازش معمولی تخصیص حافظه تکراری انجام نشود.
        private void EnsureBufferCapacity(int requiredCapacity)
        {
            if (requiredCapacity <= sampleBuffer.Length) return;
            int newCapacity = sampleBuffer.Length;
            while (newCapacity < requiredCapacity) newCapacity *= 2;
            Array.Resize(ref sampleBuffer, newCapacity);
        }
    }
}
