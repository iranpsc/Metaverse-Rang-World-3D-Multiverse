using System;

namespace Network_A.Voice.Client.Codec.Adaptation
{
    public sealed class VoiceNativeOpusEncoderAdaptationTarget : IVoiceOpusAdaptationTarget
    {
        private readonly VoiceNativeOpusCodec codec;

        public VoiceNativeOpusEncoderAdaptationTarget(VoiceNativeOpusCodec codec)
        {
            this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
        }

        // این تابع نمایه انتخاب شده را بدون وابسته کردن کدگذار به ماشین ازدحام روی کنترل های بومی اوپوس اعمال می کند.
        public void ApplyOpusAdaptation(VoiceOpusAdaptationProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            codec.ApplyEncoderSettings(
                profile.BitrateKbps,
                profile.InbandFecEnabled,
                profile.ExpectedPacketLossPercent);
        }
    }
}
