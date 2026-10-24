using System;
using System.Runtime.InteropServices;
using Network_A.Voice.Client.Audio;

namespace Network_A.Voice.Client.Codec
{
    public sealed class VoiceNativeOpusCodec : IDisposable
    {
        public const int SampleRate = VoiceAudioContract.SampleRate;
        public const int Channels = VoiceAudioContract.Channels;
        public const int FrameSamples = VoiceAudioContract.SamplesPerFrame;

        private const int OpusApplicationVoip = 2048;
        private const int SetBitrateRequest = 4002;
        private const int SetVbrRequest = 4006;
        private const int SetInbandFecRequest = 4012;
        private const int SetPacketLossPercentRequest = 4014;
        private const int SetDtxRequest = 4016;

        private readonly object encoderSync = new object();
        private IntPtr encoder;
        private IntPtr decoder;
        private bool disposed;

        public int BitrateKbps { get; private set; }
        public bool InbandFecEnabled { get; private set; }
        public int ExpectedPacketLossPercent { get; private set; }

        //* این سازنده Encoder/Decoder اوپوس را با تنظیمات رسمی 48kHz Mono و VBR بدون DTX می‌سازد تا گیت/قطع‌وصل کیفیتی ایجاد نشود.
        public VoiceNativeOpusCodec(int bitrateKbps)
        {
            ValidateEncoderSettings(bitrateKbps, 0);

            int error;
            encoder = VoiceOpusNative.EncoderCreate(SampleRate, Channels, OpusApplicationVoip, out error);
            RequireSuccess(error, "opus_encoder_create");
            if (encoder == IntPtr.Zero) throw new InvalidOperationException("Opus encoder was not created.");

            decoder = VoiceOpusNative.DecoderCreate(SampleRate, Channels, out error);
            RequireSuccess(error, "opus_decoder_create");
            if (decoder == IntPtr.Zero) throw new InvalidOperationException("Opus decoder was not created.");

            RequireSuccess(VoiceOpusNative.EncoderCtl(encoder, SetBitrateRequest, bitrateKbps * 1000), "OPUS_SET_BITRATE");
            RequireSuccess(VoiceOpusNative.EncoderCtl(encoder, SetVbrRequest, 1), "OPUS_SET_VBR");
            RequireSuccess(VoiceOpusNative.EncoderCtl(encoder, SetDtxRequest, 0), "OPUS_SET_DTX_DISABLED_FOR_G4_QUALITY");
            RequireSuccess(VoiceOpusNative.EncoderCtl(encoder, SetInbandFecRequest, 0), "OPUS_SET_INBAND_FEC");
            RequireSuccess(VoiceOpusNative.EncoderCtl(encoder, SetPacketLossPercentRequest, 0), "OPUS_SET_PACKET_LOSS_PERC");

            BitrateKbps = bitrateKbps;
            InbandFecEnabled = false;
            ExpectedPacketLossPercent = 0;
        }

        // این تابع تنظیمات کدگذار را بدون ساخت دوباره کدگذار و به صورت همگام با فشرده سازی فریم تغییر می دهد.
        public void ApplyEncoderSettings(
            int bitrateKbps,
            bool inbandFecEnabled,
            int expectedPacketLossPercent)
        {
            ValidateEncoderSettings(bitrateKbps, expectedPacketLossPercent);

            lock (encoderSync)
            {
                ThrowIfDisposed();

                if (BitrateKbps == bitrateKbps &&
                    InbandFecEnabled == inbandFecEnabled &&
                    ExpectedPacketLossPercent == expectedPacketLossPercent)
                {
                    return;
                }

                int previousBitrateKbps = BitrateKbps;
                bool previousInbandFecEnabled = InbandFecEnabled;
                int previousExpectedPacketLossPercent = ExpectedPacketLossPercent;

                try
                {
                    ApplyEncoderControls(
                        bitrateKbps,
                        inbandFecEnabled,
                        expectedPacketLossPercent);
                }
                catch (Exception applyException)
                {
                    try
                    {
                        ApplyEncoderControls(
                            previousBitrateKbps,
                            previousInbandFecEnabled,
                            previousExpectedPacketLossPercent);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new InvalidOperationException(
                            "Opus encoder adaptation and rollback both failed.",
                            new AggregateException(applyException, rollbackException));
                    }

                    throw;
                }

                BitrateKbps = bitrateKbps;
                InbandFecEnabled = inbandFecEnabled;
                ExpectedPacketLossPercent = expectedPacketLossPercent;
            }
        }

        //* این تابع دقیقاً 960 نمونه PCM Float را به یک فریم Opus بیست میلی‌ثانیه‌ای تبدیل می‌کند.
        public byte[] Encode(float[] pcm)
        {
            if (pcm == null || pcm.Length != FrameSamples)
                throw new ArgumentException("Voice Opus encode requires exactly 960 mono samples.");

            lock (encoderSync)
            {
                ThrowIfDisposed();
                byte[] output = new byte[4096];
                int length = VoiceOpusNative.EncodeFloat(encoder, pcm, FrameSamples, output, output.Length);
                RequireSuccess(length, "opus_encode_float");
                byte[] packet = new byte[length];
                Buffer.BlockCopy(output, 0, packet, 0, length);
                return packet;
            }
        }

        //* این تابع یک فریم Opus را به 960 نمونه PCM Float تک‌کاناله تبدیل می‌کند.
        public float[] Decode(byte[] packet)
        {
            if (disposed) throw new ObjectDisposedException("VoiceNativeOpusCodec");
            if (packet == null || packet.Length == 0 || packet.Length > 4096)
                throw new ArgumentException("Voice Opus packet is invalid.");

            float[] pcm = new float[FrameSamples];
            int samples = VoiceOpusNative.DecodeFloat(decoder, packet, packet.Length, pcm, FrameSamples, 0);
            RequireSuccess(samples, "opus_decode_float");
            if (samples == FrameSamples) return pcm;

            float[] trimmed = new float[samples];
            Array.Copy(pcm, trimmed, samples);
            return trimmed;
        }

        private static void RequireSuccess(int result, string operation)
        {
            if (result >= 0) return;
            throw new InvalidOperationException(operation + " failed with Opus error " + result + ".");
        }

        private void ApplyEncoderControls(
            int bitrateKbps,
            bool inbandFecEnabled,
            int expectedPacketLossPercent)
        {
            RequireSuccess(VoiceOpusNative.EncoderCtl(encoder, SetBitrateRequest, bitrateKbps * 1000), "OPUS_SET_BITRATE");
            RequireSuccess(VoiceOpusNative.EncoderCtl(encoder, SetInbandFecRequest, inbandFecEnabled ? 1 : 0), "OPUS_SET_INBAND_FEC");
            RequireSuccess(VoiceOpusNative.EncoderCtl(encoder, SetPacketLossPercentRequest, expectedPacketLossPercent), "OPUS_SET_PACKET_LOSS_PERC");
        }

        private static void ValidateEncoderSettings(
            int bitrateKbps,
            int expectedPacketLossPercent)
        {
            if (bitrateKbps != 28 && bitrateKbps != 32 && bitrateKbps != 40)
                throw new ArgumentOutOfRangeException(nameof(bitrateKbps));
            if (expectedPacketLossPercent < 0 || expectedPacketLossPercent > 100)
                throw new ArgumentOutOfRangeException(nameof(expectedPacketLossPercent));
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(VoiceNativeOpusCodec));
        }

        public void Dispose()
        {
            lock (encoderSync)
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

    internal static class VoiceOpusNative
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        private const string LibraryName = "__Internal";
#else
        private const string LibraryName = "opus";
#endif

        [DllImport(LibraryName, EntryPoint = "opus_encoder_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr EncoderCreate(int sampleRate, int channels, int application, out int error);

        [DllImport(LibraryName, EntryPoint = "opus_encoder_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void EncoderDestroy(IntPtr encoder);

        [DllImport(LibraryName, EntryPoint = "opus_encode_float", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int EncodeFloat(IntPtr encoder, float[] pcm, int frameSize, byte[] data, int maxDataBytes);

        [DllImport(LibraryName, EntryPoint = "opus_encoder_ctl", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int EncoderCtl(IntPtr encoder, int request, int value);

        [DllImport(LibraryName, EntryPoint = "opus_decoder_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr DecoderCreate(int sampleRate, int channels, out int error);

        [DllImport(LibraryName, EntryPoint = "opus_decoder_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void DecoderDestroy(IntPtr decoder);

        [DllImport(LibraryName, EntryPoint = "opus_decode_float", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int DecodeFloat(IntPtr decoder, byte[] data, int length, float[] pcm, int frameSize, int decodeFec);
    }
}

/*
توضیح فایل:
این فایل Binding اوپوس Native/WebGL را برای Mono 48kHz، فریم 20ms، VBR و DTX خاموش پیاده می‌کند و تغییر همگام نرخ بیت، FEC و درصد گم شدن مورد انتظار را بدون ساخت دوباره کدگذار فراهم می‌کند. Build نهایی باید Binary رسمی libopus هر Target را در Plugins داشته باشد.
*/
