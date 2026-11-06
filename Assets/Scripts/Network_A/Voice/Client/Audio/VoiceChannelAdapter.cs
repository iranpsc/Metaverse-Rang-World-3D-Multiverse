using System;

namespace Network_A.Voice.Client.Audio
{
    internal static class VoiceChannelAdapter
    {
        // این تابع تعداد فریم های صوتی را از روی تعداد نمونه های درهم تنیده و تعداد کانال ورودی محاسبه می کند.
        public static int GetFrameCount(int interleavedSampleCount, int sourceChannels)
        {
            if (sourceChannels <= 0) throw new ArgumentOutOfRangeException(nameof(sourceChannels));
            if (interleavedSampleCount < 0) throw new ArgumentOutOfRangeException(nameof(interleavedSampleCount));
            if (interleavedSampleCount % sourceChannels != 0) throw new ArgumentException("Interleaved sample count must be divisible by source channel count.", nameof(interleavedSampleCount));
            return interleavedSampleCount / sourceChannels;
        }

        // این تابع هر تعداد کانال ورودی را به یک کانال مونو تبدیل می کند و هیچ فرض ثابتی درباره یک یا دو کاناله بودن دستگاه ندارد.
        public static int ConvertInterleavedToMono(float[] source, int sourceOffset, int sourceSampleCount, int sourceChannels, float[] destination, int destinationOffset)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (sourceOffset < 0 || sourceSampleCount < 0 || sourceOffset + sourceSampleCount > source.Length) throw new ArgumentOutOfRangeException(nameof(sourceOffset));
            if (destinationOffset < 0 || destinationOffset > destination.Length) throw new ArgumentOutOfRangeException(nameof(destinationOffset));

            int frameCount = GetFrameCount(sourceSampleCount, sourceChannels);
            if (destinationOffset + frameCount > destination.Length) throw new ArgumentException("Destination buffer is too small.", nameof(destination));

            if (sourceChannels == 1)
            {
                Array.Copy(source, sourceOffset, destination, destinationOffset, frameCount);
                return frameCount;
            }

            int readIndex = sourceOffset;
            for (int frame = 0; frame < frameCount; frame++)
            {
                double sum = 0.0;
                for (int channel = 0; channel < sourceChannels; channel++) sum += source[readIndex++];
                destination[destinationOffset + frame] = (float)(sum / sourceChannels);
            }

            return frameCount;
        }
    }
}
