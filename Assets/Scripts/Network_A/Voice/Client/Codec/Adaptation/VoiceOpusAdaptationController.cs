using System;
using Network_A.Voice.Client.MediaV2.Congestion;

namespace Network_A.Voice.Client.Codec.Adaptation
{
    public interface IVoiceOpusAdaptationTarget
    {
        // این تابع تنظیمات انتخاب شده را روی هدف کدگذار اعمال می کند.
        void ApplyOpusAdaptation(VoiceOpusAdaptationProfile profile);
    }

    public sealed class VoiceOpusAdaptationController
    {
        private readonly IVoiceOpusAdaptationTarget target;
        private readonly VoiceOpusAdaptationProfile healthyProfile;
        private readonly VoiceOpusAdaptationProfile degradedProfile;
        private readonly VoiceOpusAdaptationProfile congestedProfile;
        private readonly VoiceOpusAdaptationProfile criticalProfile;

        private bool hasAppliedProfile;

        public VoiceOpusAdaptationController(
            IVoiceOpusAdaptationTarget target,
            VoiceOpusAdaptationProfile healthyProfile,
            VoiceOpusAdaptationProfile degradedProfile,
            VoiceOpusAdaptationProfile congestedProfile,
            VoiceOpusAdaptationProfile criticalProfile)
        {
            this.target = target ?? throw new ArgumentNullException(nameof(target));
            this.healthyProfile = RequireProfile(healthyProfile, VoiceMediaCongestionState.Healthy);
            this.degradedProfile = RequireProfile(degradedProfile, VoiceMediaCongestionState.Degraded);
            this.congestedProfile = RequireProfile(congestedProfile, VoiceMediaCongestionState.Congested);
            this.criticalProfile = RequireProfile(criticalProfile, VoiceMediaCongestionState.Critical);
        }

        public VoiceMediaCongestionState CurrentState { get; private set; } =
            VoiceMediaCongestionState.Unknown;

        public VoiceOpusAdaptationProfile CurrentProfile { get; private set; }

        // این تابع وضعیت پایدار ماشین ازدحام را دریافت می کند و فقط در صورت تغییر واقعی، نمایه متناظر را اعمال می کند.
        public bool TryApplyState(
            VoiceMediaCongestionState state,
            out VoiceOpusAdaptationProfile appliedProfile)
        {
            appliedProfile = null;
            if (state == VoiceMediaCongestionState.Unknown) return false;

            VoiceOpusAdaptationProfile selectedProfile = SelectProfile(state);
            if (hasAppliedProfile && CurrentState == state) return false;

            target.ApplyOpusAdaptation(selectedProfile);

            CurrentState = state;
            CurrentProfile = selectedProfile;
            hasAppliedProfile = true;
            appliedProfile = selectedProfile;
            return true;
        }

        // این تابع پس از جایگزینی هدف کدگذار، حافظه نمایه اعمال شده را پاک می کند تا همان وضعیت دوباره قابل اعمال باشد.
        public void Reset()
        {
            hasAppliedProfile = false;
            CurrentState = VoiceMediaCongestionState.Unknown;
            CurrentProfile = null;
        }

        private VoiceOpusAdaptationProfile SelectProfile(VoiceMediaCongestionState state)
        {
            switch (state)
            {
                case VoiceMediaCongestionState.Healthy:
                    return healthyProfile;
                case VoiceMediaCongestionState.Degraded:
                    return degradedProfile;
                case VoiceMediaCongestionState.Congested:
                    return congestedProfile;
                case VoiceMediaCongestionState.Critical:
                    return criticalProfile;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state));
            }
        }

        private static VoiceOpusAdaptationProfile RequireProfile(
            VoiceOpusAdaptationProfile profile,
            VoiceMediaCongestionState expectedState)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (profile.State != expectedState)
            {
                throw new ArgumentException(
                    "Voice Opus adaptation profile state does not match its controller slot.",
                    nameof(profile));
            }

            return profile;
        }
    }
}
