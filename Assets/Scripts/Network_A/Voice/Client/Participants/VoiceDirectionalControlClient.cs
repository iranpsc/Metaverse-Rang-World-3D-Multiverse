using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Network_A.Voice.Client.Protocol;
using Network_A.Voice.Client.Runtime;
using UnityEngine;

namespace Network_A.Voice.Client.Participants
{
    public sealed class VoiceDirectionalControlClient
    {
        private const string RuntimeRootName = "Voice_Client_Runtime_Root";
        private const string PeerConnectionsFieldName = "peerConnectionByUserId";
        private const string SessionsFieldName = "sessions";
        private const string PeerUserIdByConnectionIdFieldName = "peerUserIdByConnectionId";
        private const string SendEnvelopeMethodName = "SendEnvelopeAsync";
        private const byte SenderToReceiverKind = 4;

        private VoiceClientRuntime runtime;
        private FieldInfo peerConnectionsField;
        private FieldInfo sessionsField;
        private Type activeSessionType;
        private FieldInfo peerUserIdByConnectionIdField;
        private MethodInfo sendEnvelopeMethod;
        private bool reflectionFailureLogged;

        public Task<bool> SetOutgoingToUserAsync(string userId, bool enabled)
        {
            return SendDirectionalStateAsync(userId, enabled);
        }

        public Task<bool> SetIncomingFromUserAsync(string userId, bool enabled)
        {
            return SendExistingPerUserMuteStateAsync(userId, enabled);
        }

        private async Task<bool> SendDirectionalStateAsync(string userId, bool enabled)
        {
            if (!TryResolveRuntime()) return false;
            if (!TryResolvePeerConnectionId(userId, out string targetConnectionId)) return false;
            if (!TryResolveSendEnvelopeMethod()) return false;
            if (!runtime.IsAuthenticated || string.IsNullOrWhiteSpace(runtime.VoiceConnectionId)) return false;

            byte[] payload = CreateDirectionalPayload(targetConnectionId, !enabled);
            return await InvokeSendEnvelopeAsync(payload);
        }

        private async Task<bool> SendExistingPerUserMuteStateAsync(string userId, bool enabled)
        {
            if (!TryResolveRuntime()) return false;
            if (!TryResolvePeerConnectionId(userId, out string targetConnectionId)) return false;
            if (!TryResolveSendEnvelopeMethod()) return false;
            if (!runtime.IsAuthenticated || string.IsNullOrWhiteSpace(runtime.VoiceConnectionId)) return false;

            byte[] payload = VoiceClientControlPayload.EncodeMute(
                VoiceClientMuteKind.PerUser,
                !enabled,
                targetConnectionId);

            return await InvokeSendEnvelopeAsync(payload);
        }

        private async Task<bool> InvokeSendEnvelopeAsync(byte[] payload)
        {
            try
            {
                object result = sendEnvelopeMethod.Invoke(
                    runtime,
                    new object[]
                    {
                        VoiceClientMessageType.ListenerMuteChanged,
                        VoiceClientMessageFlags.AckRequired,
                        VoiceClientEnvelope.EmptyUuid,
                        runtime.VoiceConnectionId,
                        payload,
                        CancellationToken.None
                    });

                Task<bool> sendTask = result as Task<bool>;
                return sendTask != null && await sendTask;
            }
            catch (TargetInvocationException exception)
            {
                Debug.LogError(
                    "VOICE_DIRECTIONAL_CONTROL_SEND=FAIL | error=" +
                    (exception.InnerException?.Message ?? exception.Message));
                return false;
            }
            catch (Exception exception)
            {
                Debug.LogError("VOICE_DIRECTIONAL_CONTROL_SEND=FAIL | error=" + exception.Message);
                return false;
            }
        }

        private bool TryResolveRuntime()
        {
            if (runtime != null) return true;

            GameObject root = GameObject.Find(RuntimeRootName);
            if (root == null) return false;

            runtime = root.GetComponent<VoiceClientRuntime>();
            if (runtime == null) return false;

            peerConnectionsField = null;
            sessionsField = null;
            activeSessionType = null;
            peerUserIdByConnectionIdField = null;
            sendEnvelopeMethod = null;
            reflectionFailureLogged = false;
            return true;
        }

        private bool TryResolvePeerConnectionId(string userId, out string connectionId)
        {
            connectionId = string.Empty;
            string normalizedUserId = Normalize(userId);
            if (normalizedUserId.Length == 0) return false;

            if (TryResolvePeerConnectionIdFromActiveSessions(normalizedUserId, out connectionId))
            {
                return true;
            }

            if (peerConnectionsField == null)
            {
                peerConnectionsField = typeof(VoiceClientRuntime).GetField(
                    PeerConnectionsFieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }

            if (peerConnectionsField == null)
            {
                LogReflectionFailure("peer_connection_map_missing");
                return false;
            }

            IDictionary peerConnections = peerConnectionsField.GetValue(runtime) as IDictionary;
            if (peerConnections == null) return false;

            foreach (DictionaryEntry entry in peerConnections)
            {
                string peerUserId = Normalize(entry.Key as string);
                if (!string.Equals(peerUserId, normalizedUserId, StringComparison.Ordinal)) continue;

                connectionId = Normalize(entry.Value as string);
                return connectionId.Length > 0;
            }

            Debug.LogWarning(
                "VOICE_DIRECTIONAL_TARGET_RESOLVE=FAIL" +
                " | userId=" + normalizedUserId +
                " | reason=active_voice_peer_connection_not_found");
            return false;
        }

        private bool TryResolvePeerConnectionIdFromActiveSessions(
            string userId,
            out string connectionId)
        {
            connectionId = string.Empty;

            if (sessionsField == null)
            {
                sessionsField = typeof(VoiceClientRuntime).GetField(
                    SessionsFieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }

            if (sessionsField == null)
            {
                LogReflectionFailure("sessions_field_missing");
                return false;
            }

            IDictionary sessions = sessionsField.GetValue(runtime) as IDictionary;
            if (sessions == null) return false;

            foreach (DictionaryEntry sessionEntry in sessions)
            {
                object activeSession = sessionEntry.Value;
                if (activeSession == null) continue;

                Type sessionType = activeSession.GetType();
                if (activeSessionType != sessionType || peerUserIdByConnectionIdField == null)
                {
                    activeSessionType = sessionType;
                    peerUserIdByConnectionIdField = sessionType.GetField(
                        PeerUserIdByConnectionIdFieldName,
                        BindingFlags.Instance | BindingFlags.NonPublic);
                }

                if (peerUserIdByConnectionIdField == null)
                {
                    LogReflectionFailure("session_peer_map_missing");
                    return false;
                }

                IDictionary peers =
                    peerUserIdByConnectionIdField.GetValue(activeSession) as IDictionary;
                if (peers == null) continue;

                foreach (DictionaryEntry peerEntry in peers)
                {
                    string peerUserId = Normalize(peerEntry.Value as string);
                    if (!string.Equals(peerUserId, userId, StringComparison.Ordinal)) continue;

                    string resolvedConnectionId = Normalize(peerEntry.Key as string);
                    if (resolvedConnectionId.Length == 0) continue;

                    if (connectionId.Length == 0)
                    {
                        connectionId = resolvedConnectionId;
                        continue;
                    }

                    if (!string.Equals(
                            connectionId,
                            resolvedConnectionId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.LogError(
                            "VOICE_DIRECTIONAL_TARGET_RESOLVE=FAIL" +
                            " | userId=" + userId +
                            " | reason=ambiguous_voice_connection");
                        connectionId = string.Empty;
                        return false;
                    }
                }
            }

            if (connectionId.Length == 0) return false;

            Debug.Log(
                "VOICE_DIRECTIONAL_TARGET_RESOLVE=PASS" +
                " | source=active_session" +
                " | userId=" + userId +
                " | connectionId=" + connectionId);
            return true;
        }

        private bool TryResolveSendEnvelopeMethod()
        {
            if (sendEnvelopeMethod != null) return true;

            sendEnvelopeMethod = typeof(VoiceClientRuntime).GetMethod(
                SendEnvelopeMethodName,
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[]
                {
                    typeof(VoiceClientMessageType),
                    typeof(VoiceClientMessageFlags),
                    typeof(string),
                    typeof(string),
                    typeof(byte[]),
                    typeof(CancellationToken)
                },
                null);

            if (sendEnvelopeMethod != null) return true;

            LogReflectionFailure("send_envelope_method_missing");
            return false;
        }

        private static byte[] CreateDirectionalPayload(string targetConnectionId, bool blocked)
        {
            byte[] payload = new byte[20];
            payload[0] = 1;
            payload[1] = SenderToReceiverKind;
            payload[2] = blocked ? (byte)1 : (byte)0;
            payload[3] = 0;
            VoiceClientEnvelope.WriteUuid(payload, 4, targetConnectionId);
            return payload;
        }

        private void LogReflectionFailure(string reason)
        {
            if (reflectionFailureLogged) return;
            reflectionFailureLogged = true;
            Debug.LogError("VOICE_DIRECTIONAL_CONTROL_ADAPTER=FAIL | reason=" + reason);
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
    }
}
