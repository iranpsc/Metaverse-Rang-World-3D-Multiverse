using System;

namespace Network_A.Voice.Dedicated
{
    [Serializable]
    public sealed class VoiceDedicatedSessionEligibilityRequest
    {
        public string serviceToken;
        public string serverId;
    }

    [Serializable]
    public sealed class VoiceDedicatedSessionEligibilityResponse
    {
        public bool success;
        public string reason;
        public string message;
        public VoiceDedicatedSessionEligibilitySnapshot data;
        public long ts;
    }

    [Serializable]
    public sealed class VoiceDedicatedSessionEligibilitySnapshot
    {
        public string serverId;
        public VoiceDedicatedSessionEligibilityParticipant[] participants;
    }

    [Serializable]
    public sealed class VoiceDedicatedSessionEligibilityParticipant
    {
        public string userId;
        public string connectionId;
        public string roomId;
        public bool eligible;
        public long changedAtMs;
    }
}
