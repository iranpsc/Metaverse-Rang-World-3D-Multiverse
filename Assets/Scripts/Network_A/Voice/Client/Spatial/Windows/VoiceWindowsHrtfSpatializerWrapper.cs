#if UNITY_STANDALONE_WIN
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Network_A.Voice.Client.Spatial.Windows
{
    [DisallowMultipleComponent]
    public sealed class VoiceWindowsHrtfSpatializerWrapper : MonoBehaviour
    {
        private const string ListenerAnchorName = "VoiceListenerAnchor";
        private const string SpatialAnchorName = "VoiceSpatialAnchor";
        private const string WorkerPlaybackPrefix = "Voice_Worker_Playback_";
        private const string LegacyPlaybackPrefix = "Voice_Playback_";
        private const float ScanIntervalSeconds = 0.20f;
        private const float DirectionLogIntervalSeconds = 1.00f;
        private const float FrontCutoffHz = 22000.0f;
        private const float FullRearCutoffHz = 6000.0f;
        private const float RearCueSmoothing = 0.35f;

        private readonly HashSet<int> configuredSourceIds = new HashSet<int>();
        private readonly Dictionary<int, string> lastSectorBySourceId =
            new Dictionary<int, string>();
        private readonly Dictionary<int, float> nextDirectionLogAtBySourceId =
            new Dictionary<int, float>();

        private float nextScanAt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            VoiceWindowsHrtfSpatializerWrapper existing =
                FindObjectOfType<VoiceWindowsHrtfSpatializerWrapper>(true);

            if (existing != null)
            {
                return;
            }

            GameObject root =
                new GameObject("VoiceWindowsHrtfSpatializerWrapper");

            DontDestroyOnLoad(root);
            root.AddComponent<VoiceWindowsHrtfSpatializerWrapper>();

            Debug.Log(
                "VOICE_WINDOWS_HRTF_SPATIALIZER_V4=READY" +
                " | platformScope=Windows" +
                " | directBinaural=True" +
                " | interpolation=Bilinear" +
                " | stablePlaybackFileChanged=False" +
                " | mediaChanged=False" +
                " | codecChanged=False" +
                " | webglChanged=False" +
                " | questChanged=False");
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

            AudioSource[] sources =
                FindObjectsOfType<AudioSource>(true);

            for (int i = 0; i < sources.Length; i++)
            {
                AudioSource source = sources[i];

                if (!IsVoicePlaybackSource(source))
                {
                    continue;
                }

                ConfigureSource(source);

                if (listener != null)
                {
                    ApplyRearSpectralCue(
                        listener.transform,
                        source);

                    LogDirectionIfNeeded(
                        listener.transform,
                        source);
                }
            }
        }

        private static AudioListener FindVoiceListener()
        {
            AudioListener[] listeners =
                FindObjectsOfType<AudioListener>(true);

            for (int i = 0; i < listeners.Length; i++)
            {
                AudioListener listener = listeners[i];

                if (listener == null || !listener.enabled)
                {
                    continue;
                }

                if (string.Equals(
                        listener.transform.name,
                        ListenerAnchorName,
                        StringComparison.Ordinal))
                {
                    return listener;
                }
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

            string objectName =
                source.gameObject.name;

            bool correctPrefix =
                objectName.StartsWith(
                    WorkerPlaybackPrefix,
                    StringComparison.Ordinal) ||
                objectName.StartsWith(
                    LegacyPlaybackPrefix,
                    StringComparison.Ordinal);

            if (!correctPrefix)
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

        private void ConfigureSource(
            AudioSource source)
        {
            source.spatialBlend = 1.0f;
            source.spatialize = true;
            source.spatializePostEffects = false;
            source.panStereo = 0.0f;
            source.spread = 0.0f;
            source.dopplerLevel = 0.0f;

            SteamAudio.SteamAudioSource steamSource =
                source.GetComponent<SteamAudio.SteamAudioSource>();

            bool created = false;

            if (steamSource == null)
            {
                steamSource =
                    source.gameObject.AddComponent<SteamAudio.SteamAudioSource>();

                created = true;
            }

            steamSource.directBinaural = true;
            steamSource.interpolation =
                SteamAudio.HRTFInterpolation.Bilinear;
            steamSource.perspectiveCorrection = false;

            steamSource.distanceAttenuation = true;
            steamSource.distanceAttenuationInput =
                SteamAudio.DistanceAttenuationInput.PhysicsBased;
            steamSource.distanceAttenuationValue = 1.0f;
            steamSource.airAbsorption = false;
            steamSource.directivity = false;
            steamSource.occlusion = false;
            steamSource.transmission = false;
            steamSource.reflections = false;
            steamSource.pathing = false;
            steamSource.directMixLevel = 1.0f;

            int sourceId =
                source.GetInstanceID();

            if (!configuredSourceIds.Add(sourceId))
            {
                return;
            }

            Debug.Log(
                "VOICE_WINDOWS_HRTF_SOURCE=PASS" +
                " | source=" + source.gameObject.name +
                " | steamAudioSourceCreated=" + created +
                " | spatialBlend=" +
                    source.spatialBlend.ToString("F3") +
                " | spatialize=" + source.spatialize +
                " | spatializePostEffects=" +
                    source.spatializePostEffects +
                " | panStereo=" +
                    source.panStereo.ToString("F3") +
                " | spread=" +
                    source.spread.ToString("F3") +
                " | directBinaural=" +
                    steamSource.directBinaural +
                " | interpolation=" +
                    steamSource.interpolation +
                " | distanceAttenuation=True" +
                " | distanceAttenuationInput=PhysicsBased" +
                " | distanceAttenuationOwner=SteamAudio" +
                " | hrtfOwner=SteamAudio" +
                " | rearSpectralCue=True" +
                " | rearCutoffHz=" +
                    FullRearCutoffHz.ToString("F0"));
        }

        private static void ApplyRearSpectralCue(
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

            float forwardDot =
                Vector3.Dot(
                    flatForward,
                    flatToSource);

            float rearAmount =
                Mathf.Clamp01(-forwardDot);

            rearAmount =
                rearAmount *
                rearAmount *
                (3.0f - 2.0f * rearAmount);

            float targetCutoff =
                Mathf.Lerp(
                    FrontCutoffHz,
                    FullRearCutoffHz,
                    rearAmount);

            AudioLowPassFilter lowPass =
                source.GetComponent<AudioLowPassFilter>();

            if (lowPass == null)
            {
                lowPass =
                    source.gameObject.AddComponent<AudioLowPassFilter>();

                lowPass.cutoffFrequency =
                    FrontCutoffHz;

                lowPass.lowpassResonanceQ =
                    1.0f;
            }

            lowPass.enabled = true;
            lowPass.cutoffFrequency =
                Mathf.Lerp(
                    lowPass.cutoffFrequency,
                    targetCutoff,
                    RearCueSmoothing);
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

            float forwardDot =
                Vector3.Dot(
                    flatForward,
                    flatToSource);

            float rearAmount =
                Mathf.Clamp01(-forwardDot);

            rearAmount =
                rearAmount *
                rearAmount *
                (3.0f - 2.0f * rearAmount);

            AudioLowPassFilter lowPass =
                source.GetComponent<AudioLowPassFilter>();

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

            float now =
                Time.unscaledTime;

            bool periodic =
                !nextDirectionLogAtBySourceId.TryGetValue(
                    sourceId,
                    out float nextLogAt) ||
                now >= nextLogAt;

            if (!sectorChanged && !periodic)
            {
                return;
            }

            lastSectorBySourceId[sourceId] =
                sector;

            nextDirectionLogAtBySourceId[sourceId] =
                now + DirectionLogIntervalSeconds;

            Debug.Log(
                "VOICE_WINDOWS_HRTF_DIRECTION=OBSERVED" +
                " | source=" + source.gameObject.name +
                " | listener=" + listener.name +
                " | distance=" +
                    toSource.magnitude.ToString("F3") +
                " | signedAngle=" +
                    signedAngle.ToString("F1") +
                " | sector=" + sector +
                " | rearAmount=" +
                    rearAmount.ToString("F3") +
                " | rearCutoffHz=" +
                    (lowPass != null
                        ? lowPass.cutoffFrequency.ToString("F0")
                        : "NONE") +
                " | distanceAttenuationValue=" +
                    (source.GetComponent<SteamAudio.SteamAudioSource>() != null
                        ? source.GetComponent<SteamAudio.SteamAudioSource>().distanceAttenuationValue.ToString("F3")
                        : "NONE") +
                " | spatialize=" + source.spatialize +
                " | audioPlaying=" + source.isPlaying);
        }

        private static string ResolveSector(
            float signedAngle)
        {
            float absolute =
                Mathf.Abs(signedAngle);

            if (absolute <= 45.0f)
            {
                return "Front";
            }

            if (absolute >= 135.0f)
            {
                return "Back";
            }

            return signedAngle > 0.0f
                ? "Right"
                : "Left";
        }
    }
}
#endif
