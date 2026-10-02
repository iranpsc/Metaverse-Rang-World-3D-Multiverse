using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace Network_A.Voice.Client.Diagnostics
{
    /*
    G4 diagnostic-only probe.
    This file does not change Voice logic. It observes the runtime/playback objects
    with reflection and writes trace markers for receiver/playback diagnosis.
    */
    public sealed class VoiceClientPlaybackTraceProbe : MonoBehaviour
    {
        private const float LogIntervalSeconds = 1.0f;
        private float nextLogAt;
        private string lastRuntimeSnapshot = string.Empty;
        private string lastPlaybackSnapshot = string.Empty;
        private uint previousReceivedSequence;
        private int previousStreamCount = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (FindExistingProbe() != null) return;

            GameObject probeObject = new GameObject("G4_Unity_Voice_Playback_TraceProbe");
            DontDestroyOnLoad(probeObject);
            probeObject.AddComponent<VoiceClientPlaybackTraceProbe>();

            Debug.Log("G4_UNITY_VOICE_PLAYBACK_TRACE_PROBE=READY");
        }

        private static VoiceClientPlaybackTraceProbe FindExistingProbe()
        {
            VoiceClientPlaybackTraceProbe[] probes =
                FindObjectsOfType<VoiceClientPlaybackTraceProbe>(true);
            return probes != null && probes.Length > 0 ? probes[0] : null;
        }

        private void Update()
        {
            if (Time.unscaledTime < nextLogAt) return;
            nextLogAt = Time.unscaledTime + LogIntervalSeconds;

            try
            {
                InspectVoiceClient();
            }
            catch (Exception exception)
            {
                Debug.LogWarning("G4_UNITY_VOICE_PLAYBACK_TRACE_PROBE_FAILED | " + exception.GetType().Name + " | " + exception.Message);
            }
        }

        private void InspectVoiceClient()
        {
            MonoBehaviour runtime = FindComponentByTypeName("VoiceClientRuntime");
            MonoBehaviour playback = FindComponentByTypeName("VoiceSpatialPlaybackManager");

            if (runtime == null)
            {
                EmitChanged(
                    ref lastRuntimeSnapshot,
                    "G4_UNITY_VOICE_RUNTIME_MISSING");
                return;
            }

            Type runtimeType = runtime.GetType();
            bool isAuthenticated = ReadBoolProperty(runtime, "IsAuthenticated");
            int activeSessionCount = ReadIntProperty(runtime, "ActiveSessionCount");
            string voiceConnectionId = ReadStringProperty(runtime, "VoiceConnectionId");

            int queuedPackets = CountObject(ReadField(runtime, "receivedPackets"));
            int sessions = CountObject(ReadField(runtime, "sessions"));
            int peerConnections = CountObject(ReadField(runtime, "peerConnectionByUserId"));
            uint lastReceivedSequence = ReadUIntField(runtime, "lastReceivedSequence");
            uint lastPublishedSequence = ReadUIntField(runtime, "lastPublishedSequence");
            bool publishingAllowed = ReadBoolField(runtime, "publishingAllowed");
            bool firstVoiceFrameSendLogged = ReadBoolField(runtime, "firstVoiceFrameSendLogged");

            string runtimeSnapshot =
                "G4_UNITY_VOICE_RUNTIME_OBSERVED" +
                " | type=" + runtimeType.FullName +
                " | authenticated=" + isAuthenticated +
                " | activeSessionCount=" + activeSessionCount +
                " | sessions=" + sessions +
                " | peerConnections=" + peerConnections +
                " | queuedPackets=" + queuedPackets +
                " | lastReceivedSequence=" + lastReceivedSequence +
                " | lastPublishedSequence=" + lastPublishedSequence +
                " | publishingAllowed=" + publishingAllowed +
                " | firstVoiceFrameSendLogged=" + firstVoiceFrameSendLogged +
                " | voiceConnectionId=" + Safe(voiceConnectionId);

            EmitChanged(ref lastRuntimeSnapshot, runtimeSnapshot);

            if (lastReceivedSequence != previousReceivedSequence)
            {
                Debug.Log(
                    "G4_UNITY_VOICE_RECEIVE_SEQUENCE_ADVANCED" +
                    " | previous=" + previousReceivedSequence +
                    " | current=" + lastReceivedSequence +
                    " | delta=" + SequenceDelta(previousReceivedSequence, lastReceivedSequence) +
                    " | queuedPackets=" + queuedPackets +
                    " | activeSessionCount=" + activeSessionCount);
                previousReceivedSequence = lastReceivedSequence;
            }

            if (playback == null)
            {
                EmitChanged(
                    ref lastPlaybackSnapshot,
                    "G4_UNITY_VOICE_PLAYBACK_MANAGER_MISSING");
                return;
            }

            bool speakerOff = ReadBoolField(playback, "speakerOff");
            object streamsObject = ReadField(playback, "streams");
            int streamCount = CountObject(streamsObject);

            string playbackSnapshot =
                "G4_UNITY_VOICE_PLAYBACK_OBSERVED" +
                " | type=" + playback.GetType().FullName +
                " | speakerOff=" + speakerOff +
                " | streamCount=" + streamCount;

            EmitChanged(ref lastPlaybackSnapshot, playbackSnapshot);

            if (lastReceivedSequence > 0 && activeSessionCount > 0 && streamCount == 0 && previousStreamCount == 0)
            {
                Debug.LogWarning(
                    "G4_UNITY_VOICE_PLAYBACK_NO_STREAM_AFTER_RECEIVE" +
                    " | lastReceivedSequence=" + lastReceivedSequence +
                    " | activeSessionCount=" + activeSessionCount +
                    " | speakerOff=" + speakerOff);
            }

            previousStreamCount = streamCount;

            EmitStreamDetails(streamsObject);
        }

        private static void EmitStreamDetails(object streamsObject)
        {
            if (!(streamsObject is IEnumerable enumerable)) return;

            foreach (object entry in enumerable)
            {
                object key = ReadProperty(entry, "Key");
                object stream = ReadProperty(entry, "Value");
                if (stream == null) continue;

                string sessionId = Convert.ToString(key);
                object pcmFrames = ReadField(stream, "pcmFrames");
                int pcmQueue = CountObject(pcmFrames);
                object currentFrame = ReadField(stream, "currentFrame");
                int currentOffset = ReadIntField(stream, "currentOffset");
                AudioSource audioSource = ReadField(stream, "audioSource") as AudioSource;
                GameObject playbackObject = ReadField(stream, "playbackObject") as GameObject;

                string parentName = playbackObject != null && playbackObject.transform.parent != null
                    ? playbackObject.transform.parent.name
                    : "";

                Debug.Log(
                    "G4_UNITY_VOICE_PLAYBACK_STREAM" +
                    " | sessionId=" + Safe(sessionId) +
                    " | pcmQueue=" + pcmQueue +
                    " | currentFrame=" + (currentFrame != null) +
                    " | currentOffset=" + currentOffset +
                    " | objectActive=" + (playbackObject != null && playbackObject.activeInHierarchy) +
                    " | parent=" + Safe(parentName) +
                    " | audioExists=" + (audioSource != null) +
                    " | audioEnabled=" + (audioSource != null && audioSource.enabled) +
                    " | audioPlaying=" + (audioSource != null && audioSource.isPlaying) +
                    " | audioMute=" + (audioSource != null && audioSource.mute) +
                    " | audioVolume=" + (audioSource != null ? audioSource.volume.ToString("0.000") : "null") +
                    " | spatialBlend=" + (audioSource != null ? audioSource.spatialBlend.ToString("0.000") : "null") +
                    " | minDistance=" + (audioSource != null ? audioSource.minDistance.ToString("0.000") : "null") +
                    " | maxDistance=" + (audioSource != null ? audioSource.maxDistance.ToString("0.000") : "null") +
                    " | clip=" + Safe(audioSource != null && audioSource.clip != null ? audioSource.clip.name : ""));
            }
        }

        private static MonoBehaviour FindComponentByTypeName(string typeName)
        {
            MonoBehaviour[] components =
                FindObjectsOfType<MonoBehaviour>(true);
            foreach (MonoBehaviour component in components)
            {
                if (component == null) continue;
                if (string.Equals(component.GetType().Name, typeName, StringComparison.Ordinal))
                    return component;
            }

            return null;
        }

        private static void EmitChanged(ref string previous, string current)
        {
            if (string.Equals(previous, current, StringComparison.Ordinal)) return;
            previous = current;
            Debug.Log(current);
        }

        private static uint SequenceDelta(uint previous, uint current)
        {
            return current >= previous ? current - previous : current;
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "<empty>" : value;
        }

        private static object ReadField(object target, string name)
        {
            if (target == null) return null;
            FieldInfo field = target.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field != null ? field.GetValue(target) : null;
        }

        private static object ReadProperty(object target, string name)
        {
            if (target == null) return null;
            PropertyInfo property = target.GetType().GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property != null ? property.GetValue(target, null) : null;
        }

        private static int CountObject(object value)
        {
            if (value == null) return -1;
            if (value is ICollection collection) return collection.Count;

            PropertyInfo countProperty = value.GetType().GetProperty(
                "Count",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (countProperty == null) return -1;

            object count = countProperty.GetValue(value, null);
            return count is int intCount ? intCount : -1;
        }

        private static bool ReadBoolProperty(object target, string name)
        {
            object value = ReadProperty(target, name);
            return value is bool boolValue && boolValue;
        }

        private static int ReadIntProperty(object target, string name)
        {
            object value = ReadProperty(target, name);
            return value is int intValue ? intValue : -1;
        }

        private static string ReadStringProperty(object target, string name)
        {
            return Convert.ToString(ReadProperty(target, name));
        }

        private static bool ReadBoolField(object target, string name)
        {
            object value = ReadField(target, name);
            return value is bool boolValue && boolValue;
        }

        private static int ReadIntField(object target, string name)
        {
            object value = ReadField(target, name);
            return value is int intValue ? intValue : -1;
        }

        private static uint ReadUIntField(object target, string name)
        {
            object value = ReadField(target, name);
            return value is uint uintValue ? uintValue : 0;
        }
    }
}
