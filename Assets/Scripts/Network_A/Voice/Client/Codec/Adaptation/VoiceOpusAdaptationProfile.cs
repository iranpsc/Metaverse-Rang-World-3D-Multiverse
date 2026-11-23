using System;
using Network_A.Voice.Client.MediaV2.Congestion;

namespace Network_A.Voice.Client.Codec.Adaptation
{
    public sealed class VoiceOpusAdaptationProfile
    {
        // این سازنده یک وضعیت شبکه را به تنظیمات معتبر کدگذار اوپوس متصل می کند.
        public VoiceOpusAdaptationProfile(
            VoiceMediaCongestionState state,
            int bitrateKbps,
            bool inbandFecEnabled,
            int expectedPacketLossPercent)
        {
            if (state < VoiceMediaCongestionState.Healthy ||
                state > VoiceMediaCongestionState.Critical)
            {
                throw new ArgumentOutOfRangeException(nameof(state));
            }

            if (bitrateKbps != 28 && bitrateKbps != 32 && bitrateKbps != 40)
            {
                throw new ArgumentOutOfRangeException(nameof(bitrateKbps));
            }

            if (expectedPacketLossPercent < 0 || expectedPacketLossPercent > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(expectedPacketLossPercent));
            }

            State = state;
            BitrateKbps = bitrateKbps;
            InbandFecEnabled = inbandFecEnabled;
            ExpectedPacketLossPercent = expectedPacketLossPercent;
        }

        public VoiceMediaCongestionState State { get; }
        public int BitrateKbps { get; }
        public bool InbandFecEnabled { get; }
        public int ExpectedPacketLossPercent { get; }
    }
}
