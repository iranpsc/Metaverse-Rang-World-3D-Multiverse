using System;
using System.Threading;

namespace Network_A.Voice.Client.Runtime
{
    public sealed class VoiceInboundMediaFreshnessGuard
    {
        private const long UninitializedClockOffset = long.MaxValue;

        private readonly ulong maximumMediaAgeMs;
        private long minimumServerClockOffsetMs = UninitializedClockOffset;

        public VoiceInboundMediaFreshnessGuard(ulong maximumMediaAgeMs)
        {
            if (maximumMediaAgeMs == 0)
                throw new ArgumentOutOfRangeException(nameof(maximumMediaAgeMs));

            this.maximumMediaAgeMs = maximumMediaAgeMs;
        }

        public ulong MaximumMediaAgeMs { get { return maximumMediaAgeMs; } }

        //* این تابع کمترین اختلاف مشاهده‌شده بین ساعت محلی و Timestamp پیام کنترل سرور را نگه می‌دارد.
        public void ObserveServerControlTimestamp(
            ulong serverTimestampMs,
            ulong localReceivedAtMs)
        {
            if (serverTimestampMs == 0) return;

            long observedOffsetMs;
            if (!TrySubtractToInt64(
                    localReceivedAtMs,
                    serverTimestampMs,
                    out observedOffsetMs))
            {
                return;
            }

            while (true)
            {
                long current = Interlocked.Read(ref minimumServerClockOffsetMs);
                if (current != UninitializedClockOffset && observedOffsetMs >= current) return;

                if (Interlocked.CompareExchange(
                        ref minimumServerClockOffsetMs,
                        observedOffsetMs,
                        current) == current)
                {
                    return;
                }
            }
        }

        //* این تابع عمر انتقال فریم را در دامنه ساعت محلی تخمین می‌زند و فریم قدیمی را رد می‌کند.
        public bool IsFresh(
            ulong serverRoutedAtMs,
            ulong localReceivedAtMs,
            out ulong estimatedAgeMs)
        {
            estimatedAgeMs = 0;
            long offsetMs = Interlocked.Read(ref minimumServerClockOffsetMs);

            if (offsetMs == UninitializedClockOffset ||
                serverRoutedAtMs == 0 ||
                serverRoutedAtMs > (ulong)long.MaxValue ||
                localReceivedAtMs > (ulong)long.MaxValue)
            {
                return true;
            }

            long adjustedServerTimestampMs;
            try
            {
                adjustedServerTimestampMs = checked((long)serverRoutedAtMs + offsetMs);
            }
            catch (OverflowException)
            {
                return true;
            }

            long localTimestampMs = (long)localReceivedAtMs;
            if (localTimestampMs <= adjustedServerTimestampMs) return true;

            estimatedAgeMs = (ulong)(localTimestampMs - adjustedServerTimestampMs);
            return estimatedAgeMs <= maximumMediaAgeMs;
        }

        public void Reset()
        {
            Interlocked.Exchange(
                ref minimumServerClockOffsetMs,
                UninitializedClockOffset);
        }

        private static bool TrySubtractToInt64(
            ulong left,
            ulong right,
            out long result)
        {
            if (left >= right)
            {
                ulong difference = left - right;
                if (difference > (ulong)long.MaxValue)
                {
                    result = 0;
                    return false;
                }

                result = (long)difference;
                return true;
            }

            ulong negativeDifference = right - left;
            if (negativeDifference > (ulong)long.MaxValue)
            {
                result = 0;
                return false;
            }

            result = -(long)negativeDifference;
            return true;
        }
    }
}

/*
توضیح فایل:
این فایل بدون وابستگی به ساعت یکسان کلاینت و سرور، عمر واقعی فریم رسانه را از Timestamp زمان Route سرور محاسبه می‌کند تا فریم مانده در Backlog پیش از Decode و Playback حذف شود.
*/
