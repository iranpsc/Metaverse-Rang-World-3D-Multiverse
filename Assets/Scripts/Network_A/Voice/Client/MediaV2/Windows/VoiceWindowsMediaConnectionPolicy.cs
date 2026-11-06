#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
using System;

namespace Network_A.Voice.Client.MediaV2.Windows
{
    internal static class VoiceWindowsMediaConnectionPolicy
    {
        public const float PingIntervalSeconds = 5.00f;
        public const float HeartbeatTimeoutSeconds = 15.00f;
        public const float HeartbeatCheckIntervalSeconds = 1.00f;
        public const int BindTimeoutMs = 8000;

        private const float FirstRetryDelaySeconds = 0.10f;
        private const float MaximumRetryDelaySeconds = 2.00f;

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
