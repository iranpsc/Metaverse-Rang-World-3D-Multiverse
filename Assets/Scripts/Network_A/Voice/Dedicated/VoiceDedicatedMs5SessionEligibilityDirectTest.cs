#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Network_A.Voice.Dedicated
{
    public static class VoiceDedicatedMs5SessionEligibilityDirectTest
    {
        private const string EpochId =
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        private const string FirstSessionId =
            "11111111-1111-4111-8111-111111111111";
        private const string SecondSessionId =
            "22222222-2222-4222-8222-222222222222";

        [MenuItem("Tools/Network A/Voice/Run MS5 Simple Session Eligibility Test")]
        public static void RunFromEditorMenu()
        {
            try
            {
                TestPairLeavesAndReopensWithNewSession();
                Debug.Log("VOICE_G4_RD_MS5_PAIR_LEAVE_CLOSE=PASS");
                Debug.Log("VOICE_G4_RD_MS5_PAIR_REJOIN_NEW_SESSION=PASS");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VOICE_G4_RD_MS5_SIMPLE_SESSION_ELIGIBILITY=FAIL | " +
                    exception);
                throw;
            }
        }

        private static void TestPairLeavesAndReopensWithNewSession()
        {
            long sourceSequence = 0;
            VoiceDedicatedGroupTopologyRuntime runtime =
                new VoiceDedicatedGroupTopologyRuntime();
            VoiceDedicatedGroupParticipant participantA =
                CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB =
                CreateParticipant("b");
            VoiceDedicatedParticipantPair pair =
                CreatePair(participantA, participantB);

            IReadOnlyList<VoiceDedicatedSessionDelta> openDeltas =
                runtime.ApplyPairObservations(
                    new[]
                    {
                        CreateEnterObservation(
                            pair,
                            FirstSessionId,
                            2.0f,
                            1000)
                    },
                    EpochId,
                    () => ++sourceSequence);

            Require(
                openDeltas.Count == 1 &&
                string.Equals(
                    openDeltas[0].type,
                    "session_created",
                    StringComparison.Ordinal) &&
                string.Equals(
                    openDeltas[0].sessionId,
                    FirstSessionId,
                    StringComparison.OrdinalIgnoreCase) &&
                runtime.ActiveSessionCount == 1,
                "Initial pair Voice Session was not opened.");

            IReadOnlyList<VoiceDedicatedSessionDelta> leaveDeltas =
                runtime.RemoveParticipant(
                    participantA,
                    VoiceDedicatedSessionReason.AccessRevoked,
                    2000,
                    EpochId,
                    () => ++sourceSequence);

            Require(
                leaveDeltas.Count == 1 &&
                string.Equals(
                    leaveDeltas[0].type,
                    "member_left",
                    StringComparison.Ordinal) &&
                leaveDeltas[0].reason ==
                    (int)VoiceDedicatedSessionReason.AccessRevoked &&
                runtime.ActiveSessionCount == 0 &&
                runtime.BurnedSessionIdCount == 1,
                "Pair Voice Session did not close and burn after participant leave.");

            IReadOnlyList<VoiceDedicatedSessionDelta> reopenDeltas =
                runtime.ApplyPairObservations(
                    new[]
                    {
                        CreateEnterObservation(
                            pair,
                            SecondSessionId,
                            2.0f,
                            3000)
                    },
                    EpochId,
                    () => ++sourceSequence);

            Require(
                reopenDeltas.Count == 1 &&
                string.Equals(
                    reopenDeltas[0].type,
                    "session_created",
                    StringComparison.Ordinal) &&
                string.Equals(
                    reopenDeltas[0].sessionId,
                    SecondSessionId,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    reopenDeltas[0].sessionId,
                    FirstSessionId,
                    StringComparison.OrdinalIgnoreCase) &&
                runtime.ActiveSessionCount == 1,
                "Eligible pair did not reopen with a new Voice SessionId.");
        }

        private static VoiceDedicatedGroupParticipant CreateParticipant(
            string label)
        {
            char connectionCharacter = label[0];

            return new VoiceDedicatedGroupParticipant(
                "server-ms5",
                "room-ms5",
                "user-" + label,
                new string(connectionCharacter, 12) +
                "4" + new string(connectionCharacter, 3) +
                "8" + new string(connectionCharacter, 15));
        }

        private static VoiceDedicatedParticipantPair CreatePair(
            VoiceDedicatedGroupParticipant first,
            VoiceDedicatedGroupParticipant second)
        {
            return new VoiceDedicatedParticipantPair(
                first.ServerId,
                first.RoomId,
                first.UserId,
                first.ConnectionId,
                second.UserId,
                second.ConnectionId);
        }

        private static VoiceDedicatedTopologyPairObservation CreateEnterObservation(
            VoiceDedicatedParticipantPair pair,
            string sessionId,
            float distanceMeters,
            long effectiveAtMs)
        {
            return new VoiceDedicatedTopologyPairObservation(
                pair,
                VoiceDedicatedProximityState.Active,
                VoiceDedicatedProximityDecisionType.SessionCreated,
                sessionId,
                distanceMeters,
                effectiveAtMs);
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
