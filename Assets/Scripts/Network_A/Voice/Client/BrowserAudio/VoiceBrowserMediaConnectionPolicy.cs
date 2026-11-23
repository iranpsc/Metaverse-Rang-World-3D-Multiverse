#if UNITY_WEBGL && !UNITY_EDITOR
using System;

namespace Network_A.Voice.Client.BrowserAudio
{
    internal static class VoiceBrowserMediaConnectionPolicy
    {
        private const float FirstRetryDelaySeconds = 0.10f;
        private const float MaximumRetryDelaySeconds = 2.00f;

        public static bool ShouldMaintainConnection(
            bool audioReady,
            bool controlAuthenticated,
            bool microphoneRequested,
            bool speakerOff)
        {
            return audioReady &&
                   controlAuthenticated &&
                   (microphoneRequested || !speakerOff);
        }

        public static bool ShouldPublish(
            bool microphoneRequested,
            bool mediaBound)
        {
            return microphoneRequested && mediaBound;
        }

        public static float GetRetryDelaySeconds(int consecutiveFailures)
        {
            int safeFailures = Math.Max(1, consecutiveFailures);
            int exponent = Math.Min(5, safeFailures - 1);
            float delay = FirstRetryDelaySeconds * (1 << exponent);
            return Math.Min(MaximumRetryDelaySeconds, delay);
        }
    }
}
#endif
