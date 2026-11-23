using System;
using Network_A.Voice.Client.Audio;

namespace Network_A.Voice.Client.Codec
{
    internal enum VoicePacketRecoveryKind
    {
        Normal = 0,
        ForwardErrorCorrection = 1,
        PacketLossConcealment = 2
    }

    internal readonly struct VoiceRecoveredMediaFrame
    {
        // این سازنده شماره فریم، زمان رسانه، نمونه های بازشده و روش بازیابی را کنار هم نگه می دارد تا نتیجه هر مسیر بازیابی قابل اندازه گیری باشد.
        public VoiceRecoveredMediaFrame(ulong sequence, ulong mediaTimestamp100Ns, float[] samples, VoicePacketRecoveryKind recoveryKind)
        {
            Sequence = sequence;
            MediaTimestamp100Ns = mediaTimestamp100Ns;
            Samples = samples ?? throw new ArgumentNullException(nameof(samples));
            RecoveryKind = recoveryKind;
        }

        public ulong Sequence { get; }
        public ulong MediaTimestamp100Ns { get; }
        public float[] Samples { get; }
        public VoicePacketRecoveryKind RecoveryKind { get; }
    }

    internal sealed class VoiceOpusPacketLossRecoveryCodec : IDisposable
    {
        private const int OpusApplicationVoip = 2048;
        private const int SetBitrateRequest = 4002;
        private const int SetVbrRequest = 4006;
        private const int SetInbandFecRequest = 4012;
        private const int SetPacketLossPercentRequest = 4014;
        private const int SetDtxRequest = 4016;
        private IntPtr encoder;
        private IntPtr decoder;
        private bool disposed;

        public int BitrateKbps { get; }
        public int PacketLossPercent { get; }

        // این سازنده یک کدگذار و بازکننده مستقل اوپوس می سازد و اصلاح خطای درون بسته را بدون تغییر کدک سالم قبلی فعال می کند.
        public VoiceOpusPacketLossRecoveryCodec(int bitrateKbps, int packetLossPercent)
        {
            if (bitrateKbps != 28 && bitrateKbps != 32 && bitrateKbps != 40) throw new ArgumentOutOfRangeException(nameof(bitrateKbps));
            if (packetLossPercent < 0 || packetLossPercent > 100) throw new ArgumentOutOfRangeException(nameof(packetLossPercent));
            BitrateKbps = bitrateKbps;
            PacketLossPercent = packetLossPercent;
            CreateEncoder();
            CreateDecoder();
        }

        // این تابع یک فریم استاندارد را با اصلاح خطای درون بسته فعال فشرده می کند و شماره فریم و زمان رسانه را بدون تغییر نگه می دارد.
        public VoiceEncodedMediaFrame Encode(VoiceScheduledAudioFrame frame)
        {
            ThrowIfDisposed();
            if (frame.Samples.Array == null || frame.Samples.Count != VoiceAudioContract.SamplesPerFrame) throw new ArgumentException("فریم ورودی باید دقیقا ۹۶۰ نمونه داشته باشد.", nameof(frame));
            float[] pcm = new float[VoiceAudioContract.SamplesPerFrame];
            Array.Copy(frame.Samples.Array, frame.Samples.Offset, pcm, 0, VoiceAudioContract.SamplesPerFrame);
            byte[] output = new byte[4096];
            int length = VoiceOpusNative.EncodeFloat(encoder, pcm, VoiceAudioContract.SamplesPerFrame, output, output.Length);
            RequireSuccess(length, "فشرده سازی اوپوس");
            byte[] packet = new byte[length];
            Buffer.BlockCopy(output, 0, packet, 0, length);
            return new VoiceEncodedMediaFrame(frame.Sequence, frame.MediaTimestamp100Ns, packet);
        }

        // این تابع یک بسته سالم را به روش عادی باز می کند و نتیجه را با همان شماره و زمان فریم برمی گرداند.
        public VoiceRecoveredMediaFrame DecodeNormal(VoiceEncodedMediaFrame frame)
        {
            ThrowIfDisposed();
            if (frame.Packet == null || frame.Packet.Length == 0) throw new ArgumentException("بسته ورودی خالی است.", nameof(frame));
            float[] samples = DecodePacket(frame.Packet, false);
            return new VoiceRecoveredMediaFrame(frame.Sequence, frame.MediaTimestamp100Ns, samples, VoicePacketRecoveryKind.Normal);
        }

        // این تابع فریم گم شده قبلی را از اطلاعات اصلاح خطای موجود در بسته بعدی بازیابی می کند و شماره و زمان فریم گم شده را روی نتیجه قرار می دهد.
        public VoiceRecoveredMediaFrame DecodeForwardErrorCorrection(ulong missingSequence, ulong missingMediaTimestamp100Ns, VoiceEncodedMediaFrame nextFrame)
        {
            ThrowIfDisposed();
            if (nextFrame.Packet == null || nextFrame.Packet.Length == 0) throw new ArgumentException("بسته بعدی خالی است.", nameof(nextFrame));
            float[] samples = DecodePacket(nextFrame.Packet, true);
            return new VoiceRecoveredMediaFrame(missingSequence, missingMediaTimestamp100Ns, samples, VoicePacketRecoveryKind.ForwardErrorCorrection);
        }

        // این تابع در نبود بسته قابل بازیابی از وضعیت داخلی بازکننده برای ساخت یک فریم جایگزین بیست میلی ثانیه ای استفاده می کند تا شکاف ناگهانی در صدا ایجاد نشود.
        public VoiceRecoveredMediaFrame DecodePacketLossConcealment(ulong missingSequence, ulong missingMediaTimestamp100Ns)
        {
            ThrowIfDisposed();
            float[] samples = new float[VoiceAudioContract.SamplesPerFrame];
            int decoded = VoiceOpusNative.DecodeFloat(decoder, null, 0, samples, VoiceAudioContract.SamplesPerFrame, 0);
            RequireSuccess(decoded, "پوشاندن گم شدن بسته");
            if (decoded != VoiceAudioContract.SamplesPerFrame) throw new InvalidOperationException("تعداد نمونه های بازیابی شده با قرارداد صوتی برابر نیست.");
            return new VoiceRecoveredMediaFrame(missingSequence, missingMediaTimestamp100Ns, samples, VoicePacketRecoveryKind.PacketLossConcealment);
        }

        // این تابع یک بسته را با حالت عادی یا بازیابی خطای رو به جلو باز می کند و فقط خروجی دقیقا ۹۶۰ نمونه ای را معتبر می پذیرد.
        private float[] DecodePacket(byte[] packet, bool useForwardErrorCorrection)
        {
            float[] samples = new float[VoiceAudioContract.SamplesPerFrame];
            int decoded = VoiceOpusNative.DecodeFloat(decoder, packet, packet.Length, samples, VoiceAudioContract.SamplesPerFrame, useForwardErrorCorrection ? 1 : 0);
            RequireSuccess(decoded, useForwardErrorCorrection ? "بازیابی اصلاح خطای رو به جلو" : "بازکردن عادی اوپوس");
            if (decoded != VoiceAudioContract.SamplesPerFrame) throw new InvalidOperationException("تعداد نمونه های بازشده با قرارداد صوتی برابر نیست.");
            return samples;
        }

        // این تابع کدگذار مستقل را با نرخ بیت انتخاب شده، نرخ گم شدن مورد انتظار، نرخ بیت متغیر، اصلاح خطای درون بسته روشن و ارسال سکوت خاموش آماده می کند.
        private void CreateEncoder()
        {
            int error;
            encoder = VoiceOpusNative.EncoderCreate(VoiceAudioContract.SampleRate, VoiceAudioContract.Channels, OpusApplicationVoip, out error);
            RequireSuccess(error, "ساخت کدگذار اوپوس");
            if (encoder == IntPtr.Zero) throw new InvalidOperationException("کدگذار اوپوس ساخته نشد.");
            RequireSuccess(VoiceOpusNative.EncoderCtl(encoder, SetBitrateRequest, BitrateKbps * 1000), "تنظیم نرخ بیت");
            RequireSuccess(VoiceOpusNative.EncoderCtl(encoder, SetVbrRequest, 1), "فعال کردن نرخ بیت متغیر");
            RequireSuccess(VoiceOpusNative.EncoderCtl(encoder, SetDtxRequest, 0), "خاموش کردن ارسال سکوت");
            RequireSuccess(VoiceOpusNative.EncoderCtl(encoder, SetInbandFecRequest, 1), "فعال کردن اصلاح خطای درون بسته");
            RequireSuccess(VoiceOpusNative.EncoderCtl(encoder, SetPacketLossPercentRequest, PacketLossPercent), "تنظیم درصد گم شدن مورد انتظار");
        }

        // این تابع بازکننده مستقل را برای بازکردن عادی، بازیابی اصلاح خطا و پوشاندن گم شدن بسته آماده می کند.
        private void CreateDecoder()
        {
            int error;
            decoder = VoiceOpusNative.DecoderCreate(VoiceAudioContract.SampleRate, VoiceAudioContract.Channels, out error);
            RequireSuccess(error, "ساخت بازکننده اوپوس");
            if (decoder == IntPtr.Zero) throw new InvalidOperationException("بازکننده اوپوس ساخته نشد.");
        }

        // این تابع نتیجه منفی کتابخانه اوپوس را به خطای روشن تبدیل می کند تا هیچ شکست بومی به عنوان خروجی معتبر ادامه پیدا نکند.
        private static void RequireSuccess(int result, string operation)
        {
            if (result >= 0) return;
            throw new InvalidOperationException(operation + " با خطای " + result + " متوقف شد.");
        }

        // این تابع از استفاده دوباره نمونه پس از آزاد شدن منابع بومی جلوگیری می کند.
        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(VoiceOpusPacketLossRecoveryCodec));
        }

        // این تابع منابع کدگذار و بازکننده مستقل را فقط یک بار آزاد می کند.
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (encoder != IntPtr.Zero) VoiceOpusNative.EncoderDestroy(encoder);
            if (decoder != IntPtr.Zero) VoiceOpusNative.DecoderDestroy(decoder);
            encoder = IntPtr.Zero;
            decoder = IntPtr.Zero;
        }
    }
}
