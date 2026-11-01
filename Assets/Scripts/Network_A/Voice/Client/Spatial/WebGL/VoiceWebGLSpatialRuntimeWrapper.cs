#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Network_A.DedicatedGameServer.Client;
using Network_A.Voice.Client.Runtime;
using UnityEngine;

namespace Network_A.Voice.Client.Spatial.WebGL
{
    [DisallowMultipleComponent]
    public sealed class VoiceWebGLSpatialRuntimeWrapper : MonoBehaviour
    {
        private const string RuntimeRootName = "Voice_Client_Runtime_Root";
        private const string ListenerAnchorName = "VoiceListenerAnchor";
        private const string SpatialAnchorName = "VoiceSpatialAnchor";
        private const string SessionsFieldName = "sessions";
        private const string PeerMapFieldName = "peerUserIdByConnectionId";

        private const float UpdateIntervalSeconds = 0.05f;
        private const float NearDistance = 1.0f;
        private const float FarDistance = 3.5f;
        private const float DirectionEpsilon = 0.0001f;
        private const float DiagnosticIntervalSeconds = 1.0f;

        private readonly Dictionary<string, Transform> remoteAnchorByUserId =
            new Dictionary<string, Transform>(StringComparer.Ordinal);

        private readonly HashSet<string> activeSenderConnectionIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, float> nextDiagnosticAtBySender =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        private global::G7ThreeDModeController threeDModeController;
        private DedicatedRemotePlayerStateReceiver remoteStateReceiver;
        private VoiceClientRuntime runtime;
        private FieldInfo sessionsField;
        private Type activeSessionType;
        private FieldInfo peerMapField;
        private float nextUpdateAt;
        private bool readyLogged;
        private bool reflectionFailureLogged;

        [DllImport("__Internal")]
        private static extern int VoiceWebGLSpatialSetSenderState(
            string senderConnectionId,
            float pan,
            float gain,
            float rearAmount);

        [DllImport("__Internal")]
        private static extern int VoiceWebGLSpatialRemoveSender(
            string senderConnectionId);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            VoiceWebGLSpatialRuntimeWrapper existing =
                FindObjectOfType<VoiceWebGLSpatialRuntimeWrapper>(true);

            if (existing != null)
            {
                return;
            }

            GameObject host = new GameObject("VoiceWebGLSpatialRuntimeWrapper");
            DontDestroyOnLoad(host);
            host.AddComponent<VoiceWebGLSpatialRuntimeWrapper>();

            Debug.Log(
                "VOICE_WEBGL_SPATIAL_V1=INSTALLED" +
                " | platformScope=WebGL" +
                " | leftRight=True" +
                " | frontRear=True" +
                " | distanceAttenuation=True" +
                " | windowsChanged=False" +
                " | serverChanged=False");
        }

        private void Update()
        {
            if (Time.unscaledTime < nextUpdateAt)
            {
                return;
            }

            nextUpdateAt = Time.unscaledTime + UpdateIntervalSeconds;

            ResolveReferences();
            Transform listener = ResolveListenerTransform();
            if (listener == null || runtime == null)
            {
                return;
            }

            Dictionary<string, string> peerUserIdByConnectionId =
                CreateVoicePeerSnapshot();

            if (peerUserIdByConnectionId.Count == 0)
            {
                CleanupRemovedSenders(peerUserIdByConnectionId.Keys);
                return;
            }

            int deliveredCount = 0;
            HashSet<string> currentSenders =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, string> pair in peerUserIdByConnectionId)
            {
                string senderConnectionId = Normalize(pair.Key).ToLowerInvariant();
                string peerUserId = Normalize(pair.Value);
                if (senderConnectionId.Length == 0 || peerUserId.Length == 0)
                {
                    continue;
                }

                currentSenders.Add(senderConnectionId);

                Transform source = ResolveRemoteSpatialAnchor(peerUserId);
                if (source == null)
                {
                    continue;
                }

                ComputeSpatialState(
                    listener,
                    source,
                    out float pan,
                    out float gain,
                    out float rearAmount,
                    out float distance,
                    out float signedAngle);

                int delivered = VoiceWebGLSpatialSetSenderState(
                    senderConnectionId,
                    pan,
                    gain,
                    rearAmount);

                if (delivered > 0)
                {
                    deliveredCount += delivered;
                    LogSpatialDiagnosticIfNeeded(
                        senderConnectionId,
                        peerUserId,
                        pan,
                        gain,
                        rearAmount,
                        distance,
                        signedAngle);
                }
            }

            CleanupRemovedSenders(currentSenders);

            if (!readyLogged && deliveredCount > 0)
            {
                readyLogged = true;
                Debug.Log(
                    "VOICE_WEBGL_SPATIAL_V1=READY" +
                    " | platformScope=WebGL" +
                    " | updateHz=20" +
                    " | nearDistance=" + NearDistance.ToString("F2") +
                    " | farDistance=" + FarDistance.ToString("F2") +
                    " | rearCue=browser_lowpass" +
                    " | stereoPanning=equal_power" +
                    " | existingWindowsPathChanged=False" +
                    " | recordingChanged=False");
            }
        }

        private void ResolveReferences()
        {
            if (threeDModeController == null)
            {
                threeDModeController =
                    FindObjectOfType<global::G7ThreeDModeController>(true);
            }

            if (remoteStateReceiver == null)
            {
                remoteStateReceiver =
                    FindObjectOfType<DedicatedRemotePlayerStateReceiver>(true);
            }

            if (runtime == null)
            {
                GameObject root = GameObject.Find(RuntimeRootName);
                if (root != null)
                {
                    runtime = root.GetComponent<VoiceClientRuntime>();
                    sessionsField = null;
                    activeSessionType = null;
                    peerMapField = null;
                    reflectionFailureLogged = false;
                }
            }
        }

        private Transform ResolveListenerTransform()
        {
            if (threeDModeController == null)
            {
                return null;
            }

            GameObject localPlayer = threeDModeController.LocalPlayerInstance;
            if (localPlayer == null)
            {
                return null;
            }

            Transform anchor = FindNamedChild(
                localPlayer.transform,
                ListenerAnchorName);

            return anchor != null ? anchor : localPlayer.transform;
        }

        private Dictionary<string, string> CreateVoicePeerSnapshot()
        {
            Dictionary<string, string> result =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (runtime == null)
            {
                return result;
            }

            if (sessionsField == null)
            {
                sessionsField = typeof(VoiceClientRuntime).GetField(
                    SessionsFieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }

            if (sessionsField == null)
            {
                LogReflectionFailureOnce("sessions_field_missing");
                return result;
            }

            IDictionary sessions = sessionsField.GetValue(runtime) as IDictionary;
            if (sessions == null)
            {
                return result;
            }

            foreach (DictionaryEntry sessionEntry in sessions)
            {
                object activeSession = sessionEntry.Value;
                if (activeSession == null)
                {
                    continue;
                }

                Type sessionType = activeSession.GetType();
                if (activeSessionType != sessionType || peerMapField == null)
                {
                    activeSessionType = sessionType;
                    peerMapField = sessionType.GetField(
                        PeerMapFieldName,
                        BindingFlags.Instance | BindingFlags.NonPublic);
                }

                if (peerMapField == null)
                {
                    LogReflectionFailureOnce("peer_map_field_missing");
                    return result;
                }

                IDictionary peers = peerMapField.GetValue(activeSession) as IDictionary;
                if (peers == null)
                {
                    continue;
                }

                foreach (DictionaryEntry peerEntry in peers)
                {
                    string connectionId = Normalize(peerEntry.Key as string);
                    string userId = Normalize(peerEntry.Value as string);
                    if (connectionId.Length == 0 || userId.Length == 0)
                    {
                        continue;
                    }

                    result[connectionId] = userId;
                }
            }

            return result;
        }

        private Transform ResolveRemoteSpatialAnchor(string userId)
        {
            if (remoteAnchorByUserId.TryGetValue(userId, out Transform cached))
            {
                if (cached != null && cached.gameObject.activeInHierarchy)
                {
                    return cached;
                }

                remoteAnchorByUserId.Remove(userId);
            }

            if (threeDModeController == null)
            {
                return null;
            }

            Transform playersRoot = threeDModeController.PlayersRoot;
            if (playersRoot == null)
            {
                return null;
            }

            string displayName = ResolveRemoteDisplayName(userId);
            GameObject remotePlayer = FindRemotePlayer(
                playersRoot,
                displayName,
                userId);

            if (remotePlayer == null)
            {
                return null;
            }

            Transform anchor = FindNamedChild(
                remotePlayer.transform,
                SpatialAnchorName);

            Transform resolved = anchor != null
                ? anchor
                : remotePlayer.transform;

            remoteAnchorByUserId[userId] = resolved;
            return resolved;
        }

        private string ResolveRemoteDisplayName(string userId)
        {
            if (remoteStateReceiver == null)
            {
                return userId;
            }

            List<DedicatedRemotePlayerState> snapshot =
                remoteStateReceiver.CreateSnapshot();

            for (int i = 0; i < snapshot.Count; i++)
            {
                DedicatedRemotePlayerState state = snapshot[i];
                if (state == null)
                {
                    continue;
                }

                string playerId = Normalize(state.ResolvePlayerId());
                if (!string.Equals(playerId, userId, StringComparison.Ordinal))
                {
                    continue;
                }

                string displayName = Normalize(state.userName);
                return displayName.Length > 0 ? displayName : userId;
            }

            return userId;
        }

        private static GameObject FindRemotePlayer(
            Transform playersRoot,
            string displayName,
            string userId)
        {
            string expectedDisplayName = Normalize(displayName);
            string expectedName = "Remote_Player_" +
                SanitizeObjectName(
                    expectedDisplayName.Length > 0
                        ? expectedDisplayName
                        : userId);

            for (int i = 0; i < playersRoot.childCount; i++)
            {
                Transform child = playersRoot.GetChild(i);
                if (child == null)
                {
                    continue;
                }

                if (string.Equals(child.name, expectedName, StringComparison.Ordinal))
                {
                    return child.gameObject;
                }
            }

            return null;
        }

        private static void ComputeSpatialState(
            Transform listener,
            Transform source,
            out float pan,
            out float gain,
            out float rearAmount,
            out float distance,
            out float signedAngle)
        {
            Vector3 worldOffset = source.position - listener.position;
            distance = worldOffset.magnitude;

            Vector3 localOffset = listener.InverseTransformDirection(worldOffset);
            float horizontalMagnitude = Mathf.Sqrt(
                localOffset.x * localOffset.x +
                localOffset.z * localOffset.z);

            if (horizontalMagnitude <= DirectionEpsilon)
            {
                pan = 0f;
                rearAmount = 0f;
                signedAngle = 0f;
            }
            else
            {
                float x = localOffset.x / horizontalMagnitude;
                float z = localOffset.z / horizontalMagnitude;

                pan = Mathf.Clamp(x, -1f, 1f);
                rearAmount = Mathf.Clamp01(-z);
                rearAmount = rearAmount * rearAmount *
                    (3f - 2f * rearAmount);

                signedAngle = Mathf.Atan2(x, z) * Mathf.Rad2Deg;
            }

            if (distance <= NearDistance)
            {
                gain = 1f;
            }
            else if (distance >= FarDistance)
            {
                gain = 0f;
            }
            else
            {
                gain = 1f -
                    (distance - NearDistance) /
                    (FarDistance - NearDistance);
            }

            gain = Mathf.Clamp01(gain);
        }

        private void CleanupRemovedSenders(
            IEnumerable<string> currentSenderIds)
        {
            HashSet<string> current = currentSenderIds as HashSet<string> ??
                new HashSet<string>(
                    currentSenderIds,
                    StringComparer.OrdinalIgnoreCase);

            if (activeSenderConnectionIds.Count > 0)
            {
                List<string> stale = null;
                foreach (string senderId in activeSenderConnectionIds)
                {
                    if (current.Contains(senderId))
                    {
                        continue;
                    }

                    if (stale == null)
                    {
                        stale = new List<string>();
                    }
                    stale.Add(senderId);
                }

                if (stale != null)
                {
                    for (int i = 0; i < stale.Count; i++)
                    {
                        VoiceWebGLSpatialRemoveSender(stale[i]);
                        activeSenderConnectionIds.Remove(stale[i]);
                        nextDiagnosticAtBySender.Remove(stale[i]);
                    }
                }
            }

            foreach (string senderId in current)
            {
                activeSenderConnectionIds.Add(senderId);
            }
        }

        private void LogSpatialDiagnosticIfNeeded(
            string senderConnectionId,
            string userId,
            float pan,
            float gain,
            float rearAmount,
            float distance,
            float signedAngle)
        {
            float now = Time.unscaledTime;
            if (nextDiagnosticAtBySender.TryGetValue(
                    senderConnectionId,
                    out float nextAt) &&
                now < nextAt)
            {
                return;
            }

            nextDiagnosticAtBySender[senderConnectionId] =
                now + DiagnosticIntervalSeconds;

            string sector;
            if (rearAmount >= 0.5f)
            {
                sector = "Back";
            }
            else if (pan <= -0.35f)
            {
                sector = "Left";
            }
            else if (pan >= 0.35f)
            {
                sector = "Right";
            }
            else
            {
                sector = "Front";
            }

            Debug.Log(
                "VOICE_WEBGL_SPATIAL_STATE=PASS" +
                " | senderConnectionId=" + senderConnectionId +
                " | userId=" + userId +
                " | sector=" + sector +
                " | signedAngle=" + signedAngle.ToString("F1") +
                " | distance=" + distance.ToString("F3") +
                " | pan=" + pan.ToString("F3") +
                " | gain=" + gain.ToString("F3") +
                " | rearAmount=" + rearAmount.ToString("F3"));
        }

        private void LogReflectionFailureOnce(string reason)
        {
            if (reflectionFailureLogged)
            {
                return;
            }

            reflectionFailureLogged = true;
            Debug.LogError(
                "VOICE_WEBGL_SPATIAL_V1=FAIL" +
                " | reason=" + reason +
                " | stableCoreChanged=False");
        }

        private void OnDestroy()
        {
            foreach (string senderId in activeSenderConnectionIds)
            {
                VoiceWebGLSpatialRemoveSender(senderId);
            }

            activeSenderConnectionIds.Clear();
            remoteAnchorByUserId.Clear();
            nextDiagnosticAtBySender.Clear();
        }

        private static Transform FindNamedChild(Transform root, string targetName)
        {
            if (root == null || string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child == null)
                {
                    continue;
                }

                if (string.Equals(child.name, targetName, StringComparison.Ordinal))
                {
                    return child;
                }

                Transform nested = FindNamedChild(child, targetName);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private static string SanitizeObjectName(string value)
        {
            return Normalize(value)
                .Replace("/", "_")
                .Replace("\\", "_")
                .Replace(":", "_")
                .Replace("*", "_")
                .Replace("?", "_")
                .Replace("\"", "_")
                .Replace("<", "_")
                .Replace(">", "_")
                .Replace("|", "_");
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim();
        }
    }
}
#endif
