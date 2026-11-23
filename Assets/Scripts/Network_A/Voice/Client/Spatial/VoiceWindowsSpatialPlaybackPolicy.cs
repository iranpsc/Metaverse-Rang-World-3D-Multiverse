using System.Collections.Generic;
using UnityEngine;

namespace Network_A.Voice.Client.Spatial
{
    public static class VoiceWindowsSpatialPlaybackPolicy
    {
        private const float MinimumVoiceDistanceMeters = 1.0f;
        private const float MaximumVoiceDistanceMeters = 3.5f;

        private static readonly HashSet<int> loggedSourceIds = new HashSet<int>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            loggedSourceIds.Clear();
        }

        public static bool TryApply(
            AudioSource audioSource,
            Transform resolvedParent,
            Transform peerTransform)
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (audioSource == null || resolvedParent == null || peerTransform == null)
            {
                return false;
            }

            if (resolvedParent != peerTransform)
            {
                return false;
            }

            audioSource.spatialBlend = 1f;
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.minDistance = MinimumVoiceDistanceMeters;
            audioSource.maxDistance = MaximumVoiceDistanceMeters;
            audioSource.dopplerLevel = 0f;
            audioSource.priority = 0;

            int sourceId = audioSource.GetInstanceID();
            if (loggedSourceIds.Add(sourceId))
            {
                Debug.Log(
                    "VOICE_WINDOWS_SPATIAL_PLAYBACK=PASS" +
                    " | source=" + audioSource.gameObject.name +
                    " | parent=" + resolvedParent.name +
                    " | spatialBlend=" + audioSource.spatialBlend.ToString("F3") +
                    " | volume=" + audioSource.volume.ToString("F3") +
                    " | minDistance=" + audioSource.minDistance.ToString("F3") +
                    " | maxDistance=" + audioSource.maxDistance.ToString("F3") +
                    " | distanceModel=linear" +
                    " | distanceAttenuationIntroduced=true" +
                    " | listenerChanged=false" +
                    " | mediaChanged=false" +
                    " | codecChanged=false"
                );
            }

            return true;
#else
            return false;
#endif
        }
    }
}
