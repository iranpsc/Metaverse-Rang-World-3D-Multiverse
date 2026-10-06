using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Network_A.Voice.Client.Runtime;
using UnityEngine;

namespace Network_A.Voice.Client.Participants
{
    public sealed class VoiceClientRuntimeReadOnlyAdapter
    {
        private const string RuntimeRootName = "Voice_Client_Runtime_Root";
        private const string SessionsFieldName = "sessions";
        private const string PeerSnapshotMethodName = "CreatePeerUserIdSnapshot";

        private readonly HashSet<string> participantUserIds =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> participantSnapshot = new List<string>();

        private VoiceClientRuntime runtime;
        private FieldInfo sessionsField;
        private Type cachedSessionType;
        private MethodInfo peerSnapshotMethod;
        private bool reflectionFailureLogged;

        public bool TryCreateParticipantUserIdSnapshot(out IReadOnlyList<string> snapshot)
        {
            snapshot = Array.Empty<string>();

            if (!TryResolveRuntime()) return false;
            if (!TryResolveSessionsField()) return false;

            object sessionsValue = sessionsField.GetValue(runtime);
            IDictionary sessions = sessionsValue as IDictionary;
            if (sessions == null) return false;

            participantUserIds.Clear();
            participantSnapshot.Clear();

            foreach (DictionaryEntry entry in sessions)
            {
                object activeSession = entry.Value;
                if (activeSession == null) continue;
                if (!TryReadSessionParticipants(activeSession)) return false;
            }

            participantSnapshot.AddRange(participantUserIds);
            participantSnapshot.Sort(StringComparer.Ordinal);
            snapshot = participantSnapshot;
            return true;
        }

        private bool TryResolveRuntime()
        {
            if (runtime != null) return true;

            GameObject root = GameObject.Find(RuntimeRootName);
            if (root == null) return false;

            runtime = root.GetComponent<VoiceClientRuntime>();
            if (runtime == null) return false;

            sessionsField = null;
            cachedSessionType = null;
            peerSnapshotMethod = null;
            reflectionFailureLogged = false;
            return true;
        }

        private bool TryResolveSessionsField()
        {
            if (sessionsField != null) return true;

            sessionsField = typeof(VoiceClientRuntime).GetField(
                SessionsFieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (sessionsField != null) return true;

            LogReflectionFailure("sessions_field_missing");
            return false;
        }

        private bool TryReadSessionParticipants(object activeSession)
        {
            Type sessionType = activeSession.GetType();
            if (cachedSessionType != sessionType || peerSnapshotMethod == null)
            {
                cachedSessionType = sessionType;
                peerSnapshotMethod = sessionType.GetMethod(
                    PeerSnapshotMethodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
            }

            if (peerSnapshotMethod == null)
            {
                LogReflectionFailure("peer_snapshot_method_missing");
                return false;
            }

            object result = peerSnapshotMethod.Invoke(activeSession, null);
            IEnumerable peers = result as IEnumerable;
            if (peers == null) return true;

            foreach (object value in peers)
            {
                string userId = Normalize(value as string);
                if (userId.Length > 0) participantUserIds.Add(userId);
            }

            return true;
        }

        private void LogReflectionFailure(string reason)
        {
            if (reflectionFailureLogged) return;
            reflectionFailureLogged = true;
            Debug.LogError("VOICE_CLIENT_RUNTIME_READ_ONLY_ADAPTER=FAIL | reason=" + reason);
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
    }
}
