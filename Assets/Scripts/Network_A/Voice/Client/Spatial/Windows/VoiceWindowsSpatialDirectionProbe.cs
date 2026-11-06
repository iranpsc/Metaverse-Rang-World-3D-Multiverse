#if UNITY_STANDALONE_WIN
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Network_A.Voice.Client.Spatial.Windows
{
    [DisallowMultipleComponent]
    public sealed class VoiceWindowsSpatialDirectionProbe : MonoBehaviour
    {
        private const string ListenerAnchorName = "VoiceListenerAnchor";
        private const string SpatialAnchorName = "VoiceSpatialAnchor";
        private const string PlaybackPrefix = "Voice_Playback_";
        private const float ScanIntervalSeconds = 0.20f;
        private const float PeriodicLogSeconds = 1.00f;

        private readonly Dictionary<int, string> lastSectorBySourceId =
            new Dictionary<int, string>();

        private readonly Dictionary<int, float> nextPeriodicLogAtBySourceId =
            new Dictionary<int, float>();

        private float nextScanAt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            VoiceWindowsSpatialDirectionProbe existing =
                FindObjectOfType<VoiceWindowsSpatialDirectionProbe>(true);

            if (existing != null)
            {
                return;
            }

            GameObject root =
                new GameObject("VoiceWindowsSpatialDirectionProbe");

            DontDestroyOnLoad(root);
            root.AddComponent<VoiceWindowsSpatialDirectionProbe>();

            Debug.Log(
                "VOICE_WINDOWS_SPATIAL_DIRECTION_PROBE=READY" +
                " | platformScope=Windows" +
                " | modifiesPlayback=False" +
                " | modifiesMedia=False" +
                " | modifiesMicrophone=False" +
                " | existingFilesModified=False" +
                " | webglChanged=False");
        }

        private void Update()
        {
            if (Time.unscaledTime < nextScanAt)
            {
                return;
            }

            nextScanAt =
                Time.unscaledTime + ScanIntervalSeconds;

            AudioListener listener =
                FindVoiceListener();

            if (listener == null)
            {
                return;
            }

            AudioSource[] sources =
                FindObjectsOfType<AudioSource>(true);

            for (int i = 0; i < sources.Length; i++)
            {
                AudioSource source = sources[i];

                if (!IsVoicePlaybackSource(source))
                {
                    continue;
                }

                LogDirectionIfNeeded(
                    listener.transform,
                    source);
            }
        }

        private static AudioListener FindVoiceListener()
        {
            AudioListener[] listeners =
                FindObjectsOfType<AudioListener>(true);

            for (int i = 0; i < listeners.Length; i++)
            {
                AudioListener listener = listeners[i];

                if (listener == null ||
                    !listener.enabled)
                {
                    continue;
                }

                if (!string.Equals(
                        listener.transform.name,
                        ListenerAnchorName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                return listener;
            }

            return null;
        }

        private static bool IsVoicePlaybackSource(
            AudioSource source)
        {
            if (source == null ||
                source.gameObject == null)
            {
                return false;
            }

            if (!source.gameObject.name.StartsWith(
                    PlaybackPrefix,
                    StringComparison.Ordinal))
            {
                return false;
            }

            Transform current =
                source.transform.parent;

            while (current != null)
            {
                if (string.Equals(
                        current.name,
                        SpatialAnchorName,
                        StringComparison.Ordinal))
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        private void LogDirectionIfNeeded(
            Transform listener,
            AudioSource source)
        {
            Vector3 toSource =
                source.transform.position -
                listener.position;

            Vector3 flatToSource =
                Vector3.ProjectOnPlane(
                    toSource,
                    Vector3.up);

            Vector3 flatForward =
                Vector3.ProjectOnPlane(
                    listener.forward,
                    Vector3.up);

            if (flatToSource.sqrMagnitude < 0.000001f ||
                flatForward.sqrMagnitude < 0.000001f)
            {
                return;
            }

            flatToSource.Normalize();
            flatForward.Normalize();

            float signedAngle =
                Vector3.SignedAngle(
                    flatForward,
                    flatToSource,
                    Vector3.up);

            string sector =
                ResolveSector(signedAngle);

            int sourceId =
                source.GetInstanceID();

            bool sectorChanged =
                !lastSectorBySourceId.TryGetValue(
                    sourceId,
                    out string previousSector) ||
                !string.Equals(
                    previousSector,
                    sector,
                    StringComparison.Ordinal);

            float now = Time.unscaledTime;

            bool periodic =
                !nextPeriodicLogAtBySourceId.TryGetValue(
                    sourceId,
                    out float nextLogAt) ||
                now >= nextLogAt;

            if (!sectorChanged && !periodic)
            {
                return;
            }

            lastSectorBySourceId[sourceId] =
                sector;

            nextPeriodicLogAtBySourceId[sourceId] =
                now + PeriodicLogSeconds;

            Debug.Log(
                "VOICE_WINDOWS_SPATIAL_DIRECTION=OBSERVED" +
                " | source=" + source.gameObject.name +
                " | listener=" + listener.name +
                " | listenerPosition=" + listener.position +
                " | listenerForward=" + listener.forward +
                " | sourcePosition=" + source.transform.position +
                " | distance=" +
                    toSource.magnitude.ToString("F3") +
                " | signedAngle=" +
                    signedAngle.ToString("F1") +
                " | sector=" + sector +
                " | spatialBlend=" +
                    source.spatialBlend.ToString("F3") +
                " | spatialize=" + source.spatialize +
                " | audioPlaying=" + source.isPlaying);
        }

        private static string ResolveSector(
            float signedAngle)
        {
            float absolute =
                Mathf.Abs(signedAngle);

            if (absolute <= 45f)
            {
                return "Front";
            }

            if (absolute >= 135f)
            {
                return "Back";
            }

            return signedAngle > 0f
                ? "Right"
                : "Left";
        }
    }
}
#endif
