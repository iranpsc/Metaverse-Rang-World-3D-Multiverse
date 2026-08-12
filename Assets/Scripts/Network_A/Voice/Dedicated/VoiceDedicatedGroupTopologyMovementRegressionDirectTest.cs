#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Network_A.Voice.Dedicated
{
    public static class VoiceDedicatedGroupTopologyMovementRegressionDirectTest
    {
        private const string EpochId =
            "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

        [MenuItem("Tools/Network A/Voice/Run G5 Movement Topology Regression")]
        public static void RunFromEditorMenu()
        {
            try
            {
                RunFourMemberMovementSequence();

                Debug.Log("VOICE_G5_9_PAIR_ENTER=PASS");
                Debug.Log("VOICE_G5_9_PARTIAL_APPROACH_NO_PREMATURE_MERGE=PASS");
                Debug.Log("VOICE_G5_9_STABLE_THREE_MEMBER_MERGE=PASS");
                Debug.Log("VOICE_G5_9_SINGLE_INTERNAL_EDGE_NO_EVICTION=PASS");
                Debug.Log("VOICE_G5_9_GROUP_MEMBER_LEAVE_AND_PAIR_REFORM=PASS");
                Debug.Log("VOICE_G5_9_FOUR_MEMBER_JOIN_AND_LEAVE=PASS");
                Debug.Log("VOICE_G5_9_GROUP_SPLIT_AND_REMERGE=PASS");
                Debug.Log("VOICE_G5_9_CLOSE_BURN_AND_FRESH_REENTRY=PASS");
                Debug.Log("VOICE_G5_9_TOPOLOGY_INVARIANTS=PASS");
                Debug.Log("VOICE_G5_9_MOVEMENT_REGRESSION=PASS");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "VOICE_G5_9_MOVEMENT_REGRESSION=FAIL | " +
                    exception);

                throw;
            }
        }

        private static void RunFourMemberMovementSequence()
        {
            int generatedSessionNumber = 100;
            long sourceSequence = 0;

            VoiceDedicatedGroupTopologyRuntime runtime =
                new VoiceDedicatedGroupTopologyRuntime(
                    delegate
                    {
                        string sessionId =
                            CreateSessionId(
                                generatedSessionNumber);
                        generatedSessionNumber += 1;
                        return sessionId;
                    });

            VoiceDedicatedGroupParticipant participantA =
                CreateParticipant("a");
            VoiceDedicatedGroupParticipant participantB =
                CreateParticipant("b");
            VoiceDedicatedGroupParticipant participantC =
                CreateParticipant("c");
            VoiceDedicatedGroupParticipant participantD =
                CreateParticipant("d");

            string stableSessionId = CreateSessionId(1);

            Apply(
                runtime,
                ref sourceSequence,
                Enter(
                    participantA,
                    participantB,
                    stableSessionId,
                    2.0f,
                    1000));
            RequireTopologyInvariants(runtime);
            RequirePairSession(
                runtime,
                participantA,
                participantB,
                stableSessionId);

            Apply(
                runtime,
                ref sourceSequence,
                Enter(
                    participantA,
                    participantC,
                    CreateSessionId(2),
                    2.4f,
                    2000));
            RequireTopologyInvariants(runtime);
            Require(
                runtime.ActiveSessionCount == 2 &&
                runtime.ActiveGroupSessionCount == 0,
                "A partial C approach merged before C was close to every group member.");

            Apply(
                runtime,
                ref sourceSequence,
                Enter(
                    participantB,
                    participantC,
                    CreateSessionId(3),
                    2.2f,
                    3000));
            RequireStableGroup(
                runtime,
                stableSessionId,
                3);
            RequireTopologyInvariants(runtime);

            IReadOnlyList<VoiceDedicatedSessionDelta> internalEdgeDeltas =
                Apply(
                    runtime,
                    ref sourceSequence,
                    Exit(
                        participantB,
                        participantC,
                        3.7f,
                        4000));
            RequireTopologyInvariants(runtime);
            Require(
                internalEdgeDeltas.Count == 0,
                "Single BC exit emitted a guessed group member leave.");
            RequireStableGroup(
                runtime,
                stableSessionId,
                3);
            RequirePairSession(
                runtime,
                participantA,
                participantB,
                stableSessionId);
            RequirePairSession(
                runtime,
                participantA,
                participantC,
                stableSessionId);
            RequirePairSession(
                runtime,
                participantB,
                participantC,
                stableSessionId);

            Apply(
                runtime,
                ref sourceSequence,
                Enter(
                    participantB,
                    participantC,
                    CreateSessionId(4),
                    2.1f,
                    5000));
            RequireStableGroup(
                runtime,
                stableSessionId,
                3);
            RequireTopologyInvariants(runtime);

            Apply(
                runtime,
                ref sourceSequence,
                Enter(
                    participantA,
                    participantD,
                    CreateSessionId(5),
                    2.3f,
                    6000));
            Apply(
                runtime,
                ref sourceSequence,
                Enter(
                    participantB,
                    participantD,
                    CreateSessionId(6),
                    2.2f,
                    6100));
            Require(
                runtime.ActiveGroupSessionCount == 1,
                "D joined the existing group before every D-to-group edge was active.");
            RequireTopologyInvariants(runtime);

            Apply(
                runtime,
                ref sourceSequence,
                Enter(
                    participantC,
                    participantD,
                    CreateSessionId(7),
                    2.0f,
                    6200));
            RequireStableGroup(
                runtime,
                stableSessionId,
                4);
            RequireTopologyInvariants(runtime);

            IReadOnlyList<VoiceDedicatedSessionDelta> dLeaveDeltas =
                Apply(
                    runtime,
                    ref sourceSequence,
                    Exit(
                        participantA,
                        participantD,
                        3.8f,
                        7000),
                    Exit(
                        participantB,
                        participantD,
                        3.9f,
                        7000),
                    Exit(
                        participantC,
                        participantD,
                        3.7f,
                        7000));

            Require(
                CountDeltaType(dLeaveDeltas, "member_left") == 1,
                "D simultaneous exit emitted more than one group member leave.");
            RequireStableGroup(
                runtime,
                stableSessionId,
                3);
            RequireNoPairSession(runtime, participantA, participantD);
            RequireNoPairSession(runtime, participantB, participantD);
            RequireNoPairSession(runtime, participantC, participantD);
            RequireTopologyInvariants(runtime);

            Apply(
                runtime,
                ref sourceSequence,
                Exit(
                    participantA,
                    participantC,
                    3.8f,
                    8000),
                Exit(
                    participantB,
                    participantC,
                    3.8f,
                    8000));
            Require(
                runtime.ActiveSessionCount == 1 &&
                runtime.ActiveGroupSessionCount == 0,
                "ABC split did not preserve only stable AB.");
            RequirePairSession(
                runtime,
                participantA,
                participantB,
                stableSessionId);
            RequireTopologyInvariants(runtime);

            Apply(
                runtime,
                ref sourceSequence,
                Enter(
                    participantA,
                    participantC,
                    CreateSessionId(8),
                    2.4f,
                    9000));
            Require(
                runtime.ActiveSessionCount == 2 &&
                runtime.ActiveGroupSessionCount == 0,
                "AC pair did not reform independently after the group split.");
            RequireTopologyInvariants(runtime);

            Apply(
                runtime,
                ref sourceSequence,
                Enter(
                    participantB,
                    participantC,
                    CreateSessionId(9),
                    2.3f,
                    9100));
            RequireStableGroup(
                runtime,
                stableSessionId,
                3);
            RequireTopologyInvariants(runtime);

            Apply(
                runtime,
                ref sourceSequence,
                Exit(
                    participantA,
                    participantB,
                    4.0f,
                    10000),
                Exit(
                    participantA,
                    participantC,
                    4.0f,
                    10000),
                Exit(
                    participantB,
                    participantC,
                    4.0f,
                    10000));
            Require(
                runtime.ActiveSessionCount == 0 &&
                runtime.ActiveGroupSessionCount == 0,
                "Full group exit left an active session below two members.");
            RequireTopologyInvariants(runtime);

            string freshPairSessionId =
                CreateSessionId(10);
            Apply(
                runtime,
                ref sourceSequence,
                Enter(
                    participantA,
                    participantB,
                    freshPairSessionId,
                    1.9f,
                    11000));
            RequirePairSession(
                runtime,
                participantA,
                participantB,
                freshPairSessionId);
            Require(
                !string.Equals(
                    stableSessionId,
                    freshPairSessionId,
                    StringComparison.OrdinalIgnoreCase),
                "Fresh pair reentry reused the burned stable SessionId.");
            RequireTopologyInvariants(runtime);
        }

        private static IReadOnlyList<VoiceDedicatedSessionDelta> Apply(
            VoiceDedicatedGroupTopologyRuntime runtime,
            ref long sourceSequence,
            params VoiceDedicatedTopologyPairObservation[] observations)
        {
            long currentSequence = sourceSequence;
            IReadOnlyList<VoiceDedicatedSessionDelta> deltas =
                runtime.ApplyPairObservations(
                    observations,
                    EpochId,
                    delegate
                    {
                        currentSequence += 1;
                        return currentSequence;
                    });

            sourceSequence = currentSequence;
            return deltas;
        }

        private static VoiceDedicatedTopologyPairObservation Enter(
            VoiceDedicatedGroupParticipant first,
            VoiceDedicatedGroupParticipant second,
            string sessionId,
            float distanceMeters,
            long effectiveAtMs)
        {
            return new VoiceDedicatedTopologyPairObservation(
                CreatePair(first, second),
                VoiceDedicatedProximityState.Active,
                VoiceDedicatedProximityDecisionType.SessionCreated,
                sessionId,
                distanceMeters,
                effectiveAtMs);
        }

        private static VoiceDedicatedTopologyPairObservation Exit(
            VoiceDedicatedGroupParticipant first,
            VoiceDedicatedGroupParticipant second,
            float distanceMeters,
            long effectiveAtMs)
        {
            return new VoiceDedicatedTopologyPairObservation(
                CreatePair(first, second),
                VoiceDedicatedProximityState.Outside,
                VoiceDedicatedProximityDecisionType.SessionClosed,
                string.Empty,
                distanceMeters,
                effectiveAtMs);
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

        private static VoiceDedicatedGroupParticipant CreateParticipant(
            string label)
        {
            char connectionCharacter = label[0];
            return new VoiceDedicatedGroupParticipant(
                "server-1",
                "room-1",
                "user-" + label,
                new string(connectionCharacter, 12) +
                "4" + new string(connectionCharacter, 3) +
                "8" + new string(connectionCharacter, 15));
        }

        private static string CreateSessionId(int value)
        {
            return string.Format(
                "{0:x8}-0000-4000-8000-000000000000",
                value);
        }

        private static void RequireStableGroup(
            VoiceDedicatedGroupTopologyRuntime runtime,
            string expectedSessionId,
            int expectedMemberCount)
        {
            IReadOnlyList<VoiceDedicatedGroupSessionSnapshot> snapshots =
                runtime.CreateSessionSnapshot();

            Require(
                snapshots.Count == 1 &&
                string.Equals(
                    snapshots[0].SessionId,
                    expectedSessionId,
                    StringComparison.OrdinalIgnoreCase) &&
                snapshots[0].Members.Count == expectedMemberCount,
                "The stable group SessionId or member count changed unexpectedly.");
        }

        private static void RequireTopologyInvariants(
            VoiceDedicatedGroupTopologyRuntime runtime)
        {
            IReadOnlyList<VoiceDedicatedGroupSessionSnapshot> snapshots =
                runtime.CreateSessionSnapshot();
            HashSet<string> activeSessionIds =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Require(
                snapshots.Count == runtime.ActiveSessionCount,
                "Runtime session count and snapshot count diverged.");

            for (int sessionIndex = 0;
                 sessionIndex < snapshots.Count;
                 sessionIndex += 1)
            {
                VoiceDedicatedGroupSessionSnapshot snapshot =
                    snapshots[sessionIndex];

                Require(
                    snapshot.Members.Count >= 2,
                    "An active Voice session contains fewer than two members.");
                Require(
                    activeSessionIds.Add(snapshot.SessionId),
                    "An active Voice SessionId is duplicated.");

                for (int firstIndex = 0;
                     firstIndex < snapshot.Members.Count;
                     firstIndex += 1)
                {
                    for (int secondIndex = firstIndex + 1;
                         secondIndex < snapshot.Members.Count;
                         secondIndex += 1)
                    {
                        RequirePairSession(
                            runtime,
                            snapshot.Members[firstIndex],
                            snapshot.Members[secondIndex],
                            snapshot.SessionId);
                    }
                }
            }
        }

        private static int CountDeltaType(
            IReadOnlyList<VoiceDedicatedSessionDelta> deltas,
            string type)
        {
            int count = 0;
            for (int index = 0; index < deltas.Count; index += 1)
            {
                if (string.Equals(
                        deltas[index].type,
                        type,
                        StringComparison.Ordinal))
                {
                    count += 1;
                }
            }

            return count;
        }

        private static void RequirePairMapped(
            VoiceDedicatedGroupTopologyRuntime runtime,
            VoiceDedicatedGroupParticipant first,
            VoiceDedicatedGroupParticipant second)
        {
            string sessionId;
            Require(
                runtime.TryGetSessionIdForPair(
                    CreatePair(first, second),
                    out sessionId),
                "Expected Voice pair is not mapped to an active session.");
        }

        private static void RequirePairSession(
            VoiceDedicatedGroupTopologyRuntime runtime,
            VoiceDedicatedGroupParticipant first,
            VoiceDedicatedGroupParticipant second,
            string expectedSessionId)
        {
            string sessionId;
            Require(
                runtime.TryGetSessionIdForPair(
                    CreatePair(first, second),
                    out sessionId) &&
                string.Equals(
                    sessionId,
                    expectedSessionId,
                    StringComparison.OrdinalIgnoreCase),
                "Voice pair is not indexed by the expected SessionId.");
        }

        private static void RequireNoPairSession(
            VoiceDedicatedGroupTopologyRuntime runtime,
            VoiceDedicatedGroupParticipant first,
            VoiceDedicatedGroupParticipant second)
        {
            string sessionId;
            Require(
                !runtime.TryGetSessionIdForPair(
                    CreatePair(first, second),
                    out sessionId),
                "Voice pair remained indexed after leaving the topology.");
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
