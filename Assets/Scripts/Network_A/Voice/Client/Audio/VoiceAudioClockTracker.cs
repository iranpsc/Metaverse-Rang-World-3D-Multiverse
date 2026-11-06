using System;

namespace Network_A.Voice.Client.Audio
{
    internal readonly struct VoiceAudioClockObservation
    {
        // این سازنده نتیجه یک اندازه گیری ساعت صوتی را بدون وابستگی به ویندوز، یونیتی یا مسیر اصلی صدا ذخیره می کند.
        public VoiceAudioClockObservation(bool isValid, bool hasDriftMeasurement, bool discontinuityDetected, ulong devicePositionFrames, ulong monotonicTimestamp100Ns, ulong mediaTimestamp100Ns, double observedSampleRate, double driftPpm)
        {
            IsValid = isValid;
            HasDriftMeasurement = hasDriftMeasurement;
            DiscontinuityDetected = discontinuityDetected;
            DevicePositionFrames = devicePositionFrames;
            MonotonicTimestamp100Ns = monotonicTimestamp100Ns;
            MediaTimestamp100Ns = mediaTimestamp100Ns;
            ObservedSampleRate = observedSampleRate;
            DriftPpm = driftPpm;
        }

        public bool IsValid { get; }
        public bool HasDriftMeasurement { get; }
        public bool DiscontinuityDetected { get; }
        public ulong DevicePositionFrames { get; }
        public ulong MonotonicTimestamp100Ns { get; }
        public ulong MediaTimestamp100Ns { get; }
        public double ObservedSampleRate { get; }
        public double DriftPpm { get; }
    }

    internal sealed class VoiceAudioClockTracker
    {
        private const ulong TimeUnitsPerSecond = 10000000UL;
        private readonly int nominalSampleRate;
        private bool hasAnchor;
        private ulong anchorDevicePositionFrames;
        private ulong anchorTimestamp100Ns;
        private ulong previousDevicePositionFrames;
        private ulong previousTimestamp100Ns;

        public int NominalSampleRate => nominalSampleRate;
        public bool HasAnchor => hasAnchor;

        // این سازنده نرخ نمونه برداری واقعی جریان ورودی را دریافت می کند تا ساعت سخت افزاری همان جریان با زمان یکنواخت مقایسه شود.
        public VoiceAudioClockTracker(int nominalSampleRate)
        {
            if (nominalSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(nominalSampleRate));
            this.nominalSampleRate = nominalSampleRate;
        }

        // این تابع یک موقعیت فریم و timestamp همبسته را دریافت می کند، زمان رسانه را می سازد و اختلاف سرعت ساعت دستگاه را بر حسب ppm محاسبه می کند.
        public VoiceAudioClockObservation Observe(ulong devicePositionFrames, ulong monotonicTimestamp100Ns, bool timestampValid, bool discontinuitySignaled)
        {
            ulong mediaTimestamp100Ns = FramesTo100Ns(devicePositionFrames, nominalSampleRate);
            if (!timestampValid)
            {
                Reset();
                return new VoiceAudioClockObservation(false, false, true, devicePositionFrames, monotonicTimestamp100Ns, mediaTimestamp100Ns, 0d, 0d);
            }

            bool regressed = hasAnchor && (devicePositionFrames < previousDevicePositionFrames || monotonicTimestamp100Ns <= previousTimestamp100Ns);
            if (!hasAnchor || discontinuitySignaled || regressed)
            {
                SetAnchor(devicePositionFrames, monotonicTimestamp100Ns);
                return new VoiceAudioClockObservation(true, false, discontinuitySignaled || regressed, devicePositionFrames, monotonicTimestamp100Ns, mediaTimestamp100Ns, nominalSampleRate, 0d);
            }

            previousDevicePositionFrames = devicePositionFrames;
            previousTimestamp100Ns = monotonicTimestamp100Ns;
            ulong deltaFrames = devicePositionFrames - anchorDevicePositionFrames;
            ulong deltaTime100Ns = monotonicTimestamp100Ns - anchorTimestamp100Ns;
            if (deltaFrames == 0 || deltaTime100Ns == 0) return new VoiceAudioClockObservation(true, false, false, devicePositionFrames, monotonicTimestamp100Ns, mediaTimestamp100Ns, nominalSampleRate, 0d);

            double elapsedSeconds = deltaTime100Ns / (double)TimeUnitsPerSecond;
            double observedSampleRate = deltaFrames / elapsedSeconds;
            double driftPpm = ((observedSampleRate / nominalSampleRate) - 1d) * 1000000d;
            return new VoiceAudioClockObservation(true, true, false, devicePositionFrames, monotonicTimestamp100Ns, mediaTimestamp100Ns, observedSampleRate, driftPpm);
        }

        // این تابع مبنای ساعت را پاک می کند تا جریان بعدی یا timestamp نامعتبر از اندازه گیری قبلی تاثیر نگیرد.
        public void Reset()
        {
            hasAnchor = false;
            anchorDevicePositionFrames = 0;
            anchorTimestamp100Ns = 0;
            previousDevicePositionFrames = 0;
            previousTimestamp100Ns = 0;
        }

        // این تابع نقطه شروع جدید ساعت را ثبت می کند تا تمام اندازه گیری های بعدی نسبت به یک مبنای مشخص انجام شوند.
        private void SetAnchor(ulong devicePositionFrames, ulong monotonicTimestamp100Ns)
        {
            hasAnchor = true;
            anchorDevicePositionFrames = devicePositionFrames;
            anchorTimestamp100Ns = monotonicTimestamp100Ns;
            previousDevicePositionFrames = devicePositionFrames;
            previousTimestamp100Ns = monotonicTimestamp100Ns;
        }

        // این تابع موقعیت فریم را بدون ضرب بزرگ و خطر سرریز به زمان رسانه در واحد صد نانوثانیه تبدیل می کند.
        private static ulong FramesTo100Ns(ulong frames, int sampleRate)
        {
            ulong rate = (ulong)sampleRate;
            ulong wholeSeconds = frames / rate;
            ulong remainderFrames = frames % rate;
            return checked((wholeSeconds * TimeUnitsPerSecond) + ((remainderFrames * TimeUnitsPerSecond) / rate));
        }
    }
}
