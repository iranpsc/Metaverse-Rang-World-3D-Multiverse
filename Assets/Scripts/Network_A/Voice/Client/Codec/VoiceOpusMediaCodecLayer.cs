using System;
using Network_A.Voice.Client.Audio;

namespace Network_A.Voice.Client.Codec
{
    internal readonly struct VoiceEncodedMediaFrame
    {
        // این سازنده شماره فریم، زمان رسانه و بسته فشرده شده اوپوس را کنار هم نگه می دارد تا هویت زمانی فریم هنگام کدگذاری از بین نرود.
        public VoiceEncodedMediaFrame(ulong sequence, ulong mediaTimestamp100Ns, byte[] packet)
        {
            Sequence = sequence;
            MediaTimestamp100Ns = mediaTimestamp100Ns;
            Packet = packet ?? throw new ArgumentNullException(nameof(packet));
        }

        public ulong Sequence { get; }
        public ulong MediaTimestamp100Ns { get; }
        public byte[] Packet { get; }
    }

    internal readonly struct VoiceDecodedMediaFrame
    {
        // این سازنده شماره فریم، زمان رسانه و نمونه های بازشده را کنار هم نگه می دارد تا نتیجه بازکردن بسته با همان فریم ورودی قابل تطبیق باشد.
        public VoiceDecodedMediaFrame(ulong sequence, ulong mediaTimestamp100Ns, float[] samples)
        {
            Sequence = sequence;
            MediaTimestamp100Ns = mediaTimestamp100Ns;
            Samples = samples ?? throw new ArgumentNullException(nameof(samples));
        }

        public ulong Sequence { get; }
        public ulong MediaTimestamp100Ns { get; }
        public float[] Samples { get; }
    }

    internal interface IVoiceMediaCodec : IDisposable
    {
        // این تابع یک فریم استاندارد بیست میلی ثانیه ای را به بسته فشرده شده رسانه تبدیل می کند و زمان و شماره فریم را حفظ می کند.
        VoiceEncodedMediaFrame Encode(VoiceScheduledAudioFrame frame);

        // این تابع یک بسته فشرده شده رسانه را باز می کند و نتیجه را با همان شماره و زمان فریم بازمی گرداند.
        VoiceDecodedMediaFrame Decode(VoiceEncodedMediaFrame frame);
    }

    internal sealed class VoiceOpusMediaCodecLayer : IVoiceMediaCodec
    {
        private readonly VoiceNativeOpusCodec codec;
        private readonly float[] encodeBuffer = new float[VoiceAudioContract.SamplesPerFrame];
        private bool disposed;

        public int BitrateKbps { get; }

        // این سازنده از کدک اوپوس سالم و موجود پروژه استفاده می کند و فقط لایه مستقل رسانه را روی آن قرار می دهد؛ هیچ تنظیمی از کدک قبلی تغییر داده نمی شود.
        public VoiceOpusMediaCodecLayer(int bitrateKbps)
        {
            BitrateKbps = bitrateKbps;
            codec = new VoiceNativeOpusCodec(bitrateKbps);
        }

        // این تابع دقیقا ۹۶۰ نمونه استاندارد فاز ۵ را به کدک موجود تحویل می دهد و بسته حاصل را با شماره و زمان همان فریم برمی گرداند.
        public VoiceEncodedMediaFrame Encode(VoiceScheduledAudioFrame frame)
        {
            ThrowIfDisposed();
            if (frame.Samples.Array == null || frame.Samples.Count != VoiceAudioContract.SamplesPerFrame) throw new ArgumentException("فریم ورودی باید دقیقا ۹۶۰ نمونه استاندارد داشته باشد.", nameof(frame));
            Array.Copy(frame.Samples.Array, frame.Samples.Offset, encodeBuffer, 0, VoiceAudioContract.SamplesPerFrame);
            byte[] packet = codec.Encode(encodeBuffer);
            if (packet == null || packet.Length == 0) throw new InvalidOperationException("کدک اوپوس بسته معتبری تولید نکرد.");
            return new VoiceEncodedMediaFrame(frame.Sequence, frame.MediaTimestamp100Ns, packet);
        }

        // این تابع بسته اوپوس را با کدک موجود باز می کند و فقط در صورتی نتیجه می دهد که تعداد نمونه های خروجی با قرارداد بیست میلی ثانیه ای برابر باشد.
        public VoiceDecodedMediaFrame Decode(VoiceEncodedMediaFrame frame)
        {
            ThrowIfDisposed();
            if (frame.Packet == null || frame.Packet.Length == 0) throw new ArgumentException("بسته ورودی خالی است.", nameof(frame));
            float[] samples = codec.Decode(frame.Packet);
            if (samples == null || samples.Length != VoiceAudioContract.SamplesPerFrame) throw new InvalidOperationException("تعداد نمونه های بازشده با قرارداد فریم صوتی برابر نیست.");
            return new VoiceDecodedMediaFrame(frame.Sequence, frame.MediaTimestamp100Ns, samples);
        }

        // این تابع استفاده از نمونه آزادشده را متوقف می کند تا پس از آزادسازی منابع بومی اوپوس هیچ عملیات دیگری روی آن انجام نشود.
        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(VoiceOpusMediaCodecLayer));
        }

        // این تابع منابع کدگذار و بازکننده اوپوس را فقط یک بار آزاد می کند.
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            codec.Dispose();
        }
    }
}
