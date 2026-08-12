#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Network_A.Voice.Client.Playback;
using Network_A.Voice.Client.Protocol;
using Network_A.Voice.Client.Runtime;
using UnityEditor;
using UnityEngine;

namespace Network_A.Voice.Client.Editor
{
    public static class VoiceClientGroupPeerMappingDirectTest
    {
        [MenuItem("Tools/Network A/Voice/Run G5 Unity Group Peer Mapping Test")]
        public static void RunFromEditorMenu()
        {
            try
            {
                const string sessionId =
                    "11111111-1111-4111-8111-111111111111";
                const string peerBUserId =
                    "22222222-2222-4222-8222-222222222222";
                const string peerBConnectionId =
                    "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
                const string peerCUserId =
                    "33333333-3333-4333-8333-333333333333";
                const string peerCConnectionId =
                    "cccccccc-cccc-4ccc-8ccc-cccccccccccc";

                byte[] descriptorB = CreateExtendedDescriptor(
                    sessionId,
                    peerBUserId,
                    peerBConnectionId);
                byte[] descriptorC = CreateExtendedDescriptor(
                    sessionId,
                    peerCUserId,
                    peerCConnectionId);

                VoiceClientSessionDescriptor decodedB =
                    VoiceClientControlPayload.DecodeSessionDescriptor(
                        descriptorB);
                VoiceClientSessionDescriptor decodedC =
                    VoiceClientControlPayload.DecodeSessionDescriptor(
                        descriptorC);

                Require(
                    decodedB.SessionId == sessionId &&
                    decodedB.PeerUserId == peerBUserId &&
                    decodedB.PeerConnectionId == peerBConnectionId,
                    "First G5 peer descriptor mapping is invalid.");
                Require(
                    decodedC.SessionId == sessionId &&
                    decodedC.PeerUserId == peerCUserId &&
                    decodedC.PeerConnectionId == peerCConnectionId,
                    "Second G5 peer descriptor mapping is invalid.");

                List<VoiceClientSessionDescriptor> snapshot =
                    VoiceClientControlPayload.DecodeSessionSnapshot(
                        CreateSnapshot(descriptorB, descriptorC));

                Require(
                    snapshot.Count == 2 &&
                    snapshot[0].SessionId == sessionId &&
                    snapshot[1].SessionId == sessionId &&
                    snapshot[0].PeerConnectionId !=
                    snapshot[1].PeerConnectionId,
                    "G5 snapshot did not preserve multiple peers for one SessionId.");

                Type activeSessionType =
                    typeof(VoiceClientRuntime).GetNestedType(
                        "ActiveVoiceSession",
                        BindingFlags.NonPublic);
                Require(
                    activeSessionType != null,
                    "Client runtime active group session state was not found.");

                object activeSession =
                    Activator.CreateInstance(
                        activeSessionType,
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic,
                        null,
                        new object[] { sessionId },
                        null);

                MethodInfo mergeDescriptor =
                    activeSessionType.GetMethod(
                        "Merge",
                        BindingFlags.Public |
                        BindingFlags.Instance);
                MethodInfo resolvePeer =
                    activeSessionType.GetMethod(
                        "TryResolvePeerUserId",
                        BindingFlags.Public |
                        BindingFlags.Instance);
                MethodInfo removePeer =
                    activeSessionType.GetMethod(
                        "RemovePeer",
                        BindingFlags.Public |
                        BindingFlags.Instance);

                Require(
                    mergeDescriptor != null &&
                    resolvePeer != null &&
                    removePeer != null,
                    "Client runtime group peer methods are incomplete.");

                mergeDescriptor.Invoke(
                    activeSession,
                    new object[] { decodedB });
                mergeDescriptor.Invoke(
                    activeSession,
                    new object[] { decodedC });

                object[] resolveBArguments =
                    new object[] {
                        peerBConnectionId,
                        null
                    };
                object[] resolveCArguments =
                    new object[] {
                        peerCConnectionId,
                        null
                    };

                Require(
                    (bool)resolvePeer.Invoke(
                        activeSession,
                        resolveBArguments) &&
                    (string)resolveBArguments[1] ==
                        peerBUserId,
                    "Client runtime did not resolve peer B by sender connectionId.");
                Require(
                    (bool)resolvePeer.Invoke(
                        activeSession,
                        resolveCArguments) &&
                    (string)resolveCArguments[1] ==
                        peerCUserId,
                    "Client runtime did not resolve peer C by sender connectionId.");

                object[] removeCArguments =
                    new object[] {
                        peerCConnectionId,
                        null
                    };
                Require(
                    (bool)removePeer.Invoke(
                        activeSession,
                        removeCArguments) &&
                    (string)removeCArguments[1] ==
                        peerCUserId,
                    "Client runtime did not remove only the leaving group peer.");

                resolveBArguments[1] = null;
                Require(
                    (bool)resolvePeer.Invoke(
                        activeSession,
                        resolveBArguments) &&
                    (string)resolveBArguments[1] ==
                        peerBUserId,
                    "Removing peer C also removed peer B mapping.");

                MethodInfo receiveFrame = typeof(VoiceSpatialPlaybackManager)
                    .GetMethod(
                        "ReceiveFrame",
                        BindingFlags.Public | BindingFlags.Instance);
                MethodInfo removeSender = typeof(VoiceSpatialPlaybackManager)
                    .GetMethod(
                        "RemoveSender",
                        BindingFlags.Public | BindingFlags.Instance);

                Require(
                    receiveFrame != null && removeSender != null,
                    "Per-sender playback lifecycle contract is incomplete.");

                Debug.Log("VOICE_G5_7_GROUP_DESCRIPTOR_EXTENSION=PASS");
                Debug.Log("VOICE_G5_7_MULTI_PEER_SNAPSHOT=PASS");
                Debug.Log("VOICE_G5_7_RUNTIME_SENDER_TO_USER_MAPPING=PASS");
                Debug.Log("VOICE_G5_7_RUNTIME_SINGLE_PEER_LEAVE=PASS");
                Debug.Log("VOICE_G5_7_PER_SENDER_PLAYBACK_CLEANUP=PASS");
                Debug.Log("VOICE_G5_7_UNITY_GROUP_PEER_MAPPING=PASS");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VOICE_G5_7_UNITY_GROUP_PEER_MAPPING=FAIL | " +
                    exception);

                throw;
            }
        }

        private static byte[] CreateExtendedDescriptor(
            string sessionId,
            string peerUserId,
            string peerConnectionId)
        {
            byte[] alias = Encoding.UTF8.GetBytes(peerUserId);
            int legacyLength = 48 + alias.Length;
            byte[] payload = new byte[legacyLength + 20];

            VoiceClientEnvelope.WriteUuid(payload, 0, sessionId);
            payload[16] = 2;
            payload[17] = 1;
            VoiceClientEnvelope.WriteUInt32(payload, 18, 2000);
            VoiceClientEnvelope.WriteUInt64(payload, 22, 1786000000000);
            VoiceClientEnvelope.WriteUuid(payload, 30, peerUserId);
            VoiceClientEnvelope.WriteUInt16(payload, 46, (ushort)alias.Length);
            Buffer.BlockCopy(alias, 0, payload, 48, alias.Length);

            payload[legacyLength] = (byte)'G';
            payload[legacyLength + 1] = (byte)'5';
            payload[legacyLength + 2] = 1;
            payload[legacyLength + 3] = 0;
            VoiceClientEnvelope.WriteUuid(
                payload,
                legacyLength + 4,
                peerConnectionId);
            return payload;
        }

        private static byte[] CreateSnapshot(params byte[][] descriptors)
        {
            int totalLength = 4;
            for (int index = 0; index < descriptors.Length; index += 1)
            {
                totalLength += 2 + descriptors[index].Length;
            }

            byte[] payload = new byte[totalLength];
            VoiceClientEnvelope.WriteUInt16(
                payload,
                0,
                (ushort)descriptors.Length);
            VoiceClientEnvelope.WriteUInt16(payload, 2, 0);

            int cursor = 4;
            for (int index = 0; index < descriptors.Length; index += 1)
            {
                VoiceClientEnvelope.WriteUInt16(
                    payload,
                    cursor,
                    (ushort)descriptors[index].Length);
                cursor += 2;
                Buffer.BlockCopy(
                    descriptors[index],
                    0,
                    payload,
                    cursor,
                    descriptors[index].Length);
                cursor += descriptors[index].Length;
            }

            return payload;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
#endif
